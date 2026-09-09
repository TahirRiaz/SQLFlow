using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Schedule CHAINING against the real catalog: a shadow schedule (<c>after: parent</c>) carries no cadence and is
/// driven by the parent's completed fire instead of the clock. These cover the readiness rule (parent fired, none of
/// its runs still queued or running, and this child has not already consumed that fire), the idempotence stamp that
/// makes one parent fire trigger a child exactly once however many nodes observe it, and the fact that a chained
/// schedule never appears in the clock-driven due scan. The assembly runs serially (see AssemblyInfo); each test
/// removes its own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleChainingTests
{
    [SkippableFact]
    public async Task ChainedChild_IsReady_OnlyAfterParentsRunsAreTerminal()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var parentFire = new DateTime(2026, 8, 2, 20, 10, 0, DateTimeKind.Utc);
        var groupId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "p", parentFire, groupId));
                seed.Schedules.Add(Child(repoId, "c", "p"));
                // The parent's fire is still in flight.
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Running));
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.Name == "c");
            }

            // Finish the parent's last member: the whole fire is now terminal.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await db.Runs.Where(r => r.RepoId == repoId && r.Status == RunStatuses.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RunStatuses.Succeeded));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.Contains(ready, s => s.Name == "c");
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ChainedChild_FiresOncePerParentFire_AndOnlyOneNodeWins()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var parentFire = new DateTime(2026, 8, 2, 20, 10, 0, DateTimeKind.Utc);
        var groupId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "p", parentFire, groupId));
                seed.Schedules.Add(Child(repoId, "c", "p"));
                seed.Runs.Add(Run(repoId, groupId, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            var childId = CatalogIdentity.YamlSchedule(repoId, "c");

            // Two control-plane nodes see the same completed parent; exactly one may consume it.
            await using (var a = CatalogDatabase.Create(cs))
            await using (var b = CatalogDatabase.Create(cs))
            {
                var results = await Task.WhenAll(
                    ScheduleStore.TryClaimChainedFireAsync(a, childId, null, parentFire, DateTime.UtcNow),
                    ScheduleStore.TryClaimChainedFireAsync(b, childId, null, parentFire, DateTime.UtcNow));
                Assert.Equal(1, results.Count(won => won));
            }

            // Having consumed that fire, the child is no longer ready: the parent must fire again first.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var stamped = await db.Schedules.AsNoTracking().SingleAsync(s => s.Id == childId);
                Assert.Equal(parentFire, stamped.LastParentFireUtc);

                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.Name == "c");
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ChainedChild_IsNeverInTheClockDueScan()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Child(repoId, "c", "p"));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                // Far-future "now" would sweep up anything with a next fire; a chained schedule has none.
                var due = await ScheduleStore.ListDueAsync(db, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), 50);
                Assert.DoesNotContain(due, s => s.RepoId == repoId);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task YamlUpsert_MakingAScheduleChained_ClearsItsPendingClockFire()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var nextFire = now.AddHours(1);

        try
        {
            // First synced as a clock schedule...
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, "c", ["f"], "0 6 * * *", null, "UTC", true, false, 4, nextFire, now);
            }

            // ...then git changes it to chain behind a parent.
            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.UpsertYamlScheduleAsync(
                    db, repoId, "c", ["f"], null, null, "UTC", true, false, 4, nextFire, now,
                    definition: null, afterSchedules: ["p"]);
            }

            await using (var verify = CatalogDatabase.Create(cs))
            {
                var row = await verify.Schedules.AsNoTracking()
                    .SingleAsync(s => s.Id == CatalogIdentity.YamlSchedule(repoId, "c"));
                var parents = await verify.ScheduleParents.AsNoTracking()
                    .Where(p => p.ScheduleId == row.Id).Select(p => p.ParentName).ToListAsync();
                Assert.Equal(["p"], parents);
                Assert.Null(row.Cron);
                // The pending clock occurrence must be retired, or it would fire on the clock AND behind the parent.
                Assert.Null(row.NextFireUtc);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static CatalogSchedule Parent(Guid repoId, string name, DateTime lastFireUtc, Guid groupId) => new()
    {
        Id = CatalogIdentity.YamlSchedule(repoId, name),
        RepoId = repoId,
        Name = name,
        Cron = "10 20 * * *",
        Timezone = "UTC",
        Enabled = true,
        Source = "yaml",
        NextFireUtc = lastFireUtc.AddDays(1),
        LastFireUtc = lastFireUtc,
        LastGroupId = groupId,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    [SkippableFact]
    public async Task FanInChild_WaitsForEveryParent_NotJustTheFirstToFire()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var aFire = new DateTime(2026, 8, 5, 5, 0, 0, DateTimeKind.Utc);
        var bFire = new DateTime(2026, 8, 5, 5, 30, 0, DateTimeKind.Utc);
        var aGroup = Guid.NewGuid();
        var bGroup = Guid.NewGuid();

        try
        {
            // Only 'a' has fired. This is the case a single-parent chain gets wrong: it would fire the child now and
            // rebuild from whatever 'b' happened to hold.
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "a", aFire, aGroup));
                var b = Parent(repoId, "b", bFire, bGroup);
                b.LastFireUtc = null;
                b.LastGroupId = null;
                seed.Schedules.Add(b);
                seed.Schedules.Add(Child(repoId, "c", "a", "b"));
                seed.Runs.Add(Run(repoId, aGroup, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.RepoId == repoId);
            }

            // Now 'b' fires and completes too.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var b = await db.Schedules.SingleAsync(s => s.RepoId == repoId && s.Name == "b");
                b.LastFireUtc = bFire;
                b.LastGroupId = bGroup;
                db.Runs.Add(Run(repoId, bGroup, RunStatuses.Succeeded));
                await db.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.Contains(ready, s => s.RepoId == repoId && s.Name == "c");

                // The stamp is the NEWEST parent fire, so the next tick sees this set as consumed rather than new.
                var child = ready.Single(s => s.RepoId == repoId && s.Name == "c");
                var (newest, stale, label) = await ScheduleStore.GetParentFireStateAsync(db, child.Id, 24, bFire);
                Assert.Equal(bFire, newest);
                Assert.Null(stale);
                Assert.Equal("a, b", label);

                Assert.True(await ScheduleStore.TryClaimChainedFireAsync(db, child.Id, null, newest!.Value, bFire, stale));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                Assert.DoesNotContain(ready, s => s.RepoId == repoId);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task FanInChild_ReportsStaleParents_ButStillFires()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var now = new DateTime(2026, 8, 6, 5, 0, 0, DateTimeKind.Utc);
        var freshFire = now.AddMinutes(-10);
        var staleFire = now.AddDays(-9);   // well outside the 24h window
        var freshGroup = Guid.NewGuid();
        var staleGroup = Guid.NewGuid();

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "fresh", freshFire, freshGroup));
                seed.Schedules.Add(Parent(repoId, "quiet", staleFire, staleGroup));
                seed.Schedules.Add(Child(repoId, "c", "fresh", "quiet"));
                seed.Runs.Add(Run(repoId, freshGroup, RunStatuses.Succeeded));
                seed.Runs.Add(Run(repoId, staleGroup, RunStatuses.Succeeded));
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                // Stale does NOT hold the fire back: readiness only asks that every parent completed a fire.
                var ready = await ScheduleStore.ListChainedReadyAsync(db, 10);
                var child = Assert.Single(ready, s => s.RepoId == repoId && s.Name == "c");

                var (newest, stale, _) = await ScheduleStore.GetParentFireStateAsync(db, child.Id, 24, now);
                Assert.Equal(freshFire, newest);
                Assert.Equal("quiet", stale);

                Assert.True(await ScheduleStore.TryClaimChainedFireAsync(db, child.Id, null, newest!.Value, now, stale));
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var row = await db.Schedules.AsNoTracking()
                    .SingleAsync(s => s.RepoId == repoId && s.Name == "c");
                Assert.Equal("quiet", row.LastStaleParents);
                Assert.Equal(freshFire, row.LastParentFireUtc);
            }

            // A window of 0 opts out of the check entirely.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var child = await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId && s.Name == "c");
                var (_, stale, _) = await ScheduleStore.GetParentFireStateAsync(db, child.Id, 0, now);
                Assert.Null(stale);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task FanInChild_IsNotSuppressed_WhenOneParentSkipsItsChain()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repoId = Guid.NewGuid();
        var fire = new DateTime(2026, 8, 5, 5, 0, 0, DateTimeKind.Utc);

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(Parent(repoId, "a", fire, Guid.NewGuid()));
                seed.Schedules.Add(Parent(repoId, "b", fire, Guid.NewGuid()));
                seed.Schedules.Add(Child(repoId, "solo", "a"));       // single parent: suppressible
                seed.Schedules.Add(Child(repoId, "fan", "a", "b"));   // fan-in: must be left alone
                await seed.SaveChangesAsync();
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                await ScheduleStore.SuppressChainAsync(db, repoId, "a", fire);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var solo = await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId && s.Name == "solo");
                var fan = await db.Schedules.AsNoTracking().SingleAsync(s => s.RepoId == repoId && s.Name == "fan");

                // The sole-parent child is stamped, so it will not react to a fire that was asked to stay put.
                Assert.Equal(fire, solo.LastParentFireUtc);
                // The fan-in child is not: stamping it on one parent's account would mark the whole set consumed and
                // skip the fire it is actually waiting for, rather than deferring it.
                Assert.Null(fan.LastParentFireUtc);
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static CatalogSchedule Child(Guid repoId, string name, params string[] after) => new()
    {
        Id = CatalogIdentity.YamlSchedule(repoId, name),
        RepoId = repoId,
        Name = name,
        Parents = [.. after.Select((p, i) => new CatalogScheduleParent
        {
            ScheduleId = CatalogIdentity.YamlSchedule(repoId, name),
            RepoId = repoId,
            ParentName = p,
            Ordinal = i,
        })],
        Timezone = "UTC",
        Enabled = true,
        Source = "yaml",
        NextFireUtc = null,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static CatalogRun Run(Guid repoId, Guid groupId, string status) => new()
    {
        RunId = Guid.NewGuid(),
        PipelineId = Guid.NewGuid(),
        RepoId = repoId,
        FlowName = "member",
        FlowKind = "ing",
        GroupId = groupId,
        Status = status,
        EnqueuedUtc = DateTime.UtcNow,
        WrittenUtc = DateTime.UtcNow,
        Success = status == RunStatuses.Succeeded,
    };

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleParents.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
    }
}
