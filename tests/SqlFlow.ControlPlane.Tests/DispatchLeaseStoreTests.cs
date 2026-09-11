using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The single dispatch ownership lease (<see cref="DispatchLeaseStore"/>) against the real catalog: one owner at a
/// time, renewal keeps the epoch, a takeover after expiry advances it, a release hands over at once, and two
/// replicas racing for a free lease produce exactly one winner. Each test uses its own lease name so the suite never
/// touches the row a running control plane may hold.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DispatchLeaseStoreTests
{
    [SkippableFact]
    public async Task Acquire_Renew_Expire_Takeover_Release()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = "test-" + Guid.NewGuid().ToString("N")[..8];
        var ttl = TimeSpan.FromSeconds(30);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var t0 = DateTime.UtcNow;

            // First come, first served; a renewal by the holder keeps the epoch and extends the expiry.
            Assert.True(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-a", t0, ttl));
            Assert.False(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-b", t0.AddSeconds(1), ttl));
            Assert.True(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-a", t0.AddSeconds(10), ttl));
            var held = await DispatchLeaseStore.GetAsync(db, name);
            Assert.NotNull(held);
            Assert.Equal(("replica-a", 1L, t0.AddSeconds(40)), (held.Owner, held.Epoch, held.ExpiresUtc));

            // Past the TTL the lease is free: the newcomer takes it and the epoch advances.
            Assert.True(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-b", t0.AddSeconds(41), ttl));
            held = await DispatchLeaseStore.GetAsync(db, name);
            Assert.Equal(("replica-b", 2L), (held!.Owner, held.Epoch));
            Assert.False(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-a", t0.AddSeconds(42), ttl));

            // A release by a non-holder does nothing; the holder's release hands over immediately.
            Assert.Equal(0, await DispatchLeaseStore.ReleaseAsync(db, name, "replica-a", t0.AddSeconds(43)));
            Assert.False(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-a", t0.AddSeconds(44), ttl));
            Assert.Equal(1, await DispatchLeaseStore.ReleaseAsync(db, name, "replica-b", t0.AddSeconds(45)));
            Assert.True(await DispatchLeaseStore.TryAcquireAsync(db, name, "replica-a", t0.AddSeconds(46), ttl));
            Assert.Equal(3L, (await DispatchLeaseStore.GetAsync(db, name))!.Epoch);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.DispatchLeases.Where(l => l.Name == name).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task TwoReplicasRacingForAFreeLease_ProduceExactlyOneOwner()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var name = "race-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            var results = await Task.WhenAll(
                DispatchLeaseStore.TryAcquireAsync(a, name, "replica-a", now, TimeSpan.FromSeconds(30)),
                DispatchLeaseStore.TryAcquireAsync(b, name, "replica-b", now, TimeSpan.FromSeconds(30)));

            Assert.Equal(1, results.Count(r => r));
            await using var verify = CatalogDatabase.Create(cs);
            var held = await DispatchLeaseStore.GetAsync(verify, name);
            Assert.NotNull(held);
            Assert.Equal(results[0] ? "replica-a" : "replica-b", held.Owner);
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.DispatchLeases.Where(l => l.Name == name).ExecuteDeleteAsync();
        }
    }
}
