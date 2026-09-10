using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A run as it appears in lists: the lifecycle status, header dimensions, and headline row counts, without
/// the drill-down detail. Ordered newest-first by <see cref="WrittenUtc"/>. <see cref="Batch"/> and
/// <see cref="Wave"/> are joined in from the run's pipeline row at query time, the same way the batch report
/// combines the run log with the batch label and the lineage step: a flow whose YAML declares no batch, or a run
/// whose pipeline row left the catalog, reports under <see cref="CatalogPipeline.DefaultBatch"/>; a wave of -1
/// means lineage has not been computed for the repo (or the pipeline row is gone).
/// <see cref="LastAction"/>/<see cref="LastActionUtc"/> are the run's newest trace event (a stage summary, a
/// file read, a decision): while the run executes they answer "what is it doing right now", at rest "what did it
/// do last". Null for a run that recorded no events (one that predates the event stream, or is still queued).
/// They are resolved ONLY for a run group's member list (the runs list filtered by <see cref="GroupId"/>, and the
/// group stream): that is the one place any client renders them, and each costs a per-row lookup into the event
/// log for a message with no length bound, so the general run board (hundreds of rows, polled) does not pay for a
/// column it never shows and reports both as null there.
/// <see cref="Error"/> is why a failed run failed, carried on the summary so a set (a schedule's fire, a batch run)
/// can show its failures where they happened instead of making an operator open each member to find out. Null for
/// every run that did not fail.</summary>
public sealed record RunSummaryDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime WrittenUtc, DateTime? EnqueuedUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, Guid? GroupId,
    string? LastAction, DateTime? LastActionUtc, string? Error);

/// <summary>One run with its full header for the detail view: the summary plus the lifecycle fields (status, when it
/// was enqueued, the node that claimed it), the schema version, the start/end window, the host, the error, and the
/// run's substitution parameters (the built-in backfill's audit trail: full load, window, file pattern), and the
/// engine-computed incremental scope the run actually applied (mode, filter, resolved watermark and its source).
/// <see cref="Batch"/> and <see cref="Wave"/> follow the same pipeline-join semantics as
/// <see cref="RunSummaryDto"/>.</summary>
public sealed record RunDetailDto(
    Guid RunId, Guid PipelineId, Guid? RepoId, string FlowName, string FlowKind, string Batch, int Wave,
    string Status, bool Success,
    string? TargetPool, string? CommitSha, DateTime? EnqueuedUtc, string? ClaimedByNode, DateTime? CancelRequestedUtc,
    int SchemaVersion, DateTime WrittenUtc, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds,
    long? RowsLoaded, long? RowsInserted, long? RowsUpdated, long? RowsDeleted, int FileCount, string? Error, string? Host,
    bool FullLoad, DateTime? BackfillFrom, DateTime? BackfillTo, string? FilePattern, bool AssertionsOnly,
    bool ReprocessFromSourceMin, string? SourceFilter,
    string? IncrementalMode, string? IncrementalFilter, string? IncrementalWatermark, string? IncrementalWatermarkSource,
    string? DataSetConvention,
    int? FailedStatementOrdinal, string? FailedStatementStep, string? FailedStatementSql, Guid? GroupId);

/// <summary>One file a run processed (file flows): a drill-down row under a run.</summary>
public sealed record RunFileDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string? Path, long Rows, int Columns, long SizeBytes, string? Hash);

/// <summary>One data-quality assertion a run evaluated: a drill-down row under a run.</summary>
public sealed record RunAssertionDto(
    long Id, Guid RunId, Guid? RepoId, string Name, string Result, string AssertedValue, bool Evaluated, string? Error);

/// <summary>One generated SQL statement a run executed, in execution order: a drill-down row under a run.
/// <see cref="Error"/> is set only on the one statement that threw (the run's failure point), null otherwise.
/// <see cref="TimestampUtc"/> is when the statement was generated; null on rows projected from an artifact that
/// predates the timestamped trace.</summary>
public sealed record RunStatementDto(
    long Id, Guid RunId, Guid? RepoId, int Ordinal, DateTime? TimestampUtc, string Step, string Sql, string? Error);

/// <summary>
/// One entry of a run's consolidated trace: either a canonical run event (<see cref="Kind"/> is
/// <c>event</c>: file progress, a resolved watermark, an engine decision, a stage summary, a warning) or a
/// generated SQL statement (<see cref="Kind"/> is <c>statement</c>), the two streams interleaved by
/// <see cref="TimestampUtc"/> so the Trace view shows everything the run did in one ordered feed. An event
/// carries <see cref="Message"/> (plus optional <see cref="Rows"/>/<see cref="ElapsedMs"/>); a statement carries
/// <see cref="Sql"/> (plus <see cref="Error"/> on the one that threw). <see cref="Ordinal"/> is the 1-based
/// position within the entry's own stream. <see cref="TimestampUtc"/> is null only on statement rows projected
/// from an artifact that predates the timestamped trace; those sort first, in ordinal order.
/// </summary>
public sealed record RunTraceEntryDto(
    long Id, Guid RunId, Guid? RepoId, string Kind, int Ordinal, DateTime? TimestampUtc, string Level,
    string? Step, string? Message, string? Sql, string? Error, long? Rows, double? ElapsedMs);

/// <summary>The two kinds of <see cref="RunTraceEntryDto"/>.</summary>
public static class RunTraceKinds
{
    public const string Event = "event";
    public const string Statement = "statement";
}

