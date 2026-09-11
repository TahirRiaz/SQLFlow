using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A compute node in the fleet: when it was first and last heard from, its build, whether it is currently
/// online (last heartbeat within the liveness window, computed at read time), and whether a restart it was asked to
/// perform is still pending (the worker honors it on its next heartbeat).</summary>
public sealed record NodeDto(
    string Name, DateTime FirstSeenUtc, DateTime LastSeenUtc, string? Version, bool Online,
    DateTime? RestartRequestedUtc);

/// <summary>One worker pool's desired compute state and its live resolution: the always-on floor, the (time-bounded)
/// manual override, how many runs are queued for the pool right now, and the resulting replica target the autoscaler
/// holds. <see cref="Pool"/> is the empty string for the default (untargeted) pool. <see cref="ManualActive"/> is
/// whether the manual override applies at read time (its window is open).</summary>
public sealed record WorkerPoolDto(
    string Pool, int MinReplicas, int ManualReplicas, DateTime? ManualUntilUtc, bool ManualActive,
    int QueuedRuns, int ReplicaTarget, int OnlineNodes, DateTime? UpdatedUtc, string? UpdatedBy);

/// <summary>The outcome of purging the fleet registry's offline nodes: how many dead entries were removed.</summary>
public sealed record NodePurgeResult(int Removed);

/// <summary>A request to set a pool's desired compute state. Every field is optional so a caller can adjust one facet
/// without disturbing the other: send <see cref="MinReplicas"/> to set/clear the always-on floor; send
/// <see cref="ManualReplicas"/> (with <see cref="ManualForMinutes"/>) to bring workers up now for a bounded window
/// (a spawn-from-zero or temporary scale-up), or <see cref="ManualReplicas"/> of 0 to end an override early. A null
/// field is left unchanged. <see cref="Pool"/> is null/empty for the default pool.</summary>
public sealed record WorkerPoolScaleRequest(
    string? Pool, int? MinReplicas, int? ManualReplicas, int? ManualForMinutes);

/// <summary>
/// The fleet surface. Reads (<c>read</c> scope): <c>GET /api/v1/nodes</c> lists the workers that have heartbeated,
/// most recently seen first, with a derived online flag; <c>GET /api/v1/nodes/pools</c> lists each pool's desired
/// state and resolved replica target. Controls (<c>operate</c> scope): <c>PUT /api/v1/nodes/pools/scale</c> sets a
/// pool's always-on floor and manual override, <c>POST /api/v1/nodes/{name}/restart</c> asks a node to restart, and
/// <c>DELETE /api/v1/nodes/{name}</c> / <c>DELETE /api/v1/nodes/offline</c> drop one dead entry or every offline one.
/// The control plane never calls the orchestrator: both controls only write catalog rows, which the worker (restart)
/// and the autoscaler (scale, which already queries the catalog) read, so influencing compute needs no
/// infrastructure credentials.
/// </summary>
public static class NodeEndpoints
{
    // A node heartbeats every poll (a few seconds); treat it as online if heard from within the last minute, which
    // tolerates a slow poll or a brief hiccup without flapping.
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    // Guards against absurd desired values (a fat-fingered replica count or an unbounded warm window); the real
    // ceiling on replicas is the autoscaler's own maxReplicaCount, and a spawn window longer than a day should be a
    // deployment change, not a click.
    private const int MaxReplicas = 100;
    private const int MaxManualMinutes = 1440;

