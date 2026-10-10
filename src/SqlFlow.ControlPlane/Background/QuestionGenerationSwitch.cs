using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Background;

/// <summary>Whether sync-time question generation runs, and why.</summary>
/// <param name="Available">The deployment has an Anthropic key to generate with. Without one nothing can turn
/// generation on.</param>
/// <param name="DeploymentDefault"><c>ControlPlane:PowerAI:QuestionGeneration:Enabled</c>, followed while no admin
/// has chosen.</param>
/// <param name="Override">An admin's choice, or null to follow the deployment default.</param>
/// <param name="Enabled">The outcome: available, and switched on by the override or else by the default.</param>
/// <param name="UpdatedBy">Who last changed the switch (including a return to the default); null when no admin has.</param>
/// <param name="UpdatedUtc">When the switch was last changed.</param>
public sealed record QuestionGenerationState(
    bool Available, bool DeploymentDefault, bool? Override, bool Enabled, string? UpdatedBy, DateTime? UpdatedUtc);

/// <summary>
/// The one place that decides whether a sync generates business questions for Power BI report visuals. The deployment
/// configuration supplies the default and the Anthropic key; an admin can turn generation on or off at runtime from the
/// semantic layer page, stored on the layer's settings row, so the choice holds across restarts and replicas without a
/// redeploy. Every sync path reads it here, once per sync, before snapshotting the questions it may carry forward.
/// </summary>
public sealed class QuestionGenerationSwitch
{
    private readonly IOptions<ControlPlaneOptions> _options;

    public QuestionGenerationSwitch(IOptions<ControlPlaneOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Whether the deployment has an Anthropic key, which is what the generator is registered on.</summary>
    public bool Available => !string.IsNullOrWhiteSpace(_options.Value.Assistant.Anthropic.ApiKey);

    /// <summary>Reads the current state.</summary>
    public async Task<QuestionGenerationState> ReadAsync(CatalogDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var row = await db.SemanticLayerSettings.AsNoTracking()
            .Where(s => s.Id == CatalogSemanticLayerSettings.SingletonId)
            .Select(s => new { s.QuestionGeneration, s.QuestionGenerationUpdatedBy, s.QuestionGenerationUpdatedUtc })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return State(row?.QuestionGeneration, row?.QuestionGenerationUpdatedBy, row?.QuestionGenerationUpdatedUtc);
    }

    /// <summary>Whether a sync starting now generates questions. Without a key the answer is no whatever an admin
    /// chose, so the catalog is not read.</summary>
    public async Task<bool> IsEnabledAsync(CatalogDbContext db, CancellationToken ct)
        => Available && (await ReadAsync(db, ct).ConfigureAwait(false)).Enabled;

    /// <summary>
    /// Stores an admin's choice (null returns to the deployment default). The caller refuses turning generation on
    /// where it is not <see cref="Available"/>; this only records.
    /// </summary>
    public async Task<QuestionGenerationState> SetAsync(
        CatalogDbContext db, bool? value, string? actor, DateTime nowUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        for (var attempt = 0; ; attempt++)
        {
            var row = await db.SemanticLayerSettings.AsTracking()
                .FirstOrDefaultAsync(s => s.Id == CatalogSemanticLayerSettings.SingletonId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new CatalogSemanticLayerSettings { Id = CatalogSemanticLayerSettings.SingletonId, UpdatedUtc = nowUtc };
                db.SemanticLayerSettings.Add(row);
            }

            row.QuestionGeneration = value;
            row.QuestionGenerationUpdatedBy = actor;
            row.QuestionGenerationUpdatedUtc = nowUtc;

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return State(row.QuestionGeneration, row.QuestionGenerationUpdatedBy, row.QuestionGenerationUpdatedUtc);
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                // Another save inserted the singleton first; update that row instead.
                db.ChangeTracker.Clear();
            }
        }
    }

    private QuestionGenerationState State(bool? choice, string? updatedBy, DateTime? updatedUtc)
    {
        var available = Available;
        var deploymentDefault = _options.Value.PowerAI.QuestionGeneration.Enabled;
        return new QuestionGenerationState(
            available, deploymentDefault, choice, available && (choice ?? deploymentDefault), updatedBy, updatedUtc);
    }
}
