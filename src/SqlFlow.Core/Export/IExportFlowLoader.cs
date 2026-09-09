namespace SqlFlow.Core.Export;

/// <summary>Loads export flows from full-mode storage (the V3-native flw.Export table).</summary>
public interface IExportFlowLoader
{
    Task<ExportFlow> LoadByIdAsync(int flowId, CancellationToken ct = default);

    /// <summary>Loads the active export flows of a batch, ordered by FlowID.</summary>
    Task<IReadOnlyList<ExportFlow>> LoadBatchAsync(string batch, CancellationToken ct = default);
}
