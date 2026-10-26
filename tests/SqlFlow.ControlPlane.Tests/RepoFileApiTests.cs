using System.Diagnostics;
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
/// One repository file's text over the API (<c>GET /repos/{id}/file</c>), what the repo page's code window shows. The test
/// lays out a local-path repo on disk as a git working tree (flows, a folder git ignores, an environment file, and a folder
/// linked to one outside the repository), seeds a repo pointing at it, and asserts a file reads as text, a credential
/// store, an ignored file, a linked folder and a path leaving the repository are refused saying why, and an unknown repo
/// or file is a 404.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RepoFileApiTests
{
    [SkippableFact]
    public async Task File_ForLocalPathRepo_ShowsTheRepositorysCode_AndNothingThatHoldsCredentialsOrLiesOutsideIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoName = "cp_file_" + suffix;
        var repoId = FlowIdentity.FromName(repoName);
        var now = DateTime.UtcNow;

        var root = Path.Combine(Path.GetTempPath(), "sqlflow_file_" + suffix);
        var outside = Path.Combine(Path.GetTempPath(), "sqlflow_file_outside_" + suffix);
        Directory.CreateDirectory(Path.Combine(root, "sales"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(outside);
        Repository.Init(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), "data/\n");
        await File.WriteAllTextAsync(Path.Combine(root, "sales", "orders.flow.yaml"), "name: orders\nflowType: ing\n");
        await File.WriteAllTextAsync(Path.Combine(root, "sales", "lookup.json"), "{ \"region\": \"north\" }\n");
        await File.WriteAllTextAsync(Path.Combine(root, "data", "rows.csv"), "id,name\n1,a\n");
        await File.WriteAllTextAsync(Path.Combine(root, ".env"), "ENVIRONMENT=test\n");
        await File.WriteAllTextAsync(Path.Combine(outside, "elsewhere.txt"), "outside the repository\n");
        CreateFolderLink(Path.Combine(root, "linked"), outside);

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
            var file = $"/api/v1/repos/{repoId}/file?path=";

            // A flow and any other file of the repository read as their text, with where they were read and their size.
            using (var response = await SendAsync(client, file + "sales/orders.flow.yaml", token))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var flow = await response.Content.ReadFromJsonAsync<RepoFileDto>();
                Assert.NotNull(flow);
                Assert.Equal("sales/orders.flow.yaml", flow.Path);
                Assert.Equal("disk", flow.ReadFrom);
                Assert.Equal("name: orders\nflowType: ing\n", flow.Text);
                Assert.Equal(27, flow.SizeBytes);
                Assert.False(flow.Binary);
                Assert.False(flow.Truncated);
            }

            using (var response = await SendAsync(client, file + "sales/lookup.json", token))
            {
                var json = await response.Content.ReadFromJsonAsync<RepoFileDto>();
                Assert.Equal("{ \"region\": \"north\" }\n", json!.Text);
            }

            // A credential store and a file git ignores hold what a repository should not show; each says why.
            await AssertProblemAsync(client, token, file + ".env", HttpStatusCode.Forbidden, "Content not shown", "is an environment file");
            await AssertProblemAsync(client, token, file + "data/rows.csv", HttpStatusCode.Forbidden, "Content not shown", "Git ignores 'data/rows.csv'");

            // Nothing outside the repository: through a link, or by a path climbing out of it.
            await AssertProblemAsync(client, token, file + "linked/elsewhere.txt", HttpStatusCode.BadRequest, "Not a repository file", "reached through a link");
            await AssertProblemAsync(client, token, file + "sales/../../outside.txt", HttpStatusCode.BadRequest, "Not a repository file", "names a folder by '.', '..'");
            await AssertProblemAsync(client, token, $"/api/v1/repos/{repoId}/file", HttpStatusCode.BadRequest, "Not a repository file", "Name the file with ?path=");

            // A file the repository does not hold, and a repository the catalog does not, are not found.
            await AssertProblemAsync(client, token, file + "sales/gone.yaml", HttpStatusCode.NotFound, "Not found", "holds no file 'sales/gone.yaml'");
            await AssertProblemAsync(client, token, $"/api/v1/repos/{Guid.NewGuid()}/file?path=README.md", HttpStatusCode.NotFound, "Not found", "No repository with id");
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            }

            // The link goes first, so deleting the repository's folder never follows it into the folder it names.
            Directory.Delete(Path.Combine(root, "linked"));
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    /// <summary>A folder link to <paramref name="target"/>: a junction on Windows, which needs no privilege a symbolic
    /// link does, and a symbolic link elsewhere. Both are reparse points the read refuses to pass through.</summary>
    private static void CreateFolderLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        var start = new ProcessStartInfo("cmd.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in new[] { "/c", "mklink", "/J", link, target })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("cmd.exe did not start.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"mklink /J {link} {target} failed: {error}");
    }

    private static async Task AssertProblemAsync(
        HttpClient client, string token, string relativeUri, HttpStatusCode status, string title, string detail)
    {
        using var response = await SendAsync(client, relativeUri, token);
        Assert.Equal(status, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemPayload>();
        Assert.NotNull(problem);
        Assert.Equal(title, problem.Title);
        Assert.Contains(detail, problem.Detail, StringComparison.Ordinal);
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

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string relativeUri, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    /// <summary>The fields of an RFC 7807 ProblemDetails body the assertions read.</summary>
    private sealed record ProblemPayload(string? Title, int Status, string? Detail);
}
