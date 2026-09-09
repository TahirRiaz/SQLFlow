using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Compute;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One flow's performance over the window: run/failure counts, duration aggregates, throughput, the
/// newest run's outcome, and the duration trend against the equally-sized previous window. Durations average
/// over SUCCEEDED runs only (a failed run's short duration says nothing about the flow's real cost), while the
/// totals and failure stats cover every terminal run.</summary>
public sealed record FlowInsightDto(
    Guid PipelineId, string FlowName, string FlowKind, string? Batch, bool Active,
    long Runs, long Failures, double FailureRate,
    double? AvgDurationSeconds, double? MaxDurationSeconds, double TotalDurationSeconds,
    long RowsLoaded, double? RowsPerSecond,
    DateTime LastRunUtc, string LastStatus, string? LastError,
    double? PrevAvgDurationSeconds, long? PrevRowsLoaded, double? DurationTrendPercent);

/// <summary>The flow-performance rollup: estate totals for the window plus the per-flow table, ordered by total
/// processing time (the flows "where the time goes" first).</summary>
public sealed record FlowInsightsDto(
    int WindowDays, DateTime FromUtc, DateTime AsOfUtc,
    long TotalRuns, long TotalFailures, double TotalDurationSeconds, long TotalRowsLoaded,
    IReadOnlyList<FlowInsightDto> Flows);

/// <summary>One advisory on the attention list: a severity (critical / warning / info), a machine-usable
/// category, and a human sentence with the evidence numbers inline. Exactly one item exists per flow (a flow
/// tripping several rules keeps its most urgent finding, the rest folded into an "Also:" note). A collapsed
/// item aggregates a batch whose flows tripped the same rule together: its pipeline identity is null,
/// <see cref="FlowCount"/> says how many flows it covers, and the flow names ride in the detail.</summary>
public sealed record AttentionItemDto(
    string Severity, string Category, Guid? PipelineId, string? FlowName, string? Batch, int FlowCount,
    string Title, string Detail);

/// <summary>The "what needs attention" rollup: every advisory the window's run history supports, ordered most
/// severe first. Empty means the estate ran clean over the window.</summary>
public sealed record AttentionDto(
    int WindowDays, DateTime AsOfUtc, int TotalItems, int CriticalCount, int WarningCount, int InfoCount,
    IReadOnlyList<AttentionItemDto> Items);

/// <summary>One actionable recommendation: what to do, why (with the evidence numbers inline), and where it
/// came from. <see cref="Source"/> is "runHistory" for advisories computed from the catalog's run telemetry and
/// "warehouseDmv" for advisories read from the newest warehouse-health probe results. <see cref="SuggestedSql"/>
/// carries a ready-to-review statement (CREATE INDEX, UPDATE STATISTICS, DROP INDEX) when one exists AND the
/// caller asked for SQL (<c>includeSql=true</c>); the default answer stays compact for context-limited clients
/// (an MCP agent), with <see cref="HasSuggestedSql"/> saying a statement exists to fetch. It is a suggestion
/// for human review, never something a client should execute unreviewed. Flow-scoped items carry the pipeline
/// identity; warehouse-scoped items carry the datasource reference and database instead.</summary>
public sealed record RecommendationDto(
    string Severity, string Category, string Source, string Title, string Detail,
    string? SuggestedSql, bool HasSuggestedSql, Guid? PipelineId, string? FlowName, string? Reference,
    string? Database);

/// <summary>The freshness of one warehouse-health probe feeding the recommendations: which operation ran, when,
/// and against what. A client (the GUI's refresh, or an MCP agent) re-runs stale probes through
/// <c>POST /api/v1/datasources/tasks</c>.</summary>
public sealed record WarehouseProbeStatusDto(
    string Operation, Guid TaskId, string Reference, string? Database, DateTime? CompletedUtc);

/// <summary>The one-call "what should we focus on" answer: run-history advisories and warehouse DMV advisories
/// merged into a single ranked list, most severe first, TRUNCATED to the requested limit so the answer stays
/// readable in a chat context. The severity counts cover every advisory found (not just the page), so a
/// truncated list is visibly truncated: <c>totalItems</c> versus <c>items.length</c> says how much was cut.
/// <see cref="WarehouseProbes"/> reports which DMV probes the list is built from (empty means none has ever
/// run, so only run-history items appear and a client should trigger the <c>missingIndexes</c> /
/// <c>statisticsHealth</c> / <c>indexUsage</c> / <c>topQueries</c> compute operations to light up the
/// warehouse dimension).</summary>
public sealed record RecommendationsDto(
    int WindowDays, DateTime AsOfUtc, int TotalItems, int CriticalCount, int WarningCount, int InfoCount,
    IReadOnlyList<RecommendationDto> Items, IReadOnlyList<WarehouseProbeStatusDto> WarehouseProbes);

