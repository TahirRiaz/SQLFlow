using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Whether the estate REUSES an answer someone already checked instead of writing a new query for the same
/// question, and whether the answer it reuses is still right.
///
/// <see cref="PowerAiProofValueTests"/> proves the answers are correct, but it starts from SQL already in hand:
/// it would pass unchanged in a system that regenerated every query from scratch and never reused anything.
/// <see cref="QuestionSearchTests"/> proves the RANKING works, against questions invented for the test. Neither
/// covers the step between them, which is the one that decides whether a person is asked: a stored question is
/// found from different words, its stored SQL comes back rather than a new one, and a trusted match runs without
/// a confirmation gate while everything else falls back to prepare/run.
///
/// The corpus here is the proof-value fixture, so reuse is not merely observed but proven correct: a question is
/// stored, asked again in different words, and the SQL that comes back is run and compared to the value that
/// question is known to produce. A regression that returned a plausible but different query fails on the value.
///
/// One environmental note the assertions depend on. Retrieval ranks through a SQL Server full-text index where
/// the instance has one, and falls back to LIKE where it does not (both paths live in
/// <c>QuestionSearch.MatchingExamplesAsync</c>). The questions seeded here are matched by whole words that occur
/// literally in the stored question, so they are found on EITHER path and these tests do not require the
/// Full-Text feature to be installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PowerAiQuestionReuseTests
{
    private const string SimilarRoute = "/api/v1/lineage/subscribers/similar-questions";
    private const string ConfirmRoute = "/api/v1/powerai/questions/confirm";

    /// <summary>How long a seeded example may take to become findable. Immediate on the LIKE path; on a
    /// full-text instance the index is populated asynchronously, so a search can legitimately miss it at first.
    /// </summary>
    private static readonly TimeSpan IndexBudget = TimeSpan.FromSeconds(30);

    // ---- the question is found again, and its own SQL comes back ---------------------------------------------

    /// <summary>
    /// The whole point of the confirmed-example store: a question a person checked is found again when someone
    /// else asks the SAME thing in DIFFERENT words, and what comes back is the SQL that was stored rather than a
    /// fresh guess. The reused SQL is then RUN, and must still produce the value that question is known to
    /// answer, so this proves reuse is correct rather than merely that something was returned.
    /// </summary>
    [SkippableFact]
    public async Task AStoredQuestion_IsFoundFromDifferentWords_AndItsOwnSqlIsWhatComesBack()
    {
        var cs = CatalogTestDb.Require();
        var adventureWorks = AdventureWorksDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        // A question from the proof-value fixture, so the SQL that comes back can be checked against the value
        // it is known to produce rather than merely against itself.
        var proof = ProofValues.ById("q127");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);

        // Stored and asked with DIFFERENT wording. They share the words a search can match on
        // ("reseller", "sales", "region"), which is what makes this a retrieval test rather than a lookup.
        var storedQuestion = $"What are reseller sales by territory region {suffix}?";
        var askedDifferently = $"Show me the reseller sales broken down per region {suffix}";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            // 1. A person confirms the answer. This is the ONLY way the estate learns it.
            var stored = await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            // 2. Somebody else asks the same thing in their own words.
            var match = await FindWhenIndexedAsync(
                client, token, askedDifferently,
                m => string.Equals(m.Question, storedQuestion, StringComparison.Ordinal));

            Assert.NotNull(match);

            // 3. What came back is the STORED answer, verbatim, attributed to the person who checked it.
            Assert.Equal(proof.Sql, match.Sql);
            Assert.Equal(SemanticExampleProvenance.UserConfirmed, match.Provenance);
            Assert.Equal(stored.ExampleId, match.ExampleId);

            // 4. And the reused SQL still answers the question it claims to: run it and compare to the value
            //    this question is known to produce. Reuse of a query that no longer works is not reuse.
            var rows = await ProofValues.RunAsync(adventureWorks, match.Sql);
            Assert.Equal(proof.Rows, rows);
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    // ---- the trust boundary, in BOTH directions ---------------------------------------------------------------

    /// <summary>
    /// The trust flag is what a caller acts on to decide whether to reuse an answer outright or offer it for
    /// confirmation, so it is asserted in BOTH directions against the SAME stored question. A flag that is
    /// always true would reuse unrelated queries; always false would mean the store is never used, which is
    /// invisible from the outside because the answers stay correct. Only one of those is a security problem,
    /// but both are bugs, and a one-sided test catches neither reliably.
    ///
    /// The threshold counts distinct word STEMS matched, so the two cases differ by how many meaningful words
    /// the asked question shares with the stored one, not by anything the test sets directly.
    /// </summary>
    [SkippableFact]
    public async Task AMatchIsTrustedOnlyWhenItClearsTheThreshold_AndAWeakOneIsNot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q141");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"Which products sold the most to resellers in {suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false")
            // Stated rather than assumed: these assertions are about the threshold, so the test pins it instead
            // of inheriting whatever the shipped default happens to be.
            .WithSetting("ControlPlane:PowerAI:Retrieval:RankThreshold", "2");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            // Two meaningful words in common ("products", "resellers"), which is what the threshold asks for.
            var strong = await FindWhenIndexedAsync(
                client, token, $"which products do resellers buy in {suffix}",
                m => string.Equals(m.Question, storedQuestion, StringComparison.Ordinal));
            Assert.NotNull(strong);
            Assert.True(
                strong.Score >= 2,
                $"Expected the strong match to score at least the threshold, scored {strong.Score} on "
                + $"[{string.Join(", ", strong.MatchedTerms)}].");
            Assert.True(strong.Trusted, "A match at or above the threshold must be reported as trusted.");

            // One meaningful word in common. A single shared word is routinely coincidence, which is the whole
            // reason the threshold is two, so this must NOT come back trusted even though it is a real match.
            var weak = await FindAsync(client, token, $"how many products are there in {suffix}");
            var weakMatch = weak.Matches.FirstOrDefault(
                m => string.Equals(m.Question, storedQuestion, StringComparison.Ordinal));
            if (weakMatch is not null)
            {
                Assert.True(
                    weakMatch.Score < 2,
                    $"Expected a single shared word to score below the threshold, scored {weakMatch.Score} on "
                    + $"[{string.Join(", ", weakMatch.MatchedTerms)}].");
                Assert.False(
                    weakMatch.Trusted,
                    "A match below the threshold must not be reported as trusted: one shared word is "
                    + "routinely coincidence.");
            }
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    /// <summary>
    /// A question typed exactly as it was stored is the same question, and is trusted on that basis even though
    /// it may share too few meaningful words to clear the threshold on score alone. Without this, the shortest
    /// and most common questions ("how many customers do we have?") could never be reused, which is precisely
    /// backwards: those are the ones asked most often.
    /// </summary>
    [SkippableFact]
    public async Task AQuestionAskedExactlyAsItWasStored_IsTrustedEvenWhenItIsTooShortToScore()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q006");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);

        // One meaningful word besides the suffix, so it cannot reach a threshold of two on score.
        var storedQuestion = $"How many customers{suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false")
            .WithSetting("ControlPlane:PowerAI:Retrieval:RankThreshold", "2");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            var match = await FindWhenIndexedAsync(
                client, token, storedQuestion,
                m => string.Equals(m.Question, storedQuestion, StringComparison.Ordinal));

            Assert.NotNull(match);
            Assert.True(
                match.Trusted,
                $"The same question must be trusted on identity; it scored {match.Score} against a threshold "
                + "of 2, so nothing but identity can be carrying it here.");
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    // ---- reuse runs without a human gate, and only where it is switched on -----------------------------------

    /// <summary>
    /// Auto-run is the step that actually skips the confirmation gate, so what matters is that it runs the
    /// STORED SQL and returns the STORED question's real answer. The result is compared to the proof value, so
    /// a regression that ran a different query, or returned a stale cached document, fails on the value rather
    /// than passing on the shape.
    ///
    /// It is also the one place the product executes SQL without a person seeing it first, so the response's
    /// own claims about the caps it applied are checked too.
    /// </summary>
    [SkippableFact]
    public async Task ATrustedMatch_RunsItsStoredSqlWithoutAConfirmationGate_AndReturnsTheRealAnswer()
    {
        var cs = CatalogTestDb.Require();
        AdventureWorksDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q127");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"What are reseller sales by territory region {suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false")
            .WithSetting("ControlPlane:PowerAI:Retrieval:AutoRun:Enabled", "true");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            var stored = await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            // No prepare, no token, no approval: the confirmation happened when the answer was stored.
            using var response = await PostAsync(
                client, token, $"/api/v1/powerai/questions/{stored.ExampleId}/auto-run", new { });
            response.EnsureSuccessStatusCode();
            var run = await response.Content.ReadFromJsonAsync<AutoRunResultDto>();
            Assert.NotNull(run);

            Assert.True(run.Ran, $"Auto-run did not run the trusted match: {run.Message}");
            Assert.Equal(stored.ExampleId, run.ExampleId);
            Assert.Equal(storedQuestion, run.Question);
            Assert.Equal(proof.Sql, run.Sql);
            Assert.NotNull(run.Result);

            // The answer itself, checked against the value this question is known to produce.
            var result = run.Result.Value;
            // A column with no name would be a malformed result document rather than a mismatch, so the names
            // are required before they are compared.
            var columns = result.GetProperty("columns").EnumerateArray()
                .Select(c => c.GetProperty("name").GetString() ?? string.Empty).ToArray();
            Assert.Equal(proof.Columns, columns);
            Assert.Equal(proof.Rows.Count, result.GetProperty("rowCount").GetInt32());
            Assert.False(result.GetProperty("truncated").GetBoolean());
            Assert.Equal(proof.Rows, ReadRows(result));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    /// <summary>
    /// With auto-run switched off, reuse must fall back to the manual gate rather than quietly running anyway.
    /// This is the other half of the switch, and it is the half that matters: a deployment that has not opted
    /// in to running queries unattended must not run one, no matter how well a question matched.
    /// </summary>
    [SkippableFact]
    public async Task WithAutoRunOff_AReusableMatchIsNotRun_AndTheCallerIsSentToTheManualGate()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q127");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"What are reseller sales by territory region {suffix}?";

        // Retrieval on, auto-run off: the shipped default, and the case a deployment lands in by doing nothing.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:DataOps:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            var stored = await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            using var response = await PostAsync(
                client, token, $"/api/v1/powerai/questions/{stored.ExampleId}/auto-run", new { });

            Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);

            // The refusal must point at the manual path, since a caller that cannot tell "off" from "failed"
            // will either give up or retry forever instead of falling back.
            var problem = await response.Content.ReadAsStringAsync(CancellationToken.None);
            Assert.Contains("prepare_query", problem, StringComparison.Ordinal);

            // Nothing ran.
            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(0, await db.ComputeTasks.CountAsync(t => t.SourceRef == AdventureWorksRef));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    /// <summary>
    /// Auto-run executes a query, so it belongs to the data-operations surface and must be refused when that
    /// surface is off, even with retrieval and auto-run both switched on. Reuse is not a way around the kill
    /// switch: a deployment that has turned off ad-hoc querying has turned off ad-hoc querying.
    /// </summary>
    [SkippableFact]
    public async Task WithDataOpsOff_AutoRunIsRefused_EvenThoughRetrievalAndAutoRunAreOn()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q127");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"What are reseller sales by territory region {suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false")
            .WithSetting("ControlPlane:PowerAI:Retrieval:AutoRun:Enabled", "true");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            var stored = await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            using var response = await PostAsync(
                client, token, $"/api/v1/powerai/questions/{stored.ExampleId}/auto-run", new { });

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(0, await db.ComputeTasks.CountAsync(t => t.SourceRef == AdventureWorksRef));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    /// <summary>
    /// A question nobody has stored must come back with NO match rather than the nearest thing in the store.
    /// This is the failure mode that matters most in the reuse direction: reusing a query for a question it
    /// does not answer produces a confident, plausible, wrong number, which is worse than writing a new query.
    /// </summary>
    [SkippableFact]
    public async Task AQuestionNobodyHasStored_FindsNothing_RatherThanTheNearestStoredAnswer()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q127");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"What are reseller sales by territory region {suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            await ConfirmAsync(client, token, storedQuestion, proof.Sql);
            // Shares no meaningful word with anything stored here.
            var result = await FindAsync(client, token, $"what is the warehouse humidity {suffix}");

            Assert.DoesNotContain(
                result.Matches,
                m => string.Equals(m.Question, storedQuestion, StringComparison.Ordinal));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    /// <summary>
    /// Confirming the same question twice must refresh ONE stored answer rather than stack duplicates. Left
    /// unchecked, the store fills with identical rows that each score identically, so every later search spends
    /// its whole result budget on copies of one answer and the genuinely different ones fall off the end.
    /// </summary>
    [SkippableFact]
    public async Task ConfirmingTheSameAnswerTwice_LeavesOneReusableExample()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var proof = ProofValues.ById("q128");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedCatalogObjectsAsync(cs, suffix);
        var storedQuestion = $"What are reseller sales by country {suffix}?";

        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:PowerAI:Retrieval:Enabled", "true")
            .WithSetting("ControlPlane:PowerAI:Retrieval:ExpandSynonyms", "false");
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read", "operate"]);

        var hash = SemanticExampleHash.Compute(storedQuestion, proof.Sql);
        try
        {
            var request = new ConfirmQuestionRequest(
                storedQuestion, proof.Sql, QuestionConfirmationOutcome.Accepted, null, 3, null, AdventureWorksRef);

            using var first = await PostAsync(client, token, ConfirmRoute, request);
            first.EnsureSuccessStatusCode();
            var one = await first.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
            Assert.NotNull(one);

            using var second = await PostAsync(client, token, ConfirmRoute, request);
            second.EnsureSuccessStatusCode();
            var two = await second.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
            Assert.NotNull(two);

            // The same row, refreshed.
            Assert.Equal(one.ExampleId, two.ExampleId);

            await using var db = CatalogDatabase.Create(cs);
            Assert.Equal(1, await db.SemanticExamples.CountAsync(e => e.ContentHash == hash));
        }
        finally
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
            }

            await RemoveCatalogObjectsAsync(cs, suffix);
        }
    }

    // ---- helpers ----------------------------------------------------------------------------------------------


    /// <summary>The AdventureWorks tables the stored questions read, as the catalog knows them.</summary>
    private static readonly string[] SampleTables =
    [
        "FactResellerSales", "DimProduct", "DimReseller", "DimSalesTerritory", "DimDate", "DimCustomer",
    ];

    /// <summary>
    /// Registers the sample's tables in the catalog, as a schema sync would.
    ///
    /// The column-policy guard refuses any query naming a table the catalog has no record of, so without this
    /// every confirmation here is refused before retrieval is ever reached. Seeding it is not a workaround: an
    /// example may only be stored for objects the estate actually knows, and these tests assert reuse on top of
    /// that rule rather than around it. Rows are keyed per test run so concurrent runs cannot collide, and are
    /// removed in the same finally as the example they support.
    /// </summary>
    private static async Task SeedCatalogObjectsAsync(string catalogConnection, string runKey)
    {
        var now = DateTime.UtcNow;
        await using var db = CatalogDatabase.Create(catalogConnection);
        foreach (var table in SampleTables)
        {
            db.Objects.Add(new CatalogObject
            {
                Key = ObjectKeyFor(table, runKey),
                ServerRef = AdventureWorksRef,
                Database = "AdventureWorks",
                Schema = "dbo",
                Name = table,
                Kind = "Table",
                FirstSeenUtc = now,
                LastSeenUtc = now,
            });
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    /// <summary>The catalog key one seeded table is registered under for this run.</summary>
    private static string ObjectKeyFor(string table, string runKey)
        => $"{AdventureWorksRef}|adventureworks|dbo|{table.ToLowerInvariant()}|{runKey}";

    /// <summary>Removes this run's seeded catalog rows.</summary>
    private static async Task RemoveCatalogObjectsAsync(string catalogConnection, string runKey)
    {
        await using var db = CatalogDatabase.Create(catalogConnection);
        await db.Objects.Where(o => o.Key.EndsWith("|" + runKey)).ExecuteDeleteAsync();
    }

    /// <summary>The datasource the proof values were captured against, as the semantic layer references it.</summary>
    private const string AdventureWorksRef = "${env:SQLFLOW_ADVENTUREWORKS_DB}";

    /// <summary>Reads the rows out of an auto-run result document, in the executor's own string rendering, so
    /// they compare directly to a stored proof value.</summary>
    private static List<IReadOnlyList<string?>> ReadRows(JsonElement result)
    {
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var row in result.GetProperty("rows").EnumerateArray())
        {
            rows.Add(row.EnumerateArray()
                .Select(c => c.ValueKind == JsonValueKind.Null ? null : c.GetString())
                .ToArray());
        }

        return rows;
    }

    /// <summary>Searches, retrying until <paramref name="found"/> is satisfied or the index budget runs out.
    /// Returns the match, or null when it never appeared.</summary>
    private static async Task<SimilarQuestionDto?> FindWhenIndexedAsync(
        HttpClient client, string token, string question, Func<SimilarQuestionDto, bool> found)
    {
        var deadline = DateTime.UtcNow + IndexBudget;
        while (true)
        {
            var result = await FindAsync(client, token, question);
            var match = result.Matches.FirstOrDefault(found);
            if (match is not null || DateTime.UtcNow >= deadline)
            {
                return match;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
    }

    private static async Task<SimilarQuestionsDto> FindAsync(HttpClient client, string token, string question)
    {
        var uri = $"{SimilarRoute}?question={Uri.EscapeDataString(question)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<SimilarQuestionsDto>();
        Assert.NotNull(result);
        return result;
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

    /// <summary>Confirms an answer, failing with the server's own explanation rather than a bare status code:
    /// every refusal here names exactly which rule the confirmation broke, which is the fact a reader needs.</summary>
    private static async Task<ConfirmedQuestionDto> ConfirmAsync(
        HttpClient client, string token, string question, string sql)
    {
        using var response = await PostAsync(client, token, ConfirmRoute, new ConfirmQuestionRequest(
            question, sql, QuestionConfirmationOutcome.Accepted, ObjectKeys: null, Confidence: 3,
            RepoId: null, SourceRef: null));
        Assert.True(
            response.IsSuccessStatusCode,
            $"Confirming the example was refused with {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync(CancellationToken.None));

        var stored = await response.Content.ReadFromJsonAsync<ConfirmedQuestionDto>();
        Assert.NotNull(stored);
        Assert.True(stored.Stored, $"The confirmation stored nothing: {stored.Message}");
        Assert.NotNull(stored.ExampleId);
        return stored;
    }

    private static async Task<HttpResponseMessage> PostAsync<T>(
        HttpClient client, string token, string relativeUri, T body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relativeUri, UriKind.Relative))
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }
}
