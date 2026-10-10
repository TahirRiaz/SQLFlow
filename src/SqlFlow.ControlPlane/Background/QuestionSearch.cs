using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// One stored question matched against a newly typed one: the question itself, the SQL that already answers it,
/// the warehouse objects that SQL reads, where the example came from, and how well it matched. The match score
/// is the trustworthy confidence signal (POWERAI.md Section 6: an LLM's self-rating is not, because a wrong
/// query can sound exactly as confident as a right one).
/// </summary>
/// <param name="Question">The stored question text.</param>
/// <param name="Sql">The runnable query that answers it: a confirmed example's SQL, or a report visual's query translated
/// into T-SQL over its source tables. Empty when there is none (the visual's query could not be resolved or
/// translated; see <paramref name="TranslationProblem"/>).</param>
/// <param name="ObjectKeys">The warehouse objects <paramref name="Sql"/> reads.</param>
/// <param name="Provenance">Where the example came from: <see cref="SemanticExampleProvenance.PowerBi"/> for a
/// question derived from an extracted report visual, <see cref="SemanticExampleProvenance.UserConfirmed"/> for
/// one a person accepted or corrected. The difference is what lets a caller weigh "a dashboard asks this"
/// against "a person checked this" rather than treating every match as equally settled.</param>
/// <param name="Score">How many of the searched terms this question matched. Unbounded and relative to the
/// other matches in the same search rather than a 0-1 similarity.</param>
/// <param name="MatchedTerms">The searched terms this question actually contains, so a caller can show WHY it
/// matched rather than only how strongly.</param>
/// <param name="SubscriberKey">The report (subscriber) this question came from, empty for a confirmed example
/// that came from a person rather than from a report.</param>
/// <param name="VisualTitle">The title of the visual that answers it, when it has one. Null for a confirmed
/// example, which has no visual behind it.</param>
/// <param name="ConfirmedBy">Who confirmed the example, for a match out of the confirmed-example store. Null
/// for a question derived from a report visual, which no person has individually stood behind.</param>
/// <param name="SourceRef">The datasource <paramref name="Sql"/> runs against, in the whole-reference shape
/// the DataOps query surface requires (a <c>${env:...}</c>/<c>${keyvault:...}</c> token or an <c>@alias</c>).
/// Null when the confirmed example was stored without one, and for a PowerBI-derived question, whose connection
/// the similar-questions endpoint works out from the tables its SQL reads. This is what an auto-run caller reads to know
/// which connection to prepare <paramref name="Sql"/> against; without it the match still stands as precedent
/// but cannot be run without a person choosing a datasource first.</param>
/// <param name="ExampleId">The <c>CatalogSemanticExample.Id</c> behind this match, for an auto-run caller to
/// name (<c>POST /api/v1/powerai/questions/{id}/auto-run</c>). Null for a PowerBI-derived question, which has
/// no confirmed-example row and so nothing an auto-run endpoint could address.</param>
/// <param name="SameQuestion">Whether the stored question carries exactly the typed question's meaningful words
/// (stop words dropped, inflections folded), i.e. it is the same question reworded at most trivially. A term-count
/// threshold alone can never trust a short question: "how many customers do we have?" has one meaningful word, so
/// even its verbatim twin scores 1. This is what lets such a match be trusted on identity rather than on count.</param>
/// <param name="ReportSql">For a report-derived question: the visual's own query, which names the report's model
/// rather than the source tables and so is evidence, not something to run. Null for a confirmed example.</param>
/// <param name="TranslationProblem">For a report-derived question with no runnable <paramref name="Sql"/>: why its
/// query could not be translated. Null otherwise.</param>
public sealed record QuestionMatch(
    string Question,
    string Sql,
    IReadOnlyList<string> ObjectKeys,
    string Provenance,
    int Score,
    IReadOnlyList<string> MatchedTerms,
    string SubscriberKey,
    string? VisualTitle,
    string? ConfirmedBy = null,
    string? SourceRef = null,
    long? ExampleId = null,
    bool SameQuestion = false,
    string? ReportSql = null,
    string? TranslationProblem = null);

