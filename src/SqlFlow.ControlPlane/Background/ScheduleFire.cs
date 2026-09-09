using SqlFlow.Catalog;
using SqlFlow.Core.Runs;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Turns a schedule into queued work: the single place a schedule becomes runs, shared by the scheduler's automatic
/// fire and the manual "run now" endpoint. A schedule owns a MEMBER SET (the flows that joined it with
/// <c>schedule: &lt;name&gt;</c>), and a fire runs exactly that set: one member is enqueued as a single run, several
/// are enqueued as ONE wave-gated run group, so the members execute in dependency order (waves ascending, members
/// within a wave concurrently) instead of racing. Both paths go through the durable run queue, exactly as a manual
/// trigger does, and stamp the schedule so the work traces back to it. The scheduler's timing (the claim and
/// next-fire math) is deliberately not here: the automatic caller claims an occurrence before invoking this, and a
/// manual invoke never touches the cadence at all.
/// </summary>
public static class ScheduleFire
{
    /// <summary>Why a fire did or did not enqueue, so each caller can report it in its own idiom (the scheduler logs,
    /// the endpoint answers a status code).</summary>
    public enum Outcome
    {
        /// <summary>The schedule had exactly one runnable member and enqueued it as a single run.</summary>
        Enqueued,

        /// <summary>The schedule had several runnable members and enqueued them as a wave-gated group.</summary>
        EnqueuedGroup,

        /// <summary>The schedule resolved to no runnable flow: nothing joined it, or every member is deactivated or
        /// <c>mode: manual</c>.</summary>
        ScopeEmpty,
    }

    /// <summary>The result of a fire: the outcome, the run it enqueued (the group's first member for a multi-member
    /// fire), the group id when the set expanded to more than one, and how many flows were enqueued.</summary>
    public readonly record struct FireResult(Outcome Outcome, Guid RunId, Guid? GroupId, int MemberCount)
    {
        /// <summary>Whether the fire actually queued work (either shape).</summary>
        public bool Queued => Outcome is Outcome.Enqueued or Outcome.EnqueuedGroup;
    }

    /// <summary>
    /// Enqueues the schedule's member set and stamps the work on the schedule. Returns
    /// <see cref="Outcome.ScopeEmpty"/> when the set resolves to nothing runnable, which is a normal state (a source
    /// whose flows were all deactivated) and not a fault.
    /// <para>
    /// <paramref name="batchFilter"/> narrows the fire to members carrying one of those <c>batch:</c> tags, for
    /// "run the nightly, but only the small and medium tables". It can only ever select a subset of the schedule's
    /// own members, never pull in a flow that did not join; a null or empty filter runs every member.
    /// </para>
    /// <para>
    /// A <c>mode: manual</c> member is excluded from every fire, automatic or run-now: that flag reserves a flow for
    /// a direct trigger, and firing the schedule it happens to sit in is not a direct trigger of it.
    /// </para>
    /// </summary>
    public static async Task<FireResult> EnqueueAsync(
        CatalogDbContext catalog, IRunDispatcher dispatcher, CatalogSchedule schedule,
        DateTime nowUtc, CancellationToken ct, IReadOnlyCollection<string>? batchFilter = null,
        RunParameters? backfillWindow = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(schedule);

        // Membership decides what runs; lineage decides the order. The expansion reads the schedule's members and
        // each one's topological wave, and returns the active, non-manual members ordered by wave.
        var expansion = await RunScopeExpander.ExpandScheduleAsync(
            catalog, schedule.RepoId, schedule.Id, schedule.Name, batchFilter, ct).ConfigureAwait(false);
        if (expansion.Members.Count == 0)
        {
            return new FireResult(Outcome.ScopeEmpty, Guid.Empty, null, 0);
        }

        // A backfill window makes the whole fire reprocess the source for that range: the root fetch flows (the
        // integration copy/acquire/sftp and any root file ingestion) take the window and force-re-land their files;
        // the silver (relational ingestion) flows take MIN-from-source so the re-landed rows are re-pulled; every
        // intermediate flow runs at defaults and picks up what the roots re-land. See BuildBackfillParameters.
        var memberParameters = backfillWindow is null
            ? null
            : BuildBackfillParameters(expansion.Members, backfillWindow);

        // A single member is a single run: enqueuing a one-member group would add a group's bookkeeping and its
        // claim gate for nothing.
        if (expansion.Members.Count == 1)
        {
            var member = expansion.Members[0];
            var parameters = memberParameters?.GetValueOrDefault(member.FlowName) ?? RunParameters.None;
            var runId = await dispatcher.EnqueueAsync(
                catalog,
                new RunEnqueueRequest(
                    schedule.RepoId, member.FlowName, member.FlowKind, Parameters: parameters,
                    // Recorded on the run so monitoring can tell an automatic execution from one a person
                    // asked for. Nothing else on the row distinguishes them: a manual trigger takes this same
                    // path with the same shape.
                    TriggerSource: RunTriggerSources.Schedule, TriggerScheduleId: schedule.Id),
                ct).ConfigureAwait(false);
            await ScheduleStore.SetLastRunAsync(catalog, schedule.Id, runId, nowUtc, ct).ConfigureAwait(false);
            return new FireResult(Outcome.Enqueued, runId, null, 1);
        }

        // Enqueuing the members as one group makes the queue's claim gate the order (a member is claimable only once
        // every lower wave is terminal), so waves run in sequence while the members of a wave run concurrently. The
        // schedule's own MaxConcurrency rides along and bounds how many of those members run at once, so a fan-out
        // never opens more work against a shared upstream than that upstream can take.
        var result = await dispatcher.EnqueueGroupAsync(
            catalog,
            new RunGroupEnqueueRequest(
                schedule.RepoId, RunGroupModes.Batch, expansion.Anchor, expansion.Members,
                MemberParameters: memberParameters, MaxConcurrency: schedule.MaxConcurrency,
                TriggerSource: RunTriggerSources.Schedule, TriggerScheduleId: schedule.Id),
            ct).ConfigureAwait(false);

        var firstRunId = result.RunIds.Count > 0 ? result.RunIds[0] : Guid.Empty;
        await ScheduleStore.SetLastGroupAsync(catalog, schedule.Id, result.GroupId, firstRunId, nowUtc, ct).ConfigureAwait(false);
        return new FireResult(Outcome.EnqueuedGroup, firstRunId, result.GroupId, expansion.Members.Count);
    }

