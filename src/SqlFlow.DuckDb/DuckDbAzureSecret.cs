using System.Text.RegularExpressions;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Model;

namespace SqlFlow.DuckDb;

/// <summary>
/// Translates the ambient Azure auth intent (resolved by an <see cref="ICloudCredentialProvider"/>) into the
/// DuckDB <c>CREATE SECRET</c> statement that authenticates an ADLS/Blob read, so the operator never hand-writes a
/// secret in the flow file and the DuckDB cloud path obeys the same <c>SQLFLOW_AZURE_AUTH</c> intent as Key Vault
/// and invoke. Auto-generation steps aside whenever the operator is managing auth themselves (an explicit
/// <c>init</c> secret, or <c>cloudAuth: off</c>) or the location is not Azure storage. Pure text generation.
/// </summary>
public static class DuckDbAzureSecret
{
    /// <summary>The fixed name of the auto-generated secret (re-created idempotently on each open).</summary>
    public const string SecretName = "sqlflow_azure";

    /// <summary>
    /// The DuckDB secret statements to run for this source: a single <c>CREATE OR REPLACE SECRET</c>, or empty
    /// when there is no provider, the operator opted out (<c>cloudAuth: off</c>), the operator already declares a
    /// secret in <c>init</c>, or the location is not resolvable Azure storage.
    /// </summary>
    public static IReadOnlyList<string> StatementsFor(SourceSpec source, ICloudCredentialProvider? provider)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (provider is null || IsOff(Option(source, "cloudAuth")) || source.Location is not { } location || UserManagesSecret(source))
        {
            return [];
        }

        var credential = provider.ResolveAzureStorage(location);
        return credential is null ? [] : [CreateStatement(credential)];
    }

    /// <summary>
    /// The injection-safe <c>CREATE OR REPLACE SECRET</c> for a resolved credential (the secret name is the fixed
    /// internal constant, never caller-supplied). The account name and any service-principal material are
    /// single-quote escaped so neither can break out of the statement. A user-assigned managed identity is
    /// rejected: DuckDB's managed-identity chain cannot target a specific client id, so honoring it silently would
    /// authenticate as a different identity than the .NET paths use.
    /// </summary>
    public static string CreateStatement(AzureStorageCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var account = $", ACCOUNT_NAME '{Escape(credential.AccountName)}'";

        if (credential.Mode == CloudAuthMode.ServicePrincipal)
        {
            return $"CREATE OR REPLACE SECRET {SecretName} (TYPE AZURE, PROVIDER service_principal, "
                + $"TENANT_ID '{Escape(Require(credential.TenantId, "TenantId"))}', "
                + $"CLIENT_ID '{Escape(Require(credential.ClientId, "ClientId"))}', "
                + $"CLIENT_SECRET '{Escape(Require(credential.ClientSecret, "ClientSecret"))}'{account})";
        }

        if (credential.Mode == CloudAuthMode.ManagedIdentity && !string.IsNullOrWhiteSpace(credential.ClientId))
        {
            throw new SqlFlowException(
                "DuckDB cloud reads support only a system-assigned managed identity; a user-assigned identity "
                + "(AZURE_CLIENT_ID with SQLFLOW_AZURE_AUTH=mi) cannot be passed to DuckDB. Use a service principal "
                + "(SQLFLOW_AZURE_AUTH=sp) for the DuckDB source, or run where it is the default identity. The .NET "
                + "paths (Key Vault, invoke) do honor AZURE_CLIENT_ID.");
        }

        var chain = ChainFor(credential);
        var chainClause = chain is null ? string.Empty : $", CHAIN '{Escape(chain)}'";
        return $"CREATE OR REPLACE SECRET {SecretName} (TYPE AZURE, PROVIDER credential_chain{chainClause}{account})";
    }

    /// <summary>The DuckDB credential_chain order for a non-service-principal mode. The default chain tries managed
    /// identity only when running in Azure: off-cloud it is omitted so the read does not stall on the IMDS probe
    /// that has no endpoint on a developer machine (the same "works from a dev PC" rule the .NET chain uses).</summary>
    private static string? ChainFor(AzureStorageCredential credential) => credential.Mode switch
    {
        CloudAuthMode.ManagedIdentity => "managed_identity",
        CloudAuthMode.AzureCli => "cli",
        _ => credential.RunningInAzure ? "managed_identity;cli;env" : "cli;env",
    };

    private static string Require(string? value, string field)
        => string.IsNullOrWhiteSpace(value)
            ? throw new SqlFlowException($"Azure service-principal storage credential is missing '{field}'.")
            : value;

    // Detects an actual secret declaration in user init, not merely the word "secret" in a comment or literal.
    private static readonly Regex CreateSecretPattern =
        new(@"\bCREATE\s+(OR\s+REPLACE\s+)?(TEMPORARY\s+|PERSISTENT\s+)?SECRET\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool UserManagesSecret(SourceSpec source)
        => DuckDbQuery.InitStatements(source).Any(s => CreateSecretPattern.IsMatch(s));

    private static bool IsOff(string? value)
        => value?.Trim().ToLowerInvariant() is "off" or "false" or "none" or "no" or "0";

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string? Option(SourceSpec source, string key)
    {
        if (source.Options is null)
        {
            return null;
        }

        foreach (var (k, v) in source.Options)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }
}
