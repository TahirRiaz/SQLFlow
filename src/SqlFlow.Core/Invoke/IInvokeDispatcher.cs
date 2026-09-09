namespace SqlFlow.Core.Invoke;

/// <summary>
/// Routes an <see cref="InvokeDefinition"/> to the executor that handles its type, times the call, and returns a
/// uniform <see cref="InvokeResult"/>. It never throws for an execution failure or an unsupported type: both are
/// reported on the result (Success = false, with a precise Error), so the standalone invoke runner can log them
/// and the hook path can re-raise them. Logging and run-recording are the caller's responsibility.
/// </summary>
public interface IInvokeDispatcher
{
    /// <param name="definition">The invoke action to route and run.</param>
    /// <param name="assignedRunId">An orchestrator-assigned run id to stamp on the result instead of minting one;
    /// the top-level invoke flow supplies the control-plane run id so the recorded run resolves under it. Null
    /// mints a fresh id, which is what a nested pre/post-invoke hook does (it owns its own id).</param>
    /// <param name="ct">Cancellation for the dispatch.</param>
    Task<InvokeResult> DispatchAsync(InvokeDefinition definition, Guid? assignedRunId = null, CancellationToken ct = default);
}
