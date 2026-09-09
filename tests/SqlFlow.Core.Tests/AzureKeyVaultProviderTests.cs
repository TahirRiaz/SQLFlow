using SqlFlow.Azure;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

public sealed class AzureKeyVaultProviderTests
{
    [Fact]
    public void ParseLocator_SplitsVaultAndSecret()
    {
        var (vault, secret) = AzureKeyVaultSecretProvider.ParseLocator("myvault/mysecret");
        Assert.Equal("myvault", vault);
        Assert.Equal("mysecret", secret);
    }

    [Fact]
    public void ParseLocator_KeepsNestedSecretPath()
    {
        // Only the first '/' separates vault from the secret name; the rest is the secret part.
        var (vault, secret) = AzureKeyVaultSecretProvider.ParseLocator("v/a/b");
        Assert.Equal("v", vault);
        Assert.Equal("a/b", secret);
    }

    [Theory]
    [InlineData("novault")]      // no separator
    [InlineData("/secret")]      // empty vault
    [InlineData("vault/")]       // empty secret
    public void ParseLocator_BadFormat_Throws(string locator)
        => Assert.Throws<SqlFlowException>(() => AzureKeyVaultSecretProvider.ParseLocator(locator));

    [Fact]
    public void Provider_Scheme_IsKeyvault()
    {
        var provider = new AzureKeyVaultSecretProvider(new AzureCredentialFactory());
        Assert.Equal("keyvault", provider.Scheme);
    }
}
