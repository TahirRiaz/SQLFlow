using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>
/// The single dispatch ownership lease: exactly one control-plane replica may run the in-memory dispatcher at a
/// time, and this row decides which. Acquisition and renewal are one conditional UPDATE (free, expired, or already
/// mine), so two replicas racing for it cannot both win, and a crashed owner's lease lapses on its own after the
/// TTL. Plain SQL only: no locking hints, no provider-specific feature.
/// </summary>
public static class DispatchLeaseStore
{
    /// <summary>The one lease name the dispatcher uses; the table is keyed so a second kind of lease could coexist.</summary>
    public const string DispatchLeaseName = "dispatch";

    /// <summary>Acquires or renews the named lease for <paramref name="owner"/> until <paramref name="nowUtc"/> plus
    /// <paramref name="ttl"/>. Succeeds when no row exists yet, when the lease has expired, or when this owner already
    /// holds it (a renewal keeps the epoch; a takeover advances it). Returns whether the caller now holds it.</summary>
    public static async Task<bool> TryAcquireAsync(
        CatalogDbContext catalog, string name, string owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var expires = nowUtc + ttl;
        var updated = await catalog.DispatchLeases
            .Where(l => l.Name == name && (l.Owner == owner || l.ExpiresUtc < nowUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Owner, owner)
                .SetProperty(l => l.ExpiresUtc, expires)
                .SetProperty(l => l.Epoch, l => l.Owner == owner ? l.Epoch : l.Epoch + 1)
                .SetProperty(l => l.AcquiredUtc, l => l.Owner == owner ? l.AcquiredUtc : nowUtc), ct)
            .ConfigureAwait(false);
        if (updated > 0)
        {
            return true;
        }

        var exists = await catalog.DispatchLeases.AsNoTracking()
            .AnyAsync(l => l.Name == name, ct).ConfigureAwait(false);
        if (exists)
        {
            return false; // held by another owner and not yet expired
        }

        catalog.DispatchLeases.Add(new CatalogDispatchLease
        {
            Name = name, Owner = owner, Epoch = 1, AcquiredUtc = nowUtc, ExpiresUtc = expires,
        });
        try
        {
            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException)
        {
            // Another replica inserted the first row in the same instant; it holds the lease. Detach the failed add
            // so the context stays clean, and report the loss.
            var entry = catalog.Entry(catalog.DispatchLeases.Local.First(l => l.Name == name));
            entry.State = EntityState.Detached;
            return false;
        }
    }

    /// <summary>Releases the named lease if <paramref name="owner"/> holds it, by expiring it at once so a successor
    /// acquires it on its next attempt instead of waiting out the TTL.</summary>
    public static Task<int> ReleaseAsync(
        CatalogDbContext catalog, string name, string owner, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);

        return catalog.DispatchLeases
            .Where(l => l.Name == name && l.Owner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.ExpiresUtc, nowUtc.AddSeconds(-1)), ct);
    }

    /// <summary>The current holder of the named lease (null when never acquired).</summary>
    public static Task<CatalogDispatchLease?> GetAsync(CatalogDbContext catalog, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return catalog.DispatchLeases.AsNoTracking().FirstOrDefaultAsync(l => l.Name == name, ct);
    }
}
