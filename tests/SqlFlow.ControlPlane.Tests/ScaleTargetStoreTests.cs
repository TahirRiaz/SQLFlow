using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The one definition of a pool's replica target (<see cref="ScaleTargetStore"/>), the number the autoscaler holds
/// the pool at, against the real catalog: only the eligible backlog counts (a run behind a busy pipeline asks for
/// no node), it is divided by what one node of the pool executes at once (as the nodes report it, with the runtime
/// default when none has), busy online nodes each hold a replica while a silent node's last busy count pins
/// nothing, and the always-on floor and the manual override combine with the demand as a max. Gated on a
/// reachable catalog database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ScaleTargetStoreTests
{
    [SkippableFact]
    public async Task Resolve_DividesTheEligibleBacklogByANodesSlots_AddsBusyNodes_AndAppliesFloorAndOverride()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var repoId = FlowIdentity.FromName("st_" + suffix);
        var pool = "st-" + suffix;
        var nobody = "st-nobody-" + suffix;
        string[] nodes = [$"st-busy-{suffix}", $"st-idle-{suffix}", $"st-dead-{suffix}"];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // Five runs of distinct pipelines queued for the pool; the first is handed out (its pipeline is now busy)
            // and enqueued again behind itself: four queued runs are eligible, one is gated.
            var ids = new List<Guid>();
            for (var i = 0; i < 5; i++)
            {
                ids.Add((await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, $"st_flow_{i}_{suffix}", "ing", pool), now)).RunId);
            }

            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, ids[0], 0, nodes[0], now));
            await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, $"st_flow_0_{suffix}", "ing", pool), now);

            // The fleet: a busy node and an idle one, both with four slots, and a node that was busy when it went
            // silent two minutes ago (a dead node's last busy count must pin nothing).
            await NodeStore.HeartbeatAsync(db, nodes[0], "t", now, pool, busyRuns: 1, runSlots: 4);
            await NodeStore.HeartbeatAsync(db, nodes[1], "t", now, pool, busyRuns: 0, runSlots: 4);
            await NodeStore.HeartbeatAsync(db, nodes[2], "t", now.AddMinutes(-2), pool, busyRuns: 3, runSlots: 4);

            var target = await ScaleTargetStore.ResolveAsync(db, pool, 4, now);
            Assert.Equal(pool, target.Pool);
            Assert.Equal((4, 1, 2, 4), (target.EligibleQueuedRuns, target.BusyNodes, target.OnlineNodes, target.RunSlotsPerNode));
            Assert.Equal(2, target.Replicas); // ceil(4 eligible / 4 slots) + 1 busy node
            Assert.Equal((0, 0, false), (target.MinReplicas, target.ManualReplicas, target.ManualActive));

            // Nodes that execute two at once double the demand term; the divisor is what the fleet reports.
            await NodeStore.HeartbeatAsync(db, nodes[0], "t", now, pool, busyRuns: 1, runSlots: 2);
            await NodeStore.HeartbeatAsync(db, nodes[1], "t", now, pool, busyRuns: 0, runSlots: 2);
            Assert.Equal(3, (await ScaleTargetStore.ResolveAsync(db, pool, 4, now)).Replicas); // ceil(4 / 2) + 1

            // The floor and the manual override combine with the demand as a max, and the terms come back beside
            // the answer so the fleet page can explain it.
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 5, manualReplicas: 0, manualUntilUtc: null, updatedBy: "t", nowUtc: now);
            Assert.Equal(5, (await ScaleTargetStore.ResolveAsync(db, pool, 4, now)).Replicas);
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 0, manualReplicas: 7, manualUntilUtc: now.AddMinutes(5), updatedBy: "t", nowUtc: now);
            var manual = await ScaleTargetStore.ResolveAsync(db, pool, 4, now);
            Assert.Equal((7, true, 7), (manual.Replicas, manual.ManualActive, manual.ManualReplicas));
            await WorkerPoolStore.SaveScaleAsync(db, pool, minReplicas: 0, manualReplicas: 7, manualUntilUtc: now.AddMinutes(-1), updatedBy: "t", nowUtc: now);
            var lapsed = await ScaleTargetStore.ResolveAsync(db, pool, 4, now);
            Assert.Equal((3, false), (lapsed.Replicas, lapsed.ManualActive)); // the override's window closed: demand again

            // A pool nobody has ever served: the runtime default sizes it, and nothing queued means nothing asked for.
            var empty = await ScaleTargetStore.ResolveAsync(db, nobody, 4, now);
            Assert.Equal((0, 0, 0, 0, 4), (empty.Replicas, empty.EligibleQueuedRuns, empty.BusyNodes, empty.OnlineNodes, empty.RunSlotsPerNode));
        }
        finally
        {
            await using var db = CatalogDatabase.Create(cs);
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
            await db.Nodes.Where(n => nodes.Contains(n.Name)).ExecuteDeleteAsync();
            await db.WorkerPools.Where(p => p.Pool == pool || p.Pool == nobody).ExecuteDeleteAsync();
        }
    }
}
