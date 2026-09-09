using SqlFlow.Core.Connections;

namespace SqlFlow.Azure;

/// <summary>
/// The single interpretation of the <c>SQLFLOW_AZURE_AUTH</c> environment variable, shared by the .NET credential
/// factory and the cloud-storage credential provider so every Azure access path - Key Vault, ADF/Automation
/// invoke, and DuckDB object storage - obeys one auth intent rather than each parsing the variable its own way.
/// </summary>
public static class AzureAuth
{
    public const string ModeVariable = "SQLFLOW_AZURE_AUTH";

    /// <summary>The auth mode from the process environment.</summary>
    public static CloudAuthMode Mode() => Mode(Environment.GetEnvironmentVariable);

    /// <summary>The auth mode from an explicit environment accessor (the seam used by tests).</summary>
    public static CloudAuthMode Mode(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return environment(ModeVariable)?.Trim().ToLowerInvariant() switch
        {
            "serviceprincipal" or "sp" => CloudAuthMode.ServicePrincipal,
            "managedidentity" or "mi" or "msi" => CloudAuthMode.ManagedIdentity,
            "azurecli" or "cli" or "azlogin" => CloudAuthMode.AzureCli,
            _ => CloudAuthMode.DefaultChain,
        };
    }
}
