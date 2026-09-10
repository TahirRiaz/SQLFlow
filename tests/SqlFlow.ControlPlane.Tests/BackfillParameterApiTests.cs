using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The built-in backfill through the control plane: the queue persists the substitution parameters on the run
/// row (auditable history), the enqueue validates them, and the trigger API accepts a valid backfill, rejects an
/// invalid one at the boundary, and surfaces the parameters on the run detail. Gated on a reachable catalog
/// database, like the other DB-backed control-plane suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackfillParameterApiTests
{
    [SkippableFact]
    public async Task Enqueue_PersistsParameters_OnTheRunRow()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var flowName = $"bf-queue-{Guid.NewGuid():N}";
        var from = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, flowName, "file", Parameters: new RunParameters
                {
                    BackfillFrom = from,
                    BackfillTo = to,
                    FilePattern = "orders*.csv",
                }),
                DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.False(run.FullLoad);
            Assert.Equal(from, run.BackfillFrom);
            Assert.Equal(to, run.BackfillTo);
            Assert.Equal("orders*.csv", run.FilePattern);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Enqueue_RejectsInvalidParameters_BeforeQueueing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => RunQueueStore.EnqueueAsync(
                db,
                new RunEnqueueRequest(repoId, "bad", "file", Parameters: new RunParameters
                {
                    FullLoad = true,
                    BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                }),
                DateTime.UtcNow));

            // Nothing was queued for the repo.
            Assert.Equal(0, await db.Runs.AsNoTracking().CountAsync(r => r.RepoId == repoId));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task TriggerApi_AcceptsABackfill_AndTheRunDetailShowsIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = $"bf-api-{Guid.NewGuid():N}";
        Guid repoId;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                repoId = SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");
                var now = DateTime.UtcNow;
                db.Repos.Add(new CatalogRepo { Id = repoId, Name = name, FirstSeenUtc = now, LastSyncUtc = now });
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, "flow-a"),
                    RepoId = repoId,
                    Name = "flow-a",
                    Kind = "ing",
                    RelativePath = "flow-a.flow.yaml",
                    Active = true,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // An inverted window is refused at the boundary.
            using var invalid = await SendAsync(client, token, new
            {
                repoId,
                flowName = "flow-a",
                backfillFrom = "2023-02-01T00:00:00Z",
                backfillTo = "2023-01-01T00:00:00Z",
            });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            // A valid backfill is accepted, and the run detail carries the parameters for the audit trail.
            using var accepted = await SendAsync(client, token, new
            {
                repoId,
                flowName = "flow-a",
                pool = "bf-unserved",
                fullLoad = false,
                backfillFrom = "2023-01-01T00:00:00Z",
                backfillTo = "2023-02-01T00:00:00Z",
            });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var run = await accepted.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(run);

            using var detailRequest = new HttpRequestMessage(HttpMethod.Get, new Uri($"/api/v1/runs/{run.RunId}", UriKind.Relative));
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detail = await client.SendAsync(detailRequest);
            var body = await detail.Content.ReadAsStringAsync();
            Assert.Contains("2023-01-01", body, StringComparison.Ordinal);
            Assert.Contains("2023-02-01", body, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(cs, SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}"));
        }
    }

    private static async Task<string> TokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read", "operate"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/v1/runs", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }
}
