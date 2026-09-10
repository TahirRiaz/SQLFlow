using System.Data.Common;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Connections;

/// <summary>
/// An <see cref="IConnectionFactory"/> that also declares which <see cref="DataSourceKind"/>s it opens, so a
/// composite can dispatch by kind (the same CanHandle pattern as the connection-string canonicalizers).
/// </summary>
public interface IProviderConnectionFactory : IConnectionFactory
{
    bool CanHandle(DataSourceKind kind);
}

/// <summary>
/// Dispatches <see cref="IConnectionFactory.OpenAsync"/> to the registered provider factory for the
/// connection's kind. An unregistered kind is a clear configuration error, never a null or a silent no-op
/// (the legacy ADO engine returned null for an unknown provider and loaded zero rows; that failure mode is
/// designed out here).
/// </summary>
public sealed class CompositeConnectionFactory : IConnectionFactory
{
    private readonly IReadOnlyList<IProviderConnectionFactory> _factories;

    public CompositeConnectionFactory(IEnumerable<IProviderConnectionFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories.ToList();
    }

    public Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var factory = _factories.FirstOrDefault(f => f.CanHandle(connection.Kind))
            ?? throw new SqlFlowException(
                $"No connection provider is registered for data source kind '{connection.Kind}'. Register the matching provider (for example from SqlFlow.Providers).");
        return factory.OpenAsync(connection, ct);
    }
}

/// <summary>
/// The SQL fragments that differ per source system: identifier quoting, object qualification, date
/// arithmetic, and binary literals. The engine composes every source-side statement (the extract SELECT,
/// the incremental MIN probe, the init-load chunk predicates) through this seam, so MySQL and PostgreSQL
/// sources read with their own syntax while the single ingestion code path stays unchanged. The TARGET side
/// is always SQL Server and never goes through a dialect.
/// </summary>
public interface ISourceSqlDialect
{
    bool CanHandle(DataSourceKind kind);

    /// <summary>Quotes one identifier (brackets, backticks, or double quotes, with the dialect's escaping).</summary>
    string QuoteIdentifier(string identifier);

    /// <summary>Renders the FROM-clause object reference. SQL Server and PostgreSQL use schema.name on the
    /// connection's current database; MySQL has no separate schema concept, so its schema part IS the
    /// database.</summary>
    string QualifyObject(RelationalObject table);

    /// <summary>An expression subtracting whole days (DATEADD / DATE_SUB / interval arithmetic).</summary>
    string DateSubtractDays(string expression, int days);

    /// <summary>A binary literal (0x... for SQL Server and MySQL; bytea hex for PostgreSQL).</summary>
    string FormatBinaryLiteral(byte[] value);

    /// <summary>
    /// Renders a temporal watermark literal for a comparison in the source's own SQL, from the ISO-8601 text
    /// body (no surrounding quotes) and the SQL Server base type it came from (<c>date</c>, <c>datetime</c>,
    /// <c>datetime2</c>, <c>smalldatetime</c>, <c>datetimeoffset</c>, <c>time</c>). SQL Server, MySQL, and
    /// PostgreSQL accept the quoted ISO string directly; Oracle wraps it in <c>TO_DATE</c> /
    /// <c>TO_TIMESTAMP</c> / <c>TO_TIMESTAMP_TZ</c> so the comparison never depends on the session's NLS date
    /// format (a bare ISO string raises ORA-01843 under a non-ISO NLS setting).
    /// </summary>
    string FormatTemporalLiteral(string baseType, string isoBody);
}

/// <summary>
/// Translates one introspected source column's native type into the SQL Server type that will hold it in
/// staging and the target. The contract is strict: an unmappable type THROWS with the column and type named,
/// because a silently mistranslated type produces invalid DDL or corrupted widening decisions downstream
/// (the failure mode of letting a foreign type name fall through the parser).
/// </summary>
public interface ISourceTypeMapper
{
    bool CanHandle(DataSourceKind kind);

    /// <summary>The SQL Server type text (for example <c>nvarchar(255)</c>, <c>decimal(20, 0)</c>) for the
    /// column. Throws <see cref="SqlFlowException"/> for a type with no safe mapping.</summary>
    string ToSqlServerType(CatalogColumn column);
}

/// <summary>
/// An <see cref="ICatalogReader"/> that declares which kinds it introspects, so the composite factory can
/// dispatch by connection kind.
/// </summary>
public interface IProviderCatalogReader : ICatalogReader
{
    bool CanHandle(DataSourceKind kind);
}

/// <summary>Selects the catalog reader by the resolved connection's kind from a registered list.</summary>
public sealed class CompositeCatalogReaderFactory : ICatalogReaderFactory
{
    private readonly IReadOnlyList<IProviderCatalogReader> _readers;

    public CompositeCatalogReaderFactory(IEnumerable<IProviderCatalogReader> readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        _readers = readers.ToList();
    }

    public ICatalogReader ReaderFor(ResolvedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return _readers.FirstOrDefault(r => r.CanHandle(connection.Kind))
            ?? throw new SqlFlowException(
                $"No catalog reader is registered for data source kind '{connection.Kind}'. Register the matching provider (for example from SqlFlow.Providers).");
    }
}

/// <summary>
/// Everything one source provider contributes, bundled so a composition root registers a provider in one
/// move: the connection-string canonicalizer, the connection factory, the catalog reader, the SQL dialect,
/// and the type mapper. SQL Server is built in; MySQL and PostgreSQL ship in the SqlFlow.Providers assembly
/// and are passed into the composition roots through this registry.
/// </summary>
public sealed record SourceProviderRegistry
{
    public IReadOnlyList<IConnectionStringCanonicalizer> Canonicalizers { get; init; } = [];

    public IReadOnlyList<IProviderConnectionFactory> ConnectionFactories { get; init; } = [];

    public IReadOnlyList<IProviderCatalogReader> CatalogReaders { get; init; } = [];

    public IReadOnlyList<ISourceSqlDialect> Dialects { get; init; } = [];

    public IReadOnlyList<ISourceTypeMapper> TypeMappers { get; init; } = [];

    /// <summary>Concatenates two registries (the built-in SQL Server set first, then the add-ons).</summary>
    public SourceProviderRegistry Concat(SourceProviderRegistry other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return new SourceProviderRegistry
        {
            Canonicalizers = [.. Canonicalizers, .. other.Canonicalizers],
            ConnectionFactories = [.. ConnectionFactories, .. other.ConnectionFactories],
            CatalogReaders = [.. CatalogReaders, .. other.CatalogReaders],
            Dialects = [.. Dialects, .. other.Dialects],
            TypeMappers = [.. TypeMappers, .. other.TypeMappers],
        };
    }
}
