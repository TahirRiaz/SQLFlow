using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The retrieval contract POWERAI.md Section 6 rests on: a newly typed question finds the stored question that
/// means the same thing even when the two are worded differently, and each match arrives with the SQL that
/// answers it plus a score the caller can gate on. Expansion is supplied by the test rather than by Anthropic,
/// so what is under test is the matching, scoring, and resolution rather than any model's vocabulary.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QuestionSearchTests
{
    [SkippableFact]
    public async Task ExpandedTerms_FindAQuestionWordedDifferently_AndCarryTheSqlThatAnswersIt()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|search_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region",
                    "[Dw].[arc].[Sales]", "Revenue by Region"),
                ("Which product category sells the most?", "SELECT Category, SUM(Amount) FROM Sales GROUP BY Category",
                    "[Dw].[arc].[Sales]", "Top Categories"),
                ("How many customers do we have?", "SELECT COUNT(*) FROM Customer", "[Dw].[arc].[Customer]",
                    "Customer Count"),
            ]);

            // The typed question shares no content word with the stored one ("turnover"/"territory" versus
            // "revenue"/"region"): the expansion is what closes that gap, which is the whole point of the
            // LLM step. Supplied here directly so the test does not depend on a model's wording.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "what was our turnover per territory", topK: 3,
                ["turnover", "revenue", "sales", "territory", "region", "product"], repoId,
                CancellationToken.None);

            // Two questions carry a searched term ("revenue"+"region", and "product"); the customer-count one
            // carries none and is absent entirely rather than ranked last with a zero score.
            Assert.Equal(2, result.Matches.Count);
            var best = result.Matches[0];
            Assert.Equal("What is our revenue by region?", best.Question);
            Assert.True(best.Score > result.Matches[1].Score,
                $"the intended match ({best.Score}) should outrank the runner-up ({result.Matches[1].Score})");
            Assert.DoesNotContain(result.Matches, m => m.Question.Contains("customers", StringComparison.Ordinal));

            // A match must carry what ANSWERS the question, not just the question text: the visual's query translated
            // to the source tables as the runnable SQL, and the report's own query as evidence.
            Assert.Equal(SourceSqlOf("SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region"), best.Sql);
            Assert.Equal("SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region", best.ReportSql);
            Assert.Null(best.TranslationProblem);
            Assert.Equal(["[Dw].[arc].[Sales]"], best.ObjectKeys);
            Assert.Equal(QuestionSearch.PowerBiProvenance, best.Provenance);
            Assert.Equal(subscriberKey, best.SubscriberKey);
            Assert.Equal("Revenue by Region", best.VisualTitle);

            // The caller can explain WHY it matched, not only how strongly.
            Assert.Contains("revenue", best.MatchedTerms);
            Assert.Contains("region", best.MatchedTerms);
            Assert.DoesNotContain("turnover", best.MatchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// Inflections match: the term "sell" reaches a question worded "sells", and "categories" reaches
    /// "category". Both the full-text path (via FORMSOF(INFLECTIONAL, ...)) and the LIKE fallback's scoring
    /// handle this, so the assertion holds on either kind of instance.
    /// <para>
    /// What does NOT match, verified directly against SQL Server: "sales" does not reach "sells". They are
    /// different lemmas (a noun and a verb), not two forms of one word, so neither the engine's stemmer nor a
    /// suffix rule connects them. That is precisely the case the LLM expansion exists to cover, by returning
    /// both words among its terms.
    /// </para>
    /// </summary>
    [SkippableFact]
    public async Task InflectionsMatch_ButDifferentLemmasDoNot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|stemfts_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("Which product category sells the most?", "SELECT 1", "[Dw].[arc].[Sales]", "Top Categories"),
            ]);

            // A different grammatical form of a word in the question.
            var inflected = await QuestionSearch.FindSimilarAsync(
                db, "sell", topK: 3, ["sell"], repoId, CancellationToken.None);
            Assert.Single(inflected.Matches);

            // A plural whose singular appears in the question.
            var plural = await QuestionSearch.FindSimilarAsync(
                db, "categories", topK: 3, ["categories"], repoId, CancellationToken.None);
            Assert.Single(plural.Matches);

            // A different lemma entirely: no stemmer bridges this, which is what expansion is for.
            var otherLemma = await QuestionSearch.FindSimilarAsync(
                db, "sales", topK: 3, ["sales"], repoId, CancellationToken.None);
            Assert.Empty(otherLemma.Matches);

            // ...and with expansion supplying the related word, it is found.
            var expanded = await QuestionSearch.FindSimilarAsync(
                db, "sales", topK: 3, ["sales", "sell"], repoId, CancellationToken.None);
            Assert.Single(expanded.Matches);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    private static async Task<bool> HasFullTextAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT CAST(SERVERPROPERTY('IsFullTextInstalled') AS int)";
        return (int?)await command.ExecuteScalarAsync() == 1;
    }

    [SkippableFact]
    public async Task AStopWordQuestion_MatchesNothing_RatherThanEverything()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|stop_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            // Every word here is a stop word. Were they kept, they would match nearly every stored question
            // and rank noise above real vocabulary matches, so the correct answer is no match at all.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "what is our the", topK: 3, expander: null, repoId, CancellationToken.None);

            Assert.Empty(result.Matches);
            Assert.Empty(result.SearchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task WithNoExpander_TheTypedWordsStillSearch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|noexp_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            // Expansion off (or Anthropic unreachable): the search must still run on the typed words, so a
            // model outage degrades retrieval rather than breaking it.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "revenue by region", topK: 3, expander: null, repoId, CancellationToken.None);

            var match = Assert.Single(result.Matches);
            Assert.Equal("What is our revenue by region?", match.Question);
            Assert.Equal(2, match.Score);
            Assert.Equal(["revenue", "region"], result.SearchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// A caller's own expansion is held to the bound the server's expander uses: each term is its own predicate,
    /// so an assistant sending hundreds of terms must not turn one question into hundreds of index probes. Terms
    /// past the bound are ignored, which is observable as a term that only appears past it finding nothing.
    /// </summary>
    [SkippableFact]
    public async Task CallerTerms_PastTheExpanderBound_AreIgnored()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|termcap_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is our revenue by region?", "SELECT 1", "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            var fillers = Enumerable.Range(0, SqlFlow.Assistant.QuestionExpander.MaxTerms)
                .Select(i => $"filler{suffix}x{i}")
                .ToList();

            var pastTheBound = await QuestionSearch.FindSimilarAsync(
                db, "revenue", topK: 3, [.. fillers, "revenue"], repoId, CancellationToken.None);
            Assert.Empty(pastTheBound.Matches);
            Assert.Equal(SqlFlow.Assistant.QuestionExpander.MaxTerms, pastTheBound.SearchedTerms.Count);
            Assert.DoesNotContain("revenue", pastTheBound.SearchedTerms);

            var withinTheBound = await QuestionSearch.FindSimilarAsync(
                db, "revenue", topK: 3, ["revenue", .. fillers], repoId, CancellationToken.None);
            var match = Assert.Single(withinTheBound.Matches);
            Assert.Equal("What is our revenue by region?", match.Question);
            Assert.Equal(SqlFlow.Assistant.QuestionExpander.MaxTerms, withinTheBound.SearchedTerms.Count);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// Several grammatical forms of one word are one match, not several: an expansion routinely sends
    /// "customer" and "customers" together, and both hit the single word "customers" in a stored question.
    /// Scored as two, one shared word would clear a threshold of 2 by itself and make an unrelated stored
    /// query look trusted.
    /// </summary>
    [SkippableFact]
    public async Task FormsOfOneWord_ScoreOnce()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|forms_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("How many customers do we have?", "SELECT 1", "[Dw].[arc].[Customer]", "Customer Count"),
            ]);

            var result = await QuestionSearch.FindSimilarAsync(
                db, "total order amount for customers in Germany", topK: 3,
                ["total", "order", "amount", "customer", "customers", "germany"], repoId, CancellationToken.None);

            var match = Assert.Single(result.Matches);
            Assert.Equal(1, match.Score);
            Assert.Equal(["customer", "customers"], match.MatchedTerms);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// A short question has too few meaningful words for a term count to ever trust it: "how many customers do
    /// we have?" is one word ("customers") once stop words go, so even its verbatim twin scores 1. Identity is
    /// what closes that gap, and it must stay identity: a different question sharing the same word is not the
    /// same question, however it scores.
    /// </summary>
    [SkippableFact]
    public async Task AShortQuestionsTwin_IsTheSameQuestion_ButAQuestionSharingItsWordIsNot()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|same_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("How many customers do we have?", "SELECT COUNT(*) FROM Customer", "[Dw].[arc].[Customer]",
                    "Customer Count"),
                ("How many customers churned?", "SELECT 1", "[Dw].[arc].[Customer]", "Churn"),
            ]);

            // The typed question in the singular: inflection must not break identity.
            var result = await FindWhenIndexedAsync(
                db, "how many customer do we have", ["customer"], repoId, r => r.Matches.Count == 2);

            var twin = Assert.Single(result.Matches, m => m.Question == "How many customers do we have?");
            Assert.Equal(1, twin.Score);
            Assert.True(twin.SameQuestion);

            var other = Assert.Single(result.Matches, m => m.Question == "How many customers churned?");
            Assert.Equal(1, other.Score);
            Assert.False(other.SameQuestion);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task AWordIsMatchedWhole_SoSaleDoesNotMatchWholesale()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|word_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("How much wholesale volume did we move?", "SELECT 1", "[Dw].[arc].[Wholesale]", "Wholesale"),
            ]);

            // "sale" occurs INSIDE "wholesale". Scoring on substrings would call that a match and hand a
            // caller an unrelated dashboard, so the term must match on word boundaries.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "sale", topK: 3, expander: null, repoId, CancellationToken.None);

            Assert.Empty(result.Matches);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    [SkippableFact]
    public async Task ATermWithAnApostrophe_IsMatchedAsTextRatherThanBreakingTheQuery()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|quote_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("What is each customer's lifetime value?", "SELECT 1", "[Dw].[arc].[Customer]", "CLV"),
            ]);

            // A term carrying an apostrophe, and one carrying full-text operators, must be treated as text.
            // Were terms pasted into a predicate string these would break the query or change its meaning.
            var result = await QuestionSearch.FindSimilarAsync(
                db, "customer's lifetime value OR NEAR(\"x\")", topK: 3, expander: null, repoId,
                CancellationToken.None);

            var match = Assert.Single(result.Matches);
            Assert.Equal("What is each customer's lifetime value?", match.Question);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>The source SQL <see cref="SeedAsync"/> stores for a visual whose report query is <paramref name="sql"/>.</summary>
    private static string SourceSqlOf(string sql) => "/* source */ " + sql;

    /// <summary>
    /// Two reports can name their visuals identically ("Page 1 / Revenue") while asking different questions. A
    /// match must carry its own report's query, found by repo and subscriber as well as by name, and a visual whose
    /// query was not translated says so rather than offering the report's model SQL as something to run.
    /// </summary>
    [SkippableFact]
    public async Task AVisualsQuery_IsTheOneOfItsOwnReport_AndAnUntranslatedOneIsNotRunnable()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var firstKey = $"subscriber|scope_a_{suffix}";
        var secondKey = $"subscriber|scope_b_{suffix}";

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            await SeedAsync(db, repoId, firstKey, $"{firstKey}#report.pbix#1",
                [($"What is margin {suffix} by store?", "SELECT A FROM X", "[Dw].[arc].[X]", "Margin")]);
            await SeedAsync(db, repoId, secondKey, $"{secondKey}#report.pbix#1",
                [($"What is churn {suffix} by region?", "SELECT B FROM Y", "[Dw].[arc].[Y]", "Margin")],
                translated: false);

            var margin = await QuestionSearch.FindSimilarAsync(
                db, "margin", topK: 3, ["margin", suffix], repoId, CancellationToken.None);
            var match = Assert.Single(margin.Matches, m => m.Question.Contains("margin", StringComparison.Ordinal));
            Assert.Equal(SourceSqlOf("SELECT A FROM X"), match.Sql);
            Assert.Equal(firstKey, match.SubscriberKey);

            var churn = await QuestionSearch.FindSimilarAsync(
                db, "churn", topK: 3, ["churn", suffix], repoId, CancellationToken.None);
            var untranslated = Assert.Single(churn.Matches, m => m.Question.Contains("churn", StringComparison.Ordinal));
            Assert.Equal(string.Empty, untranslated.Sql);
            Assert.Equal("SELECT B FROM Y", untranslated.ReportSql);
            Assert.Equal("the report is not translatable here", untranslated.TranslationProblem);
        }
        finally
        {
            await CleanupAsync(db, repoId);
        }
    }

    private static async Task SeedAsync(
        CatalogDbContext db, Guid repoId, string subscriberKey, string pageKey,
        IReadOnlyList<(string Question, string Sql, string ObjectKey, string Title)> rows, bool translated = true)
    {
        db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
        {
            RepoId = repoId, SubscriberKey = subscriberKey, PageKey = pageKey,
            ReportFile = "report.pbix", Ordinal = 1, DisplayName = "Page 1",
        });

        for (var i = 0; i < rows.Count; i++)
        {
            var (question, sql, objectKey, title) = rows[i];
            var visualKey = $"{pageKey}#{i + 1}";
            var queryName = $"report.pbix / Page 1 / {title}";

            db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
            {
                RepoId = repoId, PageKey = pageKey, VisualKey = visualKey, Ordinal = i + 1,
                VisualType = "barChart", Title = title, QueryName = queryName,
                ContentHash = SubscriberReportVisualHash.Compute(title, "barChart", []),
            });
            db.SubscriberQueries.Add(new CatalogSubscriberQuery
            {
                RepoId = repoId, SubscriberKey = subscriberKey, Ordinal = i + 1, Name = queryName,
                ServerRef = "dw", Sql = sql, ObjectKeys = objectKey,
                SourceSql = translated ? SourceSqlOf(sql) : null,
                TranslationProblem = translated ? null : "the report is not translatable here",
            });
            db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
            {
                RepoId = repoId, VisualKey = visualKey, Ordinal = 1, Question = question,
            });
        }

        await db.SaveChangesAsync();
        await WaitForFullTextAsync(db, repoId, rows.Count);
    }

    /// <summary>
    /// Waits until the full-text index has caught up with the rows just inserted, because SQL Server populates
    /// it ASYNCHRONOUSLY: a CONTAINS query run immediately after an insert finds nothing, then finds the row a
    /// second or two later. Production is unaffected (questions are written by a sync and searched long
    /// afterwards) but a test that seeds and immediately searches would otherwise fail intermittently for a
    /// reason that has nothing to do with the code under test. A no-op where full-text is absent, since the
    /// LIKE fallback reads the table directly and needs no catch-up.
    /// </summary>
    private static async Task WaitForFullTextAsync(CatalogDbContext db, Guid repoId, int expected)
    {
        if (expected == 0 || !await HasFullTextAsync(db.Database.GetConnectionString()!))
        {
            return;
        }

        // Poll the catalog's own indexed-item count rather than a probe query: it counts rows the index has
        // actually absorbed, and PopulateStatus 0 means the crawl has gone idle rather than still running.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var (items, status) = await FullTextProgressAsync(db.Database.GetConnectionString()!);
            if (items >= expected && status == 0)
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException(
            $"The full-text index did not catch up with {expected} seeded question(s) within 30s.");
    }

    private static async Task<(int Items, int Status)> FullTextProgressAsync(string connectionString)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT CAST(FULLTEXTCATALOGPROPERTY('CatalogFullText','ItemCount') AS int), "
            + "CAST(FULLTEXTCATALOGPROPERTY('CatalogFullText','PopulateStatus') AS int)";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
    }


    /// <summary>
    /// The confirmed-example store is searched as part of the SAME search, not a separate one: a question a
    /// person accepted is found by the same expansion that finds the dashboards' questions, arrives with its
    /// own SQL and the user who stood behind it, and outranks a report-derived question it ties with on score.
    /// This is the retrieval half of POWERAI.md Section 6's learning loop: without it, nothing a person
    /// confirms ever comes back.
    /// </summary>
    [SkippableFact]
    public async Task AConfirmedExample_IsFoundByTheSameSearch_AndOutranksATiedReportQuestion()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var subscriberKey = $"subscriber|confirmed_{suffix}";
        var pageKey = $"{subscriberKey}#report.pbix#1";

        await using var db = CatalogDatabase.Create(cs);
        var exampleQuestion = "How much revenue did each reseller bring in?";
        var exampleSql = "SELECT ResellerKey, SUM(SalesAmount) FROM FactResellerSales GROUP BY ResellerKey";
        var example = new CatalogSemanticExample
        {
            RepoId = repoId,
            Question = exampleQuestion,
            Sql = exampleSql,
            ObjectKeys = "[Dw].[arc].[FactResellerSales]",
            Provenance = SemanticExampleProvenance.UserConfirmed,
            Confidence = 3,
            ConfirmedUtc = DateTime.UtcNow,
            ConfirmedBy = "analyst@example.com",
            ContentHash = SemanticExampleHash.Compute(exampleQuestion, exampleSql),
        };

        try
        {
            // One report-derived question and one confirmed example that match the SAME single term, so the
            // tie-break is what is under test rather than a difference in score.
            await SeedAsync(db, repoId, subscriberKey, pageKey,
            [
                ("Which region has the highest revenue?", "SELECT Region, SUM(Revenue) FROM Sales GROUP BY Region",
                    "[Dw].[arc].[Sales]", "Revenue by Region"),
            ]);

            db.SemanticExamples.Add(example);
            await db.SaveChangesAsync();

            var result = await FindWhenIndexedAsync(
                db, "where does our revenue come from", ["revenue"], repoId,
                r => r.Matches.Any(m => m.Provenance == SemanticExampleProvenance.UserConfirmed));

            // Both questions carry "revenue" and so tie on score; the confirmed one comes first because a
            // person checked it, which is the stronger precedent of the two.
            Assert.Equal(2, result.Matches.Count);
            var best = result.Matches[0];
            Assert.Equal(SemanticExampleProvenance.UserConfirmed, best.Provenance);
            Assert.Equal(exampleQuestion, best.Question);
            Assert.Equal(result.Matches[1].Score, best.Score);

            // A confirmed match carries its OWN answer, not a visual's: the SQL and objects come off its row,
            // and the confirming user travels with it so a caller can say who stood behind it.
            Assert.Equal(exampleSql, best.Sql);
            Assert.Equal(["[Dw].[arc].[FactResellerSales]"], best.ObjectKeys);
            Assert.Equal("analyst@example.com", best.ConfirmedBy);

            // It has no report behind it, and says so rather than borrowing the other match's report.
            Assert.Equal(string.Empty, best.SubscriberKey);
            Assert.Null(best.VisualTitle);

            // The report-derived half is unchanged by sharing the search: it still resolves to its visual's
            // query and names the subscriber it came from.
            var fromReport = result.Matches[1];
            Assert.Equal(QuestionSearch.PowerBiProvenance, fromReport.Provenance);
            Assert.Equal(subscriberKey, fromReport.SubscriberKey);
            Assert.Equal("Revenue by Region", fromReport.VisualTitle);
            Assert.Null(fromReport.ConfirmedBy);
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.ContentHash == example.ContentHash).ExecuteDeleteAsync();
            await CleanupAsync(db, repoId);
        }
    }

    /// <summary>
    /// An example attributed to NO repo is estate-wide knowledge and stays visible to a repo-scoped search: a
    /// person's confirmation is a fact about the estate rather than about one repository's contents, so
    /// narrowing the search to a repo must not hide the answers this estate has already checked.
    /// </summary>
    [SkippableFact]
    public async Task AnUnattachedConfirmedExample_StaysVisibleToARepoScopedSearch()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = Guid.NewGuid();
        var question = $"What is the average basket size in store {suffix}?";
        var sql = "SELECT Store, AVG(BasketSize) FROM Baskets GROUP BY Store";
        var hash = SemanticExampleHash.Compute(question, sql);

        await using var db = CatalogDatabase.Create(cs);
        try
        {
            db.SemanticExamples.Add(new CatalogSemanticExample
            {
                RepoId = null,
                Question = question,
                Sql = sql,
                ObjectKeys = "[Dw].[arc].[Baskets]",
                Provenance = SemanticExampleProvenance.UserConfirmed,
                ConfirmedUtc = DateTime.UtcNow,
                ConfirmedBy = "analyst@example.com",
                ContentHash = hash,
            });
            await db.SaveChangesAsync();

            // Scoped to a repo the example is not attached to, and found anyway.
            var result = await FindWhenIndexedAsync(
                db, "how big is the average basket", ["basket"], repoId,
                r => r.Matches.Any(m => m.Question == question));

            var match = Assert.Single(result.Matches, m => m.Question == question);
            Assert.Equal(sql, match.Sql);
            Assert.Equal(SemanticExampleProvenance.UserConfirmed, match.Provenance);
        }
        finally
        {
            await db.SemanticExamples.Where(e => e.ContentHash == hash).ExecuteDeleteAsync();
        }
    }

    /// <summary>
    /// Runs the search, retrying until <paramref name="ready"/> holds or a deadline passes. SQL Server
    /// populates a full-text index ASYNCHRONOUSLY, so a CONTAINS run immediately after an insert finds nothing
    /// and then finds the row a second or two later. Polling the search itself (rather than the catalog's
    /// item count, which is shared across every indexed table and so cannot say whether THIS row landed) is
    /// what makes these assertions stable. On an instance without full-text the LIKE path reads the table
    /// directly and the first attempt already satisfies the predicate.
    /// </summary>
    private static async Task<QuestionSearchResult> FindWhenIndexedAsync(
        CatalogDbContext db, string question, IReadOnlyList<string> terms, Guid? repoId,
        Func<QuestionSearchResult, bool> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        QuestionSearchResult result;
        do
        {
            result = await QuestionSearch.FindSimilarAsync(
                db, question, topK: 5, terms, repoId, CancellationToken.None);
            if (ready(result))
            {
                return result;
            }

            await Task.Delay(250);
        }
        while (DateTime.UtcNow < deadline);

        throw new TimeoutException(
            $"The search for '{question}' did not reach the expected state within 30s "
            + $"(last result had {result.Matches.Count} match(es)).");
    }

    private static async Task CleanupAsync(CatalogDbContext db, Guid repoId)
    {
        await db.SubscriberReportVisualQuestions.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberQueries.Where(q => q.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberReportVisuals.Where(v => v.RepoId == repoId).ExecuteDeleteAsync();
        await db.SubscriberReportPages.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
    }
}
