using Microsoft.EntityFrameworkCore;
using SqlFlow.Assistant;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Keeps each extracted PowerBI report visual's business questions (POWERAI.md's "business-question field", Section
/// 10, item 3) in step with a sync. This runs as a control-plane-only step AFTER <see cref="CatalogSync.SyncAsync"/>
/// has written a sync's <c>SubscriberReportVisual</c>/<c>Field</c> rows: <c>tools/pbix-extract</c> stays a pure parser
/// (no network, no LLM, matching its "untrusted input" security posture), and <c>CatalogSync</c>/<c>SqlFlow.Lineage</c>
/// stay free of it, since both are shared code the bare CLI's <c>sqlflow db sync</c> also runs with no Anthropic wiring.
/// <para>
/// A sync deletes and reinserts every visual row but never touches the question rows, which are keyed by the visual's
/// positional <c>VisualKey</c>. So the caller snapshots each visual's <see cref="CatalogSubscriberReportVisual.ContentHash"/>
/// BEFORE the sync (<see cref="SnapshotAsync"/>) and then, after it: <see cref="ReconcileAsync"/> (always) moves the
/// questions of a visual that only changed position, wherever the others moved, and removes those whose visual is gone;
/// <see cref="EnrichAsync(CatalogDbContext, Guid, IReadOnlyDictionary{string, string}, QuestionGenerator, CancellationToken)"/>
/// (only while generation is on) generates questions for a visual that is new, changed, or has none yet. Generation
/// only ever replaces <see cref="SubscriberQuestionOrigin.Generated"/> questions: a person's
/// (<see cref="SubscriberQuestionOrigin.Manual"/>) stay until they or their visual go.
/// </para>
/// </summary>
public static class SubscriberQuestionEnrichment
{
    /// <summary>Reads each of this repo's visuals' content hash, keyed by <c>VisualKey</c>, so the steps after the
    /// sync can tell an unchanged visual from a new, changed, or moved one. Call this immediately BEFORE
    /// <see cref="CatalogSync.SyncAsync"/>.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> SnapshotAsync(
        CatalogDbContext db, Guid repoId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        return await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId)
            .Select(v => new { v.VisualKey, v.ContentHash })
            .ToDictionaryAsync(v => v.VisualKey, v => v.ContentHash, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The step every control-plane sync runs once its own transaction has committed: <see cref="ReconcileAsync"/>,
    /// then <see cref="EnrichAsync(CatalogDbContext, Guid, IReadOnlyDictionary{string, string}, QuestionGenerator, CancellationToken)"/>
    /// when <paramref name="generator"/> is given (the deployment can generate), for the subscribers whose library asks
    /// for it. Returns the generation warnings.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAfterSyncAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before, QuestionGenerator? generator,
        CancellationToken ct)
    {
        var reconciled = await ReconcileAsync(db, repoId, before, ct).ConfigureAwait(false);

        // A subscriber whose library does not ask for generated questions has none served: those a model wrote for it
        // earlier go, while the ones a person wrote stay.
        var notAsking = db.Subscribers.Where(s => s.RepoId == repoId && !s.GenerateQuestions).Select(s => s.ObjectKey);
        var pages = db.SubscriberReportPages.Where(p => p.RepoId == repoId && notAsking.Contains(p.SubscriberKey)).Select(p => p.PageKey);
        var visuals = db.SubscriberReportVisuals.Where(v => v.RepoId == repoId && pages.Contains(v.PageKey)).Select(v => v.VisualKey);
        await db.SubscriberReportVisualQuestions
            .Where(q => q.RepoId == repoId && q.Origin == SubscriberQuestionOrigin.Generated && visuals.Contains(q.VisualKey))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);

        return generator is null
            ? []
            : await EnrichAsync(db, repoId, reconciled, generator, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings this repo's questions in line with the visuals the sync just wrote, whether or not generation is on.
    /// Questions follow their visual by content: a key whose visual changed or went away hands its questions to the
    /// one key whose visual now has the content it had, when that content is unique on both sides and the receiving
    /// key keeps no questions of its own, so a reordered page keeps its questions, including the ones a person wrote,
    /// even where every old key still exists (a visual inserted at the front shifts every other one along). Moves are
    /// made in two steps through keys no visual has, so two visuals that swapped places exchange questions rather than
    /// merge them. A visual edited in place keeps its questions. Every question whose key has no visual at all is then
    /// deleted: the question table has no foreign key to the visual, and question search would otherwise keep serving
    /// it. Returns <paramref name="before"/> with each receiving key recorded under the content it received, so
    /// generation treats it as the unchanged visual it is.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReconcileAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(before);

        var current = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId)
            .Select(v => new { v.VisualKey, v.ContentHash })
            .ToDictionaryAsync(v => v.VisualKey, v => v.ContentHash, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);
        var answered = (await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId)
                .Select(q => q.VisualKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);
        if (answered.Count == 0)
        {
            return before;
        }

        var moves = PlanMoves(before, current, answered);
        var reconciled = new Dictionary<string, string>(before, StringComparer.Ordinal);
        foreach (var (source, destination) in moves)
        {
            reconciled[destination] = before[source];
        }

        var orphaned = answered.Any(key => !current.ContainsKey(key) && !moves.ContainsKey(key));
        if (moves.Count == 0 && !orphaned)
        {
            return before;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

            // Each source moves to a key of its own no visual has first, then on to its destination, so a destination
            // that is itself a source (a swap, or a longer cycle) never receives before it has handed its own on.
            var parked = moves.Keys.ToDictionary(key => key, _ => ParkingKey(), StringComparer.Ordinal);
            foreach (var (source, parking) in parked)
            {
                await db.SubscriberReportVisualQuestions
                    .Where(q => q.RepoId == repoId && q.VisualKey == source)
                    .ExecuteUpdateAsync(u => u.SetProperty(q => q.VisualKey, parking), ct)
                    .ConfigureAwait(false);
            }

            foreach (var (source, parking) in parked)
            {
                var destination = moves[source];
                await db.SubscriberReportVisualQuestions
                    .Where(q => q.RepoId == repoId && q.VisualKey == parking)
                    .ExecuteUpdateAsync(u => u.SetProperty(q => q.VisualKey, destination), ct)
                    .ConfigureAwait(false);
            }

            await db.SubscriberReportVisualQuestions
                .Where(q => q.RepoId == repoId
                    && !db.SubscriberReportVisuals.Any(v => v.RepoId == repoId && v.VisualKey == q.VisualKey))
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return reconciled;
    }

    /// <summary>
    /// Which keys hand their questions to which: a key with questions whose content is no longer at that key moves to
    /// the one key now holding that content, provided the content was unique among the keys with questions before the
    /// sync and is unique among the visuals after it, and the receiving key keeps no questions of its own (it has none,
    /// or hands them on too).
    /// </summary>
    internal static IReadOnlyDictionary<string, string> PlanMoves(
        IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> current, IReadOnlySet<string> answered)
    {
        var uniqueBefore = answered
            .Where(before.ContainsKey)
            .GroupBy(key => before[key], StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var uniqueNow = current
            .GroupBy(pair => pair.Value, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Key, StringComparer.Ordinal);

        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in answered)
        {
            if (!before.TryGetValue(source, out var content)
                || (current.TryGetValue(source, out var now) && string.Equals(now, content, StringComparison.Ordinal))
                || !uniqueBefore.TryGetValue(content, out var only) || !string.Equals(only, source, StringComparison.Ordinal)
                || !uniqueNow.TryGetValue(content, out var destination)
                || string.Equals(destination, source, StringComparison.Ordinal))
            {
                continue;
            }

            moves[source] = destination;
        }

        // A destination keeping questions of its own receives none: they would be read as one visual's.
        bool changed;
        do
        {
            changed = false;
            foreach (var (source, destination) in moves.ToList())
            {
                if (answered.Contains(destination) && !moves.ContainsKey(destination))
                {
                    moves.Remove(source);
                    changed = true;
                }
            }
        }
        while (changed);

        return moves;
    }

    /// <summary>A visual key no visual has, for questions on their way from one visual to another.</summary>
    private static string ParkingKey() => "moving:" + Guid.NewGuid().ToString("N");

    /// <summary>
    /// Generates questions for every visual this sync wrote for <paramref name="repoId"/> that is new or changed
    /// against <paramref name="before"/>, or that has no questions at all (one synced while generation was off, or
    /// whose generation failed). An unchanged visual with questions is left alone, with no LLM call. Only generated
    /// questions are replaced; a person's stay. Call after <see cref="ReconcileAsync"/>, with the snapshot it returns,
    /// so a moved visual's questions are already under its new key and it reads as unchanged. Writes directly, after
    /// the sync's own transaction has committed, so a generation failure never rolls back what the sync wrote. Returns
    /// the warnings collected (a visual whose questions could not be generated, a question too long to store), for the
    /// caller to surface the same way a sync's own warnings are surfaced.
    /// </summary>
    public static Task<IReadOnlyList<string>> EnrichAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before,
        QuestionGenerator generator, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return EnrichAsync(db, repoId, before, generator.GenerateQuestionsAsync, ct);
    }

    /// <summary><see cref="EnrichAsync(CatalogDbContext, Guid, IReadOnlyDictionary{string, string}, QuestionGenerator, CancellationToken)"/>
    /// over any question source, so the replacement rules can be exercised without a model. Every question is generated
    /// before anything is written, and every replacement is then written in one transaction: a generation that fails or
    /// is cancelled part of the way through leaves every visual with the questions it had.</summary>
    internal static async Task<IReadOnlyList<string>> EnrichAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before,
        Func<VisualQuestionContext, CancellationToken, Task<IReadOnlyList<string>>> generate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(generate);

        // Only the visuals of a subscriber whose library asks for generated questions (generateQuestions: true): a visual
        // reaches its subscriber through its page.
        var asking = db.Subscribers.Where(s => s.RepoId == repoId && s.GenerateQuestions).Select(s => s.ObjectKey);
        var pages = db.SubscriberReportPages.Where(p => p.RepoId == repoId && asking.Contains(p.SubscriberKey)).Select(p => p.PageKey);
        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId && pages.Contains(v.PageKey))
            .ToListAsync(ct).ConfigureAwait(false);
        if (visuals.Count == 0)
        {
            return [];
        }

        var answered = (await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId)
                .Select(q => q.VisualKey)
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        var pending = visuals
            .Where(v => !answered.Contains(v.VisualKey)
                || !before.TryGetValue(v.VisualKey, out var priorHash)
                || !string.Equals(priorHash, v.ContentHash, StringComparison.Ordinal))
            .ToList();
        if (pending.Count == 0)
        {
            return [];
        }

        var pendingKeys = pending.Select(v => v.VisualKey).ToList();
        var fieldsByVisual = (await db.SubscriberReportFields.AsNoTracking()
                .Where(f => f.RepoId == repoId && pendingKeys.Contains(f.VisualKey))
                .ToListAsync(ct).ConfigureAwait(false))
            .ToLookup(f => f.VisualKey);

        var warnings = new List<string>();
        var replacements = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var visual in pending)
        {
            var context = new VisualQuestionContext(
                visual.Title,
                visual.VisualType,
                fieldsByVisual[visual.VisualKey]
                    .Select(f => new VisualQuestionField(f.Role, f.TableName, f.ColumnOrMeasure, f.IsMeasure))
                    .ToArray());
            var label = $"subscriber report visual '{visual.Title ?? visual.VisualType}' ({visual.VisualKey})";

            // What can be stored: trimmed, not blank, and within the column. A longer question is not cut into a
            // different one; it is left out, and said so.
            var questions = new List<string>();
            foreach (var generated in await generate(context, ct).ConfigureAwait(false) ?? [])
            {
                var question = generated?.Trim();
                if (string.IsNullOrEmpty(question))
                {
                    continue;
                }

                if (question.Length > CatalogSubscriberReportVisualQuestion.MaxQuestionLength)
                {
                    warnings.Add($"{label}: a generated question of {question.Length} characters is over the "
                        + $"{CatalogSubscriberReportVisualQuestion.MaxQuestionLength}-character limit and was left out.");
                    continue;
                }

                questions.Add(question);
            }

            if (questions.Count == 0)
            {
                // Whatever the visual already has is kept: a changed visual's earlier questions are closer to right
                // than none, and a later sync tries again.
                warnings.Add($"{label}: no business questions could be generated; it keeps the questions it had.");
                continue;
            }

            replacements[visual.VisualKey] = questions;
        }

        if (replacements.Count == 0)
        {
            return warnings;
        }

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var (visualKey, questions) in replacements)
            {
                await db.SubscriberReportVisualQuestions
                    .Where(q => q.RepoId == repoId && q.VisualKey == visualKey
                        && q.Origin == SubscriberQuestionOrigin.Generated)
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);

                // A person's questions keep their place; generated ones follow them, skipping any the person already
                // wrote in the same words, and any repeated in the generation itself.
                var manual = await db.SubscriberReportVisualQuestions.AsNoTracking()
                    .Where(q => q.RepoId == repoId && q.VisualKey == visualKey)
                    .Select(q => new { q.Ordinal, q.Question })
                    .ToListAsync(ct).ConfigureAwait(false);
                var taken = manual.Select(m => m.Question).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var ordinal = manual.Count == 0 ? 0 : manual.Max(m => m.Ordinal);
                foreach (var question in questions)
                {
                    if (!taken.Add(question))
                    {
                        continue;
                    }

                    ordinal++;
                    db.SubscriberReportVisualQuestions.Add(new CatalogSubscriberReportVisualQuestion
                    {
                        RepoId = repoId,
                        VisualKey = visualKey,
                        Ordinal = ordinal,
                        Question = question,
                        Origin = SubscriberQuestionOrigin.Generated,
                    });
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return warnings;
    }
}
