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

    private static string SeedGitRepoWithFlow(string path, string flowName)
    {
        Repository.Init(path);
        Directory.CreateDirectory(Path.Combine(path, "flows"));
        File.WriteAllText(Path.Combine(path, "flows", "orders.flow.yaml"), """
            name: __NAME__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: SrcOrders
            """.Replace("__NAME__", flowName, StringComparison.Ordinal));

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
