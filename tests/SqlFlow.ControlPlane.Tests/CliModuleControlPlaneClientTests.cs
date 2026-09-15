using System.Net.Http.Json;
using SqlFlow.Cli.Hosting;
using SqlFlow.Cli.Remote;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Hosting;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A CLI module verb reaches its control plane module's endpoints through <see cref="CliControlPlaneClient"/>, the same
/// transport SQLFlow's remote verbs use: bearer credential, JSON in the web defaults, 404 as an answer rather than an error,
/// and any other failure as a <see cref="SqlFlowException"/> carrying the status. No database: the module's endpoints keep
/// their items in memory.
/// </summary>
public sealed class CliModuleControlPlaneClientTests
{
    [Fact]
    public async Task AModuleVerbClient_ReadsWritesAndDeletesThroughTheModuleEndpoints()
    {
        await using var factory = new ControlPlaneAppFactory().WithModules(new ItemsModule());
        using var signIn = factory.CreateClient();
        var token = await IssueTokenAsync(signIn, "read", "operate");
        using var http = factory.CreateClient();
        using var client = new CliControlPlaneClient(new ControlPlaneClient(http) { BearerToken = token }, http.BaseAddress!);
        var ct = CancellationToken.None;

        var created = await client.PostAsync<Item>("/api/v1/items", new Item("wells", 3), ct);
        var read = await client.GetAsync<Item>("/api/v1/items/wells", ct);
        var replaced = await client.PutAsync<Item>("/api/v1/items/wells", new Item("wells", 5), ct);
        var removed = await client.DeleteAsync("/api/v1/items/wells", ct);
        var missing = await client.GetOrNullAsync<Item>("/api/v1/items/wells", ct);
        var removedAgain = await client.DeleteAsync("/api/v1/items/wells", ct);

        Assert.Equal(new Item("wells", 3), created);
        Assert.Equal(new Item("wells", 3), read);
        Assert.Equal(new Item("wells", 5), replaced);
        Assert.True(removed);
        Assert.Null(missing);
        Assert.False(removedAgain);
    }

    [Fact]
    public async Task AModuleVerbClient_ReportsARefusalWithItsStatus()
    {
        await using var factory = new ControlPlaneAppFactory().WithModules(new ItemsModule());
        using var signIn = factory.CreateClient();
        var token = await IssueTokenAsync(signIn, "read");
        using var http = factory.CreateClient();
        using var client = new CliControlPlaneClient(new ControlPlaneClient(http) { BearerToken = token }, http.BaseAddress!);

        var error = await Assert.ThrowsAsync<SqlFlowException>(
            () => client.GetAsync<Item>("/api/v1/items-admin", CancellationToken.None));

        Assert.Contains("403", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://elsewhere.example.com/api/v1/items")]
    [InlineData("//elsewhere.example.com/api/v1/items")]
    [InlineData("api/v1/items")]
    [InlineData(" ")]
    public async Task AModuleVerbClient_RefusesAnythingButAPathOnTheControlPlane(string path)
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") };
        using var client = new CliControlPlaneClient(new ControlPlaneClient(http) { BearerToken = "secret" }, http.BaseAddress);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync<Item>(path, CancellationToken.None));
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, params string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    public sealed record Item(string Name, int Count);

    private sealed class ItemsModule : IControlPlaneModule
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Item> _items = new(StringComparer.Ordinal);

        public string Name => "items";

        public void ConfigureServices(ControlPlaneModuleServices services)
        {
        }

        public void MapEndpoints(ControlPlaneModuleEndpoints endpoints)
        {
            endpoints.Read.MapGet("/items/{name}", (string name) =>
                _items.TryGetValue(name, out var item) ? Results.Ok(item) : Results.NotFound());
            endpoints.Operate.MapPost("/items", (Item item) =>
            {
                _items[item.Name] = item;
                return Results.Ok(item);
            });
            endpoints.Operate.MapPut("/items/{name}", (string name, Item item) =>
            {
                _items[name] = item with { Name = name };
                return Results.Ok(_items[name]);
            });
            endpoints.Operate.MapDelete("/items/{name}", (string name) =>
                _items.TryRemove(name, out _) ? Results.NoContent() : Results.NotFound());
            endpoints.Admin.MapGet("/items-admin", () => Results.Ok(new Item("admin", 0)));
        }
    }
}
