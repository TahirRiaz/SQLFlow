using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// Reads and writes the operator-tunable maintenance settings (the single <see cref="CatalogMaintenanceSetting"/>
/// row). The retention value lives here rather than in configuration because the GUI edits it live: the background
/// pruner and the maintenance endpoints both read it through this store, so a change takes effect on the next sweep
/// without a redeploy.
/// </summary>
public static class MaintenanceStore
{
    // The one settings row governs the whole estate; its key is fixed.
    private const int SingletonId = 1;

    /// <summary>The configured run-trace retention in days, or null when traces are kept forever (age-based pruning
    /// off, the default until an operator sets a value).</summary>
    public static async Task<int?> GetRunTraceRetentionDaysAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var row = await catalog.MaintenanceSettings.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == SingletonId, ct).ConfigureAwait(false);
        return row?.RunTraceRetentionDays;
    }

    /// <summary>Sets the run-trace retention (null keeps traces forever), creating the settings row on first write.
    /// Returns the stored value.</summary>
    public static async Task<int?> SetRunTraceRetentionDaysAsync(
        CatalogDbContext catalog, int? days, string? updatedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var row = await catalog.MaintenanceSettings
            .FirstOrDefaultAsync(s => s.Id == SingletonId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new CatalogMaintenanceSetting { Id = SingletonId };
            catalog.MaintenanceSettings.Add(row);
        }

        row.RunTraceRetentionDays = days;
        row.UpdatedUtc = nowUtc;
        row.UpdatedBy = updatedBy;
        await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        return row.RunTraceRetentionDays;
    }
}
