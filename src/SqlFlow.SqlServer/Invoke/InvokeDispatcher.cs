using SqlFlow.Core.Invoke;

namespace SqlFlow.SqlServer.Invoke;

/// <summary>
/// The default <see cref="IInvokeDispatcher"/>: holds the registered <see cref="IInvokeExecutor"/>s and routes a
/// definition to the first that handles its type. The call is timed and wrapped in an <see cref="InvokeResult"/>;
/// an execution failure (the executor throws) and an unsupported type (no executor registered) are both reported
/// as a failed result with a precise Error rather than thrown, so the caller logs once and decides whether to
/// re-raise. The set of executors is the extension point: registering an Azure Data Factory or Azure Automation
/// executor enables that type with no change here or in the flow runners.
/// </summary>
public sealed class InvokeDispatcher : IInvokeDispatcher
{
    private readonly IReadOnlyList<IInvokeExecutor> _executors;

    public InvokeDispatcher(IEnumerable<IInvokeExecutor> executors)
    {
        ArgumentNullException.ThrowIfNull(executors);
        _executors = executors.ToList();
    }

    public async Task<InvokeResult> DispatchAsync(InvokeDefinition definition, Guid? assignedRunId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var runId = assignedRunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;

        var executor = _executors.FirstOrDefault(e => e.CanHandle(definition.InvokeType));
        if (executor is null)
        {
            var endUtc = DateTime.UtcNow;
            return Failed(
                definition,
                runId,
                startUtc,
                endUtc,
                $"InvokeType '{definition.FlowType}' is not supported by this host: no executor is registered for it. " +
                "Register the matching IInvokeExecutor (an Azure Data Factory or Azure Automation executor) to enable it.");
        }

        try
        {
            var execution = await executor.ExecuteAsync(definition, ct).ConfigureAwait(false);
            var endUtc = DateTime.UtcNow;
            return new InvokeResult
            {
                RunId = runId,
                FlowId = definition.FlowId,
                InvokeAlias = definition.InvokeAlias,
                InvokeType = definition.InvokeType,
                Success = true,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = DurationSeconds(startUtc, endUtc),
                StandardOutput = execution.StandardOutput,
                StandardError = execution.StandardError,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var endUtc = DateTime.UtcNow;
            return Failed(definition, runId, startUtc, endUtc, ex.Message);
        }
    }

    private static InvokeResult Failed(InvokeDefinition definition, Guid runId, DateTime startUtc, DateTime endUtc, string error)
        => new()
        {
            RunId = runId,
            FlowId = definition.FlowId,
            InvokeAlias = definition.InvokeAlias,
            InvokeType = definition.InvokeType,
            Success = false,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = DurationSeconds(startUtc, endUtc),
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);
}
