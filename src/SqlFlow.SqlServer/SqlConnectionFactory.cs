using System.Data.Common;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer;

/// <summary>
/// Opens a pooled SQL Server connection from a <see cref="ResolvedConnection"/>. The canonical string carries
/// the authentication (passwordless Active Directory keywords or an expanded secret), so the common path just
/// opens it; ADO.NET owns token acquisition and pooling. A non-SQL-Server kind reaching this factory directly
/// (instead of through the kind-dispatching composite) and the cross-tenant injected-token path fail fast with
/// a precise message rather than a stub.
/// </summary>
public sealed class SqlConnectionFactory : IProviderConnectionFactory
{
    public bool CanHandle(DataSourceKind kind) => kind is DataSourceKind.MSSQL or DataSourceKind.AZDB;

    public async Task<DbConnection> OpenAsync(ResolvedConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (!CanHandle(connection.Kind))
        {
            throw new SqlFlowException(
                $"The SQL Server connection factory cannot open a '{connection.Kind}' connection ('{connection.RedactedString}'). " +
                "Register the matching provider (for example from SqlFlow.Providers).");
        }

        if (connection.Credential.Mode == CredentialMode.InjectedToken)
        {
            throw new SqlFlowException(
                $"Cross-tenant injected-token authentication ('{connection.RedactedString}') is not yet supported; use Active Directory connection-string auth or a ${{...}} secret reference.");
        }

        var sqlConnection = new SqlConnection(connection.CanonicalString);
        try
        {
            await sqlConnection.OpenAsync(ct).ConfigureAwait(false);
            return sqlConnection;
        }
        catch
        {
            await sqlConnection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
