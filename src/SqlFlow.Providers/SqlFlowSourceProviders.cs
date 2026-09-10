using SqlFlow.Core.Connections;
using SqlFlow.Providers.MySql;
using SqlFlow.Providers.Oracle;
using SqlFlow.Providers.Postgres;

namespace SqlFlow.Providers;

/// <summary>
/// The MySQL, PostgreSQL, and Oracle provider bundle. A composition root (the CLI, a host builder, a test)
/// appends this to the built-in SQL Server set so non-SQL-Server SOURCES resolve, open, introspect, and read
/// with their own canonicalizer, connection factory, catalog reader, SQL dialect, and type mapper. The target
/// side of every flow remains SQL Server by design.
/// </summary>
public static class SqlFlowSourceProviders
{
    public static SourceProviderRegistry CreateRegistry()
        => new()
        {
            Canonicalizers = [new MySqlConnectionStringCanonicalizer(), new PostgresConnectionStringCanonicalizer(), new OracleConnectionStringCanonicalizer()],
            ConnectionFactories = [new MySqlProviderConnectionFactory(), new PostgresProviderConnectionFactory(), new OracleProviderConnectionFactory()],
            CatalogReaders = [new MySqlCatalogReader(), new PostgresCatalogReader(), new OracleCatalogReader()],
            Dialects = [new MySqlSourceDialect(), new PostgresSourceDialect(), new OracleSourceDialect()],
            TypeMappers = [new MySqlSourceTypeMapper(), new PostgresSourceTypeMapper(), new OracleSourceTypeMapper()],
        };
}
