using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Curating the semantic layer's example queries (the saved answers) at the HTTP boundary. What matters is that
/// curation cannot put anything into the store the confirm endpoint would refuse (an edit is re-validated and its
/// duplicate hash recomputed), that a deleted answer is gone, and that only an admin can do either.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SemanticExampleAdminApiTests
{
    private const string Route = "/api/v1/powerai/semantic-layer/examples";

    [SkippableFact]
    public async Task AnEdit_IsRevalidated_RecomputesItsHash_AndCannotDuplicateAnotherAnswer()
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
            var edited = Seed(db, $"How many trips ran ({suffix})?", "SELECT 1 AS Trips");
            var other = Seed(db, $"How many stops are there ({suffix})?", "SELECT 2 AS Stops");
            await db.SaveChangesAsync();

            var newQuestion = $"How many trips ran yesterday ({suffix})?";
            const string NewSql = "SELECT 3 AS Trips";
            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/{edited.Id}",
                new UpdateSemanticExampleRequest(newQuestion, NewSql, SourceRef: null)))
            {
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<SemanticExampleAdminDto>();
                Assert.NotNull(body);
                Assert.Equal(newQuestion, body.Question);
                Assert.Equal(NewSql, body.Sql);
                Assert.Null(body.Problem);
            }

            var stored = await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.Id == edited.Id);
            Assert.Equal(SemanticExampleHash.Compute(newQuestion, NewSql), stored.ContentHash);
            Assert.False(string.IsNullOrEmpty(stored.ConfirmedBy));

            // A query that would write is refused exactly as confirming refuses it, and the row is left as it was.
            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/{edited.Id}",
                new UpdateSemanticExampleRequest(newQuestion, "DELETE FROM Trips", SourceRef: null)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            Assert.Equal(NewSql, (await db.SemanticExamples.AsNoTracking().SingleAsync(e => e.Id == edited.Id)).Sql);

            // Making it identical to another saved answer would leave two rows scoring the same in every search.
            using (var response = await SendAsync(client, token, HttpMethod.Put, $"{Route}/{edited.Id}",
                new UpdateSemanticExampleRequest(other.Question, other.Sql, SourceRef: null)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
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
            var byQuestion = Seed(db, $"Which depot is busiest ({suffix})?", "SELECT 1 AS Depot");
            var bySql = Seed(db, "Which route is longest?", $"SELECT 1 AS Route_{suffix}");
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

        // Confirming is an operate-scope action, but rewriting precedent other people's answers rest on is not.
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
