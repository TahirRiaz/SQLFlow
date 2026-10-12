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
        Assert.Contains("ControlPlane__AiContext__Enabled", body, StringComparison.Ordinal);
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

        // Letters only, so the word carrying it is a word the stored question is about, not a value.
        var suffix = string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + (c % 16))));
        var repoId = Guid.NewGuid();
        var vocabulary = $"takings{suffix}";
        var question = $"What are our total {vocabulary}?";
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
                .WithSetting("ControlPlane:AiContext:Enabled", "true")
                .WithSetting("ControlPlane:AiContext:ExpandSynonyms", "false");
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

    /// <summary>
    /// A stored answer is written for the values it names. A question that shares its words but names another year
    /// scores as well as its twin and is still not answered by it, so it comes back untrusted, with the values that
    /// differ, for a caller to adapt the SQL rather than run it; the question naming the same year is trusted.
    /// </summary>
    [SkippableFact]
    public async Task AStoredAnswerNamingOtherValues_IsNotTrusted_AndSaysWhichValuesDiffer()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // Letters only, so the word carrying it is no value of its own.
        var suffix = string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + (c % 16))));
        var repoId = Guid.NewGuid();
        var vocabulary = $"takings{suffix}";
        var question = $"What were {vocabulary} by region in 2024?";
        var sql = "SELECT Region, SUM(Amount) FROM Orders WHERE OrderYear = 2024 GROUP BY Region";
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
                .WithSetting("ControlPlane:AiContext:Enabled", "true")
                .WithSetting("ControlPlane:AiContext:ExpandSynonyms", "false");
            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            // Both questions share the two searched words, which clears the shipped threshold of two.
            string PathFor(string typed)
                => $"/api/v1/lineage/subscribers/similar-questions?question={Uri.EscapeDataString(typed)}&repoId={repoId}"
                    + $"&expandedTerms={Uri.EscapeDataString(vocabulary)}&expandedTerms=region";

            var other = await FindWhenIndexedAsync(client, token, PathFor($"what were {vocabulary} by region in 2023"), question);
            Assert.Equal(2, other.Score);
            Assert.False(other.Trusted);
            Assert.Equal(["2023", "2024"], other.DifferingValues);
            Assert.Equal(sql, other.Sql);

            var same = await FindWhenIndexedAsync(client, token, PathFor($"{vocabulary} by region in 2024"), question);
            Assert.Equal(2, same.Score);
            Assert.True(same.Trusted);
            Assert.Empty(same.DifferingValues ?? ["missing"]);
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// A dashboard match's <c>reportSql</c> is the visual's own query against the report's model, evidence that is never
    /// run. While the match carries runnable <c>sql</c> it would only repeat it, so it is not sent; a visual whose query
    /// could not be translated has no <c>sql</c>, and then <c>reportSql</c> is what says which fields the report uses.
    /// </summary>
    [SkippableFact]
    public async Task ReportSql_IsSentOnlyForAVisualWhoseQueryCouldNotBeTranslated()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = string.Concat(Guid.NewGuid().ToString("N")[..8].Select(c => (char)('a' + (c % 16))));
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|reportsql_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";
        var translated = $"What is margin{suffix} by store?";
        var untranslated = $"What is margin{suffix} by region?";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
            {
                RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey,
                ReportFile = "report.pbix", Ordinal = 1, DisplayName = "Page 1",
            });
            AddVisual(db, repoId, subscriberKey, pageKey, 1, translated, "EVALUATE 'Margin by store'", "SELECT Store FROM dbo.Margin");
            AddVisual(db, repoId, subscriberKey, pageKey, 2, untranslated, "EVALUATE 'Margin by region'", sourceSql: null);
            await db.SaveChangesAsync();

            await using var factory = new ControlPlaneAppFactory()
                .WithCatalog(cs)
                .WithSetting("ControlPlane:AiContext:Enabled", "true")
                .WithSetting("ControlPlane:AiContext:ExpandSynonyms", "false");
            using var client = factory.CreateClient();
            var token = await IssueReadTokenAsync(client);

            string PathFor(string typed)
                => $"/api/v1/lineage/subscribers/similar-questions?question={Uri.EscapeDataString(typed)}&repoId={repoId}";

            var runnable = await FindWhenIndexedAsync(client, token, PathFor($"margin{suffix} by store"), translated);
            Assert.Equal("SELECT Store FROM dbo.Margin", runnable.Sql);
            Assert.Null(runnable.ReportSql);
            Assert.Null(runnable.TranslationProblem);

            var lead = await FindWhenIndexedAsync(client, token, PathFor($"margin{suffix} by region"), untranslated);
            Assert.Equal(string.Empty, lead.Sql);
            Assert.Equal("EVALUATE 'Margin by region'", lead.ReportSql);
            Assert.NotNull(lead.TranslationProblem);
        }
        finally
        {
            await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberQueries.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
            await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }
    }

    /// <summary>Adds one report visual asking <paramref name="question"/>, with its query: the report's own
    /// (<paramref name="reportSql"/>) and, when it could be translated, the one over the source tables.</summary>
    private static void AddVisual(
        CatalogDbContext db, Guid repoId, string subscriberKey, string pageKey, int ordinal, string question,
        string reportSql, string? sourceSql)
    {
        var visualKey = $"{pageKey}#{ordinal}";
        var title = $"Visual {ordinal}";
        var queryName = $"report.pbix / Page 1 / {title}";
        db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
        {
            RepoId = repoId, PageKey = pageKey, VisualKey = visualKey, Ordinal = ordinal,
            VisualType = "barChart", Title = title, QueryName = queryName,
            ContentHash = SubscriberReportVisualHash.Compute(title, "barChart", []),
        });
        db.SubscriberQueries.Add(new CatalogSubscriberQuery
        {
            RepoId = repoId, SubscriberKey = subscriberKey, Ordinal = ordinal, Name = queryName,
            ServerRef = "dw", Sql = reportSql, ObjectKeys = "[Dw].[dbo].[Margin]",
            SourceSql = sourceSql,
            TranslationProblem = sourceSql is null ? "the report is not translatable here" : null,
        });
        db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
        {
            RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = question,
        });
    }

    /// <summary>The match for <paramref name="question"/>, polling while the full-text index catches up with the row.</summary>
    private static async Task<SimilarQuestionDto> FindWhenIndexedAsync(
        HttpClient client, string token, string relativeUri, string question)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var result = await GetAsync(client, token, relativeUri);
            if (result.Matches.FirstOrDefault(m => m.Question == question) is { } match)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"'{question}' was not found within 30s at {relativeUri}.");
            }

            await Task.Delay(250);
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
