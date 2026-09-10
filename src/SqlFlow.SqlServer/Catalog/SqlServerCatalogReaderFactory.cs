using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.Catalog;

/// <summary>Returns the SQL Server catalog reader for MSSQL and AZDB connections. A MySQL reader is added
/// when the MySQL provider lands.</summary>
public sealed class SqlServerCatalogReaderFactory : ICatalogReaderFactory
{
    private readonly SqlServerCatalogReader _reader = new();

    public ICatalogReader ReaderFor(ResolvedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        return connection.Kind is DataSourceKind.MSSQL or DataSourceKind.AZDB
            ? _reader
            : throw new SqlFlowException($"No catalog reader is available for data source kind '{connection.Kind}' (only SQL Server is supported so far).");
    }
}
