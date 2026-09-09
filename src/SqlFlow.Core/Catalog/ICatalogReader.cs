using System.Data.Common;

namespace SqlFlow.Core.Catalog;

/// <summary>
/// The single schema-reading abstraction shared by source discovery and the schema-sync engine. Every method
/// runs over a caller-supplied open <see cref="DbConnection"/> obtained from the connection registry, so it
/// inherits the firewall-permitted egress and the resolved credentials and never sees a connection string.
/// Provider-neutral: a SQL Server reader and a MySQL reader implement it. The sync engine consumes only
/// <see cref="IntrospectObjectAsync"/>; discovery uses the enumeration methods too.
/// </summary>
public interface ICatalogReader
{
    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(DbConnection connection, CatalogQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(DbConnection connection, string? database, CatalogQuery query, CancellationToken ct = default);

    Task<CatalogPage<ObjectInfo>> ListObjectsAsync(DbConnection connection, ObjectScope scope, CatalogQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<ObjectMatch>> SearchObjectsAsync(DbConnection connection, string? database, string term, CancellationToken ct = default);

    /// <summary>Full structured introspection of one table or view, or null when it does not exist.</summary>
    Task<CatalogObject?> IntrospectObjectAsync(DbConnection connection, ThreePartName name, CancellationToken ct = default);
}