/// <summary>The outcome of one search: the terms actually searched for (the expansion, or the question's own
/// words when expansion is off or unavailable) and the trustworthy matches. The terms travel with the result
/// so a caller can show what was looked for, which is what makes an empty result interpretable rather than
/// mysterious.</summary>
/// <param name="SearchedTerms">The terms the stored questions were matched against.</param>
/// <param name="Matches">The best confirmed-good matches, best first.</param>
public sealed record QuestionSearchResult(
    IReadOnlyList<string> SearchedTerms, IReadOnlyList<QuestionMatch> Matches);

/// <summary>
/// Matches stored questions against a newly typed one (POWERAI.md Section 6). The mechanism is a word search,
/// not a vector one: an LLM first expands the typed question into the business vocabulary a stored question
/// might have used instead ("turnover" also yielding "revenue", "sales"), and the stored questions are then
/// ranked by how many of those terms they contain. SQL Server's own full-text engine supplies the linguistic
/// half (inflections such as "sell"/"selling"/"sold"), and the expansion supplies the business-vocabulary half
/// it cannot know.
/// <para>
/// TWO stores are searched as one: the questions derived from extracted PowerBI visuals
/// (<see cref="CatalogSubscriberReportVisualQuestion"/>) and the confirmed examples a person accepted or
/// corrected (<see cref="CatalogSemanticExample"/>). They are ranked together by the same scoring, so the
/// learning loop's output competes with the dashboards' on equal terms and a caller gets one ordered list
/// rather than having to merge two. Which store a match came from survives as its
/// <see cref="QuestionMatch.Provenance"/>.
/// </para>
/// <para>
/// The terms reach SQL Server through <see cref="EF.Functions"/>, which parameterizes them, rather than being
/// pasted into a predicate string: a model-authored fragment interpolated into <c>CONTAINS</c> syntax would be
/// both a correctness hazard (an apostrophe in "customer's" breaks the predicate) and injection-shaped.
/// </para>
/// </summary>
public static class QuestionSearch
{
    /// <summary>The provenance of a question derived from an extracted PowerBI report visual. An alias for
    /// <see cref="SemanticExampleProvenance.PowerBi"/> so the catalog and the search cannot spell it
    /// differently.</summary>
    public const string PowerBiProvenance = SemanticExampleProvenance.PowerBi;

