using Microsoft.EntityFrameworkCore;
using SqlFlow.Dispatch;

namespace SqlFlow.Catalog;

/// <summary>The replica target for one pool and every term it was built from, so an operator reading the fleet
/// page and an autoscaler reading the scale-target endpoint see the same arithmetic: <see cref="Replicas"/> is the
/// greatest of the demanded replicas (one for each of the <see cref="EligibleWholeNodeRuns"/> that take a node to
/// themselves, the rest of <see cref="EligibleQueuedRuns"/> divided by <see cref="RunSlotsPerNode"/>, rounded up, plus
/// <see cref="BusyNodes"/>), the always-on floor <see cref="MinReplicas"/>, and the manual override while its window is
/// open. <see cref="Pool"/> is the empty string for the default (untargeted) pool.</summary>
public sealed record ScaleTarget(
    string Pool,
    int Replicas,
    int EligibleQueuedRuns,
    int BusyNodes,
    int OnlineNodes,
    int RunSlotsPerNode,
    int MinReplicas,
    int ManualReplicas,
    bool ManualActive,
    int EligibleWholeNodeRuns = 0);

/// <summary>
/// The one definition of a pool's replica target, the number an autoscaler holds the pool's worker deployment at.
/// It is computed from the journal on whichever control-plane replica is asked, so every replica answers the same
/// number and the scaler never depends on which one it reached: the queued and running rows are loaded into a
/// fresh <see cref="DispatchState"/> so the eligibility gates are evaluated by the same code the dispatcher hands
/// work out with (a run behind a busy pipeline, a lower wave or a full group cap asks for no node, because no node
/// could take it), the fleet registry supplies the busy nodes and the slot count a node of the pool offers, and
/// the pool's desired row supplies the floor and the override.
/// <para>Demand is <c>ceil(eligible / slots) + busy nodes</c>, where a run that takes a node to itself counts as a whole
/// node (<see cref="EligibleQueuedRuns.Nodes"/>). Queued runs ask for capacity to start; busy nodes hold
/// the capacity they occupy, so scale-in only ever reclaims idle replicas' worth of target: a queued-only count
/// reads zero the moment the fleet takes a batch, which would let the platform scale in mid-execution and kill
/// replicas carrying live runs. The queued term is divided by what one node executes at once, because a replica is
/// not worth one run: asking for one replica per queued run spawned four times the fleet a schedule wave needed,
/// and the surplus found nothing left to take. Busy nodes are not divided; each holds one replica's worth of
/// occupied capacity.</para>
/// <para>A node is busy when it is online and reported runs on its last heartbeat, or when the journal says it holds
/// a running run, whatever its heartbeat says. The heartbeat alone is behind the journal twice: for the few seconds
/// between a hand-out and the node's next flushed poll, and for as long as the control plane was away (nobody
/// writes heartbeats then, so every node reads silent when it returns). In both, the queued term has already dropped
/// to zero, and a target built on the heartbeat alone read zero under running work: the scaler took the pool to
/// zero and stopped the nodes mid-run. The journal is never behind, so the target reads zero only when nothing is
/// queued that could start and nothing runs, and a short cool-down on the scaler is safe. A node that died holding
/// runs holds its replica only until the dispatcher's lease expiry requeues them, when they ask for one as queued
/// runs do; a silent node's last busy count, with no running run behind it, still pins nothing.</para>
/// </summary>
public static class ScaleTargetStore
{
    /// <summary>Resolves the target for <paramref name="pool"/> (null or blank is the default pool).
    /// <paramref name="defaultRunSlotsPerNode"/> is what a node executes at once when no node of the pool has yet
    /// reported its own capacity (a pool at zero on a fresh estate): the node runtime's default.</summary>
    public static async Task<ScaleTarget> ResolveAsync(
        CatalogDbContext catalog, string? pool, int defaultRunSlotsPerNode, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentOutOfRangeException.ThrowIfLessThan(defaultRunSlotsPerNode, 1);

        var key = WorkerPoolStore.PoolKey(pool);

        // The gates need the whole active set, not just this pool's rows: a pipeline busy in another pool still
        // blocks this pool's queued run of it, and a group can span pools. The set is small (the backlog plus what
        // the fleet is executing), and this is asked a few times a minute per pool.
        var (queued, running) = await RunQueueStore.LoadDispatchStateAsync(catalog, ct).ConfigureAwait(false);
        var state = new DispatchState();
        foreach (var run in queued)
        {
            state.AddQueuedRun(run);
        }

        foreach (var record in running)
        {
            // The lease horizon is irrelevant here (nothing expires this state); a held run only needs to occupy its
            // pipeline and its group slot so the gates see it.
            state.AddLeasedRun(record.Run, record.Node, nowUtc, nowUtc.AddDays(1));
        }

        var eligible = state.CountEligible(key);

        // Every node's row, not only this pool's: a node the journal says holds a run belongs to the pool its row names,
        // and only one with no row yet (handed a run on its very first poll) is taken to serve the run's own pool. The
        // fleet registry is small: a node that left has no row, and a silent one is pruned.
        var fleet = await catalog.Nodes.AsNoTracking()
            .Select(n => new { n.Name, n.Pool, n.LastSeenUtc, n.BusyRuns, n.RunSlots })
            .ToListAsync(ct).ConfigureAwait(false);
        var poolOf = fleet.ToDictionary(n => n.Name, n => WorkerPoolStore.PoolKey(n.Pool), StringComparer.Ordinal);
        var nodes = fleet.Where(n => string.Equals(poolOf[n.Name], key, StringComparison.Ordinal)).ToList();
        var onlineSince = nowUtc - NodeRegistry.OnlineWindow;
        var online = nodes.Where(n => n.LastSeenUtc >= onlineSince).ToList();
        var busyNodes = online.Where(n => n.BusyRuns > 0).Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var record in running)
        {
            if (string.IsNullOrEmpty(record.Node))
            {
                continue;
            }

            var holderPool = poolOf.TryGetValue(record.Node, out var named) ? named : WorkerPoolStore.PoolKey(record.Run.TargetPool);
            if (string.Equals(holderPool, key, StringComparison.Ordinal))
            {
                busyNodes.Add(record.Node);
            }
        }

        var busy = busyNodes.Count;

        // What one node of the pool executes at once: what the online nodes report, else what the pool's last
        // known nodes reported (a pool scaled to zero remembers its shape), else the runtime default. Every
        // replica of a pool's deployment is configured alike, so the largest report is the deployment's value and
        // a lone row from before nodes reported slots (zero) never drags the divisor down.
        var slots = online.Count > 0 ? online.Max(n => n.RunSlots) : nodes.Count > 0 ? nodes.Max(n => n.RunSlots) : 0;
        if (slots <= 0)
        {
            slots = defaultRunSlotsPerNode;
        }

        var demanded = checked(eligible.Nodes(slots) + busy);
        var desired = await WorkerPoolStore.GetDesiredAsync(catalog, key, ct).ConfigureAwait(false);
        var manualActive = desired?.ManualUntilUtc is { } until && until > nowUtc;
        return new ScaleTarget(
            key,
            WorkerPoolStore.ResolveTarget(demanded, desired, nowUtc),
            eligible.Runs,
            busy,
            online.Count,
            slots,
            desired?.MinReplicas ?? 0,
            desired?.ManualReplicas ?? 0,
            manualActive,
            eligible.WholeNodeRuns);
    }
}
