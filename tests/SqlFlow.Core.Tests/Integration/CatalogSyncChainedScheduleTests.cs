using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// A CHAINED schedule must survive the sync and land in the catalog.
///
/// Regression guard for a bug that reached production: the sync validated every schedule with
/// <c>ScheduleClock.TryValidate</c>, which requires exactly one of cron / intervalSeconds. A chained schedule has
/// neither by design, so every one of them failed that check and was dropped before the upsert. The flows joining
/// them synced fine, which made the loss silent: the chain simply did not exist in the catalog, was invisible in the
/// GUI, and could not be run by hand. The clock check now applies only to clock-driven schedules.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncChainedScheduleTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_chain_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncChainedScheduleTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task ChainedSchedule_IsRegistered_WithNoCadenceAndNoNextFire()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var repo = "chain_" + Guid.NewGuid().ToString("N")[..8];

        WriteFlow("head_flow", "head_daily");
        WriteFlow("tail_flow", "tail_daily");
        File.WriteAllText(Path.Combine(_dir, "schedules.yaml"), """
            schedules:
              head_daily:
                cron: "10 20 * * *"
                timezone: UTC
                enabled: false
              tail_daily:
                after: head_daily
                enabled: false
            """);

        try
        {
            await using (var db = CatalogDatabase.Create(cs))
            {
                await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
            }

            await using (var db = CatalogDatabase.Create(cs))
            {
                var schedules = await db.Schedules.AsNoTracking()
                    .Where(s => s.Name == "head_daily" || s.Name == "tail_daily")
                    .ToListAsync();

                // Both must exist. Before the fix only the clock-driven one did.
                Assert.Equal(2, schedules.Count);

                var head = schedules.Single(s => s.Name == "head_daily");
                Assert.Equal("10 20 * * *", head.Cron);
                Assert.Empty(await db.ScheduleParents.AsNoTracking().Where(p => p.ScheduleId == head.Id).ToListAsync());
                Assert.NotNull(head.NextFireUtc);

                var tail = schedules.Single(s => s.Name == "tail_daily");
                var tailParents = await db.ScheduleParents.AsNoTracking()
                    .Where(p => p.ScheduleId == tail.Id).OrderBy(p => p.Ordinal).Select(p => p.ParentName).ToListAsync();
                Assert.Equal(["head_daily"], tailParents);
                Assert.Null(tail.Cron);
                Assert.Null(tail.IntervalSeconds);
                // A null next fire is what keeps a shadow schedule out of the clock-driven due scan.
                Assert.Null(tail.NextFireUtc);

                // It must also carry its member, or it would register as a schedule that runs nothing and could not
                // be executed by hand from the GUI.
                var members = await db.ScheduleMembers.AsNoTracking()
                    .Where(m => m.ScheduleId == tail.Id).ToListAsync();
                Assert.Equal("tail_flow", Assert.Single(members).FlowName);
            }
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            var ids = await db.Schedules.Where(s => s.Name == "head_daily" || s.Name == "tail_daily")
                .Select(s => s.Id).ToListAsync();
            await db.ScheduleMembers.Where(m => ids.Contains(m.ScheduleId)).ExecuteDeleteAsync();
            await db.Schedules.Where(s => ids.Contains(s.Id)).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.Name == "head_flow" || p.Name == "tail_flow").ExecuteDeleteAsync();
        }
    }

    private void WriteFlow(string flowName, string scheduleName)
    {
        var path = Path.Combine(_dir, flowName + ".yaml");
        File.WriteAllText(path, """
            name: __NAME__
            schedule: __SCHEDULE__
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: ChainOrders
            """
            .Replace("__NAME__", flowName, StringComparison.Ordinal)
            .Replace("__SCHEDULE__", scheduleName, StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // already gone
        }
    }
}
