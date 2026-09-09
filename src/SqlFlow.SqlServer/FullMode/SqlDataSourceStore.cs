using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>
/// The full-mode (with control database) data-source registry: resolves an <c>@alias</c> to a
/// <see cref="DataSource"/> from flw.DataSource (joined to flw.CredentialProfile). Reporting
/// <see cref="SupportsAliases"/> as true is what flips <see cref="ConnectionResolver"/> out of lightweight mode.
/// It returns the stored ConnectionRef verbatim (the resolver expands any <c>${...}</c> reference and the
/// canonicalizer is the authoritative secretless gate), but first runs a read-side refusal so a row that
/// bypassed the schema CHECK (or predates it) with a resting secret is caught here, before the value reaches
/// the resolver, with the alias and column named.
/// </summary>
public sealed partial class SqlDataSourceStore : IDataSourceStore
{
    private readonly string _controlConnectionString;

    public SqlDataSourceStore(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public bool SupportsAliases => true;

    public async Task<DataSource> ResolveAsync(string aliasName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(aliasName);

        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(Query, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@alias", System.Data.SqlDbType.NVarChar, 128) { Value = aliasName });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new DataSourceNotFoundException(aliasName);
        }

        var kindText = reader.GetString(reader.GetOrdinal("Kind"));
        if (!Enum.TryParse<DataSourceKind>(kindText, ignoreCase: false, out var kind))
        {
            throw new SqlFlowException($"Data source '{aliasName}' has an unknown Kind '{kindText}'. Valid values: MSSQL, AZDB, MySQL, PostgreSQL, Oracle.");
        }

        var connectionRef = reader.GetString(reader.GetOrdinal("ConnectionRef"));

        // Gate on read: a literal (non-${...}) connection string must not carry a resting secret.
        if (!IsWholeSecretReference(connectionRef) && ContainsRestingSecret(connectionRef))
        {
            throw new SecretlessViolationException(aliasName, "ConnectionRef");
        }

        var credential = ReadCredential(reader);

        return new DataSource
        {
            Alias = aliasName,
            Kind = kind,
            Host = GetStringOrNull(reader, "Host"),
            ConnectionRef = connectionRef,
            Capabilities = new DataSourceCapabilities
            {
                IsSynapse = reader.GetBoolean(reader.GetOrdinal("IsSynapse")),
                SupportsCrossDbRef = reader.GetBoolean(reader.GetOrdinal("SupportsCrossDbRef")),
                IsLocal = reader.GetBoolean(reader.GetOrdinal("IsLocal")),
                ActivityMonitoring = reader.GetBoolean(reader.GetOrdinal("ActivityMonitoring")),
            },
            Credential = credential,
            Storage = new StorageContext
            {
                StorageAccountName = GetStringOrNull(reader, "StorageAccountName"),
                BlobContainer = GetStringOrNull(reader, "BlobContainer"),
            },
        };
    }

    private static CredentialProfile ReadCredential(SqlDataReader reader)
    {
        var modeOrdinal = reader.GetOrdinal("Mode");
        if (reader.IsDBNull(modeOrdinal))
        {
            // No linked credential profile: the default passwordless ConnectionStringAuth.
            return new CredentialProfile();
        }

        var modeText = reader.GetString(modeOrdinal);
        if (!Enum.TryParse<CredentialMode>(modeText, ignoreCase: false, out var mode))
        {
            mode = CredentialMode.ConnectionStringAuth;
        }

        return new CredentialProfile
        {
            Mode = mode,
            TenantId = GetStringOrNull(reader, "TenantId"),
            ClientId = GetStringOrNull(reader, "ClientId"),
            ClientSecretRef = GetStringOrNull(reader, "ClientSecretRef"),
            KeyVaultName = GetStringOrNull(reader, "KeyVaultName"),
            KeyVaultUriOverride = GetStringOrNull(reader, "KeyVaultUriOverride"),
        };
    }

    private static string? GetStringOrNull(SqlDataReader reader, string column)
    {
        var ordinal = reader.GetOrdinal(column);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static bool IsWholeSecretReference(string value) => WholeReference().IsMatch(value);

    private static bool ContainsRestingSecret(string value)
        => value.Contains("password=", StringComparison.OrdinalIgnoreCase)
           || value.Contains("pwd=", StringComparison.OrdinalIgnoreCase)
           || value.Contains("clientsecret=", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\$\{[a-zA-Z]+:[^}]+\}$")]
    private static partial Regex WholeReference();

    private const string Query =
        "SELECT d.[Kind], d.[Host], d.[ConnectionRef], d.[IsSynapse], d.[SupportsCrossDbRef], d.[IsLocal], d.[ActivityMonitoring], " +
        "d.[StorageAccountName], d.[BlobContainer], " +
        "c.[Mode], c.[TenantId], c.[ClientId], c.[ClientSecretRef], c.[KeyVaultName], c.[KeyVaultUriOverride] " +
        "FROM [flw].[DataSource] d LEFT JOIN [flw].[CredentialProfile] c ON c.[CredentialProfileID] = d.[CredentialProfileID] " +
        "WHERE d.[Alias] = @alias;";
}
