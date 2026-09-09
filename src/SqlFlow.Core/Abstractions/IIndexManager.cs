namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Manages a pre-existing target's indexes around a bulk load. Non-clustered indexes slow bulk loads,
/// so the engine disables them before loading and rebuilds (re-enables) them afterwards. Disable keeps
/// the full index definition in the catalog, so nothing has to be scripted or recreated by hand, and
/// no SMO is involved. Clustered, primary-key, and unique-constraint indexes are left intact (a
/// disabled clustered index would make the table inaccessible).
/// </summary>
public interface IIndexManager
{
    /// <summary>
    /// Disables the target's droppable (plain non-clustered) indexes so the load runs unindexed, and
    /// returns the names of the indexes disabled so they can be rebuilt after the load.
    /// </summary>
    Task<IReadOnlyList<string>> DisableNonClusteredAsync(string connectionString, string schema, string table, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds (re-enables) the named indexes. Names that no longer exist (e.g. the table was
    /// recreated) are skipped, so this is safe to call after any load.
    /// </summary>
    Task RebuildAsync(string connectionString, string schema, string table, IReadOnlyList<string> indexNames, CancellationToken ct = default);
}