    public static RouteGroupBuilder MapNodeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/nodes", ListNodesAsync).WithTags("Nodes").WithName("ListNodes");
        group.MapGet("/nodes/pools", ListPoolsAsync).WithTags("Nodes").WithName("ListWorkerPools");
        return group;
    }

    public static RouteGroupBuilder MapNodeControlEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPut("/nodes/pools/scale", ScalePoolAsync).WithTags("Nodes").WithName("ScaleWorkerPool");
        group.MapPost("/nodes/{name}/restart", RestartNodeAsync).WithTags("Nodes").WithName("RestartNode");
        // The literal /nodes/offline outranks the /nodes/{name} parameter route, so the bulk purge is never mistaken
        // for a single delete. Worker names are orchestrator-generated (sqlflow-worker--<revision>-<suffix>), so no
        // real node answers to "offline".
        group.MapDelete("/nodes/offline", PurgeOfflineNodesAsync).WithTags("Nodes").WithName("PurgeOfflineNodes");
        group.MapDelete("/nodes/{name}", DeleteNodeAsync).WithTags("Nodes").WithName("DeleteNode");
        return group;
    }

    private static async Task<Ok<PagedResult<NodeDto>>> ListNodesAsync(
        CatalogDbContext db, TimeProvider clock, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var staleBefore = clock.GetUtcNow().UtcDateTime - OnlineWindow;

        var ordered = db.Nodes.AsNoTracking().OrderByDescending(n => n.LastSeenUtc).ThenBy(n => n.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(n => new NodeDto(
                n.Name, n.FirstSeenUtc, n.LastSeenUtc, n.Version, n.LastSeenUtc >= staleBefore, n.RestartRequestedUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<NodeDto>(items, p, size, total));
    }

    private static async Task<Ok<IReadOnlyList<WorkerPoolDto>>> ListPoolsAsync(
        CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var onlineSince = now - OnlineWindow;

        // The pools worth showing: the default pool (always), every pool with a desired row, and every pool that has
        // in-flight (queued or running) work right now, so a pool the operator routes to appears even before it is
        // configured. Union on the normalized key so the default never doubles up.
        var desired = await WorkerPoolStore.ListDesiredAsync(db, ct).ConfigureAwait(false);
        var activePools = await db.Runs.AsNoTracking()
            .Where(r => (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running) && r.TargetPool != null)
            .Select(r => r.TargetPool!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);

        var keys = new SortedSet<string>(StringComparer.Ordinal) { string.Empty };
        foreach (var row in desired)
        {
            keys.Add(row.Pool);
        }

        foreach (var pool in activePools)
        {
            keys.Add(WorkerPoolStore.PoolKey(pool));
        }

        var byPool = desired.ToDictionary(d => d.Pool, StringComparer.Ordinal);
        var result = new List<WorkerPoolDto>(keys.Count);
        foreach (var key in keys)
        {
            var row = byPool.GetValueOrDefault(key);
            var queued = await WorkerPoolStore.CountQueuedAsync(db, key, ct).ConfigureAwait(false);
            var online = await NodeStore.CountOnlineInPoolAsync(db, key, onlineSince, ct).ConfigureAwait(false);
            var manualActive = row?.ManualUntilUtc is { } until && until > now;
            result.Add(new WorkerPoolDto(
                key,
                row?.MinReplicas ?? 0,
                row?.ManualReplicas ?? 0,
                row?.ManualUntilUtc,
                manualActive,
                queued,
                WorkerPoolStore.ResolveTarget(queued, row, now),
                online,
                row is null ? null : row.UpdatedUtc,
                row?.UpdatedBy));
        }

        return TypedResults.Ok<IReadOnlyList<WorkerPoolDto>>(result);
    }

    private static async Task<Results<Ok<WorkerPoolDto>, ProblemHttpResult>> ScalePoolAsync(
        WorkerPoolScaleRequest request, CatalogDbContext db, TimeProvider clock, ClaimsPrincipal user,
        CancellationToken ct)
    {
        if (request.MinReplicas is < 0 or > MaxReplicas || request.ManualReplicas is < 0 or > MaxReplicas)
        {
            return BadRequest($"MinReplicas and ManualReplicas must be between 0 and {MaxReplicas}.");
        }

        if (request.ManualForMinutes is < 1 or > MaxManualMinutes)
        {
            return BadRequest($"ManualForMinutes, when set, must be between 1 and {MaxManualMinutes}.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var key = WorkerPoolStore.PoolKey(request.Pool);

        // Apply the requested facets over the pool's current state, so setting the always-on floor never disturbs an
        // active manual override and vice versa (a PATCH, not a full replace).
        var current = await WorkerPoolStore.GetDesiredAsync(db, key, ct).ConfigureAwait(false);
        var min = request.MinReplicas ?? current?.MinReplicas ?? 0;

        int manualReplicas;
        DateTime? manualUntil;
        if (request.ManualReplicas is { } manual)
        {
            // A positive count with a window opens/refreshes the override; a count of 0 (or a missing window) ends it.
            manualReplicas = manual;
            manualUntil = manual > 0 && request.ManualForMinutes is { } minutes
                ? now.AddMinutes(minutes)
                : null;
        }
        else
        {
            manualReplicas = current?.ManualReplicas ?? 0;
            manualUntil = current?.ManualUntilUtc;
        }

        var updatedBy = user.FindFirst("sub")?.Value ?? user.Identity?.Name;
        await WorkerPoolStore.SaveScaleAsync(db, key, min, manualReplicas, manualUntil, updatedBy, now, ct)
            .ConfigureAwait(false);

        var queued = await WorkerPoolStore.CountQueuedAsync(db, key, ct).ConfigureAwait(false);
        var online = await NodeStore.CountOnlineInPoolAsync(db, key, now - OnlineWindow, ct).ConfigureAwait(false);
        var saved = await WorkerPoolStore.GetDesiredAsync(db, key, ct).ConfigureAwait(false);
        var manualActive = saved?.ManualUntilUtc is { } u && u > now;
        return TypedResults.Ok(new WorkerPoolDto(
            key, saved?.MinReplicas ?? min, saved?.ManualReplicas ?? manualReplicas, saved?.ManualUntilUtc,
            manualActive, queued, WorkerPoolStore.ResolveTarget(queued, saved, now), online,
            saved is null ? now : saved.UpdatedUtc, saved?.UpdatedBy));
    }

    private static async Task<Results<Ok, ProblemHttpResult>> RestartNodeAsync(
        string name, CatalogDbContext db, TimeProvider clock, SqlFlow.Dispatch.Dispatcher dispatcher, CancellationToken ct)
    {
        var found = await WorkerPoolStore.RequestNodeRestartAsync(db, name, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        if (!found)
        {
            return TypedResults.Problem(
                detail: $"No node named '{name}' has heartbeated into the fleet.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // The request itself reaches the dispatcher on its next node flush (which reads it back from the row);
        // waking the node's parked poll now means it hears it on that flush rather than a long-poll later.
        dispatcher.NotifyNodeRestartRequested(name);
        return TypedResults.Ok();
    }

    private static async Task<Results<Ok, ProblemHttpResult>> DeleteNodeAsync(
        string name, CatalogDbContext db, CancellationToken ct)
    {
        var removed = await NodeStore.DeleteAsync(db, name, ct).ConfigureAwait(false);
        if (removed == 0)
        {
            return TypedResults.Problem(
                detail: $"No node named '{name}' is in the fleet registry.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        return TypedResults.Ok();
    }

    private static async Task<Ok<NodePurgeResult>> PurgeOfflineNodesAsync(
        CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        // "Offline" here is exactly what the list view shows: last heartbeat older than the liveness window. Every
        // worker pod registers under a fresh name, so these rows are dead for good; a node that is somehow still
        // alive re-registers on its next heartbeat. This is the on-demand form of the reaper's scheduled prune.
        var removed = await NodeStore
            .PruneStaleAsync(db, clock.GetUtcNow().UtcDateTime - OnlineWindow, ct)
            .ConfigureAwait(false);
        return TypedResults.Ok(new NodePurgeResult(removed));
    }

    private static ProblemHttpResult BadRequest(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
}
