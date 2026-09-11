using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Dispatch;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The compute-task journal (<see cref="ComputeTaskStore"/>) against the real catalog database: the queued ->
/// running -> terminal lifecycle as the dispatcher journals it, the conditional hand-out, the node fence on every
/// outcome write, cancellation on both sides of the hand-out, the interrupted requeue, the dispatch-state reads,
/// and the expiry that keeps an interactive ask from hanging forever. Placement is exercised without a database in
/// the SqlFlow.Dispatch tests. The assembly runs serially (see AssemblyInfo). Each test removes its own rows by
/// source reference.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ComputeTaskStoreTests
{
    private const string Node = "test-node";

    [SkippableFact]
    public async Task Enqueue_HandOut_Complete_MovesThroughTheLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            var enqueuedAt = DateTime.UtcNow;
            var enqueued = await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, "MSSQL", "{}", RequestedBy: "tester"), enqueuedAt);
            var taskId = enqueued.TaskId;
            Assert.Equal((taskId, null, enqueuedAt), (enqueued.Placement.TaskId, enqueued.Placement.TargetPool, enqueued.Placement.EnqueuedUtc));

            var queued = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Queued, queued.Status);
            Assert.Equal("tester", queued.RequestedBy);
            Assert.Null(queued.ClaimedByNode);

            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, Node, DateTime.UtcNow));
            var running = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Running, running.Status);
            Assert.Equal(Node, running.ClaimedByNode);
            Assert.NotNull(running.StartUtc);

            // A second hand-out finds the row running, not queued.
            Assert.False(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, "other", DateTime.UtcNow));

            Assert.True(await ComputeTaskStore.RecordOutcomeAsync(
                db, taskId, Node, TaskOutcomeKind.Succeeded, null, """{"items":[]}""", DateTime.UtcNow));

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
    public async Task DispatchStateReads_CarryPoolRouting_AndHolders()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var routed = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listDatabases", sourceRef, null, "{}", TargetPool: "onprem"), DateTime.UtcNow)).TaskId;
            var running = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listDatabases", sourceRef, null, "{}"), DateTime.UtcNow)).TaskId;
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, running, Node, DateTime.UtcNow));

            var (queued, holders) = await ComputeTaskStore.LoadDispatchStateAsync(db);
            Assert.Equal("onprem", Assert.Single(queued, t => t.TaskId == routed).TargetPool);
            Assert.Equal(Node, Assert.Single(holders, h => h.Task.TaskId == running).Node);

            var (queuedIds, active) = await ComputeTaskStore.ListActiveAsync(db);
            Assert.Contains(routed, queuedIds);
            Assert.Contains(active, a => a.TaskId == running && a.Node == Node);
            Assert.Single(await ComputeTaskStore.LoadQueuedAsync(db, [routed, running]));
            Assert.Single(await ComputeTaskStore.LoadRunningAsync(db, [routed, running]));
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

            // Queued: cancelled outright, and a late hand-out is refused.
            var queuedTask = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow)).TaskId;
            Assert.Equal(CancelOutcome.Cancelled, await ComputeTaskStore.CancelAsync(db, queuedTask, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, queuedTask)).Status);
            Assert.False(await ComputeTaskStore.MarkHandedOutAsync(db, queuedTask, Node, DateTime.UtcNow));

            // Running: a durable request reconcile surfaces to the dispatcher; repeating it stays CancelRequested.
            var runningTask = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow)).TaskId;
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, runningTask, Node, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await ComputeTaskStore.CancelAsync(db, runningTask, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await ComputeTaskStore.CancelAsync(db, runningTask, DateTime.UtcNow));
            var (_, active) = await ComputeTaskStore.ListActiveAsync(db);
            Assert.Contains(active, a => a.TaskId == runningTask && a.CancelRequested);

            // The node aborts and reports cancelled under its fence.
            Assert.True(await ComputeTaskStore.RecordOutcomeAsync(db, runningTask, Node, TaskOutcomeKind.Cancelled, null, null, DateTime.UtcNow));
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
    public async Task Fence_DropsOutcomesFromAnotherNode_AndNeverOverwritesATerminalTask()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var taskId = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow)).TaskId;
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, Node, DateTime.UtcNow));

            // Another node's report (a superseded holder after a requeue) is dropped.
            Assert.False(await ComputeTaskStore.RecordOutcomeAsync(db, taskId, "other", TaskOutcomeKind.Succeeded, null, "{}", DateTime.UtcNow));
            Assert.Equal(RunStatuses.Running, (await Reload(db, taskId)).Status);

            Assert.True(await ComputeTaskStore.RecordOutcomeAsync(db, taskId, Node, TaskOutcomeKind.Failed, "the source is unreachable.", null, DateTime.UtcNow));
            var failed = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.Equal("the source is unreachable.", failed.Error);

            // A late completion or a second failure never overwrites the terminal state.
            Assert.False(await ComputeTaskStore.RecordOutcomeAsync(db, taskId, Node, TaskOutcomeKind.Succeeded, null, "{}", DateTime.UtcNow));
            Assert.False(await ComputeTaskStore.RecordOutcomeAsync(db, taskId, Node, TaskOutcomeKind.Failed, "again", null, DateTime.UtcNow));
            Assert.Equal("the source is unreachable.", (await Reload(db, taskId)).Error);
        }
        finally
        {
            await CleanupAsync(cs, sourceRef);
        }
    }

    [SkippableFact]
    public async Task RequeueInterrupted_ReturnsTheTaskToTheQueue_OrHonorsAPendingCancel()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var sourceRef = NewSourceRef();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var taskId = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), DateTime.UtcNow)).TaskId;
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, Node, DateTime.UtcNow));

            // Another node's lease is never requeued by this node's expiry.
            Assert.False(await ComputeTaskStore.RequeueInterruptedAsync(db, taskId, "other-node", DateTime.UtcNow));

            // This node's lapsed lease requeues it, clean of holder state, so it is handed out again.
            Assert.True(await ComputeTaskStore.RequeueInterruptedAsync(db, taskId, Node, DateTime.UtcNow));
            var requeued = await Reload(db, taskId);
            Assert.Equal(RunStatuses.Queued, requeued.Status);
            Assert.Null(requeued.ClaimedByNode);
            Assert.Null(requeued.StartUtc);

            // A pending operator cancel is honored instead of resurrecting the task.
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(db, taskId, Node, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await ComputeTaskStore.CancelAsync(db, taskId, DateTime.UtcNow));
            Assert.False(await ComputeTaskStore.RequeueInterruptedAsync(db, taskId, Node, DateTime.UtcNow));
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, taskId)).Status);
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
        var queuedExpiry = TimeSpan.FromMinutes(15);
        var runningExpiry = TimeSpan.FromHours(6);

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;

            // A fresh queued task and a stale one (enqueued beyond the queued expiry window).
            var fresh = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"), now)).TaskId;
            var staleQueued = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"),
                now - queuedExpiry - TimeSpan.FromMinutes(1))).TaskId;

            // A running task whose node vanished long ago (handed out beyond the running expiry window).
            var staleRunning = (await ComputeTaskStore.EnqueueAsync(
                db, new ComputeTaskEnqueueRequest("listObjects", sourceRef, null, "{}"),
                now - runningExpiry - TimeSpan.FromHours(1))).TaskId;
            Assert.True(await ComputeTaskStore.MarkHandedOutAsync(
                db, staleRunning, "dead-node", now - runningExpiry - TimeSpan.FromMinutes(30)));

            var expired = await ComputeTaskStore.ExpireAsync(db, now - queuedExpiry, now - runningExpiry, now);
            Assert.Contains(staleQueued, expired);
            Assert.Contains(staleRunning, expired);
            Assert.DoesNotContain(fresh, expired);

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
