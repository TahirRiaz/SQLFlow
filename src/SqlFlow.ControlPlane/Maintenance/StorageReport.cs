using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Maintenance;

/// <summary>One table as the Maintenance page shows it: its schema and name, its rows and the bytes it takes.</summary>
public sealed record StorageTableDto(string Schema, string Name, long Rows, long Bytes);

/// <summary>The column an area clears, as the page names it.</summary>
public sealed record StorageColumnDto(string Schema, string Table, string Column);

/// <summary>
/// An area's retention: what it always keeps, what its cleanup counts (<see cref="Unit"/>, plural), the days it takes, the
/// days an admin set (null: no limit) with who set them and when, and, when days are set, how many a cleanup at those days
/// would remove now (null when not counted, with <see cref="ReclaimableError"/> saying why when counting failed).
/// </summary>
public sealed record StorageRetentionDto(
    string Keeps, string Unit, int MinDays, int MaxDays, int? Days, DateTime? UpdatedUtc, string? UpdatedBy, long? Reclaimable,
    string? ReclaimableError);

/// <summary>One cleanup of an area: who asked and how, at which cut-off, when it ran, how many rows went and how it ended.
/// <see cref="Running"/> says whether a control plane is still working on it.</summary>
public sealed record CleanupDto(
    long Id, string AreaId, string Trigger, string? Actor, int Days, DateTime OlderThanUtc, DateTime StartedUtc, DateTime? CompletedUtc,
    long Removed, string? Error, bool Running);

/// <summary>
/// One storage area as the Maintenance page shows it. <see cref="Verdict"/> is <c>keep</c>, <c>safe</c> or <c>optional</c>.
/// <see cref="Rows"/> and <see cref="Bytes"/> are its tables' (for an area of one column: the rows holding a value and the
/// bytes the values take); the area holding a table some column area clears is measured without that column's values.
/// <see cref="MeasureError"/> says why a measure is missing.
/// </summary>
public sealed record StorageAreaDto(
    string Id, string Owner, string Group, string Title, string Verdict, string Holds, string Reason, string? Lifecycle, string? Brief, bool Grows,
    string? ManagedAt, IReadOnlyList<string> Claims, StorageColumnDto? Column, IReadOnlyList<StorageTableDto> Tables, long Rows, long Bytes,
    string? MeasureError, StorageRetentionDto? Retention, CleanupDto? LastCleanup, bool Running);

/// <summary>
/// The whole database as the Maintenance page shows it: its name and data files, every area every contributor describes, the
/// tables no area describes (so nothing in the database goes unaccounted), whether the automatic cleanup runs and how often,
/// and when the measure was taken.
/// </summary>
public sealed record StorageReportDto(
    DateTime MeasuredUtc, string Database, long DataFileBytes, bool AutomaticCleanup, int CleanupIntervalSeconds,
    IReadOnlyList<StorageAreaDto> Areas, IReadOnlyList<StorageTableDto> Undescribed);

