namespace SqlFlow.Core.Invoke;

/// <summary>
/// Executes one kind of invoke (one <see cref="InvokeType"/>). The dispatcher owns a set of executors and routes
/// each definition to the first that <see cref="CanHandle"/>s its type, so an Azure Data Factory or Azure
/// Automation executor is a single registration with no change to the dispatcher or the flow runners. An executor
/// triggers a named external resource and throws on failure with a precise, secret-free message (a missing
/// pipeline / runbook, an authentication failure, a non-success run status); it never fabricates success.
/// </summary>
public interface IInvokeExecutor
{
    /// <summary>True when this executor runs the given invoke type.</summary>
    bool CanHandle(InvokeType type);

    /// <summary>Runs the action and returns its captured output (typically a run id and final status). Throws on
    /// failure so the dispatcher records a failed run.</summary>
    Task<InvokeExecution> ExecuteAsync(InvokeDefinition definition, CancellationToken ct = default);
}
