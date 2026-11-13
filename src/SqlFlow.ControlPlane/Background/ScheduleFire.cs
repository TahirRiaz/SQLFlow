using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;

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
    /// <summary>
    /// How a fire started, which every run it enqueues records (<see cref="CatalogRun.TriggerSource"/>,
    /// <see cref="CatalogRun.RequestedBy"/>, <see cref="CatalogRun.TriggerRunId"/>), so a run says whether the estate ran
    /// it on its own or someone asked, and who: the schedule's clock, its parents' fire, or a person or client asking.
    /// </summary>
    public sealed record Origin
    {
        private Origin(string triggerSource, string? requestedBy, Guid? afterRunId)
        {
            TriggerSource = triggerSource;
            RequestedBy = requestedBy;
            AfterRunId = afterRunId;
        }

        /// <summary>The fire's trigger source (<see cref="RunTriggerSources"/>).</summary>
        public string TriggerSource { get; }

        /// <summary>Who asked, or null when the schedule fired on its own and records itself.</summary>
        public string? RequestedBy { get; }

        /// <summary>The first run of the parent fire a chained fire followed, when that fire enqueued any.</summary>
        public Guid? AfterRunId { get; }

        /// <summary>The schedule's own clock fired it: its cron or interval came round.</summary>
        public static Origin Clock { get; } = new(RunTriggerSources.Schedule, null, null);

        /// <summary>The schedule fired behind its parents' fire, whose first run is <paramref name="parentFireRunId"/>
        /// (null when the newest parent fire enqueued nothing).</summary>
        public static Origin Chain(Guid? parentFireRunId) => new(RunTriggerSources.Chain, null, parentFireRunId);

        /// <summary>Someone asked for the fire: a person's run-now, or an external trigger's request
        /// (<c>trigger:&lt;name&gt;</c>). A caller whose credential carries no name is recorded as
        /// <see cref="Api.RequestActor.Unknown"/>, never as the schedule, so a fire someone asked for is never read as
        /// one the estate made on its own.</summary>
        public static Origin AskedBy(string? requestedBy)
            => new(RunTriggerSources.Manual, string.IsNullOrWhiteSpace(requestedBy) ? Api.RequestActor.Unknown : requestedBy.Trim(), null);
    }

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
    /// <remarks>The loader passed in knows the registered flow kinds: a schedule's operation and values reach the members
    /// of a registered kind that declares the operation (or every member of a registered kind when the schedule names
    /// none), each member's kind validates them, and a refusal fails the fire with a
    /// <see cref="SqlFlow.Core.SqlFlowException"/> instead of quietly running the member as defined.
    /// <para><paramref name="origin"/> is how the fire started, and every run of the fire records it: the schedule's
    /// clock (the default), its parents' fire, or someone asking (a run-now, an external trigger's request), who is then
    /// recorded as the one that asked. A fire of the schedule's own records the schedule itself
    /// (<see cref="RunActors.Schedule"/>), so what the runs do is attributed to it rather than to nobody.</para></remarks>
    public static async Task<FireResult> EnqueueAsync(
        CatalogDbContext catalog, IRunDispatcher dispatcher, YamlDocumentLoader documents, CatalogSchedule schedule,
        DateTime nowUtc, CancellationToken ct, IReadOnlyCollection<string>? batchFilter = null,
        RunParameters? backfillWindow = null, Origin? origin = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(schedule);
        origin ??= Origin.Clock;

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
        // the silver (relational ingestion) flows take MIN-from-source so the re-landed rows are re-pulled; a file flow
        // dated from the file's name or path takes the window too, since a re-landed file keeps that date; every other
        // intermediate flow runs at defaults and picks up what the roots re-land. See BackfillRouting.
        var memberParameters = backfillWindow is null
            ? null
            : BackfillRouting.ForSchedule(
                expansion.Members, backfillWindow,
                await BackfillRouting.FileDatedMembersAsync(catalog, documents, schedule.RepoId, expansion.Members, ct).ConfigureAwait(false));
        memberParameters = ApplyKindArguments(documents, schedule, expansion.Members, memberParameters);
        var actor = origin.RequestedBy ?? RunActors.Schedule(schedule.Name, schedule.Id);

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
                    TriggerSource: origin.TriggerSource, TriggerScheduleId: schedule.Id, RequestedBy: actor,
                    TriggerRunId: origin.AfterRunId),
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
                TriggerSource: origin.TriggerSource, TriggerScheduleId: schedule.Id, RequestedBy: actor,
                TriggerRunId: origin.AfterRunId),
            ct).ConfigureAwait(false);

        var firstRunId = result.RunIds.Count > 0 ? result.RunIds[0] : Guid.Empty;
        await ScheduleStore.SetLastGroupAsync(catalog, schedule.Id, result.GroupId, firstRunId, nowUtc, ct).ConfigureAwait(false);
        return new FireResult(Outcome.EnqueuedGroup, firstRunId, result.GroupId, expansion.Members.Count);
    }

    /// <summary>
    /// Merges the schedule's operation and values into the parameters of each member whose registered kind takes them:
    /// a kind declaring the schedule's operation, or any registered kind when the schedule names none. Built-in members
    /// and members of a kind without that operation are left as routed. Each merged set is validated by the member's
    /// kind through the loader's one rule. Returns the (possibly new) map, or the input unchanged when the schedule
    /// carries no kind arguments.
    /// </summary>
    private static Dictionary<string, RunParameters>? ApplyKindArguments(
        YamlDocumentLoader documents, CatalogSchedule schedule, IReadOnlyList<RunScopeMember> members,
        Dictionary<string, RunParameters>? memberParameters)
    {
        var values = RunParameters.ValuesFromJson(schedule.ValuesJson);
        if (schedule.Operation is null && values.Count == 0)
        {
            return memberParameters;
        }

        var map = memberParameters ?? new Dictionary<string, RunParameters>(StringComparer.Ordinal);
        foreach (var member in members)
        {
            if (!TakesKindArguments(documents, schedule, member.FlowKind))
            {
                continue;
            }

            var merged = (map.GetValueOrDefault(member.FlowName) ?? RunParameters.None) with
            {
                Operation = schedule.Operation,
                Values = values,
            };
            documents.ValidateRunParameters(member.FlowKind, merged);
            map[member.FlowName] = merged;
        }

        return map;
    }

    /// <summary>
    /// Whether a member of kind <paramref name="flowKind"/> is given the schedule's operation and values when it fires: a
    /// registered kind that declares the schedule's operation, or any registered kind when the schedule names none. A
    /// built-in member, or one whose kind lacks the operation, runs as routed, with its document's own values. The one
    /// decision a fire (<see cref="EnqueueAsync"/>) and a schedule's preview of its members' values both take.
    /// </summary>
    public static bool TakesKindArguments(YamlDocumentLoader documents, CatalogSchedule schedule, string flowKind)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(schedule);
        var kind = documents.FindKind(flowKind);
        return kind is not null
               && (schedule.Operation is not { } operation || kind.Operations.Any(o => string.Equals(o.Name, operation, StringComparison.Ordinal)));
    }
}