/// <summary>One engine step's cost across the window's runs of a flow, with a sample of the SQL the step
/// executed in the newest traced run (so the hotspot points at reviewable code, not just a label).</summary>
public sealed record StepInsightDto(
    string Step, long Occurrences, double AvgElapsedMs, double MaxElapsedMs, double TotalElapsedMs,
    long RowsProcessed, string? SampleSql);

/// <summary>A flow's step-level hotspot breakdown, ordered by total elapsed time.</summary>
public sealed record StepInsightsDto(
    Guid PipelineId, string FlowName, int WindowDays, DateTime FromUtc, Guid? SampleRunId,
    IReadOnlyList<StepInsightDto> Steps);

/// <summary>
/// The insights read surface: catalog-side performance analytics computed from the run history the estate
/// already collects (<c>CatalogRun</c> durations and row counts, <c>CatalogRunEvent</c> per-step timings,
/// <c>CatalogRunStatement</c> SQL). Three endpoints: <c>/insights/flows</c> is the per-flow performance table,
/// <c>/insights/attention</c> distills it into a ranked advisory list ("where the warehouse is bleeding"), and
/// <c>/insights/pipelines/{id}/steps</c> drills one flow down to its hot steps and their SQL. Read scope,
/// computed at read time; the live-DMV counterpart (missing indexes, statistics) rides the compute-task rail
/// under <c>/datasources/tasks</c> because only a worker may open a datasource connection.
/// </summary>
public static class InsightsEndpoints
{
    /// <summary>The widest window the aggregates accept. Beyond this the run table scan stops being an
    /// interactive read, and retention typically prunes older trace rows anyway.</summary>
    public const int MaxWindowDays = 90;

    public const int MaxFlows = 500;

    /// <summary>Advisory text carries at most this much of a run error; the full error lives on the run.</summary>
    private const int MaxErrorChars = 240;

    /// <summary>Sample SQL per step is truncated to this length; the full text lives on the run statements.</summary>
    private const int MaxSampleSqlChars = 2000;

    public static RouteGroupBuilder MapInsightsEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var insights = group.MapGroup("/insights").WithTags("Insights");
        insights.MapGet("/flows", GetFlowInsightsAsync).WithName("GetFlowInsights");
        insights.MapGet("/attention", GetAttentionAsync).WithName("GetAttention");
        insights.MapGet("/recommendations", GetRecommendationsAsync).WithName("GetRecommendations");
        insights.MapGet("/pipelines/{pipelineId:guid}/steps", GetStepInsightsAsync).WithName("GetStepInsights");

