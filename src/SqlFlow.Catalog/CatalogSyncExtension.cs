namespace SqlFlow.Catalog;

/// <summary>What one sync extension did to its document family in one repository sync.</summary>
public sealed record CatalogSyncExtensionResult(int Added, int Updated, int Unchanged, int Removed, int Invalid)
{
    /// <summary>Nothing reconciled.</summary>
    public static CatalogSyncExtensionResult Empty { get; } = new(0, 0, 0, 0, 0);

    /// <summary>The two tallies summed.</summary>
    public CatalogSyncExtensionResult Add(CatalogSyncExtensionResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new CatalogSyncExtensionResult(
            Added + other.Added, Updated + other.Updated, Unchanged + other.Unchanged, Removed + other.Removed,
            Invalid + other.Invalid);
    }
}

/// <summary>
/// A document family a host module adds to the repository sync beyond flows and schedules: documents a registered
/// flow kind owns beside its flows, reconciled into the module's own rows. The sync calls every registered extension
/// inside its reconciliation transaction, after the pipelines, runs and lineage, with the repository's materialized
/// root; the extension reconciles its rows for that repository through the same connection and transaction and
/// reports the tally. A document-level problem is reported as a warning; an exception fails the whole sync and rolls
/// every write back, so the catalog never shows a repository half reconciled. Extensions are registered in the host's
/// composition root, so the catalog never depends on the modules that exist.
/// </summary>
public interface ICatalogSyncExtension
{
    /// <param name="context">The sync's catalog context, inside its transaction. An extension that keeps its rows in a
    /// context of its own enlists that context in this one's connection and transaction.</param>
    /// <param name="repoId">The repository being synced.</param>
    /// <param name="root">The repository's materialized root directory.</param>
    /// <param name="nowUtc">The sync's timestamp.</param>
    /// <param name="warnings">Where a document-level problem is reported without failing the sync.</param>
    /// <param name="ct">Cancellation.</param>
    Task<CatalogSyncExtensionResult> SyncAsync(
        CatalogDbContext context, Guid repoId, string root, DateTime nowUtc, ICollection<string> warnings, CancellationToken ct);

    /// <summary>
    /// Whether a document this extension owns changed, since its rows were last reconciled, in a way the lineage of the
    /// repository's flows depends on: a companion document a registered flow reads while describing its lineage. The
    /// sync asks before it takes its unchanged-estate shortcut, outside its transaction, and recomputes lineage when
    /// any extension says yes. The default is no. An exception fails the sync.
    /// </summary>
    /// <param name="context">The sync's catalog context; an extension reads its own rows through a context of its own on
    /// the same connection.</param>
    /// <param name="repoId">The repository being synced.</param>
    /// <param name="root">The repository's materialized root directory.</param>
    /// <param name="ct">Cancellation.</param>
    Task<bool> LineageInputsChangedAsync(CatalogDbContext context, Guid repoId, string root, CancellationToken ct)
        => Task.FromResult(false);
}