/// <summary>The payload of the live trace stream's final <c>end</c> event: the run's terminal status. On
/// receiving it the client refetches the paged trace, which by then holds the authoritative re-projection
/// from the run artifact.</summary>
public sealed record RunTraceStreamEndDto(string Status);

/// <summary>One surrogate-key generation outcome of a run (ingestion flows): a drill-down row under a run.</summary>
public sealed record RunSurrogateKeyDto(
    long Id, Guid RunId, Guid? RepoId, int SurrogateKeyId, string SurrogateTable, string SurrogateColumn,
    bool IsRemote, long KeysGenerated, long RowsStamped, bool Executed, string? Error);

/// <summary>One per-metric health-check summary of a run (hc flows): a drill-down row under a run.</summary>
public sealed record RunHealthCheckMetricDto(
    long Id, Guid RunId, Guid? RepoId, string Name, int SeriesPoints, int ImputedPoints, int ImmaturePoints,
    int Anomalies, int LevelShifts, bool ModelTrained, string? ModelTrainer, string? Error);

/// <summary>One flow a scope expansion would run, with the wave that orders it in the set.</summary>
public sealed record RunScopePreviewMemberDto(string FlowName, string FlowKind, int Wave);

/// <summary>What a Node or Batch run would enqueue, without enqueuing anything: the resolved anchor, the member
/// count, how many waves they span, and the ordered members. The dialog and the lineage graph show this so an
/// operator sees "will run N flows across M waves" before committing.</summary>
public sealed record RunScopePreviewDto(
    string Scope, string Anchor, int MemberCount, int WaveCount, IReadOnlyList<RunScopePreviewMemberDto> Members);

/// <summary>A run group's member counts by lifecycle state, for the group view's rollup.</summary>
public sealed record RunGroupCountsDto(
    int Total, int Queued, int Running, int Succeeded, int Failed, int Cancelled, int Skipped);

/// <summary>One run group's header: what it was (mode, anchor), when it was enqueued, the commit every member is
/// pinned to, and the live rollup of its members' states. The members themselves come from the runs list filtered
/// by this group id.</summary>
public sealed record RunGroupDto(
    Guid GroupId, Guid RepoId, string Mode, string Anchor, int MemberCount, string? CommitSha, DateTime EnqueuedUtc,
    RunGroupCountsDto Counts);

/// <summary>
/// The read API over the shadow catalog's run history: the run headers and their drill-down detail (the files a run
/// processed, the assertions it evaluated, the SQL it executed, its surrogate-key outcomes, and its health-check
/// metrics), all projected from the on-disk <c>run.json</c> artifacts the sync mapped into the catalog. Every query
/// is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>) and DTO-projected so the raw EF
/// entities never leave the host; list endpoints are bounded by page size.
/// </summary>
public static class RunEndpoints
{
    public static RouteGroupBuilder MapRunEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var runs = group.MapGroup("/runs").WithTags("Runs");
        runs.MapGet("/", ListRunsAsync).WithName("ListRuns");
        runs.MapGet("/preview", PreviewScopeAsync).WithName("PreviewRunScope");
        runs.MapGet("/groups/{groupId:guid}", GetRunGroupAsync).WithName("GetRunGroup");
        runs.MapGet("/groups/{groupId:guid}/stream", StreamRunGroupAsync).WithName("StreamRunGroup");
        runs.MapGet("/{runId:guid}", GetRunAsync).WithName("GetRun");
        runs.MapGet("/{runId:guid}/files", GetRunFilesAsync).WithName("GetRunFiles");
        runs.MapGet("/{runId:guid}/assertions", GetRunAssertionsAsync).WithName("GetRunAssertions");
        runs.MapGet("/{runId:guid}/statements", GetRunStatementsAsync).WithName("GetRunStatements");
        runs.MapGet("/{runId:guid}/trace", GetRunTraceAsync).WithName("GetRunTrace");
        runs.MapGet("/{runId:guid}/trace/text", GetRunTraceTextAsync).WithName("GetRunTraceText");
        runs.MapGet("/{runId:guid}/trace/stream", StreamRunTraceAsync).WithName("StreamRunTrace");

        // The pipeline-anchored trace: the latest run's consolidated trace without knowing a run id up front,
        // the entry point for debugging a pipeline ("show me what its last run did"). The text form renders the
        // whole trace as one plain-text document, made for pasting into a ticket or handing to an LLM.
        var pipelines = group.MapGroup("/pipelines").WithTags("Runs");
        pipelines.MapGet("/{pipelineId:guid}/trace", GetPipelineTraceAsync).WithName("GetPipelineLatestRunTrace");
        pipelines.MapGet("/{pipelineId:guid}/trace/text", GetPipelineTraceTextAsync).WithName("GetPipelineLatestRunTraceText");
        runs.MapGet("/{runId:guid}/surrogate-keys", GetRunSurrogateKeysAsync).WithName("GetRunSurrogateKeys");
        runs.MapGet("/{runId:guid}/health-metrics", GetRunHealthMetricsAsync).WithName("GetRunHealthMetrics");

