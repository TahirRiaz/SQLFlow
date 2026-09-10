using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Runs;

namespace SqlFlow.SqlServer.Invoke;

/// <summary>
/// Executes a standalone invoke flow (legacy flw.Invoke): dispatch the action, then record the run. It shares the
/// run-log seam with the other flow types, logging the legacy InvokeType code as the FlowType (the legacy trigger
/// logged FlowType = InvokeType) and Process = <c>--&gt;InvokeAlias</c>, with zero row counts. Without-database
/// mode logs nothing; with-database mode writes one flw.SysLog row. The dispatcher never throws, so a failed
/// invoke becomes a failed result and a logged failure; the runner itself never throws (the run-log write is
/// best-effort and never masks the original error).
/// </summary>
public sealed class InvokeFlowRunner
{
    private readonly IInvokeDispatcher _dispatcher;
    private readonly IIngestionRunLog _runLog;

    public InvokeFlowRunner(IInvokeDispatcher dispatcher, IIngestionRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
    }

    public async Task<InvokeResult> RunAsync(InvokeDefinition flow, IngestionRunOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();
        var events = options.Events ?? NullRunEventSink.Instance;

        events.Log(RunLogLevel.Info, "run.start",
            $"invoke '{flow.InvokeAlias}' (flow {flow.FlowId}, type {flow.FlowType})"
            + (flow.PipelineName is not null ? $", pipeline '{flow.PipelineName}'" : string.Empty)
            + (flow.RunbookName is not null ? $", runbook '{flow.RunbookName}'" : string.Empty));

        var result = await _dispatcher.DispatchAsync(flow, options.RunId, ct).ConfigureAwait(false);
        events.Log(RunLogLevel.Info, "run.end", result.Success
            ? $"SUCCESS in {result.DurationSeconds}s{(result.StandardOutput is null ? string.Empty : $": {result.StandardOutput}")}"
            : $"FAILED after {result.DurationSeconds}s: {result.Error}");

        try
        {
            await _runLog.WriteAsync(BuildRecord(flow, options, result), ct).ConfigureAwait(false);
        }
        catch (Exception logEx) when (logEx is not OperationCanceledException)
        {
            // Best-effort: a run-log write failure must not mask the dispatch result, which is returned below.
        }

        return result;
    }

    private static IngestionRunRecord BuildRecord(InvokeDefinition flow, IngestionRunOptions options, InvokeResult result)
        => new()
        {
            RunId = result.RunId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"-->{flow.InvokeAlias}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = result.StartTimeUtc,
            EndTimeUtc = result.EndTimeUtc,
            DurationSeconds = result.DurationSeconds,
            Success = result.Success,
            Error = result.Error,
        };
}
