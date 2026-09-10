using Azure.Core;
using Azure.Identity;
using SqlFlow.Azure;
using SqlFlow.Azure.Invoke;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

public sealed class AzureServicePrincipalResolverTests
{
    private const string Tenant = "72f988bf-1111-2222-3333-444444444444";
    private const string Client = "11112222-3333-4444-5555-666666666666";

    private static AzureServicePrincipalResolver Resolver(IServicePrincipalStore store, string? secret = "resolved-secret")
        => new(store, new FakeSecretResolver(secret ?? string.Empty), new FakeCredentialFactory());

    [Fact]
    public async Task NoReference_Throws()
    {
        var resolver = Resolver(new FakeStore(new ServicePrincipalProfile { Alias = "x" }));
        await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync(null));
    }

    [Fact]
    public async Task NonAliasReference_Throws()
    {
        var resolver = Resolver(new FakeStore(new ServicePrincipalProfile { Alias = "x" }));
        await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync("Server=.;Database=db;"));
    }

    [Fact]
    public async Task LightweightStore_Throws()
    {
        var resolver = Resolver(NullServicePrincipalStore.Instance);
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync("@sp"));
        Assert.Contains("full mode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MissingSubscriptionOrResourceGroup_Throws()
    {
        var profile = new ServicePrincipalProfile { Alias = "sp", SubscriptionId = "sub" }; // no ResourceGroup
        var resolver = Resolver(new FakeStore(profile));
        await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync("@sp"));
    }

    [Fact]
    public async Task WithSecretRef_BuildsClientSecretCredential_AndCarriesCoordinates()
    {
        var profile = new ServicePrincipalProfile
        {
            Alias = "sp",
            TenantId = Tenant,
            ClientId = Client,
            ClientSecretRef = "${env:SECRET}",
            SubscriptionId = "sub-1",
            ResourceGroup = "rg-1",
            DataFactoryName = "adf-1",
            AutomationAccountName = "aa-1",
        };

        var resolved = await Resolver(new FakeStore(profile)).ResolveAsync("@sp");

        Assert.IsType<ClientSecretCredential>(resolved.Credential);
        Assert.Equal("sub-1", resolved.SubscriptionId);
        Assert.Equal("rg-1", resolved.ResourceGroup);
        Assert.Equal("adf-1", resolved.DataFactoryName);
        Assert.Equal("aa-1", resolved.AutomationAccountName);
    }

    [Fact]
    public async Task WithoutSecretRef_UsesAmbientCredential()
    {
        var profile = new ServicePrincipalProfile { Alias = "sp", SubscriptionId = "sub", ResourceGroup = "rg" };
        var factory = new FakeCredentialFactory();
        var resolver = new AzureServicePrincipalResolver(new FakeStore(profile), new FakeSecretResolver("x"), factory);

        var resolved = await resolver.ResolveAsync("@sp");

        Assert.Same(factory.Sentinel, resolved.Credential);
    }

    [Fact]
    public async Task SecretRefResolvesEmpty_Throws()
    {
        var profile = new ServicePrincipalProfile
        {
            Alias = "sp",
            TenantId = Tenant,
            ClientId = Client,
            ClientSecretRef = "${env:SECRET}",
            SubscriptionId = "sub",
            ResourceGroup = "rg",
        };
        var resolver = Resolver(new FakeStore(profile), secret: string.Empty);
        await Assert.ThrowsAsync<SqlFlowException>(() => resolver.ResolveAsync("@sp"));
    }

    private sealed class FakeStore : IServicePrincipalStore
    {
        private readonly ServicePrincipalProfile _profile;

        public FakeStore(ServicePrincipalProfile profile) => _profile = profile;

        public bool SupportsAliases => true;

        public Task<ServicePrincipalProfile> ResolveAsync(string aliasName, CancellationToken ct = default)
            => Task.FromResult(_profile);
    }

    private sealed class FakeSecretResolver : ISecretResolver
    {
        private readonly string _secret;

        public FakeSecretResolver(string secret) => _secret = secret;

        public string Resolve(string value) => _secret;

        public Task<string> ResolveAsync(string value, CancellationToken ct = default) => Task.FromResult(_secret);
    }

    private sealed class FakeCredentialFactory : IAzureCredentialFactory
    {
        public TokenCredential Sentinel { get; } = new AzureCliCredential();

        public TokenCredential Create() => Sentinel;
    }
}
