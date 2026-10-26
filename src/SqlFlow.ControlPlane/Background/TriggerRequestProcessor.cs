using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Moves one trigger request along, as far as it can go in one pass, under the lease its caller holds: it waits for the
/// repository sync that answers the request (copying the sync's own lines into the request's trace as they appear),
/// checks that the synced commit contains the commit the caller expected, fires each requested schedule through the one
/// fire path the scheduler and run-now take, reports each run as it starts and ends, and ends the request once every run
/// has. A pass that stops part way (a restart, a lost lease) leaves the request exactly where it was, and the next pass
/// carries on: a schedule found mid-fire is matched to the runs its fire queued before it is fired again.
/// </summary>
public sealed class TriggerRequestProcessor
{
    /// <summary>The most lines of a sync's trace a request copies into its own; the rest are counted, not copied.</summary>
    public const int MaxCopiedSyncLines = 500;

    /// <summary>The longest single line a request's trace carries; a longer one is cut and marked.</summary>
    public const int MaxLineLength = 4000;

    /// <summary>The step prefix of a line the request copied from its sync's own trace; the sync's step follows it.</summary>
    public const string SyncLineStep = "sync:";

    private readonly CatalogDbContext _catalog;
    private readonly IRunDispatcher _dispatcher;
    private readonly SqlFlow.Yaml.YamlDocumentLoader _documents;
    private readonly RepoSyncSignal _syncSignal;
    private readonly ICommitContainment _containment;
    private readonly IReadOnlyList<IRunOutcomeDescriber> _describers;
    private readonly ControlPlaneOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<TriggerRequestProcessor> _logger;

