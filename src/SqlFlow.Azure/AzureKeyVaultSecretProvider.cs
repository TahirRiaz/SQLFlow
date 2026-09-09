using System.Collections.Concurrent;
using Azure.Core;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Azure;

/// <summary>
/// Resolves <c>${keyvault:vault/secret}</c> references from Azure Key Vault using the ambient Azure credential.
/// Secrets are never persisted - values are fetched at runtime under the caller's identity and held only in a
/// short-lived in-memory cache (<see cref="CacheTtl"/>), so a run that resolves the same reference repeatedly
/// pays one Key Vault round-trip instead of one per resolution while rotation still takes effect within the TTL.
/// Reads route through one <see cref="AzureKeyVaultSecretVault"/> per vault (the same client used for writes),
/// so reading and writing share a single code path; the async overload is genuinely asynchronous so a Key Vault
/// round-trip never blocks a thread-pool thread.
/// </summary>
public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    private readonly TokenCredential _credential;
    private readonly ConcurrentDictionary<string, AzureKeyVaultSecretVault> _vaults = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Value, DateTimeOffset ExpiresUtc)> _values = new(StringComparer.OrdinalIgnoreCase);

    public AzureKeyVaultSecretProvider(IAzureCredentialFactory credentialFactory)
    {
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _credential = credentialFactory.Create();
    }

    public string Scheme => "keyvault";

    public string Resolve(string locator)
    {
        var (vault, secretName) = ParseLocator(locator);
        if (TryGetCached(locator, out var cached))
        {
            return cached;
        }

        var value = VaultFor(vault).GetSecretAsync(secretName).GetAwaiter().GetResult()
            ?? throw NotFound(vault, secretName);
        _values[locator] = (value, DateTimeOffset.UtcNow + CacheTtl);
        return value;
    }

    public async Task<string> ResolveAsync(string locator, CancellationToken ct = default)
    {
        var (vault, secretName) = ParseLocator(locator);
        if (TryGetCached(locator, out var cached))
        {
            return cached;
        }

        var value = await VaultFor(vault).GetSecretAsync(secretName, ct).ConfigureAwait(false)
            ?? throw NotFound(vault, secretName);
        _values[locator] = (value, DateTimeOffset.UtcNow + CacheTtl);
        return value;
    }

    /// <summary>Returns the cached value for <paramref name="locator"/> when present and unexpired; expired
    /// entries are evicted on the spot so a rotated secret is re-fetched rather than served stale forever.
    /// Missing secrets are never cached: a lookup that failed should retry the vault, not replay the failure.</summary>
    private bool TryGetCached(string locator, out string value)
    {
        if (_values.TryGetValue(locator, out var entry))
        {
            if (entry.ExpiresUtc > DateTimeOffset.UtcNow)
            {
                value = entry.Value;
                return true;
            }

            _values.TryRemove(locator, out _);
        }

        value = string.Empty;
        return false;
    }

    /// <summary>Splits a <c>vault/secret</c> locator. Throws <see cref="SqlFlowException"/> for a missing vault
    /// or secret part.</summary>
    public static (string Vault, string Secret) ParseLocator(string locator)
    {
        ArgumentNullException.ThrowIfNull(locator);

        var separator = locator.IndexOf('/', StringComparison.Ordinal);
        if (separator <= 0 || separator == locator.Length - 1)
        {
            throw new SqlFlowException($"Key Vault reference must be 'vault/secret', got '{locator}'.");
        }

        return (locator[..separator], locator[(separator + 1)..]);
    }

    private AzureKeyVaultSecretVault VaultFor(string vault)
        => _vaults.GetOrAdd(vault, v => new AzureKeyVaultSecretVault(_credential, AzureKeyVaultSecretVault.VaultUriFor(v)));

    private static SqlFlowException NotFound(string vault, string secretName)
        => new($"Key Vault secret '{secretName}' was not found in vault '{vault}'.");
}
