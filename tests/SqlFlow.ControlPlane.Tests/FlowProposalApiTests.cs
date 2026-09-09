using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The authoring endpoint's authorization and input validation. The git-push-and-open-pull-request path needs a live
/// remote and is out of scope for the in-memory host; these cover the guards that run before any git or database
/// work: the "author" scope requirement, and the request/file validation that rejects a bad proposal up front.
/// </summary>
public sealed class FlowProposalApiTests
{
    private static readonly Guid SourceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Propose_WithAnyAuthenticatedToken_IsAuthorized()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        // Authoring pull-request proposals is part of the product every authenticated user gets: only user
        // administration is scope-gated. A token WITHOUT any authoring scope therefore reaches the authoring
        // surface, whose title guard rejects a blank title with a 400 before any repo or git work (proving it was
        // not fenced off at 403).
        var token = await IssueTokenAsync(client, ["read"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("   ", null, null, null,
                [new ProposeFlowsFile("flows/orders.01_pre.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Propose_WithoutAnyToken_Returns401()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, ProposalUri())
        {
            Content = JsonContent.Create(new ProposeFlowsRequest("t", null, null, null,
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")])),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Propose_BlankTitle_Returns400_BeforeTouchingGitOrDatabase()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("   ", null, null, null,
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Propose_NoFiles_Returns400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, null, []));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("../secrets.yaml")]           // traversal
    [InlineData("/etc/passwd.yaml")]          // absolute
    [InlineData("flows/./orders.flow.yaml")]  // '.' segment
    [InlineData("flows/run.exe")]             // disallowed extension
    public async Task Propose_UnsafeOrDisallowedPath_Returns400(string path)
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, null,
                [new ProposeFlowsFile(path, "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Propose_InvalidHeadBranchName_Returns400()
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["author"]);

        using var response = await PostAsync(client, token,
            new ProposeFlowsRequest("Add flows", null, null, "bad branch name",
                [new ProposeFlowsFile("flows/a.flow.yaml", "name: x")]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>The preflight runs before any git work, with the same loader the managed sync parses with: a
    /// proposal whose flow file would silently fail to import (here, an ing document with no source block) is
    /// rejected as 422 naming the loader's cause, so a merged-but-never-landing flow cannot happen. Gated on a
    /// reachable catalog database because the preflight resolves the target repo source first.</summary>
    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task Propose_FlowThatWouldNeverImport_Returns422_BeforeAnyGitWork()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceId = Guid.NewGuid();

        await using (var db = CatalogDatabase.Create(cs))
        {
            var now = DateTime.UtcNow;
            db.RepoSources.Add(new CatalogRepoSource
            {
                Id = sourceId,
                Name = $"preflight-{sourceId:N}",
                RemoteUrl = "https://github.com/example/preflight.git",
                Branch = "main",
                Enabled = false,
                SyncIntervalSeconds = 300,
                CreatedUtc = now,
                UpdatedUtc = now,
            });
            await db.SaveChangesAsync();
        }

        try
        {
            await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["author"]);

            using var response = await PostAsync(client, token,
                new ProposeFlowsRequest("Add broken flow", null, null, null,
                    [new ProposeFlowsFile("flows/broken.flow.yaml", "flowType: ing\nname: broken\n")]),
                sourceId);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var problem = await response.Content.ReadAsStringAsync();
            Assert.Contains("would never land", problem, StringComparison.Ordinal);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Id == sourceId).ExecuteDeleteAsync();
        }
    }

    private static Uri ProposalUri(Guid? sourceId = null)
        => new($"/api/v1/repos/sources/{sourceId ?? SourceId}/proposals", UriKind.Relative);

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client, string token, ProposeFlowsRequest body, Guid? sourceId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ProposalUri(sourceId))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
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
}
