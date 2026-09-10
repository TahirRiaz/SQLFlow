namespace SqlFlow.Core.Invoke;

/// <summary>Loads invoke flows from full-mode storage (the V3-native flw.Invoke table).</summary>
public interface IInvokeFlowLoader
{
    Task<InvokeDefinition> LoadByIdAsync(int flowId, CancellationToken ct = default);

    /// <summary>Loads the invoke flow with the given unique InvokeAlias (the value Pre/PostInvokeAlias hooks
    /// reference).</summary>
    Task<InvokeDefinition> LoadByAliasAsync(string invokeAlias, CancellationToken ct = default);

    /// <summary>Loads the active invoke flows of a batch, ordered by FlowID.</summary>
    Task<IReadOnlyList<InvokeDefinition>> LoadBatchAsync(string batch, CancellationToken ct = default);
}
