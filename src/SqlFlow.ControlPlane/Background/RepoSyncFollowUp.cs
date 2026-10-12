using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SqlFlow.Assistant;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Background;

/// <summary>Whether this deployment can generate business questions for report visuals: it has an Anthropic key. A
/// subscriber's library decides whether it does (<c>generateQuestions: true</c>).</summary>
public static class QuestionGenerationAvailability
{
    /// <summary>True when <paramref name="options"/> carry an Anthropic key to generate with.</summary>
    public static bool IsAvailable(ControlPlaneOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return !string.IsNullOrWhiteSpace(options.Assistant.Anthropic.ApiKey);
    }
}

/// <summary>What the steps after a repository sync have to say, by step.</summary>
/// <param name="Questions">From bringing report questions in line with the synced visuals and generating missing ones.</param>
/// <param name="AiContexts">From reading the repository's AI context files.</param>
public sealed record RepoSyncFollowUpResult(IReadOnlyList<string> Questions, IReadOnlyList<string> AiContexts);

/// <summary>
/// The steps every control-plane repository sync runs once the catalog sync's own transaction has committed, in one
/// place so the API's local-path sync and the managed sync of a git source cannot drift apart. Each writes on its own,
/// so a slow model call or a context that cannot be resolved never rolls back what the sync wrote.
/// </summary>
public static class RepoSyncFollowUp
{
    /// <summary>What the steps need from before the sync: this repository's report visuals as they were. Call it
    /// immediately before <see cref="CatalogSync.SyncAsync"/>, which replaces them.</summary>
    public static Task<IReadOnlyDictionary<string, string>> BeforeAsync(CatalogDbContext db, Guid repoId, CancellationToken ct)
        => SubscriberQuestionEnrichment.SnapshotAsync(db, repoId, ct);

    /// <summary>
    /// Brings report questions in line with the synced visuals, generating them for the subscribers whose library asks
    /// (where the deployment can), then reads the repository's AI context files.
    /// </summary>
    public static async Task<RepoSyncFollowUpResult> AfterAsync(
        CatalogDbContext db, IServiceProvider services, Guid repoId, string repoRoot,
        IReadOnlyDictionary<string, string> before, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(services);
        var questions = new List<string>();
        var options = services.GetRequiredService<IOptions<ControlPlaneOptions>>().Value;
        QuestionGenerator? generator = null;
        if (QuestionGenerationAvailability.IsAvailable(options))
        {
            generator = services.GetRequiredService<QuestionGenerator>();
        }
        else
        {
            var asking = await db.Subscribers.AsNoTracking()
                .CountAsync(s => s.RepoId == repoId && s.GenerateQuestions, ct).ConfigureAwait(false);
            if (asking > 0)
            {
                questions.Add(
                    $"{asking} subscriber(s) ask for generated business questions (generateQuestions: true), but this "
                    + "deployment has no Anthropic key to generate with (ControlPlane:Assistant:Anthropic:ApiKey).");
            }
        }

        questions.AddRange(await SubscriberQuestionEnrichment.ApplyAfterSyncAsync(db, repoId, before, generator, ct).ConfigureAwait(false));
        var contexts = await AiContextSync.ApplyAsync(db, repoId, repoRoot, nowUtc, ct).ConfigureAwait(false);
        return new RepoSyncFollowUpResult(questions, contexts);
    }
}
