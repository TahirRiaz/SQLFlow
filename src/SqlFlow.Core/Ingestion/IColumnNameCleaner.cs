namespace SqlFlow.Core.Ingestion;

/// <summary>
/// Cleans raw source column names for the target according to a <see cref="SchemaSyncPolicy"/> (legacy
/// OnSyncCleanColumnName family). Dialect-agnostic: it transforms names only. Returns names index-aligned to
/// the input so the caller can build the source-to-target column map for the bulk copy.
/// </summary>
public interface IColumnNameCleaner
{
    /// <summary>
    /// Returns cleaned target names aligned by index to <paramref name="rawNames"/>: applies the policy's
    /// cleanup regex and replacement, maps an empty result to a placeholder, and de-duplicates collisions.
    /// When cleaning is disabled the raw names are returned unchanged.
    /// </summary>
    IReadOnlyList<string> Clean(IReadOnlyList<string> rawNames, SchemaSyncPolicy policy);
}
