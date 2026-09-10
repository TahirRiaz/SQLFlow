namespace SqlFlow.Core.StoredProcedures;

/// <summary>Loads stored-procedure flows from full-mode storage (the V3-native flw.StoredProcedure table).</summary>
public interface IStoredProcedureFlowLoader
{
    Task<StoredProcedureFlow> LoadByIdAsync(int flowId, CancellationToken ct = default);

    /// <summary>Loads the active stored-procedure flows of a batch, ordered by FlowID.</summary>
    Task<IReadOnlyList<StoredProcedureFlow>> LoadBatchAsync(string batch, CancellationToken ct = default);
}
