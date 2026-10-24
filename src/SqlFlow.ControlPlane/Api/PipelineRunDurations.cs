using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One run of a pipeline as its duration profile names it: which run, how it ended, and its timing.</summary>
/// <param name="RunId">The run.</param>
/// <param name="Status">Its lifecycle state (<see cref="RunStatuses"/>).</param>
/// <param name="StartUtc">When it started.</param>
/// <param name="EndUtc">When it ended; null for the run still executing.</param>
/// <param name="DurationSeconds">How long it took; null for the run still executing, whose elapsed time is the time
/// since <paramref name="StartUtc"/>.</param>
public sealed record PipelineRunTimingDto(
    Guid RunId, string Status, DateTime? StartUtc, DateTime? EndUtc, double? DurationSeconds);

/// <summary>
/// How long a pipeline's runs take: the typical figures over its newest succeeded runs, the newest run that ran to an
/// end, and the run executing now. The typical figures read succeeded runs only, because a failed run's duration says
/// when it failed, not how long the work takes. A run whose work was spread over members of its own pipeline (a fan-out)
/// is read through its root: the root's duration is the whole run's, and each member's is only its part.
/// </summary>
/// <param name="Runs">How many succeeded runs the typical figures are over: the newest
/// <see cref="PipelineRunDurations.RecentRuns"/> that carry a duration, fewer when the pipeline has fewer. Zero leaves
/// every figure null.</param>
/// <param name="MedianSeconds">The median of those durations: the honest "usual" when a few outliers drag the mean.</param>
/// <param name="AverageSeconds">Their mean.</param>
/// <param name="FastestSeconds">The shortest of them.</param>
/// <param name="SlowestSeconds">The longest of them.</param>
/// <param name="Last">The newest run that started and ended, whatever its outcome; null when none has.</param>
/// <param name="Running">The run executing now (the root, when its work is spread over members); null when none is.</param>
public sealed record PipelineRunDurationsDto(
    int Runs, double? MedianSeconds, double? AverageSeconds, double? FastestSeconds, double? SlowestSeconds,
    PipelineRunTimingDto? Last, PipelineRunTimingDto? Running);

/// <summary>
/// The duration profile of a pipeline, computed from its recorded run history rather than stored, so it reflects every
/// run the catalog holds. Each read is a seek on the run table's (pipeline, written) order, bounded by
/// <see cref="RecentRuns"/>, so the cost does not grow with the pipeline's history.
/// </summary>
public static class PipelineRunDurations
{
    /// <summary>How many of the newest succeeded runs the typical figures cover: enough to say what a run usually
    /// takes, few enough that a long-past regime (before the source grew, before a fix) does not drown out today's.</summary>
    public const int RecentRuns = 50;

    /// <summary>The profile of a pipeline from the database.</summary>
    public static async Task<PipelineRunDurationsDto> ComputeAsync(
        CatalogDbContext db, Guid pipelineId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        // A fan-out member is a part of its root's run, so only runs that are not members are profiled.
        var runs = db.Runs.AsNoTracking().Where(r => r.PipelineId == pipelineId && r.FanOutRoot == null);
        var recent = await runs
            .Where(r => r.Status == RunStatuses.Succeeded && r.DurationSeconds != null)
            .OrderByDescending(r => r.WrittenUtc).ThenByDescending(r => r.RunId)
            .Take(RecentRuns)
            .Select(r => r.DurationSeconds!.Value)
            .ToListAsync(ct).ConfigureAwait(false);

        var last = await runs
            .Where(r => r.StartUtc != null && r.EndUtc != null)
            .OrderByDescending(r => r.WrittenUtc).ThenByDescending(r => r.RunId)
            .Select(r => new PipelineRunTimingDto(r.RunId, r.Status, r.StartUtc, r.EndUtc, r.DurationSeconds))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var running = await runs
            .Where(r => r.Status == RunStatuses.Running)
            .OrderBy(r => r.StartUtc)
            .Select(r => new PipelineRunTimingDto(r.RunId, r.Status, r.StartUtc, null, null))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        return Profile(recent, last, running);
    }

    /// <summary>The profile from durations already read: the median, mean and extremes of <paramref name="durations"/>,
    /// with the newest and the running run as given.</summary>
    public static PipelineRunDurationsDto Profile(
        IReadOnlyList<double> durations, PipelineRunTimingDto? last, PipelineRunTimingDto? running)
    {
        ArgumentNullException.ThrowIfNull(durations);
        if (durations.Count == 0)
        {
            return new PipelineRunDurationsDto(0, null, null, null, null, last, running);
        }

        var sorted = durations.Order().ToList();
        var middle = sorted.Count / 2;
        var median = sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
        return new PipelineRunDurationsDto(
            sorted.Count, median, sorted.Average(), sorted[0], sorted[^1], last, running);
    }
}
