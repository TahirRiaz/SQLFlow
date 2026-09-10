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
/// The repository content listing (<c>GET /repos/{id}/tree</c>), which is what makes the repo view show the folders a
/// repository actually holds rather than only the folders the catalog imported a flow from. Exercised over the
/// local-path read: the test lays out a working tree on disk (a folder of flows, a folder of SQL with no flow in it,
/// a root file, and git's own metadata folder), seeds a repo pointing at it, and asserts the listing carries every
/// folder including the one with no pipelines, skips <c>.git</c>, and reports paths forward-slashed and repo-relative.
/// The git-backed read shares this walk's output shape and is covered by the history clone tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RepoTreeApiTests
{
    [SkippableFact]
    public async Task Tree_ForLocalPathRepo_ListsEveryFolderNotJustThoseHoldingFlows()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_tree_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var now = DateTime.UtcNow;

        var root = Path.Combine(Path.GetTempPath(), "sqlflow_tree_" + suffix);
        Directory.CreateDirectory(Path.Combine(root, "sales"));
        Directory.CreateDirectory(Path.Combine(root, "sql", "views"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        await File.WriteAllTextAsync(Path.Combine(root, "sales", "orders.flow.yaml"), "name: orders\n");
        await File.WriteAllTextAsync(Path.Combine(root, "sql", "views", "compat_views.sql"), "create view v as select 1 as x;\n");
        await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "# repo\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".git", "config"), "[core]\n");

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = "https://example/" + repoName + ".git",
                    RootPath = root,
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var tree = await GetJsonAsync<RepoTreeDto>(client, token, $"/api/v1/repos/{repoId}/tree");
            Assert.Equal("disk", tree.ReadFrom);
            Assert.False(tree.Truncated);

            var paths = tree.Entries.Select(e => e.Path).ToList();

            // The folder holding a flow and the folder holding none are both listed: the point of reading the
            // repository instead of the catalog.
            Assert.Contains("sales", paths, StringComparer.Ordinal);
            Assert.Contains("sql", paths, StringComparer.Ordinal);
            Assert.Contains("sql/views", paths, StringComparer.Ordinal);
            Assert.Contains("sales/orders.flow.yaml", paths, StringComparer.Ordinal);
            Assert.Contains("sql/views/compat_views.sql", paths, StringComparer.Ordinal);
            Assert.Contains("README.md", paths, StringComparer.Ordinal);

            // git's own metadata is machinery, not repository content.
            Assert.DoesNotContain(paths, p => p == ".git" || p.StartsWith(".git/", StringComparison.Ordinal));

            // Folders carry no size; a file carries its real one.
            Assert.True(tree.Entries.Single(e => e.Path == "sql").IsFolder);
            Assert.Equal(0, tree.Entries.Single(e => e.Path == "sql").SizeBytes);
            var flowFile = tree.Entries.Single(e => e.Path == "sales/orders.flow.yaml");
            Assert.False(flowFile.IsFolder);
            Assert.True(flowFile.SizeBytes > 0);

            // The listing is path-ordered, so the outline can be built from it without re-sorting.
            Assert.Equal(paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList(), paths);

            // An unknown repo is a 404 with a ProblemDetails body, not an empty tree.
            using var missing = await SendAsync(client, HttpMethod.Get, $"/api/v1/repos/{Guid.NewGuid()}/tree", token);
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var problem = await missing.Content.ReadFromJsonAsync<ProblemPayload>();
            Assert.NotNull(problem);
            Assert.Equal(404, problem.Status);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            Directory.Delete(root, recursive: true);
        }
    }

    [SkippableFact]
    public async Task Tree_ForRepoWithNoSourceAndNoPath_ReportsWhyItCannotBeListed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_tree_none_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var now = DateTime.UtcNow;

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                db.Repos.Add(new CatalogRepo
                {
                    Id = repoId,
                    Name = repoName,
                    RemoteUrl = null,
                    RootPath = null,
                    FirstSeenUtc = now,
                    LastSyncUtc = now,
                });
                await db.SaveChangesAsync();
            }

            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // Unreachable contents are stated, never returned as an empty listing that would read as "this
            // repository holds nothing".
            using var response = await SendAsync(client, HttpMethod.Get, $"/api/v1/repos/{repoId}/tree", token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
            Assert.NotNull(problem);
            Assert.Equal("Contents unavailable", problem.Title);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    private static async Task<string> IssueReadTokenAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, ["read"]));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, HttpMethod.Get, relativeUri, token);
        response.EnsureSuccessStatusCode();
        var value = await response.Content.ReadFromJsonAsync<T>();
        Assert.NotNull(value);
        return value;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string relativeUri, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    /// <summary>The fields of an RFC 7807 ProblemDetails body the assertions read.</summary>
    private sealed record ProblemPayload(string? Title, int Status);
}
