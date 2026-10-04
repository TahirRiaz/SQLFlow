using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using LibGit2Sharp;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The managed git-to-catalog sync: authorization and validation of the register endpoint, and an end-to-end proof
/// that the background sync service pulls a (local) git repo and projects its flow into the catalog. DB-backed
/// tests seed/remove their own rows; the assembly runs serially (see AssemblyInfo).
/// </summary>
public sealed class RepoSourceTests
{
    [Fact]
    public async Task RegisterRepoSource_WithAnyAuthenticatedToken_IsAuthorized()
    {
        // Managing repo sources is part of the operational product every authenticated user gets: only user
        // administration is scope-gated. A token WITHOUT the operate scope therefore passes authorization and reaches
        // the endpoint's validation, which rejects a blank remote URL with a 400 (proving it was not fenced at 403).
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest("repo", "   ", "main", 300, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterRepoSource_WithBlankRemoteUrl_Returns400_BeforeTouchingTheDatabase()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest("repo", "   ", "main", 300, true));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ManagedSync_PullsAGitRepo_AndProjectsItsFlowIntoTheCatalog()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "src_" + suffix;
        var flowName = "src_orders_" + suffix;
        var syncedRepoId = FlowIdentity.FromName(repoName);
        var sourceId = FlowIdentity.FromName($"reposource/{repoName}");
        var gitDir = NewTempDir();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            var branch = SeedGitRepoWithFlow(gitDir, flowName);

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            // Register the local repo as a tracked source. A long interval means it syncs once during the test.
            using (var register = await PostAsync(client, token, "/api/v1/repos/sources",
                new RegisterRepoSourceRequest(repoName, gitDir, branch, 3600, true)))
            {
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
            }

            // The sync service (1s tick in tests) pulls + syncs: wait for the source to report a successful sync.
            RepoSourceDto? source = null;
            for (var attempt = 0; attempt < 80 && source?.LastSyncedSha is null; attempt++)
            {
                await Task.Delay(250);
                var list = await GetJsonAsync<PagedResult<RepoSourceDto>>(client, token, "/api/v1/repos/sources?pageSize=200");
                source = list.Items.FirstOrDefault(s => s.Id == sourceId);
            }

            Assert.NotNull(source);
            Assert.False(string.IsNullOrEmpty(source.LastSyncedSha), $"the source never reported a successful sync. LastError: {source.LastError}");
            Assert.Null(source.LastError);

            // The flow from git was projected into the catalog under the source's repo.
            await using var db = CatalogDatabase.Create(cs);
            Assert.True(await db.Pipelines.AsNoTracking().AnyAsync(p => p.RepoId == syncedRepoId && p.Name == flowName),
                "the synced flow did not appear as a pipeline in the catalog.");

            // The sync traced its progress into the activity log: an ordered set of lines ending in a terminal
            // "succeeded" event, which is what the GUI's bottom trace panel streams.
            var trace = await db.ActivityEvents.AsNoTracking()
                .Where(e => e.Kind == "repo-sync" && e.SubjectKey == sourceId.ToString())
                .OrderBy(e => e.Id).ToListAsync();
            Assert.NotEmpty(trace);
            Assert.Contains(trace, e => e.Step == "clone");
            Assert.True(trace[^1].Terminal);
            Assert.Equal("succeeded", trace[^1].Status);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.FlowDependencies.Where(d => d.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.LineageEdges.Where(e => e.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == syncedRepoId).ExecuteDeleteAsync();
                await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
                await db.ActivityEvents.Where(e => e.SubjectKey == sourceId.ToString()).ExecuteDeleteAsync();
            }

            DeleteDir(gitDir);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task TriggerNow_RequestsForcedLineage_AndASuccessfulSyncClearsIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var name = "src_force_" + suffix;
        var id = FlowIdentity.FromName($"reposource/{name}");
        var now = DateTime.UtcNow;

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://example/repo.git", "main", enabled: true, syncIntervalSeconds: 3600, now);
            }