        // The original group flows back so the read surface's fluent registration chain keeps composing.
        return group;
    }

    private static async Task<Results<Ok<FlowInsightsDto>, ProblemHttpResult>> GetFlowInsightsAsync(
        CatalogDbContext db, TimeProvider clock, int? days, Guid? repoId, string? batch, int? limit,
        CancellationToken ct)
    {
        if (Validate(days, limit) is { } problem)
        {
            return problem;
        }

        var report = await ComputeFlowInsightsAsync(db, clock, days ?? 7, repoId, batch, limit ?? 100, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(report);
    }

    private static async Task<Results<Ok<AttentionDto>, ProblemHttpResult>> GetAttentionAsync(
        CatalogDbContext db, TimeProvider clock, int? days, Guid? repoId, string? batch, int? limit,
        CancellationToken ct)
    {
        if (Validate(days, limit) is { } problem)
        {
            return problem;
        }

        var windowDays = days ?? 7;
        var (asOfUtc, items) = await ComputeAttentionAsync(db, clock, windowDays, repoId, batch, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(new AttentionDto(
            windowDays, asOfUtc, items.Count,
            items.Count(i => i.Severity == "critical"),
            items.Count(i => i.Severity == "warning"),
            items.Count(i => i.Severity == "info"),
            items.Take(limit ?? 50).ToList()));
    }

    /// <summary>
    /// The one-call action list an operator or an MCP agent starts from: the run-history advisories merged with
    /// the newest warehouse DMV probe results. Compact by default: the list caps at <c>limit</c> (severity
    /// counts cover everything found, so truncation is visible) and SQL suggestions travel only when
    /// <c>includeSql=true</c>, so a context-limited client reads a briefing, not a dump. The DMV dimension
    /// reads the newest SUCCEEDED compute task per (operation, datasource); it never opens a datasource
    /// connection itself, so a client wanting fresher DMV data triggers the probes through
    /// <c>POST /api/v1/datasources/tasks</c> and re-reads.
    /// </summary>
    private static async Task<Results<Ok<RecommendationsDto>, ProblemHttpResult>> GetRecommendationsAsync(
        CatalogDbContext db, TimeProvider clock, int? days, Guid? repoId, string? batch, int? limit,
        bool? includeSql, CancellationToken ct)
    {
        if (Validate(days, limit) is { } problem)
        {
            return problem;
        }

        var windowDays = days ?? 7;
        var (asOfUtc, attention) = await ComputeAttentionAsync(db, clock, windowDays, repoId, batch, ct)
            .ConfigureAwait(false);
        var items = attention
            .Select(a => new RecommendationDto(
                a.Severity, a.Category, "runHistory", a.Title, a.Detail,
                SuggestedSql: null, HasSuggestedSql: false, a.PipelineId, a.FlowName,
                Reference: null, Database: null))
            .ToList();

        var (probeItems, probes) = await DistillWarehouseProbesAsync(db, ct).ConfigureAwait(false);
        items.AddRange(probeItems);

        var ordered = items
            .OrderBy(i => SeverityRank(i.Severity))
            .ThenBy(i => i.Source, StringComparer.Ordinal) // runHistory before warehouseDmv within a severity
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var page = ordered
            .Take(limit ?? 20)
            .Select(i => includeSql == true ? i : i with { SuggestedSql = null })
            .ToList();
        return TypedResults.Ok(new RecommendationsDto(
            windowDays, asOfUtc, ordered.Count,
            ordered.Count(i => i.Severity == "critical"),
            ordered.Count(i => i.Severity == "warning"),
            ordered.Count(i => i.Severity == "info"),
            page, probes));
    }

    /// <summary>One advisory candidate before dedupe/collapse: the rank orders categories by urgency, the
    /// impact orders within a category, and the short label is what the candidate contributes when it folds
    /// into another item's "Also:" note.</summary>
    private sealed record AttentionCandidate(
        int Rank, double Impact, string Severity, string Category, Guid PipelineId, string FlowName,
        string? Batch, string Title, string Detail, string ShortLabel);

    /// <summary>Categories where many flows of one source trip together (an upstream went quiet, a schedule
    /// stopped firing): three or more same-category items in one batch collapse to a single advisory.</summary>
    private static readonly string[] CollapsibleCategories = ["zero-rows", "silent"];

    private const int CollapseThreshold = 3;

    /// <summary>
    /// Builds the attention list with three noise controls, in order. (1) Signal quality: zero-rows fires only
    /// for a feed that WENT quiet (loaded rows in the previous window, none in this one); an incremental flow
    /// with simply no new data, or a staged flow that never loaded, is not an advisory. (2) One item per flow:
    /// a flow tripping several rules keeps its most urgent item, with the others folded into an "Also:" note,
    /// so the same flow never occupies multiple slots. (3) Batch collapse: three or more same-category items
    /// in one batch (a source whose flows went quiet together) merge into a single advisory naming the batch
    /// and its flows. The result is ordered most-urgent-first and UNCAPPED; the endpoints cap it and report
    /// the full counts.
    /// </summary>
    private static async Task<(DateTime AsOfUtc, List<AttentionItemDto> Items)> ComputeAttentionAsync(
        CatalogDbContext db, TimeProvider clock, int windowDays, Guid? repoId, string? batch, CancellationToken ct)
    {
        var report = await ComputeFlowInsightsAsync(db, clock, windowDays, repoId, batch, MaxFlows, ct)
            .ConfigureAwait(false);
        var candidates = new List<AttentionCandidate>();

        foreach (var flow in report.Flows)
        {
            if (flow.Failures >= 2 && flow.FailureRate >= 0.5)
            {
                candidates.Add(new AttentionCandidate(
                    0, flow.Failures, "critical", "failing", flow.PipelineId, flow.FlowName, flow.Batch,
                    $"{flow.FlowName} fails repeatedly",
                    $"{flow.Failures} of {flow.Runs} runs failed in the last {windowDays}d." +
                    (flow.LastError is null ? string.Empty : $" Last error: {flow.LastError}"),
                    "fails repeatedly"));
            }
            else if (flow.LastStatus == RunStatuses.Failed)
            {
                candidates.Add(new AttentionCandidate(
                    1, flow.TotalDurationSeconds, "warning", "last-run-failed", flow.PipelineId, flow.FlowName,
                    flow.Batch,
                    $"{flow.FlowName}: last run failed",
                    $"Failed at {flow.LastRunUtc:yyyy-MM-dd HH:mm} UTC." +
                    (flow.LastError is null ? string.Empty : $" Error: {flow.LastError}"),
                    "last run failed"));
            }

            if (flow is { DurationTrendPercent: >= 50, AvgDurationSeconds: >= 10, PrevAvgDurationSeconds: not null }
                && flow.Runs >= 2)
            {
                candidates.Add(new AttentionCandidate(
                    1, flow.DurationTrendPercent.Value, "warning", "degrading", flow.PipelineId, flow.FlowName,
                    flow.Batch,
                    $"{flow.FlowName} is getting slower",
                    $"Average duration +{flow.DurationTrendPercent:0}% vs the previous {windowDays}d " +
                    $"({FormatSeconds(flow.PrevAvgDurationSeconds.Value)} to {FormatSeconds(flow.AvgDurationSeconds!.Value)}).",
                    $"getting slower (+{flow.DurationTrendPercent:0}%)"));
            }

            // A feed that WENT quiet: rows in the previous window, none in this one. A flow that never loads
            // rows (incremental with no new data, or a staged endpoint) is normal and stays off the list.
            if (flow is { FlowKind: "ing", Failures: 0, Runs: >= 1, RowsLoaded: 0, PrevRowsLoaded: > 0 })
            {
                candidates.Add(new AttentionCandidate(
                    2, flow.PrevRowsLoaded ?? 0, "warning", "zero-rows", flow.PipelineId, flow.FlowName, flow.Batch,
                    $"{flow.FlowName} went quiet",
                    $"0 rows in {flow.Runs} succeeded run(s) this window; the previous {windowDays}d loaded " +
                    $"{flow.PrevRowsLoaded:#,0} rows. The upstream may have stopped producing.",
                    "went quiet (0 rows)"));
            }

            if (report.TotalDurationSeconds > 0 && flow.TotalDurationSeconds >= 600
                && flow.TotalDurationSeconds / report.TotalDurationSeconds >= 0.25)
            {
                candidates.Add(new AttentionCandidate(
                    3, flow.TotalDurationSeconds, "info", "time-hog", flow.PipelineId, flow.FlowName, flow.Batch,
                    $"{flow.FlowName} dominates processing time",
                    $"{flow.TotalDurationSeconds / report.TotalDurationSeconds * 100:0}% of all processing time " +
                    $"({FormatSeconds(flow.TotalDurationSeconds)}); the highest-leverage flow to optimize.",
                    "dominates processing time"));
            }
        }

        // Active flows that ran before the window but not inside it: a schedule that stopped firing, a paused
        // source, or a worker that cannot claim them. Reported as info; an intentionally idle flow reads the same.
        var fromUtc = report.FromUtc;
        var silent = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active && (repoId == null || p.RepoId == repoId))
            .Where(p => !db.Runs.Any(r => r.PipelineId == p.Id && r.WrittenUtc >= fromUtc))
            .Select(p => new
            {
                p.Id, p.Name, p.Batch,
                LastRunUtc = db.Runs.Where(r => r.PipelineId == p.Id).Max(r => (DateTime?)r.WrittenUtc),
            })
            .Where(p => p.LastRunUtc != null)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var pipeline in silent)
        {
            if (!string.IsNullOrWhiteSpace(batch)
                && !string.Equals(pipeline.Batch, batch.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidates.Add(new AttentionCandidate(
                4, 0, "info", "silent", pipeline.Id, pipeline.Name, pipeline.Batch,
                $"{pipeline.Name} is active but not running",
                $"No run in the last {windowDays}d; last ran {pipeline.LastRunUtc:yyyy-MM-dd HH:mm} UTC.",
                "not running"));
        }

        // One item per flow: keep the most urgent candidate, fold the rest into an "Also:" note.
        var perFlow = candidates
            .GroupBy(c => c.PipelineId)
            .Select(g =>
            {
                var ordered = g.OrderBy(c => c.Rank).ThenByDescending(c => c.Impact).ToList();
                var primary = ordered[0];
                return ordered.Count == 1
                    ? primary
                    : primary with
                    {
                        Detail = $"{primary.Detail} Also: {string.Join(", ", ordered.Skip(1).Select(c => c.ShortLabel))}.",
                    };
            })
            .ToList();

        // Batch collapse: a source whose flows went quiet (or silent) together reads as ONE advisory, not a
        // wall of near-identical lines. Flow names ride in the detail, capped, so the item stays scannable.
        var items = new List<(int Rank, double Impact, AttentionItemDto Item)>();
        foreach (var cluster in perFlow.GroupBy(c => (c.Category, Batch: c.Batch ?? string.Empty)))
        {
            var members = cluster.OrderByDescending(c => c.Impact).ThenBy(c => c.FlowName, StringComparer.OrdinalIgnoreCase).ToList();
            if (members.Count >= CollapseThreshold && CollapsibleCategories.Contains(cluster.Key.Category)
                && cluster.Key.Batch.Length > 0)
            {
                var first = members[0];
                var names = members.Select(m => m.FlowName).Take(8).ToList();
                var overflow = members.Count - names.Count;
                var verb = cluster.Key.Category == "zero-rows"
                    ? "went quiet (0 rows after loading rows in the previous window)"
                    : "are active but not running";
                items.Add((first.Rank, members.Count, new AttentionItemDto(
                    first.Severity, first.Category, PipelineId: null, FlowName: null, cluster.Key.Batch,
                    members.Count,
                    $"{members.Count} {cluster.Key.Batch} flows {cluster.Key.Category switch { "zero-rows" => "went quiet", _ => "stopped running" }}",
                    $"{string.Join(", ", names)}{(overflow > 0 ? $" and {overflow} more" : string.Empty)} {verb}.")));
                continue;
            }

            items.AddRange(members.Select(m => (m.Rank, m.Impact, new AttentionItemDto(
                m.Severity, m.Category, m.PipelineId, m.FlowName, m.Batch, 1, m.Title, m.Detail))));
        }

        var orderedItems = items
            .OrderBy(i => i.Rank)
            .ThenByDescending(i => i.Impact)
            .ThenBy(i => i.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Select(i => i.Item)
            .ToList();
        return (report.AsOfUtc, orderedItems);
    }

    /// <summary>How many advisories each DMV category contributes to the recommendations; the full lists stay
    /// on the task result (<c>GET /api/v1/datasources/tasks/{id}</c>).</summary>
    private const int MaxProbeItemsPerCategory = 5;

    /// <summary>Distills the newest succeeded warehouse-health task per (operation, datasource) into
    /// recommendation items. A task result that fails to parse contributes nothing but still reports its probe
    /// status, so a schema drift in the result JSON degrades to "data exists but yielded no items", visibly,
    /// rather than failing the whole endpoint.</summary>
    private static async Task<(List<RecommendationDto> Items, List<WarehouseProbeStatusDto> Probes)>
        DistillWarehouseProbesAsync(CatalogDbContext db, CancellationToken ct)
    {
        string[] operations =
        [
            ComputeOperations.MissingIndexes, ComputeOperations.StatisticsHealth,
            ComputeOperations.IndexUsage, ComputeOperations.TopQueries,
        ];
        var recent = await db.ComputeTasks.AsNoTracking()
            .Where(t => t.Status == RunStatuses.Succeeded && t.ResultJson != null && operations.Contains(t.Operation))
            .OrderByDescending(t => t.EndUtc)
            .Take(40)
            .Select(t => new { t.TaskId, t.Operation, t.SourceRef, t.EndUtc, t.ResultJson })
            .ToListAsync(ct).ConfigureAwait(false);

        var items = new List<RecommendationDto>();
        var probes = new List<WarehouseProbeStatusDto>();
        foreach (var task in recent.GroupBy(t => (t.Operation, t.SourceRef)).Select(g => g.First()))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(task.ResultJson!);
            }
            catch (JsonException)
            {
                continue; // a legacy or hand-edited row must not break the endpoint
            }

            using (document)
            {
                var root = document.RootElement;
                var database = root.TryGetProperty("database", out var databaseElement)
                    && databaseElement.ValueKind == JsonValueKind.String
                        ? databaseElement.GetString()
                        : null;
                probes.Add(new WarehouseProbeStatusDto(
                    task.Operation, task.TaskId, task.SourceRef, database, task.EndUtc));

                switch (task.Operation)
                {
                    case ComputeOperations.MissingIndexes:
                        AddMissingIndexItems(items, root, task.SourceRef, database);
                        break;
                    case ComputeOperations.StatisticsHealth:
                        AddStaleStatisticsItems(items, root, task.SourceRef, database);
                        break;
                    case ComputeOperations.IndexUsage:
                        AddUnusedIndexItems(items, root, task.SourceRef, database);
                        break;
                    default:
                        AddExpensiveQueryItems(items, root, task.SourceRef, database);
                        break;
                }
            }
        }

        return (items, probes);
    }

    private static void AddMissingIndexItems(
        List<RecommendationDto> items, JsonElement root, string reference, string? database)
    {
        foreach (var advisory in ArrayOf(root, "advisories").Take(MaxProbeItemsPerCategory))
        {
            var table = $"{Str(advisory, "schema")}.{Str(advisory, "table")}";
            var seeks = Num(advisory, "userSeeks") + Num(advisory, "userScans");
            var impact = Num(advisory, "avgUserImpactPercent");
            var measure = Num(advisory, "improvementMeasure");
            var sql = Str(advisory, "suggestedIndexSql");
            items.Add(new RecommendationDto(
                measure >= 1_000_000 ? "warning" : "info", "missing-index", "warehouseDmv",
                $"Missing index on {table}",
                $"The optimizer wanted this index {seeks:0} time(s) with an estimated {impact:0}% cost " +
                "reduction. Review for overlap with existing indexes and write cost before creating it.",
                sql, sql is not null, PipelineId: null, FlowName: null, reference, database));
        }
    }

    private static void AddStaleStatisticsItems(
        List<RecommendationDto> items, JsonElement root, string reference, string? database)
    {
        foreach (var stat in ArrayOf(root, "statistics")
                     .Where(s => s.TryGetProperty("isStale", out var stale)
                         && stale.ValueKind == JsonValueKind.True)
                     .Take(MaxProbeItemsPerCategory))
        {
            var table = $"{Str(stat, "schema")}.{Str(stat, "table")}";
            var sql = Str(stat, "suggestedUpdateSql");
            items.Add(new RecommendationDto(
                "warning", "stale-statistics", "warehouseDmv",
                $"Stale statistics on {table}",
                $"'{Str(stat, "statisticName")}' has {Num(stat, "modificationCounter"):0} modifications " +
                $"({Num(stat, "modificationPercent"):0.#}% of {Num(stat, "rows"):0} rows) since its last " +
                "update; the optimizer is planning against a stale picture of the data.",
                sql, sql is not null, PipelineId: null, FlowName: null, reference, database));
        }
    }

    private static void AddUnusedIndexItems(
        List<RecommendationDto> items, JsonElement root, string reference, string? database)
    {
        foreach (var index in ArrayOf(root, "indexes")
                     .Where(i => i.TryGetProperty("isUnused", out var unused)
                         && unused.ValueKind == JsonValueKind.True)
                     .Take(MaxProbeItemsPerCategory))
        {
            var schema = Str(index, "schema") ?? "dbo";
            var table = Str(index, "table") ?? "?";
            var name = Str(index, "indexName") ?? "?";
            items.Add(new RecommendationDto(
                "info", "unused-index", "warehouseDmv",
                $"Unused index {name} on {schema}.{table}",
                $"Maintained by {Num(index, "writes"):0} write(s) but served zero reads since the usage " +
                $"counters last reset ({Num(index, "sizeKb"):0} KB). Confirm the counter window covers a full " +
                "workload cycle (month-end, year-end jobs) before dropping.",
                $"DROP INDEX [{name}] ON [{schema}].[{table}];", HasSuggestedSql: true,
                PipelineId: null, FlowName: null, reference, database));
        }
    }

    private static void AddExpensiveQueryItems(
        List<RecommendationDto> items, JsonElement root, string reference, string? database)
    {
        foreach (var query in ArrayOf(root, "queries").Take(3))
        {
            var snippet = Str(query, "statementText") ?? string.Empty;
            if (snippet.Length > 160)
            {
                snippet = string.Concat(snippet.AsSpan(0, 160), "...");
            }

            items.Add(new RecommendationDto(
                "info", "expensive-query", "warehouseDmv",
                "Expensive query in the plan cache",
                $"{Num(query, "executionCount"):0} execution(s), {Num(query, "avgElapsedMs"):0} ms average, " +
                $"{FormatSeconds(Num(query, "totalElapsedMs") / 1000)} total elapsed: {snippet}",
                SuggestedSql: null, HasSuggestedSql: false, PipelineId: null, FlowName: null, reference,
                Str(query, "database") ?? database));
        }
    }

    private static IEnumerable<JsonElement> ArrayOf(JsonElement root, string property)
        => root.TryGetProperty(property, out var array)
            && array.ValueKind == JsonValueKind.Array
                ? array.EnumerateArray()
                : [];

    private static string? Str(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static double Num(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
                ? value.GetDouble()
                : 0;

    private static int SeverityRank(string severity) => severity switch
    {
        "critical" => 0,
        "warning" => 1,
        _ => 2,
    };

    private static async Task<Results<Ok<StepInsightsDto>, ProblemHttpResult>> GetStepInsightsAsync(
        Guid pipelineId, CatalogDbContext db, TimeProvider clock, int? days, bool? includeSql, CancellationToken ct)
    {
        if (Validate(days, limit: null) is { } problem)
        {
            return problem;
        }

        var windowDays = days ?? 30;
        var now = clock.GetUtcNow().UtcDateTime;
        var fromUtc = now.AddDays(-windowDays);

        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.Id, p.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return TypedResults.Problem(
                detail: $"No pipeline '{pipelineId}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // Stage timings: only the steps the engine measured (ElapsedMs is emitted on stage summaries), grouped
        // across every run of the flow in the window so a one-off blip does not read as the shape of the flow.
        var steps = await db.RunEvents.AsNoTracking()
            .Join(db.Runs.AsNoTracking(), e => e.RunId, r => r.RunId, (e, r) => new { Event = e, Run = r })
            .Where(x => x.Run.PipelineId == pipelineId && x.Run.WrittenUtc >= fromUtc)
            .Where(x => x.Event.ElapsedMs != null && x.Event.Step != null)
            .GroupBy(x => x.Event.Step!)
            .Select(g => new
            {
                Step = g.Key,
                Occurrences = g.LongCount(),
                AvgElapsedMs = g.Average(x => x.Event.ElapsedMs!.Value),
                MaxElapsedMs = g.Max(x => x.Event.ElapsedMs!.Value),
                TotalElapsedMs = g.Sum(x => x.Event.ElapsedMs!.Value),
                RowsProcessed = g.Sum(x => x.Event.Rows ?? 0),
            })
            .OrderByDescending(g => g.TotalElapsedMs)
            .ToListAsync(ct).ConfigureAwait(false);

        // The newest run that recorded statements supplies one sample SQL per step, so a hot step shows the
        // code it ran. Steps the trace labels but the statements do not (source.open, incremental) get null.
        // SQL bodies travel only on request (includeSql=true): the default answer stays small enough for a
        // context-limited client, which still learns which steps are hot and can re-ask with SQL for one flow.
        var wantSql = includeSql == true;
        var sampleRunId = await db.RunStatements.AsNoTracking()
            .Join(db.Runs.AsNoTracking(), s => s.RunId, r => r.RunId, (s, r) => new { s.RunId, r.PipelineId, r.WrittenUtc })
            .Where(x => x.PipelineId == pipelineId)
            .OrderByDescending(x => x.WrittenUtc).ThenByDescending(x => x.RunId)
            .Select(x => (Guid?)x.RunId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var sampleSqlByStep = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (wantSql && sampleRunId is { } runId)
        {
            var statements = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId)
                .OrderBy(s => s.Ordinal)
                .Select(s => new { s.Step, s.Sql })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var statement in statements)
            {
                if (!sampleSqlByStep.ContainsKey(statement.Step))
                {
                    sampleSqlByStep[statement.Step] = statement.Sql.Length > MaxSampleSqlChars
                        ? string.Concat(statement.Sql.AsSpan(0, MaxSampleSqlChars), "\n-- ...truncated")
                        : statement.Sql;
                }
            }
        }

        var dto = new StepInsightsDto(
            pipeline.Id, pipeline.Name, windowDays, fromUtc, sampleRunId,
            steps.Select(s => new StepInsightDto(
                s.Step, s.Occurrences, s.AvgElapsedMs, s.MaxElapsedMs, s.TotalElapsedMs, s.RowsProcessed,
                sampleSqlByStep.GetValueOrDefault(s.Step))).ToList());
        return TypedResults.Ok(dto);
    }

    /// <summary>The shared aggregation both /flows and /attention read: one grouped pass over the window's
    /// terminal runs, one over the previous window (for the trend), one latest-run-per-pipeline probe (the same
    /// TOP(1) shape the runs board uses, answered by the (PipelineId, WrittenUtc DESC, RunId DESC) index), and
    /// one pipeline-metadata lookup. Cancelled runs are excluded throughout: they say nothing about cost.</summary>
    private static async Task<FlowInsightsDto> ComputeFlowInsightsAsync(
        CatalogDbContext db, TimeProvider clock, int windowDays, Guid? repoId, string? batch, int limit,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var fromUtc = now.AddDays(-windowDays);
        var prevFromUtc = now.AddDays(-2 * windowDays);

        var window = db.Runs.AsNoTracking()
            .Where(r => r.WrittenUtc >= fromUtc
                && (r.Status == RunStatuses.Succeeded || r.Status == RunStatuses.Failed)
                && (repoId == null || r.RepoId == repoId));

        var aggregates = await window
            .GroupBy(r => r.PipelineId)
            .Select(g => new
            {
                PipelineId = g.Key,
                Runs = g.LongCount(),
                Failures = g.LongCount(r => r.Status == RunStatuses.Failed),
                AvgDuration = g.Where(r => r.Status == RunStatuses.Succeeded).Average(r => r.DurationSeconds),
                MaxDuration = g.Max(r => r.DurationSeconds),
                TotalDuration = g.Sum(r => r.DurationSeconds ?? 0),
                RowsLoaded = g.Sum(r => r.RowsLoaded ?? 0),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        var previous = await db.Runs.AsNoTracking()
            .Where(r => r.WrittenUtc >= prevFromUtc && r.WrittenUtc < fromUtc
                && r.Status == RunStatuses.Succeeded
                && (repoId == null || r.RepoId == repoId))
            .GroupBy(r => r.PipelineId)
            .Select(g => new
            {
                PipelineId = g.Key,
                AvgDuration = g.Average(r => r.DurationSeconds),
                Rows = g.Sum(r => r.RowsLoaded ?? 0),
            })
            .ToDictionaryAsync(g => g.PipelineId, g => (g.AvgDuration, g.Rows), ct).ConfigureAwait(false);

        var latest = await window
            .Where(r => r.RunId == db.Runs
                .Where(c => c.PipelineId == r.PipelineId && c.WrittenUtc >= fromUtc
                    && (c.Status == RunStatuses.Succeeded || c.Status == RunStatuses.Failed)
                    && (repoId == null || c.RepoId == repoId))
                .OrderByDescending(c => c.WrittenUtc).ThenByDescending(c => c.RunId)
                .Select(c => c.RunId)
                .FirstOrDefault())
            .Select(r => new { r.PipelineId, r.FlowName, r.FlowKind, r.Status, r.Error, r.WrittenUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        var latestByPipeline = latest.ToDictionary(r => r.PipelineId);

        var pipelineIds = aggregates.Select(a => a.PipelineId).ToList();
        var meta = await db.Pipelines.AsNoTracking()
            .Where(p => pipelineIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Batch, p.Active })
            .ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);

        var batchFilter = string.IsNullOrWhiteSpace(batch) ? null : batch.Trim();
        var flows = new List<FlowInsightDto>(aggregates.Count);
        foreach (var aggregate in aggregates)
        {
            // A run whose pipeline left the catalog still aggregates (its history is real); it simply carries no
            // batch and reads as inactive. The batch filter excludes it, matching the runs board's semantics.
            var pipelineMeta = meta.GetValueOrDefault(aggregate.PipelineId);
            if (batchFilter is not null
                && !string.Equals(pipelineMeta?.Batch, batchFilter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!latestByPipeline.TryGetValue(aggregate.PipelineId, out var last))
            {
                continue; // the pipeline's runs all left the window between the two queries; nothing to report
            }

            var hasPrevious = previous.TryGetValue(aggregate.PipelineId, out var prev);
            double? trend = hasPrevious && prev.AvgDuration is > 0 && aggregate.AvgDuration is { } avg
                ? (avg - prev.AvgDuration.Value) / prev.AvgDuration.Value * 100.0
                : null;
            flows.Add(new FlowInsightDto(
                aggregate.PipelineId, last.FlowName, last.FlowKind, pipelineMeta?.Batch,
                pipelineMeta?.Active ?? false,
                aggregate.Runs, aggregate.Failures,
                aggregate.Runs > 0 ? (double)aggregate.Failures / aggregate.Runs : 0,
                aggregate.AvgDuration, aggregate.MaxDuration, aggregate.TotalDuration,
                aggregate.RowsLoaded,
                aggregate.TotalDuration > 0 ? aggregate.RowsLoaded / aggregate.TotalDuration : null,
                last.WrittenUtc, last.Status, Truncate(last.Error, MaxErrorChars),
                hasPrevious ? prev.AvgDuration : null, hasPrevious ? prev.Rows : null, trend));
        }

        var orderedFlows = flows
            .OrderByDescending(f => f.TotalDurationSeconds)
            .ThenByDescending(f => f.Runs)
            .Take(limit)
            .ToList();
        return new FlowInsightsDto(
            windowDays, fromUtc, now,
            flows.Sum(f => f.Runs), flows.Sum(f => f.Failures),
            flows.Sum(f => f.TotalDurationSeconds), flows.Sum(f => f.RowsLoaded),
            orderedFlows);
    }

    private static ProblemHttpResult? Validate(int? days, int? limit)
    {
        if (days is < 1 or > MaxWindowDays)
        {
            return TypedResults.Problem(
                detail: $"days must be between 1 and {MaxWindowDays}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (limit is < 1 or > MaxFlows)
        {
            return TypedResults.Problem(
                detail: $"limit must be between 1 and {MaxFlows}.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        return null;
    }

    private static string FormatSeconds(double seconds) => seconds switch
    {
        >= 3600 => $"{seconds / 3600:0.#}h",
        >= 60 => $"{seconds / 60:0.#}m",
        _ => $"{seconds:0.#}s",
    };

    private static string? Truncate(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : string.Concat(trimmed.AsSpan(0, maxChars), "...");
    }
}
