using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Security;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>Where a client's browser sign-in is approved: the GUI, behind its sign-in, whenever the control plane knows
/// the GUI's address, since that is the only place a person signs in with Microsoft Entra.</summary>
public sealed class DeviceApprovalPageTests
{
    private static ControlPlaneOptions Options(string? guiBaseUrl = null, params string[] origins)
    {
        var options = new ControlPlaneOptions();
        options.Notifications.GuiBaseUrl = guiBaseUrl;
        options.Cors.AllowedOrigins = origins;
        return options;
    }

    [Fact]
    public void The_configured_gui_address_answers_and_carries_the_code()
    {
        var url = DeviceApprovalPage.GuiApprovalUrl(Options("https://gui.example.com/", "https://other.example.com"), "ABCD-EFGH");

        Assert.Equal("https://gui.example.com/device?code=ABCD-EFGH", url);
    }

    [Fact]
    public void The_one_allowed_origin_is_the_gui_when_no_address_is_configured()
    {
        Assert.Equal("https://gui.example.com/device", DeviceApprovalPage.GuiApprovalUrl(Options(null, "https://gui.example.com"), null));
    }

    [Fact]
    public void Several_allowed_origins_name_no_gui_and_the_local_page_answers()
    {
        Assert.Null(DeviceApprovalPage.GuiApprovalUrl(Options(null, "https://a.example.com", "https://b.example.com"), "ABCD"));
        Assert.Null(DeviceApprovalPage.GuiApprovalUrl(Options(), "ABCD"));
    }

    [Fact]
    public void The_code_cannot_steer_the_redirect()
    {
        var url = DeviceApprovalPage.GuiApprovalUrl(Options("https://gui.example.com"), "x&next=https://evil.example.com/#");

        Assert.Equal("https://gui.example.com/device?code=x%26next%3Dhttps%3A%2F%2Fevil.example.com%2F%23", url);
    }
}
