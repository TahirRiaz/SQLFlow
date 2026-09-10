using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Catalog;

namespace SqlFlow.SqlServer;

/// <summary>
/// The T-SQL source dialect: bracket quoting with <c>]]</c> escaping, schema.name qualification on the
/// connection's current database, DATEADD date arithmetic, and 0x binary literals. Extracted verbatim from
/// the engine's previous inline helpers, so SQL Server source behavior is byte-identical to before the
/// dialect seam existed.
/// </summary>
public sealed class SqlServerSourceDialect : ISourceSqlDialect
{
    public bool CanHandle(DataSourceKind kind) => kind is DataSourceKind.MSSQL or DataSourceKind.AZDB;

    public string QuoteIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    public string QualifyObject(RelationalObject table)
    {
        ArgumentNullException.ThrowIfNull(table);
        return $"{QuoteIdentifier(table.Schema)}.{QuoteIdentifier(table.Name)}";
    }

    public string DateSubtractDays(string expression, int days) => $"DATEADD(day, -{days}, {expression})";

    public string FormatBinaryLiteral(byte[] value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return "0x" + Convert.ToHexString(value);
    }

    // T-SQL accepts an ISO-8601 date/time string literal directly in a comparison.
    public string FormatTemporalLiteral(string baseType, string isoBody)
    {
        ArgumentNullException.ThrowIfNull(isoBody);
        return $"'{isoBody}'";
    }
}

/// <summary>
/// The SQL Server source type mapper: the introspected NativeType is already rendered in T-SQL by the SQL
/// Server catalog reader, so the mapping is the identity.
/// </summary>
public sealed class SqlServerSourceTypeMapper : ISourceTypeMapper
{
    public bool CanHandle(DataSourceKind kind) => kind is DataSourceKind.MSSQL or DataSourceKind.AZDB;

    public string ToSqlServerType(CatalogColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);
        return column.NativeType;
    }
}

/// <summary>The built-in SQL Server provider set, the base every composition root starts from.</summary>
public static class SqlServerSourceProvider
{
    public static SourceProviderRegistry CreateRegistry()
        => new()
        {
            Canonicalizers = [new SqlConnectionStringCanonicalizer()],
            ConnectionFactories = [new SqlConnectionFactory()],
            CatalogReaders = [new SqlServerCatalogReader()],
            Dialects = [new SqlServerSourceDialect()],
            TypeMappers = [new SqlServerSourceTypeMapper()],
        };
}
