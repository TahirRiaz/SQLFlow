using SqlFlow.Core;
using SqlFlow.Core.Connections;

namespace SqlFlow.Azure;

/// <summary>
/// Resolves the Azure Storage credential for an object-store location from the same <c>SQLFLOW_AZURE_AUTH</c>
/// intent the .NET credential factory uses, so a DuckDB read of ADLS/Blob authenticates exactly like Key Vault
/// and invoke do - no per-flow secret, no separately configured auth. The storage account is parsed from the
/// location; service-principal material comes from the AZURE_* environment variables (never from a flow file).
/// </summary>
public sealed class AzureStorageCredentialProvider : ICloudCredentialProvider
{
    private readonly Func<string, string?> _environment;

    public AzureStorageCredentialProvider()
        : this(Environment.GetEnvironmentVariable)
    {
    }

    internal AzureStorageCredentialProvider(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public AzureStorageCredential? ResolveAzureStorage(string location)
    {
        var account = AccountFromLocation(location);
        if (account is null)
        {
            return null;
        }

        var mode = AzureAuth.Mode(_environment);
        var runningInAzure = AzureEnvironment.IsRunningInAzure(_environment);

        return mode switch
        {
            CloudAuthMode.ServicePrincipal => new AzureStorageCredential
            {
                AccountName = account,
                Mode = mode,
                RunningInAzure = runningInAzure,
                TenantId = Required("AZURE_TENANT_ID"),
                ClientId = Required("AZURE_CLIENT_ID"),
                ClientSecret = Required("AZURE_CLIENT_SECRET"),
            },
            // A non-empty AZURE_CLIENT_ID selects a user-assigned identity; carried so the consumer can honor it
            // (the .NET paths) or reject it loudly when it cannot (the DuckDB path supports system-assigned only).
            CloudAuthMode.ManagedIdentity => new AzureStorageCredential
            {
                AccountName = account,
                Mode = mode,
                RunningInAzure = runningInAzure,
                ClientId = NullIfBlank(_environment("AZURE_CLIENT_ID")),
            },
            _ => new AzureStorageCredential { AccountName = account, Mode = mode, RunningInAzure = runningInAzure },
        };
    }

    private string Required(string variable)
    {
        var value = _environment(variable);
        return string.IsNullOrWhiteSpace(value)
            ? throw new SqlFlowException(
                $"Azure service-principal auth (SQLFLOW_AZURE_AUTH=sp) requires environment variable '{variable}'.")
            : value;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// The storage account from an Azure object-store URI. <c>abfss://container@account.dfs.core.windows.net/...</c>
    /// (the Databricks/ADLS Gen2 form) and any <c>scheme://...account.(dfs|blob).core.windows.net/...</c> host
    /// yield the account. A bare <c>az://container/path</c> (no account in the URI) is ambiguous, so it returns
    /// null and the caller leaves auth to the user. Non-Azure schemes (s3, gs, file, ...) return null.
    /// </summary>
    private static string? AccountFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return null;
        }

        var schemeEnd = location.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0)
        {
            return null;
        }

        var scheme = location[..schemeEnd].ToLowerInvariant();
        if (scheme is not ("abfss" or "abfs" or "az" or "azure" or "wasb" or "wasbs"))
        {
            return null;
        }

        var rest = location[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var authority = slash < 0 ? rest : rest[..slash];
        if (authority.Length == 0)
        {
            return null;
        }

        // A well-formed authority has at most one '@' (container@host). More than one is malformed; reject it
        // rather than guess which '@' is real.
        var atCount = authority.Count(c => c == '@');
        if (atCount > 1)
        {
            return null;
        }

        var at = authority.IndexOf('@', StringComparison.Ordinal);
        var host = at < 0 ? authority : authority[(at + 1)..];

        // Drop any port before matching the host and taking the account label.
        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            host = host[..colon];
        }

        // Only extract an account when the URI actually carries one: a container@host form, or a well-known
        // storage host. A bare host (az://container/...) is the container, not the account, so do not guess.
        var isWellKnownHost = host.EndsWith(".core.windows.net", StringComparison.OrdinalIgnoreCase);
        if (at < 0 && !isWellKnownHost)
        {
            return null;
        }

        var dot = host.IndexOf('.', StringComparison.Ordinal);
        var account = (dot < 0 ? host : host[..dot]).Trim();

        // A storage account name is letters and digits only. Anything else (a stray '@', empty, punctuation from
        // a malformed URI) is rejected so a junk account never reaches the credential statement.
        return account.Length > 0 && account.All(char.IsAsciiLetterOrDigit) ? account : null;
    }
}