/// <summary>Builds the Maintenance page's report from SQL Server's metadata, the contributors' areas and the catalog's
/// maintenance state.</summary>
public static class StorageReport
{
    /// <summary>
    /// Measures every table once, assigns each to the area that holds it, measures the columns areas clear, and adds each
    /// area's retention, what a cleanup would remove now, and its latest cleanup. One area that cannot be measured or counted
    /// carries the reason and the rest of the report stands.
    /// </summary>
    public static async Task<StorageReportDto> BuildAsync(
        CatalogDbContext catalog, IEnumerable<IStorageContributor> contributors, MaintenanceRunner runner, TimeProvider clock,
        bool automaticCleanup, int cleanupIntervalSeconds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(contributors);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(clock);

        var now = clock.GetUtcNow().UtcDateTime;
        var database = await CatalogStorage.MeasureDatabaseAsync(catalog, ct).ConfigureAwait(false);
        var tables = await CatalogStorage.MeasureTablesAsync(catalog, ct).ConfigureAwait(false);
        var retentions = await MaintenanceStore.GetRetentionsAsync(catalog, ct).ConfigureAwait(false);
        var latest = await MaintenanceStore.LatestCleanupsAsync(catalog, ct).ConfigureAwait(false);

        var described = contributors.SelectMany(c => c.Areas.Select(a => new FoundStorageArea(c, a))).ToList();
        var claimed = new HashSet<TableStorage>();
        var areaTables = new Dictionary<string, List<TableStorage>>(StringComparer.Ordinal);
        foreach (var found in described)
        {
            var held = tables.Where(t => found.Area.Tables.Any(claim => claim.Matches(t.Schema, t.Name)))
                .OrderByDescending(t => t.Bytes).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            areaTables[found.Area.Id] = held;
            claimed.UnionWith(held);
        }

        // The columns areas clear, measured once each and taken off the area that holds their table, so the areas add up to
        // the tables.
        var columnMeasures = new Dictionary<string, (ColumnStorage? Measure, string? Error)>(StringComparer.Ordinal);
        var columnBytesByTable = new Dictionary<(string Schema, string Table), long>();
        foreach (var found in described.Where(f => f.Area.Column is not null))
        {
            var column = found.Area.Column!;
            try
            {
                var measure = await CatalogStorage.MeasureColumnAsync(catalog, column.Schema, column.Table, column.Column, ct).ConfigureAwait(false);
                columnMeasures[found.Area.Id] = (measure, null);
                var key = (column.Schema.ToUpperInvariant(), column.Table.ToUpperInvariant());
                columnBytesByTable[key] = columnBytesByTable.GetValueOrDefault(key) + measure.Bytes;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                columnMeasures[found.Area.Id] = (null, "Could not measure the column: " + SecretHygiene.RedactedMessage(ex));
            }
        }

        var areas = new List<StorageAreaDto>(described.Count);
        foreach (var (contributor, area) in described)
        {
            IReadOnlyList<StorageTableDto> tableDtos;
            long rows;
            long bytes;
            string? measureError = null;
            if (area.Column is { } column)
            {
                var (measure, error) = columnMeasures[area.Id];
                tableDtos = [];
                rows = measure?.Values ?? 0;
                bytes = measure?.Bytes ?? 0;
                measureError = error;
            }
            else
            {
                var held = areaTables[area.Id];
                tableDtos = held.Select(t => new StorageTableDto(
                        t.Schema, t.Name, t.Rows,
                        Math.Max(0, t.Bytes - columnBytesByTable.GetValueOrDefault((t.Schema.ToUpperInvariant(), t.Name.ToUpperInvariant())))))
                    .ToList();
                rows = tableDtos.Sum(t => t.Rows);
                bytes = tableDtos.Sum(t => t.Bytes);
                if (held.Count == 0 && area.Tables.All(t => !t.IsPrefix))
                {
                    measureError = "None of the area's tables is in this database yet.";
                }
            }

            var retention = await RetentionAsync(contributor, area, retentions, now, ct).ConfigureAwait(false);
            var last = latest.TryGetValue(area.Id, out var entry) ? Cleanup(entry, runner, now) : null;
            areas.Add(new StorageAreaDto(
                area.Id, contributor.Owner, area.Group, area.Title, Verdict(area.Verdict), area.Holds, area.Reason, area.Lifecycle, area.Brief, area.Grows,
                area.ManagedAt, area.Tables.Select(t => t.ToString()).ToList(),
                area.Column is { } c ? new StorageColumnDto(c.Schema, c.Table, c.Column) : null,
                tableDtos, rows, bytes, measureError, retention, last, runner.IsRunning(area.Id) || last?.Running == true));
        }

        var undescribed = tables.Where(t => !claimed.Contains(t))
            .OrderByDescending(t => t.Bytes).ThenBy(t => t.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new StorageTableDto(t.Schema, t.Name, t.Rows, t.Bytes))
            .ToList();

        return new StorageReportDto(now, database.Name, database.DataFileBytes, automaticCleanup, cleanupIntervalSeconds, areas, undescribed);
    }

    /// <summary>The verdict as the API writes it.</summary>
    public static string Verdict(StorageVerdict verdict) => verdict switch
    {
        StorageVerdict.Keep => "keep",
        StorageVerdict.Safe => "safe",
        StorageVerdict.Optional => "optional",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, null),
    };

    /// <summary>A cleanup entry as the API writes it, live when a control plane is still working on it.</summary>
    public static CleanupDto Cleanup(CatalogMaintenanceCleanup entry, MaintenanceRunner runner, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(runner);
        return new CleanupDto(
            entry.Id, entry.AreaId, entry.Trigger, entry.Actor, entry.Days, entry.OlderThanUtc, entry.StartedUtc, entry.CompletedUtc,
            entry.Removed, entry.Error, runner.IsLive(entry, nowUtc));
    }

    private static async Task<StorageRetentionDto?> RetentionAsync(
        IStorageContributor contributor, StorageArea area, IReadOnlyDictionary<string, CatalogRetentionSetting> retentions, DateTime now,
        CancellationToken ct)
    {
        if (area.Retention is not { } retention)
        {
            return null;
        }

        retentions.TryGetValue(area.Id, out var setting);
        long? reclaimable = null;
        string? error = null;
        if (setting?.Days is { } days)
        {
            try
            {
                reclaimable = await contributor.CountAsync(area, now.AddDays(-days), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                error = "Could not count what a cleanup would remove: " + SecretHygiene.RedactedMessage(ex);
            }
        }

        return new StorageRetentionDto(
            retention.Keeps, retention.Unit, retention.MinDays, retention.MaxDays, setting?.Days, setting?.UpdatedUtc, setting?.UpdatedBy,
            reclaimable, error);
    }
}
