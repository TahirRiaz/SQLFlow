using Microsoft.EntityFrameworkCore;
using SqlFlow.Dispatch;

namespace SqlFlow.Catalog;

/// <summary>The replica target for one pool and every term it was built from, so an operator reading the fleet
/// page and an autoscaler reading the scale-target endpoint see the same arithmetic: <see cref="Replicas"/> is the
/// greatest of the demanded replicas (<see cref="EligibleQueuedRuns"/> divided by <see cref="RunSlotsPerNode"/>,
/// rounded up, plus <see cref="BusyNodes"/>), the always-on floor <see cref="MinReplicas"/>, and the manual override
/// while its window is open. <see cref="Pool"/> is the empty string for the default (untargeted) pool.</summary>
public sealed record ScaleTarget(
    string Pool,
    int Replicas,
    int EligibleQueuedRuns,
    int BusyNodes,
    int OnlineNodes,
    int RunSlotsPerNode,
    int MinReplicas,
    int ManualReplicas,
    bool ManualActive);

/// <summary>
/// The one definition of a pool's replica target, the number an autoscaler holds the pool's worker deployment at.
/// It is computed from the journal on whichever control-plane replica is asked, so every replica answers the same
/// number and the scaler never depends on which one it reached: the queued and running rows are loaded into a
/// fresh <see cref="DispatchState"/> so the eligibility gates are evaluated by the same code the dispatcher hands
/// work out with (a run behind a busy pipeline, a lower wave or a full group cap asks for no node, because no node
/// could take it), the fleet registry supplies the busy nodes and the slot count a node of the pool offers, and
/// the pool's desired row supplies the floor and the override.
/// <para>Demand is <c>ceil(eligible / slots) + busy nodes</c>. Queued runs ask for capacity to start; busy nodes hold
/// the capacity they occupy, so scale-in only ever reclaims idle replicas' worth of target: a queued-only count
/// reads zero the moment the fleet takes a batch, which would let the platform scale in mid-execution and kill
/// replicas carrying live runs. The queued term is divided by what one node executes at once, because a replica is
/// not worth one run: asking for one replica per queued run spawned four times the fleet a schedule wave needed,
/// and the surplus found nothing left to take. Busy nodes are not divided; each holds one replica's worth of
/// occupied capacity. The liveness window means a dead node's last busy count never pins a replica: its runs are
/// requeued by the dispatcher's lease expiry and re-enter the queued term.</para>
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

        var eligible = state.CountEligibleQueuedRuns(key);

        var isDefault = key.Length == 0;
        var nodes = await catalog.Nodes.AsNoTracking()
            .Where(n => isDefault ? (n.Pool == null || n.Pool == "") : n.Pool == key)
            .Select(n => new { n.LastSeenUtc, n.BusyRuns, n.RunSlots })
            .ToListAsync(ct).ConfigureAwait(false);
        var onlineSince = nowUtc - NodeRegistry.OnlineWindow;
        var online = nodes.Where(n => n.LastSeenUtc >= onlineSince).ToList();
        var busy = online.Count(n => n.BusyRuns > 0);

        // What one node of the pool executes at once: what the online nodes report, else what the pool's last
        // known nodes reported (a pool scaled to zero remembers its shape), else the runtime default. Every
        // replica of a pool's deployment is configured alike, so the largest report is the deployment's value and
        // a lone row from before nodes reported slots (zero) never drags the divisor down.
        var slots = online.Count > 0 ? online.Max(n => n.RunSlots) : nodes.Count > 0 ? nodes.Max(n => n.RunSlots) : 0;
        if (slots <= 0)
        {
            slots = defaultRunSlotsPerNode;
        }

        var demanded = (int)Math.Ceiling(eligible / (double)slots) + busy;
        var desired = await WorkerPoolStore.GetDesiredAsync(catalog, key, ct).ConfigureAwait(false);
        var manualActive = desired?.ManualUntilUtc is { } until && until > nowUtc;
        return new ScaleTarget(
            key,
            WorkerPoolStore.ResolveTarget(demanded, desired, nowUtc),
            eligible,
            busy,
            online.Count,
            slots,
            desired?.MinReplicas ?? 0,
            desired?.ManualReplicas ?? 0,
            manualActive);
    }
}