    /// <summary>
    /// Returns the <paramref name="topK"/> stored questions best matching <paramref name="question"/>, best
    /// first. <paramref name="expander"/> is optional: without it (or when it fails) the search runs on the
    /// question's own words, which is weaker but still answers, so an Anthropic outage degrades retrieval
    /// rather than breaking it.
    /// </summary>
    /// <param name="db">The catalog to search.</param>
    /// <param name="question">The newly typed question to match stored examples against.</param>
    /// <param name="topK">How many matches to return, best first.</param>
    /// <param name="expander">Expands the question into business-vocabulary terms, or null to search the typed
    /// words alone.</param>
    /// <param name="repoId">Restricts the search to one repo, or searches the whole estate when null. Estate-wide
    /// is the useful default: a question about revenue is worth answering from whichever repo's report first
    /// asked it.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<QuestionSearchResult> FindSimilarAsync(
        CatalogDbContext db, string question, int topK, QuestionExpander? expander, Guid? repoId,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        var terms = await BuildTermsAsync(question, expander, ct).ConfigureAwait(false);
        return await FindSimilarAsync(db, question, topK, terms, repoId, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The same search against terms already decided, for a caller that has its own expansion (and for tests,
    /// which supply terms directly rather than depending on a model's vocabulary). The terms are normalized
    /// here too, so a caller cannot accidentally search on stop words, and only the first
    /// <see cref="QuestionExpander.MaxTerms"/> distinct ones are kept, since each becomes its own predicate and a
    /// caller's expansion is held to the same bound as the server's own.
    /// </summary>
    /// <param name="db">The catalog to search.</param>
    /// <param name="question">The typed question, for context only; matching is on <paramref name="terms"/>.</param>
    /// <param name="topK">How many matches to return, best first.</param>
    /// <param name="terms">The vocabulary to match stored questions against.</param>
    /// <param name="repoId">Restricts the search to one repo, or the whole estate when null.</param>
    /// <param name="ct">Cancels the search.</param>
    public static async Task<QuestionSearchResult> FindSimilarAsync(
        CatalogDbContext db, string question, int topK, IReadOnlyList<string> terms, Guid? repoId,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentOutOfRangeException.ThrowIfLessThan(topK, 1);

        terms = Normalize(terms).Take(QuestionExpander.MaxTerms).ToList();
        if (terms.Count == 0)
        {
            return new QuestionSearchResult([], []);
        }

        // One full-text predicate per term, OR-ed: a question matching more of them ranks higher. Built as a
        // single CONTAINS over an OR-of-terms string would let SQL Server rank internally, but that means
        // assembling search syntax from model output; scoring by counting matched terms keeps every term a
        // parameter and makes the score explainable (MatchedTerms says exactly which ones hit).
        var candidates = await MatchingQuestionsAsync(db, terms, repoId, ct).ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return new QuestionSearchResult(terms, []);
        }

        // Every candidate already matched at least one term in the database. Scoring re-checks each term here
        // to count and name them, and must credit a STEM match too: where the full-text index matched "sells"
        // from "sell", a literal-only check would score that row 0 and discard the very row the index just
        // found. Rows that still score nothing are dropped, which only happens on the LIKE path, where a
        // substring match can fall inside a longer word ("sale" within "wholesale").
        //
        // A confirmed example outranks a report-derived question on an equal score: both are real precedent,
        // but one of them a person actually checked. Shorter questions break the remaining ties, as the more
        // specific phrasing at the same score.
        //
        // The score counts distinct word STEMS matched, not terms: an expansion lists each concept in several
        // grammatical forms ("customer", "customers"), and every one of them matches the same word of a stored
        // question. Counting them separately would let one shared word clear the trust threshold on its own and
        // auto-run an unrelated query. MatchedTerms still names every term that hit.
        var ranked = candidates
            .Select(c => (Row: c, Matched: terms.Where(t => MatchesTerm(c.Question, t)).ToList()))
            .Select(c => (c.Row, c.Matched, Score: c.Matched.Select(Stem).Distinct(StringComparer.Ordinal).Count()))
            .Where(c => c.Score > 0)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Row.ExampleId > 0)
            .ThenBy(c => c.Row.Question.Length)
            .Take(topK)
            .ToList();
        if (ranked.Count == 0)
        {
            return new QuestionSearchResult(terms, []);
        }

        var visuals = await ResolveVisualsAsync(db, ranked.Select(r => r.Row), ct).ConfigureAwait(false);
        var examples = await ResolveExamplesAsync(db, ranked.Select(r => r.Row), ct).ConfigureAwait(false);

        // Identity is judged on the TYPED question's own words, never on the expansion: the expansion adds
        // synonyms no stored question is expected to carry all of, so comparing against it would never match.
        var typedWords = MeaningfulStems(question);
        var matches = new List<QuestionMatch>(ranked.Count);
        foreach (var (row, matched, score) in ranked)
        {
            var sameQuestion = typedWords.Count > 0 && typedWords.SetEquals(MeaningfulStems(row.Question));
            matches.Add(row.ExampleId > 0
                ? ExampleMatch(row, matched, score, sameQuestion, examples)
                : VisualMatch(row, matched, score, sameQuestion, visuals));
        }

        return new QuestionSearchResult(terms, matches);
    }