            // Sync-now marks the source due immediately AND requests a full lineage recompute on that sync.
            await using (var db = CatalogDatabase.Create(cs))
            {
                Assert.Equal(RepoSourceMutation.Applied, await RepoSourceStore.TriggerNowAsync(db, id, now));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var source = await db.RepoSources.AsNoTracking().SingleAsync(s => s.Id == id);
                Assert.True(source.ForceLineageOnNextSync);
            }

            // A successful sync honors and clears the one-shot request, so the next periodic sync is cheap again.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.RecordSuccessAsync(db, id, "abc123", now.AddSeconds(1));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var source = await db.RepoSources.AsNoTracking().SingleAsync(s => s.Id == id);
                Assert.False(source.ForceLineageOnNextSync);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The columns a sync-now waits on move as the sync does: the request is stamped by sync-now, the start by the
    /// claim, and an attempt that was already running when the request arrived does not settle it, so the request's
    /// forced lineage survives for the attempt that answers it. A host stopping mid-attempt clears the start.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SyncProgress_IsRecordedByTheRequestTheClaimAndTheOutcome()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = "src_progress_" + Guid.NewGuid().ToString("N")[..8];
        var id = FlowIdentity.FromName($"reposource/{name}");
        var t0 = new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc);

        async Task<CatalogRepoSource> ReadAsync()
        {
            await using var db = CatalogDatabase.Create(cs);
            return await db.RepoSources.AsNoTracking().SingleAsync(s => s.Id == id);
        }

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://example/repo.git", "main", enabled: true, syncIntervalSeconds: 3600, t0);
                var due = (await ReadAsync()).NextSyncUtc!.Value;

                // A periodic attempt starts; the operator asks for a sync while it runs.
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, due, t0.AddHours(1), t0.AddSeconds(10)));
                Assert.Equal(RepoSourceMutation.Applied, await RepoSourceStore.TriggerNowAsync(db, id, t0.AddSeconds(11)));
                await RepoSourceStore.RecordSuccessAsync(db, id, "old", t0.AddSeconds(12));
            }

            var afterPeriodic = await ReadAsync();
            Assert.Equal(t0.AddSeconds(10), afterPeriodic.SyncStartedUtc);
            Assert.Equal(t0.AddSeconds(11), afterPeriodic.SyncRequestedUtc);
            Assert.False(RepoSourceStore.IsSyncAnswered(afterPeriodic, t0.AddSeconds(11)));
            Assert.True(afterPeriodic.ForceLineageOnNextSync);

            await using (var db = CatalogDatabase.Create(cs))
            {
                // The attempt the request made due answers it, and clears the forced lineage it carried out.
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, t0.AddSeconds(11), t0.AddHours(1), t0.AddSeconds(13)));
                await RepoSourceStore.RecordSuccessAsync(db, id, "new", t0.AddSeconds(15));
            }

            var answered = await ReadAsync();
            Assert.True(RepoSourceStore.IsSyncAnswered(answered, t0.AddSeconds(11)));
            Assert.Equal("new", answered.LastSyncedSha);
            Assert.False(answered.ForceLineageOnNextSync);

            await using (var db = CatalogDatabase.Create(cs))
            {
                // An attempt whose host stops records no outcome, but no longer shows as running.
                Assert.True(await RepoSourceStore.TryClaimSyncAsync(db, id, t0.AddHours(1), t0.AddHours(2), t0.AddSeconds(20)));
                Assert.True(RepoSourceStore.IsSyncRunning(await ReadAsync()));
                await RepoSourceStore.RecordAbandonedAsync(db, id, t0.AddSeconds(21));
            }

            var abandoned = await ReadAsync();
            Assert.Null(abandoned.SyncStartedUtc);
            Assert.False(RepoSourceStore.IsSyncRunning(abandoned));
            Assert.Equal("new", abandoned.LastSyncedSha);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// The scenario that made a run execute the previous commit: commit to the tracked branch, press "sync now", start
    /// a run. Sync-now now answers only once its sync has happened, with the commit it pulled, so that commit is what a
    /// run started afterwards is pinned to.
    /// </summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SyncNow_AnswersOnceTheSyncHasHappened_WithTheCommitItPulled()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "src_now_" + suffix;
        var flowName = "src_now_orders_" + suffix;
        var syncedRepoId = FlowIdentity.FromName(repoName);
        var sourceId = FlowIdentity.FromName($"reposource/{repoName}");
        var gitDir = NewTempDir();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            var branch = SeedGitRepoWithFlow(gitDir, flowName);

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);
            using (var register = await PostAsync(client, token, "/api/v1/repos/sources",
                new RegisterRepoSourceRequest(repoName, gitDir, branch, 3600, true)))
            {
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
            }

            // The first sync happens on registration; wait for it, so the sync-now below is the only one pending.
            for (var attempt = 0; attempt < 120; attempt++)
            {
                var list = await GetJsonAsync<PagedResult<RepoSourceDto>>(client, token, "/api/v1/repos/sources?pageSize=200");
                if (list.Items.FirstOrDefault(s => s.Id == sourceId) is { LastSyncedSha: not null, SyncPending: false })
                {
                    break;
                }

                await Task.Delay(250);
            }

            var committed = CommitChange(gitDir, "data/logtype_seed.csv", "id,code,tag6\n1,a,x\n");

            using var response = await PostAsync(client, token, $"/api/v1/repos/sources/{sourceId}/sync", new { });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var synced = await response.Content.ReadFromJsonAsync<RepoSourceDto>();
            Assert.NotNull(synced);
            Assert.Equal(committed, synced.LastSyncedSha);
            Assert.Null(synced.LastError);
            Assert.False(synced.SyncPending);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.FlowDependencies.Where(d => d.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.LineageEdges.Where(e => e.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == syncedRepoId).ExecuteDeleteAsync();
                await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
                await db.ActivityEvents.Where(e => e.SubjectKey == sourceId.ToString()).ExecuteDeleteAsync();
            }

            DeleteDir(gitDir);
        }
    }

    /// <summary>A sync that outlasts the wait (here no sync loop runs at all) is answered 202 with the source still
    /// marked as syncing, never as if the previous commit were the one asked for.</summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task SyncNow_ThatOutlastsTheWait_IsAccepted_AndStillPending()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = "src_wait_" + Guid.NewGuid().ToString("N")[..8];
        var id = FlowIdentity.FromName($"reposource/{name}");

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:ManagedSync:Enabled", "false")
            .WithSetting("ControlPlane:ManagedSync:SyncNowWaitSeconds", "1");

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await RepoSourceStore.UpsertAsync(db, name, "https://example/repo.git", "main", enabled: true, syncIntervalSeconds: 3600, DateTime.UtcNow);
            }

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);
            using var response = await PostAsync(client, token, $"/api/v1/repos/sources/{id}/sync", new { });

            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            var pending = await response.Content.ReadFromJsonAsync<RepoSourceDto>();
            Assert.NotNull(pending);
            Assert.True(pending.SyncPending);
            Assert.NotNull(pending.SyncRequestedUtc);
            Assert.Null(pending.LastSyncedSha);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync();
            await db.ActivityEvents.Where(e => e.SubjectKey == id.ToString()).ExecuteDeleteAsync();
        }
    }

    /// <summary>Commits <paramref name="content"/> at <paramref name="relativePath"/> on the repo's current branch and
    /// returns the new commit's id.</summary>
    private static string CommitChange(string path, string relativePath, string content)
    {
        var file = Path.Combine(path, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);

        using var repo = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        Commands.Stage(repo, "*");
        return repo.Commit("change", signature, signature).Sha;
    }

    [Theory]
    [InlineData("https://example/repo.git", ".")]
    [InlineData(null, null)]
    public async Task RegisterRepoSource_WithoutExactlyOneOfRemoteUrlAndLocalPath_Returns400(string? remoteUrl, string? localPath)
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest("repo", remoteUrl, "main", 300, true, LocalPath: localPath));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task RegisterRepoSource_WithALocalPathTheHostCannotSee_Returns400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);
        var missing = Path.Combine(Path.GetTempPath(), "sqlflow_missing_" + Guid.NewGuid().ToString("N"));

        using var response = await PostAsync(client, token, "/api/v1/repos/sources",
            new RegisterRepoSourceRequest("repo", null, "main", 300, true, LocalPath: missing));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task ManagedSync_OfALocalPath_ReadsTheWorkingTreeLive_IncludingUncommittedFlows()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "src_local_" + suffix;
        var committedFlow = "src_committed_" + suffix;
        var uncommittedFlow = "src_uncommitted_" + suffix;
        var syncedRepoId = FlowIdentity.FromName(repoName);
        var sourceId = FlowIdentity.FromName($"reposource/{repoName}");
        var dir = NewTempDir();

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            // One flow committed, one only on disk: a git source would see the first alone.
            SeedGitRepoWithFlow(dir, committedFlow);
            File.WriteAllText(Path.Combine(dir, "flows", "draft.flow.yaml"), FlowYaml(uncommittedFlow));

            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using (var register = await PostAsync(client, token, "/api/v1/repos/sources",
                new RegisterRepoSourceRequest(repoName, null, "main", 3600, true, LocalPath: dir)))
            {
                Assert.Equal(HttpStatusCode.Created, register.StatusCode);
            }

            RepoSourceDto? source = null;
            for (var attempt = 0; attempt < 80 && source?.LastSyncedSha is null; attempt++)
            {
                await Task.Delay(250);
                var list = await GetJsonAsync<PagedResult<RepoSourceDto>>(client, token, "/api/v1/repos/sources?pageSize=200");
                source = list.Items.FirstOrDefault(s => s.Id == sourceId);
            }

            Assert.NotNull(source);
            Assert.Null(source.LastError);
            Assert.StartsWith("worktree@", source.LastSyncedSha, StringComparison.Ordinal);
            Assert.Null(source.RemoteUrl);
            Assert.Equal(Path.GetFullPath(dir), source.LocalPath);

            await using var db = CatalogDatabase.Create(cs);
            var names = await db.Pipelines.AsNoTracking().Where(p => p.RepoId == syncedRepoId).Select(p => p.Name).ToListAsync();
            Assert.Contains(committedFlow, names);
            Assert.Contains(uncommittedFlow, names);

            // The repo reads the directory itself: no remote, so no run is pinned to a commit that lacks the working tree.
            var repo = await db.Repos.AsNoTracking().SingleAsync(r => r.Id == syncedRepoId);
            Assert.Null(repo.RemoteUrl);
            Assert.Equal(Path.GetFullPath(dir), Path.GetFullPath(repo.RootPath!));

            var trace = await db.ActivityEvents.AsNoTracking()
                .Where(e => e.Kind == "repo-sync" && e.SubjectKey == sourceId.ToString())
                .OrderBy(e => e.Id).ToListAsync();
            Assert.Contains(trace, e => e.Step == "read");
            Assert.DoesNotContain(trace, e => e.Step == "clone");
            Assert.Equal("succeeded", trace[^1].Status);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.FlowDependencies.Where(d => d.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.LineageEdges.Where(e => e.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Pipelines.Where(p => p.RepoId == syncedRepoId).ExecuteDeleteAsync();
                await db.Repos.Where(r => r.Id == syncedRepoId).ExecuteDeleteAsync();
                await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
                await db.ActivityEvents.Where(e => e.SubjectKey == sourceId.ToString()).ExecuteDeleteAsync();
            }

            DeleteDir(dir);
        }
    }

    private static string FlowYaml(string flowName) => """
        name: __NAME__
        source:
          type: csv
          location: ./data.csv
        target:
          connection: ${env:SQLFlowSinkConStr}
          schema: dbo
          table: __NAME__
        """.Replace("__NAME__", flowName, StringComparison.Ordinal);

    private static string SeedGitRepoWithFlow(string path, string flowName)
    {
        Repository.Init(path);
        Directory.CreateDirectory(Path.Combine(path, "flows"));
        File.WriteAllText(Path.Combine(path, "flows", "orders.flow.yaml"), FlowYaml(flowName));

        using var repo = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        Commands.Stage(repo, "*");
        repo.Commit("initial", signature, signature);
        return repo.Head.FriendlyName; // the actual default branch (master/main), so the clone checks it out
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
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

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_src_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
