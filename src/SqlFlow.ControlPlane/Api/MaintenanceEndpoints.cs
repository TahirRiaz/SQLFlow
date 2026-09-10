using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>How much per-run trace is stored, split by kind, plus how many SQL-statement rows the retention policy
/// would reclaim and the retention window in effect. Events are reported for context; they are never pruned.
/// <see cref="RetentionDays"/> is null when SQL statements are kept forever.</summary>
public sealed record RunTraceStorageDto(
    long TotalStatements,
    long TotalEvents,
    long PrunableStatements,
    int PrunableRuns,
    int? RetentionDays);

/// <summary>A request to change the SQL-statement retention: how many days a superseded successful run keeps its
/// statements, or null to keep them forever (age-based pruning off).</summary>
public sealed record RunTraceRetentionUpdateDto(int? RetentionDays);

/// <summary>The stored SQL-statement retention after an update; null means keep forever.</summary>
public sealed record RunTraceRetentionDto(int? RetentionDays);

/// <summary>The outcome of a manual SQL-statement purge: how many statement rows it deleted.</summary>
public sealed record RunStatementPurgeResultDto(int StatementsDeleted);

/// <summary>The outcome of a manual run-event purge: how many event rows it deleted.</summary>
public sealed record RunEventPurgeResultDto(int EventsDeleted);

/// <summary>
/// Housekeeping the operator can see and drive from the GUI. Today that is the per-run trace, split by kind: the
/// generated <b>SQL statements</b> (heavy and near-identical every run, so purgeable) and the <b>run events</b> (the
/// per-run rows-affected and timing, kept for history and analytics, never pruned). A read reports how much of each
/// is stored and how many statement rows are reclaimable; a write tunes the statement retention (days, or forever);
/// and a purge deletes all SQL statements now and returns the count (so the GUI shows the real outcome and any
/// database error surfaces instead of being swallowed). The retention prune also runs automatically in
/// <c>RunTraceReaper</c>.
/// </summary>
public static class MaintenanceEndpoints
{
    // A generous ceiling on the retention window: enough for any real policy, low enough that TimeSpan.FromDays
    // never overflows and a fat-fingered value cannot silently mean "never prune".
    private const int MaxRetentionDays = 36500;

    public static RouteGroupBuilder MapMaintenanceEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var maintenance = group.MapGroup("/maintenance").WithTags("Maintenance");
        maintenance.MapGet("/trace-storage", GetTraceStorageAsync).WithName("GetRunTraceStorage");
        maintenance.MapPut("/statement-retention", SetTraceRetentionAsync).WithName("SetRunStatementRetention");
        maintenance.MapPost("/statements/purge", PurgeStatementsAsync).WithName("PurgeRunStatements");
        maintenance.MapPost("/events/purge", PurgeEventsAsync).WithName("PurgeRunEvents");
        return group;
    }

    private static async Task<Ok<RunTraceStorageDto>> GetTraceStorageAsync(
        CatalogDbContext db,
        TimeProvider clock,
        CancellationToken ct)
    {
        var retentionDays = await MaintenanceStore.GetRunTraceRetentionDaysAsync(db, ct).ConfigureAwait(false);
        DateTime? supersededBefore = retentionDays is { } days
            ? clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(days)
            : null;
        var summary = await RunTraceStore.SummarizeAsync(db, supersededBefore, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunTraceStorageDto(
            summary.TotalStatements,
            summary.TotalEvents,
            summary.PrunableStatements,
            summary.PrunableRuns,
            retentionDays));
    }

    private static async Task<Results<Ok<RunTraceRetentionDto>, ValidationProblem>> SetTraceRetentionAsync(
        RunTraceRetentionUpdateDto request,
        CatalogDbContext db,
        TimeProvider clock,
        ClaimsPrincipal user,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RetentionDays is { } days && (days < 0 || days > MaxRetentionDays))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(RunTraceRetentionUpdateDto.RetentionDays)] =
                    [$"RetentionDays must be null (keep forever) or between 0 and {MaxRetentionDays}."],
            });
        }

        var stored = await MaintenanceStore.SetRunTraceRetentionDaysAsync(
            db, request.RetentionDays, user.Identity?.Name, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunTraceRetentionDto(stored));
    }

    private static async Task<Ok<RunStatementPurgeResultDto>> PurgeStatementsAsync(
        CatalogDbContext db,
        CancellationToken ct)
    {
        // Deletes every SQL-statement row here, on the request's own catalog scope (not on any pipeline's path), and
        // returns the count. Run events and run headers are untouched. A database error propagates to the global
        // handler as a problem response, so the GUI sees the real failure instead of a false "done".
        var deleted = await RunTraceStore.PurgeAllStatementsAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunStatementPurgeResultDto(deleted));
    }

    private static async Task<Ok<RunEventPurgeResultDto>> PurgeEventsAsync(
        CatalogDbContext db,
        CancellationToken ct)
    {
        // Deletes every run-event row here, on the request's own catalog scope. Run headers and SQL statements are
        // untouched. A database error propagates as a problem response, so the GUI sees a real failure, not a false
        // "done". Events are never pruned automatically; this manual purge is the only path that removes them.
        var deleted = await RunTraceStore.PurgeAllEventsAsync(db, ct).ConfigureAwait(false);
        return TypedResults.Ok(new RunEventPurgeResultDto(deleted));
    }
}
