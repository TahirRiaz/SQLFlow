using System.Data.Common;
using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Catalog;

/// <summary>
/// The single entry point for source discovery, shared by the CLI and the API. For every call it resolves a
/// connection reference (an <c>@alias</c> in full mode, or an inline / <c>${...}</c> reference) through the
/// registry, opens the connection through the same factory the engine ingests with, selects the provider's
/// reader, and returns DTOs. This is the only place that knows the resolve-open-read sequence.
/// </summary>
public sealed class CatalogService
{
    private readonly IConnectionResolver _resolver;
    private readonly IConnectionFactory _factory;
    private readonly ICatalogReaderFactory _readers;

    public CatalogService(IConnectionResolver resolver, IConnectionFactory factory, ICatalogReaderFactory readers)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(readers);
        _resolver = resolver;
        _factory = factory;
        _readers = readers;
    }

    public Task<IReadOnlyList<DatabaseInfo>> DatabasesAsync(string reference, CatalogQuery query, DataSourceKind? kind = null, CancellationToken ct = default)
        => RunAsync(reference, kind, (reader, connection) => reader.ListDatabasesAsync(connection, query, ct), ct);

    public Task<IReadOnlyList<SchemaInfo>> SchemasAsync(string reference, string? database, CatalogQuery query, DataSourceKind? kind = null, CancellationToken ct = default)
        => RunAsync(reference, kind, (reader, connection) => reader.ListSchemasAsync(connection, database, query, ct), ct);

    public Task<CatalogPage<ObjectInfo>> ObjectsAsync(string reference, ObjectScope scope, CatalogQuery query, DataSourceKind? kind = null, CancellationToken ct = default)
        => RunAsync(reference, kind, (reader, connection) => reader.ListObjectsAsync(connection, scope, query, ct), ct);

    public Task<IReadOnlyList<ObjectMatch>> SearchAsync(string reference, string? database, string term, DataSourceKind? kind = null, CancellationToken ct = default)
        => RunAsync(reference, kind, (reader, connection) => reader.SearchObjectsAsync(connection, database, term, ct), ct);

    public Task<CatalogObject?> IntrospectAsync(string reference, ThreePartName name, DataSourceKind? kind = null, CancellationToken ct = default)
        => RunAsync(reference, kind, (reader, connection) => reader.IntrospectObjectAsync(connection, name, ct), ct);

    private async Task<T> RunAsync<T>(string reference, DataSourceKind? kind, Func<ICatalogReader, DbConnection, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var resolved = await _resolver.ResolveAsync(reference, ConnectionRole.Source, kind, ct).ConfigureAwait(false);
        var reader = _readers.ReaderFor(resolved);
        await using var connection = await _factory.OpenAsync(resolved, ct).ConfigureAwait(false);
        return await operation(reader, connection).ConfigureAwait(false);
    }
}
