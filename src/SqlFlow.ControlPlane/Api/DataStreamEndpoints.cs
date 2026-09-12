using System.Linq.Expressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Catalog;
using SqlFlow.Core.Model;
using SqlFlow.HealthCheck;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One detector's verdict on a stream: whether it fired, its confidence in [0, 1], which way the
/// deviation went (<c>below</c> / <c>above</c> / <c>none</c>), whether it may raise a finding on its own, and
/// the evidence sentence. Detectors that stayed quiet are reported too, so a healthy verdict is auditable
/// rather than merely asserted.</summary>
public sealed record StreamSignalDto(
    string Detector, bool Fired, double Score, string Direction, bool Primary, string Detail);

/// <summary>A recurring delivery on top of the ordinary rhythm: the vendor who ships a bigger refill every
/// fortnight, the month-end settlement file. <c>periodDays</c> is the cycle length (0 when it repeats on a
/// position in the calendar month, which <c>monthly</c> marks); <c>cycleRows</c> against <c>ordinaryRows</c>
/// is how much bigger it is; <c>nextExpectedUtc</c> is the day to check.</summary>
public sealed record StreamCycleDto(
    int PeriodDays, bool Monthly, int Occurrences, bool Heavier, double CycleRows, double OrdinaryRows,
    double Lift, DateTime? LastOccurrenceUtc, DateTime? NextExpectedUtc, string Description);

/// <summary>What one table's traffic normally looks like, learned from its own history after reprocessing was
/// excluded. <c>shape</c> is <c>daily</c>, <c>weekdays</c>, <c>weekly</c>, <c>several-days-a-week</c>,
/// <c>fortnightly</c>, <c>monthly</c>, <c>periodic</c>, or <c>sporadic</c>; <c>loadDays</c> names the weekdays
/// it reliably loads on;
/// <c>reliability</c> is the share of expected days it actually delivered on; <c>cycle</c> is the recurring
/// larger (or smaller) delivery on top of that rhythm, null for a stream that has none.</summary>
public sealed record StreamPatternDto(
    string Shape, IReadOnlyList<string> LoadDays, double TypicalRows, double LowRows, double HighRows,
    double Reliability,
    // True for a table that writes only when its SOURCE changes rather than on every run: a reference or
    // dimension table read every morning that changes a handful of times a year. Nothing about such a stream
    // may be judged against its schedule's firing interval, because the cron says how often we ask, not how
    // often the answer differs.
    bool ChangeDriven,
    StreamCycleDto? Cycle, string Description);

/// <summary>A stream's measured normal: how much it writes, how often, and where it is trending. These are the
/// averages an operator checks a verdict against, and what every detector is calibrated on.</summary>
public sealed record StreamProfileDto(
    StreamPatternDto Pattern,
    string Cadence, double ExpectedGapDays, string CadenceSource, double MaxObservedGapDays,
    DateTime? LastLoadUtc, DateTime? LastRunUtc, double? DaysSinceLastLoad, double? DaysSinceLastRun,
    int RunDays, int LoadedDays, long Runs, long Failures,
    long TotalRowsInserted, long TotalRowsUpdated, long TotalRowsDeleted,
    double AvgRowsInsertedPerRun, double AvgRowsUpdatedPerRun, double AvgRowsDeletedPerRun,
    double AvgRowsWrittenPerLoadedDay, double MedianRowsWrittenPerLoadedDay, double TrendRowsPerDay,
    // Of the mature days the flow ran and at least one run succeeded, the share that actually wrote rows: the
    // evidence behind ChangeDriven, and the answer to "does running this flow produce data".
    double DeliveryShare,
    // The days the stream was expected to load on, so a client can state the missed count as "3 of 30".
    int ExpectedDays, int UnexpectedNullDays, int EmptyRunDays, int NoRunDays, double PredictedNullDays,
    int TrimmedLoadDays, double TrimFence);

/// <summary>
/// The stream's last two weeks, compact enough to ride on every board row: one number per day for what
/// arrived and what was expected, plus the positions of the days the analysis flagged and the expected days
/// that wrote nothing. A row on the board can then SHOW "less data than usual" as a shape instead of asking
/// the reader to trust a sentence, and it costs a few hundred bytes rather than the full scored series.
/// </summary>
/// <param name="FromUtc">The day the first entry describes, or null when the stream has no analysed day.</param>
/// <param name="Rows">Rows written per day, oldest first.</param>
/// <param name="Expected">The trend-and-weekday expectation per day, aligned with <paramref name="Rows"/>.</param>
/// <param name="FlaggedDays">Zero-based positions of the days the analysis flagged as anomalous.</param>
/// <param name="MissedDays">Zero-based positions of the days the stream was expected to load on and did not.</param>
/// <param name="ImmatureDays">How many trailing entries may still be receiving data and were never judged.</param>
public sealed record StreamSparklineDto(
    DateTime? FromUtc, IReadOnlyList<long> Rows, IReadOnlyList<double> Expected,
    IReadOnlyList<int> FlaggedDays, IReadOnlyList<int> MissedDays, int ImmatureDays);

/// <summary>One analysed day: what arrived, what was expected, and how the point was judged.</summary>
public sealed record StreamPointDto(
    DateTime Date, long RowsWritten, long RowsInserted, long RowsUpdated, long RowsDeleted,
    int Runs, int Failures, int ExcludedBackfillRuns,
    double Expected, double Severity, bool Anomaly, string? Reason, bool Imputed, bool Immature,
    double ExpectedLoadRate, bool UnexpectedNull, bool Trimmed);

