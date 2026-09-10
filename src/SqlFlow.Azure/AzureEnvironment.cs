using Azure.Identity;

namespace SqlFlow.Azure;

/// <summary>
/// Detects whether the process is running inside Azure (a managed identity is available) and builds the default
/// credential options accordingly. This is the long-standing "works from a dev PC" behavior: off-cloud the
/// managed-identity credential is excluded so the chain does not stall on the IMDS probe that has no endpoint on
/// a developer machine, and the developer credentials (Visual Studio, VS Code, interactive browser) are enabled
/// so a signed-in developer authenticates with no configuration. In Azure the managed identity is the first
/// choice. The same in-Azure decision drives the DuckDB cloud-storage chain, so every Azure path agrees.
/// </summary>
public static class AzureEnvironment
{
    // Signals that the host is an Azure compute environment with a managed identity endpoint. Mirrors the set the
    // legacy engine used (App Service / Functions / Container Instance / AKS / VM / MSI), plus the explicit override.
    private static readonly string[] AzureSignals =
    [
        "WEBSITE_INSTANCE_ID",              // App Service
        "FUNCTIONS_WORKER_RUNTIME",         // Functions
        "AZURE_CONTAINER_INSTANCE_ROOT_PATH", // Container Instances
        "KUBERNETES_SERVICE_HOST",          // AKS (also AWS EKS; harmless here, MI just will not resolve)
        "AZURE_VM_RESOURCE_GROUP",          // VM
        "MSI_ENDPOINT",                     // Managed identity (classic)
        "MSI_SECRET",                       // Managed identity (classic)
        "IDENTITY_HEADER",                  // Managed identity (App Service / Functions)
        "APPSETTING_WEBSITE_SITE_NAME",     // App Service
    ];

    private const string OverrideVariable = "IS_RUNNING_IN_AZURE";

    /// <summary>Whether the process is running inside Azure, from the process environment.</summary>
    public static bool IsRunningInAzure() => IsRunningInAzure(Environment.GetEnvironmentVariable);

    /// <summary>Whether the process is running inside Azure, from an explicit accessor (the seam used by tests).</summary>
    public static bool IsRunningInAzure(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        // An explicit override wins both ways: it can force in-Azure on, or off, regardless of detection.
        var overrideValue = environment(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overrideValue))
        {
            return overrideValue.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        return AzureSignals.Any(signal => !string.IsNullOrEmpty(environment(signal)));
    }

    /// <summary>
    /// The default credential options: managed identity is excluded off-cloud (so the chain does not stall on the
    /// IMDS probe), and the developer credentials are enabled so a signed-in developer authenticates with no setup.
    /// </summary>
    public static DefaultAzureCredentialOptions DefaultCredentialOptions()
    {
        var inAzure = IsRunningInAzure();
        return new DefaultAzureCredentialOptions
        {
            ExcludeEnvironmentCredential = false,
            ExcludeManagedIdentityCredential = !inAzure,
            ExcludeAzureCliCredential = false,
            ExcludeVisualStudioCredential = false,
            ExcludeVisualStudioCodeCredential = false,
            ExcludeInteractiveBrowserCredential = false,
        };
    }
}
