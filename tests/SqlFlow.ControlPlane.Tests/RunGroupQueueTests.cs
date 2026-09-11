using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The multi-flow run group over the real catalog database: enqueuing a set as one wave-ordered group, the claim's
/// wave gating (a higher-wave member is never claimed before its lower-wave siblings finish), the on-failure skip of
/// a failed flow's dependents (while independent branches keep running), cancelling a whole group, and the scope
/// expander that turns Flow / Node / Batch into the concrete member set. Gated on a reachable catalog database like
/// the other DB-backed tests; each test removes its own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunGroupQueueTests
{
    private const string Node = "test-group-node";

    [SkippableFact]
    public async Task EnqueueGroup_InsertsHeaderAndMembers_QueuedInWaveOrder()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var now = DateTime.UtcNow;
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, $"a_{suffix}", members), now);

            Assert.Equal(2, result.RunIds.Count);
            var header = await db.RunGroups.AsNoTracking().FirstOrDefaultAsync(g => g.GroupId == result.GroupId);
            Assert.NotNull(header);
            Assert.Equal(RunGroupModes.Node, header.Mode);
            Assert.Equal(2, header.MemberCount);

            var runs = await db.Runs.AsNoTracking()
                .Where(r => r.GroupId == result.GroupId).OrderBy(r => r.GroupWave).ToListAsync();
            Assert.Equal(2, runs.Count);
            Assert.All(runs, r => Assert.Equal(RunStatuses.Queued, r.Status));
            Assert.Equal(0, runs[0].GroupWave);
            Assert.Equal(1, runs[1].GroupWave);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task EnqueueGroup_NodeBackfill_WindowsAnchor_AndReprocessesDescendants()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // A three-layer chain like copy -> file -> ing: the window stays on the anchor, the relational ing
            // descendant reprocesses from source min, and the file descendant runs at defaults (it catches the
            // re-landed files through its own normal incremental). This mirrors what TriggerGroupAsync builds.
            string anchor = $"a_{suffix}", fileChild = $"b_{suffix}", ingChild = $"c_{suffix}";
            var members = new List<RunScopeMember>
            {
                new(anchor, "cpy", 0, CatalogPipeline.DefaultBatch),
                new(fileChild, "file", 1, CatalogPipeline.DefaultBatch),
                new(ingChild, "ing", 2, CatalogPipeline.DefaultBatch),
            };
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            var memberParameters = new Dictionary<string, RunParameters>(StringComparer.Ordinal)
            {
                [anchor] = new RunParameters { BackfillFrom = from, BackfillTo = to },
                [ingChild] = new RunParameters { ReprocessFromSourceMin = true },
                // fileChild is intentionally absent: a window-honoring descendant runs at defaults.
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db,
                new RunGroupEnqueueRequest(
                    repoId, RunGroupModes.Node, anchor, members, MemberParameters: memberParameters),
                DateTime.UtcNow);

            var runs = await db.Runs.AsNoTracking()
                .Where(r => r.GroupId == result.GroupId).ToListAsync();
            var anchorRun = Assert.Single(runs, r => r.FlowName == anchor);
            var fileRun = Assert.Single(runs, r => r.FlowName == fileChild);
            var ingRun = Assert.Single(runs, r => r.FlowName == ingChild);

            // The anchor carries the window (it selects the files to re-land at the source).
            Assert.Equal(from, anchorRun.BackfillFrom);
            Assert.Equal(to, anchorRun.BackfillTo);
            Assert.False(anchorRun.ReprocessFromSourceMin);

            // The file descendant runs at defaults: no window (it cannot re-enforce a modified-date window), no reprocess.
            Assert.Null(fileRun.BackfillFrom);
            Assert.Null(fileRun.BackfillTo);
            Assert.False(fileRun.ReprocessFromSourceMin);

            // The relational descendant carries MIN-from-source and no window, so it re-pulls the back-dated rows.
            Assert.True(ingRun.ReprocessFromSourceMin);
            Assert.Null(ingRun.BackfillFrom);
            Assert.Null(ingRun.BackfillTo);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task EnqueueGroup_PlacementsCarryTheWaveOrderAndCap_TheDispatcherGatesOn()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, $"a_{suffix}", members, MaxConcurrency: 2), DateTime.UtcNow);
            var (runA, runB) = (result.RunIds[0], result.RunIds[1]);

            // The placements handed to the dispatcher mirror the journaled rows: same group, waves, and cap.
            Assert.Equal(2, result.Placements.Count);
            var a = Assert.Single(result.Placements, p => p.RunId == runA);
            var b = Assert.Single(result.Placements, p => p.RunId == runB);
            Assert.Equal((result.GroupId, 0, 2), (a.GroupId, a.GroupWave, a.GroupMaxConcurrency));
            Assert.Equal((result.GroupId, 1, 2), (b.GroupId, b.GroupWave, b.GroupMaxConcurrency));
            var rowB = await Reload(db, runB);
            Assert.Equal((result.GroupId, 1, 2), (rowB.GroupId, rowB.GroupWave, rowB.GroupMaxConcurrency));

            // The journal's own reload reproduces exactly those placements, so a dispatcher rebuilt from it gates
            // the same way as one told at enqueue time.
            var (queued, _) = await RunQueueStore.LoadDispatchStateAsync(db);
            Assert.Equal(a, Assert.Single(queued, p => p.RunId == runA));
            Assert.Equal(b, Assert.Single(queued, p => p.RunId == runB));

            // Wave order end to end through the in-memory gate: only the wave-0 member is handed out, and the
            // wave-1 member follows once it is terminal.
            var state = new DispatchState();
            foreach (var placement in result.Placements)
            {
                state.AddQueuedRun(placement);
            }

            var pick = Assert.Single(state.ReserveRuns(Node, [], 10));
            Assert.Equal(runA, pick.RunId);
            Assert.True(await RunQueueStore.MarkHandedOutAsync(db, runA, pick.ExpectedAttempt, Node, DateTime.UtcNow));
            state.ConfirmRunLease(runA, Node, 1, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1));
            Assert.Empty(state.ReserveRuns(Node, [], 10));
            Assert.Equal(RunStatuses.Queued, (await Reload(db, runB)).Status);

            await CompleteSuccess(db, runA, $"a_{suffix}");
            state.RemoveRun(runA);
            Assert.Equal(runB, Assert.Single(state.ReserveRuns(Node, [], 10)).RunId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fail_SkipsDependents_ButLeavesIndependentBranchesRunnable()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            // B depends on A (wave 1 behind wave 0); C is an independent wave-1 flow with no dependency on A.
            await SeedDependencyAsync(db, repoId, A, B);

            var members = new List<RunScopeMember>
            {
                new(A, "ing", 0, CatalogPipeline.DefaultBatch),
                new(B, "ing", 1, CatalogPipeline.DefaultBatch),
                new(C, "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Node, A, members), DateTime.UtcNow);
            var (runA, runB, runC) = (result.RunIds[0], result.RunIds[1], result.RunIds[2]);

            Assert.True(await RunQueueStore.MarkHandedOutAsync(db, runA, 0, Node, DateTime.UtcNow));

            // A fails: its dependent B is skipped (and reported, so the dispatcher drops it from memory); the
            // independent C stays queued.
            var failed = await RunQueueStore.FailAsync(db, runA, "boom", DateTime.UtcNow, Node, 1);
            Assert.True(failed.Applied);
            Assert.Equal([runB], failed.SkippedRunIds);
            Assert.Equal(RunStatuses.Failed, (await Reload(db, runA)).Status);
            Assert.Equal(RunStatuses.Skipped, (await Reload(db, runB)).Status);
            Assert.Equal(RunStatuses.Queued, (await Reload(db, runC)).Status);
            Assert.True(await RunQueueStore.MarkHandedOutAsync(db, runC, 0, Node, DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task CancelGroup_CancelsQueuedMembers_AndReportsUnknownGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var members = new List<RunScopeMember>
            {
                new($"a_{suffix}", "ing", 0, CatalogPipeline.DefaultBatch),
                new($"b_{suffix}", "ing", 1, CatalogPipeline.DefaultBatch),
            };
            var result = await RunQueueStore.EnqueueGroupAsync(
                db, new RunGroupEnqueueRequest(repoId, RunGroupModes.Batch, $"bt_{suffix}", members), DateTime.UtcNow);

            var cancelled = await RunQueueStore.CancelGroupAsync(db, result.GroupId, DateTime.UtcNow);
            Assert.True(cancelled.Found);
            Assert.Equal(2, cancelled.CancelledQueued);
            Assert.Equal(0, cancelled.RequestedRunning);
            var runs = await db.Runs.AsNoTracking().Where(r => r.GroupId == result.GroupId).ToListAsync();
            Assert.All(runs, r => Assert.Equal(RunStatuses.Cancelled, r.Status));

            var unknown = await RunQueueStore.CancelGroupAsync(db, Guid.NewGuid(), DateTime.UtcNow);
            Assert.False(unknown.Found);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Expand_Node_ReturnsAnchorAndTransitiveDescendants()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            await SeedPipelineAsync(db, repoId, A, wave: 0, batch: $"bt_{suffix}");
            await SeedPipelineAsync(db, repoId, B, wave: 1, batch: $"bt_{suffix}");
            await SeedPipelineAsync(db, repoId, C, wave: 1, batch: $"bt_{suffix}");
            await SeedDependencyAsync(db, repoId, A, B);

            var node = await RunScopeExpander.ExpandAsync(db, repoId, A, RunScope.Node);
            Assert.Equal(new[] { A, B }, node.Members.Select(m => m.FlowName).OrderBy(x => x).ToArray());

            var flow = await RunScopeExpander.ExpandAsync(db, repoId, A, RunScope.Flow);
            Assert.Equal(new[] { A }, flow.Members.Select(m => m.FlowName).ToArray());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ExpandSchedule_ReturnsItsMembersInWaveOrder_AndOnlyItsMembers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            await SeedPipelineAsync(db, repoId, A, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, B, wave: 1, batch: "large");
            // C carries the same batch tag as A but never joined the schedule. Under membership a label match is not
            // a join, which is exactly what the old batch scope got wrong.
            await SeedPipelineAsync(db, repoId, C, wave: 0, batch: "small");

            var scheduleId = await SeedScheduleAsync(db, repoId, $"nightly_{suffix}", [A, B]);

            var expansion = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}");
            Assert.Equal($"nightly_{suffix}", expansion.Anchor);
            Assert.Equal(new[] { A, B }, expansion.Members.Select(m => m.FlowName).ToArray());
            Assert.Equal(new[] { 0, 1 }, expansion.Members.Select(m => m.Wave).ToArray());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task ExpandSchedule_BatchFilter_NarrowsToTaggedMembersOnly()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            string A = $"a_{suffix}", B = $"b_{suffix}", C = $"c_{suffix}";
            await SeedPipelineAsync(db, repoId, A, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, B, wave: 1, batch: "large");
            await SeedPipelineAsync(db, repoId, C, wave: 0, batch: "small");

            // A and B joined; C did not, though it is tagged 'small'.
            var scheduleId = await SeedScheduleAsync(db, repoId, $"nightly_{suffix}", [A, B]);

            // "Run the nightly, but only the small tables": a subset of the members, never a widening of them.
            var filtered = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}", ["small"]);
            Assert.Equal(new[] { A }, filtered.Members.Select(m => m.FlowName).ToArray());

            // A tag no member carries runs nothing rather than falling back to the whole set.
            var none = await RunScopeExpander.ExpandScheduleAsync(db, repoId, scheduleId, $"nightly_{suffix}", ["medium"]);
            Assert.Empty(none.Members);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    /// <summary>Seeds a schedule with an explicit member set, the shape the estate scan produces.</summary>
    private static async Task<Guid> SeedScheduleAsync(
        CatalogDbContext db, Guid repoId, string name, IReadOnlyList<string> members)
    {
        var now = DateTime.UtcNow;
        var id = CatalogIdentity.YamlSchedule(repoId, name);
        db.Schedules.Add(new CatalogSchedule
        {
            Id = id, RepoId = repoId, Name = name, Cron = "0 4 * * *", Timezone = "UTC",
            Enabled = true, Source = "yaml", CreatedUtc = now, UpdatedUtc = now,
        });
        foreach (var member in members)
        {
            db.ScheduleMembers.Add(new CatalogScheduleMember
            {
                ScheduleId = id, PipelineId = CatalogIdentity.Pipeline(repoId, member), RepoId = repoId, FlowName = member,
            });
        }

        await db.SaveChangesAsync();
        return id;
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("grp_" + suffix), suffix);
    }

    private static async Task SeedPipelineAsync(
        CatalogDbContext db, Guid repoId, string name, int wave, string batch)
    {
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = "ing",
            Batch = batch,
            RelativePath = $"flows/{name}.flow.yaml",
            Active = true,
            Wave = wave,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedDependencyAsync(CatalogDbContext db, Guid repoId, string from, string to)
    {
        db.FlowDependencies.Add(new CatalogFlowDependency
        {
            RepoId = repoId,
            FromFlow = from,
            ToFlow = to,
            FromPipelineId = CatalogIdentity.Pipeline(repoId, from),
            ToPipelineId = CatalogIdentity.Pipeline(repoId, to),
            ViaObjects = string.Empty,
        });
        await db.SaveChangesAsync();
    }

    private static async Task CompleteSuccess(CatalogDbContext db, Guid runId, string flowName)
    {
        var artifact = $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": true,
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": { "rowsLoaded": 1, "durationSeconds": 1.0 }
            }
            """;
        var outcome = await RunQueueStore.RecordOutcomeAsync(db, runId, Node, 1, RunOutcomeKind.Completed, null, artifact, DateTime.UtcNow);
        Assert.Equal(RunOutcomeStatus.Recorded, outcome.Status);
    }

    private static async Task<CatalogRun> Reload(CatalogDbContext db, Guid runId)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId);
        Assert.NotNull(run);
        return run;
    }

    private static async Task Cleanup(string cs, Guid repoId, string? dir = null)
    {
        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
            await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
        }

        if (dir is not null && Directory.Exists(dir))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // A transient lock on the temp file must not fail the test.
            }
        }
    }
}
