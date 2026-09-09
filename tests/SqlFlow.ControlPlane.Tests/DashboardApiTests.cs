using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The dashboard summary endpoint: it requires authentication, and it aggregates the catalog (estate size, the run
/// queue's live state, fleet, schedules, sources). The aggregation test seeds a repo + pipeline + a pool-pinned
/// queued run (which the untargeted control-plane worker never claims, so it stays queued) and asserts the rollup
/// reflects them. Removes its own rows; the assembly runs serially (see AssemblyInfo).
/// </summary>
public sealed class DashboardApiTests
{
    [Fact]
    public async Task Summary_WithoutAToken_Returns401()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(new Uri("/api/v1/summary", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Summary_AggregatesEstateRunsAndQueue()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("dash_" + suffix);
        var flowName = "dash_flow_" + suffix;
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = "dash_repo_" + suffix, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, flowName),
                    RepoId = repoId,
                    Name = flowName,
                    Kind = "ing",
                    RelativePath = "flows/" + flowName + ".flow.yaml",
                    ContentHash = new string('0', 64),
                    Yaml = "name: " + flowName + "\n",
                    DefinitionJson = "{}",
                    Active = true,
                    Wave = 0,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();

                // A pool-pinned run the untargeted control-plane worker never claims, so it stays queued for the count.
                await RunQueueStore.EnqueueAsync(
                    db, new RunEnqueueRequest(repoId, flowName, "ing", "dash-pool-" + suffix), DateTime.UtcNow);
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["read"]);
            var dashboard = await GetJsonAsync<DashboardDto>(client, token, "/api/v1/summary");

            Assert.True(dashboard.Repos >= 1, "the seeded repo should be counted");
            Assert.True(dashboard.Pipelines >= 1, "the seeded pipeline should be counted");
            Assert.True(dashboard.ActivePipelines >= 1);
            Assert.True(dashboard.Runs.Queued >= 1, "the seeded queued run should be counted");
            Assert.True(dashboard.AsOfUtc > DateTime.UtcNow.AddMinutes(-5), "AsOfUtc should be computed at read time");
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
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