    /// <summary>
    /// Resolves the winning report-derived rows to the SQL that answers them. A visual's <c>QueryName</c> names
    /// the <see cref="CatalogSubscriberQuery"/> it was synthesized as, which is where the rendered SQL and the
    /// objects it reads actually live. Run only over the winners, so the ranking pass stays a text scan and
    /// only a handful of rows cost a lookup.
    /// </summary>
    private static async Task<VisualLookup> ResolveVisualsAsync(
        CatalogDbContext db, IEnumerable<QuestionRow> rows, CancellationToken ct)
    {
        var visualKeys = rows
            .Where(r => r.ExampleId == 0 && r.VisualKey is not null)
            .Select(r => r.VisualKey!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (visualKeys.Count == 0)
        {
            return new VisualLookup(
                new Dictionary<string, ResolvedVisual>(StringComparer.Ordinal),
                new Dictionary<(Guid, string, string), ResolvedQuery>());
        }

        // A visual's query is found by its repo, its subscriber, and its name together: two reports can give
        // same-named visuals different queries, and a name alone would hand one report's SQL to the other.
        var visuals = await (
                from visual in db.SubscriberReportVisuals.AsNoTracking()
                where visualKeys.Contains(visual.VisualKey)
                join page in db.SubscriberReportPages.AsNoTracking()
                    on new { visual.RepoId, visual.PageKey } equals new { page.RepoId, page.PageKey }
                select new { visual.VisualKey, visual.QueryName, visual.Title, visual.RepoId, page.SubscriberKey })
            .ToListAsync(ct).ConfigureAwait(false);

        var queryNames = visuals.Select(v => v.QueryName).Distinct().ToList();
        var subscriberKeys = visuals.Select(v => v.SubscriberKey).Distinct().ToList();
        var queries = await db.SubscriberQueries.AsNoTracking()
            .Where(q => queryNames.Contains(q.Name) && subscriberKeys.Contains(q.SubscriberKey))
            .Select(q => new { q.RepoId, q.SubscriberKey, q.Name, q.Sql, q.SourceSql, q.TranslationProblem, q.ObjectKeys })
            .ToListAsync(ct).ConfigureAwait(false);

        return new VisualLookup(
            visuals
                .GroupBy(v => v.VisualKey, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new ResolvedVisual(g.First().RepoId, g.First().SubscriberKey, g.First().QueryName, g.First().Title),
                    StringComparer.Ordinal),
            queries
                .GroupBy(q => (q.RepoId, q.SubscriberKey, q.Name))
                .ToDictionary(
                    g => g.Key,
                    g => new ResolvedQuery(
                        g.First().Sql, g.First().SourceSql, g.First().TranslationProblem, g.First().ObjectKeys, g.First().SubscriberKey)));
    }

    /// <summary>Loads the winning confirmed examples' own SQL, objects, and confirming user. An example carries
    /// everything that answers it on its own row, so this is one lookup by id with nothing to join.</summary>
    private static async Task<Dictionary<long, ResolvedExample>> ResolveExamplesAsync(
        CatalogDbContext db, IEnumerable<QuestionRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.ExampleId > 0).Select(r => r.ExampleId).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<long, ResolvedExample>();
        }

        var examples = await db.SemanticExamples.AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .Select(e => new { e.Id, e.Sql, e.ObjectKeys, e.ConfirmedBy, e.SourceRef })
            .ToListAsync(ct).ConfigureAwait(false);

