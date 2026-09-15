using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core.Hosting;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A branded host's OpenAPI document carries its product name and lockup, and the branding is a service modules can use;
/// SQLFlow's own host keeps the document's framework title.
/// </summary>
public sealed class ProductBrandingApiTests
{
    [Fact]
    public async Task ABrandedHost_TitlesItsOpenApiDocumentWithTheProductName()
    {
        var branding = new ProductBranding("Acme Data", "Acme Data, powered by SQLFlow");
        await using var factory = new ControlPlaneAppFactory().WithBranding(branding);
        using var client = factory.CreateClient();

        var info = await OpenApiInfoAsync(client);

        Assert.Equal("Acme Data", info.GetProperty("title").GetString());
        Assert.Equal("Acme Data, powered by SQLFlow", info.GetProperty("description").GetString());
        Assert.Same(branding, factory.Services.GetRequiredService<ProductBranding>());
    }

    [Fact]
    public async Task SqlFlowsOwnHost_KeepsTheFrameworkTitle()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        var info = await OpenApiInfoAsync(client);

        Assert.Equal("SqlFlow.ControlPlane | v1", info.GetProperty("title").GetString());
        Assert.Same(ProductBranding.SqlFlow, factory.Services.GetRequiredService<ProductBranding>());
    }

    private static async Task<JsonElement> OpenApiInfoAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("info").Clone();
    }
}
