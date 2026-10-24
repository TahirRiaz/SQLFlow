using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>The two ways a storage area gets cleaned, as <see cref="CatalogMaintenanceCleanup.Trigger"/> records them.</summary>
public static class CleanupTriggers
{
    /// <summary>An admin's "clean up now", with the days the admin chose.</summary>
    public const string Manual = "manual";

    /// <summary>The control plane's cleanup pass, applying the retention an admin set.</summary>
    public const string Automatic = "automatic";
}

/// <summary>
/// The Maintenance page's state in the catalog: the retention an admin set for each storage area
/// (<see cref="CatalogRetentionSetting"/>) and the log of every cleanup that removed something, failed or was asked for
/// (<see cref="CatalogMaintenanceCleanup"/>). The retention lives here rather than in configuration because the GUI edits it
/// live: the cleanup pass and the endpoints read it through this store, so a change takes effect on the next pass without a
/// redeploy. The store knows area ids only; what an area is, and how it is cleaned, belongs to whoever describes it.
/// </summary>
public static class MaintenanceStore
{
    /// <summary>How long the cleanup log keeps an entry: a year of who removed what, after which the log trims itself.</summary>
    public static readonly TimeSpan CleanupLogKept = TimeSpan.FromDays(365);

    /// <summary>The longest error a cleanup entry stores, the column's width.</summary>
    public const int MaxErrorLength = 2000;

    /// <summary>Every retention an admin set, by area id. An area with no entry, or a null value, has no limit.</summary>
    public static async Task<IReadOnlyDictionary<string, CatalogRetentionSetting>> GetRetentionsAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var rows = await catalog.RetentionSettings.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return rows.ToDictionary(r => r.AreaId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Sets how many days an area's data stays (null removes the limit), recording who changed it and when, and answers the
    /// stored row. Turning a retention off keeps the row, so who turned it off is on record too.
    /// </summary>
    public static async Task<CatalogRetentionSetting> SetRetentionAsync(
        CatalogDbContext catalog, string areaId, int? days, string? updatedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        var row = await catalog.RetentionSettings.AsTracking().FirstOrDefaultAsync(r => r.AreaId == areaId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new CatalogRetentionSetting { AreaId = areaId };
            catalog.RetentionSettings.Add(row);
        }

        row.Days = days;
        row.UpdatedUtc = nowUtc;
        row.UpdatedBy = Cap(updatedBy, 256);
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }

        return row;
    }

    /// <summary>Records a cleanup that has started and answers its entry, whose <see cref="CatalogMaintenanceCleanup.CompletedUtc"/>
    /// stays null until <see cref="CompleteCleanupAsync"/> closes it.</summary>
    public static async Task<CatalogMaintenanceCleanup> StartCleanupAsync(
        CatalogDbContext catalog, string areaId, string trigger, string? actor, string host, int days, DateTime olderThanUtc,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var entry = new CatalogMaintenanceCleanup
        {
            AreaId = areaId,
            Trigger = trigger,
            Actor = Cap(actor, 256),
            Host = Cap(host, 256)!,
            Days = days,
            OlderThanUtc = olderThanUtc,
            StartedUtc = nowUtc,
        };
        catalog.MaintenanceCleanups.Add(entry);
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }

        return entry;
    }

    /// <summary>Closes a cleanup's entry with what it removed and, when it failed or stopped, why.</summary>
    public static Task<int> CompleteCleanupAsync(
        CatalogDbContext catalog, long cleanupId, long removed, string? error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var stored = Cap(error, MaxErrorLength);
        return catalog.MaintenanceCleanups
            .Where(c => c.Id == cleanupId && c.CompletedUtc == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(c => c.CompletedUtc, nowUtc).SetProperty(c => c.Removed, removed).SetProperty(c => c.Error, stored),
                ct);
    }

    /// <summary>
    /// Records a finished automatic cleanup in one write. A pass logs only what removed something or failed, so the log says
    /// what maintenance did rather than every hour it found nothing.
    /// </summary>
    public static async Task RecordAutomaticAsync(
        CatalogDbContext catalog, string areaId, string? actor, string host, int days, DateTime olderThanUtc, DateTime startedUtc,
        DateTime completedUtc, long removed, string? error, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        catalog.MaintenanceCleanups.Add(new CatalogMaintenanceCleanup
        {
            AreaId = areaId,
            Trigger = CleanupTriggers.Automatic,
            Actor = Cap(actor, 256),
            Host = Cap(host, 256)!,
            Days = days,
            OlderThanUtc = olderThanUtc,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            Removed = removed,
            Error = Cap(error, MaxErrorLength),
        });
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            catalog.ChangeTracker.Clear();
        }
    }

    /// <summary>One cleanup entry by its id, or null.</summary>
    public static Task<CatalogMaintenanceCleanup?> GetCleanupAsync(CatalogDbContext catalog, long cleanupId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.MaintenanceCleanups.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cleanupId, ct);
    }

    /// <summary>An area's cleanups, newest first, at most <paramref name="take"/> (1 to 500).</summary>
    public static async Task<IReadOnlyList<CatalogMaintenanceCleanup>> ListCleanupsAsync(
        CatalogDbContext catalog, string areaId, int take, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        return await catalog.MaintenanceCleanups.AsNoTracking()
            .Where(c => c.AreaId == areaId)
            .OrderByDescending(c => c.StartedUtc).ThenByDescending(c => c.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The newest cleanup of every area that has one, by area id: what the overview shows beside each area.</summary>
    public static async Task<IReadOnlyDictionary<string, CatalogMaintenanceCleanup>> LatestCleanupsAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var latestIds = catalog.MaintenanceCleanups
            .GroupBy(c => c.AreaId)
            .Select(g => g.Max(c => c.Id));
        var latest = await catalog.MaintenanceCleanups.AsNoTracking()
            .Where(c => latestIds.Contains(c.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        return latest.ToDictionary(c => c.AreaId, StringComparer.Ordinal);
    }

    /// <summary>
    /// Closes the cleanup entries still open that nobody is working on any more, and answers how many it closed: every open
    /// entry of <paramref name="host"/> but those of the areas in <paramref name="busyAreaIds"/> (the host was stopped with
    /// them), and any entry open longer than <paramref name="staleAfter"/> (its host went away). What such a cleanup removed stays removed; the
    /// entry says so, and that running it again finishes it.
    /// </summary>
    public static Task<int> CloseInterruptedAsync(
        CatalogDbContext catalog, string host, IReadOnlyCollection<string> busyAreaIds, TimeSpan staleAfter, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(busyAreaIds);
        const string Interrupted = "The control plane stopped before this cleanup finished. What it removed stays removed; clean up again to finish.";
        var staleBefore = nowUtc - staleAfter;
        var busy = busyAreaIds.ToList();
        return catalog.MaintenanceCleanups
            .Where(c => c.CompletedUtc == null && !busy.Contains(c.AreaId) && (c.Host == host || c.StartedUtc < staleBefore))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.CompletedUtc, nowUtc).SetProperty(c => c.Error, Interrupted), ct);
    }

    /// <summary>Removes the cleanup log's finished entries older than <see cref="CleanupLogKept"/>, and answers how many went.</summary>
    public static Task<int> TrimCleanupLogAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var before = nowUtc - CleanupLogKept;
        return catalog.MaintenanceCleanups
            .Where(c => c.StartedUtc < before && c.CompletedUtc != null)
            .ExecuteDeleteAsync(ct);
    }

    private static string? Cap(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];
}