    public TriggerRequestProcessor(
        CatalogDbContext catalog, IRunDispatcher dispatcher, SqlFlow.Yaml.YamlDocumentLoader documents, RepoSyncSignal syncSignal,
        ICommitContainment containment, IEnumerable<IRunOutcomeDescriber> describers, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, ILogger<TriggerRequestProcessor> logger)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(syncSignal);
        ArgumentNullException.ThrowIfNull(containment);
        ArgumentNullException.ThrowIfNull(describers);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _catalog = catalog;
        _dispatcher = dispatcher;
        _documents = documents;
        _syncSignal = syncSignal;
        _containment = containment;
        _describers = [.. describers];
        _options = options.Value;
        _clock = clock;
        _logger = logger;
    }

    private DateTime Now() => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Advances request <paramref name="requestId"/>. <paramref name="renewLease"/> extends the caller's lease and answers
    /// whether it still holds it; it is asked right before anything that must happen once (a fire), so a replica whose
    /// lease lapsed during a slow step never fires what another replica may be firing.
    /// </summary>
    public async Task AdvanceAsync(Guid requestId, Func<CancellationToken, Task<bool>> renewLease, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(renewLease);
        var request = await _catalog.TriggerRequests.AsTracking().FirstOrDefaultAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
        if (request is null || TriggerRequestStates.IsTerminal(request.State))
        {
            return;
        }

        var schedules = await _catalog.TriggerRequestSchedules.AsTracking()
            .Where(s => s.RequestId == requestId).OrderBy(s => s.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
        var trace = await ActivityTrace.ResumeAsync(
            _catalog, ActivityKinds.TriggerRequest, requestId.ToString("D"), request.ActivityId, _clock, ct).ConfigureAwait(false);

        if (request.CancelRequestedUtc is not null)
        {
            await CancelAsync(request, schedules, trace, ct).ConfigureAwait(false);
            return;
        }

        if (request.State == TriggerRequestStates.Syncing)
        {
            await SyncAsync(request, schedules, trace, ct).ConfigureAwait(false);
        }

        if (request.State == TriggerRequestStates.Firing)
        {
            await FireAsync(request, schedules, trace, renewLease, ct).ConfigureAwait(false);
        }

        if (request.State == TriggerRequestStates.Running)
        {
            await FollowAsync(request, schedules, trace, ct).ConfigureAwait(false);
        }

        await SaveAsync(request, ct).ConfigureAwait(false);
    }

    // ---- Sync -----------------------------------------------------------------------------------------------------------

    private async Task SyncAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, CancellationToken ct)
    {
        var source = await _catalog.RepoSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == request.RepoName, ct).ConfigureAwait(false);
        if (source is null)
        {
            await FailAsync(request, schedules, trace, $"The repo source '{request.RepoName}' no longer exists.", ct).ConfigureAwait(false);
            return;
        }

        if (!request.Sync)
        {
            request.SyncOutcome = TriggerSyncOutcomes.Skipped;
            request.SyncedCommit = source.LastSyncedSha;
            await trace.InfoAsync("sync", $"No sync was asked for; firing on the commit '{request.RepoName}' is synced at, {Short(source.LastSyncedSha)}.", ct).ConfigureAwait(false);
            request.State = TriggerRequestStates.Firing;
            return;
        }

        if (request.SyncRequestedUtc is not { } requestedUtc)
        {
            await RequestSyncAsync(request, schedules, source, trace, ct).ConfigureAwait(false);
            return;
        }

        if (!source.Enabled)
        {
            await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.Failed,
                $"The repo source '{request.RepoName}' was disabled while the request waited for its sync.", ct).ConfigureAwait(false);
            return;
        }

        var answered = RepoSourceStore.IsSyncAnswered(source, requestedUtc);
        await CopySyncLinesAsync(request, source, answered, trace, ct).ConfigureAwait(false);
        if (!answered)
        {
            var timeout = TimeSpan.FromMinutes(_options.ExternalTriggers.SyncTimeoutMinutes);
            if (Now() - requestedUtc > timeout)
            {
                var hint = _options.ManagedSync.Enabled
                    ? string.Empty
                    : " This control plane does not run the managed sync (ControlPlane:ManagedSync:Enabled is false), so another one must.";
                await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.TimedOut,
                    $"No sync of '{request.RepoName}' answered the request within {_options.ExternalTriggers.SyncTimeoutMinutes} minutes "
                    + $"(ControlPlane:ExternalTriggers:SyncTimeoutMinutes).{hint}", ct).ConfigureAwait(false);
            }

            return;
        }

        if (source.LastError is { } error)
        {
            request.SyncError = error;
            await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.Failed, $"The sync of '{request.RepoName}' failed: {error}", ct).ConfigureAwait(false);
            return;
        }

        request.SyncedCommit = source.LastSyncedSha;
        if (request.ExpectedCommit is { } expected && source.LastSyncedSha is { } synced && !SameCommit(synced, expected))
        {
            var containment = await _containment.ContainsAsync(source, synced, expected, ct).ConfigureAwait(false);
            if (containment.Contains is not true)
            {
                var reason = containment.Contains is false
                    ? $"The sync read {Short(synced)}, which does not contain {Short(expected)}. Is {Short(expected)} on branch '{source.Branch}', the branch '{request.RepoName}' syncs?"
                    : containment.Problem!;
                await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.Succeeded, reason, ct).ConfigureAwait(false);
                return;
            }

            await trace.InfoAsync("sync", $"Synced at {Short(synced)}, which contains {Short(expected)}: the branch moved on past it before the sync read it.", ct).ConfigureAwait(false);
        }
        else
        {
            await trace.InfoAsync("sync", $"Synced at {Short(source.LastSyncedSha)}.", ct).ConfigureAwait(false);
        }

        request.SyncOutcome = TriggerSyncOutcomes.Succeeded;
        request.State = TriggerRequestStates.Firing;
    }

    /// <summary>Asks for the request's sync, or finds it needs none: a repository already synced at the expected commit is
    /// current, and syncing it again would change nothing.</summary>
    private async Task RequestSyncAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, CatalogRepoSource source, ActivityTrace trace,
        CancellationToken ct)
    {
        if (!source.Enabled)
        {
            await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.Failed,
                $"The repo source '{request.RepoName}' is disabled, so nothing syncs it; enable it on the Repositories page.", ct).ConfigureAwait(false);
            return;
        }

        if (request.ExpectedCommit is { } expected && source.LastSyncedSha is { } synced && SameCommit(synced, expected)
            && source.LastError is null && !RepoSourceStore.IsSyncRunning(source))
        {
            request.SyncOutcome = TriggerSyncOutcomes.Current;
            request.SyncedCommit = synced;
            await trace.InfoAsync("sync", $"'{request.RepoName}' is already synced at {Short(synced)}; no sync is needed.", ct).ConfigureAwait(false);
            request.State = TriggerRequestStates.Firing;
            return;
        }

        var nowUtc = Now();
        if (await RepoSourceStore.RequestSyncAsync(_catalog, source.Id, nowUtc, forceLineage: false, ct).ConfigureAwait(false)
            == RepoSourceMutation.NotFound)
        {
            await FailSyncAsync(request, schedules, trace, TriggerSyncOutcomes.Failed,
                $"The repo source '{request.RepoName}' is disabled or was removed, so nothing syncs it.", ct).ConfigureAwait(false);
            return;
        }

        // The sync's own trace says who asked, so an operator watching the repository's sync knows why it ran.
        var syncTrace = await ActivityTrace.BeginAsync(_catalog, ActivityKinds.RepoSync, source.Id.ToString(), _clock, ct).ConfigureAwait(false);
        await syncTrace.InfoAsync("queued", $"Sync requested by {request.RequestedBy} (trigger request {request.Id:D}); waiting for a worker to pick it up.", ct).ConfigureAwait(false);
        _syncSignal.Wake();

        request.SyncRequestedUtc = nowUtc;
        await trace.InfoAsync("sync", $"Asked for a sync of '{request.RepoName}' (branch {source.Branch})"
            + (request.ExpectedCommit is { } commit ? $", expecting {Short(commit)}" : string.Empty) + ".", ct).ConfigureAwait(false);
        if (!_options.ManagedSync.Enabled)
        {
            await trace.WarnAsync("sync", "This control plane does not run the managed sync (ControlPlane:ManagedSync:Enabled is false); another control plane must pick the sync up.", ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies the lines the answering sync attempt has written since the last copy into the request's trace. While the
    /// sync runs, the attempt is the one whose start line was written at or after it started (it started after the
    /// request); once it has answered, the source names it. Each line is copied once (<see cref="CatalogTriggerRequest.SyncCopiedThroughId"/>),
    /// at most <see cref="MaxCopiedSyncLines"/> in all.
    /// </summary>
    private async Task CopySyncLinesAsync(
        CatalogTriggerRequest request, CatalogRepoSource source, bool answered, ActivityTrace trace, CancellationToken ct)
    {
        var subject = source.Id.ToString();
        Guid? activityId = answered ? source.LastSyncActivityId : request.SyncActivityId;
        if (activityId is null && !answered && RepoSourceStore.IsSyncRunning(source) && source.SyncStartedUtc is { } started
            && started >= request.SyncRequestedUtc)
        {
            activityId = await _catalog.ActivityEvents.AsNoTracking()
                .Where(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == subject && e.Step == "start" && e.TimestampUtc >= started)
                .OrderBy(e => e.Id)
                .Select(e => (Guid?)e.ActivityId)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        if (activityId is not { } attempt)
        {
            if (answered)
            {
                await trace.WarnAsync("sync", "The sync answered, but its trace is no longer kept, so its lines cannot be shown here.", ct).ConfigureAwait(false);
            }

            return;
        }

        if (request.SyncActivityId != attempt)
        {
            // Another attempt than the one copied so far answered (the first was abandoned): copy this one from its start.
            request.SyncActivityId = attempt;
            request.SyncCopiedThroughId = null;
        }

        var after = request.SyncCopiedThroughId ?? 0;
        var subjectOfRequest = request.Id.ToString("D");
        var copied = await _catalog.ActivityEvents.AsNoTracking()
            .CountAsync(e => e.Kind == ActivityKinds.TriggerRequest && e.SubjectKey == subjectOfRequest && e.Step!.StartsWith(SyncLineStep), ct)
            .ConfigureAwait(false);
        var lines = await _catalog.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == subject && e.ActivityId == attempt && e.Id > after)
            .OrderBy(e => e.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var line in lines)
        {
            request.SyncCopiedThroughId = line.Id;
            if (copied >= MaxCopiedSyncLines)
            {
                continue;
            }

            // Each line keeps the sync's own step under the sync: prefix, so the trace reads "sync:clone" and the message as written.
            var step = SyncLineStep + (string.IsNullOrWhiteSpace(line.Step) ? "line" : line.Step);
            step = step.Length > 64 ? step[..64] : step;
            var text = Bound(line.Message);
            switch (line.Level)
            {
                case SqlFlow.Core.Runs.RunEventLevels.Error:
                    await trace.ErrorAsync(step, text, ct).ConfigureAwait(false);
                    break;
                case SqlFlow.Core.Runs.RunEventLevels.Warning:
                    await trace.WarnAsync(step, text, ct).ConfigureAwait(false);
                    break;
                default:
                    await trace.InfoAsync(step, text, ct).ConfigureAwait(false);
                    break;
            }

            copied++;
        }

        if (answered && copied >= MaxCopiedSyncLines)
        {
            var total = await _catalog.ActivityEvents.AsNoTracking()
                .CountAsync(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == subject && e.ActivityId == attempt, ct)
                .ConfigureAwait(false);
            if (total > MaxCopiedSyncLines)
            {
                await trace.WarnAsync("sync", $"The sync wrote {total} lines; the first {MaxCopiedSyncLines} are shown. The repository's sync trace holds the rest while it is kept.", ct).ConfigureAwait(false);
            }
        }
    }

    // ---- Fire -----------------------------------------------------------------------------------------------------------

    private async Task FireAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace,
        Func<CancellationToken, Task<bool>> renewLease, CancellationToken ct)
    {
        if (schedules.Count == 0)
        {
            await CompleteAsync(request, schedules, trace,
                $"'{request.RepoName}' is synced at {Short(request.SyncedCommit)}; no schedule was asked for.", ct).ConfigureAwait(false);
            return;
        }

        var repoId = FlowIdentity.FromName(request.RepoName);
        var batch = TriggerRequestStore.ParseBatch(request.Batch);
        foreach (var row in schedules.Where(s => s.State is TriggerScheduleStates.Pending or TriggerScheduleStates.Firing))
        {
            var schedule = await _catalog.Schedules.AsNoTracking()
                .FirstOrDefaultAsync(s => s.RepoId == repoId && s.Name == row.ScheduleName, ct).ConfigureAwait(false);
            if (schedule is null)
            {
                var names = await _catalog.Schedules.AsNoTracking().Where(s => s.RepoId == repoId)
                    .OrderBy(s => s.Name).Select(s => s.Name).Take(21).ToListAsync(ct).ConfigureAwait(false);
                var known = names.Count == 0
                    ? "it has no schedules."
                    : "its schedules are " + string.Join(", ", names.Take(20)) + (names.Count > 20 ? ", ..." : ".");
                await NotFiredAsync(row, trace,
                    $"No schedule named '{row.ScheduleName}' in '{request.RepoName}' at {Short(request.SyncedCommit)}; {known}", ct).ConfigureAwait(false);
                continue;
            }

            row.ScheduleId = schedule.Id;
            if (!schedule.Enabled)
            {
                await NotFiredAsync(row, trace, $"Schedule '{schedule.Name}' is disabled in its YAML (enabled: false).", ct).ConfigureAwait(false);
                continue;
            }

            if (schedule.Paused)
            {
                await NotFiredAsync(row, trace, $"Schedule '{schedule.Name}' is paused by an operator; resume it on the Schedules page to fire it.", ct).ConfigureAwait(false);
                continue;
            }

            if (row.State == TriggerScheduleStates.Firing && await AdoptAsync(request, row, schedule, trace, ct).ConfigureAwait(false))
            {
                continue;
            }

            // A fire happens once: the lease is extended (and so proven) right before it, and the row is marked firing
            // first, so a replica that takes over after a crash finds the runs this fire queued instead of firing again.
            if (!await renewLease(ct).ConfigureAwait(false))
            {
                return;
            }

            var firingUtc = Now();
            row.State = TriggerScheduleStates.Firing;
            row.FiringUtc = firingUtc;
            await SaveAsync(request, ct).ConfigureAwait(false);

            ScheduleFire.FireResult fire;
            try
            {
                fire = await ScheduleFire.EnqueueAsync(
                    _catalog, _dispatcher, _documents, schedule, firingUtc, ct, batch.Count == 0 ? null : batch, null,
                    request.RequestedBy).ConfigureAwait(false);
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                await NotFiredAsync(row, trace, $"Schedule '{schedule.Name}' refused to fire: {ex.Message}", ct).ConfigureAwait(false);
                continue;
            }

            if (!fire.Queued)
            {
                await NotFiredAsync(row, trace, batch.Count == 0
                    ? $"Schedule '{schedule.Name}' has nothing to run: nothing joins it, or every member is deactivated or mode: manual."
                    : $"Schedule '{schedule.Name}' has no runnable member in batch {string.Join(", ", batch)}.", ct).ConfigureAwait(false);
                continue;
            }

            if (!request.Chain)
            {
                await ScheduleStore.SuppressChainAsync(_catalog, schedule.RepoId, schedule.Name, firingUtc, ct).ConfigureAwait(false);
            }

            await MarkFiredAsync(request, row, fire.RunId, fire.GroupId, fire.MemberCount, trace, ct).ConfigureAwait(false);
        }

        if (schedules.Any(s => s.State == TriggerScheduleStates.Running))
        {
            request.State = TriggerRequestStates.Running;
            return;
        }

        if (schedules.All(s => TriggerScheduleStates.IsTerminal(s.State)))
        {
            await CompleteAsync(request, schedules, trace, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>For a schedule found mid-fire: adopts the runs its fire queued (the schedule's runs this request asked for,
    /// queued since the fire began). True when there were some.</summary>
    private async Task<bool> AdoptAsync(
        CatalogTriggerRequest request, CatalogTriggerRequestSchedule row, CatalogSchedule schedule, ActivityTrace trace, CancellationToken ct)
    {
        if (row.FiringUtc is not { } firingUtc)
        {
            return false;
        }

        var runs = await _catalog.Runs.AsNoTracking()
            .Where(r => r.TriggerScheduleId == schedule.Id && r.RequestedBy == request.RequestedBy && r.EnqueuedUtc >= firingUtc)
            .OrderBy(r => r.EnqueuedUtc).ThenBy(r => r.GroupWave)
            .Select(r => new { r.RunId, r.GroupId })
            .ToListAsync(ct).ConfigureAwait(false);
        if (runs.Count == 0)
        {
            return false;
        }

        var groupId = runs[0].GroupId;
        var count = groupId is { } g ? runs.Count(r => r.GroupId == g) : 1;
        await trace.InfoAsync("fire", $"Schedule '{schedule.Name}' had fired before this control plane took the request over; following the runs it queued.", ct).ConfigureAwait(false);
        await MarkFiredAsync(request, row, runs[0].RunId, groupId, count, trace, ct).ConfigureAwait(false);
        return true;
    }

    private async Task MarkFiredAsync(
        CatalogTriggerRequest request, CatalogTriggerRequestSchedule row, Guid runId, Guid? groupId, int memberCount, ActivityTrace trace,
        CancellationToken ct)
    {
        var runs = await RunsOf(groupId, runId)
            .Select(r => new { r.RunId, r.FlowName, r.CommitSha })
            .ToListAsync(ct).ConfigureAwait(false);
        var commit = groupId is { } g
            ? await _catalog.RunGroups.AsNoTracking().Where(x => x.GroupId == g).Select(x => x.CommitSha).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            : runs.FirstOrDefault()?.CommitSha;

        row.State = TriggerScheduleStates.Running;
        row.RunId = runId;
        row.GroupId = groupId;
        row.MemberCount = memberCount;
        row.CommitSha = commit;
        row.FiredUtc = Now();
        row.Detail = null;
        await SaveAsync(request, ct).ConfigureAwait(false);
        await TriggerRequestStore.LinkRunsAsync(_catalog, request.Id, row.Ordinal, runs.Select(r => (r.RunId, r.FlowName)).ToList(), ct).ConfigureAwait(false);

        await trace.InfoAsync("fire", memberCount == 1
            ? $"Fired schedule '{row.ScheduleName}': flow {runs.FirstOrDefault()?.FlowName ?? "?"} queued as run {runId:D}{At(commit)}."
            : $"Fired schedule '{row.ScheduleName}': {memberCount} flows queued as group {groupId:D}{At(commit)}.", ct).ConfigureAwait(false);
    }

    private async Task NotFiredAsync(CatalogTriggerRequestSchedule row, ActivityTrace trace, string reason, CancellationToken ct)
    {
        row.State = TriggerScheduleStates.NotFired;
        row.Detail = Bound(reason, 2000);
        row.CompletedUtc = Now();
        await trace.ErrorAsync("fire", reason, ct).ConfigureAwait(false);
    }

    // ---- Follow ---------------------------------------------------------------------------------------------------------

    private async Task FollowAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, CancellationToken ct)
    {
        var links = (await TriggerRequestStore.RunsOfAsync(_catalog, request.Id, ct).ConfigureAwait(false))
            .ToDictionary(l => l.RunId);
        foreach (var row in schedules.Where(s => s.State == TriggerScheduleStates.Running))
        {
            var runs = await RunsOf(row.GroupId, row.RunId).ToListAsync(ct).ConfigureAwait(false);

            var unlinked = runs.Where(r => !links.ContainsKey(r.RunId)).ToList();
            if (unlinked.Count > 0)
            {
                await TriggerRequestStore.LinkRunsAsync(_catalog, request.Id, row.Ordinal, unlinked.Select(r => (r.RunId, r.FlowName)).ToList(), ct).ConfigureAwait(false);
                foreach (var run in unlinked)
                {
                    links[run.RunId] = new CatalogTriggerRequestRun { RequestId = request.Id, RunId = run.RunId, FlowName = run.FlowName, ScheduleOrdinal = row.Ordinal };
                }
            }

            foreach (var run in runs)
            {
                await ReportAsync(request, run, links[run.RunId], trace, ct).ConfigureAwait(false);
            }

            if (!runs.All(r => RunStatuses.IsTerminal(r.Status)))
            {
                continue;
            }

            // Runs that left the catalog before they ended (their repository was removed, or a cleanup took them) count
            // as not succeeded: nothing says they did.
            var total = Math.Max(row.MemberCount ?? runs.Count, runs.Count);
            var missing = total - runs.Count;
            row.Succeeded = runs.Count(r => r.Status == RunStatuses.Succeeded);
            row.Failed = runs.Count(r => r.Status == RunStatuses.Failed) + missing;
            row.Cancelled = runs.Count(r => r.Status == RunStatuses.Cancelled);
            row.Skipped = runs.Count(r => r.Status == RunStatuses.Skipped);
            row.State = row.Succeeded == total && total > 0 ? TriggerScheduleStates.Succeeded : TriggerScheduleStates.Failed;
            row.Detail = Counts(row, total) + (missing == 0
                ? string.Empty
                : $" {missing} of its runs {(missing == 1 ? "is" : "are")} no longer in the catalog.");
            row.CompletedUtc = Now();
            var line = $"Schedule '{row.ScheduleName}' {(row.State == TriggerScheduleStates.Succeeded ? "succeeded" : "failed")}: {row.Detail}";
            if (row.State == TriggerScheduleStates.Succeeded)
            {
                await trace.InfoAsync("schedule", line, ct).ConfigureAwait(false);
            }
            else
            {
                await trace.ErrorAsync("schedule", line, ct).ConfigureAwait(false);
            }
        }

        if (schedules.All(s => TriggerScheduleStates.IsTerminal(s.State)))
        {
            await CompleteAsync(request, schedules, trace, null, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a run's start and end into the request's trace, each once whichever replica sees it: a run that
    /// started and ended between two passes is reported by its end alone.</summary>
    private async Task ReportAsync(CatalogTriggerRequest request, CatalogRun run, CatalogTriggerRequestRun link, ActivityTrace trace, CancellationToken ct)
    {
        if (run.Status == link.ReportedStatus || run.Status == RunStatuses.Queued)
        {
            return;
        }

        if (!await TriggerRequestStore.TryMarkReportedAsync(_catalog, request.Id, run.RunId, run.Status, ct).ConfigureAwait(false))
        {
            return;
        }

        link.ReportedStatus = run.Status;
        var took = run.DurationSeconds is { } seconds ? " in " + Duration(seconds) : string.Empty;
        switch (run.Status)
        {
            case RunStatuses.Running:
                await trace.InfoAsync("run", $"{run.FlowName} started (run {run.RunId:D}).", ct).ConfigureAwait(false);
                break;
            case RunStatuses.Succeeded:
                var did = RunOutcomeDescriptions.Describe(run, _describers, _logger);
                await trace.InfoAsync("run", $"{run.FlowName} succeeded{took}" + (did is null ? "." : $": {did}."), ct).ConfigureAwait(false);
                break;
            case RunStatuses.Failed:
                await trace.ErrorAsync("run", Bound($"{run.FlowName} failed{took}: {FirstLine(run.Error) ?? "no error was recorded"}"), ct).ConfigureAwait(false);
                break;
            case RunStatuses.Skipped:
                await trace.WarnAsync("run", $"{run.FlowName} was skipped: a flow it depends on did not succeed.", ct).ConfigureAwait(false);
                break;
            case RunStatuses.Cancelled:
                await trace.WarnAsync("run", $"{run.FlowName} was cancelled{took}.", ct).ConfigureAwait(false);
                break;
            default:
                await trace.InfoAsync("run", $"{run.FlowName} is {run.Status}.", ct).ConfigureAwait(false);
                break;
        }
    }

    // ---- Ending ---------------------------------------------------------------------------------------------------------

    private async Task CancelAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, CancellationToken ct)
    {
        foreach (var row in schedules)
        {
            switch (row.State)
            {
                case TriggerScheduleStates.Running or TriggerScheduleStates.Firing when row.GroupId is { } groupId:
                    await _dispatcher.CancelGroupAsync(_catalog, groupId, ct).ConfigureAwait(false);
                    row.State = TriggerScheduleStates.Cancelled;
                    break;
                case TriggerScheduleStates.Running or TriggerScheduleStates.Firing when row.RunId is { } runId:
                    await _dispatcher.CancelAsync(_catalog, runId, ct).ConfigureAwait(false);
                    row.State = TriggerScheduleStates.Cancelled;
                    break;
                case TriggerScheduleStates.Pending or TriggerScheduleStates.Firing:
                    row.State = TriggerScheduleStates.NotFired;
                    break;
                default:
                    continue;
            }

            row.Detail = "The request was cancelled.";
            row.CompletedUtc = Now();
        }

        request.State = TriggerRequestStates.Cancelled;
        request.Detail = $"Cancelled by {request.CancelRequestedBy ?? "someone"}.";
        request.CompletedUtc = Now();
        await trace.CompleteAsync(ActivityStatuses.Failed, request.Detail + " Its queued runs were cancelled and its running runs asked to stop.", ct).ConfigureAwait(false);
        await SaveAsync(request, ct).ConfigureAwait(false);
    }

    private async Task FailSyncAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, string outcome, string reason,
        CancellationToken ct)
    {
        request.SyncOutcome = outcome;
        await FailAsync(request, schedules, trace, reason, ct).ConfigureAwait(false);
    }

    private async Task FailAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, string reason, CancellationToken ct)
    {
        foreach (var row in schedules.Where(s => s.State is TriggerScheduleStates.Pending))
        {
            row.State = TriggerScheduleStates.NotFired;
            row.Detail = "Not fired: " + Bound(reason, 1900);
            row.CompletedUtc = Now();
        }

        request.State = TriggerRequestStates.Failed;
        request.Detail = Bound(reason, 2000);
        request.CompletedUtc = Now();
        await trace.CompleteAsync(ActivityStatuses.Failed, Bound(reason), ct).ConfigureAwait(false);
    }

    /// <summary>Ends a request whose sync went through: succeeded when every schedule it asked for fired and every run
    /// succeeded, failed otherwise, with one sentence saying which.</summary>
    private async Task CompleteAsync(
        CatalogTriggerRequest request, List<CatalogTriggerRequestSchedule> schedules, ActivityTrace trace, string? detail, CancellationToken ct)
    {
        var succeeded = schedules.All(s => s.State == TriggerScheduleStates.Succeeded);
        request.State = succeeded ? TriggerRequestStates.Succeeded : TriggerRequestStates.Failed;
        request.Detail = Bound(detail ?? Outcome(schedules), 2000);
        request.CompletedUtc = Now();
        await trace.CompleteAsync(succeeded ? ActivityStatuses.Succeeded : ActivityStatuses.Failed, request.Detail, ct).ConfigureAwait(false);
    }

    private static string Outcome(IReadOnlyList<CatalogTriggerRequestSchedule> schedules)
    {
        var good = schedules.Count(s => s.State == TriggerScheduleStates.Succeeded);
        var notFired = schedules.Where(s => s.State == TriggerScheduleStates.NotFired).Select(s => s.ScheduleName).ToList();
        var failed = schedules.Where(s => s.State is TriggerScheduleStates.Failed or TriggerScheduleStates.Cancelled).Select(s => s.ScheduleName).ToList();
        if (notFired.Count == 0 && failed.Count == 0)
        {
            return good == 1 ? $"Schedule '{schedules[0].ScheduleName}' succeeded." : $"All {good} schedules succeeded.";
        }

        var parts = new List<string>();
        if (failed.Count > 0)
        {
            parts.Add((failed.Count == 1 ? "schedule " : "schedules ") + string.Join(", ", failed.Select(n => $"'{n}'")) + " did not succeed");
        }

        if (notFired.Count > 0)
        {
            parts.Add((notFired.Count == 1 ? "schedule " : "schedules ") + string.Join(", ", notFired.Select(n => $"'{n}'")) + " did not fire");
        }

        var text = string.Join("; ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + (good > 0 ? $"; {good} succeeded." : ".");
    }

    private static string Counts(CatalogTriggerRequestSchedule row, int total)
    {
        var parts = new List<string> { $"{row.Succeeded} succeeded" };
        if (row.Failed > 0)
        {
            parts.Add($"{row.Failed} failed");
        }

        if (row.Skipped > 0)
        {
            parts.Add($"{row.Skipped} skipped");
        }

        if (row.Cancelled > 0)
        {
            parts.Add($"{row.Cancelled} cancelled");
        }

        return $"{total} {(total == 1 ? "flow" : "flows")}: {string.Join(", ", parts)}.";
    }

    /// <summary>A fire's runs in wave order: its group's members, or its one run.</summary>
    private IQueryable<CatalogRun> RunsOf(Guid? groupId, Guid? runId)
    {
        var runs = _catalog.Runs.AsNoTracking();
        runs = groupId is { } group ? runs.Where(r => r.GroupId == group) : runs.Where(r => r.RunId == runId);
        return runs.OrderBy(r => r.GroupWave).ThenBy(r => r.FlowName);
    }

    private async Task SaveAsync(CatalogTriggerRequest request, CancellationToken ct)
    {
        request.UpdatedUtc = Now();
        await _catalog.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Whether two spellings name the same commit: equal, or one a prefix of the other of at least 7 characters
    /// (a short SHA), compared without regard to case.</summary>
    public static bool SameCommit(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        var x = a.Trim();
        var y = b.Trim();
        if (string.Equals(x, y, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var (shorter, longer) = x.Length <= y.Length ? (x, y) : (y, x);
        return shorter.Length >= 7 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string? sha) => sha is null ? "(none)" : sha.Length > 12 ? sha[..12] : sha;

    private static string At(string? commit) => commit is null ? string.Empty : $" at {Short(commit)}";

    /// <summary>A duration as a person reads it: <c>12.3 s</c>, <c>4m 05s</c>, <c>26h 03m</c>.</summary>
    public static string Duration(double seconds)
    {
        if (seconds < 60)
        {
            return Math.Max(0, seconds).ToString("0.#", CultureInfo.InvariantCulture) + " s";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return seconds < 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{span.Minutes}m {span.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(long)span.TotalHours}h {span.Minutes:00}m");
    }

    private static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var redacted = SecretHygiene.RedactedMessage(text).Trim();
        var end = redacted.IndexOfAny(['\r', '\n']);
        return end < 0 ? redacted : redacted[..end];
    }

    private static string Bound(string text, int max = MaxLineLength)
        => text.Length <= max ? text : text[..(max - 12)] + " [truncated]";
}
