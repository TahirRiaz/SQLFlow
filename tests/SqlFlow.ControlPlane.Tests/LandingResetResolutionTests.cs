using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The node's landing-reset verdict (<see cref="RunContextStore.ResolveLandingResetAsync"/>): may the engine truncate a
/// chained landing (bronze) table before the next load? The contract under test is one hop only: the reset is
/// authorized exactly when every flow DIRECTLY reading the landing's typed view has completed a successful run
/// that started after the landing flow's last successful load ended; anything further downstream never enters the
/// verdict. Every refusal must carry the blocking consumer, and the destructive direction must fail closed: no
/// prior load, a base-table reader, a lagging consumer, or a window/filter-bounded backfill of a flow that
/// re-lands its whole selection all preserve the staged rows. A bounded backfill of an incremental flow, like a
/// plain forced full load, keeps the normal gate. Gated on a reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LandingResetResolutionTests
{
    private sealed record Graph(
        Guid RepoId, Guid ProducerId, Guid ConsumerId, string ConsumerFlow, string Schema, string Table, string ViewKey);

    /// <summary>Seeds the minimal chained landing graph: producer -> consumer via the typed view
    /// <c>[pre].[v_&lt;table&gt;]</c>, with the consumer's Reads edge on the view. Runs are seeded per test.</summary>
    private static async Task<Graph> SeedGraphAsync(CatalogDbContext db, string suffix)
    {
        var graph = new Graph(
            RepoId: Guid.NewGuid(),
            ProducerId: Guid.NewGuid(),
            ConsumerId: Guid.NewGuid(),
            ConsumerFlow: "ods_" + suffix,
            Schema: "pre",
            Table: "T_" + suffix,
            ViewKey: $"@srv|dwpre|pre|v_T_{suffix}");
        var now = DateTime.UtcNow;

        db.FlowDependencies.Add(new CatalogFlowDependency
        {
            RepoId = graph.RepoId, FromFlow = "pre_" + suffix, ToFlow = graph.ConsumerFlow,
            FromPipelineId = graph.ProducerId, ToPipelineId = graph.ConsumerId, ViaObjects = $"pre.v_{graph.Table}",
        });
        db.LineageEdges.Add(new CatalogLineageEdge
        {
            RepoId = graph.RepoId, Flow = graph.ConsumerFlow, PipelineId = graph.ConsumerId,
            Relation = "Reads", ObjectKey = graph.ViewKey, ObjectName = $"v_{graph.Table}", Tier = "Declared",
        });
        db.Objects.Add(new CatalogObject
        {
            Key = graph.ViewKey, ServerRef = "@srv", Database = "dwpre", Schema = graph.Schema,
            Name = $"v_{graph.Table}", Kind = "View", FirstSeenUtc = now, LastSeenUtc = now,
        });
        await db.SaveChangesAsync();
        return graph;
    }

    private static CatalogRun Run(Guid pipelineId, string flowName, bool success, DateTime start, DateTime end) => new()
    {
        RunId = Guid.NewGuid(), PipelineId = pipelineId, FlowName = flowName, FlowKind = "file",
        Success = success, Status = success ? RunStatuses.Succeeded : RunStatuses.Failed,
        StartUtc = start, EndUtc = end, WrittenUtc = end,
    };

    private static async Task CleanupAsync(CatalogDbContext db, Graph graph)
    {
        await db.FlowDependencies.Where(d => d.RepoId == graph.RepoId).ExecuteDeleteAsync();
        await db.LineageEdges.Where(e => e.RepoId == graph.RepoId).ExecuteDeleteAsync();
        await db.Objects.Where(o => o.Key == graph.ViewKey).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.PipelineId == graph.ProducerId || r.PipelineId == graph.ConsumerId).ExecuteDeleteAsync();
    }

    [SkippableFact]
    public async Task NoConsumers_ReturnsNull_SoPlainAppendFlowsAreLeftAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);

        var verdict = await RunContextStore.ResolveLandingResetAsync(
            db, Guid.NewGuid(), Guid.NewGuid(), "pre", "T_none_" + Guid.NewGuid().ToString("N")[..8], RunParameters.None, incrementalLanding: true, CancellationToken.None);

        Assert.Null(verdict);
    }

    [SkippableFact]
    public async Task ConsumerCaughtUp_Authorizes()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            await db.SaveChangesAsync();

            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, RunParameters.None, incrementalLanding: true, CancellationToken.None);

            Assert.NotNull(verdict);
            Assert.True(verdict.Authorized);
            Assert.Contains(graph.ConsumerFlow, verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task BoundedBackfillOfAFullSelectionFlow_Blocks_TheSliceMustNotReplaceTheWholeDataset()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Fully caught up: without the window the verdict would authorize. A flow that re-lands its whole
            // selection on every run keeps the whole dataset in the landing table, so its bounded run must still
            // refuse: truncating first would leave just the slice for a consumer that rebuilds from it. A plain
            // forced full load keeps the normal gate.
            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            await db.SaveChangesAsync();

            var windowed = RunParameters.None with { BackfillFrom = DateTime.UtcNow.AddYears(-1) };
            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, windowed, incrementalLanding: false, CancellationToken.None);
            Assert.NotNull(verdict);
            Assert.False(verdict.Authorized);
            Assert.Contains("backfill", verdict.Reason, StringComparison.Ordinal);

            var patterned = RunParameters.None with { FilePattern = "orders_2023*.csv" };
            var patternVerdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, patterned, incrementalLanding: false, CancellationToken.None);
            Assert.NotNull(patternVerdict);
            Assert.False(patternVerdict.Authorized);

            var filtered = RunParameters.None with { SourceFilter = "Region = 'NO'" };
            var filterVerdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, filtered, incrementalLanding: false, CancellationToken.None);
            Assert.NotNull(filterVerdict);
            Assert.False(filterVerdict.Authorized);

            var full = RunParameters.None with { FullLoad = true };
            var fullVerdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, full, incrementalLanding: false, CancellationToken.None);
            Assert.NotNull(fullVerdict);
            Assert.True(fullVerdict.Authorized);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task BoundedBackfillOfAnIncrementalFlow_TakesTheNormalGate_SoRepeatedBackfillsDoNotPileUp()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // An incremental flow's plain run leaves only its delta, so a slice is the same kind of load: once
            // the consumer has caught up, the backfill resets instead of stacking on rows already delivered.
            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            await db.SaveChangesAsync();

            var bounded = new[]
            {
                RunParameters.None with { BackfillFrom = DateTime.UtcNow.AddYears(-1) },
                RunParameters.None with { BackfillFrom = DateTime.UtcNow.AddYears(-1), BackfillTo = DateTime.UtcNow.AddMonths(-6) },
                RunParameters.None with { FilePattern = "orders_2023*.csv" },
                RunParameters.None with { SourceFilter = "Region = 'NO'" },
            };
            foreach (var parameters in bounded)
            {
                var verdict = await RunContextStore.ResolveLandingResetAsync(
                    db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, parameters, incrementalLanding: true, CancellationToken.None);
                Assert.NotNull(verdict);
                Assert.True(verdict.Authorized, $"{parameters.Describe()}: {verdict.Reason}");
                Assert.Contains(graph.ConsumerFlow, verdict.Reason, StringComparison.Ordinal);
            }

            // The gate is the normal one, not a free pass: a second load the consumer has not seen yet keeps
            // the rows, and the refusal names the consumer rather than the backfill.
            var secondLoadEnd = DateTime.UtcNow.AddMinutes(-20);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, secondLoadEnd.AddMinutes(-5), secondLoadEnd));
            await db.SaveChangesAsync();

            var lagging = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, bounded[0], incrementalLanding: true, CancellationToken.None);
            Assert.NotNull(lagging);
            Assert.False(lagging.Authorized);
            Assert.Contains(graph.ConsumerFlow, lagging.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("backfill", lagging.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task ResolveAsync_TakesTheWindowFromTheRunRow_AndTheIncrementalFactFromTheNode()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The whole path a node's request takes: the backfill window is read from the executing run's row,
            // the incremental fact from the request. A consumer that has caught up frees the landing table for
            // an incremental flow's backfill, and a node that does not say (an older one) is read as a flow that
            // re-lands its whole selection, so the rows are kept.
            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            var backfill = new CatalogRun
            {
                RunId = Guid.NewGuid(), PipelineId = graph.ProducerId, RepoId = graph.RepoId, FlowName = "pre", FlowKind = "file",
                Status = RunStatuses.Running, ClaimedByNode = "n1", Attempt = 1, StartUtc = DateTime.UtcNow,
                WrittenUtc = DateTime.UtcNow, BackfillFrom = DateTime.UtcNow.AddYears(-1),
            };
            db.Runs.Add(backfill);
            await db.SaveChangesAsync();

            var incremental = new RunContextRequest("n1", 1, graph.Schema, graph.Table, ResolveWatermark: true, ResolveLandingReset: true, IncrementalLanding: true);
            var authorized = await RunContextStore.ResolveAsync(db, backfill.RunId, incremental);
            Assert.True(authorized.Held);
            Assert.NotNull(authorized.LandingReset);
            Assert.True(authorized.LandingReset.Authorized, authorized.LandingReset.Reason);

            var unstated = new RunContextRequest("n1", 1, graph.Schema, graph.Table, ResolveWatermark: false, ResolveLandingReset: true);
            var refused = await RunContextStore.ResolveAsync(db, backfill.RunId, unstated);
            Assert.True(refused.Held);
            Assert.NotNull(refused.LandingReset);
            Assert.False(refused.LandingReset.Authorized);
            Assert.Contains("backfill", refused.LandingReset.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task LaggingConsumer_Blocks_AndNamesTheConsumer()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            // A run that STARTED before the load ended proves nothing (it may have read a partial table), even
            // though it finished after; and a run that started after but FAILED proves nothing either.
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(-1), loadEnd.AddMinutes(20)));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: false, loadEnd.AddMinutes(30), loadEnd.AddMinutes(35)));
            await db.SaveChangesAsync();

            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, RunParameters.None, incrementalLanding: true, CancellationToken.None);

            Assert.NotNull(verdict);
            Assert.False(verdict.Authorized);
            Assert.Contains(graph.ConsumerFlow, verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task NoPriorSuccessfulLoad_Blocks_SoASeededTableIsNeverTruncatedOnFaith()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // The consumer has run recently, but this flow itself has never succeeded: whatever sits in the
            // landing table (a manual seed, a migration backfill) has no ledger entry proving delivery.
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, DateTime.UtcNow.AddMinutes(-10), DateTime.UtcNow.AddMinutes(-5)));
            await db.SaveChangesAsync();

            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, RunParameters.None, incrementalLanding: true, CancellationToken.None);

            Assert.NotNull(verdict);
            Assert.False(verdict.Authorized);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task ConsumerReadingTheBaseTable_Blocks_TheChainedContractRequiresTheTypedView()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        try
        {
            // Repoint the consumer's read at the BASE table: it depends on this flow, but not through the typed
            // view, so its contract with the landing rows is unknown and the reset must be refused.
            await db.LineageEdges.Where(e => e.RepoId == graph.RepoId && e.PipelineId == graph.ConsumerId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.ObjectKey, $"@srv|dwpre|pre|{graph.Table}")
                    .SetProperty(e => e.ObjectName, graph.Table));

            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            await db.SaveChangesAsync();

            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, RunParameters.None, incrementalLanding: true, CancellationToken.None);

            Assert.NotNull(verdict);
            Assert.False(verdict.Authorized);
            Assert.Contains("typed view", verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(db, graph);
        }
    }

    [SkippableFact]
    public async Task EveryConsumerMustCatchUp_OneLaggardBlocksTheReset()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        await using var db = CatalogDatabase.Create(cs);
        var graph = await SeedGraphAsync(db, Guid.NewGuid().ToString("N")[..8]);
        var secondConsumerId = Guid.NewGuid();
        var secondConsumerFlow = graph.ConsumerFlow + "_b";
        try
        {
            // A second silver flow reads the SAME typed view (one landing shredded into several arc tables).
            db.FlowDependencies.Add(new CatalogFlowDependency
            {
                RepoId = graph.RepoId, FromFlow = "pre", ToFlow = secondConsumerFlow,
                FromPipelineId = graph.ProducerId, ToPipelineId = secondConsumerId, ViaObjects = $"pre.v_{graph.Table}",
            });
            db.LineageEdges.Add(new CatalogLineageEdge
            {
                RepoId = graph.RepoId, Flow = secondConsumerFlow, PipelineId = secondConsumerId,
                Relation = "Reads", ObjectKey = graph.ViewKey, ObjectName = $"v_{graph.Table}", Tier = "Declared",
            });

            var loadEnd = DateTime.UtcNow.AddHours(-2);
            db.Runs.Add(Run(graph.ProducerId, "pre", success: true, loadEnd.AddMinutes(-5), loadEnd));
            db.Runs.Add(Run(graph.ConsumerId, graph.ConsumerFlow, success: true, loadEnd.AddMinutes(10), loadEnd.AddMinutes(15)));
            // The second consumer's only success predates the load: it has not seen the staged rows.
            db.Runs.Add(Run(secondConsumerId, secondConsumerFlow, success: true, loadEnd.AddHours(-3), loadEnd.AddHours(-2.5)));
            await db.SaveChangesAsync();

            var verdict = await RunContextStore.ResolveLandingResetAsync(
                db, graph.RepoId, graph.ProducerId, graph.Schema, graph.Table, RunParameters.None, incrementalLanding: true, CancellationToken.None);

            Assert.NotNull(verdict);
            Assert.False(verdict.Authorized);
            Assert.Contains(secondConsumerFlow, verdict.Reason, StringComparison.Ordinal);
        }
        finally
        {
            await db.Runs.Where(r => r.PipelineId == secondConsumerId).ExecuteDeleteAsync();
            await CleanupAsync(db, graph);
        }
    }
}
