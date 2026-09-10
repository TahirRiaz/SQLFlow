using SqlFlow.ControlPlane.Configuration;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Entra single sign-on turns on automatically once at least one allowed tenant id and a client id are configured:
/// supplying the credentials is the whole switch, with no separate flag to remember, and an explicit
/// <c>Enabled</c> either requires SSO (fail fast when a credential is missing) or forces it off as a kill switch.
/// Local username/password sign-in is unaffected either way; these cases pin the additive Microsoft option's gate.
/// </summary>
public sealed class AzureAdOptionsTests
{
    private const string TenantA = "00000000-0000-0000-0000-000000000001";
    private const string TenantB = "00000000-0000-0000-0000-000000000003";
    private const string Client = "00000000-0000-0000-0000-000000000002";

    [Fact]
    public void CredentialsPresent_AutoEnables()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA], ClientId = Client };
        Assert.True(options.HasCredentials);
        Assert.True(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void MultipleAllowedTenants_AutoEnables()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA, TenantB], ClientId = Client };
        Assert.True(options.HasCredentials);
        Assert.True(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void NoCredentials_StaysOff_AndDoesNotThrow()
    {
        var options = new AzureAdOptions();
        Assert.False(options.HasCredentials);
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "   ")]
    public void OnlyOneCredential_StaysOff(bool includeTenant, string? clientId)
    {
        var options = new AzureAdOptions
        {
            AllowedTenantIds = includeTenant ? [TenantA] : [],
            ClientId = clientId,
        };
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void ExplicitFalse_ForcesOff_EvenWithCredentials()
    {
        var options = new AzureAdOptions { Enabled = false, AllowedTenantIds = [TenantA], ClientId = Client };
        Assert.True(options.HasCredentials);
        Assert.False(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void ExplicitTrue_WithoutCredentials_IsAStartupError()
    {
        var options = new AzureAdOptions { Enabled = true };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("AllowedTenantIds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitTrue_WithCredentials_Enables()
    {
        var options = new AzureAdOptions { Enabled = true, AllowedTenantIds = [TenantA], ClientId = Client };
        Assert.True(options.IsEnabled);
        options.Validate();
    }

    [Fact]
    public void Enabled_WithBlankAllowedTenantEntry_IsAStartupError()
    {
        var options = new AzureAdOptions { Enabled = true, AllowedTenantIds = [TenantA, "  "], ClientId = Client };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("AllowedTenantIds", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Enabled_WithBlankDefaultRole_IsAStartupError()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA], ClientId = Client, DefaultRole = "  " };
        var error = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("DefaultRole", error.Message, StringComparison.Ordinal);
    }

    /// <summary>One allowed tenant pins the authority to it, which is what keeps B2B GUEST sign-in working: an
    /// external address resolves as a guest of THIS tenant rather than as a member of its own home tenant, where
    /// the app is unknown. Sending a single-tenant estate to the shared endpoint would silently break every
    /// external consultant's sign-in.</summary>
    [Fact]
    public void ResolveAuthority_WithOneTenant_PinsToThatTenant()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA], ClientId = Client };
        Assert.Equal($"https://login.microsoftonline.com/{TenantA}/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void ResolveAuthority_WithSeveralTenants_UsesTheOrganizationsEndpoint()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA, TenantB], ClientId = Client };
        Assert.Equal("https://login.microsoftonline.com/organizations/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void ResolveAuthority_IgnoresBlankEntriesWhenDecidingToPin()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA, "  "], ClientId = Client };
        Assert.Equal($"https://login.microsoftonline.com/{TenantA}/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void ResolveAuthority_HonoursOverride_AndTrimsTrailingSlash()
    {
        var options = new AzureAdOptions
        {
            AllowedTenantIds = [TenantA, TenantB],
            ClientId = Client,
            Authority = "https://login.microsoftonline.us/",
        };
        Assert.Equal("https://login.microsoftonline.us/organizations/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void ResolveAuthority_HonoursOverride_WhenPinnedToOneTenant()
    {
        var options = new AzureAdOptions
        {
            AllowedTenantIds = [TenantA],
            ClientId = Client,
            Authority = "https://login.microsoftonline.us/",
        };
        Assert.Equal($"https://login.microsoftonline.us/{TenantA}/v2.0", options.ResolveAuthority());
    }

    [Fact]
    public void TenantAuthority_BuildsTheConcretePerTenantEndpoint()
    {
        var options = new AzureAdOptions { AllowedTenantIds = [TenantA], ClientId = Client };
        Assert.Equal($"https://login.microsoftonline.com/{TenantA}/v2.0", options.TenantAuthority(TenantA));
    }

    [Fact]
    public void TenantAuthority_HonoursOverride()
    {
        var options = new AzureAdOptions
        {
            AllowedTenantIds = [TenantA],
            ClientId = Client,
            Authority = "https://login.microsoftonline.us",
        };
        Assert.Equal($"https://login.microsoftonline.us/{TenantA}/v2.0", options.TenantAuthority(TenantA));
    }
}
