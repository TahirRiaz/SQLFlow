using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The fleet registry: a node's heartbeat (insert then refresh, preserving first-seen) and the read API that lists
/// nodes with a derived online flag. DB-backed; each test removes its own node rows (other real nodes - this
/// machine's worker - are left untouched). The assembly runs serially (see AssemblyInfo).
/// </summary>
[Trait("Category", "Integration")]
public sealed class NodeRegistryTests
{
    [SkippableFact]
    public async Task Heartbeat_InsertsThenRefreshes_PreservingFirstSeen()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var node = "node_" + Guid.NewGuid().ToString("N")[..8];
        var t1 = DateTime.UtcNow.AddMinutes(-1);
        var t2 = DateTime.UtcNow;

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            await NodeStore.HeartbeatAsync(db, node, "1.0.0", t1);
            var first = await db.Nodes.AsNoTracking().SingleAsync(n => n.Name == node);
            Assert.Equal(t1, first.FirstSeenUtc);
            Assert.Equal(t1, first.LastSeenUtc);
            Assert.Equal("1.0.0", first.Version);

            await NodeStore.HeartbeatAsync(db, node, "1.0.1", t2);
            var second = await db.Nodes.AsNoTracking().SingleAsync(n => n.Name == node);
            Assert.Equal(t1, second.FirstSeenUtc); // first-seen is preserved across heartbeats
            Assert.Equal(t2, second.LastSeenUtc);   // last-seen is refreshed
            Assert.Equal("1.0.1", second.Version);
        }
        finally
        {
            await Cleanup(cs, node);
        }
    }

    [SkippableFact]
    public async Task ListNodes_ReportsOnlineByRecencyOfLastSeen()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var onlineNode = "node_on_" + suffix;
        var offlineNode = "node_off_" + suffix;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await NodeStore.HeartbeatAsync(db, onlineNode, "1.0", DateTime.UtcNow);
                db.Nodes.Add(new CatalogNode
                {
                    Name = offlineNode,
                    FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                    LastSeenUtc = DateTime.UtcNow.AddMinutes(-10), // well outside the liveness window
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var nodes = await GetJsonAsync<PagedResult<NodeDto>>(client, token, "/api/v1/nodes?pageSize=200");

            Assert.True(nodes.Items.Single(n => n.Name == onlineNode).Online, "a just-seen node should be online");
            Assert.False(nodes.Items.Single(n => n.Name == offlineNode).Online, "a node not seen in 10 minutes should be offline");
        }
        finally
        {
            await Cleanup(cs, onlineNode);
            await Cleanup(cs, offlineNode);
        }
    }

    // The purge is fleet-wide by design, so this test also clears any other dead rows the shared catalog is holding.
    // That is exactly what the reaper does on its own schedule, and no live node is touched.
    [SkippableFact]
    public async Task PurgeOffline_RemovesStaleNodes_AndLeavesLiveOnesAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var onlineNode = "node_on_" + suffix;
        var offlineNode = "node_off_" + suffix;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await NodeStore.HeartbeatAsync(db, onlineNode, "1.0", DateTime.UtcNow);
                db.Nodes.Add(new CatalogNode
                {
                    Name = offlineNode,
                    FirstSeenUtc = DateTime.UtcNow.AddDays(-1),
                    LastSeenUtc = DateTime.UtcNow.AddMinutes(-10), // well outside the liveness window
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var request = new HttpRequestMessage(
                HttpMethod.Delete, new Uri("/api/v1/nodes/offline", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var purged = await response.Content.ReadFromJsonAsync<NodePurgeResult>();

            Assert.NotNull(purged);
            Assert.True(purged.Removed >= 1, "the stale node should have been purged");

            await using var check = CatalogDatabase.Create(cs);
            Assert.False(await check.Nodes.AnyAsync(n => n.Name == offlineNode), "the offline node should be gone");
            Assert.True(await check.Nodes.AnyAsync(n => n.Name == onlineNode), "a live node must survive the purge");
        }
        finally
        {
            await Cleanup(cs, onlineNode);
            await Cleanup(cs, offlineNode);
        }
    }

    private static async Task Cleanup(string cs, string node)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Nodes.Where(n => n.Name == node).ExecuteDeleteAsync();
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }
}
