using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Diagnostics;
using SqlFlow.Core.Events;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Orchestrates a flow: infer source schema, introspect target, diff, generate DDL, then
/// (plan: stop and report / run: execute + bulk load). Stateless and reentrant: all per-run state
/// lives in a <see cref="RunContext"/> created per call, so any number of flows can run concurrently
/// without interfering. Every run has a unique RunId stamped on each event, log scope, and result,
/// so concurrent runs stay individually attributable.
/// </summary>
public sealed class FlowRunner
{
    private readonly IReadOnlyList<ISourceReader> _sources;
    private readonly ISqlTypeMapper _typeMapper;
    private readonly ISchemaProvider _schema;
    private readonly IColumnTypeReconciler _typeReconciler;
    private readonly IDdlGenerator _ddl;
    private readonly IBulkLoader _loader;
    private readonly IIndexManager _indexManager;
    private readonly IDesiredIndexManager _desiredIndexManager;
    private readonly IIncrementalProbe _incrementalProbe;
    private readonly IStateStore _state;
    private readonly IFlowEventSink _events;
    private readonly ISecretResolver _secrets;
    private readonly IInferenceService _inference;
    private readonly ILogger<FlowRunner> _logger;

    public FlowRunner(
        IEnumerable<ISourceReader> sources,
        ISqlTypeMapper typeMapper,
        ISchemaProvider schema,
        IColumnTypeReconciler typeReconciler,
        IDdlGenerator ddl,
        IBulkLoader loader,
        IIndexManager indexManager,
        IDesiredIndexManager desiredIndexManager,
        IIncrementalProbe incrementalProbe,
        IStateStore state,
        IFlowEventSink events,
        ISecretResolver secrets,
        IInferenceService inference,
        ILogger<FlowRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = sources.ToList();
        _typeMapper = typeMapper;
        _schema = schema;
        _typeReconciler = typeReconciler;
        _ddl = ddl;
        _loader = loader;
        _indexManager = indexManager;
        _desiredIndexManager = desiredIndexManager;
        _incrementalProbe = incrementalProbe;
        _state = state;
        _events = events;
        _secrets = secrets;
        _inference = inference;
        _logger = logger;
    }

    public async Task<FlowPlan> PlanAsync(FlowDefinition flow, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var context = new RunContext(Guid.CreateVersion7(), flow.FlowId, flow.Name, _events);
        using var scope = _logger.BeginScope("Flow {FlowName} ({RunId})", flow.Name, context.RunId);
        return await PlanCoreAsync(flow, context, ct).ConfigureAwait(false);
    }

    public Task<FlowResult> RunAsync(FlowDefinition flow, CancellationToken ct = default)
        => RunAsync(flow, null, null, ct);

    public Task<FlowResult> RunAsync(FlowDefinition flow, Guid? runId, CancellationToken ct = default)
        => RunAsync(flow, runId, null, null, ct);

    public Task<FlowResult> RunAsync(FlowDefinition flow, Guid? runId, IRunStatementSink? statementSink, CancellationToken ct = default)
        => RunAsync(flow, runId, statementSink, null, null, null, null, ct);

    public Task<FlowResult> RunAsync(
        FlowDefinition flow, Guid? runId, IRunStatementSink? statementSink, string? runHistoryDirectory, CancellationToken ct = default)
        => RunAsync(flow, runId, statementSink, runHistoryDirectory, null, null, null, ct);

    /// <param name="flow">The validated flow to run.</param>
    /// <param name="runId">An orchestrator-assigned run id stamped on the run instead of minting one; the
    /// control-plane trigger supplies the id it already handed the caller so the recorded run resolves under it.
    /// Null mints a fresh time-ordered id, which is what every direct CLI run does.</param>
    /// <param name="statementSink">Receives each executed SQL statement as it runs, so the node can stream the
    /// trace into the catalog live (the Statements view updates while the run is in flight). Null (every CLI and
    /// library caller) records nothing live; the trace still rides the result and is projected at completion.</param>
    /// <param name="runHistoryDirectory">The directory this flow's on-disk run history is anchored to (the flow
    /// document's folder). Supplied so a file flow's incremental probe can read the durable last-processed
    /// watermark from prior runs when the target table is a transient landing table. Null skips that source and
    /// uses only the target-table probe.</param>
    /// <param name="watermarkTable">The next durable table downstream in the lineage graph (the ods/silver table
    /// this flow feeds), resolved by the control plane. When set, the incremental watermark is probed from THIS
    /// table instead of the flow's own target, so deleting rows there re-opens the read window (bronze is driven
    /// by what silver holds). Null (a direct CLI run, or no unambiguous downstream table) probes the flow's own
    /// target. The anchor is column-safe and reachability-safe: an absent table/column falls back to the target.</param>
    /// <param name="events">A per-run event sink attached ALONGSIDE the host-wide one (the executor's artifact
    /// collector, and through it the node's live catalog writer), so this run's canonical events reach the
    /// run.json <c>events</c> array and the control plane's Events view while the CLI console keeps its own
    /// stream. Null (a plain library caller) publishes to the host-wide sink only.</param>
    /// <param name="landingReset">The control plane's consolidation verdict for a chained landing target
    /// (<c>load.resetWhenConsolidated</c>): when authorized, the target is truncated after the source read finds
    /// files and before the load, so a landing table whose rows every direct consumer has already merged starts
    /// the run empty instead of growing forever. Null (a direct CLI run, or a flow with no lineage consumers)
    /// never resets.</param>
    /// <param name="ct">Cancellation for the run.</param>
    public async Task<FlowResult> RunAsync(
        FlowDefinition flow, Guid? runId, IRunStatementSink? statementSink, string? runHistoryDirectory,
        RelationalObject? watermarkTable, IFlowEventSink? events = null, LandingReset? landingReset = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);