    /// <summary>The flow kinds a schedule backfill acts on, matching the three layers of an ingest source: the
    /// integration flows that fetch from an external system (copy, acquire, sftp) and the file flows that ingest the
    /// fetched files into the database. The silver (relational ingestion) layer is handled separately, by
    /// reprocessing from the source minimum. Every other kind (stored procedure, export, health check, inventory)
    /// is not part of a backfill and runs at defaults.</summary>
    private static readonly HashSet<string> IntegrationKinds =
        new(StringComparer.OrdinalIgnoreCase) { "cpy", "api", "sftp", "file" };

    /// <summary>
    /// Routes a schedule fire's members to their backfill parameters. Unlike a node run there is no single anchor, so
    /// the routing keys off each member's ROLE in the fired set:
    /// <list type="bullet">
    /// <item>A ROOT integration or file flow (an external-source reader with nothing upstream of it in the set)
    /// takes the window and force-re-lands its files, so the requested slice re-enters the pipeline.</item>
    /// <item>A relational ingestion (silver) flow takes MIN-from-source, so the re-landed rows are re-pulled even
    /// though they carry old business dates the target's high-water mark would otherwise filter out.</item>
    /// <item>An intermediate file flow (one reading a root copy's output, whose freshly re-landed files it cannot
    /// re-select by a past modified-date window) runs at defaults and picks them up through its own incremental.</item>
    /// </list>
    /// "Root" is the minimum wave of the fired set: an external-source reader has nothing feeding it, so it sits in
    /// wave 0. A member left out of the returned map runs with default parameters.
    /// </summary>
    private static Dictionary<string, RunParameters> BuildBackfillParameters(
        IReadOnlyList<RunScopeMember> members, RunParameters window)
    {
        var reprocess = new RunParameters { ReprocessFromSourceMin = true };
        var rootWave = members.Min(m => m.Wave);
        var map = new Dictionary<string, RunParameters>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (member.Wave == rootWave && IntegrationKinds.Contains(member.FlowKind))
            {
                map[member.FlowName] = window;
            }
            else if (string.Equals(member.FlowKind, "ing", StringComparison.OrdinalIgnoreCase))
            {
                map[member.FlowName] = reprocess;
            }
            // Otherwise the member is left out of the map, so it runs with default parameters.
        }

        return map;
    }
}