/// <summary>
/// One data stream: a flow, the table it writes, and the ensemble's verdict on whether data is still arriving
/// the way it should. <see cref="Series"/> is null on the board (a hundred streams times sixty days is a
/// payload nobody reads) and populated on the single-stream endpoint, which is what the chart draws; the
/// board carries the two-week <see cref="Sparkline"/> instead, which is what a row draws.
/// </summary>
public sealed record DataStreamDto(
    Guid PipelineId, string FlowName, string FlowKind, string? Batch, bool Active, string? TargetObject,
    // The data SOURCE this stream belongs to (Cyclehire, Fara, Boatbooking), from the repository layout or
    // schedule membership rather than from the flow's name. One source routinely has fifty objects, so
    // without it a board is a flat wall of tables with no way to see that forty rows are one vendor. Not the
    // same thing as Batch, which groups the flows of one DATASET inside a source.
    string Source,
    // "source" when this stream brings data in from outside the estate (a vendor delivery), "internal" when
    // it derives one of our tables from another, and how that was decided: "lineage" (every flow upstream of
    // it is an ingestion flow, or the first derivation was found), "origin" (it reads nothing this estate
    // produces), "schema" (the target schema is configured as one side or the other), or "kind".
    string Scope, string ScopeReason,
    // Where in the pipeline this stream sits, which is the sharper form of the same question: "integration"
    // fetches from the vendor (a failure here is usually THEIRS), "file-ingestion" lands what was fetched and
    // "archive" loads it into the silver layer (failures there are ours, on the vendor's data), and "derived"
    // is everything built from the archive onwards.
    string Stage,
    string? ScheduleName, string? Cron, string? Timezone,
    string Status, string Category, string Severity, double Confidence, int AgreeingDetectors, string Summary,
    StreamProfileDto Profile, IReadOnlyList<StreamSignalDto> Signals, StreamSparklineDto Sparkline,
    IReadOnlyList<StreamPointDto>? Series);

/// <summary>
/// The data-stream board: every monitored stream with its verdict, ranked most urgent first, plus the counts
/// that say how much of the estate is in each state. The counts cover every ANALYSED stream, so a truncated
/// list is visibly truncated.
/// </summary>
public sealed record DataStreamsDto(
    int WindowDays, DateTime FromUtc, DateTime AsOfUtc, bool IncludeBackfills,
    // Which side was analysed: "source", "internal", or "all".
    string Scope,
    // How many streams exist on each side, whichever side was analysed, so a client can offer both without a
    // second request.
    int SourceStreams, int InternalStreams,
    // Streams left out because they join no enabled schedule. Reported rather than silently dropped, so the
    // board says how much of the estate it decided was not part of the delivery contract.
    int UnscheduledStreams, bool IncludeUnscheduled,
    int TotalStreams, int AnalyzedStreams, long ExcludedBackfillRuns,
    int StalledCount, int DegradedCount, int WatchCount, int HealthyCount, int InsufficientHistoryCount,
    IReadOnlyList<DataStreamDto> Streams);

/// <summary>
/// DataStream anomaly detection. Three questions about every loaded table, ranked because they are not
/// equally urgent: <b>zero data</b> (an outage, and the only one that can be critical), <b>less data than
/// normal</b> (a degradation, capped at warning), and <b>more data than normal</b> (information).
///
/// <para>
/// The estate already records how many rows every run inserted, updated, and deleted. That is a heartbeat for
/// every table the platform writes, collected with no configuration at all, which is what makes this the
/// complement of the <c>hc</c> flow rather than a competitor to it. A health-check flow reads the monitored
/// table itself along a declared date column and sees the shape of the data; it costs a flow per table, so in
/// practice a few tables get one. This surface covers every stream the moment it has run once, and hands the
/// deep instrument the shortlist worth pointing at.
/// </para>
///
/// <para>
/// Reprocessing is removed in two passes, and everything above depends on it. This endpoint drops the runs
/// the log FLAGS as backfills (<c>includeBackfills</c> keeps them), and the detector then trims the outsized
/// days the flags missed: a catch-up after an outage, a re-run kicked off without backfill parameters, a
/// source that delivered a year in one file. Either kind left in redefines the stream's normal and leaves
/// every ordinary day afterwards reading as a collapse, which is what makes a volume test worthless. And
/// where a stream declares a schedule, the cadence it is held to comes from the CRON rather than its own
/// recent behaviour, because a stream broken for three weeks otherwise teaches the detector that three-week
/// gaps are what it does.
/// </para>
///
/// The analysis itself is <see cref="StreamAnomalyDetector"/>, which shares its estimators with the
/// health-check engine so the two surfaces cannot disagree about what abnormal means.
/// </summary>
public static class DataStreamEndpoints
{
    /// <summary>The widest window the board accepts. Past this the per-stream change-point search stops being
    /// an interactive read, and the older history is a different regime anyway.</summary>
    public const int MaxWindowDays = 180;

    /// <summary>The default window: long enough for a weekly stream to show a cadence and for the rate test to
    /// have a baseline behind its recent slice.</summary>
    public const int DefaultWindowDays = 60;

    /// <summary>Streams analysed at most in one request, newest activity first. Ranking requires analysing
    /// every candidate, so this is the real bound on the work a single call can do; the response reports both
    /// numbers, so a capped sweep is visible rather than silently partial.</summary>
    public const int MaxStreams = 1000;

    public const int MaxResults = 500;

    /// <summary>
    /// How many windows of history before the analysed one are read to judge whether a stream that loaded
    /// nothing inside the window is dead or merely static. Three is enough for the question (did running this
    /// flow ever produce data) and bounds a query that would otherwise walk a stream's whole life.
    /// </summary>
    private const int PriorHistoryWindows = 3;

    /// <summary>The days a board row's sparkline covers. Two weeks is long enough for a weekly stream to show
    /// two loads and for a shortfall to read as a shape, and short enough that a row stays a row.</summary>
    public const int SparklineDays = 14;

    /// <summary>Vendor deliveries: streams that bring data in from outside the estate. The default scope,
    /// because "has the vendor delivered" is the question with an owner outside this building, and because a
    /// single upstream going quiet would otherwise light up its whole downstream chain as separate findings.</summary>
    public const string SourceScope = "source";

    /// <summary>Our own processing: streams that derive one of our tables from another.</summary>
    public const string InternalScope = "internal";

    public const string AllScopes = "all";

