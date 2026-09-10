namespace SqlFlow.Core.Connections;

/// <summary>
/// How an Azure resource is authenticated. Resolved once from the <c>SQLFLOW_AZURE_AUTH</c> environment variable
/// so every Azure access path - the .NET SDK ones (Key Vault, ADF/Automation invoke) and the DuckDB
/// cloud-storage one - obeys a single auth intent, instead of each being configured independently.
/// </summary>
public enum CloudAuthMode
{
    /// <summary>The default credential chain: environment service principal, then managed identity, then Azure CLI.</summary>
    DefaultChain,

    /// <summary>An explicit service principal (tenant + client id + client secret).</summary>
    ServicePrincipal,

    /// <summary>A managed identity (system-assigned, or user-assigned via AZURE_CLIENT_ID).</summary>
    ManagedIdentity,

    /// <summary>The signed-in Azure CLI user (<c>az login</c>).</summary>
    AzureCli,
}

/// <summary>
/// A resolved Azure Storage credential for one storage account: the auth mode plus the service-principal material
/// when that mode is selected. Produced by an <see cref="ICloudCredentialProvider"/> from the ambient auth intent
/// and consumed by a cloud reader (e.g. DuckDB) to authenticate object-store reads, so no secret ever lives in a
/// flow file.
/// </summary>
public sealed record AzureStorageCredential
{
    /// <summary>The storage account the credential scopes to (parsed from the location).</summary>
    public required string AccountName { get; init; }

    public required CloudAuthMode Mode { get; init; }

    /// <summary>Service-principal tenant id; set only when <see cref="Mode"/> is <see cref="CloudAuthMode.ServicePrincipal"/>.</summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Client id: the service-principal app id under <see cref="CloudAuthMode.ServicePrincipal"/>, or the
    /// user-assigned managed-identity client id under <see cref="CloudAuthMode.ManagedIdentity"/> (when set).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>Service-principal client secret; set only when <see cref="Mode"/> is <see cref="CloudAuthMode.ServicePrincipal"/>.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Whether the process is running inside Azure (managed identity available). For the default credential
    /// chain this decides whether managed identity is tried at all: off-cloud it must be skipped so the chain
    /// does not stall on the IMDS probe that has no endpoint on a developer machine.
    /// </summary>
    public bool RunningInAzure { get; init; }
}

/// <summary>
/// Resolves the ambient cloud-storage credential for a location, so a reader authenticates object storage with
/// the same intent the rest of SQLFlow uses for Azure. Implemented by the Azure plugin; when Azure is not wired
/// the implementation is simply absent and the reader leaves auth to the user (an explicit <c>init</c> secret).
/// </summary>
public interface ICloudCredentialProvider
{
    /// <summary>
    /// The Azure Storage credential for an <c>abfss://</c>/<c>abfs://</c>/<c>az://</c>/<c>wasbs://</c> location,
    /// or null when the location is not Azure object storage or the account cannot be determined (so the caller
    /// leaves auth to the user).
    /// </summary>
    AzureStorageCredential? ResolveAzureStorage(string location);
}
