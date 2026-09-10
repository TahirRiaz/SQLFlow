using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Run counts by lifecycle state, plus how many ran in the last 24 hours.</summary>
public sealed record RunCountsDto(long Queued, long Running, long Succeeded, long Failed, long Cancelled, long Last24h);

/// <summary>The control-plane dashboard rollup a GUI landing page shows: estate size, the run queue's live state,
/// fleet liveness, scheduling, and managed-sync health, computed at read time.</summary>
public sealed record DashboardDto(
    long Repos, long Pipelines, long ActivePipelines, RunCountsDto Runs,
    long NodesOnline, long NodesTotal, long SchedulesEnabled, long SchedulesPaused,
    long RepoSources, long RepoSourcesWithErrors, DateTime AsOfUtc);

/// <summary>
/// The dashboard read surface: <c>GET /api/v1/summary</c> aggregates the catalog into the one-call overview a GUI
/// landing page needs, so the front end does not fan out a dozen list calls just to render headline numbers. Read
/// scope; every figure is computed at read time (always current) in exactly two database round trips: one grouped
/// scan of the run table for the lifecycle counts, and one labelled-count union for every other headline number.
/// </summary>
public static class SummaryEndpoints
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    public static RouteGroupBuilder MapSummaryEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/summary", GetSummaryAsync).WithTags("Summary").WithName("GetSummary");
        return group;
    }

    private static async Task<Ok<DashboardDto>> GetSummaryAsync(CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var dayAgo = now.AddDays(-1);
        var onlineSince = now - OnlineWindow;

        // Round trip 1: every lifecycle count from ONE grouped pass over the run table (the largest table on
        // this page), instead of a COUNT query per status. The server groups under its (case-insensitive)
        // collation, so the lookup is case-insensitive too, exactly like the equality COUNTs it replaces.
        var runsByStatus = await db.Runs
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        // Round trip 2: every other headline number in ONE statement. Each table contributes rows tagged with a
        // label (its filter applied per branch) and a single GROUP BY counts them, so the dashboard costs two
        // round trips no matter how many tiles it shows. A label with no rows is simply absent (count 0).
        var counted = await db.Repos.Select(r => "repos")
            .Concat(db.Pipelines.Select(p => "pipelines"))
            .Concat(db.Pipelines.Where(p => p.Active).Select(p => "activePipelines"))
            .Concat(db.Runs.Where(r => r.WrittenUtc >= dayAgo).Select(r => "runsLast24h"))
            .Concat(db.Nodes.Where(n => n.LastSeenUtc >= onlineSince).Select(n => "nodesOnline"))
            .Concat(db.Nodes.Select(n => "nodesTotal"))
            .Concat(db.Schedules.Where(s => s.Enabled && !s.Paused).Select(s => "schedulesEnabled"))
            .Concat(db.Schedules.Where(s => s.Paused).Select(s => "schedulesPaused"))
            .Concat(db.RepoSources.Select(s => "repoSources"))
            .Concat(db.RepoSources.Where(s => s.LastError != null).Select(s => "repoSourcesWithErrors"))
            .GroupBy(label => label)
            .Select(g => new { Label = g.Key, Count = g.LongCount() })
            .ToDictionaryAsync(x => x.Label, x => x.Count, ct).ConfigureAwait(false);

        var runs = new RunCountsDto(
            Queued: CountOf(runsByStatus, RunStatuses.Queued),
            Running: CountOf(runsByStatus, RunStatuses.Running),
            Succeeded: CountOf(runsByStatus, RunStatuses.Succeeded),
            Failed: CountOf(runsByStatus, RunStatuses.Failed),
            Cancelled: CountOf(runsByStatus, RunStatuses.Cancelled),
            Last24h: CountOf(counted, "runsLast24h"));

        var dashboard = new DashboardDto(
            Repos: CountOf(counted, "repos"),
            Pipelines: CountOf(counted, "pipelines"),
            ActivePipelines: CountOf(counted, "activePipelines"),
            Runs: runs,
            NodesOnline: CountOf(counted, "nodesOnline"),
            NodesTotal: CountOf(counted, "nodesTotal"),
            SchedulesEnabled: CountOf(counted, "schedulesEnabled"),
            SchedulesPaused: CountOf(counted, "schedulesPaused"),
            RepoSources: CountOf(counted, "repoSources"),
            RepoSourcesWithErrors: CountOf(counted, "repoSourcesWithErrors"),
            AsOfUtc: now);

        return TypedResults.Ok(dashboard);
    }

    private static long CountOf(IReadOnlyDictionary<string, long> counts, string key)
        => counts.TryGetValue(key, out var count) ? count : 0;
}
