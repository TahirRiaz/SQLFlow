using System.Collections.Concurrent;
using Azure.Core;
using Azure.Identity;
using SqlFlow.Core.Connections;

namespace SqlFlow.Azure;

/// <summary>
/// Builds the Azure credential from the ambient context. The auth mode is chosen by the
/// <c>SQLFLOW_AZURE_AUTH</c> environment variable (parsed once in <see cref="AzureAuth"/>, shared with the
/// cloud-storage credential provider); the default (<see cref="DefaultAzureCredential"/>) automatically chains
/// Managed Identity → Service Principal (env) → Azure CLI, so the same binary works in a cloud VM/container
/// (managed identity), in CI (service principal), and on a developer box (az login) with no configuration - and
/// no stored secrets.
///
/// Credentials are cached per distinct auth configuration and shared by every consumer (blob store, Key Vault,
/// invoke): Azure.Identity credentials are thread-safe and cache/refresh their tokens internally, so reusing the
/// instance turns repeated authentications within and across runs into cached-token lookups. The cache key
/// captures the environment values the credential is built from, so a changed auth mode or principal yields the
/// matching credential rather than a stale one.
/// </summary>
public sealed class AzureCredentialFactory : IAzureCredentialFactory
{
    // Values never leave the process; the key holds nothing more sensitive than the environment variables it is
    // derived from. A GetOrAdd race can build a transient duplicate credential; only one is cached and the other
    // is unused and collectable, which is harmless.
    private readonly ConcurrentDictionary<string, TokenCredential> _cache = new(StringComparer.Ordinal);

    public TokenCredential Create()
        => AzureAuth.Mode() switch
        {
            CloudAuthMode.ServicePrincipal => CachedServicePrincipal(),
            CloudAuthMode.ManagedIdentity => CachedManagedIdentity(),
            CloudAuthMode.AzureCli => _cache.GetOrAdd("cli", static _ => new AzureCliCredential()),
            // The default chain excludes managed identity off-cloud and enables the developer credentials, so it
            // works from a dev PC (no stall on the IMDS probe) and in Azure (managed identity first) alike.
            _ => _cache.GetOrAdd(
                $"default|{AzureEnvironment.IsRunningInAzure()}",
                static _ => new DefaultAzureCredential(AzureEnvironment.DefaultCredentialOptions())),
        };

    private TokenCredential CachedManagedIdentity()
    {
        // A non-empty AZURE_CLIENT_ID selects a specific user-assigned identity; otherwise the system-assigned
        // identity is used. (The old string ctor is obsolete in current Azure.Identity.)
        var clientId = Environment.GetEnvironmentVariable("AZURE_CLIENT_ID");
        var userAssigned = string.IsNullOrWhiteSpace(clientId) ? null : clientId;
        return _cache.GetOrAdd($"mi|{userAssigned}", _ => new ManagedIdentityCredential(
            userAssigned is null ? ManagedIdentityId.SystemAssigned : ManagedIdentityId.FromUserAssignedClientId(userAssigned)));
    }

    private TokenCredential CachedServicePrincipal()
    {
        var tenantId = Required("AZURE_TENANT_ID");
        var clientId = Required("AZURE_CLIENT_ID");
        var clientSecret = Required("AZURE_CLIENT_SECRET");
        return _cache.GetOrAdd(
            $"sp|{tenantId}|{clientId}|{clientSecret}",
            _ => new ClientSecretCredential(tenantId, clientId, clientSecret));
    }

    private static string Required(string variable)
        => Environment.GetEnvironmentVariable(variable)
           ?? throw new InvalidOperationException($"Service-principal auth requires environment variable '{variable}'.");
}
