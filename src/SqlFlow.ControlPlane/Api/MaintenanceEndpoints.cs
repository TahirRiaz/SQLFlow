using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.ControlPlane.Maintenance;

namespace SqlFlow.ControlPlane.Api;

/// <summary>Days for an area: how long its data stays, or, for a retention, null to set no limit.</summary>
public sealed record StorageDaysDto(int? Days);

/// <summary>What a cleanup of an area at <see cref="Days"/> would remove now: the cut-off, and how many of what the area's
/// cleanup counts (<see cref="Unit"/>).</summary>
public sealed record CleanupPreviewDto(string AreaId, int Days, DateTime OlderThanUtc, long Count, string Unit);

/// <summary>
/// The Maintenance page: what the catalog's database holds, area by area, what may be removed and why, and the cleanups. A
/// read and a preview of a cleanup are anyone's who may operate (which is anyone signed in), and setting a retention or
/// cleaning up an admin's, because what a cleanup removes cannot be brought back. Every area, SQLFlow's own and each module's, goes
/// through the same <see cref="IStorageContributor"/> contract and the same <see cref="MaintenanceRunner"/>.
/// </summary>
public static class MaintenanceEndpoints
{
    /// <summary>The most cleanup entries one history request answers.</summary>
    private const int MaxHistory = 200;

