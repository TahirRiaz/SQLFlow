using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The durable run queue (<see cref="RunQueueStore"/>) against the real catalog database: the queued -> running ->
/// terminal lifecycle, the atomic claim (two workers never get the same run), cancellation of a queued run, fail,
/// and crash recovery of orphaned running runs. The assembly runs serially (see AssemblyInfo), so the only queued
/// run during a test is the one it enqueued, which is what the "claim returns my run" assertions rely on. Each test
/// removes its own repo's rows. Gated on a reachable catalog database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunQueueStoreTests
{
    private const string Node = "test-node";

    [SkippableFact]
    public async Task Enqueue_Claim_Complete_MovesThroughTheLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            var enqueuedAt = DateTime.UtcNow;
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), enqueuedAt);

            var queued = await Reload(db, runId);
            Assert.Equal(RunStatuses.Queued, queued.Status);
            Assert.Equal(enqueuedAt, queued.EnqueuedUtc);
            Assert.Null(queued.ClaimedByNode);

            var claimed = await RunQueueStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);
            Assert.NotNull(claimed);
            Assert.Equal(runId, claimed.Value.RunId);
            // The first claim consumes the first execution attempt, and that value is the claim's fencing token.
            Assert.Equal(1, claimed.Value.Attempt);

            var running = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, running.Status);
            Assert.Equal(Node, running.ClaimedByNode);
            Assert.Equal(1, running.Attempt);
            Assert.NotNull(running.StartUtc);

            // The queue is now empty, so a second claim returns null (the run is running, not queued).
            Assert.Null(await ClaimId(db, Node, [], DateTime.UtcNow));

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(runId, flowName, success: true, rowsLoaded: 5));
            // Completion under the claim's own fence records normally.
            var recorded = await RunQueueStore.CompleteFromArtifactAsync(
                db, runId, repoId, runJson, DateTime.UtcNow, Node, claimed.Value.Attempt);
            Assert.Equal(RunCompletionOutcome.Recorded, recorded);

            var done = await Reload(db, runId);
            Assert.Equal(RunStatuses.Succeeded, done.Status);
            Assert.True(done.Success);
            Assert.Equal(5, done.RowsLoaded);
            // The completion preserved the queue-only fields set at enqueue/claim.
            Assert.Equal(enqueuedAt, done.EnqueuedUtc);
            Assert.Equal(Node, done.ClaimedByNode);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_ConcurrentClaims_NeverGiveTheSameRunToTwoWorkers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            Guid runId;
            await using (var seed = CatalogDatabase.Create(cs))
            {
                runId = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            }

            // Two independent contexts (separate connections) claim at the same time. READPAST plus the
            // single-statement claim guarantee exactly one of them gets the run; the other gets null.
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var results = await Task.WhenAll(
                RunQueueStore.ClaimNextAsync(a, "node-a", [], DateTime.UtcNow),
                RunQueueStore.ClaimNextAsync(b, "node-b", [], DateTime.UtcNow));

            Assert.Equal(1, results.Count(c => c?.RunId == runId));
            Assert.Equal(1, results.Count(c => c is null));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_SamePipeline_NeverRunsTwiceConcurrently()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var otherFlow = flowName + "_other";
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            // Two queued runs of the SAME flow (a double-trigger) plus one run of a different flow.
            var first = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            var duplicate = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            var unrelated = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, otherFlow, "ing"), DateTime.UtcNow);

            // The oldest same-flow run is claimed; its duplicate is NOT claimable while it runs (each flow
            // stages through one canonical work table, so executions must serialize), but the pipeline gate is
            // per flow: the unrelated flow's run is handed out immediately.
            Assert.Equal(first, await ClaimId(db, Node, [], DateTime.UtcNow));
            Assert.Equal(unrelated, await ClaimId(db, Node, [], DateTime.UtcNow));
            Assert.Null(await ClaimId(db, Node, [], DateTime.UtcNow));
            Assert.Equal(RunStatuses.Queued, (await Reload(db, duplicate)).Status);

            // Once the running execution reaches a terminal state, the duplicate becomes claimable.
            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(first, flowName, success: true, rowsLoaded: 1));
            Assert.Equal(RunCompletionOutcome.Recorded, await RunQueueStore.CompleteFromArtifactAsync(db, first, repoId, runJson, DateTime.UtcNow));
            Assert.Equal(duplicate, await ClaimId(db, Node, [], DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_ConcurrentClaimsOfOnePipeline_StartOnlyOneExecution()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            Guid first, duplicate;
            await using (var seed = CatalogDatabase.Create(cs))
            {
                // A double-trigger: two queued runs of one flow, exactly what a schedule firing over a still-running
                // wave produces.
                first = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
                duplicate = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            }

            // Two nodes claim at the same instant. The claim's "no running sibling" gate is a read, so both can see
            // a clear queue; the database is what keeps them apart. Whichever loses must come back empty rather than
            // start a second execution of a flow whose staging table is shared.
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var results = await Task.WhenAll(
                RunQueueStore.ClaimNextAsync(a, "node-a", [], DateTime.UtcNow),
                RunQueueStore.ClaimNextAsync(b, "node-b", [], DateTime.UtcNow));

            Assert.Equal(1, results.Count(c => c is not null));
            Assert.Equal(first, results.Single(c => c is not null)!.Value.RunId);

            await using var verify = CatalogDatabase.Create(cs);
            var pipelineId = (await Reload(verify, first)).PipelineId;
            Assert.Equal(1, await verify.Runs.CountAsync(r => r.PipelineId == pipelineId && r.Status == RunStatuses.Running));
            Assert.Equal(RunStatuses.Queued, (await Reload(verify, duplicate)).Status);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task RunningPipelineIndex_RefusesASecondRunningRunOfOnePipeline()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var first = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            var duplicate = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            Assert.Equal(first, await ClaimId(db, Node, [], DateTime.UtcNow));

            // The write the claim's gate is meant to make impossible, issued directly: the guarantee has to hold in
            // the schema, not only in the statement that normally performs it. A race that slips past the gate takes
            // exactly this shape, and the filtered unique index turns it into a duplicate-key error.
            var conflict = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE [catalog].[Run] SET [Status] = 'running' WHERE [RunId] = {0};", duplicate));
            Assert.Contains(conflict.Errors.Cast<SqlError>(), e => e.Number is 2601 or 2627);

            // The refused write left the row exactly as it was: still queued, still claimable later.
            Assert.Equal(RunStatuses.Queued, (await Reload(db, duplicate)).Status);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task ClaimNext_RoutesByPool_OnlyAnEligibleNodeClaimsATargetedRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pool = "pool_" + Guid.NewGuid().ToString("N")[..6];

        try
        {
            Guid targetedRunId;
            await using (var seed = CatalogDatabase.Create(cs))
            {
                targetedRunId = await RunQueueStore.EnqueueAsync(seed, new RunEnqueueRequest(repoId, flowName, "ing", pool), DateTime.UtcNow);
            }

            // A worker that does not serve the pool cannot claim the run (it is routed elsewhere).
            await using (var other = CatalogDatabase.Create(cs))
            {
                Assert.NotEqual(targetedRunId, await ClaimId(other, "other-node", ["a-different-pool"], DateTime.UtcNow));
            }

            // An untargeted worker cannot claim it either.
            await using (var untargeted = CatalogDatabase.Create(cs))
            {
                Assert.NotEqual(targetedRunId, await ClaimId(untargeted, "plain-node", [], DateTime.UtcNow));
            }

            // A worker serving the pool claims it.
            await using (var pooled = CatalogDatabase.Create(cs))
            {
                Assert.Equal(targetedRunId, await ClaimId(pooled, "pool-node", [pool], DateTime.UtcNow));
            }
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Cancel_QueuedRun_CancelsIt_AndItIsNeverClaimed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);

            Assert.Equal(CancelOutcome.Cancelled, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, runId)).Status);

            // A cancelled run is not queued, so the claim never returns it.
            Assert.Null(await ClaimId(db, Node, [], DateTime.UtcNow));

            // A second cancel of the same run is not cancellable; an unknown run is not found.
            Assert.Equal(CancelOutcome.NotCancellable, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.NotFound, await RunQueueStore.CancelAsync(db, Guid.NewGuid(), DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task Cancel_RunningRun_RequestsCancellation_ForTheOwningNodeToObserveAndRecord()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var node = "cancel-node-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            Assert.Equal(runId, await ClaimId(db, node, [], DateTime.UtcNow));

            // A running run cannot be cancelled out from under its worker: it is a durable request instead, stamped
            // on the row for the owning node to observe. The run stays 'running' until the node records the outcome.
            var requestedAt = DateTime.UtcNow;
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, requestedAt));
            var requested = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, requested.Status);
            Assert.Equal(requestedAt, requested.CancelRequestedUtc);

            // A second cancel is idempotent (still a pending request) and does not move the original request time.
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, requestedAt.AddSeconds(5)));
            Assert.Equal(requestedAt, (await Reload(db, runId)).CancelRequestedUtc);

            // The owning node sees exactly this run in its cancel-requested set; a different node sees nothing.
            Assert.Equal([runId], await RunQueueStore.ListCancelRequestedAsync(db, node));
            Assert.Empty(await RunQueueStore.ListCancelRequestedAsync(db, "some-other-node"));

            // After the node aborts the in-flight statement it records the run cancelled; the request then clears
            // from the set (the run is no longer 'running'), and a further cancel finds nothing to cancel.
            Assert.Equal(1, await RunQueueStore.CancelRunningAsync(db, runId, DateTime.UtcNow));
            var cancelled = await Reload(db, runId);
            Assert.Equal(RunStatuses.Cancelled, cancelled.Status);
            Assert.False(cancelled.Success);
            Assert.NotNull(cancelled.EndUtc);
            Assert.Empty(await RunQueueStore.ListCancelRequestedAsync(db, node));
            Assert.Equal(CancelOutcome.NotCancellable, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task CancelRunning_DoesNotOverwriteAnAlreadyCompletedRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await ClaimId(db, Node, [], DateTime.UtcNow);

            // The run finishes successfully in the same instant a late cancel lands: CancelRunningAsync is guarded on
            // 'running', so it updates nothing and the recorded success stands.
            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(runId, flowName, success: true, rowsLoaded: 3));
            Assert.Equal(RunCompletionOutcome.Recorded, await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            Assert.Equal(0, await RunQueueStore.CancelRunningAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Succeeded, (await Reload(db, runId)).Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task RecoverStuckRunning_RequeuesThisNodesOrphans_AndFailsOneOutOfAttempts()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var (repoId2, flowName2) = NewIds();
        var node = "recover-node-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            Assert.Equal(runId, await ClaimId(db, node, [], DateTime.UtcNow));

            // A second run that has already consumed its whole attempt budget (each claim increments Attempt; the
            // budget's exhaustion is simulated directly rather than through three real crash cycles).
            var exhaustedRun = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId2, flowName2, "ing"), DateTime.UtcNow);
            Assert.Equal(exhaustedRun, await ClaimId(db, node, [], DateTime.UtcNow));
            await db.Runs.Where(r => r.RunId == exhaustedRun)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Attempt, RunQueueStore.MaxExecutionAttempts));

            var recovered = await RunQueueStore.RecoverStuckRunningAsync(db, node, DateTime.UtcNow);
            Assert.Equal(1, recovered);

            var requeued = await Reload(db, runId);
            Assert.Equal(RunStatuses.Queued, requeued.Status);
            Assert.Null(requeued.ClaimedByNode);
            Assert.Null(requeued.StartUtc);

            var failed = await Reload(db, exhaustedRun);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.Contains("interrupted", failed.Error!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
            await Cleanup(cs, repoId2, null);
        }
    }

    [SkippableFact]
    public async Task ReapOrphanedRunning_RequeuesRunsWhoseNodeHasStoppedHeartbeating()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var deadNode = "reap-dead-" + Guid.NewGuid().ToString("N")[..8];
        var goneNode = "reap-gone-" + Guid.NewGuid().ToString("N")[..8];
        var (repoId2, flowName2) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A run claimed by a node whose last heartbeat is well before the stale cutoff (a crashed pod), and a
            // second run claimed by a node with no registry row at all (it died without its heartbeat ever landing,
            // or a Kubernetes replacement pod took a new name). Both are orphans.
            var deadRun = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(deadRun, await ClaimId(db, deadNode, [], now));
            await NodeStore.HeartbeatAsync(db, deadNode, "1.0.0", now.AddMinutes(-10));

            var goneRun = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId2, flowName2, "ing"), now);
            Assert.Equal(goneRun, await ClaimId(db, goneNode, [], now));

            // Losing a node is recoverable: both orphans go BACK TO THE QUEUE (claim cleared, ready for any
            // worker), not to failed. Nothing about the pipeline is lost.
            var staleBefore = now.AddMinutes(-3);
            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, staleBefore, now);
            Assert.True(reaped.Requeued >= 2);

            foreach (var runId in new[] { deadRun, goneRun })
            {
                var requeued = await Reload(db, runId);
                Assert.Equal(RunStatuses.Queued, requeued.Status);
                Assert.Null(requeued.ClaimedByNode);
                Assert.Null(requeued.StartUtc);
                Assert.Equal(1, requeued.Attempt); // the lost execution's attempt stays consumed
            }

            // A fresh worker claims the requeued run; the claim consumes the second attempt, which fences off any
            // late write from the first execution's zombie.
            var reclaimed = await RunQueueStore.ClaimNextAsync(db, "reap-successor", [], DateTime.UtcNow);
            Assert.NotNull(reclaimed);
            Assert.Equal(2, reclaimed.Value.Attempt);
        }
        finally
        {
            await DeleteNodes(cs, deadNode, goneNode, "reap-successor");
            await Cleanup(cs, repoId, null);
            await Cleanup(cs, repoId2, null);
        }
    }

    [SkippableFact]
    public async Task ReapOrphanedRunning_FailsARunThatExhaustedItsAttempts()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var deadNode = "reap-cap-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // The run has already consumed its whole attempt budget (a poison run that killed its node each time).
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(runId, await ClaimId(db, deadNode, [], now));
            await db.Runs.Where(r => r.RunId == runId)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Attempt, RunQueueStore.MaxExecutionAttempts));

            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), now);
            Assert.True(reaped.Failed >= 1);

            var failed = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.NotNull(failed.EndUtc);
            // The error names the node and the exhausted budget so an operator can see what happened and where.
            Assert.Contains(deadNode, failed.Error!, StringComparison.Ordinal);
            Assert.Contains("interrupted", failed.Error!, StringComparison.OrdinalIgnoreCase);

            // A second sweep no longer sees it: the run is terminal.
            var again = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), DateTime.UtcNow);
            Assert.Equal(RunStatuses.Failed, (await Reload(db, runId)).Status);
            _ = again; // other tests' orphans may exist; only this run's fate is asserted
        }
        finally
        {
            await DeleteNodes(cs, deadNode);
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task ReapOrphanedRunning_RecordsCancelled_WhenAnOperatorCancelWasPending()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var deadNode = "reap-cancel-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // The operator asked to cancel while the run executed; the node died before observing the request.
            // The cancel intent is authoritative: a requeue would resurrect work the operator explicitly killed.
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(runId, await ClaimId(db, deadNode, [], now));
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, now));

            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), now);
            Assert.True(reaped.Cancelled >= 1);

            var cancelled = await Reload(db, runId);
            Assert.Equal(RunStatuses.Cancelled, cancelled.Status);
            Assert.False(cancelled.Success);
            Assert.NotNull(cancelled.EndUtc);
        }
        finally
        {
            await DeleteNodes(cs, deadNode);
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task ClaimFence_DropsAZombiesLateWrites_AfterTheRunWasRequeuedAndReclaimed()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var zombieNode = "fence-zombie-" + Guid.NewGuid().ToString("N")[..8];
        var successorNode = "fence-successor-" + Guid.NewGuid().ToString("N")[..8];
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // The zombie's claim (attempt 1). Its node then "dies" (never heartbeats) and the reaper requeues the
            // run; a successor claims it (attempt 2) and is still executing.
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            var zombieClaim = await RunQueueStore.ClaimNextAsync(db, zombieNode, [], now);
            Assert.Equal(1, zombieClaim!.Value.Attempt);
            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), now);
            Assert.True(reaped.Requeued >= 1);
            var successorClaim = await RunQueueStore.ClaimNextAsync(db, successorNode, [], DateTime.UtcNow);
            Assert.Equal(runId, successorClaim!.Value.RunId);
            Assert.Equal(2, successorClaim.Value.Attempt);

            // The zombie was alive all along and now records its (stale) success. The fence drops the write: the
            // row still belongs to the successor's execution, untouched.
            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, RunArtifact(runId, flowName, success: true, rowsLoaded: 99));
            var outcome = await RunQueueStore.CompleteFromArtifactAsync(
                db, runId, repoId, runJson, DateTime.UtcNow, zombieNode, zombieClaim.Value.Attempt);
            Assert.Equal(RunCompletionOutcome.StaleClaim, outcome);

            var afterComplete = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, afterComplete.Status);
            Assert.Equal(successorNode, afterComplete.ClaimedByNode);
            Assert.Null(afterComplete.RowsLoaded);

            // The zombie's late fail and late cancel are dropped by the same fence.
            await RunQueueStore.FailAsync(db, runId, "zombie failure", DateTime.UtcNow, zombieNode, zombieClaim.Value.Attempt);
            Assert.Equal(RunStatuses.Running, (await Reload(db, runId)).Status);
            Assert.Equal(0, await RunQueueStore.CancelRunningAsync(db, runId, DateTime.UtcNow, zombieNode, zombieClaim.Value.Attempt));
            Assert.Equal(RunStatuses.Running, (await Reload(db, runId)).Status);

            // The successor's own fenced completion still lands: the fence blocks stale writers, not the owner.
            var successorOutcome = await RunQueueStore.CompleteFromArtifactAsync(
                db, runId, repoId, runJson, DateTime.UtcNow, successorNode, successorClaim.Value.Attempt);
            Assert.Equal(RunCompletionOutcome.Recorded, successorOutcome);
            Assert.Equal(RunStatuses.Succeeded, (await Reload(db, runId)).Status);
        }
        finally
        {
            await DeleteNodes(cs, zombieNode, successorNode);
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task ReapOrphanedRunning_LeavesRunsOfALiveNodeAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var liveNode = "reap-live-" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A busy node executing a long run still heartbeats on its independent cadence, so its last-seen stays
            // fresh. The reaper must never fail its work, however long the run has been going.
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now);
            Assert.Equal(runId, await ClaimId(db, liveNode, [], now));
            await NodeStore.HeartbeatAsync(db, liveNode, "1.0.0", now);

            var reaped = await RunQueueStore.ReapOrphanedRunningAsync(db, now.AddMinutes(-3), now);
            _ = reaped; // other tests' orphans may exist; only this run's fate is asserted

            var stillRunning = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, stillRunning.Status);
            Assert.Equal(liveNode, stillRunning.ClaimedByNode);
            Assert.Null(stillRunning.EndUtc);
        }
        finally
        {
            await DeleteNodes(cs, liveNode);
            await Cleanup(cs, repoId, null);
        }
    }

    [SkippableFact]
    public async Task Fail_DrivesARunningRunToFailed_AndIsTerminalRespecting()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await ClaimId(db, Node, [], DateTime.UtcNow);

            await RunQueueStore.FailAsync(db, runId, "boom", DateTime.UtcNow);
            var failed = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.Equal("boom", failed.Error);

            // A second fail must not overwrite the recorded terminal state.
            await RunQueueStore.FailAsync(db, runId, "second", DateTime.UtcNow);
            Assert.Equal("boom", (await Reload(db, runId)).Error);
        }
        finally
        {
            await Cleanup(cs, repoId, null);
        }
    }

    [Fact]
    public void RunStatements_ProjectsTheErrorOntoTheFailingStatementOnly()
    {
        var runId = Guid.NewGuid();
        var repoId = Guid.NewGuid();
        using var doc = JsonDocument.Parse(FailedArtifactWithTrace(runId, "rq_flow"));

        var statements = CatalogProjection.RunStatements(doc.RootElement, runId, repoId);

        Assert.Equal(2, statements.Count);
        Assert.Equal(1, statements[0].Ordinal);
        Assert.Equal("staging.create", statements[0].Step);
        Assert.Null(statements[0].Error);
        Assert.Equal(2, statements[1].Ordinal);
        Assert.Equal("upsert.insert", statements[1].Step);
        Assert.Equal("Cannot insert duplicate key", statements[1].Error);
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_KeepsACompleteLiveTraceUnderStableIds_AppendingNothing()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await ClaimId(db, Node, [], DateTime.UtcNow);

            // The node's live feed captured the run's whole trace as it executed: the same rows, ordinals, and
            // failure attribution the artifact carries. The trace stream has already delivered these under their
            // ids, so completion must keep them exactly - not delete and re-issue them under fresh ids.
            var liveStatements = new[]
            {
                new CatalogRunStatement
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, Step = "staging.create", Sql = "CREATE TABLE #s;",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 58, DateTimeKind.Utc),
                },
                new CatalogRunStatement
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, Step = "upsert.insert", Sql = "INSERT INTO t;",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 59, DateTimeKind.Utc),
                    Error = "Cannot insert duplicate key",
                },
            };
            var liveEvents = new[]
            {
                new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 1, Level = "info", Step = "incremental",
                    Message = "watermark resolved to 2026-06-18",
                    TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 57, DateTimeKind.Utc),
                },
                new CatalogRunEvent
                {
                    RunId = runId, RepoId = repoId, Ordinal = 2, Level = "error", Step = "upsert.insert",
                    Message = "run failed: Cannot insert duplicate key",
                    TimestampUtc = new DateTime(2026, 6, 19, 10, 0, 0, DateTimeKind.Utc),
                },
            };
            db.RunStatements.AddRange(liveStatements);
            db.RunEvents.AddRange(liveEvents);
            await db.SaveChangesAsync();
            var liveStatementIds = liveStatements.Select(s => s.Id).ToArray();
            var liveEventIds = liveEvents.Select(e => e.Id).ToArray();

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, FailedArtifactWithTrace(runId, flowName));
            Assert.Equal(RunCompletionOutcome.Recorded, await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            // No duplication and no re-issue: the very same live rows remain, under the very same ids.
            var statements = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal(liveStatementIds, statements.Select(s => s.Id).ToArray());
            Assert.Equal("Cannot insert duplicate key", statements[1].Error);

            var events = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId).OrderBy(e => e.Ordinal).ToListAsync();
            Assert.Equal(liveEventIds, events.Select(e => e.Id).ToArray());

            var run = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, run.Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_AppendsOnlyTheTailWhenALiveFeedBrokeMidRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var dir = NewTempDir();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow);
            await ClaimId(db, Node, [], DateTime.UtcNow);

            // The live feed broke after the first statement and the first event: only the prefix reached the
            // catalog. Completion must preserve that prefix under its ids and append only the missing tail.
            var liveStatement = new CatalogRunStatement
            {
                RunId = runId, RepoId = repoId, Ordinal = 1, Step = "staging.create", Sql = "CREATE TABLE #s;",
                TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 58, DateTimeKind.Utc),
            };
            var liveEvent = new CatalogRunEvent
            {
                RunId = runId, RepoId = repoId, Ordinal = 1, Level = "info", Step = "incremental",
                Message = "watermark resolved to 2026-06-18",
                TimestampUtc = new DateTime(2026, 6, 19, 9, 59, 57, DateTimeKind.Utc),
            };
            db.RunStatements.Add(liveStatement);
            db.RunEvents.Add(liveEvent);
            await db.SaveChangesAsync();
            var liveStatementId = liveStatement.Id;
            var liveEventId = liveEvent.Id;

            var runJson = Path.Combine(dir, "run.json");
            await File.WriteAllTextAsync(runJson, FailedArtifactWithTrace(runId, flowName));
            Assert.Equal(RunCompletionOutcome.Recorded, await RunQueueStore.CompleteFromArtifactAsync(db, runId, repoId, runJson, DateTime.UtcNow));

            // The whole trace is present, exactly once: the prefix under its original id, the tail newly appended.
            var statements = await db.RunStatements.AsNoTracking()
                .Where(s => s.RunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal(2, statements.Count);
            Assert.Equal(liveStatementId, statements[0].Id);
            Assert.NotEqual(liveStatementId, statements[1].Id);
            Assert.Equal("INSERT INTO t;", statements[1].Sql);
            Assert.Equal("Cannot insert duplicate key", statements[1].Error);

            var events = await db.RunEvents.AsNoTracking()
                .Where(e => e.RunId == runId).OrderBy(e => e.Ordinal).ToListAsync();
            Assert.Equal(2, events.Count);
            Assert.Equal(liveEventId, events[0].Id);
            Assert.NotEqual(liveEventId, events[1].Id);
            Assert.Equal("error", events[1].Level);
            Assert.Equal("run failed: Cannot insert duplicate key", events[1].Message);

            var run = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, run.Status);
        }
        finally
        {
            await Cleanup(cs, repoId, dir);
        }
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("rq_" + suffix), "rq_flow_" + suffix);
    }

    /// <summary>Claims the next eligible run and returns just its id (null when nothing is claimable), for the many
    /// assertions that only care WHICH run was handed out. Tests exercising the fencing token call
    /// <see cref="RunQueueStore.ClaimNextAsync"/> directly and keep the whole claim.</summary>
    private static async Task<Guid?> ClaimId(CatalogDbContext db, string node, IReadOnlyList<string> pools, DateTime nowUtc)
        => (await RunQueueStore.ClaimNextAsync(db, node, pools, nowUtc))?.RunId;

    private static async Task<CatalogRun> Reload(CatalogDbContext db, Guid runId)
    {
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.RunId == runId);
        Assert.NotNull(run);
        return run;
    }

    private static string RunArtifact(Guid runId, string flowName, bool success, long rowsLoaded)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": {{(success ? "true" : "false")}},
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": { "rowsLoaded": {{rowsLoaded}}, "durationSeconds": 1.0 }
            }
            """;

    // A failed ingestion artifact whose SQL trace attributes the failure to the second statement (the upsert
    // insert), mirroring a real duplicate-key failure: the projection stamps its Error onto that entry only.
    // Carries a canonical events array too, so the completion's event reconciliation is exercised alongside.
    private static string FailedArtifactWithTrace(Guid runId, string flowName)
        => $$"""
            {
              "schemaVersion": 1,
              "flowKind": "ing",
              "flowName": "{{flowName}}",
              "runId": "{{runId}}",
              "success": false,
              "error": "Cannot insert duplicate key",
              "writtenUtc": "2026-06-19T10:00:00Z",
              "result": {
                "durationSeconds": 2.0,
                "sqlTrace": [
                  { "sequence": 1, "timestampUtc": "2026-06-19T09:59:58Z", "step": "staging.create", "sql": "CREATE TABLE #s;" },
                  { "sequence": 2, "timestampUtc": "2026-06-19T09:59:59Z", "step": "upsert.insert", "sql": "INSERT INTO t;", "error": "Cannot insert duplicate key" }
                ]
              },
              "events": [
                { "timestampUtc": "2026-06-19T09:59:57Z", "level": "info", "step": "incremental", "message": "watermark resolved to 2026-06-18" },
                { "timestampUtc": "2026-06-19T10:00:00Z", "level": "error", "step": "upsert.insert", "message": "run failed: Cannot insert duplicate key" }
              ]
            }
            """;

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_rq_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task DeleteNodes(string cs, params string[] names)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.Nodes.Where(n => names.Contains(n.Name)).ExecuteDeleteAsync();
    }

    private static async Task Cleanup(string cs, Guid repoId, string? dir = null)
    {
        await using (var db = CatalogDatabase.Create(cs))
        {
            await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunSurrogateKeys.Where(k => k.RepoId == repoId).ExecuteDeleteAsync();
            await db.RunHealthCheckMetrics.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
            await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
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
