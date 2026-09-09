using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Sftp;

namespace SqlFlow.Sftp;

/// <summary>
/// Executes an SFTP flow (flowType: sftp): runs the engine and logs the run boundary through the shared run-log seam.
/// The one code path for CLI, control plane, and worker. Never throws for a transfer failure.
/// </summary>
public sealed class SftpFlowRunner
{
    private readonly SftpEngine _engine;

    public SftpFlowRunner(SftpEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
    }

    public async Task<SftpRunResult> RunAsync(SftpFlow flow, IngestionRunOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(options);
        var events = options.Events ?? NullRunEventSink.Instance;

        var where = flow.Steps.Count == 1
            ? $"{flow.Server.Host}:{flow.Server.Port}{flow.Steps[0].RemotePath} <-> {flow.Steps[0].Local}"
            : $"{flow.Server.Host}:{flow.Server.Port} ({flow.Steps.Count} steps)";
        events.Log(RunLogLevel.Info, "run.start", $"sftp '{flow.Name}' ({flow.Direction}) {where}");

        var runId = options.RunId ?? Guid.NewGuid();
        var result = await _engine.RunAsync(flow, runId, events, ct, options.Parameters).ConfigureAwait(false);

        var skippedNote = result.FilesSkipped > 0 ? $", {result.FilesSkipped} unchanged" : string.Empty;
        events.Log(RunLogLevel.Info, "run.end", result.Success
            ? $"SUCCESS in {result.DurationSeconds}s: {result.FilesTransferred} file(s), {result.BytesTransferred} byte(s) from {result.Matched} matched{skippedNote}"
            : $"FAILED after {result.DurationSeconds}s ({result.FilesTransferred} transferred before failure): {result.Error}");

        return result;
    }
}