    /// <summary>
    /// The pipeline stages, in the order data moves through them. The scope split answers "whose data is
    /// this"; the stage answers the more useful "where did it stop", and the two are not the same question.
    /// A vendor delivery that fails at <c>integration</c> means nothing arrived from them; the same delivery
    /// failing at <c>file-ingestion</c> or <c>archive</c> means it arrived and WE did not take it in. Both are
    /// source-side, and sending them to the same person would be wrong.
    /// </summary>
    private static string StageOf(string? flowKind, string scope) => scope == InternalScope
        ? "derived"
        : flowKind switch
        {
            "api" or "sftp" or "cpy" => "integration",
            "file" => "file-ingestion",
            _ => "archive",
        };

    public static RouteGroupBuilder MapDataStreamEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var streams = group.MapGroup("/datastreams").WithTags("DataStreams");
        streams.MapGet(string.Empty, GetDataStreamsAsync).WithName("GetDataStreams");
        streams.MapGet("/{pipelineId:guid}", GetDataStreamAsync).WithName("GetDataStream");

        return group;
    }

    /// <summary>
    /// The board. Analyses every stream that ran in the window and returns them ranked most urgent first.
    /// </summary>
    /// <param name="db">The shadow catalog.</param>
    /// <param name="clock">The clock the window and drought arithmetic are anchored at.</param>
    /// <param name="options">Deployment configuration, for how this estate splits source from internal.</param>
    /// <param name="days">The analysis window (1 to <see cref="MaxWindowDays"/>).</param>
    /// <param name="repoId">Restrict to one repository.</param>
    /// <param name="batch">Restrict to one batch (data source).</param>
    /// <param name="status">Restrict to one verdict: <c>stalled</c>, <c>degraded</c>, <c>watch</c>,
    /// <c>healthy</c>, or <c>insufficient-history</c>.</param>
    /// <param name="scope">Which side to analyse: <c>source</c> (vendor deliveries, the default),
    /// <c>internal</c> (our own processing), or <c>all</c>. The two answer different questions and have
    /// different owners, so they are separate boards by default rather than one mixed list.</param>
    /// <param name="includeBackfills">Count backfills and other operator-driven reprocessing as normal
    /// traffic. False by default, which is what keeps a history replay from redefining a stream's normal.</param>
    /// <param name="includeUnscheduled">Analyse streams that join no ENABLED schedule too. False by default:
    /// a flow nothing schedules has no say in whether data is delivered, so holding it to a delivery
    /// expectation invents an incident out of a flow that was never promised to run.</param>
    /// <param name="limit">How many streams to return (the counts still cover every analysed stream).</param>
    /// <param name="ct">Cancellation.</param>
    private static async Task<Results<Ok<DataStreamsDto>, ProblemHttpResult>> GetDataStreamsAsync(
        CatalogDbContext db, TimeProvider clock, IOptions<ControlPlaneOptions> options, int? days, Guid? repoId,
        string? batch, string? status, string? scope, bool? includeBackfills, bool? includeUnscheduled,
        int? limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(days, limit) is { } problem)
        {
            return problem;
        }

        var requested = NormalizeScope(scope);
        if (requested is null)
        {
            return TypedResults.Problem(
                detail: $"scope must be '{SourceScope}', '{InternalScope}', or '{AllScopes}'.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var report = await ComputeAsync(
            db, clock, options.Value.DataStreams, days ?? DefaultWindowDays, repoId, batch,
            includeBackfills == true, includeUnscheduled == true, requested, pipelineId: null, ct)
            .ConfigureAwait(false);

        var filtered = string.IsNullOrWhiteSpace(status)
            ? report.Streams
            : report.Streams.Where(s => s.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        return TypedResults.Ok(report with { Streams = filtered.Take(limit ?? 100).ToList() });
    }

    /// <summary>One stream in full, including the day-by-day series the chart draws and every detector's
    /// reasoning. Same analysis as the board, so the detail can never disagree with the row that led to it.</summary>
    private static async Task<Results<Ok<DataStreamDto>, NotFound, ProblemHttpResult>> GetDataStreamAsync(
        Guid pipelineId, CatalogDbContext db, TimeProvider clock, IOptions<ControlPlaneOptions> options,
        int? days, bool? includeBackfills, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (Validate(days, limit: null) is { } problem)
        {
            return problem;
        }

        // Always every scope here: a caller asking for one stream by id has already chosen it, and refusing to
        // show it because it sits on the other side of the split would be obstruction, not filtering.
        var report = await ComputeAsync(
            db, clock, options.Value.DataStreams, days ?? DefaultWindowDays, repoId: null, batch: null,
            includeBackfills == true, includeUnscheduled: true, AllScopes, pipelineId, ct)
            .ConfigureAwait(false);

        var stream = report.Streams.FirstOrDefault(s => s.PipelineId == pipelineId);
        return stream is null ? TypedResults.NotFound() : TypedResults.Ok(stream);
    }

    /// <summary>
    /// The shared computation both endpoints read, in four passes over the catalog and one analysis per
    /// stream: the per-day write statistics of the runs that count, the per-day count of the backfill runs
    /// that do not, the schedule each stream is held to, and the pipeline metadata. The target table is
    /// resolved last, for the returned page only, because a lineage read for a thousand streams to display a
    /// hundred is work nobody asked for.
    /// </summary>
    private static async Task<DataStreamsDto> ComputeAsync(
        CatalogDbContext db, TimeProvider clock, DataStreamOptions classification, int windowDays, Guid? repoId,
        string? batch, bool includeBackfills, bool includeUnscheduled, string scope, Guid? pipelineId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var fromUtc = now.Date.AddDays(-(windowDays - 1));

        var window = db.Runs.AsNoTracking()
            .Where(r => r.WrittenUtc >= fromUtc
                && (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed)
                && (repoId == null || r.RepoId == repoId)
                && (pipelineId == null || r.PipelineId == pipelineId));

        // Ordinary traffic only, unless the caller asked otherwise: see Reprocessing for what that excludes.
        var counted = includeBackfills ? window : window.Where(NotReprocessing);

        var activity = await counted
            .GroupBy(r => new { r.PipelineId, Day = r.WrittenUtc.Date })
            .Select(g => new
            {
                g.Key.PipelineId,
                g.Key.Day,
                Runs = g.Count(),
                Failures = g.Count(r => r.Status == RunStatuses.Failed),
                // A run that reports no insert/update/delete breakdown still reports what it loaded; attributing
                // that to inserts keeps the stream's total volume honest for the flow kinds (file, export, copy)
                // whose results carry only a row count.
                Inserted = g.Sum(r => r.RowsInserted != null || r.RowsUpdated != null || r.RowsDeleted != null
                    ? r.RowsInserted ?? 0
                    : r.RowsLoaded ?? 0),
                Updated = g.Sum(r => r.RowsUpdated ?? 0),
                Deleted = g.Sum(r => r.RowsDeleted ?? 0),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var excluded = includeBackfills
            ? []
            : await window.Where(Reprocessing)
                .GroupBy(r => new { r.PipelineId, Day = r.WrittenUtc.Date })
                .Select(g => new ExcludedDay(g.Key.PipelineId, g.Key.Day, g.Count()))
                .ToListAsync(ct).ConfigureAwait(false);
        var excludedByStream = excluded
            .GroupBy(e => e.PipelineId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(e => e.Day.Date, e => e.Runs));

        var schedules = await LoadSchedulesAsync(db, now, repoId, ct).ConfigureAwait(false);

        var candidateIds = activity.Select(a => a.PipelineId)
            .Concat(excludedByStream.Keys)
            .Distinct()
            .ToList();
        var meta = await db.Pipelines.AsNoTracking()
            .Where(p => candidateIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Kind, p.Batch, p.Active, p.RelativePath })
            .ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);

        // The written object and its SCHEMA, for every candidate. Resolved before the analysis rather than
        // after the ranking, because which side of the source/internal split a stream falls on decides whether
        // it is analysed at all.
        var graph = await LoadScopeGraphAsync(db, repoId, ct).ConfigureAwait(false);
        var classifier = new ScopeClassifier(
            graph,
            meta.ToDictionary(m => m.Key, m => m.Value.Kind),
            new HashSet<string>(classification.LandingSchemas, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(classification.DownstreamSchemas, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(classification.SourceFlowKinds, StringComparer.OrdinalIgnoreCase));
        var scopeById = candidateIds.ToDictionary(id => id, classifier.Classify);

        var batchFilter = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim();
        var byStream = activity.GroupBy(a => a.PipelineId).ToDictionary(g => g.Key, g => g.ToList());

        // A stream that loaded NOTHING inside the window may simply be new, or may have been dead longer than
        // we looked. Those are opposite findings, and the window cannot tell them apart, so the last load
        // before it is read from the full history. Scoped to the streams that need it, which is normally a
        // handful, rather than scanning the run table for every stream on the board.
        var silentIds = candidateIds.Where(id => !byStream.ContainsKey(id)
            || byStream[id].All(r => r.Inserted + r.Updated + r.Deleted == 0)).ToList();
        var lastLoadBeforeWindow = silentIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking()
                .Where(r => silentIds.Contains(r.PipelineId)
                    && r.WrittenUtc < fromUtc
                    && r.Status == RunStatuses.Succeeded
                    && ((r.RowsInserted ?? 0) + (r.RowsUpdated ?? 0) + (r.RowsDeleted ?? 0) > 0
                        || (r.RowsInserted == null && r.RowsUpdated == null && r.RowsDeleted == null
                            && (r.RowsLoaded ?? 0) > 0)))
                .GroupBy(r => r.PipelineId)
                .Select(g => new { PipelineId = g.Key, LastLoadUtc = g.Max(r => r.WrittenUtc) })
                .ToDictionaryAsync(x => x.PipelineId, x => x.LastLoadUtc, ct).ConfigureAwait(false);

        // For those same streams: how often running them USED to produce data. A window with no loads in it
        // cannot tell a table that died from a table that simply does not change, because both run, succeed,
        // and write nothing. The history before the window can: a stream that delivered on nearly every run
        // and has delivered on none since is an outage, while one that delivered on two runs in three hundred
        // is a reference table whose quiet is its normal.
        var priorFrom = fromUtc.AddDays(-PriorHistoryWindows * windowDays);
        var priorDelivery = silentIds.Count == 0
            ? []
            : await db.Runs.AsNoTracking()
                .Where(r => silentIds.Contains(r.PipelineId)
                    && r.WrittenUtc >= priorFrom
                    && r.WrittenUtc < fromUtc
                    && r.Status == RunStatuses.Succeeded)
                .GroupBy(r => r.PipelineId)
                .Select(g => new PriorDelivery(
                    g.Key,
                    g.Count(),
                    g.Count(r => (r.RowsInserted ?? 0) + (r.RowsUpdated ?? 0) + (r.RowsDeleted ?? 0) > 0
                        || (r.RowsInserted == null && r.RowsUpdated == null && r.RowsDeleted == null
                            && (r.RowsLoaded ?? 0) > 0))))
                .ToDictionaryAsync(x => x.PipelineId, ct).ConfigureAwait(false);

        // The candidates, newest activity first, so a capped sweep keeps the streams whose state is current.
        var ordered = candidateIds
            .Where(id => batchFilter is null
                || string.Equals(meta.GetValueOrDefault(id)?.Batch, batchFilter, StringComparison.OrdinalIgnoreCase))
            .Where(id => scope == AllScopes || scopeById[id].Scope == scope)
            // A flow nothing schedules has no say in whether data is delivered: it runs when somebody runs it,
            // so measuring it against a delivery cadence would manufacture findings about a promise nobody
            // made. Schedule membership is what defines the population this board is about.
            .Where(id => includeUnscheduled || schedules.ContainsKey(id))
            .OrderByDescending(id => byStream.TryGetValue(id, out var rows) ? rows.Max(r => r.Day) : DateTime.MinValue)
            .ToList();
        var analyzed = ordered.Take(MaxStreams).ToList();

        var results = new List<DataStreamDto>(analyzed.Count);
        foreach (var id in analyzed)
        {
            ct.ThrowIfCancellationRequested();

            var excludedDays = excludedByStream.GetValueOrDefault(id) ?? [];
            var rows = byStream.GetValueOrDefault(id) ?? [];
            var days = rows.Select(r => r.Day.Date)
                .Concat(excludedDays.Keys)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            var buckets = days.Select(day =>
            {
                var row = rows.FirstOrDefault(r => r.Day.Date == day);
                return new StreamBucket
                {
                    Date = day,
                    Runs = row?.Runs ?? 0,
                    Failures = row?.Failures ?? 0,
                    ExcludedBackfillRuns = excludedDays.GetValueOrDefault(day),
                    RowsInserted = row?.Inserted ?? 0,
                    RowsUpdated = row?.Updated ?? 0,
                    RowsDeleted = row?.Deleted ?? 0,
                };
            }).ToList();

            var schedule = schedules.GetValueOrDefault(id);
            var analysis = StreamAnomalyDetector.Analyze(
                buckets, fromUtc, now,
                new StreamAnomalyOptions
                {
                    ExpectedGapDaysOverride = schedule?.ExpectedGapDays,
                    LastKnownLoadUtc = lastLoadBeforeWindow.TryGetValue(id, out var seen) ? seen : null,
                    PriorSuccessfulRuns = priorDelivery.GetValueOrDefault(id)?.Runs ?? 0,
                    PriorLoadingRuns = priorDelivery.GetValueOrDefault(id)?.LoadingRuns ?? 0,
                });

            var pipeline = meta.GetValueOrDefault(id);
            results.Add(new DataStreamDto(
                id, pipeline?.Name ?? id.ToString(), pipeline?.Kind ?? "?", pipeline?.Batch,
                pipeline?.Active ?? false, graph.Targets.GetValueOrDefault(id).Name,
                SourceOf(pipeline?.RelativePath, schedule?.Name, pipeline?.Name ?? string.Empty),
                scopeById[id].Scope, scopeById[id].Reason, StageOf(pipeline?.Kind, scopeById[id].Scope),
                schedule?.Name, schedule?.Cron, schedule?.Timezone,
                StatusName(analysis.Status), analysis.Category, analysis.Severity, analysis.Confidence,
                analysis.AgreeingDetectors, analysis.Summary,
                ToDto(analysis.Profile),
                analysis.Signals.Select(s => new StreamSignalDto(
                    DetectorName(s.Detector), s.Fired, s.Score, DirectionName(s.Direction), s.Primary,
                    s.Detail)).ToList(),
                ToSparkline(analysis.Series),
                pipelineId is null ? null : analysis.Series.Select(ToDto).ToList()));
        }

        var ranked = results
            .OrderBy(s => SeverityRank(s.Severity))
            .ThenBy(s => StatusRank(s.Status))
            .ThenBy(s => CategoryRank(s.Category))
            .ThenByDescending(s => s.Confidence)
            .ThenBy(s => s.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Scoped to the streams that were analysed, so the number squares with the board rather than counting
        // reprocessing on streams a filter removed.
        var analyzedSet = analyzed.ToHashSet();
        return new DataStreamsDto(
            windowDays, fromUtc, now, includeBackfills,
            scope,
            candidateIds.Count(id => scopeById[id].Scope == SourceScope && (includeUnscheduled || schedules.ContainsKey(id))),
            candidateIds.Count(id => scopeById[id].Scope == InternalScope && (includeUnscheduled || schedules.ContainsKey(id))),
            candidateIds.Count(id => !schedules.ContainsKey(id)),
            includeUnscheduled,
            ordered.Count, ranked.Count,
            excluded.Where(e => analyzedSet.Contains(e.PipelineId)).Sum(e => (long)e.Runs),
            ranked.Count(s => s.Status == "stalled"),
            ranked.Count(s => s.Status == "degraded"),
            ranked.Count(s => s.Status == "watch"),
            ranked.Count(s => s.Status == "healthy"),
            ranked.Count(s => s.Status == "insufficient-history"),
            ranked);
    }

    /// <summary>How often running one stream produced data over the history BEFORE the analysed window: the
    /// evidence that separates a dead feed from a table nobody ever changes.</summary>
    private sealed record PriorDelivery(Guid PipelineId, int Runs, int LoadingRuns);

    /// <summary>Backfill runs on one day of one stream, counted but never analysed.</summary>
    private sealed record ExcludedDay(Guid PipelineId, DateTime Day, int Runs);

    /// <summary>
    /// The run shapes that are reprocessing rather than the stream's ordinary traffic: a forced full load, a
    /// bounded backfill window, an ad-hoc source predicate or file pattern, a reprocess from the source
    /// minimum, an assertions-only evaluation (which loads nothing by design), or an engine-derived scope of
    /// backfill / init-load. Every one is already flagged on the run row, so excluding them is a fact read
    /// from the log rather than a guess from the row counts.
    /// </summary>
    private static readonly Expression<Func<CatalogRun, bool>> Reprocessing = run =>
        run.FullLoad
            || run.BackfillFrom != null
            || run.BackfillTo != null
            || run.SourceFilter != null
            || run.FilePattern != null
            || run.ReprocessFromSourceMin
            || run.AssertionsOnly
            || run.IncrementalMode == IncrementalModes.Backfill
            || run.IncrementalMode == IncrementalModes.InitLoad;

    /// <summary>The exact complement of <see cref="Reprocessing"/>, negated from the same expression tree so
    /// the counted and excluded queries cannot drift into double-counting a run or dropping one.</summary>
    private static readonly Expression<Func<CatalogRun, bool>> NotReprocessing =
        Expression.Lambda<Func<CatalogRun, bool>>(
            Expression.Not(Reprocessing.Body), Reprocessing.Parameters);

    /// <summary>The declared cadence each stream is held to. A flow on several enabled schedules is held to the
    /// most frequent of them (the shortest gap), because that is the one whose miss is noticeable first. Gaps
    /// are computed once per distinct timing rather than once per member, since a source's whole wave usually
    /// shares one schedule.</summary>
    private static async Task<Dictionary<Guid, ScheduleCadence>> LoadSchedulesAsync(
        CatalogDbContext db, DateTime now, Guid? repoId, CancellationToken ct)
    {
        var members = await db.ScheduleMembers.AsNoTracking()
            .Join(db.Schedules.AsNoTracking(), m => m.ScheduleId, s => s.Id, (m, s) => new
            {
                m.PipelineId, s.Name, s.Cron, s.IntervalSeconds, s.Timezone, s.Enabled, s.RepoId,
            })
            .Where(x => x.Enabled && (repoId == null || x.RepoId == repoId))
            .ToListAsync(ct).ConfigureAwait(false);

        var gapCache = new Dictionary<(string? Cron, int? Interval, string Timezone), double?>();
        var byPipeline = new Dictionary<Guid, ScheduleCadence>();
        foreach (var member in members)
        {
            var key = (member.Cron, member.IntervalSeconds, member.Timezone);
            if (!gapCache.TryGetValue(key, out var gap))
            {
                gap = ScheduleClock.ExpectedGapDays(member.Cron, member.IntervalSeconds, member.Timezone, now);
                gapCache[key] = gap;
            }

            // A CHAINED schedule has no cadence of its own: it fires behind its parents, so the clock cannot
            // compute a gap for it. That is a missing cadence, NOT a missing membership, and conflating the
            // two would drop every downstream flow of a chained wave off a board that filters on being
            // scheduled. The membership is recorded either way; the detector simply infers the cadence from
            // the stream's own history when none is declared.
            var cadence = new ScheduleCadence(member.Name, member.Cron, member.Timezone, gap);
            if (!byPipeline.TryGetValue(member.PipelineId, out var existing))
            {
                byPipeline[member.PipelineId] = cadence;
                continue;
            }

            // A flow on several schedules is held to the most frequent of them, because that is the one whose
            // miss is noticeable first. A computable cadence always beats none.
            var better = (existing.ExpectedGapDays, gap) switch
            {
                (null, not null) => true,
                (not null, not null) when gap < existing.ExpectedGapDays => true,
                _ => false,
            };
            if (better)
            {
                byPipeline[member.PipelineId] = cadence;
            }
        }

        return byPipeline;
    }

    /// <summary>
    /// The lineage a stream is classified from: what each pipeline writes (with the schema, for the override),
    /// what each pipeline reads, and which pipelines produce each object. Two queries over the repo's edges,
    /// joined to the object registry for the write side.
    /// </summary>
    private sealed record ScopeGraph(
        Dictionary<Guid, (string? Name, string? Schema)> Targets,
        Dictionary<Guid, List<string>> Reads,
        Dictionary<string, List<Guid>> Producers);

    /// <summary>
    /// The target's QUALIFIED name. The bare object name is not an identity: one dataset routinely exists as a
    /// staging copy and an archive copy that share it, so a board showing only the name presents two different
    /// tables as the same one, and a finding about the staging hop then reads as a finding about the table
    /// everything downstream depends on. The database is included because the two halves of a chain commonly
    /// live in different databases as well as different schemas.
    /// </summary>
    private static string Qualify(string? database, string? schema, string name)
    {
        var qualified = string.IsNullOrWhiteSpace(schema) ? name : $"{schema}.{name}";
        return string.IsNullOrWhiteSpace(database) ? qualified : $"{database}.{qualified}";
    }

    private static async Task<ScopeGraph> LoadScopeGraphAsync(
        CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        var writes = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId != null && e.Relation == "Writes" && (repoId == null || e.RepoId == repoId))
            .Join(db.Objects.AsNoTracking(), e => e.ObjectKey, o => o.Key, (e, o) => new
            {
                PipelineId = e.PipelineId!.Value, e.ObjectKey, e.ObjectName, e.Tier,
                o.Database, o.Schema, ObjectRealName = o.Name,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var reads = await db.LineageEdges.AsNoTracking()
            .Where(e => e.PipelineId != null && e.Relation == "Reads" && (repoId == null || e.RepoId == repoId))
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.ObjectKey })
            .ToListAsync(ct).ConfigureAwait(false);

        // A flow can write several objects (a staging table and its target). The DECLARED edge is the flow's
        // own target; anything else is derived from a module body, so it is used only when nothing declared
        // exists.
        var targets = writes
            .GroupBy(w => w.PipelineId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var best = g.FirstOrDefault(w => w.Tier == "Declared") ?? g.First();
                    return ((string?)Qualify(best.Database, best.Schema, best.ObjectRealName ?? best.ObjectName),
                        best.Schema);
                });

        return new ScopeGraph(
            targets,
            reads.GroupBy(r => r.PipelineId)
                .ToDictionary(g => g.Key, g => g.Select(r => r.ObjectKey).Distinct().ToList()),
            writes.GroupBy(w => w.ObjectKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Select(w => w.PipelineId).Distinct().ToList(),
                    StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Decides which side of the split each stream is on, and says why.
    ///
    /// <para>
    /// The rule that matters is the LINEAGE one, because it needs no naming convention. Data is a vendor
    /// delivery for as long as it is still a copy of what the vendor sent, and it stops being one the moment
    /// something of ours derives from it. Walking upstream from a stream through the flow graph, that is
    /// exactly the point where a flow appears that is not an ingestion flow: an acquisition fetches a file, a
    /// file flow lands it, an ingestion flow loads it into the archive, and every one of those is still moving
    /// the vendor's data. A stored procedure building a fact table from that archive is the first step that is
    /// ours. So a stream is a vendor delivery when every flow on the path from the external origin down to it
    /// is an ingestion-shaped flow, and it is internal from the first derivation onwards. The archive layer
    /// falls out of that as the last source-side stop, without anyone having to name it.
    /// </para>
    ///
    /// <para>
    /// The schema lists stay as an OVERRIDE, checked first, because an estate that has already made this
    /// decision in its naming should not have it re-derived and possibly contradicted. Where lineage has not
    /// been computed there is no graph to walk, and the flow kind alone decides.
    /// </para>
    /// </summary>
    private sealed class ScopeClassifier
    {
        private readonly ScopeGraph _graph;
        private readonly IReadOnlyDictionary<Guid, string> _kinds;
        private readonly IReadOnlySet<string> _landing;
        private readonly IReadOnlySet<string> _downstream;
        private readonly IReadOnlySet<string> _sourceKinds;
        private readonly Dictionary<Guid, (string Scope, string Reason)> _memo = [];

        /// <summary>Pipelines currently being resolved, so a cyclic lineage (a flow whose upstream reaches
        /// itself) terminates instead of recursing forever. A cycle cannot be an external origin, so it
        /// resolves as internal.</summary>
        private readonly HashSet<Guid> _visiting = [];

        public ScopeClassifier(
            ScopeGraph graph, IReadOnlyDictionary<Guid, string> kinds, IReadOnlySet<string> landing,
            IReadOnlySet<string> downstream, IReadOnlySet<string> sourceKinds)
        {
            _graph = graph;
            _kinds = kinds;
            _landing = landing;
            _downstream = downstream;
            _sourceKinds = sourceKinds;
        }

        public (string Scope, string Reason) Classify(Guid pipelineId)
        {
            if (_memo.TryGetValue(pipelineId, out var cached))
            {
                return cached;
            }

            if (!_visiting.Add(pipelineId))
            {
                return (InternalScope, "cycle");
            }

            var result = Resolve(pipelineId);
            _visiting.Remove(pipelineId);
            _memo[pipelineId] = result;
            return result;
        }

        private (string Scope, string Reason) Resolve(Guid pipelineId)
        {
            var schema = _graph.Targets.GetValueOrDefault(pipelineId).Schema;
            if (schema is not null && _downstream.Contains(schema))
            {
                return (InternalScope, "schema");
            }

            if (schema is not null && _landing.Contains(schema))
            {
                return (SourceScope, "schema");
            }

            var kind = _kinds.GetValueOrDefault(pipelineId);
            var isIngestionShaped = kind is not null && _sourceKinds.Contains(kind);
            if (!isIngestionShaped)
            {
                // The first derivation. Everything downstream of here is ours by construction.
                return (InternalScope, "kind");
            }

            // An ingestion-shaped flow is still only carrying the vendor's data if everything upstream of it
            // is too. An object nothing produces is the external origin itself, which is the base case.
            var reads = _graph.Reads.GetValueOrDefault(pipelineId);
            if (reads is null || reads.Count == 0)
            {
                return (SourceScope, "origin");
            }

            var sawProducer = false;
            foreach (var objectKey in reads)
            {
                if (_graph.Producers.GetValueOrDefault(objectKey) is not { Count: > 0 } producers)
                {
                    continue; // external origin: a file, an endpoint, a table nothing here writes
                }

                foreach (var producer in producers.Where(producer => producer != pipelineId))
                {
                    sawProducer = true;
                    if (Classify(producer).Scope == InternalScope)
                    {
                        return (InternalScope, "lineage");
                    }
                }
            }

            return (SourceScope, sawProducer ? "lineage" : "origin");
        }
    }

    /// <summary>
    /// The data source a stream belongs to, taken from the strongest STRUCTURAL signal available rather than
    /// from what the flow is called.
    ///
    /// <para>
    /// Naming is the wrong basis for this. A flow whose name does not follow the estate's convention, or that
    /// was renamed, or that belongs to a source whose prefix differs from its folder, would land in a group of
    /// its own and quietly break the overview the grouping exists to give. The repository LAYOUT is a fact
    /// about where someone deliberately filed the flow, and SCHEDULE MEMBERSHIP is a fact about what fires
    /// together; both survive any amount of misnaming.
    /// </para>
    ///
    /// <para>
    /// So: the folder the document lives in first, because an estate organises a vendor's flows under one
    /// directory. Then the schedule it joins, because one schedule fires one source's whole wave, which makes
    /// it the operational grouping even for a flow sitting loose at the repository root. Only with neither
    /// does the name convention get a turn, and then the flow name itself, so a stream always lands somewhere
    /// rather than in an unnamed bucket.
    /// </para>
    /// </summary>
    private static string SourceOf(string? relativePath, string? scheduleName, string flowName)
    {
        if (FolderOf(relativePath) is { Length: > 0 } folder)
        {
            return folder;
        }

        if (!string.IsNullOrWhiteSpace(scheduleName))
        {
            return scheduleName;
        }

        var underscore = flowName.IndexOf('_', StringComparison.Ordinal);
        return underscore > 0 ? flowName[..underscore] : flowName;
    }

    /// <summary>The immediate parent directory of a repo-relative flow path, or null when the document sits at
    /// the repository root.</summary>
    private static string? FolderOf(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        var lastSlash = normalized.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return null;
        }

        var folder = normalized[..lastSlash];
        var parent = folder.LastIndexOf('/');
        return parent >= 0 ? folder[(parent + 1)..] : folder;
    }

    /// <summary>The requested scope, or null when it is not one this endpoint knows.</summary>
    private static string? NormalizeScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return SourceScope;
        }

        var trimmed = scope.Trim();
        return trimmed.Equals(SourceScope, StringComparison.OrdinalIgnoreCase) ? SourceScope
            : trimmed.Equals(InternalScope, StringComparison.OrdinalIgnoreCase) ? InternalScope
            : trimmed.Equals(AllScopes, StringComparison.OrdinalIgnoreCase) ? AllScopes
            : null;
    }

    /// <summary>One stream's declared cadence: which schedule holds it, and how many days that schedule
    /// normally leaves between fires.</summary>
    /// <summary>One stream's enabled schedule membership: which schedule holds it, and how many days that
    /// schedule normally leaves between fires when it has a cadence of its own at all (a chained schedule
    /// does not).</summary>
    private sealed record ScheduleCadence(string Name, string? Cron, string Timezone, double? ExpectedGapDays);

    private static StreamCycleDto? ToDto(StreamCycle? cycle) => cycle is null
        ? null
        : new StreamCycleDto(
            cycle.PeriodDays, cycle.Monthly, cycle.Occurrences, cycle.Heavier, cycle.CycleRows,
            cycle.OrdinaryRows, cycle.Lift, cycle.LastOccurrenceUtc, cycle.NextExpectedUtc, cycle.Description);

    private static StreamProfileDto ToDto(StreamProfile profile) => new(
        new StreamPatternDto(
            profile.Pattern.Shape,
            profile.Pattern.LoadDays.Select(d => d.ToString()).ToList(),
            profile.Pattern.TypicalRows, profile.Pattern.LowRows, profile.Pattern.HighRows,
            profile.Pattern.Reliability, profile.Pattern.ChangeDriven, ToDto(profile.Pattern.Cycle),
            profile.Pattern.Description),
        profile.Cadence.ToString().ToLowerInvariant(), profile.ExpectedGapDays, profile.CadenceSource,
        profile.MaxObservedGapDays, profile.LastLoadUtc, profile.LastRunUtc,
        Finite(profile.DaysSinceLastLoad), Finite(profile.DaysSinceLastRun),
        profile.RunDays, profile.LoadedDays, profile.Runs, profile.Failures,
        profile.TotalRowsInserted, profile.TotalRowsUpdated, profile.TotalRowsDeleted,
        profile.AvgRowsInsertedPerRun, profile.AvgRowsUpdatedPerRun, profile.AvgRowsDeletedPerRun,
        profile.AvgRowsWrittenPerLoadedDay, profile.MedianRowsWrittenPerLoadedDay, profile.TrendRowsPerDay,
        profile.DeliveryShare, profile.ExpectedDays, profile.UnexpectedNullDays, profile.EmptyRunDays, profile.NoRunDays,
        profile.PredictedNullDays,
        profile.TrimmedLoadDays, profile.TrimFence);

    /// <summary>The trailing <see cref="SparklineDays"/> of the scored series, as parallel arrays. The series
    /// is one entry per calendar day of the window, so the tail is the most recent fortnight; a stream with
    /// fewer analysed days than that (one dead before the window, whose series is only the runs it made) ships
    /// what it has, and an empty series ships empty arrays rather than a null a client has to special-case.</summary>
    private static StreamSparklineDto ToSparkline(IReadOnlyList<StreamPoint> series)
    {
        var recent = series.Count <= SparklineDays ? series : series.Skip(series.Count - SparklineDays).ToList();
        var indexed = recent.Select((point, index) => (point, index)).ToList();
        return new StreamSparklineDto(
            recent.Count > 0 ? recent[0].Date : null,
            recent.Select(p => p.RowsWritten).ToList(),
            recent.Select(p => Math.Round(p.Expected, 1)).ToList(),
            indexed.Where(x => x.point.Anomaly).Select(x => x.index).ToList(),
            indexed.Where(x => x.point.UnexpectedNull).Select(x => x.index).ToList(),
            recent.Count(p => p.Immature));
    }

    private static StreamPointDto ToDto(StreamPoint point) => new(
        point.Date, point.RowsWritten, point.RowsInserted, point.RowsUpdated, point.RowsDeleted,
        point.Runs, point.Failures, point.ExcludedBackfillRuns, point.Expected, point.Severity,
        point.Anomaly, point.Reason, point.Imputed, point.Immature,
        point.ExpectedLoadRate, point.UnexpectedNull, point.Trimmed);

    /// <summary>"Never" is infinity to the detector and null over the wire: JSON has no infinity, and a client
    /// that reads one as a number would render a stream that has never loaded as loading moments ago.</summary>
    private static double? Finite(double value) => double.IsFinite(value) ? Math.Round(value, 2) : null;

    private static string StatusName(StreamStatus status) => status switch
    {
        StreamStatus.Stalled => "stalled",
        StreamStatus.Degraded => "degraded",
        StreamStatus.Watch => "watch",
        StreamStatus.Healthy => "healthy",
        _ => "insufficient-history",
    };

    private static string DetectorName(StreamDetector detector) => detector switch
    {
        StreamDetector.Silence => "silence",
        StreamDetector.NullDays => "nullDays",
        StreamDetector.Cadence => "cadence",
        StreamDetector.VolumeOutlier => "volumeOutlier",
        StreamDetector.LevelShift => "levelShift",
        _ => "rateChange",
    };

    private static string DirectionName(StreamDirection direction) => direction switch
    {
        StreamDirection.Below => "below",
        StreamDirection.Above => "above",
        _ => "none",
    };

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2,
    };

    /// <summary>Within one severity, a stopped stream outranks a wobbling one, and anything analysed outranks
    /// a stream there was not enough history to judge.</summary>
    private static int StatusRank(string status) => status switch
    {
        "stalled" => 0,
        "degraded" => 1,
        "watch" => 2,
        "healthy" => 3,
        _ => 4,
    };

    /// <summary>Within one severity and status, the three questions in the order an operator must answer
    /// them: no data at all, then less than normal, then more than normal.</summary>
    private static int CategoryRank(string category) => category switch
    {
        "stalled" => 0,
        "failing" => 1,
        "gap-days" => 2,
        "not-running" => 3,
        "less-than-normal" => 4,
        "more-than-normal" => 5,
        _ => 6,
    };

    private static ProblemHttpResult? Validate(int? days, int? limit)
    {
        if (days is < 1 or > MaxWindowDays)
        {
            return TypedResults.Problem(
                detail: $"days must be between 1 and {MaxWindowDays}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (limit is < 1 or > MaxResults)
        {
            return TypedResults.Problem(
                detail: $"limit must be between 1 and {MaxResults}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        return null;
    }
}