        return examples.ToDictionary(
            e => e.Id, e => new ResolvedExample(e.Sql, e.ObjectKeys, e.ConfirmedBy, e.SourceRef));
    }

    /// <summary>Builds the match for a report-derived question, from the visual and the query behind it.</summary>
    private static QuestionMatch VisualMatch(
        QuestionRow row, IReadOnlyList<string> matched, int score, bool sameQuestion, VisualLookup lookup)
    {
        var visualKey = row.VisualKey ?? string.Empty;
        lookup.Visuals.TryGetValue(visualKey, out var visual);
        var query = visual is not null
                    && lookup.Queries.TryGetValue((visual.RepoId, visual.SubscriberKey, visual.QueryName), out var found)
            ? found
            : null;

        return new QuestionMatch(
            row.Question,
            query?.SourceSql ?? string.Empty,
            SplitObjectKeys(query?.ObjectKeys),
            row.Provenance,
            score,
            matched,
            query?.SubscriberKey ?? SubscriberKeyFromVisualKey(visualKey),
            visual?.Title,
            SameQuestion: sameQuestion,
            ReportSql: query?.Sql,
            TranslationProblem: query is null
                ? "the visual's query was not found"
                : query.SourceSql is null ? query.TranslationProblem ?? "the visual's query has not been translated yet" : null);
    }

    /// <summary>Builds the match for a confirmed example, whose answer lives on its own row.</summary>
    private static QuestionMatch ExampleMatch(
        QuestionRow row, IReadOnlyList<string> matched, int score, bool sameQuestion,
        IReadOnlyDictionary<long, ResolvedExample> lookup)
    {
        lookup.TryGetValue(row.ExampleId, out var example);

        return new QuestionMatch(
            row.Question,
            example?.Sql ?? string.Empty,
            SplitObjectKeys(example?.ObjectKeys),
            row.Provenance,
            score,
            matched,
            string.Empty,
            null,
            example?.ConfirmedBy,
            example?.SourceRef,
            row.ExampleId,
            sameQuestion);
    }

    /// <summary>A question reduced to its meaningful words: split, stop words and one-character tokens dropped
    /// (the same <see cref="Normalize"/> the search terms go through), and each word stemmed so "customer" and
    /// "customers" count as one. Two questions with equal sets ask the same thing.</summary>
    private static HashSet<string> MeaningfulStems(string question)
        => Normalize(question.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
            .Select(Stem)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Loads the stored questions containing any of <paramref name="terms"/> from BOTH stores, using each
    /// table's full-text index when the instance has one and falling back to a plain <c>LIKE</c> scan when it
    /// does not. The fallback exists because full-text is an installable SQL Server feature the catalog cannot
    /// assume (its own migrations skip the indexes where it is absent), and a deployment without it should get
    /// weaker matching rather than an error naming a feature its DBA may not be willing to install.
    /// </summary>
    private static async Task<List<QuestionRow>> MatchingQuestionsAsync(
        CatalogDbContext db, IReadOnlyList<string> terms, Guid? repoId, CancellationToken ct)
    {
        var visualQuestions = db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => repoId == null || q.RepoId == repoId);

        var rows = new List<QuestionRow>();

        if (await HasFullTextIndexAsync(db, VisualQuestionTable, ct).ConfigureAwait(false))
        {
            // FORMSOF(INFLECTIONAL, ...) is what actually buys stemming: a plain CONTAINS matches the word
            // literally, so it would find neither "sells" from "sell" nor "category" from "categories". The
            // whole condition is still passed as a PARAMETER by EF.Functions.Contains, so a term carrying an
            // apostrophe or a full-text operator is matched as text rather than changing the predicate.
            rows.AddRange(await visualQuestions
                .Where(AnyTerm<CatalogSubscriberReportVisualQuestion>(terms, term =>
                {
                    var condition = InflectionalCondition(term);
                    return q => EF.Functions.Contains(q.Question, condition);
                }))
                .Select(q => new QuestionRow(q.Question, PowerBiProvenance, q.VisualKey, 0L))
                .Take(MaxCandidates)
                .ToListAsync(ct).ConfigureAwait(false));
        }
        else
        {
            rows.AddRange(await visualQuestions
                .Where(AnyTerm<CatalogSubscriberReportVisualQuestion>(
                    terms, term => q => q.Question.Contains(term)))
                .Select(q => new QuestionRow(q.Question, PowerBiProvenance, q.VisualKey, 0L))
                .Take(MaxCandidates)
                .ToListAsync(ct).ConfigureAwait(false));
        }

        rows.AddRange(await MatchingExamplesAsync(db, terms, repoId, ct).ConfigureAwait(false));
        return rows;
    }

    /// <summary>
    /// Loads <see cref="CatalogSemanticExample"/> rows containing any of <paramref name="terms"/>, via full-text
    /// or LIKE depending on what the instance has. Every stored example is a confirmed-good precedent: a
    /// rejection is never written to this table (POWERAI.md Section 6), so there is no separate confirmed/
    /// known-bad split to filter on here.
    /// </summary>
    private static async Task<List<QuestionRow>> MatchingExamplesAsync(
        CatalogDbContext db, IReadOnlyList<string> terms, Guid? repoId, CancellationToken ct)
    {
        // A confirmed example attributed to NO repo stays in a repo-scoped search on purpose: a person's
        // confirmation is a fact about the estate rather than about one repository's contents, so scoping the
        // search to a repo must not hide the answers this estate has already checked.
        var examples = db.SemanticExamples.AsNoTracking()
            .Where(e => repoId == null || e.RepoId == repoId || e.RepoId == null);

        // Probed per TABLE rather than once for the deployment: the two full-text indexes ship in separate
        // migrations, so an estate migrated while the Full-Text feature was absent and upgraded afterwards can
        // genuinely carry one index and not the other. A single probe for both would then either skip every
        // confirmed example or aim CONTAINS at a table that has no index to serve it.
        if (await HasFullTextIndexAsync(db, SemanticExampleTable, ct).ConfigureAwait(false))
        {
            return await examples
                .Where(AnyTerm<CatalogSemanticExample>(terms, term =>
                {
                    var condition = InflectionalCondition(term);
                    return e => EF.Functions.Contains(e.Question, condition);
                }))
                .Select(e => new QuestionRow(e.Question, e.Provenance, null, e.Id))
                .Take(MaxCandidates)
                .ToListAsync(ct).ConfigureAwait(false);
        }

        return await examples
            .Where(AnyTerm<CatalogSemanticExample>(terms, term => e => e.Question.Contains(term)))
            .Select(e => new QuestionRow(e.Question, e.Provenance, null, e.Id))
            .Take(MaxCandidates)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Folds one predicate per term into a single OR-ed predicate, so a row matching any term is a
    /// candidate and a per-term factory is all each store has to supply.</summary>
    private static System.Linq.Expressions.Expression<Func<T, bool>> AnyTerm<T>(
        IReadOnlyList<string> terms,
        Func<string, System.Linq.Expressions.Expression<Func<T, bool>>> forTerm)
        => terms.Aggregate(
            (System.Linq.Expressions.Expression<Func<T, bool>>?)null,
            (acc, term) => acc is null ? forTerm(term) : Or(acc, forTerm(term)))!;

    /// <summary>Whether <paramref name="table"/> carries a full-text index on this instance. Cached per table
    /// per process: it is a deployment property that changes only when the catalog is migrated or the feature
    /// is installed, both of which restart or redeploy the control plane.</summary>
    private static async Task<bool> HasFullTextIndexAsync(
        CatalogDbContext db, string table, CancellationToken ct)
    {
        if (FullTextProbes.TryGetValue(table, out var known))
        {
            return known;
        }

        bool present;
        try
        {
            // The qualified name is a PARAMETER to OBJECT_ID, not string-concatenated into the statement, even
            // though both table names are compile-time constants here.
            var qualified = $"catalog.{table}";
            var result = await db.Database
                .SqlQuery<int>($@"
                    SELECT CASE WHEN SERVERPROPERTY('IsFullTextInstalled') = 1
                                 AND EXISTS (SELECT 1 FROM sys.fulltext_indexes
                                             WHERE object_id = OBJECT_ID({qualified}))
                                THEN 1 ELSE 0 END AS [Value]")
                .SingleAsync(ct).ConfigureAwait(false);
            present = result == 1;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A probe that cannot run (a restricted principal, an unusual provider) must not fail the search:
            // assume no index and use the LIKE path, which works on any instance.
            present = false;
        }

        FullTextProbes[table] = present;
        return present;
    }

    /// <summary>The questions a sync derived from PowerBI report visuals.</summary>
    private const string VisualQuestionTable = "SubscriberReportVisualQuestion";

    /// <summary>The confirmed-example store the learning loop writes into: the semantic layer's example queries.</summary>
    private const string SemanticExampleTable = "SemanticExample";

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> FullTextProbes =
        new(StringComparer.Ordinal);

    /// <summary>Resets the cached full-text probes. For tests, which migrate a catalog mid-process and so can
    /// see an index appear after a probe has already run.</summary>
    internal static void ResetFullTextProbe() => FullTextProbes.Clear();

    /// <summary>
    /// Wraps one term as an inflectional full-text condition, so the index matches its grammatical forms
    /// ("sell" reaching "sells"/"selling"/"sold", "categories" reaching "category") rather than only the exact
    /// word. The term is quoted so a multi-word phrase stays one unit, and any embedded quote is doubled,
    /// which is how a full-text phrase escapes one: without that a term carrying a quote would end the phrase
    /// early and the rest would be read as operators. The result is still passed to SQL as a parameter, never
    /// concatenated into the statement.
    /// </summary>
    private static string InflectionalCondition(string term)
        => $"FORMSOF(INFLECTIONAL, \"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";

    /// <summary>The most stored questions one search pulls back FROM EACH STORE before ranking. A ceiling
    /// rather than a page: past this many term hits the query is too broad for the extra rows to change the
    /// winners.</summary>
    private const int MaxCandidates = 500;

    /// <summary>One candidate before resolution: its text, its provenance, and whichever identity resolves the
    /// answer behind it. <paramref name="VisualKey"/> is set for a report-derived question and
    /// <paramref name="ExampleId"/> for a confirmed example; exactly one of the two is meaningful, and a
    /// non-zero id is what says which.</summary>
    private sealed record QuestionRow(string Question, string Provenance, string? VisualKey, long ExampleId);

    private sealed record ResolvedVisual(Guid RepoId, string SubscriberKey, string QueryName, string? Title);

    private sealed record ResolvedQuery(
        string Sql, string? SourceSql, string? TranslationProblem, string ObjectKeys, string SubscriberKey);

    private sealed record ResolvedExample(string Sql, string ObjectKeys, string? ConfirmedBy, string? SourceRef);

    /// <summary>The two lookups a report-derived match is built from, carried together so the resolution pass
    /// returns one value rather than a tuple of dictionaries.</summary>
    private sealed record VisualLookup(
        IReadOnlyDictionary<string, ResolvedVisual> Visuals,
        IReadOnlyDictionary<(Guid RepoId, string SubscriberKey, string Name), ResolvedQuery> Queries);

    private static System.Linq.Expressions.Expression<Func<T, bool>> Or<T>(
        System.Linq.Expressions.Expression<Func<T, bool>> left,
        System.Linq.Expressions.Expression<Func<T, bool>> right)
    {
        var parameter = System.Linq.Expressions.Expression.Parameter(typeof(T), "q");
        var body = System.Linq.Expressions.Expression.OrElse(
            new ReplaceParameter(left.Parameters[0], parameter).Visit(left.Body)!,
            new ReplaceParameter(right.Parameters[0], parameter).Visit(right.Body)!);
        return System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    private sealed class ReplaceParameter(
        System.Linq.Expressions.ParameterExpression from, System.Linq.Expressions.ParameterExpression to)
        : System.Linq.Expressions.ExpressionVisitor
    {
        protected override System.Linq.Expressions.Expression VisitParameter(
            System.Linq.Expressions.ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

    /// <summary>The terms to search for: the expansion when it produced any, otherwise the question's own
    /// meaningful words, so a search still runs when expansion is off or unreachable.</summary>
    private static async Task<IReadOnlyList<string>> BuildTermsAsync(
        string question, QuestionExpander? expander, CancellationToken ct)
    {
        if (expander is not null)
        {
            var expanded = await expander.ExpandAsync(question, ct).ConfigureAwait(false);
            if (expanded.Count > 0)
            {
                return Normalize(expanded);
            }
        }

        return Normalize(question.Split(
            [' ', '\t', '\n', '\r', '?', '!', ',', '.', ';', ':', '(', ')', '"', '\''],
            StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Trims, lowercases, drops stop words and one-character tokens, and de-duplicates. Stop words are
    /// dropped here rather than left to SQL Server because they would otherwise count toward a question's score
    /// and let "what is the" outrank a real vocabulary match.</summary>
    private static IReadOnlyList<string> Normalize(IEnumerable<string> terms)
        => terms
            .Select(t => t.Trim().Trim('"').ToLowerInvariant())
            .Where(t => t.Length > 1 && !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "of", "in", "on", "at", "to", "for", "by", "with", "from", "as",
        "is", "are", "was", "were", "be", "been", "do", "does", "did", "what", "which", "who", "whom",
        "how", "why", "when", "where", "our", "we", "us", "you", "your", "it", "its", "this", "that",
        "these", "those", "there", "their", "have", "has", "had", "can", "could", "would", "should",
        "many", "much", "most", "more", "per", "into", "out", "over", "up", "down", "about",
    };

    /// <summary>
    /// Whether a question matches a term for SCORING: the term as a whole word, or one of its grammatical
    /// forms. The inflected comparison exists because the full-text index matches inflections, so a row it
    /// returned for "sell" (because the question says "sells") has to be credited here or it would be found
    /// and then silently discarded. It is a deliberately small suffix comparison rather than a real stemmer:
    /// its only job is to agree with matches the database already made, not to find new ones.
    /// </summary>
    private static bool MatchesTerm(string question, string term)
    {
        if (ContainsWord(question, term))
        {
            return true;
        }

        // Compare against each word of the question with common inflectional endings removed from both sides.
        var stem = Stem(term);
        if (stem.Length < MinStemLength)
        {
            return false;
        }

        foreach (var word in question.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(Stem(word), stem, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Strips the regular English endings that separate one form of a word from another, so "sells",
    /// "selling" and "sell" reduce alike, as do "categories" and "category". Irregulars ("sold") are out of
    /// scope on purpose: the expansion supplies those, and a rule that tried to cover them would mis-stem far
    /// more words than it fixed.</summary>
    private static string Stem(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.EndsWith("ies", StringComparison.Ordinal) && w.Length > 4)
        {
            return string.Concat(w.AsSpan(0, w.Length - 3), "y");
        }
        foreach (var suffix in Suffixes)
        {
            if (w.Length - suffix.Length >= MinStemLength && w.EndsWith(suffix, StringComparison.Ordinal))
            {
                return w[..^suffix.Length];
            }
        }
        return w;
    }

    private static readonly string[] Suffixes = ["ing", "ed", "es", "s"];

    /// <summary>Below this many characters a stem is too short to be a meaningful match, and stripping a
    /// suffix from an already-short word ("sales" to "sal") produces noise rather than a root.</summary>
    private const int MinStemLength = 3;

    private static readonly char[] WordSeparators =
        [' ', '\t', '\n', '\r', '?', '!', ',', '.', ';', ':', '(', ')', '"', '\'', '-', '/'];

    /// <summary>Whether a question contains a term as a whole word, so "sale" does not score against
    /// "wholesale". A term that is itself multi-word (a short noun phrase from the expansion) is matched as a
    /// substring, since its own boundaries already make it specific.</summary>
    private static bool ContainsWord(string question, string term)
    {
        if (term.Contains(' ', StringComparison.Ordinal))
        {
            return question.Contains(term, StringComparison.OrdinalIgnoreCase);
        }

        var index = 0;
        while ((index = question.IndexOf(term, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var beforeOk = index == 0 || !char.IsLetterOrDigit(question[index - 1]);
            var after = index + term.Length;
            var afterOk = after >= question.Length || !char.IsLetterOrDigit(question[after]);
            if (beforeOk && afterOk)
            {
                return true;
            }
            index = after;
        }

        return false;
    }

    /// <summary>Splits the newline-joined object keys the catalog stores into a list, dropping blanks.</summary>
    private static IReadOnlyList<string> SplitObjectKeys(string? objectKeys)
        => string.IsNullOrWhiteSpace(objectKeys)
            ? []
            : objectKeys.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Recovers the subscriber key from a visual key when the query row could not be resolved. A visual
    /// key is <c>&lt;subscriberKey&gt;#&lt;reportFile&gt;#&lt;page&gt;#&lt;visual&gt;</c>, so the subscriber is
    /// everything before the first '#'.</summary>
    private static string SubscriberKeyFromVisualKey(string visualKey)
    {
        var hash = visualKey.IndexOf('#', StringComparison.Ordinal);
        return hash < 0 ? visualKey : visualKey[..hash];
    }
}
