namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Loads a complete <see cref="IngestionFlow"/> from full-mode storage. Returning <see cref="IngestionFlow"/>
/// (not a storage-shaped row) is the seam the storage format never crosses: the current SQL loader reads the
/// normalized flw.Ingestion through the lossless mapper, and a future document loader (the same pipeline-as-code
/// YAML used in without-database mode) is a one-registration swap with no change to the runner.
/// </summary>
public interface IFlowLoader
{
    Task<IngestionFlow> LoadByIdAsync(int flowId, CancellationToken ct = default);

    Task<IngestionFlow> LoadByAliasAsync(string sysAlias, CancellationToken ct = default);

    /// <summary>Loads the active flows of a batch, ordered by BatchOrderBy then FlowID.</summary>
    Task<IReadOnlyList<IngestionFlow>> LoadBatchAsync(string batch, CancellationToken ct = default);
}
