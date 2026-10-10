using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The similar-questions endpoint's contract at the HTTP boundary. The ranking itself is covered against a
/// real catalog by <see cref="QuestionSearchTests"/>; what matters here is that a deployment WITHOUT retrieval
/// configured says so distinctly, rather than returning an empty result a caller would read as "nothing is
/// close to your question". Those two answers demand opposite follow-ups, so conflating them would have an
/// assistant tell someone their estate has no matching dashboard when in truth it never looked.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SimilarQuestionsApiTests
{
    [SkippableFact]
    public async Task WhenRetrievalIsDisabled_SaysNotConfigured_RatherThanReturningNoMatches()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // Retrieval is off in the shipped defaults, which registers no embedding provider at all.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueReadTokenAsync(client);

        using var response = await SendAsync(
            client, token, "/api/v1/lineage/subscribers/similar-questions?question=what%20is%20our%20revenue");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PowerAI:Retrieval:Enabled", body, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task AnEmptyQuestion_IsRejected()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs);
        using var client = factory.CreateClient();
        var token = await IssueReadTokenAsync(client);

        using var response = await SendAsync(
            client, token, "/api/v1/lineage/subscribers/similar-questions?question=%20%20");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// An assistant expands the question itself and sends the vocabulary as repeated <c>expandedTerms</c>
    /// keys, which is what lets a deployment with no server-side expansion (no model key) still find a question
    /// worded differently. The same request without them searches the typed words alone and misses it.
    /// </summary>
    [SkippableFact]
    public async Task CallerExpandedTerms_FindAQuestionTheTypedWordsMiss()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var vocabulary = $"takings{suffix}";
        var question = $"What are total {vocabulary} by country?";
        var sql = "SELECT Country, SUM(Amount) FROM Orders GROUP BY Country";
        var hash = SemanticExampleHash.Compute(question, sql);

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.SemanticExamples.Add(new CatalogSemanticExample
            {
                RepoId = repoId,
                Question = question,
                Sql = sql,
                ObjectKeys = "[Dw].[arc].[Orders]",
                Provenance = SemanticExampleProvenance.UserConfirmed,
                ConfirmedUtc = DateTime.UtcNow,
                ConfirmedBy = "analyst@example.com",
                ContentHash = hash,
            });
            await db.SaveChangesAsync();

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
                .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");
            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            var typed = Uri.EscapeDataString($"what drives our turnover {suffix}");
            var basePath = $"/api/v1/lineage/subscribers/similar-questions?question={typed}&repoId={repoId}";

            // The full-text index fills asynchronously, so poll until the expanded request finds the row.
            var expandedPath = $"{basePath}&expandedTerms=turnover&expandedTerms={Uri.EscapeDataString(vocabulary)}";
            SimilarQuestionsDto? expanded = null;
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                expanded = await GetAsync(client, token, expandedPath);
                if (expanded.Matches.Any(m => m.Question == question))
                {
                    break;
                }

                await Task.Delay(250);
            }

            Assert.NotNull(expanded);
            Assert.Equal(["turnover", vocabulary], expanded.SearchedTerms);
            var match = Assert.Single(expanded.Matches, m => m.Question == question);
            Assert.Equal(sql, match.Sql);
            Assert.Equal([vocabulary], match.MatchedTerms);

            // Without the caller's terms (and with server expansion off) only the typed words are searched.
            var plain = await GetAsync(client, token, basePath);
            Assert.DoesNotContain(vocabulary, plain.SearchedTerms);
            Assert.DoesNotContain(plain.Matches, m => m.Question == question);
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
        }
    }

    private static async Task<SimilarQuestionsDto> GetAsync(HttpClient client, string token, string relativeUri)
    {
        using var response = await SendAsync(client, token, relativeUri);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<SimilarQuestionsDto>();
        Assert.NotNull(body);
        return body;
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

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string token, string relativeUri)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(relativeUri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
