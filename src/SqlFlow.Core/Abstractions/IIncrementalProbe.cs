using SqlFlow.Core.Model;

namespace SqlFlow.Core.Abstractions;

/// <summary>
/// Probes a target table for the incremental high-water mark, optionally shifted back by an overlap window.
/// The engine injects the result into the source read so only data past the watermark is ingested. The target
/// table is the state, so no control database is required.
/// </summary>
public interface IIncrementalProbe
{
    /// <summary>
    /// Returns <c>MAX(dateColumn) - overlapDays</c> from <paramref name="table"/>, or null when the
    /// table does not exist or holds no rows (so the caller does a full load). The column's type is
    /// resolved at runtime to build a valid query.
    /// </summary>
    Task<DateTimeOffset?> GetWatermarkAsync(string connectionString, string table, string dateColumn, int overlapDays, CancellationToken ct = default);

    /// <summary>
    /// Returns the typed row-level high-water mark: <c>MAX(column)</c> from <paramref name="table"/>, shifted
    /// back by <paramref name="overlap"/> (days for a date column, raw units for a numeric column, ignored for
    /// text). Null when the table does not exist or holds no rows (so the caller does a full load). Throws when
    /// the column is missing or its type is not orderable as a watermark.
    /// </summary>
    Task<WatermarkValue?> GetRowWatermarkAsync(string connectionString, string table, string column, double overlap, CancellationToken ct = default);
}
