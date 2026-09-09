using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Commit pinning at enqueue: an unpinned request defaults to the repo's last synced commit (resolved from the
/// managed-sync source, joined by name), so the executed version always matches the cataloged one and any fleet
/// node can materialize it. Covers the resolution, its two deliberate fallbacks (no source; no remote to
/// materialize from), the explicit-pin override, and the API surface (the pin visible on the run detail, and the
/// trust-boundary rejection of a malformed sha). Gated on a reachable catalog database like the other DB suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunPinningTests
{
    private const string SyncedSha = "0123456789abcdef0123456789abcdef01234567";

    [SkippableFact]
    public async Task Enqueue_WithoutSha_PinsToTheLastSyncedCommit()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRepoAsync(db, name, remoteUrl: "https://example.test/repo.git", lastSyncedSha: SyncedSha);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(RepoId(name), "flow-a", "ing"), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal(SyncedSha, run.CommitSha);
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    [SkippableFact]
    public async Task Enqueue_WithExplicitSha_HonorsItOverTheSyncedCommit()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();
        const string explicitSha = "fedcba9876543210fedcba9876543210fedcba98";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRepoAsync(db, name, remoteUrl: "https://example.test/repo.git", lastSyncedSha: SyncedSha);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(RepoId(name), "flow-a", "ing", CommitSha: explicitSha), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Equal(explicitSha, run.CommitSha);
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    [SkippableFact]
    public async Task Enqueue_WhenTheRepoHasNoManagedSource_StaysUnpinned()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedRepoAsync(db, name, remoteUrl: "https://example.test/repo.git", lastSyncedSha: null);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(RepoId(name), "flow-a", "ing"), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Null(run.CommitSha);
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    [SkippableFact]
    public async Task Enqueue_WhenTheRepoHasNoRemoteToMaterializeFrom_StaysUnpinned()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // A source with a synced sha exists, but the repo itself has no remote: a pin would be unexecutable
            // (the worker materializes from repo.RemoteUrl), so the run must stay unpinned.
            await SeedRepoAsync(db, name, remoteUrl: null, lastSyncedSha: SyncedSha);

            var runId = await RunQueueStore.EnqueueAsync(
                db, new RunEnqueueRequest(RepoId(name), "flow-a", "ing"), DateTime.UtcNow);

            var run = await db.Runs.AsNoTracking().SingleAsync(r => r.RunId == runId);
            Assert.Null(run.CommitSha);
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    [SkippableFact]
    public async Task TriggerApi_PinsTheRun_AndTheDetailShowsTheCommit()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = UniqueName();

        try
        {
            Guid repoId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                repoId = await SeedRepoAsync(db, name, "https://example.test/repo.git", SyncedSha);
                db.Pipelines.Add(new CatalogPipeline
                {
                    Id = CatalogIdentity.Pipeline(repoId, "flow-a"),
                    RepoId = repoId,
                    Name = "flow-a",
                    Kind = "ing",
                    RelativePath = "flow-a.flow.yaml",
                    Active = true,
                    FirstSeenUtc = DateTime.UtcNow,
                    LastSeenUtc = DateTime.UtcNow,
                });
                await db.SaveChangesAsync();
            }

            using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            var token = await TokenAsync(client);

            // A malformed explicit sha is rejected at the boundary, never queued.
            using var malformed = await SendAsync(client, token, new { repoId, flowName = "flow-a", commitSha = "not-a-sha!" });
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);

            // An omitted sha pins to the synced commit, visible on the run detail from the moment it is queued.
            using var accepted = await SendAsync(client, token, new { repoId, flowName = "flow-a", pool = "pin-test-pool" });
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            var run = await accepted.Content.ReadFromJsonAsync<RunTriggerAccepted>();
            Assert.NotNull(run);

            using var detailRequest = new HttpRequestMessage(
                HttpMethod.Get, new Uri($"/api/v1/runs/{run.RunId}", UriKind.Relative));
            detailRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var detail = await client.SendAsync(detailRequest);
            Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
            var body = await detail.Content.ReadAsStringAsync();
            Assert.Contains(SyncedSha, body, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await CleanupAsync(cs, name);
        }
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private static string UniqueName() => $"pin-test-{Guid.NewGuid():N}";

    private static Guid RepoId(string name) => SqlFlow.Core.Identity.FlowIdentity.FromName($"repo/{name}");

    /// <summary>Seeds a repo row and (when <paramref name="lastSyncedSha"/> is non-null) its managed-sync source,
    /// joined by name exactly as the managed sync records them.</summary>
    private static async Task<Guid> SeedRepoAsync(
        CatalogDbContext db, string name, string? remoteUrl, string? lastSyncedSha)
    {
        var repoId = RepoId(name);
        var now = DateTime.UtcNow;
        db.Repos.Add(new CatalogRepo
        {
            Id = repoId,
            Name = name,
            RemoteUrl = remoteUrl,
            RootPath = null,
            FirstSeenUtc = now,
            LastSyncUtc = now,
        });
        if (lastSyncedSha is not null)
        {
            db.RepoSources.Add(new CatalogRepoSource
            {
                Id = SqlFlow.Core.Identity.FlowIdentity.FromName($"reposource/{name}"),
                Name = name,
                RemoteUrl = remoteUrl ?? "https://example.test/repo.git",
                Branch = "main",
                Enabled = true,
                SyncIntervalSeconds = 300,
                LastSyncedSha = lastSyncedSha,
                LastSyncUtc = now,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
        }

        await db.SaveChangesAsync();
        return repoId;
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

    private static async Task CleanupAsync(string cs, string name)
    {
        await using var db = CatalogDatabase.Create(cs);
        var repoId = RepoId(name);
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.RepoSources.Where(s => s.Name == name).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }
}
