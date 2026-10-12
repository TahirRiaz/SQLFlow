using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The semantic layer's saved answers at the HTTP boundary: they are listed and searched, a deleted answer is gone, an
/// answer an AI context gives is not deleted here (its file is where it is removed, so the file and what is served never
/// disagree), and only an admin can do any of it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticExampleAdminApiTests
{
    private const string Route = "/api/v1/powerai/semantic-layer/examples";

    [SkippableFact]
    public async Task AnAnswerAnAiContextGives_IsNotDeletedHere_AndStaysServed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var context = "orders-" + suffix;
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            var given = Seed(db, $"How many orders were placed ({suffix})?", "SELECT 1 AS Orders");
            given.Provenance = SemanticExampleProvenance.AiContext;
            given.AiContext = context;
            await db.SaveChangesAsync();

            using (var response = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/{given.Id}", body: null))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
                Assert.Contains(context, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            Assert.True(await db.SemanticExamples.AnyAsync(e => e.Id == given.Id));

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}/{given.Id}", body: null))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<SemanticExampleAdminDto>();
                Assert.NotNull(body);
                Assert.Equal(SemanticExampleProvenance.AiContext, body.Provenance);
            }
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.Question.Contains(suffix)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task TheList_SearchesQuestionAndQuery_AndADeletedAnswerIsGone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate", "admin"]);
        await using var db = CatalogDatabase.Create(cs);

        try
        {
            var byQuestion = Seed(db, $"Which region sells most ({suffix})?", "SELECT 1 AS Region");
            var bySql = Seed(db, "Which product sells best?", $"SELECT 1 AS Product_{suffix}");
            await db.SaveChangesAsync();

            using (var response = await SendAsync(client, token, HttpMethod.Get, $"{Route}?search={suffix}", body: null))
            {
                response.EnsureSuccessStatusCode();
                var page = await response.Content.ReadFromJsonAsync<PagedResult<SemanticExampleAdminDto>>();
                Assert.NotNull(page);
                Assert.Equal(2, page.Total);
                Assert.Contains(page.Items, item => item.Id == byQuestion.Id);
                Assert.Contains(page.Items, item => item.Id == bySql.Id);
            }

            using (var response = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/{byQuestion.Id}", body: null))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            Assert.False(await db.SemanticExamples.AnyAsync(e => e.Id == byQuestion.Id));

            using (var response = await SendAsync(client, token, HttpMethod.Delete, $"{Route}/{byQuestion.Id}", body: null))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.Question.Contains(suffix) || e.Sql.Contains(suffix)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task CuratingTheLayersExamples_RequiresTheAdminScope()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // Confirming is an operate-scope action, but removing precedent other people's answers rest on is not.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        using var response = await SendAsync(client, token, HttpMethod.Get, Route, body: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static CatalogSemanticExample Seed(CatalogDbContext db, string question, string sql)
    {
        var example = new CatalogSemanticExample
        {
            Question = question,
            Sql = sql,
            ObjectKeys = string.Empty,
            Provenance = SemanticExampleProvenance.UserConfirmed,
            ConfirmedUtc = DateTime.UtcNow,
            ContentHash = SemanticExampleHash.Compute(question, sql),
        };
        db.SemanticExamples.Add(example);
        return example;
    }

    private static async Task<string> IssueTokenAsync(HttpClient client, string[] scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, string token, HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
