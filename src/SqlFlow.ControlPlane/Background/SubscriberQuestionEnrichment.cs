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
/// questions of a visual that only changed position and removes those whose visual is gone;
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
    /// when <paramref name="generator"/> is given (question generation is on for this sync). Returns the generation
    /// warnings.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApplyAfterSyncAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before, QuestionGenerator? generator,
        CancellationToken ct)
    {
        var reconciled = await ReconcileAsync(db, repoId, before, ct).ConfigureAwait(false);
        return generator is null
            ? []
            : await EnrichAsync(db, repoId, reconciled, generator, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Brings this repo's questions in line with the visuals the sync just wrote, whether or not generation is on. A
    /// question whose visual is gone follows it when the visual only moved (its old content hash now belongs to exactly
    /// one visual the snapshot did not have, and that visual has no questions of its own), so a reordered page keeps
    /// its questions, including the ones a person wrote. Every other question whose visual is gone is deleted: the
    /// question table has no foreign key to the visual, and question search would otherwise keep serving it. Returns
    /// <paramref name="before"/> with each moved visual also recorded under its new key, so generation treats it as
    /// the unchanged visual it is.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ReconcileAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(before);

        var orphanKeys = await db.SubscriberReportVisualQuestions.AsNoTracking()
            .Where(q => q.RepoId == repoId
                && !db.SubscriberReportVisuals.Any(v => v.RepoId == repoId && v.VisualKey == q.VisualKey))
            .Select(q => q.VisualKey)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (orphanKeys.Count == 0)
        {
            return before;
        }

        // Candidate destinations: visuals this sync introduced under a key the snapshot did not have, with no
        // questions yet, grouped by content. A hash shared by two such visuals is ambiguous and moves nothing.
        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId
                && !db.SubscriberReportVisualQuestions.Any(q => q.RepoId == repoId && q.VisualKey == v.VisualKey))
            .Select(v => new { v.VisualKey, v.ContentHash })
            .ToListAsync(ct).ConfigureAwait(false);
        var destinations = visuals
            .Where(v => !before.ContainsKey(v.VisualKey))
            .GroupBy(v => v.ContentHash, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().VisualKey, StringComparer.Ordinal);

        // An orphan hash shared by two old keys is just as ambiguous.
        var orphanHashes = orphanKeys
            .Where(before.ContainsKey)
            .GroupBy(key => before[key], StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Single(), g => g.Key, StringComparer.Ordinal);

        var reconciled = new Dictionary<string, string>(before, StringComparer.Ordinal);
        foreach (var orphan in orphanKeys)
        {
            if (orphanHashes.TryGetValue(orphan, out var hash) && destinations.TryGetValue(hash, out var destination))
            {
                await db.SubscriberReportVisualQuestions
                    .Where(q => q.RepoId == repoId && q.VisualKey == orphan)
                    .ExecuteUpdateAsync(u => u.SetProperty(q => q.VisualKey, destination), ct)
                    .ConfigureAwait(false);
                reconciled[destination] = hash;
            }
        }

        await db.SubscriberReportVisualQuestions
            .Where(q => q.RepoId == repoId
                && !db.SubscriberReportVisuals.Any(v => v.RepoId == repoId && v.VisualKey == q.VisualKey))
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return reconciled;
    }

    /// <summary>
    /// Generates questions for every visual this sync wrote for <paramref name="repoId"/> that is new or changed
    /// against <paramref name="before"/>, or that has no questions at all (one synced while generation was off, or
    /// whose generation failed). An unchanged visual with questions is left alone, with no LLM call. Only generated
    /// questions are replaced; a person's stay. Call after <see cref="ReconcileAsync"/>, with the snapshot it returns,
    /// so a moved visual's questions are already under its new key and it reads as unchanged. Writes directly (its own
    /// <c>SaveChangesAsync</c>), after the sync's own transaction has committed, so a generation failure never rolls
    /// back what the sync wrote. Returns the warnings collected (a visual whose questions could not be generated), for
    /// the caller to surface the same way a sync's own warnings are surfaced.
    /// </summary>
    public static Task<IReadOnlyList<string>> EnrichAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before,
        QuestionGenerator generator, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(generator);
        return EnrichAsync(db, repoId, before, generator.GenerateQuestionsAsync, ct);
    }

    /// <summary><see cref="EnrichAsync(CatalogDbContext, Guid, IReadOnlyDictionary{string, string}, QuestionGenerator, CancellationToken)"/>
    /// over any question source, so the replacement rules can be exercised without a model.</summary>
    internal static async Task<IReadOnlyList<string>> EnrichAsync(
        CatalogDbContext db, Guid repoId, IReadOnlyDictionary<string, string> before,
        Func<VisualQuestionContext, CancellationToken, Task<IReadOnlyList<string>>> generate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(generate);

        var visuals = await db.SubscriberReportVisuals.AsNoTracking()
            .Where(v => v.RepoId == repoId)
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
        foreach (var visual in pending)
        {
            var context = new VisualQuestionContext(
                visual.Title,
                visual.VisualType,
                fieldsByVisual[visual.VisualKey]
                    .Select(f => new VisualQuestionField(f.Role, f.TableName, f.ColumnOrMeasure, f.IsMeasure))
                    .ToArray());
            var questions = await generate(context, ct).ConfigureAwait(false);
            if (questions.Count == 0)
            {
                // Whatever the visual already has is kept: a changed visual's earlier questions are closer to right
                // than none, and a later sync tries again.
                warnings.Add(
                    $"subscriber report visual '{visual.Title ?? visual.VisualType}' ({visual.VisualKey}): "
                    + "no business questions could be generated; it keeps the questions it had.");
                continue;
            }

            await db.SubscriberReportVisualQuestions
                .Where(q => q.RepoId == repoId && q.VisualKey == visual.VisualKey
                    && q.Origin == SubscriberQuestionOrigin.Generated)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);

            // A person's questions keep their place; generated ones follow them, skipping any the person already
            // wrote in the same words.
            var manual = await db.SubscriberReportVisualQuestions.AsNoTracking()
                .Where(q => q.RepoId == repoId && q.VisualKey == visual.VisualKey)
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
                    VisualKey = visual.VisualKey,
                    Ordinal = ordinal,
                    Question = question,
                    Origin = SubscriberQuestionOrigin.Generated,
                });
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return warnings;
    }
}
