using SqlFlow.Core.Compute;

namespace SqlFlow.Execution;

/// <summary>
/// An ad-hoc operation a host module adds to the compute task queue beside SQLFlow's built-in datasource operations: a
/// module's own probe or read-back, run near the data on whichever node claims the task. It is registered in the host's
/// composition root and dispatched by <see cref="Name"/>, so the execution layer never references the module. The
/// module enqueues its tasks through its own endpoints (with its own authorization and validation), and a client polls
/// them through SQLFlow's compute task endpoints like any other task.
/// </summary>
public interface IComputeOperation
{
    /// <summary>The operation name a task carries: camelCase letters, digits and '-', distinct from every built-in
    /// operation name (<see cref="ComputeOperations.IsValidRegisteredName"/>).</summary>
    string Name { get; }

    /// <summary>
    /// Runs the task and returns its result as one JSON document, which the queue stores and serves back. The payload's
    /// <see cref="ComputeTaskPayload.Arguments"/> have been bounds-checked; what they mean is validated here. A refusal or
    /// failure is a <see cref="SqlFlow.Core.SqlFlowException"/> (or the underlying exception), recorded redacted on the
    /// task. The token trips when an operator cancels the task or the node shuts down.
    /// </summary>
    Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct);
}
