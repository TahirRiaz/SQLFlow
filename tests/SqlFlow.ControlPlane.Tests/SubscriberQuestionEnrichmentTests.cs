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
/// a visual that only moved, and a question whose visual is gone is removed. Only a subscriber whose library asks for
/// generated questions (<c>generateQuestions: true</c>) is sent to the generator; one that does not ask loses the questions
/// a model wrote for it and keeps the ones a person wrote.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SubscriberQuestionEnrichmentTests
{
    private const string Page = "subscriber|enrich#report.pbix#1";

    private const string SubscriberKey = "subscriber|enrich";

    /// <summary>An Anthropic key that authenticates with nobody: any call through it fails fast and yields no
    /// questions, so a test asserting "the generator was never invoked" proves it by seeing no warning.</summary>
    private static QuestionGenerator UncallableGenerator()
        => new(new AnthropicOptions { ApiKey = "sk-ant-invalid-test-key", Model = "claude-sonnet-5" },
            NullLogger<QuestionGenerator>.Instance);

    [SkippableFact]
    public async Task ASubscriberThatDoesNotAsk_IsNeverSentToTheGenerator_AndLosesOnlyItsGeneratedQuestions()
    {
        await using var scope = await Scope.CreateAsync(generateQuestions: false);
        scope.AddVisual(1, "Sales by Region");
        scope.AddVisual(2, "Never answered");
        scope.AddQuestion(1, "What are sales by region?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(1, "Which region grew fastest?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        var calls = 0;
        var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string>(),
            (_, _) =>
            {
                calls++;
                return Task.FromResult<IReadOnlyList<string>>(["A generated question?"]);
            },
            CancellationToken.None);
        Assert.Empty(warnings);
        Assert.Equal(0, calls);

        // The step every sync runs removes what a model wrote for a subscriber that no longer asks, generator or not.
        using var generator = UncallableGenerator();
        Assert.Empty(await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string>(), generator, CancellationToken.None));
        Assert.Equal([(Scope.Key(1), "Which region grew fastest?")], await scope.QuestionsAsync());
    }

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
    public async Task AVisualInsertedAtTheFront_LeavesEveryOtherVisualsQuestionsWithIt()
    {
        // Every old key still exists after the insert, so nothing is orphaned: the questions must follow content.
        await using var scope = await Scope.CreateAsync();
        var before = new Dictionary<string, string>
        {
            [Scope.Key(1)] = SubscriberReportVisualHash.Compute("Sales", "card", []),
            [Scope.Key(2)] = SubscriberReportVisualHash.Compute("Margin", "card", []),
        };
        scope.AddVisual(1, "Brand new");
        scope.AddVisual(2, "Sales");
        scope.AddVisual(3, "Margin");
        scope.AddQuestion(1, "What are sales?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(1, "A person's sales question?", SubscriberQuestionOrigin.Manual);
        scope.AddQuestion(2, "A person's margin question?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        var reconciled = await SubscriberQuestionEnrichment.ReconcileAsync(scope.Db, scope.RepoId, before, CancellationToken.None);

        Assert.Equal(
            [
                (Scope.Key(2), "What are sales?"), (Scope.Key(2), "A person's sales question?"),
                (Scope.Key(3), "A person's margin question?"),
            ],
            await scope.QuestionsAsync());
        Assert.Equal(before[Scope.Key(1)], reconciled[Scope.Key(2)]);
        Assert.Equal(before[Scope.Key(2)], reconciled[Scope.Key(3)]);
    }

    [SkippableFact]
    public async Task TwoVisualsThatSwappedPlaces_ExchangeTheirQuestions_RatherThanMergeThem()
    {
        await using var scope = await Scope.CreateAsync();
        var sales = scope.AddVisual(2, "Sales");
        var margin = scope.AddVisual(1, "Margin");
        scope.AddQuestion(1, "What are sales?", SubscriberQuestionOrigin.Manual);
        scope.AddQuestion(2, "What is the margin?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        await SubscriberQuestionEnrichment.ReconcileAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = sales, [Scope.Key(2)] = margin },
            CancellationToken.None);

        Assert.Equal([(Scope.Key(1), "What is the margin?"), (Scope.Key(2), "What are sales?")], await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task AVisualEditedInPlace_KeepsItsQuestions_AndNothingMovesOntoThem()
    {
        await using var scope = await Scope.CreateAsync();
        var sales = SubscriberReportVisualHash.Compute("Sales", "card", []);
        scope.AddVisual(1, "Sales, retitled");
        scope.AddQuestion(1, "What are sales?", SubscriberQuestionOrigin.Manual);
        await scope.Db.SaveChangesAsync();

        await SubscriberQuestionEnrichment.ReconcileAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string> { [Scope.Key(1)] = sales }, CancellationToken.None);

        Assert.Equal([(Scope.Key(1), "What are sales?")], await scope.QuestionsAsync());
    }

    [Fact]
    public void PlanMoves_FollowsAThreeWayRotation_AndRefusesAReceiverThatKeepsItsOwn()
    {
        var before = new Dictionary<string, string> { ["k1"] = "A", ["k2"] = "B", ["k3"] = "C" };
        var rotated = new Dictionary<string, string> { ["k1"] = "C", ["k2"] = "A", ["k3"] = "B" };
        Assert.Equal(
            new Dictionary<string, string> { ["k1"] = "k2", ["k2"] = "k3", ["k3"] = "k1" },
            SubscriberQuestionEnrichment.PlanMoves(before, rotated, new HashSet<string> { "k1", "k2", "k3" }));

        // k2 was edited in place (its content B is nowhere now), so it keeps its questions, and k1's cannot land on it.
        var edited = new Dictionary<string, string> { ["k1"] = "X", ["k2"] = "A" };
        Assert.Empty(SubscriberQuestionEnrichment.PlanMoves(
            new Dictionary<string, string> { ["k1"] = "A", ["k2"] = "B" }, edited, new HashSet<string> { "k1", "k2" }));

        // A key with no snapshot (a question added after it) cannot be placed, and stays where it is.
        Assert.Empty(SubscriberQuestionEnrichment.PlanMoves(
            new Dictionary<string, string>(), new Dictionary<string, string> { ["k2"] = "A" }, new HashSet<string> { "k1" }));
    }

    [SkippableFact]
    public async Task AGenerationThatFailsPartWay_LeavesEveryVisualWithTheQuestionsItHad()
    {
        await using var scope = await Scope.CreateAsync();
        scope.AddVisual(1, "First, changed");
        scope.AddVisual(2, "Second, changed");
        scope.AddQuestion(1, "First earlier?", SubscriberQuestionOrigin.Generated);
        scope.AddQuestion(2, "Second earlier?", SubscriberQuestionOrigin.Generated);
        await scope.Db.SaveChangesAsync();

        var calls = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(() => SubscriberQuestionEnrichment.EnrichAsync(
            scope.Db, scope.RepoId,
            new Dictionary<string, string> { [Scope.Key(1)] = "old first", [Scope.Key(2)] = "old second" },
            (_, _) => ++calls == 1
                ? Task.FromResult<IReadOnlyList<string>>(["A new first question?"])
                : throw new OperationCanceledException("the sync was stopped"),
            CancellationToken.None));

        Assert.Equal(2, calls);
        Assert.Equal([(Scope.Key(1), "First earlier?"), (Scope.Key(2), "Second earlier?")], await scope.QuestionsAsync());
    }

    [SkippableFact]
    public async Task AGeneratedQuestionTooLongToStore_IsLeftOut_AndSaidSo_WhileTheRestAreStored()
    {
        await using var scope = await Scope.CreateAsync();
        scope.AddVisual(1, "Changed");
        await scope.Db.SaveChangesAsync();

        var tooLong = new string('q', CatalogSubscriberReportVisualQuestion.MaxQuestionLength + 1);
        var warnings = await SubscriberQuestionEnrichment.EnrichAsync(
            scope.Db, scope.RepoId, new Dictionary<string, string>(),
            (_, _) => Task.FromResult<IReadOnlyList<string>>([tooLong, "  ", "What changed?", "WHAT CHANGED?"]),
            CancellationToken.None);

        Assert.Contains($"{tooLong.Length} characters", Assert.Single(warnings), StringComparison.Ordinal);
        Assert.Equal([(Scope.Key(1), "What changed?")], await scope.QuestionsAsync());
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

        /// <summary>A repo with one Power BI subscriber and one page, whose library asks for generated questions
        /// unless <paramref name="generateQuestions"/> says otherwise.</summary>
        public static async Task<Scope> CreateAsync(bool generateQuestions = true)
        {
            var cs = CatalogTestDb.Require();
            await CatalogDatabase.MigrateAsync(cs);
            var scope = new Scope(CatalogDatabase.Create(cs));
            var now = DateTime.UtcNow;
            scope.Db.Subscribers.Add(new CatalogSubscriber
            {
                RepoId = scope.RepoId, Name = "Enrich", Type = "PowerBI", ObjectKey = SubscriberKey, File = "subscribers.yaml",
                GenerateQuestions = generateQuestions, FirstSeenUtc = now, LastSeenUtc = now,
            });
            scope.Db.SubscriberReportPages.Add(new CatalogSubscriberReportPage
            {
                RepoId = scope.RepoId, SubscriberKey = SubscriberKey, PageKey = Page, ReportFile = "report.pbix", Ordinal = 1,
                Name = "Page 1", DisplayName = "Page 1",
            });
            await scope.Db.SaveChangesAsync();
            return scope;
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
            await Db.SubscriberReportPages.Where(p => p.RepoId == RepoId).ExecuteDeleteAsync();
            await Db.Subscribers.Where(s => s.RepoId == RepoId).ExecuteDeleteAsync();
            await Db.DisposeAsync();
        }
    }
}