        var runEvents = events is null ? _events : new CompositeFlowEventSink(_events, events);
        var context = new RunContext(runId ?? Guid.CreateVersion7(), flow.FlowId, flow.Name, runEvents, runHistoryDirectory);
        var startedAt = Stopwatch.GetTimestamp();
        string? connectionString = null;
        IncrementalSummary? incrementalSummary = null;
        IReadOnlyList<string> disabledIndexes = [];

        // The run's ordered SQL trace: every statement executed against the target, captured as it runs and
        // streamed to the live sink so the node persists it during the run. On failure the offending statement
        // is stamped (executingSequence tracks which traced statement is mid-execution; 0 means the failure was
        // outside any traced statement, for example the bulk load, so no statement is falsely blamed).
        var statements = statementSink ?? NullRunStatementSink.Instance;
        var trace = new List<SqlTraceEntry>();
        var executingSequence = 0;
        void Trace(string step, string? sql)
        {
            if (string.IsNullOrWhiteSpace(sql))
            {
                return;
            }

            var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql };
            trace.Add(entry);
            statements.Report(entry);
        }

        // Trace each statement in a DDL group, execute the group as one stage, and clear the in-flight marker on
        // success. A throw leaves executingSequence pointing at the group's last statement so the catch attributes
        // the failure (best-effort within a batched group, precise for single-statement stages like the view).
        async Task RunDdlAsync(string step, IReadOnlyList<string> ddl)
        {
            if (ddl.Count == 0)
            {
                return;
            }

            foreach (var statement in ddl)
            {
                Trace(step, statement);
            }

            executingSequence = trace.Count;
            await StageAsync(step, context, () => _schema.ExecuteDdlAsync(connectionString!, ddl, ct)).ConfigureAwait(false);
            executingSequence = 0;
        }

        using var scope = _logger.BeginScope("Flow {FlowName} {FlowId} ({RunId})", flow.Name, context.FlowId, context.RunId);
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity("flow.run");
        activity?.SetTag("flow.name", flow.Name);
        activity?.SetTag("flow.id", context.FlowId);
        activity?.SetTag("flow.run_id", context.RunId);
        activity?.SetTag("flow.type", flow.Source.Type);
        Emit(context, $"Flow '{flow.Name}' started -> {flow.Target.QualifiedName}");

        try
        {
            connectionString = await _secrets.ResolveAsync(flow.Target.Connection, ct).ConfigureAwait(false);

            // Incremental: probe the target for the watermark and bound the source read to new files
            // only. Returns the original flow unchanged when there is no incremental spec or no watermark,
            // alongside the summary of which filter (if any) the read used.
            FlowDefinition effectiveFlow;
            (effectiveFlow, incrementalSummary) = await ApplyIncrementalAsync(flow, connectionString, watermarkTable, context, ct).ConfigureAwait(false);

            var plan = await PlanCoreAsync(effectiveFlow, context, ct, connectionString).ConfigureAwait(false);

            // A table that did not exist before this run is created by the schema DDL below; that
            // distinction drives index handling: a new table gets its declared (desired) indexes built
            // after load, while an existing table has its current indexes disabled for the load and
            // rebuilt after. The two paths are mutually exclusive, so no index work is duplicated.
            var tableIsNew = plan.Actual is null;

            await RunDdlAsync("schema.apply-ddl", plan.DdlStatements).ConfigureAwait(false);

            await RunDdlAsync("target.preprocess", flow.PreProcess).ConfigureAwait(false);

            // Disabling and rebuilding every nonclustered index costs O(table size), which pays off when the
            // load replaces or dwarfs the existing data but dominates wall-clock when a watermark-bounded
            // incremental run appends a small delta. An incremental bound (effectiveFlow differs from flow)
            // means only new data is read, so per-row index maintenance during the insert is the cheaper path
            // and the disable/rebuild pair is skipped for that run.
            var incrementallyBounded = !ReferenceEquals(effectiveFlow, flow);
            if (flow.Load.ManageIndexes && !tableIsNew && !incrementallyBounded)
            {
                disabledIndexes = await StageAsync("indexes.disable", context,
                    () => _indexManager.DisableNonClusteredAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct)).ConfigureAwait(false);
            }
            else if (flow.Load.ManageIndexes && !tableIsNew)
            {
                Emit(context, "indexes: incremental run loads only the delta; keeping indexes online instead of disable/rebuild", stage: "indexes.disable");
            }

            if (flow.Load.Mode == LoadMode.TruncateLoad)
            {
                await StageAsync("target.truncate", context, () => _loader.TruncateAsync(connectionString, flow.Target, ct)).ConfigureAwait(false);
            }

            var reader = ResolveReader(effectiveFlow.Source.Type);
            var read = await StageAsync("source.open", context, () => reader.OpenAsync(effectiveFlow.Source, plan.SourceColumns, ct)).ConfigureAwait(false);

            // Landing reset (load.resetWhenConsolidated): a chained landing (bronze) target is pure staging, so
            // once every direct consumer has merged its rows into the next phase (the control plane's verdict,
            // computed from lineage and the run ledger), this run starts the table empty instead of appending
            // forever. Deliberately AFTER source.open: a run that finds no files never reaches here (the no-op
            // catch below fires first), so a quiet day keeps the landing rows and a downstream full-reload
            // consumer never reloads from an emptied table. Only an append-mode flow that generates the typed
            // view participates (truncate-load already replaces; no view means no chained landing contract),
            // and only when the table existed before this run (a just-created table has nothing to reset).
            if (landingReset is not null && flow.Load is { Mode: LoadMode.Append, ResetWhenConsolidated: true }
                && flow.Inference.GeneratesView && !tableIsNew)
            {
                if (landingReset.Authorized)
                {
                    await StageAsync("target.reset", context, () => _loader.TruncateAsync(connectionString, flow.Target, ct)).ConfigureAwait(false);
                    Emit(context, $"landing {flow.Target.QualifiedName} reset before load: {landingReset.Reason}", stage: "target.reset");
                }
                else
                {
                    Emit(context, $"landing {flow.Target.QualifiedName} retained: {landingReset.Reason}", stage: "target.reset");
                }
            }

            // Row-level incremental: drop any row not past the watermark before it reaches the bulk loader.
            // This is reader-agnostic (DuckDB also pushes the same bound into its scan; here it is a no-op
            // pass-through), so every source kind gets the same row-level filter from one place.
            await using var data = WrapWatermarkFilter(effectiveFlow.Source, read.Reader);
            var rows = await StageAsync("target.load", context, () => _loader.LoadAsync(connectionString, flow.Target, data, flow.Load, ct), r => r).ConfigureAwait(false);

            // The manifest is populated as the reader streams, so per-file row counts are known only
            // once the load has drained it.
            foreach (var file in read.ProcessedFiles)
            {
                Emit(context, $"read '{file.Name}' ({file.Rows} row(s))", stage: "source.open");
            }

            if (disabledIndexes.Count > 0)
            {
                var toRebuild = disabledIndexes;
                disabledIndexes = [];
                await StageAsync("indexes.rebuild", context,
                    () => _indexManager.RebuildAsync(connectionString, flow.Target.Schema, flow.Target.Table, toRebuild, ct)).ConfigureAwait(false);
            }

            if (tableIsNew && !string.IsNullOrWhiteSpace(flow.DesiredIndexes))
            {
                var script = flow.DesiredIndexes;
                var actions = await StageAsync("indexes.desired", context,
                    () => _desiredIndexManager.ApplyAsync(connectionString, script, ct), a => a.Count).ConfigureAwait(false);
                foreach (var action in actions)
                {
                    if (action.Kind == IndexActionKind.Created)
                    {
                        Emit(context, $"created index [{action.IndexName}] on {action.Table}", stage: "indexes.desired");
                    }
                    else
                    {
                        Emit(context, $"index [{action.IndexName}] not created: {action.Detail}", FlowEventLevel.Warning, "indexes.desired");
                    }
                }
            }

            await RunDdlAsync("target.postprocess", flow.PostProcess).ConfigureAwait(false);

            await StageAsync("source.complete", context, () => reader.CompleteAsync(effectiveFlow.Source, ct)).ConfigureAwait(false);

            // Pre-ingestion transform view: refresh the typed view over the just-loaded table (the V2 post-process
            // that downstream chained flows read for correct data types). Runs after source.complete so a view
            // failure never leaves the files un-finalized: the load stands, the files are marked ingested, and a
            // re-run regenerates the view (CREATE OR ALTER is idempotent) without re-reading anything. A failure
            // here still fails the run - downstream flows read this view, so a stale one must be loud.
            TransformViewResult? transformView = null;
            if (flow.Inference.GeneratesView)
            {
                var cs = connectionString;
                transformView = await StageAsync("transform.view", context, async () =>
                {
                    // Build the view DDL (introspect + infer + resolve), trace it, then execute: trace-then-execute
                    // so a failure here stamps the exact view statement, and the live sink sees it before it runs.
                    var built = await BuildTransformViewAsync(flow, cs, ct).ConfigureAwait(false);
                    Trace("transform.view", built.Ddl);
                    executingSequence = trace.Count;
                    await _schema.ExecuteDdlAsync(cs, [built.Ddl], ct).ConfigureAwait(false);
                    executingSequence = 0;
                    return built;
                }, r => r.Columns.Count).ConfigureAwait(false);
                Emit(context,
                    $"transformation view [{flow.Target.Schema}].[{transformView.ViewName}] refreshed "
                    + $"({transformView.Columns.Count} column(s), {transformView.Columns.Count(c => c.Converted)} typed)",
                    stage: "transform.view");
            }

            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            activity?.SetTag("rows.loaded", rows);

            _logger.LogInformation(
                "Flow '{Flow}' succeeded: {Rows} row(s) into {Target} in {ElapsedMs:F0} ms.",
                flow.Name, rows, flow.Target.QualifiedName, totalMs);
            Emit(context, $"Flow '{flow.Name}' succeeded: {rows} row(s) in {totalMs:F0} ms");

            await _state.SaveAsync(
                new FlowState { FlowName = flow.Name, LastRunUtc = DateTimeOffset.UtcNow, LastStatus = "Success" },
                ct).ConfigureAwait(false);

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Success,
                RowsLoaded = rows,
                // The file load is a straight SqlBulkCopy (no upsert on this path), so every loaded row is an
                // insert; report it as such so the run's inserted count is projected and shown in the GUI.
                RowsInserted = rows,
                DdlExecuted = plan.DdlStatements,
                SqlTrace = trace,
                ProcessedFiles = read.ProcessedFiles,
                Trace = context.Trace,
                TotalMs = totalMs,
                TransformView = transformView,
                Incremental = incrementalSummary,
                DataSetConvention = read.DataSetConvention,
            };
        }
        catch (NoSourceFilesException ex) when (flow.Incremental is { FullLoad: false })
        {
            // Incremental runs commonly find nothing new; that is a clean no-op, not a failure. The
            // probe runs before any target mutation, so there is nothing to roll back here.
            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            // A location holding no candidate file at all is also a no-op, but not an unremarkable one: it is
            // indistinguishable from a wrong path or pattern, and a flow that quietly loads nothing forever is
            // the failure mode this warning exists to catch. Files that are merely all older than the watermark
            // are the opposite - the expected resting state - and stay at info.
            var noCandidates = ex.Reason == NoSourceFilesReason.NoCandidates;
            if (noCandidates)
            {
                _logger.LogWarning("Flow '{Flow}' loaded nothing: {Reason}", flow.Name, ex.Message);
            }
            else
            {
                _logger.LogInformation("Flow '{Flow}': {Reason}", flow.Name, ex.Message);
            }

            Emit(context, $"Flow '{flow.Name}': {ex.Message}", noCandidates ? FlowEventLevel.Warning : FlowEventLevel.Info);

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Success,
                RowsLoaded = 0,
                SqlTrace = trace,
                Trace = context.Trace,
                TotalMs = totalMs,
                Incremental = incrementalSummary,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Best-effort: rebuild any indexes we disabled, so a failed load doesn't leave the table
            // with its indexes disabled.
            if (disabledIndexes.Count > 0 && connectionString is not null)
            {
                try
                {
                    await _indexManager.RebuildAsync(connectionString, flow.Target.Schema, flow.Target.Table, disabledIndexes, ct).ConfigureAwait(false);
                }
                catch (Exception restoreEx)
                {
                    _logger.LogError(restoreEx, "Failed to rebuild disabled indexes after a failed load on {Target}.", flow.Target.QualifiedName);
                }
            }

            var totalMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.LogError(ex, "Flow '{Flow}' failed after {ElapsedMs:F0} ms.", flow.Name, totalMs);
            Emit(context, $"Flow '{flow.Name}' failed: {ex.Message}", FlowEventLevel.Error);

            // Stamp the failure onto the statement that was mid-execution (a DDL group or the view refresh) and
            // report it to the live sink, so both the artifact trace and the live rows mark which one broke. A
            // failure outside any traced statement (the bulk load) leaves executingSequence 0 and blames nothing.
            if (executingSequence > 0)
            {
                trace[executingSequence - 1] = trace[executingSequence - 1] with { Error = SecretHygiene.RedactedMessage(ex) };
                statements.ReportFailure(executingSequence, ex.Message);
            }

            return new FlowResult
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = flow.Name,
                Status = FlowStatus.Failed,
                SqlTrace = trace,
                Trace = context.Trace,
                TotalMs = totalMs,
                Error = SecretHygiene.RedactedMessage(ex),
                Incremental = incrementalSummary,
            };
        }
    }

    /// <summary>
    /// Probes the target for the incremental watermark and returns a flow whose source is bounded to new data.
    /// In file-date mode the source is bounded to files newer than the watermark; in row-level mode the bound
    /// is injected as reader options that DuckDB pushes into its scan and the engine enforces row by row.
    /// Returns the flow unchanged when there is no incremental spec, a forced full load, or no prior watermark
    /// (empty/missing target).
    /// </summary>
    private async Task<(FlowDefinition Flow, IncrementalSummary? Summary)> ApplyIncrementalAsync(
        FlowDefinition flow, string connectionString, RelationalObject? watermarkTable, RunContext context, CancellationToken ct)
    {
        if (flow.Incremental is not { FullLoad: false } incremental)
        {
            // No incremental spec at all: no incremental surface to report. A declared/forced full load still has
            // one, so the detail can state the watermark was bypassed rather than showing nothing.
            var summary = flow.Incremental is null
                ? null
                : new IncrementalSummary { Mode = IncrementalModes.Full, Filter = "full load (watermark bypassed)" };
            return (flow, summary);
        }

        var probeTable = string.IsNullOrWhiteSpace(incremental.Table) ? flow.Target.QualifiedName : incremental.Table!;

        // Downstream anchoring (the default) applies only when the flow did not explicitly pin a probe table with
        // incremental.table: an explicit override is a deliberate operator choice and wins over the lineage-derived
        // silver table.
        var downstream = string.IsNullOrWhiteSpace(incremental.Table) ? watermarkTable : null;

        if (!string.IsNullOrWhiteSpace(incremental.WatermarkColumn))
        {
            return await ApplyRowWatermarkAsync(flow, connectionString, probeTable, downstream, incremental, context, ct).ConfigureAwait(false);
        }

        // Probe the silver table's file-date watermark first. Its MAX is the end-to-end high-water mark; the
        // run-history floor below is deliberately NOT applied to it, so deleting rows from silver lets the mark
        // regress and the source is re-pulled. A missing/empty silver table or a renamed/absent date column returns
        // null and falls through to the flow's own target + run-history path.
        if (downstream is not null)
        {
            var anchored = await TryDownstreamFileWatermarkAsync(flow, connectionString, downstream, incremental, context, ct).ConfigureAwait(false);
            if (anchored is not null)
            {
                return anchored.Value;
            }
        }

        var probed = await StageAsync("incremental.probe", context,
            () => _incrementalProbe.GetWatermarkAsync(connectionString, probeTable, incremental.DateColumn, incremental.OverlapDays, ct))
            .ConfigureAwait(false);

        // The durable, database-free floor: the newest file this flow has already processed, read from its own
        // on-disk run history. A file flow's target is often a transient landing table (append-mode, truncated
        // between stages), so probing it for MAX(DateColumn) returns null and the run would re-fetch every file;
        // the run log survives that. The same overlap the probe applies server-side is applied here, then the
        // higher (more recent) of the two marks wins, so the watermark never regresses below either source.
        DateTimeOffset? logged = null;
        if (!string.IsNullOrWhiteSpace(context.RunHistoryDirectory)
            && RunHistoryReader.LastProcessedFileDate(context.RunHistoryDirectory!, flow.Name) is { } lastProcessed)
        {
            logged = lastProcessed.AddDays(-incremental.OverlapDays);
        }

        var best = MostRecent(probed, logged);
        if (best is not { } mark)
        {
            Emit(context, "incremental: no prior watermark (target empty and no run history); loading all available files");
            return (flow, new IncrementalSummary { Mode = IncrementalModes.Full, Filter = "all files (no prior watermark)" });
        }

        var source = probed is { } p && mark == p ? "target" : "run log";
        Emit(context, $"incremental: watermark {mark:u} (from {source}); reading files newer than it (overlap {incremental.OverlapDays}d)");

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
        {
            ["incrementalAfterDate"] = mark.UtcDateTime.ToString("o"),
        };

        var dateSummary = new IncrementalSummary
        {
            Mode = IncrementalModes.Incremental,
            Filter = $"files newer than {mark:u} (overlap {incremental.OverlapDays}d)",
            Watermark = mark.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
            WatermarkSource = source == "target" ? $"target MAX {probeTable}" : "run log",
        };

        return (flow with { Source = flow.Source with { Options = options } }, dateSummary);
    }

    // The more recent of two optional watermarks: the "best" incremental floor. Null only when both are null.
    private static DateTimeOffset? MostRecent(DateTimeOffset? a, DateTimeOffset? b)
        => a is null ? b : b is null ? a : (a.Value >= b.Value ? a : b);

    /// <summary>
    /// Probes the downstream (silver) table's file-date watermark for the default downstream anchoring. Its MAX is
    /// authoritative and, unlike the target probe, is NOT floored by the run history: the whole point is that the
    /// watermark tracks the silver table and regresses with it, so deleting rows from silver re-opens the read
    /// window and the source is re-pulled. Returns null (so the caller falls back to the flow's own target +
    /// run-history path) when the silver table is missing or empty, or when its date column is renamed/absent or an
    /// unsuitable type (the probe throws, caught here) - never failing the run or forcing a blind full reload.
    /// </summary>
    private async Task<(FlowDefinition Flow, IncrementalSummary? Summary)?> TryDownstreamFileWatermarkAsync(
        FlowDefinition flow, string connectionString, RelationalObject watermarkTable, IncrementalSpec incremental, RunContext context, CancellationToken ct)
    {
        DateTimeOffset? probed;
        try
        {
            probed = await _incrementalProbe
                .GetWatermarkAsync(connectionString, watermarkTable.QualifiedName, incremental.DateColumn, incremental.OverlapDays, ct)
                .ConfigureAwait(false);
        }
        catch (SqlFlowException)
        {
            return null;
        }

        if (probed is not { } mark)
        {
            return null;
        }

        Emit(context, $"incremental: downstream watermark {mark:u} (from {watermarkTable.QualifiedName}); reading files newer than it (overlap {incremental.OverlapDays}d)");

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
        {
            ["incrementalAfterDate"] = mark.UtcDateTime.ToString("o"),
        };

        var summary = new IncrementalSummary
        {
            Mode = IncrementalModes.Incremental,
            Filter = $"files newer than {mark:u} (overlap {incremental.OverlapDays}d)",
            Watermark = mark.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
            WatermarkSource = $"downstream MAX {watermarkTable.QualifiedName}",
        };

        return (flow with { Source = flow.Source with { Options = options } }, summary);
    }

    /// <summary>
    /// Row-level incremental: probe <c>MAX(WatermarkColumn)</c> on the target, then inject the typed bound as
    /// source options. DuckDB renders them into a pushdown predicate (so it reads almost nothing past the bound
    /// on a large dataset); every reader is then filtered row by row against the same bound by the engine.
    /// </summary>
    private async Task<(FlowDefinition Flow, IncrementalSummary? Summary)> ApplyRowWatermarkAsync(
        FlowDefinition flow, string connectionString, string probeTable, RelationalObject? watermarkTable, IncrementalSpec incremental, RunContext context, CancellationToken ct)
    {
        var column = incremental.WatermarkColumn!;

        // Downstream anchoring (the default): probe the silver table's row-level MAX. A missing/empty table or a
        // renamed/absent column falls back to the flow's own target probe below.
        if (watermarkTable is not null)
        {
            WatermarkValue? downstreamBound;
            try
            {
                downstreamBound = await _incrementalProbe
                    .GetRowWatermarkAsync(connectionString, watermarkTable.QualifiedName, column, incremental.WatermarkOverlap, ct)
                    .ConfigureAwait(false);
            }
            catch (SqlFlowException)
            {
                downstreamBound = null;
            }

            if (downstreamBound is { } dwn)
            {
                var canonicalDownstream = WatermarkPredicate.Format(dwn);
                Emit(context, $"incremental: downstream watermark {column} > {canonicalDownstream} (from {watermarkTable.QualifiedName}); reading only rows past it");

                var downstreamOptions = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
                {
                    [WatermarkPredicate.ColumnOption] = column,
                    [WatermarkPredicate.ValueOption] = canonicalDownstream,
                    [WatermarkPredicate.KindOption] = dwn.Kind.ToString(),
                };

                var downstreamSummary = new IncrementalSummary
                {
                    Mode = IncrementalModes.Incremental,
                    Filter = $"rows where {column} > {canonicalDownstream}",
                    Watermark = canonicalDownstream,
                    WatermarkSource = $"downstream MAX {watermarkTable.QualifiedName}",
                };

                return (flow with { Source = flow.Source with { Options = downstreamOptions } }, downstreamSummary);
            }
        }

        var bound = await StageAsync("incremental.probe", context,
            () => _incrementalProbe.GetRowWatermarkAsync(connectionString, probeTable, column, incremental.WatermarkOverlap, ct))
            .ConfigureAwait(false);

        if (bound is null)
        {
            Emit(context, $"incremental: no prior watermark on the target for column '{column}'; loading all available rows");
            return (flow, new IncrementalSummary { Mode = IncrementalModes.Full, Filter = "all rows (no prior watermark)" });
        }

        var canonical = WatermarkPredicate.Format(bound);
        Emit(context, $"incremental: target watermark {column} > {canonical}; reading only rows past it");

        var options = new Dictionary<string, string?>(flow.Source.Options, StringComparer.OrdinalIgnoreCase)
        {
            [WatermarkPredicate.ColumnOption] = column,
            [WatermarkPredicate.ValueOption] = canonical,
            [WatermarkPredicate.KindOption] = bound.Kind.ToString(),
        };

        var summary = new IncrementalSummary
        {
            Mode = IncrementalModes.Incremental,
            Filter = $"rows where {column} > {canonical}",
            Watermark = canonical,
            WatermarkSource = $"target MAX {probeTable}",
        };

        return (flow with { Source = flow.Source with { Options = options } }, summary);
    }

    /// <summary>
    /// Wraps the source reader with the row-level watermark filter when the engine injected a row-level bound
    /// (see <see cref="ApplyRowWatermarkAsync"/>); otherwise returns the reader unchanged.
    /// </summary>
    private static DbDataReader WrapWatermarkFilter(SourceSpec source, DbDataReader reader)
    {
        if (!source.Options.TryGetValue(WatermarkPredicate.ColumnOption, out var column) || string.IsNullOrWhiteSpace(column))
        {
            return reader;
        }

        var value = source.Options.TryGetValue(WatermarkPredicate.ValueOption, out var v) ? v ?? string.Empty : string.Empty;
        var kindName = source.Options.TryGetValue(WatermarkPredicate.KindOption, out var k) ? k : null;
        var kind = WatermarkPredicate.ParseKind(kindName!);
        return new WatermarkFilteringDataReader(reader, column, value, kind);
    }

    /// <summary>
    /// The pre-ingestion transform post-process: introspects the just-loaded table (fresh, so evolved columns are
    /// included), profiles it when inference is on, merges the authored transforms over the inferred columns, and
    /// refreshes <c>[schema].[v&lt;Table&gt;]</c> with the resolved projection. The view is what the downstream
    /// chained flow reads, so the resolved columns ride back on the result for the run log and the catalog.
    /// </summary>
    /// <summary>Builds the transformation view's <c>CREATE OR ALTER VIEW</c> DDL and resolved projection without
    /// executing it; the caller traces the statement and runs it (trace-then-execute) so a failure is attributed
    /// to the exact view statement and the live sink observes it before it runs.</summary>
    private async Task<TransformViewResult> BuildTransformViewAsync(FlowDefinition flow, string connectionString, CancellationToken ct)
    {
        var table = await _schema.GetTableSchemaAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException(
                $"The transformation view cannot be generated: {flow.Target.QualifiedName} was not found after the load.");
        var tableColumns = table.Columns.Select(c => c.Name).ToList();

        IReadOnlyList<InferredColumn> inferred = [];
        if (flow.Inference.Enabled)
        {
            // The inference service resolves the connection reference itself (same secret path as the load).
            var report = await _inference.InferAsync(new InferenceRequest
            {
                Connection = flow.Target.Connection,
                Schema = flow.Target.Schema,
                Table = flow.Target.Table,
                Policy = flow.Inference,
            }, ct).ConfigureAwait(false);

            inferred = report.Columns.Select(c => new InferredColumn
            {
                ColumnName = c.ColumnName,
                DataType = c.DataType,
                SelectExpression = c.SelectExpression,
                Converted = c.Converted,
                Style = c.Style,
                NumericFormat = Enum.TryParse<NumericFormat>(c.NumericFormat, out var format) ? format : null,
            }).ToList();
        }

        var resolved = ColumnTransformResolver.Resolve(tableColumns, flow.Inference, inferred);
        var viewName = $"v_{flow.Target.Table}";
        var ddl = TransformViewBuilder.Build(flow.Target.Schema, viewName, flow.Target.Schema, flow.Target.Table, resolved);

        return new TransformViewResult { ViewName = viewName, Ddl = ddl, Columns = resolved };
    }

    /// <param name="flow">The flow to plan.</param>
    /// <param name="context">Per-run state for events and tracing.</param>
    /// <param name="ct">Cancellation for the plan.</param>
    /// <param name="resolvedConnection">The already-resolved target connection string when the caller resolved
    /// it (the run path does, so one run never resolves the same secret twice); null resolves it here.</param>
    private async Task<FlowPlan> PlanCoreAsync(FlowDefinition flow, RunContext context, CancellationToken ct, string? resolvedConnection = null)
    {
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity("flow.plan");
        activity?.SetTag("flow.name", flow.Name);
        activity?.SetTag("flow.id", context.FlowId);
        activity?.SetTag("flow.run_id", context.RunId);

        var connectionString = resolvedConnection ?? await _secrets.ResolveAsync(flow.Target.Connection, ct).ConfigureAwait(false);
        var reader = ResolveReader(flow.Source.Type);

        // An incremental read that selects no files is a no-op, not a failure: RunAsync turns it into a clean
        // zero-row success, so the stage reports it as an outcome. A full load has no such fallback, and there an
        // empty source really is the failure it looks like.
        var sourceColumns = await StageAsync(
            "source.columns",
            context,
            () => reader.GetColumnsAsync(flow.Source, ct),
            benign: ex => ex is NoSourceFilesException && flow.Incremental is { FullLoad: false }).ConfigureAwait(false);
        var desired = DesiredSchemaBuilder.Build(flow.Target, sourceColumns, flow.Schema, _typeMapper);
        var actual = await StageAsync("target.introspect", context, () => _schema.GetTableSchemaAsync(connectionString, flow.Target.Schema, flow.Target.Table, ct)).ConfigureAwait(false);
        var delta = SchemaDiffer.Diff(desired, actual, flow.Schema.Evolve, _typeReconciler);
        var statements = _ddl.Generate(flow.Target, delta);

        _logger.LogInformation(
            "Planned '{Flow}': {Action}; {Adds} column(s) to add; {Widenings} column(s) to widen; {Ddl} DDL statement(s).",
            flow.Name, actual is null ? "create table" : "table exists", delta.ColumnsToAdd.Count, delta.ColumnsToAlter.Count, statements.Count);

        return new FlowPlan
        {
            Flow = flow,
            SourceColumns = sourceColumns,
            Desired = desired,
            Actual = actual,
            Delta = delta,
            DdlStatements = statements,
            Trace = context.Trace,
        };
    }

    /// <summary>
    /// Runs one stage of a flow and reports it to both the trace and the live event stream: start, elapsed time,
    /// optional row count, and outcome. <paramref name="benign"/> recognizes an exception that is a normal
    /// outcome the caller converts into a clean result rather than a stage failure (an incremental read finding
    /// nothing new). A benign throw still propagates and only its reporting changes, so a healthy run shows no
    /// failed stage for work that did exactly what it should.
    /// </summary>
    private async Task<T> StageAsync<T>(
        string operation,
        RunContext context,
        Func<Task<T>> action,
        Func<T, long?>? rows = null,
        Func<Exception, bool>? benign = null)
    {
        using var activity = SqlFlowDiagnostics.ActivitySource.StartActivity(operation);
        Emit(context, $"{operation} started", FlowEventLevel.Trace, operation);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            var result = await action().ConfigureAwait(false);
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
            var rowCount = rows?.Invoke(result);
            activity?.SetTag("rows", rowCount);
            context.Trace.Add(new TraceEntry { Operation = operation, ElapsedMs = elapsedMs, Succeeded = true, Rows = rowCount });
            _logger.LogDebug("Stage '{Stage}' ok in {ElapsedMs:F1} ms.", operation, elapsedMs);
            context.Events.Publish(new FlowEvent
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = context.FlowName,
                Stage = operation,
                Rows = rowCount,
                ElapsedMs = elapsedMs,
                Message = $"{operation} ({elapsedMs:F0} ms{(rowCount is { } r ? $", {r} rows" : string.Empty)})",
            });
            return result;
        }
        catch (Exception ex)
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

            // The stage ran to a definite, correct answer and the caller turns that answer into a clean result.
            // It is reported as the outcome it is, not as a failure: an operator scanning the trace of a
            // successful run must not find an error row explaining why the run was fine.
            if (benign?.Invoke(ex) == true)
            {
                context.Trace.Add(new TraceEntry { Operation = operation, ElapsedMs = elapsedMs, Succeeded = true, Detail = ex.Message });
                context.Events.Publish(new FlowEvent
                {
                    RunId = context.RunId,
                    FlowId = context.FlowId,
                    FlowName = context.FlowName,
                    Stage = operation,
                    ElapsedMs = elapsedMs,
                    Message = $"{operation}: {ex.Message}",
                });
                throw;
            }

            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            context.Trace.Add(new TraceEntry { Operation = operation, ElapsedMs = elapsedMs, Succeeded = false, Detail = ex.Message });
            context.Events.Publish(new FlowEvent
            {
                RunId = context.RunId,
                FlowId = context.FlowId,
                FlowName = context.FlowName,
                Level = FlowEventLevel.Error,
                Stage = operation,
                ElapsedMs = elapsedMs,
                Message = $"{operation} failed: {ex.Message}",
            });
            throw;
        }
    }

    private Task StageAsync(string operation, RunContext context, Func<Task> action)
        => StageAsync(operation, context, async () =>
        {
            await action().ConfigureAwait(false);
            return true;
        });

    private static void Emit(RunContext context, string message, FlowEventLevel level = FlowEventLevel.Info, string? stage = null)
        => context.Events.Publish(new FlowEvent
        {
            RunId = context.RunId,
            FlowId = context.FlowId,
            FlowName = context.FlowName,
            Level = level,
            Stage = stage,
            Message = message,
        });

    private ISourceReader ResolveReader(string sourceType)
        => _sources.FirstOrDefault(s => s.CanHandle(sourceType))
           ?? throw new SqlFlowException($"No source reader is registered for source type '{sourceType}'.");

    /// <summary>Per-run state. A fresh instance per run/plan call keeps concurrent runs isolated.</summary>
    private sealed class RunContext(
        Guid runId, Guid flowId, string flowName, IFlowEventSink events, string? runHistoryDirectory = null)
    {
        public Guid RunId { get; } = runId;
        public Guid FlowId { get; } = flowId;
        public string FlowName { get; } = flowName;

        /// <summary>This run's event sink: the host-wide sink plus any per-run sink the caller attached (the
        /// executor's artifact collector, the node's live catalog writer). Carried on the context so concurrent
        /// runs on the singleton runner never see each other's sinks.</summary>
        public IFlowEventSink Events { get; } = events;

        /// <summary>The directory this flow's on-disk run history is anchored to (the flow document's folder),
        /// so the incremental probe can read the durable last-processed watermark from prior runs. Null when the
        /// caller has no on-disk history (a pure in-memory run), which falls back to the target-table probe.</summary>
        public string? RunHistoryDirectory { get; } = runHistoryDirectory;

        public List<TraceEntry> Trace { get; } = [];
    }
}