        return group;
    }

    private static async Task<Ok<PagedResult<RunSummaryDto>>> ListRunsAsync(
        CatalogDbContext db, Guid? repoId, Guid? pipelineId, string? flowKind, string? status, bool? success,
        string? flowName, string? batch, Guid? scheduleId, Guid? groupId, bool? latest, DateTime? from, DateTime? to,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Runs.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (pipelineId is { } pid)
        {
            query = query.Where(x => x.PipelineId == pid);
        }

        // A time window over the run history (the schedules timeline reads runs by day). The window is applied on
        // WrittenUtc: the moment the run's outcome landed in the catalog and the same key the list already orders
        // newest-first by, so the (WrittenUtc)-ordered scan the endpoint runs answers the range directly. Both
        // bounds are optional and inclusive; a caller can pass just one to open-end the window in either direction.
        // WrittenUtc is stored as a UTC wall-clock value, but query-string binding turns an ISO instant like
        // "2026-03-10T12:00:00Z" into a server-local DateTime, so each bound is normalized back to UTC before it is
        // compared: without this the window would be off by the host's UTC offset.
        if (from is { } fromUtc)
        {
            var lower = ToUtc(fromUtc);
            query = query.Where(x => x.WrittenUtc >= lower);
        }

        if (to is { } toUtc)
        {
            var upper = ToUtc(toUtc);
            query = query.Where(x => x.WrittenUtc <= upper);
        }

        // A group's member runs, for the group view: every run stamped with this GroupId, in wave order below.
        if (groupId is { } gid)
        {
            query = query.Where(x => x.GroupId == gid);
        }

        // The runs of one schedule's flows: the run board's schedule filter, resolved through MEMBERSHIP (the same
        // selector a fire uses) rather than a stamp on the run, so it covers a flow's manual and scheduled runs
        // alike. A run survives when its pipeline joined this schedule.
        if (scheduleId is { } sid)
        {
            query = query.Where(x => db.ScheduleMembers.Any(m => m.ScheduleId == sid && m.PipelineId == x.PipelineId));
        }

        if (!string.IsNullOrWhiteSpace(flowKind))
        {
            query = query.Where(x => x.FlowKind == flowKind);
        }

        if (!string.IsNullOrWhiteSpace(flowName))
        {
            query = query.Where(x => x.FlowName.Contains(flowName));
        }

        // latest=true keeps only each pipeline's newest run: the batch status board ("what is red right now"),
        // one row per pipeline, versus the full history the flat inbox and the pipeline detail show. The newest
        // run of a pipeline is the top-1 of its runs ordered by WrittenUtc then RunId, both descending (RunId
        // breaks WrittenUtc ties, and being the primary key it makes the maximum unique).
        //
        // The set is built PER PIPELINE, not per run: the distinct pipeline list drives one TOP(1) seek each on
        // the (PipelineId, WrittenUtc DESC, RunId DESC) index, and the outer query keeps the runs whose id is in
        // that set. Correlating the top-1 to the outer row instead (x.RunId == top-1 for x.PipelineId) reads
        // identically but makes the optimizer evaluate the seek once per row of the WHOLE run history to keep the
        // few hundred that survive, so its cost grew with every run ever recorded rather than with the number of
        // pipelines. Distinct pipelines is the real cardinality of the answer.
        //
        // It still applies BEFORE the lifecycle filters below so status=failed means "currently failed", not
        // "ever failed", and the source is db.Runs (not the Pipeline table) so a run whose pipeline left the
        // catalog still reports its own latest.
        if (latest == true)
        {
            var latestRunIds = db.Runs.AsNoTracking()
                .Select(run => run.PipelineId)
                .Distinct()
                .Select(pipelineId => db.Runs
                    .Where(candidate => candidate.PipelineId == pipelineId)
                    .OrderByDescending(candidate => candidate.WrittenUtc)
                    .ThenByDescending(candidate => candidate.RunId)
                    .Select(candidate => candidate.RunId)
                    .FirstOrDefault());
            query = query.Where(x => latestRunIds.Contains(x.RunId));
        }

        // Lifecycle filter (queued / running / succeeded / failed / cancelled): the GUI's "active runs" and
        // "failures" views. Normalized to lower case so the query is case-insensitive on the stored value.
        if (!string.IsNullOrWhiteSpace(status))
        {
            var normalized = status.Trim().ToLowerInvariant();
            query = query.Where(x => x.Status == normalized);
        }

        if (success is { } s)
        {
            query = query.Where(x => x.Success == s);
        }

        var joined = JoinPipelines(db, query);
        if (!string.IsNullOrWhiteSpace(batch))
        {
            // Exact match: the board's batch filter is a dropdown of the estate's real batch labels, so a chosen
            // "trans" must not also drag in "trans_item". The label is compared after the pipeline join, since a
            // run whose pipeline left the catalog coalesces to the default batch there.
            var batchFilter = batch.Trim();
            joined = joined.Where(x => x.Batch == batchFilter);
        }

        // The history inbox reads newest-first; the status board (latest=true) reads in report order, batch then
        // lineage step then flow. A group view (groupId set) reads in execution order: by the member's wave then
        // flow name, so the set lists exactly as it runs.
        var ordered = latest == true
            ? joined.OrderBy(x => x.Batch).ThenBy(x => x.Wave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
            : groupId is not null
                ? joined.OrderBy(x => x.Run.GroupWave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId)
                : joined.OrderByDescending(x => x.Run.WrittenUtc).ThenBy(x => x.Run.RunId);
        // The last action is only ever rendered for a group's members (the group page's member table, the CLI's
        // group watch), so it is resolved only when a group is what was asked for: on the open run board that
        // saves two per-row lookups into the event log on every row of every poll.
        var items = await ProjectSummaries(db, ordered.Skip((p - 1) * size).Take(size), groupId is not null)
            .ToListAsync(ct).ConfigureAwait(false);

        // The page is read first because it often proves the total: a first page that came back short IS the whole
        // result, so the count query (which re-runs the filters, the latest-run resolution and the pipeline join)
        // is skipped outright for every view narrow enough to fit one page.
        var total = p == 1 && items.Count < size
            ? items.Count
            : await ordered.LongCountAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSummaryDto>(items, p, size, total));
    }

    /// <summary>The run row paired with its pipeline's batch label and lineage wave: the intermediate shape the
    /// summary projection reads. A named class (not an anonymous type) so the join, the filters, and the final
    /// projection compose across the runs list and the group stream without duplicating the query.</summary>
    private sealed class SummarySource
    {
        public required CatalogRun Run { get; init; }

        public required string Batch { get; init; }

        public int Wave { get; init; }
    }

    /// <summary>The batch label and the lineage wave live on the pipeline row (git/YAML is their source of
    /// truth), so they are joined in at query time rather than denormalized onto every run: one source, always
    /// current, and a run whose pipeline left the estate still lists (left join) under the default batch. A
    /// missing label coalesces to <see cref="CatalogPipeline.DefaultBatch"/> so every run belongs to a batch
    /// group.</summary>
    private static IQueryable<SummarySource> JoinPipelines(CatalogDbContext db, IQueryable<CatalogRun> runs)
        => from run in runs
           join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id into pipelines
           from pipeline in pipelines.DefaultIfEmpty()
           select new SummarySource
           {
               Run = run,
               Batch = pipeline != null && pipeline.Batch != null ? pipeline.Batch : CatalogPipeline.DefaultBatch,
               Wave = pipeline != null ? pipeline.Wave : -1,
           };

    /// <summary>Projects joined run rows to <see cref="RunSummaryDto"/>: the single summary shape the runs list
    /// and the group stream both serve. The per-row subqueries are TOP-1/COUNT seeks on the RunId indexes, and
    /// they run only for the rows of the page the caller asked for (the projection is applied after the paging).
    /// <paramref name="includeLastAction"/> decides whether the newest trace event is resolved: it costs two of
    /// those seeks per row, one of them fetching a message with no length bound, and only a group's member list
    /// shows it, so every other caller passes false and reports it as null.</summary>
    private static IQueryable<RunSummaryDto> ProjectSummaries(
        CatalogDbContext db, IQueryable<SummarySource> source, bool includeLastAction)
        => includeLastAction
            ? source.Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted,
                db.RunFiles.Count(f => f.RunId == x.Run.RunId), x.Run.GroupId,
                db.RunEvents.Where(e => e.RunId == x.Run.RunId)
                    .OrderByDescending(e => e.Id).Select(e => (string?)e.Message).FirstOrDefault(),
                db.RunEvents.Where(e => e.RunId == x.Run.RunId)
                    .OrderByDescending(e => e.Id).Select(e => (DateTime?)e.TimestampUtc).FirstOrDefault(),
                x.Run.Error))
            : source.Select(x => new RunSummaryDto(
                x.Run.RunId, x.Run.PipelineId, x.Run.RepoId, x.Run.FlowName, x.Run.FlowKind, x.Batch, x.Wave,
                x.Run.Status, x.Run.Success,
                x.Run.TargetPool, x.Run.CommitSha, x.Run.WrittenUtc, x.Run.EnqueuedUtc, x.Run.DurationSeconds,
                x.Run.RowsLoaded, x.Run.RowsInserted, x.Run.RowsUpdated, x.Run.RowsDeleted,
                db.RunFiles.Count(f => f.RunId == x.Run.RunId), x.Run.GroupId,
                null, null,
                x.Run.Error));

    /// <summary>A group's member summaries in execution order: the shape both the group view's member list and
    /// the group stream serve. This is the one list that shows the last action, so it resolves it.</summary>
    private static IQueryable<RunSummaryDto> GroupMembersQuery(CatalogDbContext db, Guid groupId)
        => ProjectSummaries(
            db,
            JoinPipelines(db, db.Runs.AsNoTracking().Where(r => r.GroupId == groupId))
                .OrderBy(x => x.Run.GroupWave).ThenBy(x => x.Run.FlowName).ThenBy(x => x.Run.RunId),
            includeLastAction: true);

    private static async Task<Results<Ok<RunDetailDto>, ProblemHttpResult>> GetRunAsync(
        Guid runId, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await (
                from run in db.Runs.AsNoTracking()
                where run.RunId == runId
                join pipeline in db.Pipelines.AsNoTracking() on run.PipelineId equals pipeline.Id into pipelines
                from pipeline in pipelines.DefaultIfEmpty()
                select new RunDetailDto(
                    run.RunId, run.PipelineId, run.RepoId, run.FlowName, run.FlowKind,
                    pipeline != null && pipeline.Batch != null ? pipeline.Batch : CatalogPipeline.DefaultBatch,
                    pipeline != null ? pipeline.Wave : -1,
                    run.Status, run.Success,
                    run.TargetPool, run.CommitSha, run.EnqueuedUtc, run.ClaimedByNode, run.CancelRequestedUtc,
                    run.SchemaVersion, run.WrittenUtc, run.StartUtc, run.EndUtc, run.DurationSeconds,
                    run.RowsLoaded, run.RowsInserted, run.RowsUpdated, run.RowsDeleted,
                    db.RunFiles.Count(f => f.RunId == run.RunId), run.Error, run.Host,
                    run.FullLoad, run.BackfillFrom, run.BackfillTo, run.FilePattern, run.AssertionsOnly,
                    run.ReprocessFromSourceMin, run.SourceFilter,
                    run.IncrementalMode, run.IncrementalFilter, run.IncrementalWatermark, run.IncrementalWatermarkSource,
                    run.DataSetConvention,
                    null, null, null, run.GroupId))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (dto is null)
        {
            return NotFound("run", runId);
        }

        // On a failed run, surface the exact statement that threw alongside the header, so the detail view can show
        // the offending SQL next to the error banner instead of making the operator hunt for it in the Statements
        // list. This is the one statement whose projection carries an Error (there is at most one per run); the
        // lookup is skipped for every non-failed run.
        if (dto.Status == RunStatuses.Failed)
        {
            var failed = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId && s.Error != null)
                .OrderBy(s => s.Ordinal)
                .Select(s => new { s.Ordinal, s.Step, s.Sql })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (failed is not null)
            {
                dto = dto with
                {
                    FailedStatementOrdinal = failed.Ordinal,
                    FailedStatementStep = failed.Step,
                    FailedStatementSql = failed.Sql,
                };
            }
        }

        return TypedResults.Ok(dto);
    }

    private static async Task<Results<Ok<RunScopePreviewDto>, ProblemHttpResult>> PreviewScopeAsync(
        CatalogDbContext db, Guid repoId, string? flowName, string? scope, string? batch, bool? includeAll, CancellationToken ct)
    {
        var parsed = RunScopeExpander.TryParseScope(scope);
        if (parsed is null)
        {
            return TypedResults.Problem(
                detail: "scope must be one of 'flow' or 'node'. To preview what a whole source runs, read its "
                        + "schedule's plan (GET /schedules/{id}/plan): a schedule's members are what a fire runs.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        RunScopeExpansion expansion;
        try
        {
            expansion = await RunScopeExpander.ExpandAsync(
                    db, repoId,
                    string.IsNullOrWhiteSpace(flowName) ? null : flowName.Trim(), parsed.Value, includeAll ?? false, ct)
                .ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(
                detail: ex.Message, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var members = expansion.Members
            .Select(m => new RunScopePreviewMemberDto(m.FlowName, m.FlowKind, m.Wave))
            .ToList();
        var waveCount = members.Select(m => m.Wave).Distinct().Count();
        return TypedResults.Ok(new RunScopePreviewDto(
            parsed.Value.ToString().ToLowerInvariant(), expansion.Anchor, members.Count, waveCount, members));
    }

    private static async Task<Results<Ok<RunGroupDto>, ProblemHttpResult>> GetRunGroupAsync(
        Guid groupId, CatalogDbContext db, CancellationToken ct)
    {
        var group = await db.RunGroups.AsNoTracking()
            .Where(g => g.GroupId == groupId)
            .Select(g => new { g.GroupId, g.RepoId, g.Mode, g.Anchor, g.MemberCount, g.CommitSha, g.EnqueuedUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (group is null)
        {
            return NotFound("run group", groupId);
        }

        // The live rollup: the members' current lifecycle states, counted in one grouped pass.
        var byStatus = await db.Runs.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        int CountOf(string status) => byStatus.FirstOrDefault(x => x.Status == status)?.Count ?? 0;
        var counts = new RunGroupCountsDto(
            byStatus.Sum(x => x.Count),
            CountOf(RunStatuses.Queued), CountOf(RunStatuses.Running), CountOf(RunStatuses.Succeeded),
            CountOf(RunStatuses.Failed), CountOf(RunStatuses.Cancelled), CountOf(RunStatuses.Skipped));

        return TypedResults.Ok(new RunGroupDto(
            group.GroupId, group.RepoId, group.Mode, group.Anchor, group.MemberCount, group.CommitSha,
            group.EnqueuedUtc, counts));
    }

    // Every drill-down endpoint first verifies the run exists and returns 404 when it does not, so an unknown run id
    // is reported (rather than silently returning an empty page that a caller could not distinguish from a real run
    // that processed nothing). This is consistent across all drill-down endpoints below.

    private static async Task<Results<Ok<PagedResult<RunFileDto>>, ProblemHttpResult>> GetRunFilesAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunFiles.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunFileDto(x.Id, x.RunId, x.RepoId, x.Name, x.Path, x.Rows, x.Columns, x.SizeBytes, x.Hash))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunFileDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunAssertionDto>>, ProblemHttpResult>> GetRunAssertionsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunAssertions.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunAssertionDto(
                x.Id, x.RunId, x.RepoId, x.Name, x.Result, x.AssertedValue, x.Evaluated, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunAssertionDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunStatementDto>>, ProblemHttpResult>> GetRunStatementsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        // A run can emit many statements (every generated CREATE/MERGE/DDL), so this list is paged; ordered by the
        // 1-based execution position the sync recorded.
        var ordered = db.RunStatements.AsNoTracking().Where(x => x.RunId == runId)
            .OrderBy(x => x.Ordinal).ThenBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunStatementDto(x.Id, x.RunId, x.RepoId, x.Ordinal, x.TimestampUtc, x.Step, x.Sql, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunStatementDto>(items, p, size, total));
    }

    /// <summary>The run's consolidated trace as one SQL-side query: the UNION of the two live-streamed
    /// drill-down streams (canonical events and generated statements), interleaved by timestamp. Both sides
    /// project to the same anonymous shape (EF's supported form for set operations) so the Concat translates to
    /// a single UNION ALL and ordering, counting, and paging all stay in SQL. Statement rows carry their SQL;
    /// event rows carry their message: the two never overlap, so nothing is duplicated. Legacy statement rows
    /// without a timestamp sort first (MinValue), in ordinal order; on a timestamp tie events sort before
    /// statements ("event" &lt; "statement"), and Id makes the order fully deterministic.</summary>
    private static IQueryable<RunTraceEntryDto> TraceQuery(CatalogDbContext db, Guid runId)
    {
        var events = db.RunEvents.AsNoTracking().Where(x => x.RunId == runId)
            .Select(x => new
            {
                x.Id, x.RunId, x.RepoId, Kind = RunTraceKinds.Event, x.Ordinal,
                TimestampUtc = (DateTime?)x.TimestampUtc, x.Level, x.Step, Message = (string?)x.Message,
                Sql = (string?)null, Error = (string?)null, x.Rows, x.ElapsedMs,
            });
        var statements = db.RunStatements.AsNoTracking().Where(x => x.RunId == runId)
            .Select(x => new
            {
                x.Id, x.RunId, x.RepoId, Kind = RunTraceKinds.Statement, x.Ordinal,
                x.TimestampUtc, Level = "trace", Step = (string?)x.Step, Message = (string?)null,
                Sql = (string?)x.Sql, x.Error, Rows = (long?)null, ElapsedMs = (double?)null,
            });

        return events.Concat(statements)
            .OrderBy(x => x.TimestampUtc ?? DateTime.MinValue)
            .ThenBy(x => x.Ordinal).ThenBy(x => x.Kind).ThenBy(x => x.Id)
            .Select(x => new RunTraceEntryDto(
                x.Id, x.RunId, x.RepoId, x.Kind, x.Ordinal, x.TimestampUtc, x.Level, x.Step, x.Message,
                x.Sql, x.Error, x.Rows, x.ElapsedMs));
    }

    private static async Task<Ok<PagedResult<RunTraceEntryDto>>> PageTraceAsync(
        CatalogDbContext db, Guid runId, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var trace = TraceQuery(db, runId);
        var total = await trace.LongCountAsync(ct).ConfigureAwait(false);
        var items = await trace.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunTraceEntryDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunTraceEntryDto>>, ProblemHttpResult>> GetRunTraceAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        return await PageTraceAsync(db, runId, page, pageSize, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetRunTraceTextAsync(
        Guid runId, CatalogDbContext db, CancellationToken ct)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NotFound("run", runId);
        }

        return await TraceTextAsync(db, run, ct).ConfigureAwait(false);
    }

    /// <summary>The latest run's paged trace for a pipeline, so a caller can go from "this pipeline" straight to
    /// "what its last run did" without resolving a run id first.</summary>
    private static async Task<Results<Ok<PagedResult<RunTraceEntryDto>>, ProblemHttpResult>> GetPipelineTraceAsync(
        Guid pipelineId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var run = await LatestRunAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NoRunForPipeline(pipelineId);
        }

        return await PageTraceAsync(db, run.RunId, page, pageSize, ct).ConfigureAwait(false);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetPipelineTraceTextAsync(
        Guid pipelineId, CatalogDbContext db, CancellationToken ct)
    {
        var run = await LatestRunAsync(db, pipelineId, ct).ConfigureAwait(false);
        if (run is null)
        {
            return NoRunForPipeline(pipelineId);
        }

        return await TraceTextAsync(db, run, ct).ConfigureAwait(false);
    }

    /// <summary>The pipeline's newest run header, by the same ordering the runs list uses (newest WrittenUtc
    /// first); null when the pipeline has no recorded runs (or does not exist, which reads identically).</summary>
    private static Task<CatalogRun?> LatestRunAsync(CatalogDbContext db, Guid pipelineId, CancellationToken ct)
        => db.Runs.AsNoTracking()
            .Where(r => r.PipelineId == pipelineId)
            .OrderByDescending(r => r.WrittenUtc).ThenByDescending(r => r.RunId)
            .FirstOrDefaultAsync(ct);

    private static ProblemHttpResult NoRunForPipeline(Guid pipelineId)
        => TypedResults.Problem(
            detail: $"No run is recorded for pipeline '{pipelineId}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static async Task<ContentHttpResult> TraceTextAsync(CatalogDbContext db, CatalogRun run, CancellationToken ct)
    {
        var entries = await TraceQuery(db, run.RunId).ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Text(RenderTraceText(run, entries), "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Renders a run's consolidated trace as one plain-text document: a header identifying the run (flow, kind,
    /// status, window, rows, error), then one line per entry in timeline order, with generated SQL and
    /// multi-line messages indented under their entry line. This single rendering serves every text consumer:
    /// the GUI's "Copy trace" button, a ticket paste, and an LLM debugging a pipeline from its last run.
    /// </summary>
    private static string RenderTraceText(CatalogRun run, IReadOnlyList<RunTraceEntryDto> entries)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("run ").Append(run.RunId)
          .Append(" flow '").Append(run.FlowName).Append("' (").Append(run.FlowKind).Append(')')
          .Append(" status ").Append(run.Status);
        if (run.StartUtc is { } start)
        {
            sb.Append(" started ").Append(start.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append('Z');
        }

        if (run.DurationSeconds is { } duration)
        {
            sb.Append(" duration ").Append(duration.ToString("0.###", CultureInfo.InvariantCulture)).Append('s');
        }

        if (run.RowsLoaded is { } rows)
        {
            sb.Append(" rows ").Append(rows.ToString(CultureInfo.InvariantCulture));
        }

        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(run.Error))
        {
            sb.Append("error: ").AppendLine(run.Error.ReplaceLineEndings(" ").Trim());
        }

        foreach (var entry in entries)
        {
            var stamp = entry.TimestampUtc is { } ts
                ? ts.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z"
                : new string('-', 24);
            var label = entry.Kind == RunTraceKinds.Statement ? "sql" : entry.Level;
            sb.Append(stamp).Append(' ')
              .Append(label.ToUpperInvariant().PadRight(7)).Append(' ')
              .Append((entry.Step ?? "-").PadRight(24)).Append(' ');

            var body = entry.Kind == RunTraceKinds.Statement
                ? $"statement {entry.Ordinal}"
                : entry.Message ?? string.Empty;
            var lines = body.ReplaceLineEndings("\n").Split('\n');
            sb.AppendLine(lines[0]);
            foreach (var line in lines.Skip(1))
            {
                sb.Append("    | ").AppendLine(line);
            }

            if (entry.Sql is { } sql)
            {
                foreach (var line in sql.ReplaceLineEndings("\n").TrimEnd().Split('\n'))
                {
                    sb.Append("    ").AppendLine(line);
                }
            }

            if (entry.Error is { } entryError)
            {
                sb.Append("    !! error: ").AppendLine(entryError.ReplaceLineEndings(" ").Trim());
            }
        }

        return sb.ToString();
    }

    // The server-side tail cadence of the live stream: short enough that an entry reaches the browser well under
    // a second after the node persisted it, long enough that one open run view costs a handful of cheap indexed
    // reads per second. The heartbeat keeps idle proxies from reaping the connection during a long quiet stage.
    private static readonly TimeSpan StreamTailInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan StreamHeartbeatInterval = TimeSpan.FromSeconds(15);

    /// <summary>
    /// The live run trace as Server-Sent Events: one <c>entry</c> event per new trace entry (the same shape as
    /// <see cref="RunTraceEntryDto"/>) as the executing node streams it into the catalog, then a single
    /// <c>end</c> event carrying the terminal status once the run completes, after which the paged endpoint
    /// holds the authoritative re-projected trace and the client should refetch it. The
    /// <paramref name="afterEventId"/>/<paramref name="afterStatementId"/> cursors resume a dropped connection
    /// without replaying entries the client already holds. The stream tails the same catalog rows the paged
    /// endpoint reads, so it works identically for the in-process worker and a self-hosted node.
    /// </summary>
    private static async Task<IResult> StreamRunTraceAsync(
        Guid runId, CatalogDbContext db, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json,
        long? afterEventId, long? afterStatementId, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tell buffering reverse proxies (nginx) to pass frames through as they are written.
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        var lastEventId = afterEventId ?? 0L;
        var lastStatementId = afterStatementId ?? 0L;
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The live rows are an immutable, append-only log: the node writes each event/statement once under a
                // monotonically increasing id and never rewrites them (completion only appends any missing tail). So
                // this tail forwards each delta exactly once, keyed on the client's id cursor, and the client renders
                // what it receives with no de-duplication. Read status first: a terminal run ends the stream here,
                // and the client then loads the authoritative paged trace, which includes any final rows a tick did
                // not reach before the run completed.
                var status = await db.Runs.AsNoTracking()
                    .Where(r => r.RunId == runId).Select(r => r.Status)
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                var terminal = status is null || (status != RunStatuses.Queued && status != RunStatuses.Running);

                if (!terminal)
                {
                    var newEvents = await db.RunEvents.AsNoTracking()
                        .Where(x => x.RunId == runId && x.Id > lastEventId).OrderBy(x => x.Id)
                        .Select(x => new RunTraceEntryDto(
                            x.Id, x.RunId, x.RepoId, RunTraceKinds.Event, x.Ordinal, x.TimestampUtc, x.Level,
                            x.Step, x.Message, null, null, x.Rows, x.ElapsedMs))
                        .ToListAsync(ct).ConfigureAwait(false);
                    var newStatements = await db.RunStatements.AsNoTracking()
                        .Where(x => x.RunId == runId && x.Id > lastStatementId).OrderBy(x => x.Id)
                        .Select(x => new RunTraceEntryDto(
                            x.Id, x.RunId, x.RepoId, RunTraceKinds.Statement, x.Ordinal, x.TimestampUtc, "trace",
                            x.Step, null, x.Sql, x.Error, null, null))
                        .ToListAsync(ct).ConfigureAwait(false);

                    if (newEvents.Count > 0)
                    {
                        lastEventId = newEvents[^1].Id;
                    }

                    if (newStatements.Count > 0)
                    {
                        lastStatementId = newStatements[^1].Id;
                    }

                    // The same interleave order as the paged endpoint, applied to this tick's delta.
                    var batch = newEvents.Concat(newStatements)
                        .OrderBy(x => x.TimestampUtc ?? DateTime.MinValue)
                        .ThenBy(x => x.Ordinal).ThenBy(x => x.Kind).ThenBy(x => x.Id);
                    foreach (var entry in batch)
                    {
                        await WriteSseAsync(response, "entry", JsonSerializer.Serialize(entry, serializer), ct)
                            .ConfigureAwait(false);
                        lastHeartbeat = DateTime.UtcNow;
                    }
                }
                else
                {
                    // Terminal (or the run row vanished, which only a manual catalog cleanup can cause): tell the
                    // client to swap to the authoritative paged trace and stop.
                    await WriteSseAsync(
                        response, "end",
                        JsonSerializer.Serialize(new RunTraceStreamEndDto(status ?? "unknown"), serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= StreamHeartbeatInterval)
                {
                    await response.WriteAsync(": hb\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(StreamTailInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away (tab closed, navigation): the normal way a live stream ends mid-run.
        }

        return TypedResults.Empty;
    }

    private static async Task WriteSseAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The live run group as Server-Sent Events: one <c>member</c> event (a <see cref="RunSummaryDto"/>) for
    /// every member whose summary changed since the previous tick (status transitions, the newest trace event
    /// as the "last action", durations and row counts), a full snapshot on connect, then a single <c>end</c>
    /// event carrying the final <see cref="RunGroupCountsDto"/> rollup once every member is terminal. The batch
    /// overview feeds its member table from this, so each row shows what its flow is doing the moment it does
    /// it. Reconnecting simply replays the current snapshot: the diffing state is per connection.
    /// </summary>
    private static async Task<IResult> StreamRunGroupAsync(
        Guid groupId, CatalogDbContext db, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, CancellationToken ct)
    {
        if (!await db.RunGroups.AsNoTracking().AnyAsync(g => g.GroupId == groupId, ct).ConfigureAwait(false))
        {
            return NotFound("run group", groupId);
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        // Per-member fingerprint of the last frame sent: the serialized summary itself, so change detection and
        // emission share one serialization and any changed field (status, last action, rows, timing) republishes.
        var sent = new Dictionary<Guid, string>();
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var members = await GroupMembersQuery(db, groupId).ToListAsync(ct).ConfigureAwait(false);
                foreach (var member in members)
                {
                    var payload = JsonSerializer.Serialize(member, serializer);
                    if (!sent.TryGetValue(member.RunId, out var previous) || previous != payload)
                    {
                        sent[member.RunId] = payload;
                        await WriteSseAsync(response, "member", payload, ct).ConfigureAwait(false);
                        lastHeartbeat = DateTime.UtcNow;
                    }
                }

                // Terminal once every member has left the queue and the worker: the completion projections have
                // landed by then (each member's status flips in the same transaction as its re-projection), so
                // the last member frames sent above already carry the final summaries. An empty member list can
                // only mean a manual catalog cleanup mid-stream; it ends the stream the same way.
                if (members.Count == 0 || members.All(m => RunStatuses.IsTerminal(m.Status)))
                {
                    int CountOf(string status) => members.Count(m => m.Status == status);
                    var counts = new RunGroupCountsDto(
                        members.Count,
                        CountOf(RunStatuses.Queued), CountOf(RunStatuses.Running), CountOf(RunStatuses.Succeeded),
                        CountOf(RunStatuses.Failed), CountOf(RunStatuses.Cancelled), CountOf(RunStatuses.Skipped));
                    await WriteSseAsync(response, "end", JsonSerializer.Serialize(counts, serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= StreamHeartbeatInterval)
                {
                    await response.WriteAsync(": hb\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(StreamTailInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away (tab closed, navigation): the normal way a live stream ends mid-group.
        }

        return TypedResults.Empty;
    }

    private static async Task<Results<Ok<PagedResult<RunSurrogateKeyDto>>, ProblemHttpResult>> GetRunSurrogateKeysAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunSurrogateKeys.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunSurrogateKeyDto(
                x.Id, x.RunId, x.RepoId, x.SurrogateKeyId, x.SurrogateTable, x.SurrogateColumn,
                x.IsRemote, x.KeysGenerated, x.RowsStamped, x.Executed, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunSurrogateKeyDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<PagedResult<RunHealthCheckMetricDto>>, ProblemHttpResult>> GetRunHealthMetricsAsync(
        Guid runId, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RunExistsAsync(db, runId, ct).ConfigureAwait(false))
        {
            return NotFound("run", runId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RunHealthCheckMetrics.AsNoTracking().Where(x => x.RunId == runId).OrderBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new RunHealthCheckMetricDto(
                x.Id, x.RunId, x.RepoId, x.Name, x.SeriesPoints, x.ImputedPoints, x.ImmaturePoints,
                x.Anomalies, x.LevelShifts, x.ModelTrained, x.ModelTrainer, x.Error))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RunHealthCheckMetricDto>(items, p, size, total));
    }

    /// <summary>Normalizes a run-window bound to the UTC wall-clock value the WrittenUtc column stores: a local
    /// value (how query-string binding materializes an ISO instant with a 'Z' suffix) is converted, an already-UTC
    /// value is kept, and an unspecified-kind value is taken to be UTC (every timestamp in the API is UTC by
    /// contract).</summary>
    private static DateTime ToUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };

    private static Task<bool> RunExistsAsync(CatalogDbContext db, Guid runId, CancellationToken ct)
        => db.Runs.AsNoTracking().AnyAsync(x => x.RunId == runId, ct);

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
}
