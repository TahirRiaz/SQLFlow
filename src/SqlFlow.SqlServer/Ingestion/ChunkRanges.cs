using SqlFlow.Core;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// The shared chunk-range math ported from the legacy Functions.BatchByMonth / BatchByDay and
/// CommonDB.GetKeyRanges. One code path for every chunked feature (InitLoad backfill and Export): calendar-aligned
/// month windows, rolling fixed-width day windows, and inclusive integer-key buckets. Pure and deterministic.
/// </summary>
internal static class ChunkRanges
{
    // Calendar-aligned month chunks (snap to month-end), first/last clamped to the window; NOT rolling 30-day.
    internal static IEnumerable<(DateOnly Start, DateOnly End)> ByMonth(DateOnly start, DateOnly end, int chunkSize)
    {
        var currentStart = start;
        while (currentStart <= end)
        {
            var anchor = chunkSize > 1 ? currentStart.AddMonths(chunkSize - 1) : currentStart;
            var currentEnd = new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month));
            if (currentEnd > end)
            {
                currentEnd = end;
            }

            yield return (currentStart, currentEnd);
            currentStart = currentEnd.AddDays(1);
        }
    }

    // Rolling fixed-width day windows, last chunk clamped.
    internal static IEnumerable<(DateOnly Start, DateOnly End)> ByDay(DateOnly start, DateOnly end, int chunkSize)
    {
        if (chunkSize < 1)
        {
            throw new SqlFlowException("Day batch size must be at least 1.");
        }

        var currentStart = start;
        var currentEnd = currentStart.AddDays(chunkSize - 1);
        while (currentStart <= end)
        {
            if (currentEnd > end)
            {
                currentEnd = end;
            }

            yield return (currentStart, currentEnd);
            currentStart = currentEnd.AddDays(1);
            currentEnd = currentStart.AddDays(chunkSize - 1);
        }
    }

    // Inclusive [lo, hi] integer buckets. Empty when the bucket size is non-positive or the range is inverted
    // (the caller still appends its IS NULL segment).
    internal static IEnumerable<(int Lo, int Hi)> ByKey(int start, int end, int maxRowsPerBucket)
    {
        if (maxRowsPerBucket <= 0 || end < start)
        {
            yield break;
        }

        var currentStart = start;
        while (currentStart <= end)
        {
            var currentEnd = Math.Min(currentStart + maxRowsPerBucket - 1, end);
            yield return (currentStart, currentEnd);
            currentStart = currentEnd + 1;
        }
    }
}
