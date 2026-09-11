using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a schedule actually enqueues, through the real fire path. A schedule owns a member set (the flows that joined
/// it), and that set is the only selector: one member stays a single run, several expand into ONE wave-gated group so
/// the members carry the waves that order them. The assembly runs serially (see AssemblyInfo); each test removes its
/// own repo's rows.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScheduleFireTests
{
    [SkippableFact]
    public async Task Fire_SingleMember_EnqueuesOneRun_AndNoGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        var solo = $"a_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, solo, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, $"b_{suffix}", wave: 1, batch: "small");

            // Only `solo` joined, even though the other flow shares its batch tag: a label match is not a join.
            var schedule = await SeedScheduleAsync(db, repoId, $"s_{suffix}", [solo]);
            var fire = await ScheduleFire.EnqueueAsync(db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.Enqueued, fire.Outcome);
            Assert.Null(fire.GroupId);
            Assert.Equal(1, fire.MemberCount);
            var run = Assert.Single(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
            Assert.Equal(solo, run.FlowName);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_ManyMembers_EnqueuesOneWaveOrderedGroup()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string copy = $"cpy_{suffix}", pre = $"pre_{suffix}", ods = $"ods_{suffix}", other = $"oth_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // The Baatbooking shape: copy (wave 1) -> pre load (wave 2) -> ods merge (wave 3).
            await SeedPipelineAsync(db, repoId, copy, wave: 0, batch: "BB");
            await SeedPipelineAsync(db, repoId, pre, wave: 1, batch: "BB");
            await SeedPipelineAsync(db, repoId, ods, wave: 2, batch: "BB");
            // Carries the same batch tag but never joined, so the fire must not touch it.
            await SeedPipelineAsync(db, repoId, other, wave: 0, batch: "BB");

            var schedule = await SeedScheduleAsync(db, repoId, $"daily_{suffix}", [copy, pre, ods]);
            var fire = await ScheduleFire.EnqueueAsync(db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            Assert.NotNull(fire.GroupId);
            Assert.Equal(3, fire.MemberCount);

            var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            Assert.Equal(3, runs.Count);
            Assert.DoesNotContain(runs, r => r.FlowName == other);
            // The waves are what gate the order: the merge can never start before the load that feeds it.
            Assert.Equal(0, runs.Single(r => r.FlowName == copy).GroupWave);
            Assert.Equal(1, runs.Single(r => r.FlowName == pre).GroupWave);
            Assert.Equal(2, runs.Single(r => r.FlowName == ods).GroupWave);

            var stamped = await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == schedule.Id);
            Assert.Equal(fire.GroupId, stamped.LastGroupId);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_BatchFilter_RunsOnlyTheTaggedMembers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string small1 = $"s1_{suffix}", small2 = $"s2_{suffix}", large = $"lg_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, small1, wave: 0, batch: "small");
            await SeedPipelineAsync(db, repoId, small2, wave: 1, batch: "small");
            await SeedPipelineAsync(db, repoId, large, wave: 1, batch: "large");

            var schedule = await SeedScheduleAsync(db, repoId, $"nightly_{suffix}", [small1, small2, large]);

            // "Run the nightly, but only the small tables."
            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default, batchFilter: ["small"]);

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            Assert.Equal(2, fire.MemberCount);
            var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            Assert.Equal([small1, small2], runs.Select(r => r.FlowName).OrderBy(n => n, StringComparer.Ordinal));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_ManualMember_IsNeverRunByASchedule()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        string auto = $"a_{suffix}", manual = $"m_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, auto, wave: 0, batch: "BB");
            await SeedPipelineAsync(db, repoId, manual, wave: 1, batch: "BB", mode: PipelineExecutionModes.Manual);

            // Both joined, but mode: manual reserves a flow for a direct trigger, and firing a schedule it sits in
            // is not a direct trigger of it.
            var schedule = await SeedScheduleAsync(db, repoId, $"daily_{suffix}", [auto, manual]);
            var fire = await ScheduleFire.EnqueueAsync(db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.Enqueued, fire.Outcome);
            var run = Assert.Single(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
            Assert.Equal(auto, run.FlowName);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_WithBackfillWindow_RoutesRootToWindow_SilverToReprocessMin_IntermediateToDefault()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();
        // The Baatbooking shape by kind: an integration copy root, a file ingestion, and a silver (relational) load.
        string copy = $"cpy_{suffix}", file = $"file_{suffix}", silver = $"ing_{suffix}";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            await SeedPipelineAsync(db, repoId, copy, wave: 0, batch: "BB", kind: "cpy");
            await SeedPipelineAsync(db, repoId, file, wave: 1, batch: "BB", kind: "file");
            await SeedPipelineAsync(db, repoId, silver, wave: 2, batch: "BB", kind: "ing");

            var schedule = await SeedScheduleAsync(db, repoId, $"daily_{suffix}", [copy, file, silver]);
            var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            var fire = await ScheduleFire.EnqueueAsync(
                db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default,
                backfillWindow: new SqlFlow.Core.Runs.RunParameters { BackfillFrom = from, BackfillTo = to });

            Assert.Equal(ScheduleFire.Outcome.EnqueuedGroup, fire.Outcome);
            var runs = await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync();
            var copyRun = Assert.Single(runs, r => r.FlowName == copy);
            var fileRun = Assert.Single(runs, r => r.FlowName == file);
            var silverRun = Assert.Single(runs, r => r.FlowName == silver);

            // The root integration flow carries the window (it re-lands the slice).
            Assert.Equal(from, copyRun.BackfillFrom);
            Assert.Equal(to, copyRun.BackfillTo);
            Assert.False(copyRun.ReprocessFromSourceMin);

            // The intermediate file flow runs at defaults (it picks up the re-landed files via its own incremental).
            Assert.Null(fileRun.BackfillFrom);
            Assert.False(fileRun.ReprocessFromSourceMin);

            // The silver flow re-pulls from the source minimum.
            Assert.True(silverRun.ReprocessFromSourceMin);
            Assert.Null(silverRun.BackfillFrom);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fire_NoRunnableMember_EnqueuesNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, suffix) = NewRepo();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            // A schedule nothing joined: a normal state (a source whose flows all left), not a fault.
            var schedule = await SeedScheduleAsync(db, repoId, $"empty_{suffix}", []);
            var fire = await ScheduleFire.EnqueueAsync(db, new RecordingDispatcher(), schedule, DateTime.UtcNow, default);

            Assert.Equal(ScheduleFire.Outcome.ScopeEmpty, fire.Outcome);
            Assert.False(fire.Queued);
            Assert.Empty(await db.Runs.AsNoTracking().Where(r => r.RepoId == repoId).ToListAsync());
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    /// <summary>A dispatcher that enqueues through the real store (so the rows, group and waves are genuine) without
    /// the control plane's in-process signalling, which needs a hosted worker.</summary>
    private sealed class RecordingDispatcher : IRunDispatcher
    {
        public async Task<Guid> EnqueueAsync(CatalogDbContext catalog, RunEnqueueRequest request, CancellationToken ct = default)
            => (await RunQueueStore.EnqueueAsync(catalog, request, DateTime.UtcNow, ct)).RunId;

        public Task<RunGroupEnqueueResult> EnqueueGroupAsync(
            CatalogDbContext catalog, RunGroupEnqueueRequest request, CancellationToken ct = default)
            => RunQueueStore.EnqueueGroupAsync(catalog, request, DateTime.UtcNow, ct);

        public Task<CancelOutcome> CancelAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<GroupCancelResult> CancelGroupAsync(CatalogDbContext catalog, Guid groupId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Guid> EnqueueComputeTaskAsync(CatalogDbContext catalog, ComputeTaskEnqueueRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<CancelOutcome> CancelComputeTaskAsync(CatalogDbContext catalog, Guid taskId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static (Guid RepoId, string Suffix) NewRepo()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("scope_" + suffix), suffix);
    }

    private static async Task SeedPipelineAsync(
        CatalogDbContext db, Guid repoId, string name, int wave, string batch, string? mode = null, string kind = "ing")
    {
        db.Pipelines.Add(new CatalogPipeline
        {
            Id = CatalogIdentity.Pipeline(repoId, name),
            RepoId = repoId,
            Name = name,
            Kind = kind,
            Batch = batch,
            RelativePath = $"flows/{name}.flow.yaml",
            Active = true,
            Wave = wave,
            ExecutionMode = mode ?? PipelineExecutionModes.Auto,
            FirstSeenUtc = DateTime.UtcNow,
            LastSeenUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static async Task<CatalogSchedule> SeedScheduleAsync(
        CatalogDbContext db, Guid repoId, string scheduleName, IReadOnlyCollection<string> members)
    {
        var now = DateTime.UtcNow;
        await ScheduleStore.StageYamlUpsertAsync(
            db, repoId, scheduleName, members, "0 4 * * *", null, "UTC", enabled: true, catchup: false, maxConcurrency: ScheduleDefaults.MaxConcurrency, now.AddHours(1), now);
        await db.SaveChangesAsync();
        var id = CatalogIdentity.YamlSchedule(repoId, scheduleName);
        return await db.Schedules.AsNoTracking().FirstAsync(s => s.Id == id);
    }

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync();
        await db.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync();
        await db.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync();
    }
}
