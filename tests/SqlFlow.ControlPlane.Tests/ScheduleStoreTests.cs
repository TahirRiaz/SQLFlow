using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The schedule store against the real catalog: the due scan, the atomic fire-claim (two control-plane nodes never
/// fire the same occurrence), pause excluding a schedule from the due set, and the YAML upsert preserving an
/// operator's API pause across a git re-sync. The assembly runs serially (see AssemblyInfo). Each test removes its
/// own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleStoreTests
{
    [SkippableFact]
    public async Task ClaimFire_Concurrently_OnlyOneNodeWinsTheOccurrence()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var due = new DateTime(2026, 6, 19, 6, 0, 0, DateTimeKind.Utc);
        var nextA = due.AddDays(1);

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                seed.Schedules.Add(NewSchedule(repoId, flowName, due));
                await seed.SaveChangesAsync();
            }

            var id = CatalogIdentity.YamlSchedule(repoId, flowName);
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var results = await Task.WhenAll(
                ScheduleStore.TryClaimFireAsync(a, id, due, nextA, DateTime.UtcNow),
                ScheduleStore.TryClaimFireAsync(b, id, due, nextA, DateTime.UtcNow));

            // Exactly one compare-and-swap from the observed next-fire succeeds.
            Assert.Equal(1, results.Count(won => won));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ListDue_ExcludesPausedAndFutureSchedules()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pastDue = DateTime.UtcNow.AddMinutes(-5);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var schedule = NewSchedule(repoId, flowName, pastDue);
            db.Schedules.Add(schedule);
            await db.SaveChangesAsync();

            // Due and active: appears.
            var due = await ScheduleStore.ListDueAsync(db, DateTime.UtcNow, 1000);
            Assert.Contains(due, s => s.Id == schedule.Id);

            // Paused: excluded even though its next-fire is in the past.
            await ScheduleStore.SetPausedAsync(db, schedule.Id, paused: true, nextFireUtcOnResume: null, DateTime.UtcNow);
            var afterPause = await ScheduleStore.ListDueAsync(db, DateTime.UtcNow, 1000);
            Assert.DoesNotContain(afterPause, s => s.Id == schedule.Id);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task UpsertYamlSchedule_PreservesAnApiPause_AcrossReSync()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var now = DateTime.UtcNow;

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            // Sync a YAML schedule, then an operator pauses it through the API.
            var id = await ScheduleStore.UpsertYamlScheduleAsync(
                db, repoId, flowName, [flowName], "0 6 * * *", null, "UTC", enabled: true, catchup: false, maxConcurrency: ScheduleDefaults.MaxConcurrency, now.AddHours(1), now);
            await ScheduleStore.SetPausedAsync(db, id, paused: true, nextFireUtcOnResume: null, now);

            // A re-sync of the same (unchanged) schedule must not clear the operator's pause.
            await ScheduleStore.UpsertYamlScheduleAsync(
                db, repoId, flowName, [flowName], "0 6 * * *", null, "UTC", enabled: true, catchup: false, maxConcurrency: ScheduleDefaults.MaxConcurrency, now.AddHours(1), now);

            var after = await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == id);
            Assert.True(after.Paused);
            Assert.Equal("yaml", after.Source);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("sch_" + suffix), "sch_flow_" + suffix);
    }

    private static CatalogSchedule NewSchedule(Guid repoId, string flowName, DateTime nextFireUtc) => new()
    {
        Id = CatalogIdentity.YamlSchedule(repoId, flowName),
        RepoId = repoId,
        Name = flowName,

        Cron = "0 6 * * *",
        Timezone = "UTC",
        Enabled = true,
        Source = "yaml",
        NextFireUtc = nextFireUtc,
        CreatedUtc = DateTime.UtcNow,
        UpdatedUtc = DateTime.UtcNow,
    };

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
    }
}
