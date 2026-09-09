using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Azure;

/// <summary>
/// An <see cref="ISecretVault"/> backed by one Azure Key Vault, using the ambient Azure credential. Reading a
/// missing secret returns null (a 404 is a normal "not there" answer, not an error); writing creates the secret
/// or adds a new version; deleting begins removal and reports whether anything was there. No secret value is
/// cached or persisted by this type - it is a thin, credential-bound gateway to the vault.
/// </summary>
public sealed class AzureKeyVaultSecretVault : ISecretVault
{
    private readonly SecretClient _client;

    /// <summary>Binds to the vault named <paramref name="vaultName"/> (the public-cloud
    /// <c>https://{vaultName}.vault.azure.net/</c> endpoint) under the ambient credential.</summary>
    public AzureKeyVaultSecretVault(IAzureCredentialFactory credentialFactory, string vaultName)
    {
        ArgumentNullException.ThrowIfNull(credentialFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultName);
        _client = new SecretClient(BuildVaultUri(vaultName), credentialFactory.Create());
    }

    /// <summary>Binds to an explicit vault URI under the given credential (use for sovereign clouds).</summary>
    public AzureKeyVaultSecretVault(TokenCredential credential, Uri vaultUri)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(vaultUri);
        _client = new SecretClient(vaultUri, credential);
    }

    public Uri VaultUri => _client.VaultUri;

    public async Task<string?> GetSecretAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            var response = await _client.GetSecretAsync(name, cancellationToken: ct).ConfigureAwait(false);
            return response.Value.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<string> SetSecretAsync(string name, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(value);
        var response = await _client.SetSecretAsync(name, value, ct).ConfigureAwait(false);
        return response.Value.Properties.Version;
    }

    public async Task<bool> DeleteSecretAsync(string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        try
        {
            // Begin deletion (a soft-delete vault completes the purge asynchronously); existence is all we report.
            await _client.StartDeleteSecretAsync(name, ct).ConfigureAwait(false);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return false;
        }
    }

    internal static Uri VaultUriFor(string vaultName) => BuildVaultUri(vaultName);

    private static Uri BuildVaultUri(string vaultName) => new($"https://{vaultName}.vault.azure.net/");
}
