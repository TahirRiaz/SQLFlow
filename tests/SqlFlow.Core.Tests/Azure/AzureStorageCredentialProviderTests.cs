using SqlFlow.Azure;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests.Azure;

/// <summary>
/// The Azure storage credential provider: it parses the storage account from an object-store URI and maps the
/// shared SQLFLOW_AZURE_AUTH intent to a credential, using an injected environment accessor so the mapping is
/// tested deterministically without touching (or racing on) the process environment.
/// </summary>
public sealed class AzureStorageCredentialProviderTests
{
    private static AzureStorageCredentialProvider WithEnv(params (string Key, string? Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase);
        return new AzureStorageCredentialProvider(k => map.TryGetValue(k, out var v) ? v : null);
    }

    [Theory]
    [InlineData("abfss://data@acct.dfs.core.windows.net/dim", "acct")]
    [InlineData("abfs://c@acct.dfs.core.windows.net/p", "acct")]
    [InlineData("wasbs://c@acct.blob.core.windows.net/p", "acct")]
    [InlineData("az://acct.dfs.core.windows.net/c/p", "acct")]
    [InlineData("ABFSS://c@acct.dfs.core.windows.net/p", "acct")]            // uppercase scheme
    [InlineData("abfss://c@acct.dfs.core.windows.net:443/p", "acct")]        // host with a port
    [InlineData("az://acct.blob.core.windows.net:10000/c/p", "acct")]        // bare well-known host with a port
    public void ParsesAccount_FromAzureLocations(string location, string account)
    {
        var credential = WithEnv().ResolveAzureStorage(location);
        Assert.NotNull(credential);
        Assert.Equal(account, credential!.AccountName);
    }

    [Theory]
    [InlineData("s3://bucket/x.parquet")]
    [InlineData("gs://bucket/x.parquet")]
    [InlineData("/local/path/x.parquet")]
    [InlineData("https://host/x.parquet")]
    [InlineData("az://justacontainer/path")]                    // bare container, no account in the URI: ambiguous
    [InlineData("abfss://c@@acct.dfs.core.windows.net/p")]      // malformed: more than one '@'
    [InlineData("abfss:///p")]                                  // no authority
    [InlineData("abfss://c@/p")]                                // empty host after '@'
    public void ReturnsNull_ForNonAzureMalformedOrAmbiguousLocations(string location)
        => Assert.Null(WithEnv().ResolveAzureStorage(location));

    [Fact]
    public void DefaultChain_WhenNoModeSet()
    {
        var credential = WithEnv().ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.DefaultChain, credential!.Mode);
        Assert.Null(credential.ClientSecret);
    }

    [Theory]
    [InlineData("mi", CloudAuthMode.ManagedIdentity)]
    [InlineData("managedidentity", CloudAuthMode.ManagedIdentity)]
    [InlineData("cli", CloudAuthMode.AzureCli)]
    [InlineData("azlogin", CloudAuthMode.AzureCli)]
    public void MapsNonSecretModes(string mode, CloudAuthMode expected)
    {
        var credential = WithEnv(("SQLFLOW_AZURE_AUTH", mode)).ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(expected, credential!.Mode);
        Assert.Null(credential.TenantId);
    }

    [Fact]
    public void ServicePrincipal_CapturesEnvMaterial()
    {
        var credential = WithEnv(
            ("SQLFLOW_AZURE_AUTH", "sp"),
            ("AZURE_TENANT_ID", "tid"),
            ("AZURE_CLIENT_ID", "cid"),
            ("AZURE_CLIENT_SECRET", "shh")).ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");

        Assert.Equal(CloudAuthMode.ServicePrincipal, credential!.Mode);
        Assert.Equal("tid", credential.TenantId);
        Assert.Equal("cid", credential.ClientId);
        Assert.Equal("shh", credential.ClientSecret);
    }

    [Fact]
    public void ServicePrincipal_MissingEnv_Throws()
    {
        var provider = WithEnv(("SQLFLOW_AZURE_AUTH", "sp")); // no AZURE_TENANT_ID/CLIENT_ID/SECRET
        var ex = Assert.Throws<SqlFlowException>(() => provider.ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p"));
        Assert.Contains("AZURE_TENANT_ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedIdentity_CapturesUserAssignedClientId()
    {
        var credential = WithEnv(("SQLFLOW_AZURE_AUTH", "mi"), ("AZURE_CLIENT_ID", "uai"))
            .ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.Equal(CloudAuthMode.ManagedIdentity, credential!.Mode);
        Assert.Equal("uai", credential.ClientId);
    }

    [Fact]
    public void RunningInAzure_FlagReflectsTheEnvironment()
    {
        var offCloud = WithEnv().ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.False(offCloud!.RunningInAzure);

        var inCloud = WithEnv(("WEBSITE_INSTANCE_ID", "x")).ResolveAzureStorage("abfss://d@acct.dfs.core.windows.net/p");
        Assert.True(inCloud!.RunningInAzure);
    }
}