    /// <summary>Maps the reads: the storage report and an area's cleanup history.</summary>
    public static RouteGroupBuilder MapMaintenanceReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var maintenance = group.MapGroup("/maintenance").WithTags("Maintenance");
        maintenance.MapGet("/storage", GetStorageAsync).WithName("GetStorageReport");
        maintenance.MapGet("/areas/{areaId}/cleanups", ListCleanupsAsync).WithName("ListStorageAreaCleanups");
        return group;
    }

    /// <summary>Maps the preview of a cleanup, which changes nothing.</summary>
    public static RouteGroupBuilder MapMaintenanceOperateEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGroup("/maintenance").WithTags("Maintenance")
            .MapPost("/areas/{areaId}/preview", PreviewAsync).WithName("PreviewStorageAreaCleanup");
        return group;
    }

    /// <summary>Maps the writes: an area's retention, and a cleanup now.</summary>
    public static RouteGroupBuilder MapMaintenanceAdminEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var maintenance = group.MapGroup("/maintenance").WithTags("Maintenance");
        maintenance.MapPut("/areas/{areaId}/retention", SetRetentionAsync).WithName("SetStorageAreaRetention");
        maintenance.MapPost("/areas/{areaId}/cleanup", CleanupAsync).WithName("CleanStorageArea");
        return group;
    }

    private static async Task<Ok<StorageReportDto>> GetStorageAsync(
        CatalogDbContext db, [FromServices] IEnumerable<IStorageContributor> contributors, MaintenanceRunner runner,
        IOptions<ControlPlaneOptions> options, TimeProvider clock, CancellationToken ct)
    {
        var maintenance = options.Value.Maintenance;
        var report = await StorageReport.BuildAsync(db, contributors, runner, clock, maintenance.Enabled, maintenance.PollSeconds, ct).ConfigureAwait(false);
        return TypedResults.Ok(report);
    }

    private static async Task<Results<Ok<IReadOnlyList<CleanupDto>>, ProblemHttpResult>> ListCleanupsAsync(
        string areaId, int? take, CatalogDbContext db, [FromServices] IEnumerable<IStorageContributor> contributors, MaintenanceRunner runner,
        TimeProvider clock, CancellationToken ct)
    {
        if (StorageAreas.Find(contributors, areaId) is null)
        {
            return UnknownArea(areaId);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var entries = await MaintenanceStore.ListCleanupsAsync(db, areaId, Math.Clamp(take ?? 50, 1, MaxHistory), ct).ConfigureAwait(false);
        return TypedResults.Ok<IReadOnlyList<CleanupDto>>(entries.Select(e => StorageReport.Cleanup(e, runner, now)).ToList());
    }

    private static async Task<Results<Ok<CleanupPreviewDto>, ProblemHttpResult>> PreviewAsync(
        string areaId, StorageDaysDto request, [FromServices] IEnumerable<IStorageContributor> contributors, TimeProvider clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (found, problem) = Cleanable(contributors, areaId, request.Days);
        if (found is null)
        {
            return problem!;
        }

        var days = request.Days!.Value;
        var olderThan = clock.GetUtcNow().UtcDateTime.AddDays(-days);
        var count = await found.Contributor.CountAsync(found.Area, olderThan, ct).ConfigureAwait(false);
        return TypedResults.Ok(new CleanupPreviewDto(areaId, days, olderThan, count, found.Area.Retention!.Unit));
    }

    private static async Task<Results<Ok<StorageRetentionDto>, ProblemHttpResult>> SetRetentionAsync(
        string areaId, StorageDaysDto request, CatalogDbContext db, [FromServices] IEnumerable<IStorageContributor> contributors,
        TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var found = StorageAreas.Find(contributors, areaId);
        if (found is null)
        {
            return UnknownArea(areaId);
        }

        if (found.Area.Retention is not { } retention)
        {
            return NotCleanable(found.Area);
        }

        if (request.Days is { } days && (days < retention.MinDays || days > retention.MaxDays))
        {
            return DaysOutOfRange(found.Area, retention);
        }

        var stored = await MaintenanceStore.SetRetentionAsync(db, areaId, request.Days, RequestActor.Of(user), clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(new StorageRetentionDto(
            retention.Keeps, retention.Unit, retention.MinDays, retention.MaxDays, stored.Days, stored.UpdatedUtc, stored.UpdatedBy, null, null));
    }

    private static async Task<Results<Accepted<CleanupDto>, ProblemHttpResult>> CleanupAsync(
        string areaId, StorageDaysDto request, MaintenanceRunner runner, TimeProvider clock, ClaimsPrincipal user, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Days is not { } days)
        {
            return TypedResults.Problem(
                detail: "Name the days to keep with 'days'; data older than that goes, apart from what the area always keeps.",
                statusCode: StatusCodes.Status400BadRequest, title: "No days");
        }

        var start = await runner.StartAsync(areaId, days, RequestActor.Of(user), ct).ConfigureAwait(false);
        if (start.Entry is { } entry)
        {
            return TypedResults.Accepted(
                $"/api/v1/maintenance/areas/{Uri.EscapeDataString(areaId)}/cleanups",
                StorageReport.Cleanup(entry, runner, clock.GetUtcNow().UtcDateTime));
        }

        var (status, title) = start.Refusal switch
        {
            CleanupRefusal.UnknownArea => (StatusCodes.Status404NotFound, "No such area"),
            CleanupRefusal.AlreadyRunning => (StatusCodes.Status409Conflict, "Cleanup already running"),
            CleanupRefusal.NotCleanable => (StatusCodes.Status400BadRequest, "Nothing to clean"),
            _ => (StatusCodes.Status400BadRequest, "Days out of range"),
        };
        return TypedResults.Problem(detail: start.Message, statusCode: status, title: title);
    }

    /// <summary>The area and its contributor when <paramref name="days"/> are days it takes, else the problem to answer.</summary>
    private static (FoundStorageArea? Found, ProblemHttpResult? Problem) Cleanable(
        IEnumerable<IStorageContributor> contributors, string areaId, int? days)
    {
        var found = StorageAreas.Find(contributors, areaId);
        if (found is null)
        {
            return (null, UnknownArea(areaId));
        }

        if (found.Area.Retention is not { } retention)
        {
            return (null, NotCleanable(found.Area));
        }

        if (days is null || days < retention.MinDays || days > retention.MaxDays)
        {
            return (null, DaysOutOfRange(found.Area, retention));
        }

        return (found, null);
    }

    private static ProblemHttpResult UnknownArea(string areaId)
        => TypedResults.Problem(detail: $"No storage area '{areaId}'.", statusCode: StatusCodes.Status404NotFound, title: "No such area");

    private static ProblemHttpResult NotCleanable(StorageArea area)
        => TypedResults.Problem(
            detail: $"'{area.Title}' is kept or cleans itself; nothing removes it from the Maintenance page.",
            statusCode: StatusCodes.Status400BadRequest, title: "Nothing to clean");

    private static ProblemHttpResult DaysOutOfRange(StorageArea area, StorageRetention retention)
        => TypedResults.Problem(
            detail: $"'{area.Title}' keeps between {retention.MinDays} and {retention.MaxDays} days.",
            statusCode: StatusCodes.Status400BadRequest, title: "Days out of range");
}
