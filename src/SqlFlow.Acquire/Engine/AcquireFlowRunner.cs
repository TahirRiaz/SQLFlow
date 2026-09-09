using SqlFlow.Core.Acquire;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Executes an acquisition flow (flowType: acq): resolves the incremental watermark from the run history, runs the
/// engine, and logs the run boundary through the shared run-log seam. It never throws for a fetch failure - the
/// engine returns a failed/partial result - so a batch member behaves exactly like a directly-invoked flow. The one
/// code path for CLI, control plane, and worker.
/// </summary>
public sealed class AcquireFlowRunner
{
    private readonly AcquireEngine _engine;

    public AcquireFlowRunner(AcquireEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<AcquireRunResult> RunAsync(
        AcquireFlow flow, string anchorDirectory, IngestionRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentException.ThrowIfNullOrWhiteSpace(anchorDirectory);
        ArgumentNullException.ThrowIfNull(options);
        var events = options.Events ?? NullRunEventSink.Instance;

        events.Log(RunLogLevel.Info, "run.start",
            flow.Items.Count == 1
                ? $"acquire '{flow.Name}' (flow {flow.FlowId}, transport {flow.Source.Transport}) -> {flow.Items[0].Landing.Target}"
                : $"acquire '{flow.Name}' (flow {flow.FlowId}, transport {flow.Source.Transport}) -> {flow.Items.Count} items");

        // The typed per-run contract maps onto the acquisition's native knobs. A reprocess (a full load OR a backfill
        // window) ignores the stored watermark so the flow re-fetches from its declared bounds / the window: without
        // this, a flow whose incremental is a bind-variable watermark (not a date window) would stay capped at the
        // last fetched point and a backfill window could never reach historical data. A backfill window then also
        // re-windows every date-window iteration (below). A file pattern has no acquisition meaning.
        var parameters = options.Parameters;
        var priorWatermark = flow.Incremental is not null && !parameters.ReprocessFiles
            ? AcquireWatermarkHistory.LastWatermark(anchorDirectory, flow.Name)
            : null;
        if (parameters.ReprocessFiles && flow.Incremental is not null)
        {
            events.Log(RunLogLevel.Info, "params",
                parameters.FullLoad
                    ? "full load requested: the stored watermark is ignored for this run."
                    : "backfill window requested: the stored watermark is ignored so the window can reach historical data.");
        }

        var overrides = new AcquireRunOverrides
        {
            WindowFrom = parameters.BackfillFrom is { } from ? new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)) : null,
            WindowTo = parameters.BackfillTo is { } to ? new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)) : null,
            // A backfill re-lands every re-fetched file (fresh timestamp) rather than skipping byte-identical payloads,
            // so the downstream incremental flows pick them up again.
            ReprocessFiles = parameters.ReprocessFiles,
        };

        var runId = options.RunId ?? Guid.NewGuid();
        var result = await _engine.RunAsync(flow, runId, events, priorWatermark, ct, overrides).ConfigureAwait(false);

        // The summary has to distinguish "the source produced new data" from "the source produced the same data
        // again", because a rolling-window feed re-fetches the same days every run and lands them byte-identical.
        // Quoting the landed count alone made a run that wrote nothing read as new files arriving, and the flows
        // downstream (which correctly saw no new files and loaded no rows) then looked broken by comparison.
        var newFiles = result.FilesWritten - result.Unchanged;
        var landedNote = result.Unchanged > 0
            ? $"{newFiles} new file(s), {result.Unchanged} unchanged"
            : $"{result.FilesWritten} file(s)";
        events.Log(RunLogLevel.Info, "run.end", result.Success
            ? $"SUCCESS in {result.DurationSeconds}s: {landedNote}, {result.BytesWritten} byte(s), {result.PagesFetched} page(s) across {result.Iterations} iteration(s)"
            : $"FAILED after {result.DurationSeconds}s ({result.FilesWritten} file(s) landed before failure): {result.Error}");

        return result;
    }
}
