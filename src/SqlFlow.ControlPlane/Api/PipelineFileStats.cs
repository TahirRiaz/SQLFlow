using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// One distinct file a pipeline has processed, reduced to the facts the size profile is computed from. A file
/// re-pulled by several runs contributes a single row (its newest recorded values), so a re-run never re-weights
/// the profile.
/// </summary>
/// <remarks>A member-initialised class rather than a positional record on purpose: EF Core composes further
/// queries (ordering, paging, a second aggregate) over a member-init projection, but not over one built through a
/// constructor, and every query here composes over this shape.</remarks>
public sealed class PipelineFileFacts
{
    /// <summary>The file's size in bytes.</summary>
    public long SizeBytes { get; init; }

    /// <summary>The row count the flow read from (or wrote to) the file.</summary>
    public long Rows { get; init; }

    /// <summary>The source file's last-modified timestamp, or null when the store reported none.</summary>
    public DateTimeOffset? Modified { get; init; }
}

/// <summary>
/// The database-side aggregate over a pipeline's distinct files: the sums and extremes a single pass can produce.
/// <see cref="SumOfSquares"/> carries sum(size^2) so the standard deviation falls out without a second pass.
/// </summary>
/// <param name="FileCount">How many distinct files the pipeline has processed.</param>
/// <param name="TotalBytes">The summed size of those files.</param>
/// <param name="MinBytes">The smallest file.</param>
/// <param name="MaxBytes">The largest file.</param>
/// <param name="SumOfSquares">Sum of size^2, in bytes squared, for the standard deviation.</param>
/// <param name="TotalRows">The summed row count of those files.</param>
/// <param name="OldestModified">The oldest last-modified timestamp seen, or null when no file carries one.</param>
/// <param name="NewestModified">The newest last-modified timestamp seen, or null when no file carries one.</param>
public sealed record PipelineFileAggregate(
    long FileCount, long TotalBytes, long MinBytes, long MaxBytes, double SumOfSquares, long TotalRows,
    DateTimeOffset? OldestModified, DateTimeOffset? NewestModified);

/// <summary>
/// The file-size profile of a pipeline: "what does a delivery from this flow normally look like", computed from
/// the recorded run history rather than stored, so it always reflects every run the catalog holds. The universe is
/// the same one the pipeline files view browses (every file across every run, deduplicated by name + path), which
/// keeps the profile and the file list telling the same story.
/// </summary>
public static class PipelineFileStats
{
    /// <summary>How many of the newest files (by last-modified) the recent window covers: enough to characterise
    /// the current delivery shape, short enough that a long-past regime does not drown it out.</summary>
    public const int RecentWindow = 30;

    /// <summary>The profile of a pipeline that has processed no files: all zero, with no windows or timestamps.
    /// Distinct from a 404, which means the pipeline itself does not exist.</summary>
    public static PipelineFileStatsDto Empty { get; } =
        new(0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null);

    /// <summary>
    /// One row per distinct file (name + path) the pipeline has processed across its whole run history, carrying
    /// the newest recorded size, rows, and modified timestamp. File-constant attributes are stable across re-pulls,
    /// so the per-group maximum is the file's own value.
    /// </summary>
    public static IQueryable<PipelineFileFacts> DistinctFiles(CatalogDbContext db, Guid pipelineId)
    {
        ArgumentNullException.ThrowIfNull(db);

        return from f in db.RunFiles.AsNoTracking()
               join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
               where r.PipelineId == pipelineId
               group f by new { f.Name, f.Path } into g
               select new PipelineFileFacts
               {
                   SizeBytes = g.Max(x => x.SizeBytes),
                   Rows = g.Max(x => x.Rows),
                   Modified = g.Max(x => x.Modified),
               };
    }

    /// <summary>
    /// The single-pass aggregate over <see cref="DistinctFiles"/>. Yields no row for a pipeline that has processed
    /// no files (an empty group produces nothing), which the caller reads as <see cref="Empty"/>.
    /// </summary>
    public static IQueryable<PipelineFileAggregate> Aggregate(CatalogDbContext db, Guid pipelineId)
        => DistinctFiles(db, pipelineId)
            .GroupBy(_ => 1)
            .Select(g => new PipelineFileAggregate(
                g.LongCount(),
                g.Sum(x => x.SizeBytes),
                g.Min(x => x.SizeBytes),
                g.Max(x => x.SizeBytes),
                g.Sum(x => (double)x.SizeBytes * x.SizeBytes),
                g.Sum(x => x.Rows),
                g.Min(x => x.Modified),
                g.Max(x => x.Modified)));

    /// <summary>
    /// The one (odd count) or two (even count) middle sizes of the size-ordered file set: the median, which is the
    /// honest "normal file size" when a handful of outsized deliveries drag the mean away from the typical file.
    /// </summary>
    public static IQueryable<long> MiddleSizes(CatalogDbContext db, Guid pipelineId, long fileCount)
        => DistinctFiles(db, pipelineId)
            .Select(x => x.SizeBytes)
            .OrderBy(size => size)
            .Skip((int)((fileCount - 1) / 2))
            .Take(fileCount % 2 == 0 ? 2 : 1);

    /// <summary>The newest <see cref="RecentWindow"/> files by last-modified: the current delivery regime, which a
    /// caller compares against the all-time profile to tell "this is normal" from "this has changed".</summary>
    public static IQueryable<PipelineFileFacts> RecentFiles(CatalogDbContext db, Guid pipelineId)
        => DistinctFiles(db, pipelineId)
            .OrderByDescending(x => x.Modified)
            .ThenByDescending(x => x.SizeBytes)
            .Take(RecentWindow);

    /// <summary>
    /// Computes the pipeline's whole file-size profile: the all-time aggregate, the median, and the recent window.
    /// A pipeline that has processed no files returns <see cref="Empty"/>.
    /// </summary>
    public static async Task<PipelineFileStatsDto> ComputeAsync(
        CatalogDbContext db, Guid pipelineId, CancellationToken ct)
    {
        var aggregate = await Aggregate(db, pipelineId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (aggregate is null || aggregate.FileCount == 0)
        {
            return Empty;
        }

        var middle = await MiddleSizes(db, pipelineId, aggregate.FileCount)
            .ToListAsync(ct).ConfigureAwait(false);
        var recent = await RecentFiles(db, pipelineId).ToListAsync(ct).ConfigureAwait(false);

        var mean = (double)aggregate.TotalBytes / aggregate.FileCount;
        // Population variance from the sums; clamped at zero because floating-point cancellation can push a set of
        // identically sized files a hair below it.
        var variance = Math.Max(0, (aggregate.SumOfSquares / aggregate.FileCount) - (mean * mean));

        return new PipelineFileStatsDto(
            aggregate.FileCount,
            aggregate.TotalBytes,
            Round(mean),
            middle.Count == 0 ? 0 : Round(middle.Average()),
            aggregate.MinBytes,
            aggregate.MaxBytes,
            Round(Math.Sqrt(variance)),
            aggregate.TotalRows,
            Round((double)aggregate.TotalRows / aggregate.FileCount),
            aggregate.OldestModified,
            aggregate.NewestModified,
            recent.Count == 0
                ? null
                : new PipelineRecentFilesDto(
                    recent.Count,
                    Round(recent.Average(x => (double)x.SizeBytes)),
                    recent.Min(x => x.SizeBytes),
                    recent.Max(x => x.SizeBytes),
                    recent.Min(x => x.Modified),
                    recent.Max(x => x.Modified)));
    }

    private static long Round(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);
}
