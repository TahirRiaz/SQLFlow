using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.ControlPlane.Maintenance;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The Maintenance page against the real catalog: the store keeps each area's retention and the log of its cleanups, the
/// run-trace cleanups take only superseded successful runs' statements and events (never a flow's latest run or a failed
/// one), and the API reports every area, lets anyone signed in read and preview and only an admin set a retention or clean
/// up, and runs a module's cleanup through the module's own contributor. The cleanups the API runs belong to a stand-in
/// module, so no test removes another test's catalog rows; the run-trace cleanups use cut-offs decades in the past, which
/// only this test's backdated runs fall behind.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MaintenanceApiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task Store_KeepsRetentions_AndLogsCleanups_ClosingOnlyTheInterruptedOnes()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var area = $"test.store-{Guid.NewGuid():N}";
        var other = $"test.store-{Guid.NewGuid():N}";
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);

        try
        {
            await MaintenanceStore.SetRetentionAsync(db, area, 30, "alice", now);
            var off = await MaintenanceStore.SetRetentionAsync(db, area, null, "bob", now.AddMinutes(1));
            Assert.Null(off.Days);
            var stored = (await MaintenanceStore.GetRetentionsAsync(db))[area];
            Assert.Null(stored.Days);
            Assert.Equal("bob", stored.UpdatedBy);

            var mine = await MaintenanceStore.StartCleanupAsync(db, area, CleanupTriggers.Manual, "alice", "host-a", 7, now.AddDays(-7), now);
            var theirs = await MaintenanceStore.StartCleanupAsync(db, other, CleanupTriggers.Manual, "carol", "host-b", 7, now.AddDays(-7), now);
            var old = await MaintenanceStore.StartCleanupAsync(db, other, CleanupTriggers.Manual, "carol", "host-b", 7, now.AddDays(-8), now.AddHours(-13));

            // Host a restarts: its own open entry closes; host b's recent one is left to b, its stale one closes.
            var closed = await MaintenanceStore.CloseInterruptedAsync(db, "host-a", [], TimeSpan.FromHours(12), now);
            Assert.Equal(2, closed);
            Assert.NotNull((await MaintenanceStore.GetCleanupAsync(db, mine.Id))!.Error);
            Assert.Null((await MaintenanceStore.GetCleanupAsync(db, theirs.Id))!.CompletedUtc);
            Assert.NotNull((await MaintenanceStore.GetCleanupAsync(db, old.Id))!.CompletedUtc);

            // An area the host is busy with is left alone, whatever its entries.
            Assert.Equal(0, await MaintenanceStore.CloseInterruptedAsync(db, "host-b", [other], TimeSpan.FromHours(12), now));
            Assert.Equal(1, await MaintenanceStore.CompleteCleanupAsync(db, theirs.Id, 42, null, now.AddMinutes(5)));
            Assert.Equal(0, await MaintenanceStore.CompleteCleanupAsync(db, theirs.Id, 99, "late", now.AddMinutes(6)));

            await MaintenanceStore.RecordAutomaticAsync(db, other, "bob", "host-b", 30, now.AddDays(-30), now.AddMinutes(10), now.AddMinutes(11), 5, null);
            var history = await MaintenanceStore.ListCleanupsAsync(db, other, 10);
            Assert.Equal([CleanupTriggers.Automatic, CleanupTriggers.Manual, CleanupTriggers.Manual], history.Select(h => h.Trigger));
            Assert.Equal(42, history[1].Removed);
            Assert.Equal(CleanupTriggers.Automatic, (await MaintenanceStore.LatestCleanupsAsync(db))[other].Trigger);

            // A year on, the log trims what it keeps no longer.
            Assert.True(await MaintenanceStore.TrimCleanupLogAsync(db, now.AddDays(400)) >= 4);
            Assert.Empty(await MaintenanceStore.ListCleanupsAsync(db, other, 10));
        }
        finally
        {
            await db.MaintenanceCleanups.Where(c => c.AreaId == area || c.AreaId == other).ExecuteDeleteAsync();
            await db.RetentionSettings.Where(r => r.AreaId == area).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task RunTraceCleanups_TakeOnlySupersededSuccessfulRuns()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var pipeline = Guid.NewGuid();
        var day = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var superseded = Run(pipeline, RunStatuses.Succeeded, day);
        var failed = Run(pipeline, RunStatuses.Failed, day.AddDays(1));
        var latest = Run(pipeline, RunStatuses.Succeeded, day.AddDays(2));
        var runs = new[] { superseded, failed, latest };

        try
        {
            db.Runs.AddRange(runs);
            foreach (var run in runs)
            {
                db.RunStatements.Add(new CatalogRunStatement { RunId = run.RunId, RepoId = run.RepoId, Ordinal = 1, TimestampUtc = run.WrittenUtc, Step = "load", Sql = "SELECT 1" });
                db.RunEvents.Add(new CatalogRunEvent { RunId = run.RunId, RepoId = run.RepoId, Ordinal = 1, TimestampUtc = run.WrittenUtc, Level = "info", Message = "loaded", Rows = 10 });
            }

            await db.SaveChangesAsync();

            // A cut-off before every run takes nothing; one after all three takes only the superseded successful run's rows.
            var before = day.AddDays(-1);
            var after = day.AddDays(30);
            Assert.Equal(0, await RunTraceStore.CountPrunableStatementsAsync(db, before));
            Assert.Equal(1, await RunTraceStore.CountPrunableStatementsAsync(db, after));
            Assert.Equal(1, await RunTraceStore.CountPrunableEventsAsync(db, after));

            Assert.Equal(1, await RunTraceStore.PruneStatementsAsync(db, after));
            Assert.Equal(1, await RunTraceStore.PruneEventsAsync(db, after));
            Assert.Equal(0, await RunTraceStore.PruneEventsAsync(db, after));

            var ids = runs.Select(r => r.RunId).ToList();
            var statementRuns = await db.RunStatements.AsNoTracking().Where(s => ids.Contains(s.RunId)).Select(s => s.RunId).ToListAsync();
            var eventRuns = await db.RunEvents.AsNoTracking().Where(e => ids.Contains(e.RunId)).Select(e => e.RunId).ToListAsync();
            Assert.Equal(new HashSet<Guid> { failed.RunId, latest.RunId }, statementRuns.ToHashSet());
            Assert.Equal(new HashSet<Guid> { failed.RunId, latest.RunId }, eventRuns.ToHashSet());
            Assert.Equal(3, await db.Runs.AsNoTracking().CountAsync(r => ids.Contains(r.RunId)));
        }
        finally
        {
            var ids = runs.Select(r => r.RunId).ToList();
            await db.RunStatements.Where(s => ids.Contains(s.RunId)).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => ids.Contains(e.RunId)).ExecuteDeleteAsync();
            await db.Runs.Where(r => ids.Contains(r.RunId)).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task Api_ReportsEveryArea_AndHoldsEachWriteToItsScope()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var module = new StandInModule();
        // The automatic pass is off, so the only cleanup of the stand-in area is the one this test asks for.
        await using var factory = new ControlPlaneAppFactory().WithCatalog(cs)
            .WithSetting("ControlPlane:Maintenance:Enabled", "false")
            .WithServices(s =>
            {
                s.AddSingleton(module);
                s.AddScoped<IStorageContributor, StandInContributor>();
            });
        using var client = factory.CreateClient();
        var read = await AiContextTestKit.IssueTokenAsync(client, ["read"]);
        var operate = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate"]);
        var admin = await AiContextTestKit.IssueTokenAsync(client, ["read", "operate", "admin"]);

        try
        {
            using (var response = await AiContextTestKit.SendAsync(client, read, HttpMethod.Get, "/api/v1/maintenance/storage"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var report = await response.Content.ReadFromJsonAsync<StorageReportDto>(Web);
                Assert.NotNull(report);
                var statements = report.Areas.Single(a => a.Id == CatalogStorageContributor.RunStatementsArea);
                Assert.Equal("safe", statements.Verdict);
                Assert.Equal("SQLFlow", statements.Owner);
                Assert.Equal("RunStatement", Assert.Single(statements.Tables).Name);
                Assert.NotNull(statements.Retention);
                Assert.Equal("keep", report.Areas.Single(a => a.Id == "sqlflow.runs").Verdict);
                var standIn = report.Areas.Single(a => a.Id == StandInContributor.AreaId);
                Assert.Equal("Stand-in module", standIn.Owner);
                Assert.Equal("optional", standIn.Verdict);
                Assert.True(report.Areas.Where(a => a.Owner == "SQLFlow").Sum(a => a.Bytes) > 0);
                Assert.DoesNotContain(report.Undescribed, t => t.Schema == CatalogDbContext.SchemaName);
            }

            var area = $"/api/v1/maintenance/areas/{StandInContributor.AreaId}";

            // Anyone signed in may preview; only an admin may set a retention or clean up.
            using (var response = await AiContextTestKit.SendAsync(client, operate, HttpMethod.Post, $"{area}/preview", new StorageDaysDto(10)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var preview = await response.Content.ReadFromJsonAsync<CleanupPreviewDto>(Web);
                Assert.Equal(StandInModule.Rows, preview!.Count);
                Assert.Equal("entries", preview.Unit);
            }

            using (var response = await AiContextTestKit.SendAsync(client, operate, HttpMethod.Put, $"{area}/retention", new StorageDaysDto(30)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            using (var response = await AiContextTestKit.SendAsync(client, operate, HttpMethod.Post, $"{area}/cleanup", new StorageDaysDto(30)))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            // Days the area does not take, an area that takes none and one nobody describes are refused.
            using (var response = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Put, $"{area}/retention", new StorageDaysDto(0)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using (var response = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Put, "/api/v1/maintenance/areas/sqlflow.runs/retention", new StorageDaysDto(30)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using (var response = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Post, "/api/v1/maintenance/areas/test.nothing/cleanup", new StorageDaysDto(30)))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var response = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Put, $"{area}/retention", new StorageDaysDto(30)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var stored = (await response.Content.ReadFromJsonAsync<StorageRetentionDto>(Web))!;
                Assert.Equal(30, stored.Days);
                // Who set it is on record, by the token's subject.
                Assert.False(string.IsNullOrWhiteSpace(stored.UpdatedBy));
            }

            using (var response = await AiContextTestKit.SendAsync(client, read, HttpMethod.Get, "/api/v1/maintenance/storage"))
            {
                var report = await response.Content.ReadFromJsonAsync<StorageReportDto>(Web);
                var retention = report!.Areas.Single(a => a.Id == StandInContributor.AreaId).Retention!;
                Assert.Equal(30, retention.Days);
                Assert.Equal(StandInModule.Rows, retention.Reclaimable);
            }

            // An admin's cleanup is accepted at once, runs through the module's contributor, and its entry closes with what it removed.
            CleanupDto started;
            using (var response = await AiContextTestKit.SendAsync(client, admin, HttpMethod.Post, $"{area}/cleanup", new StorageDaysDto(10)))
            {
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                started = (await response.Content.ReadFromJsonAsync<CleanupDto>(Web))!;
                Assert.Equal(CleanupTriggers.Manual, started.Trigger);
                Assert.False(string.IsNullOrWhiteSpace(started.Actor));
                Assert.Equal(10, started.Days);
            }

            var runner = factory.Services.GetRequiredService<MaintenanceRunner>();
            if (runner.Running(StandInContributor.AreaId) is { } running)
            {
                await running.WaitAsync(TimeSpan.FromSeconds(30));
            }

            using (var response = await AiContextTestKit.SendAsync(client, read, HttpMethod.Get, $"{area}/cleanups"))
            {
                var history = await response.Content.ReadFromJsonAsync<List<CleanupDto>>(Web);
                var done = Assert.Single(history!, h => h.Id == started.Id);
                Assert.NotNull(done.CompletedUtc);
                Assert.Equal(StandInModule.Rows, done.Removed);
                Assert.Null(done.Error);
                Assert.False(done.Running);
            }

            Assert.Single(module.Cleaned);
            Assert.True(module.Cleaned.Single() < DateTime.UtcNow.AddDays(-9));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.MaintenanceCleanups.Where(c => c.AreaId == StandInContributor.AreaId).ExecuteDeleteAsync();
            await db.RetentionSettings.Where(r => r.AreaId == StandInContributor.AreaId).ExecuteDeleteAsync();
        }
    }

    private static CatalogRun Run(Guid pipeline, string status, DateTime written) => new()
    {
        RunId = Guid.CreateVersion7(),
        PipelineId = pipeline,
        RepoId = Guid.NewGuid(),
        FlowName = $"maintenance-test-{Guid.NewGuid():N}",
        FlowKind = "file",
        Status = status,
        Success = status == RunStatuses.Succeeded,
        WrittenUtc = written,
        EndUtc = written,
    };

    /// <summary>What the stand-in module's one area holds, and the cut-offs it was cleaned at.</summary>
    private sealed class StandInModule
    {
        public const long Rows = 7;

        public ConcurrentQueue<DateTime> Cleaned { get; } = new();
    }

    /// <summary>A module's contributor: one optional area over a table of its own, counted and cleaned in memory.</summary>
    private sealed class StandInContributor(StandInModule module) : IStorageContributor
    {
        public const string AreaId = "standin.history";

        public string Owner => "Stand-in module";

        public IReadOnlyList<StorageArea> Areas { get; } =
        [
            new()
            {
                Id = AreaId,
                Group = "Stand-in",
                Title = "History",
                Verdict = StorageVerdict.Optional,
                Holds = "The stand-in module's history.",
                Reason = "Older history is lost.",
                Tables = [StorageTable.Named("standin", "History")],
                Retention = new StorageRetention("The latest entry.", "entries", MinDays: 1),
            },
        ];

        public Task<long> CountAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct) => Task.FromResult(StandInModule.Rows);

        public Task<long> CleanAsync(StorageArea area, DateTime olderThanUtc, CancellationToken ct)
        {
            module.Cleaned.Enqueue(olderThanUtc);
            return Task.FromResult(StandInModule.Rows);
        }
    }
}
