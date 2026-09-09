using SqlFlow.Azure;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Live read/write/delete against a real Azure Key Vault. Runs only when SQLFLOW_AZTEST_KEYVAULT_NAME is set
/// (with ambient Azure auth); otherwise skips. Proves the write path the migration tooling needs: set a secret,
/// read it back, overwrite it, delete it, and that a missing read returns null while a missing delete returns
/// false. Each test uses a unique secret name and cleans up after itself.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AzureKeyVaultIntegrationTests
{
    private static AzureKeyVaultSecretVault Vault(string vaultName) => new(new AzureCredentialFactory(), vaultName);

    private static string UniqueName(string prefix) => $"sqlflow-{prefix}-{Guid.NewGuid():N}";

    [SkippableFact]
    public async Task SetGetOverwriteDelete_RoundTrips()
    {
        var vaultName = AzureTestEnv.RequireKeyVault();
        var vault = Vault(vaultName);
        var name = UniqueName("rw");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            Assert.Null(await vault.GetSecretAsync(name, cts.Token));            // not there yet

            var version = await vault.SetSecretAsync(name, "first-value", cts.Token);
            Assert.False(string.IsNullOrWhiteSpace(version));
            Assert.Equal("first-value", await vault.GetSecretAsync(name, cts.Token));

            await vault.SetSecretAsync(name, "second-value", cts.Token);          // overwrite -> new version
            Assert.Equal("second-value", await vault.GetSecretAsync(name, cts.Token));

            Assert.True(await vault.DeleteSecretAsync(name, cts.Token));          // existed
            Assert.Null(await vault.GetSecretAsync(name, cts.Token));             // gone from the active set
        }
        finally
        {
            // Best-effort: returns false (no-op) if the test already deleted it.
            await vault.DeleteSecretAsync(name, cts.Token);
        }
    }

    [SkippableFact]
    public async Task DeleteMissingSecret_ReturnsFalse()
    {
        var vaultName = AzureTestEnv.RequireKeyVault();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        Assert.False(await Vault(vaultName).DeleteSecretAsync(UniqueName("missing"), cts.Token));
    }

    [SkippableFact]
    public async Task Provider_ResolvesReference_AfterWrite()
    {
        var vaultName = AzureTestEnv.RequireKeyVault();
        var factory = new AzureCredentialFactory();
        var vault = new AzureKeyVaultSecretVault(factory, vaultName);
        var provider = new AzureKeyVaultSecretProvider(factory);
        var name = UniqueName("ref");
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        try
        {
            await vault.SetSecretAsync(name, "ref-value", cts.Token);
            var resolved = await provider.ResolveAsync($"{vaultName}/{name}", cts.Token);
            Assert.Equal("ref-value", resolved);
        }
        finally
        {
            await vault.DeleteSecretAsync(name, cts.Token);
        }
    }
}
