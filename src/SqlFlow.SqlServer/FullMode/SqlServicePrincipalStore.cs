using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>
/// The full-mode (with control database) service-principal registry: resolves an <c>@alias</c> to a
/// <see cref="ServicePrincipalProfile"/> from flw.ServicePrincipal. Reporting <see cref="SupportsAliases"/> as
/// true is what lets a resolver leave lightweight mode. Plain coordinates are returned verbatim; the
/// <c>ClientSecretRef</c> is returned for the resolver to expand, but a read-side refusal first catches a row
/// that bypassed the schema CHECK (or predates it) with a resting secret, naming the alias and column.
/// </summary>
public sealed partial class SqlServicePrincipalStore : IServicePrincipalStore
{
    private readonly string _controlConnectionString;

    public SqlServicePrincipalStore(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public bool SupportsAliases => true;

    public async Task<ServicePrincipalProfile> ResolveAsync(string aliasName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);

        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(Query, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@alias", System.Data.SqlDbType.NVarChar, 128) { Value = aliasName });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new ServicePrincipalNotFoundException(aliasName);
        }

        var clientSecretRef = GetStringOrNull(reader, "ClientSecretRef");

        // Gate on read: the only secret-bearing column may rest ONLY as a whole ${...} reference.
        if (clientSecretRef is not null && !IsWholeSecretReference(clientSecretRef))
        {
            throw new SecretlessViolationException(aliasName, "ClientSecretRef");
        }

        return new ServicePrincipalProfile
        {
            Alias = aliasName,
            TenantId = GetStringOrNull(reader, "TenantId"),
            ClientId = GetStringOrNull(reader, "ClientId"),
            ClientSecretRef = clientSecretRef,
            SubscriptionId = GetStringOrNull(reader, "SubscriptionId"),
            ResourceGroup = GetStringOrNull(reader, "ResourceGroup"),
            DataFactoryName = GetStringOrNull(reader, "DataFactoryName"),
            AutomationAccountName = GetStringOrNull(reader, "AutomationAccountName"),
            KeyVaultName = GetStringOrNull(reader, "KeyVaultName"),
        };
    }

    private static string? GetStringOrNull(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool IsWholeSecretReference(string value) => WholeReference().IsMatch(value);

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();

    private const string Query =
        "SELECT [TenantId],[ClientId],[ClientSecretRef],[SubscriptionId],[ResourceGroup]," +
        "[DataFactoryName],[AutomationAccountName],[KeyVaultName] " +
        "FROM [flw].[ServicePrincipal] WHERE [ServicePrincipalAlias] = @alias;";
}
