using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Two ways a catalog sync used to lose what it holds. A flow document still in the repo but not loading (its inline
/// schedule carries an operation its built-in kind refuses) was skipped by the estate scan without a word, so the sync
/// deleted its pipeline, its own schedule and its memberships as though the flow had left the repository; now the sync
/// warns and holds them as the last sync recorded them until the file loads again. And a git schedule whose name an
/// API-created schedule already holds failed the whole sync on the unique (repo, name) index; now that schedule is left
/// out with a warning naming both, and everything else syncs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CatalogSyncHeldFlowAndScheduleNameTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_held_" + Guid.NewGuid().ToString("N"));

    public CatalogSyncHeldFlowAndScheduleNameTests() => Directory.CreateDirectory(_dir);

    [SkippableFact]
    public async Task AFlowThatStopsLoading_IsHeld_WithItsSchedulesAndMemberships_AndReported()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "held_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        string orders = "orders_" + suffix, invoices = "invoices_" + suffix, inline = "orders_nightly_" + suffix, shared = "daily_" + suffix;

        // orders declares its own named schedule and also joins the library's; invoices joins the library's only.
        WriteFlow(orders, $"schedule:\n  name: {inline}\n  cron: \"0 2 * * *\"\n");
        WriteFlow(invoices, $"schedule: {shared}\n");
        File.WriteAllText(Path.Combine(_dir, "schedules.yaml"), $"schedules:\n  {shared}: {{ cron: \"0 4 * * *\" }}\n");
        // A library schedule orders joins too, so the held flow's membership of a schedule defined elsewhere is covered.
        File.WriteAllText(Path.Combine(_dir, "more.schedules.yaml"), $"schedules:\n  weekly_{suffix}: {{ cron: \"0 5 * * 1\" }}\n");
        WriteFlow("joiner_" + suffix, $"schedule: weekly_{suffix}\n");

        try
        {
            await SyncAsync(cs, repo);

            // A flow's schedule: is either a list of names or an inline block, so orders' membership of weekly (a schedule
            // defined elsewhere) is recorded directly, as an earlier sync of a different revision would have left it.
            await using (var db = CatalogDatabase.Create(cs))
            {
                var weeklyId = CatalogIdentity.YamlSchedule(repoId, "weekly_" + suffix);
                db.ScheduleMembers.Add(new CatalogScheduleMember
                {
                    ScheduleId = weeklyId,
                    PipelineId = CatalogIdentity.Pipeline(repoId, orders),
                    RepoId = repoId,
                    FlowName = orders,
                });
                await db.SaveChangesAsync();
            }

            var before = await ReadAsync(cs, repoId, orders, inline, "weekly_" + suffix);
            Assert.True(before.PipelineActive);

            // The flow stops loading: a built-in kind takes no operation in its schedule.
            WriteFlow(orders, $"schedule:\n  name: {inline}\n  cron: \"0 2 * * *\"\n  operation: load\n");
            var result = await SyncAsync(cs, repo);

            Assert.Contains(result.Warnings, w => w.StartsWith($"{orders}.yaml: is a flow document that does not load", StringComparison.Ordinal)
                                                  && w.Contains("'file' flows take none", StringComparison.Ordinal));
            Assert.Contains(result.Warnings, w => w.Contains($"'{orders}' ({orders}.yaml) does not load, so this sync holds its pipeline", StringComparison.Ordinal));
            Assert.Equal(0, result.PipelinesDeleted);

            var after = await ReadAsync(cs, repoId, orders, inline, "weekly_" + suffix);
            Assert.True(after.PipelineActive);
            Assert.Equal(before.ContentHash, after.ContentHash);
            Assert.True(after.InlineScheduleExists, "the schedule the held flow declares must be kept");
            Assert.True(after.InlineMember, "the held flow must stay a member of its own schedule");
            Assert.True(after.WeeklyMember, "the held flow must stay a member of a schedule defined elsewhere");

            // Fixed again, it syncs as usual.
            WriteFlow(orders, $"schedule:\n  name: {inline}\n  cron: \"0 2 * * *\"\n");
            var fixedResult = await SyncAsync(cs, repo);
            Assert.DoesNotContain(fixedResult.Warnings, w => w.Contains("does not load", StringComparison.Ordinal));
            Assert.True((await ReadAsync(cs, repoId, orders, inline, "weekly_" + suffix)).InlineMember);
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task AGitScheduleNamedLikeAnApiSchedule_IsLeftOutWithAWarning_AndTheSyncCommits()
    {
        var cs = IntegrationDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repo = "clash_" + suffix;
        var repoId = FlowIdentity.FromName(repo);
        string flow = "orders_" + suffix, name = "Nightly_" + suffix;

        WriteFlow(flow, string.Empty);
        try
        {
            await SyncAsync(cs, repo);
            Guid apiId;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var now = DateTime.UtcNow;
                apiId = await ScheduleStore.CreateApiScheduleAsync(
                    db, repoId, name, [flow], "0 6 * * *", null, "UTC", enabled: true, catchup: false,
                    maxConcurrency: null, now.AddHours(1), now);
            }

            // git now declares the same name, spelled in another case: one name per repo, matched case-insensitively.
            WriteFlow(flow, $"schedule:\n  name: {name.ToLowerInvariant()}\n  cron: \"0 2 * * *\"\n");
            var result = await SyncAsync(cs, repo);

            Assert.Contains(result.Warnings, w => w.StartsWith($"schedule '{name.ToLowerInvariant()}' ('{flow}' ({flow}.yaml)) is not synced", StringComparison.Ordinal)
                                                  && w.Contains($"API-created schedule named '{name}' (id {apiId})", StringComparison.Ordinal));
            await using var check = CatalogDatabase.Create(cs);
            var schedules = await check.Schedules.AsNoTracking().Where(s => s.RepoId == repoId).ToListAsync();
            var api = Assert.Single(schedules);
            Assert.Equal(apiId, api.Id);
            Assert.Equal("api", api.Source);
            Assert.Equal("0 6 * * *", api.Cron);
            Assert.True(await check.Pipelines.AnyAsync(p => p.RepoId == repoId && p.Name == flow && p.Active));
        }
        finally
        {
            await CleanupAsync(cs, repoId);
        }
    }

    private async Task<CatalogSyncResult> SyncAsync(string cs, string repo)
    {
        await using var db = CatalogDatabase.Create(cs);
        return await new CatalogSync().SyncAsync(db, _dir, repo, null, DateTime.UtcNow);
    }

    private static async Task<(bool PipelineActive, string? ContentHash, bool InlineScheduleExists, bool InlineMember, bool WeeklyMember)> ReadAsync(
        string cs, Guid repoId, string flow, string inline, string weekly)
    {
        await using var db = CatalogDatabase.Create(cs);
        var pipelineId = CatalogIdentity.Pipeline(repoId, flow);
        var pipeline = await db.Pipelines.AsNoTracking().SingleOrDefaultAsync(p => p.Id == pipelineId);
        var inlineId = CatalogIdentity.YamlSchedule(repoId, inline);
        var weeklyId = CatalogIdentity.YamlSchedule(repoId, weekly);
        return (
            pipeline?.Active ?? false,
            pipeline?.ContentHash,
            await db.Schedules.AnyAsync(s => s.Id == inlineId),
            await db.ScheduleMembers.AnyAsync(m => m.ScheduleId == inlineId && m.PipelineId == pipelineId),
            await db.ScheduleMembers.AnyAsync(m => m.ScheduleId == weeklyId && m.PipelineId == pipelineId));
    }

    private static async Task CleanupAsync(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleParents.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
    }

    /// <summary>Writes a file flow whose <c>schedule:</c> is <paramref name="scheduleBlock"/>, written from column 0 (an
    /// empty block declares none).</summary>
    private void WriteFlow(string flowName, string scheduleBlock)
        => File.WriteAllText(Path.Combine(_dir, flowName + ".yaml"), $$"""
            name: {{flowName}}
            {{scheduleBlock}}
            source:
              type: csv
              location: ./data.csv
            target:
              connection: ${env:SQLFlowSinkConStr}
              schema: dbo
              table: HeldOrders
            """);

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
