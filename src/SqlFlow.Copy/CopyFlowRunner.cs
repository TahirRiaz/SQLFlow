using SqlFlow.Core.Copy;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Copy;

/// <summary>
/// Executes a copy flow (flowType: cpy): runs the engine and logs the run boundary through the shared run-log seam.
/// The one code path for CLI, control plane, and worker. It never throws for a transfer failure - the engine returns
/// a failed/partial result - so a batch member behaves exactly like a directly-invoked flow.
/// </summary>
public sealed class CopyFlowRunner
{
    private readonly CopyEngine _engine;

    public CopyFlowRunner(CopyEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<CopyRunResult> RunAsync(CopyFlow flow, IngestionRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(options);
        var events = options.Events ?? NullRunEventSink.Instance;

        var summary = flow.Steps.Count == 1
            ? $"{flow.Steps[0].Source.Location} -> {flow.Steps[0].Target.Location}"
            : $"{flow.Steps.Count} steps";
        var parameters = options.Parameters;
        var paramNote = parameters.IsDefault ? string.Empty : $" [parameters: {parameters.Describe()}]";
        events.Log(RunLogLevel.Info, "run.start", $"copy '{flow.Name}' ({flow.Operation}) {summary}{paramNote}");

        var runId = options.RunId ?? Guid.NewGuid();
        var result = await _engine.RunAsync(flow, runId, events, ct, parameters).ConfigureAwait(false);

        var skippedNote = result.FilesSkipped > 0 ? $", {result.FilesSkipped} unchanged" : string.Empty;
        events.Log(RunLogLevel.Info, "run.end", result.Success
            ? $"SUCCESS in {result.DurationSeconds}s: {result.FilesWritten} file(s), {result.BytesWritten} byte(s) from {result.Matched} matched{skippedNote}"
            : $"FAILED after {result.DurationSeconds}s ({result.FilesWritten} written before failure): {result.Error}");

        return result;
    }
}
