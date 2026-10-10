using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The contract POWERAI.md's business-question field depends on across syncs. A sync rewrites every visual row but
/// never the question rows, so these tests seed the post-sync state directly and run the step that follows a sync:
/// an unchanged visual keeps its questions with NO generator call (proven with a generator whose every call fails),
/// a new, changed, or unanswered one is sent to it, generation replaces only generated questions, a question follows
/// a visual that only moved, and a question whose visual is gone is removed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SubscriberQuestionEnrichmentTests
{
    private const string Page = "subscriber|enrich#report.pbix#1";

    /// <summary>An Anthropic key that authenticates with nobody: any call through it fails fast and yields no
    /// questions, so a test asserting "the generator was never invoked" proves it by seeing no warning.</summary>
    private static QuestionGenerator UncallableGenerator()
        => new(new AnthropicOptions { ApiKey = "sk-ant-invalid-test-key", Model = "claude-sonnet-5" },
            NullLogger<QuestionGenerator>.Instance);

    [SkippableFact]
    public async Task UnchangedVisualWithQuestions_KeepsThem_WithNoGeneratorCall()
    {
        await using var scope = await Scope.CreateAsync();
        var hash = scope.AddVisual(1, "Sales by Region");
        scope.AddQuestion(1, "What are sales by region?", SubscriberQuestionOrigin.Generated);
        await scope.Db.SaveChangesAsync();

        using var generator = UncallableGenerator();
        var warnings = await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = hash }, generator,
            CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal([(Scope.Key(1), "What are sales by region?")], await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task UnchangedVisualWithoutQuestions_IsSentToTheGenerator()
    {
        await using var scope = await Scope.CreateAsync();
        var hash = scope.AddVisual(1, "Orders");
        await scope.Db.SaveChangesAsync();

        // Synced while generation was off: unchanged, but never answered. The uncallable generator failing is the
        // proof it was asked.
        using var generator = UncallableGenerator();
        var warnings = await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = hash }, generator,
            CancellationToken.None);

        Assert.Contains("no business questions could be generated", Assert.Single(warnings));
    }

    [SkippableFact]
    public async Task ChangedVisual_ReplacesOnlyGeneratedQuestions_AndKeepsAPersonsInPlace()
    {
        await using var scope = await Scope.CreateAsync();
        scope.AddVisual(1, "Sales by Region, now by Country");
        scope.AddQuestion(1, "Old generated question?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(1, "Which country grew fastest?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        var calls = 0;
        var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = "the old content hash" },
            (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<string>>(
                    ["What are sales by country?", "which country grew FASTEST?"]);
            },
            CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal(1, calls);
        var rows = await scope.RowsAsync();
        Assert.Equal(
            [
                ("Which country grew fastest?", SubscriberQuestionOrigin.Manual, 2),
                ("What are sales by country?", SubscriberQuestionOrigin.Generated, 3),
            ],
            rows.Select(r => (r.Question, r.Origin, r.Ordinal)));
    }

    [SkippableFact]
    public async Task ChangedVisual_WhoseGenerationFails_KeepsTheQuestionsItHad()
    {
        await using var scope = await Scope.CreateAsync();
        scope.AddVisual(1, "Changed");
        scope.AddQuestion(1, "Earlier question?", SubscriberQuestionOrigin.Generated);
        await scope.Db.SaveChangesAsync();

        var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = "the old content hash" },
            (_, _) => Task.FromResult<IReadOnlyList<string>>([]), CancellationToken.None);

        Assert.Contains("keeps the questions it had", Assert.Single(warnings));
        Assert.Equal([(Scope.Key(1), "Earlier question?")], await scope.QuestionsAsync());
        // The visual row the sync wrote is untouched by the failure.
        Assert.True(await scope.Db.SubscriberReportVisuals.AnyAsync(v => v.RepoId == scope.RepoId));
    }

    [SkippableFact]
    public async Task RemovedVisual_HasItsQuestionsPruned_EvenAPersons_WhileAKeptVisualKeepsItsOwn()
    {
        await using var scope = await Scope.CreateAsync();
        var hash = scope.AddVisual(1, "Kept");
        scope.AddQuestion(1, "What is the kept total?", SubscriberQuestionOrigin.Generated);
        // Visual 2 left the report: the sync wrote no row for it.
        scope.AddQuestion(2, "What did the removed visual show?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(2, "A person's question on it?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        // Generation off: the reconcile step still runs.
        var warnings = await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId,
            new Dictionary<string, string> { [Scope.Key(1)] = hash, [Scope.Key(2)] = "removed visual hash" },
            generator: null, CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal([(Scope.Key(1), "What is the kept total?")], await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task MovedVisual_TakesItsQuestionsAlong_AndIsNotRegenerated()
    {
        await using var scope = await Scope.CreateAsync();
        // Before the sync the visual sat at position 1; the sync wrote it at position 2 with the same content.
        var hash = scope.AddVisual(2, "Moved");
        scope.AddQuestion(1, "Generated before the move?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(1, "Written by a person?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        using var generator = UncallableGenerator();
        var warnings = await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = hash }, generator,
            CancellationToken.None);

        Assert.Empty(warnings);
        Assert.Equal(
            [(Scope.Key(2), "Generated before the move?"), (Scope.Key(2), "Written by a person?")],
            await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task TwoMovedVisualsWithTheSameContent_AreAmbiguous_AndTheirQuestionsGo()
    {
        await using var scope = await Scope.CreateAsync();
        var hash = scope.AddVisual(3, "Twin");
        scope.AddVisual(4, "Twin");
        scope.AddQuestion(1, "First twin?", SubscriberQuestionOrigin.Manual);
        scope.AddQuestion(2, "Second twin?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        await SubscriberQuestionEnrichment.ReconcileAsync(
            scope.Db, scope.RepoId,
            new Dictionary<string, string> { [Scope.Key(1)] = hash, [Scope.Key(2)] = hash },
            CancellationToken.None);

        Assert.Empty(await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task RepoWithNoVisualsLeft_HasAllItsQuestionsPruned()
    {
        await using var scope = await Scope.CreateAsync();
        scope.AddQuestion(1, "What did the deleted report show?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string>(), generator: null, CancellationToken.None);

        Assert.Empty(await scope.QuestionsAsync());
    }

    /// <summary>One test's own repo in the shared test catalog, removed again on dispose.</summary>
    private sealed class Scope : IAsyncDisposable
    {
        private Scope(CatalogDbContext db) => Db = db;

        public CatalogDbContext Db { get; }

        public Guid RepoId { get; } = Guid.NewGuid();

        public static string Key(int ordinal) => $"{Page}#{ordinal}";

        public static async Task<Scope> CreateAsync()
        {
            var cs = CatalogTestDb.Require();
            await CatalogDatabase.MigrateAsync(cs);
            return new Scope(CatalogDatabase.Create(cs));
        }

        /// <summary>Stages a visual at <paramref name="ordinal"/> and returns its content hash.</summary>
        public string AddVisual(int ordinal, string title)
        {
            var hash = SubscriberReportVisualHash.Compute(title, "card", []);
            Db.SubscriberReportVisuals.Add(new CatalogSubscriberReportVisual
            {
                RepoId = RepoId, PageKey = Page, VisualKey = Key(ordinal), Ordinal = ordinal, VisualType = "card",
                Title = title, QueryName = $"report.pbix / Page 1 / {title}", ContentHash = hash,
            });
            return hash;
        }

        public void AddQuestion(int visualOrdinal, string question, string origin)
        {
            var ordinal = Db.SubscriberReportVisualQuestions.Local.Count(q => q.VisualKey == Key(visualOrdinal)) + 1;
            Db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
            {
                RepoId = RepoId, VisualKey = Key(visualOrdinal), Ordinal = ordinal, Question = question,
                Origin = origin,
                UpdatedBy = origin == SubscriberQuestionOrigin.Manual ? "tester" : null,
                UpdatedUtc = origin == SubscriberQuestionOrigin.Manual ? DateTime.UtcNow : null,
            });
        }

        public async Task<List<CatalogSubscriberReportVisualQuestion>> RowsAsync()
            => await Db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == RepoId)
                .OrderBy(q => q.VisualKey).ThenBy(q => q.Ordinal).ThenBy(q => q.Id)
                .ToListAsync();

        public async Task<List<(string VisualKey, string Question)>> QuestionsAsync()
            => (await RowsAsync()).Select(q => (q.VisualKey, q.Question)).ToList();

        public async ValueTask DisposeAsync()
        {
            await Db.SubscriberReportVisualQuestions.Where(q => q.RepoId == RepoId).ExecuteDeleteAsync();
            await Db.SubscriberReportVisuals.Where(v => v.RepoId == RepoId).ExecuteDeleteAsync();
            await Db.DisposeAsync();
        }
    }
}
