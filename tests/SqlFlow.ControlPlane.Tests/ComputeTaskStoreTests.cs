using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The durable compute-task queue (<see cref="ComputeTaskStore"/>) against the real catalog database: the
/// queued -> running -> terminal lifecycle, the atomic claim (two workers never get the same task), pool
/// routing, cancellation on both sides of the claim, crash recovery, and the lazy expiry that keeps an
/// interactive ask from hanging forever. The assembly runs serially (see AssemblyInfo), so the only queued
/// tasks during a test are the ones it enqueued. Each test removes its own rows by source reference.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ComputeTaskStoreTests
{
    private const string Node = "test-node";

    [SkippableFact]
    public async Task Enqueue_Claim_Complete_MovesThroughTheLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            var enqueuedAt = DateTime.UtcNow;
            var taskId = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, "MSSQL", "{}", RequestedBy: "tester"), enqueuedAt);

            var queued = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Queued, queued.Status);
            Assert.Equal("tester", queued.RequestedBy);
            Assert.Null(queued.ClaimedByNode);

            var claimed = await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow);
            Assert.Equal(taskId, claimed);

            var running = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Running, running.Status);
            Assert.Equal(Node, running.ClaimedByNode);
            Assert.NotNull(running.StartUtc);

            // A second claim finds nothing: the only queued task is already running.
            Assert.Null(await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));

            var recorded = await ComputeTaskStore.CompleteAsync(db, taskId, """{"items":[]}""", DateTime.UtcNow);
            Assert.True(recorded);

            var succeeded = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Succeeded, succeeded.Status);
            Assert.Equal("""{"items":[]}""", succeeded.ResultJson);
            Assert.Null(succeeded.Error);
            Assert.NotNull(succeeded.EndUtc);
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task Claim_HonorsPoolRouting()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var taskId = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listDatabases", sourceRef, null, "{}", TargetPool: "onprem"), DateTime.UtcNow);

            // A node with no pools (or the wrong pool) never claims a routed task.
            Assert.Null(await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
            Assert.Null(await ComputeTaskStore.ClaimNextAsync(db, Node, ["cloud"], DateTime.UtcNow));

            // A node serving the pool claims it.
            Assert.Equal(taskId, await ComputeTaskStore.ClaimNextAsync(db, Node, ["cloud", "onprem"], DateTime.UtcNow));
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task Cancel_QueuedIsImmediate_RunningIsARequest_TerminalIsRefused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            // Queued: cancelled outright.
            var queuedTask = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow);
            Assert.Equal(CancelOutcome.Cancelled, await ComputeTaskStore.CancelAsync(db, queuedTask, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, queuedTask)).Status);

            // Running: a durable request the owning node observes; repeating it stays CancelRequested.
            var runningTask = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow);
            Assert.Equal(runningTask, await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await ComputeTaskStore.CancelAsync(db, runningTask, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await ComputeTaskStore.CancelAsync(db, runningTask, DateTime.UtcNow));
            Assert.Contains(runningTask, await ComputeTaskStore.ListCancelRequestedAsync(db, Node));

            // The node aborts and records cancelled.
            Assert.Equal(1, await ComputeTaskStore.CancelRunningAsync(db, runningTask, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, runningTask)).Status);

            // Terminal: nothing to cancel; unknown: not found.
            Assert.Equal(CancelOutcome.NotCancellable, await ComputeTaskStore.CancelAsync(db, runningTask, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.NotFound, await ComputeTaskStore.CancelAsync(db, Guid.CreateVersion7(), DateTime.UtcNow));
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task Fail_RecordsTheError_AndNeverOverwritesATerminalTask()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var taskId = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow);
            Assert.Equal(taskId, await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));

            Assert.Equal(1, await ComputeTaskStore.FailAsync(db, taskId, "the source is unreachable.", DateTime.UtcNow));
            var failed = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.Equal("the source is unreachable.", failed.Error);

            // A late completion or a second failure never overwrites the terminal state.
            Assert.False(await ComputeTaskStore.CompleteAsync(db, taskId, "{}", DateTime.UtcNow));
            Assert.Equal(0, await ComputeTaskStore.FailAsync(db, taskId, "again", DateTime.UtcNow));
            Assert.Equal("the source is unreachable.", (await Reload(db, taskId)).Error);
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task Recovery_RequeuesThisNodesRunningTasks()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var taskId = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow);
            Assert.Equal(taskId, await ComputeTaskStore.ClaimNextAsync(db, Node, [], DateTime.UtcNow));

            // Another node's recovery leaves this node's task alone.
            Assert.Equal(0, await ComputeTaskStore.RecoverStuckRunningAsync(db, "other-node"));

            // This node's restart requeues it, clean of claim state, so it is claimable again.
            Assert.Equal(1, await ComputeTaskStore.RecoverStuckRunningAsync(db, Node));
            var requeued = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Queued, requeued.Status);
            Assert.Null(requeued.ClaimedByNode);
            Assert.Null(requeued.StartUtc);
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task Expiry_FailsStaleQueuedAndRunningTasks_AndLeavesFreshOnesAlone()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A fresh queued task and a stale one (enqueued beyond the queued expiry window).
            var fresh = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), now);
            var staleQueued = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"),
                now - ComputeTaskStore.QueuedExpiry - TimeSpan.FromMinutes(1));

            // A running task whose node vanished long ago (claimed beyond the running expiry window).
            var staleRunning = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"),
                now - ComputeTaskStore.RunningExpiry - TimeSpan.FromHours(1));
            Assert.Equal(staleRunning, await ComputeTaskStore.ClaimNextAsync(
                db, "dead-node", [], now - ComputeTaskStore.RunningExpiry - TimeSpan.FromMinutes(30)));

            var expired = await ComputeTaskStore.ExpireAsync(db, now);
            Assert.Equal(2, expired);

            Assert.Equal(RunStatuses.Queued, (await Reload(db, fresh)).Status);
            var expiredQueued = await Reload(db, staleQueued);
            Assert.Equal(RunStatuses.Failed, expiredQueued.Status);
            Assert.Contains("No worker claimed the task", expiredQueued.Error, StringComparison.Ordinal);
            var expiredRunning = await Reload(db, staleRunning);
            Assert.Equal(RunStatuses.Failed, expiredRunning.Status);
            Assert.Contains("presumed", expiredRunning.Error, StringComparison.Ordinal);
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    private static string NewSourceRef() => "${env:CP_CT_" + Guid.NewGuid().ToString("N")[..8] + "}";

    private static async Task<CatalogComputeTask> Reload(CatalogDbContext db, Guid taskId)
        => await db.ComputeTasks.AsNoTracking().SingleAsync(t => t.TaskId == taskId);

    private static async Task CleanupAsync(string cs, string sourceRef)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.ComputeTasks.Where(t => t.SourceRef == sourceRef).ExecuteDeleteAsync();
    }
}
