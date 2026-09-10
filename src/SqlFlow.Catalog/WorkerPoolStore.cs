using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The desired-compute surface: reads and writes the per-pool <see cref="CatalogWorkerPoolDesired"/> rows and the
/// per-node restart request the fleet controls in the GUI drive, plus the resolved replica target the autoscaler
/// reads. Every operation is a write to (or read from) the catalog only, never a call to the orchestrator: the
/// autoscaler already queries the catalog for queue depth, so widening what it reads to include these rows is what
/// lets the control plane influence compute with no infrastructure credentials. Stateless like the other catalog
/// stores.
/// </summary>
public static class WorkerPoolStore
{
    /// <summary>Normalizes a pool reference to its stored key: the default (untargeted) pool, whose runs carry a null
    /// <c>TargetPool</c>, is keyed by the empty string, so a null/blank input and an explicit default both resolve to
    /// the one canonical row.</summary>
    public static string PoolKey(string? pool) => string.IsNullOrWhiteSpace(pool) ? string.Empty : pool.Trim();

    /// <summary>Every pool's desired state, ordered by name (the default pool, keyed by the empty string, sorts
    /// first). A pool with no row yet simply is not listed; the caller treats a missing row as the all-zero default
    /// (pure autoscaling).</summary>
    public static Task<List<CatalogWorkerPoolDesired>> ListDesiredAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.WorkerPools.AsNoTracking().OrderBy(p => p.Pool).ToListAsync(ct);
    }

    /// <summary>One pool's desired state, or null when it has never been set (the caller treats null as the all-zero
    /// default).</summary>
    public static Task<CatalogWorkerPoolDesired?> GetDesiredAsync(
        CatalogDbContext catalog, string? pool, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var key = PoolKey(pool);
        return catalog.WorkerPools.AsNoTracking().FirstOrDefaultAsync(p => p.Pool == key, ct);
    }

    /// <summary>Upserts a pool's desired state: the always-on floor and the (optionally time-bounded) manual override,
    /// stamped with who changed it and when. Idempotent by pool key; the steady-state path is a single UPDATE, with an
    /// INSERT only the first time a pool is configured. Both replica counts are clamped to non-negative; a null or
    /// past <paramref name="manualUntilUtc"/> means no override is active regardless of <paramref name="manualReplicas"/>.</summary>
    public static async Task SaveScaleAsync(
        CatalogDbContext catalog, string? pool, int minReplicas, int manualReplicas, DateTime? manualUntilUtc,
        string? updatedBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var key = PoolKey(pool);
        var min = Math.Max(0, minReplicas);
        var manual = Math.Max(0, manualReplicas);

        var updated = await catalog.WorkerPools
            .Where(p => p.Pool == key)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.MinReplicas, min)
                .SetProperty(p => p.ManualReplicas, manual)
                .SetProperty(p => p.ManualUntilUtc, manualUntilUtc)
                .SetProperty(p => p.UpdatedUtc, nowUtc)
                .SetProperty(p => p.UpdatedBy, updatedBy), ct)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            return;
        }

        catalog.WorkerPools.Add(new CatalogWorkerPoolDesired
        {
            Pool = key,
            MinReplicas = min,
            ManualReplicas = manual,
            ManualUntilUtc = manualUntilUtc,
            UpdatedUtc = nowUtc,
            UpdatedBy = updatedBy,
        });
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // A concurrent writer inserted the row between the update and this insert; re-apply the values so the
            // last writer still wins, then detach the failed add so the context stays clean.
            var entry = catalog.Entry(catalog.WorkerPools.Local.First(p => p.Pool == key));
            entry.State = EntityState.Detached;
            await catalog.WorkerPools
                .Where(p => p.Pool == key)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.MinReplicas, min)
                    .SetProperty(p => p.ManualReplicas, manual)
                    .SetProperty(p => p.ManualUntilUtc, manualUntilUtc)
                    .SetProperty(p => p.UpdatedUtc, nowUtc)
                    .SetProperty(p => p.UpdatedBy, updatedBy), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The replica target for a pool given its live queue depth and desired state: the greatest of the
    /// queued-run count, the always-on floor, and the manual override while its window is open. This is the one
    /// authoritative definition of the scaler's target, mirrored by the KEDA mssql query in the deploy manifests, so
    /// the control plane, the list view, tests, and the autoscaler all agree rather than each re-deriving it. Pure, so
    /// a caller that already holds the queue count and desired row (the fleet list) computes the target without
    /// another round trip.</summary>
    public static int ResolveTarget(int queuedRuns, CatalogWorkerPoolDesired? desired, DateTime nowUtc)
    {
        if (desired is null)
        {
            return queuedRuns;
        }

        var manual = desired.ManualUntilUtc is { } until && until > nowUtc ? desired.ManualReplicas : 0;
        return Math.Max(queuedRuns, Math.Max(desired.MinReplicas, manual));
    }

    /// <summary>The replica target the autoscaler should hold for a pool right now, resolved against the live catalog:
    /// counts the pool's queued runs (the default pool matches a null <c>TargetPool</c>), reads its desired row, and
    /// combines them via <see cref="ResolveTarget"/>.</summary>
    public static async Task<int> ResolveReplicaTargetAsync(
        CatalogDbContext catalog, string? pool, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var key = PoolKey(pool);
        var queued = await CountQueuedAsync(catalog, key, ct).ConfigureAwait(false);
        var desired = await catalog.WorkerPools.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Pool == key, ct).ConfigureAwait(false);
        return ResolveTarget(queued, desired, nowUtc);
    }

    /// <summary>Counts the runs queued for a pool key (the empty-string default pool matches a null
    /// <c>TargetPool</c>), the queue-depth term the scaler target is built from.</summary>
    public static Task<int> CountQueuedAsync(CatalogDbContext catalog, string poolKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var isDefault = poolKey.Length == 0;
        return catalog.Runs.AsNoTracking()
            .CountAsync(
                r => r.Status == RunStatuses.Queued
                    && (isDefault ? r.TargetPool == null : r.TargetPool == poolKey),
                ct);
    }

    /// <summary>Records an operator's request to restart a node by stamping <see cref="CatalogNode.RestartRequestedUtc"/>,
    /// which the worker observes on its heartbeat cadence and honors by draining and exiting. Conditional on the node
    /// existing (a heartbeated row), so a request for an unknown node is reported rather than silently creating a
    /// phantom row. Returns whether a node row was stamped.</summary>
    public static async Task<bool> RequestNodeRestartAsync(
        CatalogDbContext catalog, string nodeName, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeName);

        var stamped = await catalog.Nodes
            .Where(n => n.Name == nodeName)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.RestartRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return stamped > 0;
    }
}
