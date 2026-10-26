using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using SqlFlow.Core.Hosting;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Body of <c>POST /trigger-requests</c>: the repository (an external trigger's own when omitted), the commit
/// expected (the merge being reported), whether to sync first (default true), the schedules to fire in order, the batches
/// and chain every fire takes, and the caller's own key for the request.</summary>
public sealed record CreateTriggerRequest(
    string? Repo = null, string? Commit = null, bool? Sync = null, IReadOnlyList<string>? Schedules = null,
    IReadOnlyList<string>? Batch = null, bool? Chain = null, string? RequestKey = null);

/// <summary>How many of a fire's runs stand where.</summary>
public sealed record TriggerRunCountsDto(int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

/// <summary>One schedule a request asked to fire: where it stands, why it did not fire or how its runs ended, the run or
/// group its fire queued and the commit they run, the counts of its runs, and a link to them in the GUI.</summary>
public sealed record TriggerRequestScheduleDto(
    int Ordinal, string Name, Guid? ScheduleId, string State, string? Detail, Guid? RunId, Guid? GroupId, int? MemberCount,
    string? CommitSha, DateTime? FiredUtc, DateTime? CompletedUtc, TriggerRunCountsDto Counts, string? Url);

/// <summary>
/// One trigger request: who asked and from where, what it asked for, where it stands, how its sync went, each schedule it
/// asked to fire, and how it ended. <see cref="Conclusion"/> is <c>success</c>, <c>failure</c> or <c>cancelled</c> once it
/// has ended and null before, the one field a caller decides its own outcome by.
/// </summary>
public sealed record TriggerRequestDto(
    Guid Id, string RepoName, Guid? TriggerId, string? TriggerName, string RequestedBy, string? RequestKey, string? Origin,
    string? OriginUrl, string? ExpectedCommit, bool Sync, IReadOnlyList<string> Batch, bool Chain, string State, string? Conclusion,
    string? Detail, string? SyncOutcome, DateTime? SyncRequestedUtc, string? SyncedCommit, string? SyncError, DateTime CreatedUtc,
    DateTime UpdatedUtc, DateTime? CompletedUtc, DateTime? CancelRequestedUtc, string? CancelRequestedBy,
    IReadOnlyList<TriggerRequestScheduleDto> Schedules, string? Url);

/// <summary>One run a request's fires started: which schedule fired it, the flow, its wave, where it stands, when it ran,
/// what it did (<see cref="Outcome"/>), its error's first line when it failed, and a link to it in the GUI.</summary>
public sealed record TriggerRequestRunDto(
    Guid RunId, int ScheduleOrdinal, string FlowName, string FlowKind, int Wave, string Status, DateTime? StartUtc, DateTime? EndUtc,
    double? DurationSeconds, string? Outcome, string? Error, string? Url);

/// <summary>A request with every run its fires started, read live.</summary>
public sealed record TriggerRequestDetailDto(TriggerRequestDto Request, IReadOnlyList<TriggerRequestRunDto> Runs);

/// <summary>A page of a request's trace after a cursor: the events, the cursor to ask from next, and whether the trace has
/// ended (the request is over and every event has been read).</summary>
public sealed record TriggerRequestEventsDto(IReadOnlyList<ActivityEventDto> Events, long Next, bool Done);

/// <summary>
/// Trigger requests: a repository sync and, optionally, a selection of its schedules fired once the sync has run. An
/// external trigger (a GitHub workflow after a merge, a key holder) makes them with the credential its exchange issued and
/// reads back only its own; a person makes and reads any. A request is answered at once (202) and moved along in the
/// background (<see cref="TriggerRequestService"/>); its caller follows it here: the request, its live trace, the Markdown
/// summary to post where it reports, and each run's trace text.
/// </summary>
public static partial class TriggerRequestEndpoints
{
    /// <summary>The most events one page of a request's trace carries.</summary>
    public const int MaxEventsPerPage = 500;

    public static RouteGroupBuilder MapTriggerRequestEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var requests = group.MapGroup("/trigger-requests").WithTags("Trigger requests");
        requests.MapPost("/", CreateAsync).WithName("CreateTriggerRequest").AllowExternalTrigger();
        requests.MapGet("/", ListAsync).WithName("ListTriggerRequests").AllowExternalTrigger();
        requests.MapGet("/{id:guid}", GetAsync).WithName("GetTriggerRequest").AllowExternalTrigger();
        requests.MapGet("/{id:guid}/events", EventsAsync).WithName("GetTriggerRequestEvents").AllowExternalTrigger();
        requests.MapGet("/{id:guid}/summary", SummaryAsync).WithName("GetTriggerRequestSummary").AllowExternalTrigger();
        requests.MapGet("/{id:guid}/runs/{runId:guid}/trace", RunTraceAsync).WithName("GetTriggerRequestRunTrace").AllowExternalTrigger();
        requests.MapPost("/{id:guid}/cancel", CancelAsync).WithName("CancelTriggerRequest").AllowExternalTrigger();
        return group;
    }

    /// <summary>
    /// Makes a request: checks it at the trust boundary (the repository and its source, the commit's form, the schedules
    /// the caller may fire, the batches), records it with its first trace line, and wakes the processor. 202 with the
    /// request; 200 with the request already made when the caller's key repeats; 400 for a request no processing could
    /// carry out; 403 for a schedule or repository the trigger may not touch; 404 for an unknown repository; 503 while
    /// external triggers are turned off.
    /// </summary>
    private static async Task<IResult> CreateAsync(
        CreateTriggerRequest body, ClaimsPrincipal user, CatalogDbContext db, TriggerRequestSignal signal,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, CancellationToken ct)
    {
        if (!options.Value.ExternalTriggers.Enabled)
        {
            return TurnedOff();
        }

        if (body is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "The request has no body.");
        }

        CatalogExternalTrigger? trigger = null;
        if (ExternalTriggerCredential.IsTriggerCredential(user))
        {
            trigger = await CurrentTriggerAsync(db, user, ct).ConfigureAwait(false);
            if (trigger is null)
            {
                return Problem(StatusCodes.Status403Forbidden, "Trigger not available",
                    "The trigger this credential was issued to was disabled or removed; it can make no request.");
            }
        }

        var repo = string.IsNullOrWhiteSpace(body.Repo) ? trigger?.RepoName : body.Repo.Trim();
        if (repo is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "Name the repository to sync (repo).");
        }

        if (trigger is not null && !string.Equals(repo, trigger.RepoName, StringComparison.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status403Forbidden, "Not this trigger's repository",
                $"Trigger '{trigger.Name}' syncs '{trigger.RepoName}', not '{repo}'.");
        }

        var source = await db.RepoSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == repo, ct).ConfigureAwait(false);
        if (source is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"No repo source named '{repo}'.");
        }

        var sync = body.Sync ?? true;
        var commit = string.IsNullOrWhiteSpace(body.Commit) ? null : body.Commit.Trim().ToLowerInvariant();
        if (commit is not null)
        {
            if (!CommitPattern().IsMatch(commit))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    $"commit must be a git commit id: 7 to 64 hexadecimal characters, not '{body.Commit}'.");
            }

            if (!sync)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    "A commit asks for the repository to be synced at it; leave sync on, or give no commit to fire on what is synced now.");
            }

            if (source.RemoteUrl is null)
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    $"Repo source '{repo}' reads a local directory live, which has no commits; give no commit.");
            }
        }

        if (sync && !source.Enabled)
        {
            return Problem(StatusCodes.Status409Conflict, "Repo source disabled",
                $"Repo source '{repo}' is disabled, so nothing syncs it; enable it, or ask for no sync.");
        }

        var schedules = new List<string>();
        foreach (var name in body.Schedules ?? [])
        {
            var trimmed = name?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 256 || trimmed.Any(char.IsControl))
            {
                return Problem(StatusCodes.Status400BadRequest, "Invalid request", "A schedule name is blank, longer than 256 characters, or holds a control character.");
            }

            if (!schedules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                schedules.Add(trimmed);
            }
        }

        if (schedules.Count > TriggerRequestStore.MaxSchedules)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", $"A request fires at most {TriggerRequestStore.MaxSchedules} schedules.");
        }

        if (trigger is not null)
        {
            var refused = schedules.Where(s => !ExternalTriggerStore.MayFire(trigger, s)).ToList();
            if (refused.Count > 0)
            {
                var allowed = ExternalTriggerStore.ParseSchedules(trigger.Schedules);
                return Problem(StatusCodes.Status403Forbidden, "Schedule not allowed",
                    $"Trigger '{trigger.Name}' may not fire {string.Join(", ", refused.Select(s => $"'{s}'"))}. "
                    + (allowed.Count == 0 ? "It may fire no schedule; it only syncs." : $"It may fire: {string.Join(", ", allowed)}."));
            }
        }

        var batch = (body.Batch ?? []).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim()).Distinct(StringComparer.Ordinal).ToList();
        if (batch.Count > TriggerRequestStore.MaxBatches || batch.Any(b => b.Length > 100 || b.Any(char.IsControl)))
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request",
                $"batch names at most {TriggerRequestStore.MaxBatches} batches of at most 100 characters each.");
        }

        if (batch.Count > 0 && schedules.Count == 0)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "batch narrows a schedule's fire; name the schedules to fire.");
        }

        var key = string.IsNullOrWhiteSpace(body.RequestKey) ? null : body.RequestKey.Trim();
        if (key is { Length: > 200 } || key?.Any(char.IsControl) == true)
        {
            return Problem(StatusCodes.Status400BadRequest, "Invalid request", "requestKey is at most 200 characters without control characters.");
        }

        var requestedBy = trigger is not null
            ? RunActors.Trigger(trigger.Name, trigger.Id)
            : RequestActor.Of(user) ?? RequestActor.Unknown;
        var now = clock.GetUtcNow().UtcDateTime;
        var (record, created) = await TriggerRequestStore.CreateAsync(
            db,
            new NewTriggerRequest(
                source.Name, source.Id, trigger?.Id, trigger?.Name, requestedBy, key,
                user.FindFirst(ExternalTriggerCredential.OriginClaim)?.Value, user.FindFirst(ExternalTriggerCredential.OriginUrlClaim)?.Value,
                commit, sync, batch, body.Chain ?? true, schedules),
            now, ct).ConfigureAwait(false);

        if (created)
        {
            var trace = await ActivityTrace.ResumeAsync(
                db, ActivityKinds.TriggerRequest, record.Request.Id.ToString("D"), record.Request.ActivityId, clock, ct).ConfigureAwait(false);
            await trace.InfoAsync("request", Describe(record.Request, schedules), ct).ConfigureAwait(false);
            signal.Wake();
        }

        var dto = await ViewAsync(db, record, options.Value, ct).ConfigureAwait(false);
        return created
            ? TypedResults.Accepted($"/api/v1/trigger-requests/{record.Request.Id:D}", dto)
            : TypedResults.Ok(dto);
    }

    /// <summary>The requests, newest first, narrowed by trigger, repository and state. An external trigger sees only its own.</summary>
    private static async Task<Ok<PagedResult<TriggerRequestDto>>> ListAsync(
        ClaimsPrincipal user, CatalogDbContext db, IOptions<ControlPlaneOptions> options, Guid? triggerId, string? repo, string? state,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var own = ExternalTriggerCredential.TriggerId(user);
        if (ExternalTriggerCredential.IsTriggerCredential(user))
        {
            triggerId = own ?? Guid.Empty;
        }

        var (items, total) = await TriggerRequestStore.ListAsync(db, new TriggerRequestQuery(triggerId, repo, state), p, size, ct).ConfigureAwait(false);
        var dtos = items.Select(r => ToDto(r, options.Value, liveCounts: null)).ToList();
        return TypedResults.Ok(new PagedResult<TriggerRequestDto>(dtos, p, size, total));
    }

    /// <summary>One request with every run its fires started, read live.</summary>
    private static async Task<Results<Ok<TriggerRequestDetailDto>, ProblemHttpResult>> GetAsync(
        Guid id, ClaimsPrincipal user, CatalogDbContext db, IOptions<ControlPlaneOptions> options,
        IEnumerable<IRunOutcomeDescriber> describers, ILogger<TriggerRequestSummaryLog> logger, CancellationToken ct)
    {
        var record = await OwnAsync(db, user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(id);
        }

        return TypedResults.Ok(await DetailAsync(db, record, options.Value, describers, logger, ct).ConfigureAwait(false));
    }

    /// <summary>The request's trace after <paramref name="after"/> (a cursor from the last page; 0 from the start), at most
    /// <paramref name="limit"/> events, and whether it has ended.</summary>
    private static async Task<Results<Ok<TriggerRequestEventsDto>, ProblemHttpResult>> EventsAsync(
        Guid id, ClaimsPrincipal user, CatalogDbContext db, long? after, int? limit, CancellationToken ct)
    {
        var record = await OwnAsync(db, user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(id);
        }

        var cursor = Math.Max(0, after ?? 0);
        var take = Math.Clamp(limit ?? MaxEventsPerPage, 1, MaxEventsPerPage);
        var subject = id.ToString("D");
        var events = await db.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.TriggerRequest && e.SubjectKey == subject && e.Id > cursor)
            .OrderBy(e => e.Id)
            .Take(take)
            .Select(e => new ActivityEventDto(e.Id, e.ActivityId, e.Kind, e.SubjectKey, e.Ordinal, e.TimestampUtc, e.Level, e.Step, e.Message, e.Terminal, e.Status))
            .ToListAsync(ct).ConfigureAwait(false);
        var next = events.Count == 0 ? cursor : events[^1].Id;
        var done = TriggerRequestStates.IsTerminal(record.Request.State)
            && events.Count < take
            && !await db.ActivityEvents.AsNoTracking().AnyAsync(e => e.Kind == ActivityKinds.TriggerRequest && e.SubjectKey == subject && e.Id > next, ct).ConfigureAwait(false);
        return TypedResults.Ok(new TriggerRequestEventsDto(events, next, done));
    }

    /// <summary>The request as Markdown (<see cref="TriggerRequestSummary"/>), for a caller to post where it reports.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> SummaryAsync(
        Guid id, ClaimsPrincipal user, CatalogDbContext db, IOptions<ControlPlaneOptions> options, ProductBranding branding,
        IEnumerable<IRunOutcomeDescriber> describers, ILogger<TriggerRequestSummaryLog> logger, CancellationToken ct)
    {
        var record = await OwnAsync(db, user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(id);
        }

        var detail = await DetailAsync(db, record, options.Value, describers, logger, ct).ConfigureAwait(false);
        var subject = id.ToString("D");
        var copied = await db.ActivityEvents.AsNoTracking()
            .Where(e => e.Kind == ActivityKinds.TriggerRequest && e.SubjectKey == subject && e.Step!.StartsWith(TriggerRequestProcessor.SyncLineStep))
            .OrderBy(e => e.Id)
            .Select(e => new { e.Step, e.Message })
            .ToListAsync(ct).ConfigureAwait(false);
        var syncLines = copied.Select(e => $"[{e.Step![TriggerRequestProcessor.SyncLineStep.Length..]}] {e.Message}").ToList();
        return TypedResults.Text(TriggerRequestSummary.Markdown(detail, syncLines, branding.ProductName), "text/markdown; charset=utf-8");
    }

    /// <summary>One run's whole trace as text, for a run this request's fires started: what a caller keeps beside its job.</summary>
    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> RunTraceAsync(
        Guid id, Guid runId, ClaimsPrincipal user, CatalogDbContext db, CancellationToken ct)
    {
        var record = await OwnAsync(db, user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(id);
        }

        var linked = await db.TriggerRequestRuns.AsNoTracking().AnyAsync(r => r.RequestId == id && r.RunId == runId, ct).ConfigureAwait(false);
        var run = linked ? await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false) : null;
        if (run is null)
        {
            return Problem(StatusCodes.Status404NotFound, "Not found", $"Trigger request {id:D} started no run {runId:D} that is still in the catalog.");
        }

        return await RunEndpoints.TraceTextAsync(db, run, ct).ConfigureAwait(false);
    }

    /// <summary>Asks to cancel a request: it stops waiting for its sync, fires nothing more, and cancels the runs it fired.
    /// 202 when asked; 409 when it had already ended.</summary>
    private static async Task<Results<Accepted<TriggerRequestDto>, ProblemHttpResult>> CancelAsync(
        Guid id, ClaimsPrincipal user, CatalogDbContext db, TriggerRequestSignal signal, IOptions<ControlPlaneOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        var record = await OwnAsync(db, user, id, ct).ConfigureAwait(false);
        if (record is null)
        {
            return NotFound(id);
        }

        var by = RequestActor.Of(user) ?? RequestActor.Unknown;
        var outcome = await TriggerRequestStore.RequestCancelAsync(db, id, by, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (outcome == TriggerRequestCancel.AlreadyEnded)
        {
            return Problem(StatusCodes.Status409Conflict, "Already ended", $"Trigger request {id:D} has already ended ({record.Request.State}).");
        }

        signal.Wake();
        var current = await TriggerRequestStore.GetAsync(db, id, ct).ConfigureAwait(false) ?? record;
        return TypedResults.Accepted($"/api/v1/trigger-requests/{id:D}", ToDto(current, options.Value, liveCounts: null));
    }

    // ---- Reading ----------------------------------------------------------------------------------------------------

    /// <summary>The request, when the caller may read it: any request for a person, only its own for an external trigger
    /// (another trigger's request reads as not found, so a trigger learns nothing about requests it did not make).</summary>
    private static async Task<TriggerRequestRecord?> OwnAsync(CatalogDbContext db, ClaimsPrincipal user, Guid id, CancellationToken ct)
    {
        var record = await TriggerRequestStore.GetAsync(db, id, ct).ConfigureAwait(false);
        if (record is null || !ExternalTriggerCredential.IsTriggerCredential(user))
        {
            return record;
        }

        var own = ExternalTriggerCredential.TriggerId(user);
        return own is { } trigger && record.Request.TriggerId == trigger ? record : null;
    }

    private static async Task<CatalogExternalTrigger?> CurrentTriggerAsync(CatalogDbContext db, ClaimsPrincipal user, CancellationToken ct)
    {
        if (ExternalTriggerCredential.TriggerId(user) is not { } id)
        {
            return null;
        }

        var trigger = await ExternalTriggerStore.FindAsync(db, id, ct).ConfigureAwait(false);
        return trigger is { Enabled: true } ? trigger : null;
    }

    /// <summary>A request as listed, with the run counts read live for its schedules.</summary>
    internal static async Task<TriggerRequestDto> ViewAsync(CatalogDbContext db, TriggerRequestRecord record, ControlPlaneOptions options, CancellationToken ct)
    {
        var counts = await LiveCountsAsync(db, record, ct).ConfigureAwait(false);
        return ToDto(record, options, counts);
    }

    /// <summary>A request with every run its fires started, each described and linked.</summary>
    internal static async Task<TriggerRequestDetailDto> DetailAsync(
        CatalogDbContext db, TriggerRequestRecord record, ControlPlaneOptions options, IEnumerable<IRunOutcomeDescriber> describers,
        ILogger logger, CancellationToken ct)
    {
        var links = await TriggerRequestStore.RunsOfAsync(db, record.Request.Id, ct).ConfigureAwait(false);
        var ids = links.Select(l => l.RunId).ToList();
        var runs = ids.Count == 0
            ? []
            : await db.Runs.AsNoTracking().Where(r => ids.Contains(r.RunId)).ToListAsync(ct).ConfigureAwait(false);
        var byId = runs.ToDictionary(r => r.RunId);
        var gui = options.Notifications.GuiBaseUrl;
        var describerList = describers.ToList();
        var runDtos = links
            .OrderBy(l => l.ScheduleOrdinal)
            .Select(l => byId.TryGetValue(l.RunId, out var run)
                ? new TriggerRequestRunDto(
                    run.RunId, l.ScheduleOrdinal, run.FlowName, run.FlowKind, run.GroupWave, run.Status, run.StartUtc, run.EndUtc,
                    run.DurationSeconds,
                    run.Status == RunStatuses.Succeeded ? RunOutcomeDescriptions.Describe(run, describerList, logger) : null,
                    run.Status == RunStatuses.Failed ? FirstLines(run.Error) : null,
                    Link(gui, $"/runs/{run.RunId:D}"))
                : new TriggerRequestRunDto(
                    l.RunId, l.ScheduleOrdinal, l.FlowName, string.Empty, 0, l.ReportedStatus, null, null, null,
                    "no longer in the catalog", null, null))
            .OrderBy(r => r.ScheduleOrdinal).ThenBy(r => r.Wave).ThenBy(r => r.FlowName, StringComparer.Ordinal)
            .ToList();
        var counts = runDtos.GroupBy(r => r.ScheduleOrdinal).ToDictionary(g => g.Key, g => Count(g.Select(r => r.Status), g.Count()));
        return new TriggerRequestDetailDto(ToDto(record, options, counts), runDtos);
    }

    private static async Task<IReadOnlyDictionary<int, TriggerRunCountsDto>> LiveCountsAsync(
        CatalogDbContext db, TriggerRequestRecord record, CancellationToken ct)
    {
        var id = record.Request.Id;
        var statuses = await db.TriggerRequestRuns.AsNoTracking()
            .Where(l => l.RequestId == id)
            .Join(db.Runs.AsNoTracking(), l => l.RunId, r => r.RunId, (l, r) => new { l.ScheduleOrdinal, r.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        return statuses.GroupBy(s => s.ScheduleOrdinal).ToDictionary(g => g.Key, g => Count(g.Select(s => s.Status), g.Count()));
    }

    private static TriggerRunCountsDto Count(IEnumerable<string> statuses, int total)
    {
        var list = statuses.ToList();
        return new TriggerRunCountsDto(
            total, list.Count(s => s == RunStatuses.Queued), list.Count(s => s == RunStatuses.Running),
            list.Count(s => s == RunStatuses.Succeeded), list.Count(s => s == RunStatuses.Failed),
            list.Count(s => s == RunStatuses.Cancelled), list.Count(s => s == RunStatuses.Skipped));
    }

    /// <summary>A request's view. <paramref name="liveCounts"/> are its schedules' run counts read now; without them a
    /// schedule shows the counts recorded when its runs ended (zero while they run).</summary>
    internal static TriggerRequestDto ToDto(
        TriggerRequestRecord record, ControlPlaneOptions options, IReadOnlyDictionary<int, TriggerRunCountsDto>? liveCounts)
    {
        var r = record.Request;
        var gui = options.Notifications.GuiBaseUrl;
        var schedules = record.Schedules.Select(s =>
        {
            var recorded = new TriggerRunCountsDto(
                s.Succeeded + s.Failed + s.Cancelled + s.Skipped, 0, 0, s.Succeeded, s.Failed, s.Cancelled, s.Skipped);
            var counts = liveCounts?.GetValueOrDefault(s.Ordinal) is { } live && !TriggerScheduleStates.IsTerminal(s.State) ? live : recorded;
            var url = s.GroupId is { } g ? Link(gui, $"/runs/groups/{g:D}") : s.RunId is { } run ? Link(gui, $"/runs/{run:D}") : null;
            return new TriggerRequestScheduleDto(
                s.Ordinal, s.ScheduleName, s.ScheduleId, s.State, s.Detail, s.RunId, s.GroupId, s.MemberCount, s.CommitSha,
                s.FiredUtc, s.CompletedUtc, counts, url);
        }).ToList();
        return new TriggerRequestDto(
            r.Id, r.RepoName, r.TriggerId, r.TriggerName, r.RequestedBy, r.RequestKey, r.Origin, r.OriginUrl, r.ExpectedCommit, r.Sync,
            TriggerRequestStore.ParseBatch(r.Batch), r.Chain, r.State, Conclusion(r.State), r.Detail, r.SyncOutcome, r.SyncRequestedUtc,
            r.SyncedCommit, r.SyncError, r.CreatedUtc, r.UpdatedUtc, r.CompletedUtc, r.CancelRequestedUtc, r.CancelRequestedBy, schedules,
            Link(gui, $"/external-triggers?request={r.Id:D}"));
    }

    /// <summary>How a caller reads a request's end: <c>success</c>, <c>failure</c>, <c>cancelled</c>, or null while it runs.</summary>
    public static string? Conclusion(string state)
        => state switch
        {
            TriggerRequestStates.Succeeded => "success",
            TriggerRequestStates.Failed => "failure",
            TriggerRequestStates.Cancelled => "cancelled",
            _ => null,
        };

    /// <summary>The request's first trace line: who asked for what.</summary>
    private static string Describe(CatalogTriggerRequest request, IReadOnlyList<string> schedules)
    {
        var what = !request.Sync
            ? $"no sync of '{request.RepoName}'"
            : request.ExpectedCommit is { } commit
                ? $"a sync of '{request.RepoName}' at {(commit.Length > 12 ? commit[..12] : commit)}"
                : $"a sync of '{request.RepoName}'";
        var then = schedules.Count == 0
            ? ", and no schedule"
            : $", then {(schedules.Count == 1 ? "schedule" : "schedules")} {string.Join(", ", schedules.Select(s => $"'{s}'"))}";
        var from = request.Origin is { } origin ? $" from {origin}" : string.Empty;
        return $"{request.RequestedBy} asked for {what}{then}{from}.";
    }

    private static string? FirstLines(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return null;
        }

        var redacted = SecretHygiene.RedactedMessage(error).Trim();
        var lines = redacted.Split('\n', 21);
        var shown = string.Join('\n', lines.Take(20)).TrimEnd();
        return (shown.Length > 4000 ? shown[..4000] + " [truncated]" : shown) + (lines.Length > 20 ? "\n..." : string.Empty);
    }

    private static string? Link(string? guiBaseUrl, string path)
        => string.IsNullOrWhiteSpace(guiBaseUrl) ? null : guiBaseUrl.TrimEnd('/') + path;

    internal static IResult TurnedOff()
        => Problem(StatusCodes.Status503ServiceUnavailable, "External triggers are turned off",
            "This control plane does not accept external triggers (ControlPlane:ExternalTriggers:Enabled is false).");

    private static ProblemHttpResult NotFound(Guid id)
        => TypedResults.Problem(detail: $"No trigger request {id:D}.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult Problem(int status, string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: status, title: title);

    [GeneratedRegex("^[0-9a-f]{7,64}$")]
    private static partial Regex CommitPattern();
}

/// <summary>The log category describers' failures are written under when a request is read.</summary>
public sealed class TriggerRequestSummaryLog;
