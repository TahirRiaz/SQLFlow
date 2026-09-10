using Azure.Core;
using SqlFlow.Azure;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Unit coverage for the Azure blob export destination's routing and guards. The actual blob I/O (write, size,
/// delete, zip) is exercised end to end against a real storage account by the deployment's export test; here we
/// pin the pure decisions: which locations it claims, that construction rejects a null credential factory, and
/// that a malformed Azure URI fails at parse time before any network or credential call.
/// </summary>
public sealed class AzureBlobExportDestinationTests
{
    [Theory]
    [InlineData("abfss://exports@acct.dfs.core.windows.net/orders/data.parquet", true)]
    [InlineData("wasbs://exports@acct.blob.core.windows.net/orders/data.csv", true)]
    [InlineData("https://acct.blob.core.windows.net/exports/orders/data.parquet", true)]
    [InlineData("https://acct.dfs.core.windows.net/exports/orders/data.parquet", true)]
    [InlineData("./out/orders.parquet", false)]
    [InlineData("/mnt/exports/orders.csv", false)]
    [InlineData("file:///tmp/orders.csv", false)]
    [InlineData("https://api.example.com/v1/orders", false)]
    [InlineData("", false)]
    public void CanHandle_ClaimsAzureStorageUrisOnly(string location, bool expected)
    {
        var destination = new AzureBlobExportDestination(new ThrowingCredentialFactory());
        Assert.Equal(expected, destination.CanHandle(location));
    }

    [Fact]
    public void Constructor_NullCredentials_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new AzureBlobExportDestination(null!));
    }

    [Fact]
    public async Task OpenWrite_MalformedAzureUri_ThrowsBeforeAuthenticating()
    {
        // The credential factory throws if touched, so this proves the parse guard runs first: an authority with
        // no 'container@' is not addressable and must surface a clear error, not an auth attempt.
        var destination = new AzureBlobExportDestination(new ThrowingCredentialFactory());
        await Assert.ThrowsAsync<SqlFlowException>(
            () => destination.OpenWriteAsync("abfss://acct.dfs.core.windows.net/orders/data.parquet"));
    }

    // CanHandle and the parse guard must not authenticate; Create throwing proves neither reaches the credential.
    private sealed class ThrowingCredentialFactory : IAzureCredentialFactory
    {
        public TokenCredential Create() => throw new InvalidOperationException("Routing must not authenticate.");
    }
}
