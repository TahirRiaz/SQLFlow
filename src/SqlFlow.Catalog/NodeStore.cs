using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The fleet registry: a worker upserts its heartbeat here as it polls, so the control plane and GUI can see which
/// nodes are alive. Stateless like the other catalog stores; the heartbeat is intentionally cheap (one statement in
/// the common case) and best-effort, so it never slows or breaks a worker's draining.
/// </summary>
public static class NodeStore
{
    /// <summary>Records a node's heartbeat: refreshes its last-seen (and version, and how many runs it is executing,
    /// the autoscaler's busy signal), inserting the node the first time it is heard from, and returns the node's
    /// current <see cref="CatalogNode.RestartRequestedUtc"/> so the caller can honor an operator's restart request
    /// on the same cadence it heartbeats (null when none is pending). A freshly inserted node has no pending
    /// request, so its first heartbeat always returns null. Idempotent and safe to call on every poll.</summary>
    public static async Task<DateTime?> HeartbeatAsync(
        CatalogDbContext catalog, string name, string? version, DateTime nowUtc, string? pool = null,
        int busyRuns = 0, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfNegative(busyRuns);

        // The steady-state path is a single UPDATE; only a node's very first heartbeat falls through to an insert.
        var updated = await catalog.Nodes
            .Where(n => n.Name == name)
            .ExecuteUpdateAsync(s => s
                .SetProperty(n => n.LastSeenUtc, nowUtc)
                .SetProperty(n => n.Version, version)
                .SetProperty(n => n.Pool, pool)
                .SetProperty(n => n.BusyRuns, busyRuns), ct)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            // A second cheap indexed read on the primary key returns any pending restart request the operator stamped.
            return await catalog.Nodes.AsNoTracking()
                .Where(n => n.Name == name)
                .Select(n => n.RestartRequestedUtc)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
        }

        catalog.Nodes.Add(new CatalogNode
        {
            Name = name, FirstSeenUtc = nowUtc, LastSeenUtc = nowUtc, Version = version, Pool = pool, BusyRuns = busyRuns,
        });
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Another heartbeat (a concurrent poll, or a racing node of the same name) inserted the row first; the
            // node is registered either way, so this is a no-op. Detach the failed add so the context stays clean.
            var entry = catalog.Entry(catalog.Nodes.Local.First(n => n.Name == name));
            entry.State = EntityState.Detached;
        }

        return null;
    }

    /// <summary>Counts the nodes of a pool that are currently online (heartbeated at or after
    /// <paramref name="onlineSince"/>), so the fleet view can show how many workers a pool has up against its replica
    /// target. The default pool (empty key) matches nodes whose pool is the empty string or null (the latter a node
    /// that registered before pools were recorded); a named pool matches that name exactly.</summary>
    public static Task<int> CountOnlineInPoolAsync(
        CatalogDbContext catalog, string poolKey, DateTime onlineSince, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var isDefault = poolKey.Length == 0;
        return catalog.Nodes.AsNoTracking()
            .CountAsync(
                n => n.LastSeenUtc >= onlineSince
                    && (isDefault ? (n.Pool == null || n.Pool == "") : n.Pool == poolKey),
                ct);
    }

    /// <summary>Removes a node from the fleet registry by name. The registry only ever records liveness, so a node's
    /// row carries no dependent state and deleting it is safe: it simply drops a dead entry from the fleet view. If
    /// the named node is in fact still alive, it re-registers on its very next heartbeat, so this is also harmless to
    /// call on a live node. Returns the number of rows removed (0 when no such node exists).</summary>
    public static Task<int> DeleteAsync(CatalogDbContext catalog, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return catalog.Nodes.Where(n => n.Name == name).ExecuteDeleteAsync(ct);
    }

    /// <summary>Prunes nodes whose last heartbeat predates <paramref name="olderThanUtc"/>: dead entries the fleet
    /// accumulates because every worker pod (each orchestrator revision, each autoscale-up) registers under a fresh
    /// name and the registry never removes the ones that stopped heartbeating. A node genuinely restarting comes back
    /// within seconds under a new name, so a long-stale row is always gone for good. Returns the number pruned. Safe
    /// to run on a schedule: a live node is never stale, so it is never touched.</summary>
    public static Task<int> PruneStaleAsync(CatalogDbContext catalog, DateTime olderThanUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Nodes.Where(n => n.LastSeenUtc < olderThanUtc).ExecuteDeleteAsync(ct);
    }
}
