using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The run journal (<see cref="RunQueueStore"/>) against the real catalog database: the queued -> running ->
/// terminal lifecycle as the dispatcher journals it, the hand-out write that is conditional on the row's status
/// and attempt, the (node, attempt) fence on every outcome write, the dispositions of an interrupted run, the
/// dispatch-state reads, cancellation on both sides of the hand-out, and the artifact projection. Placement itself
/// is exercised without a database in the SqlFlow.Dispatch tests; this suite proves the journal keeps every
/// promise the dispatcher relies on. The assembly runs serially (see AssemblyInfo). Each test removes its own
/// repo's rows. Gated on a reachable catalog database, like the other DB-backed tests.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunQueueStoreTests
{
    private const string Node = "test-node";

    [SkippableFact]
    public async Task Enqueue_HandOut_Complete_MovesThroughTheLifecycle()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            var enqueuedAt = DateTime.UtcNow;
            var enqueued = await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), enqueuedAt);
            var runId = enqueued.RunId;
            // The placement row the dispatcher receives mirrors the journaled row exactly.
            Assert.Equal((runId, CatalogIdentity.Pipeline(repoId, flowName), null, 0, false),
                (enqueued.Placement.RunId, enqueued.Placement.PipelineId, enqueued.Placement.TargetPool, enqueued.Placement.Attempt, enqueued.Placement.CancelRequested));

            var queued = await Reload(db, runId);
            Assert.Equal(RunStatuses.Queued, queued.Status);
            Assert.Equal(enqueuedAt, queued.EnqueuedUtc);
            Assert.Null(queued.ClaimedByNode);

            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

            var running = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, running.Status);
            Assert.Equal(Node, running.ClaimedByNode);
            Assert.Equal(1, running.Attempt);
            Assert.NotNull(running.StartUtc);

            // A second hand-out of the same row is refused: it is running, not queued.
            Assert.Null(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, "other", DateTime.UtcNow));
            Assert.Null(await RunQueueStore.MarkHandedOutAsync(db, runId, 1, "other", DateTime.UtcNow));

            var recorded = await RunQueueStore.RecordOutcomeAsync(
                db, runId, Node, 1, RunOutcomeKind.Completed, null, RunArtifact(runId, flowName, success: true, rowsLoaded: 5), DateTime.UtcNow);
            Assert.Equal(RunOutcomeStatus.Recorded, recorded.Status);

            var done = await Reload(db, runId);
            Assert.Equal(RunStatuses.Succeeded, done.Status);
            Assert.True(done.Success);
            Assert.Equal(5, done.RowsLoaded);
            // The completion preserved the queue-only fields set at enqueue and hand-out.
            Assert.Equal(enqueuedAt, done.EnqueuedUtc);
            Assert.Equal(Node, done.ClaimedByNode);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task MarkHandedOut_IsConditionalOnTheExpectedAttempt_SoAStaleDecisionNeverLands()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;

            // A dispatcher whose memory is behind (it believes the run is still at attempt 0 after a requeue
            // advanced it) cannot hand it out on that stale knowledge.
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));
            Assert.True((await RunQueueStore.RequeueInterruptedAsync(db, runId, Node, 1, DateTime.UtcNow)).Applied);
            Assert.Null(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 1, Node, DateTime.UtcNow));
            Assert.Equal(2, (await Reload(db, runId)).Attempt);

            // Two dispatchers racing for one row (an ownership overlap): exactly one write applies.
            var second = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_b", "ing"), DateTime.UtcNow)).RunId;
            await using var a = CatalogDatabase.Create(cs);
            await using var b = CatalogDatabase.Create(cs);
            var results = await Task.WhenAll(
                RunQueueStore.MarkHandedOutAsync(a, second, 0, "node-a", DateTime.UtcNow),
                RunQueueStore.MarkHandedOutAsync(b, second, 0, "node-b", DateTime.UtcNow));
            Assert.Equal(1, results.Count(r => r is not null));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Cancel_QueuedRun_CancelsIt_AndAHandOutIsThenRefused()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;

            Assert.Equal(CancelOutcome.Cancelled, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            var cancelled = await Reload(db, runId);
            Assert.Equal(RunStatuses.Cancelled, cancelled.Status);
            Assert.NotNull(cancelled.EndUtc);

            // The dispatcher's hand-out write finds the row no longer queued and drops it.
            Assert.Null(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.NotCancellable, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.NotFound, await RunQueueStore.CancelAsync(db, Guid.CreateVersion7(), DateTime.UtcNow));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Cancel_RunningRun_StampsARequest_WhichTheDispatchReadsSurface()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            // Idempotent: a second click does not move the stamp or change the answer.
            var first = (await Reload(db, runId)).CancelRequestedUtc;
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, runId, DateTime.UtcNow));
            Assert.Equal(first, (await Reload(db, runId)).CancelRequestedUtc);

            // Reconcile sees the flag on the running row, so a cancel stamped straight against the journal (the
            // break-glass CLI path) still reaches the node.
            var (_, running) = await RunQueueStore.ListActiveAsync(db);
            Assert.Contains(running, r => r.RunId == runId && r.Node == Node && r.Attempt == 1 && r.CancelRequested);

            // The node aborts and reports cancelled under its fence.
            var outcome = await RunQueueStore.RecordOutcomeAsync(db, runId, Node, 1, RunOutcomeKind.Cancelled, null, null, DateTime.UtcNow);
            Assert.Equal(RunOutcomeStatus.Recorded, outcome.Status);
            var cancelled = await Reload(db, runId);
            Assert.Equal(RunStatuses.Cancelled, cancelled.Status);
            Assert.False(cancelled.Success);
            Assert.NotNull(cancelled.EndUtc);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task Fence_DropsEveryOutcomeWriteFromASupersededHolder()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        const string zombie = "fence-zombie";
        const string successor = "fence-successor";

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;

            // The zombie's hand-out (attempt 1), its lease lapses and the dispatcher requeues; a successor is
            // handed the run (attempt 2) and is still executing.
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, zombie, DateTime.UtcNow));
            Assert.True((await RunQueueStore.RequeueInterruptedAsync(db, runId, zombie, 1, DateTime.UtcNow)).Applied);
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 1, successor, DateTime.UtcNow));

            // The zombie was alive all along and now reports. Every kind of report is dropped by the fence.
            var artifact = RunArtifact(runId, flowName, success: true, rowsLoaded: 99);
            Assert.Equal(RunOutcomeStatus.StaleClaim, (await RunQueueStore.RecordOutcomeAsync(
                db, runId, zombie, 1, RunOutcomeKind.Completed, null, artifact, DateTime.UtcNow)).Status);
            Assert.Equal(RunOutcomeStatus.StaleClaim, (await RunQueueStore.RecordOutcomeAsync(
                db, runId, zombie, 1, RunOutcomeKind.Failed, "zombie failure", null, DateTime.UtcNow)).Status);
            Assert.Equal(RunOutcomeStatus.StaleClaim, (await RunQueueStore.RecordOutcomeAsync(
                db, runId, zombie, 1, RunOutcomeKind.Cancelled, null, null, DateTime.UtcNow)).Status);
            // So are the interrupted dispositions for the zombie's lease.
            Assert.False((await RunQueueStore.RequeueInterruptedAsync(db, runId, zombie, 1, DateTime.UtcNow)).Applied);
            Assert.False((await RunQueueStore.FailInterruptedAsync(db, runId, zombie, 1, DateTime.UtcNow)).Applied);
            Assert.False((await RunQueueStore.CancelInterruptedAsync(db, runId, zombie, 1, DateTime.UtcNow)).Applied);

            var untouched = await Reload(db, runId);
            Assert.Equal(RunStatuses.Running, untouched.Status);
            Assert.Equal(successor, untouched.ClaimedByNode);
            Assert.Equal(2, untouched.Attempt);
            Assert.Null(untouched.RowsLoaded);

            // The successor's own fenced completion still lands: the fence blocks stale writers, not the owner.
            Assert.Equal(RunOutcomeStatus.Recorded, (await RunQueueStore.RecordOutcomeAsync(
                db, runId, successor, 2, RunOutcomeKind.Completed, null, artifact, DateTime.UtcNow)).Status);
            Assert.Equal(RunStatuses.Succeeded, (await Reload(db, runId)).Status);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task InterruptedDispositions_RequeueKeepsTheAttempt_FailNamesTheNode_CancelHonorsTheOperator()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        const string deadNode = "dead-node";

        try
        {
            await using var db = CatalogDatabase.Create(cs);

            // Requeue: back to queued with the holder cleared and the consumed attempt kept.
            var requeued = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_r", "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, requeued, 0, deadNode, DateTime.UtcNow));
            Assert.True((await RunQueueStore.RequeueInterruptedAsync(db, requeued, deadNode, 1, DateTime.UtcNow)).Applied);
            var row = await Reload(db, requeued);
            Assert.Equal((RunStatuses.Queued, (string?)null, (DateTime?)null, 1), (row.Status, row.ClaimedByNode, row.StartUtc, row.Attempt));

            // Requeue refuses a run past its attempt budget, and the fail disposition names the node.
            var exhausted = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_x", "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, exhausted, 0, deadNode, DateTime.UtcNow));
            await db.Runs.Where(r => r.RunId == exhausted)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Attempt, RunQueueStore.MaxExecutionAttempts));
            Assert.False((await RunQueueStore.RequeueInterruptedAsync(db, exhausted, deadNode, RunQueueStore.MaxExecutionAttempts, DateTime.UtcNow)).Applied);
            Assert.True((await RunQueueStore.FailInterruptedAsync(db, exhausted, deadNode, RunQueueStore.MaxExecutionAttempts, DateTime.UtcNow)).Applied);
            var failed = await Reload(db, exhausted);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.Contains(deadNode, failed.Error!, StringComparison.Ordinal);
            Assert.Contains("interrupted", failed.Error!, StringComparison.OrdinalIgnoreCase);

            // Cancel: an operator's pending cancel is honored, never resurrected.
            var cancelled = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_c", "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, cancelled, 0, deadNode, DateTime.UtcNow));
            Assert.Equal(CancelOutcome.CancelRequested, await RunQueueStore.CancelAsync(db, cancelled, DateTime.UtcNow));
            Assert.False((await RunQueueStore.RequeueInterruptedAsync(db, cancelled, deadNode, 1, DateTime.UtcNow)).Applied);
            Assert.True((await RunQueueStore.CancelInterruptedAsync(db, cancelled, deadNode, 1, DateTime.UtcNow)).Applied);
            Assert.Equal(RunStatuses.Cancelled, (await Reload(db, cancelled)).Status);

            // Every disposition is terminal-respecting: a second pass changes nothing.
            Assert.False((await RunQueueStore.FailInterruptedAsync(db, exhausted, deadNode, RunQueueStore.MaxExecutionAttempts, DateTime.UtcNow)).Applied);
            Assert.False((await RunQueueStore.CancelInterruptedAsync(db, cancelled, deadNode, 1, DateTime.UtcNow)).Applied);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task DispatchStateReads_ReturnQueuedPlacements_RunningHolders_AndOnlyThoseStillActive()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pool = "pool_" + Guid.NewGuid().ToString("N")[..6];

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var queued = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing", pool), DateTime.UtcNow)).RunId;
            var running = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_b", "ing"), DateTime.UtcNow)).RunId;
            var done = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName + "_c", "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, running, 0, Node, DateTime.UtcNow));
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, done, 0, Node, DateTime.UtcNow));
            await RunQueueStore.RecordOutcomeAsync(db, done, Node, 1, RunOutcomeKind.Failed, "boom", null, DateTime.UtcNow);

            var (queuedRows, runningRows) = await RunQueueStore.LoadDispatchStateAsync(db);
            var placement = Assert.Single(queuedRows, r => r.RunId == queued);
            Assert.Equal((pool, 0, false), (placement.TargetPool, placement.Attempt, placement.CancelRequested));
            var holder = Assert.Single(runningRows, r => r.Run.RunId == running);
            Assert.Equal((Node, 1), (holder.Node, holder.Run.Attempt));
            Assert.DoesNotContain(queuedRows, r => r.RunId == done);
            Assert.DoesNotContain(runningRows, r => r.Run.RunId == done);

            var (queuedIds, active) = await RunQueueStore.ListActiveAsync(db);
            Assert.Contains(queued, queuedIds);
            Assert.DoesNotContain(running, queuedIds);
            Assert.Contains(active, a => a.RunId == running && a.Node == Node && a.Attempt == 1 && !a.CancelRequested);

            // Targeted loads answer only for rows still in the asked-for state.
            Assert.Single(await RunQueueStore.LoadQueuedAsync(db, [queued, running, done]));
            Assert.Single(await RunQueueStore.LoadRunningAsync(db, [queued, running, done]));
            Assert.Empty(await RunQueueStore.LoadQueuedAsync(db, []));
        }
        finally
        {
            await Cleanup(cs, repoId);
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
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

            var result = await RunQueueStore.FailAsync(db, runId, "boom", DateTime.UtcNow);
            Assert.True(result.Applied);
            var failed = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.False(failed.Success);
            Assert.Equal("boom", failed.Error);

            // A second fail must not overwrite the recorded terminal state.
            Assert.False((await RunQueueStore.FailAsync(db, runId, "second", DateTime.UtcNow)).Applied);
            Assert.Equal("boom", (await Reload(db, runId)).Error);
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_AnUnreadableArtifact_FailsTheRunSoItNeverLingers()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

            var outcome = await RunQueueStore.RecordOutcomeAsync(
                db, runId, Node, 1, RunOutcomeKind.Completed, null, "{ not json", DateTime.UtcNow);

            Assert.Equal(RunOutcomeStatus.ArtifactUnreadable, outcome.Status);
            var failed = await Reload(db, runId);
            Assert.Equal(RunStatuses.Failed, failed.Status);
            Assert.Contains("could not be recorded", failed.Error!, StringComparison.Ordinal);
        }
        finally
        {
            await Cleanup(cs, repoId);
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

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

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

            Assert.Equal(RunOutcomeStatus.Recorded, (await RunQueueStore.CompleteFromArtifactAsync(
                db, runId, FailedArtifactWithTrace(runId, flowName), DateTime.UtcNow, Node, 1)).Status);

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
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task CompleteFromArtifact_AppendsOnlyTheTailWhenALiveFeedBrokeMidRun()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), DateTime.UtcNow)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, DateTime.UtcNow));

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

            Assert.Equal(RunOutcomeStatus.Recorded, (await RunQueueStore.CompleteFromArtifactAsync(
                db, runId, FailedArtifactWithTrace(runId, flowName), DateTime.UtcNow, Node, 1)).Status);

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
            await Cleanup(cs, repoId);
        }
    }

    [SkippableFact]
    public async Task MarkHandedOut_ReturnsTheJoinedSpec_AndNullsForRowsThatLeftTheCatalog()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();
        var pipelineId = CatalogIdentity.Pipeline(repoId, flowName);
        var repoName = "rq_repo_" + flowName;

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            db.Repos.Add(new CatalogRepo { Id = repoId, Name = repoName, RootPath = @"C:\estate\pipelines", RemoteUrl = "https://git.invalid/pipelines.git", FirstSeenUtc = now, LastSyncUtc = now });
            db.Pipelines.Add(new CatalogPipeline
            {
                Id = pipelineId, RepoId = repoId, Name = flowName, Kind = "ing", RelativePath = "flows/" + flowName + ".flow.yaml",
                ContentHash = string.Empty, Yaml = string.Empty, DefinitionJson = "{}", Active = true, Wave = 0, FirstSeenUtc = now, LastSeenUtc = now,
            });
            db.RepoSources.Add(new CatalogRepoSource
            {
                Id = Guid.NewGuid(), Name = repoName, RemoteUrl = "https://git.invalid/pipelines.git", Branch = "main",
                CredentialReference = "${keyvault:vault/git-token}", CredentialUsername = "x-token-auth",
                LastSyncedSha = "0123456789abcdef0123456789abcdef01234567", CreatedUtc = now, UpdatedUtc = now,
            });
            await db.SaveChangesAsync();

            var parameters = new RunParameters { FullLoad = true };
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing", Parameters: parameters), now)).RunId;
            var spec = await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, now);

            Assert.NotNull(spec);
            Assert.Equal((repoId, pipelineId, flowName), (spec.RepoId, spec.PipelineId, spec.FlowName));
            Assert.Equal((repoName, "https://git.invalid/pipelines.git", @"C:\estate\pipelines"), (spec.RepoName, spec.RepoRemoteUrl, spec.RepoRootPath));
            Assert.Equal("flows/" + flowName + ".flow.yaml", spec.PipelineRelativePath);
            // Pinned to the source's last synced commit; no snapshot, because the pipeline row holds no YAML.
            Assert.Equal("0123456789abcdef0123456789abcdef01234567", spec.CommitSha);
            Assert.Null(spec.FlowVersionHash);
            Assert.Equal(("${keyvault:vault/git-token}", "x-token-auth"), (spec.CredentialReference, spec.CredentialUsername));
            Assert.True(spec.Parameters.FullLoad);

            // A run whose repo and pipeline rows are gone still hands out, with the nulls the node turns into a
            // precise failure; a run that is gone altogether does not.
            var orphanFlow = flowName + "_orphan";
            var orphan = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, orphanFlow, "ing"), now)).RunId;
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
            var orphanSpec = await RunQueueStore.MarkHandedOutAsync(db, orphan, 0, Node, now);
            Assert.NotNull(orphanSpec);
            Assert.Equal((repoId, orphanFlow), (orphanSpec.RepoId, orphanSpec.FlowName));
            Assert.Null(orphanSpec.RepoName);
            Assert.Null(orphanSpec.PipelineRelativePath);
            Assert.Null(await RunQueueStore.MarkHandedOutAsync(db, Guid.CreateVersion7(), 0, Node, now));
        }
        finally
        {
            await Cleanup(cs, repoId);
            await using var db = CatalogDatabase.Create(cs);
            await db.RepoSources.Where(s => s.Name == repoName).ExecuteDeleteAsync();
            await db.Pipelines.Where(p => p.Id == pipelineId).ExecuteDeleteAsync();
            await db.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync();
        }
    }

    [SkippableFact]
    public async Task LiveTrace_IsAppendedUnderTheFence_AndDiscardedWhenTheAttemptIsRequeued()
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);
        var (repoId, flowName) = NewIds();

        try
        {
            await using var db = CatalogDatabase.Create(cs);
            var now = DateTime.UtcNow;
            var runId = (await RunQueueStore.EnqueueAsync(db, new RunEnqueueRequest(repoId, flowName, "ing"), now)).RunId;
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 0, Node, now));

            static RunTraceBatch Batch(string node, int attempt, int ordinal, DateTime at) => new(
                node, attempt,
                [new TraceStatement(ordinal, at, "staging.create", $"SELECT {ordinal}", null)],
                [],
                [new TraceEvent(ordinal, at, "info", "stage", $"event {ordinal}", null, null)]);

            // The holder's batches land; a failure stamps the statement it names; anyone else's batch is refused.
            Assert.True(await RunTraceStore.AppendLiveAsync(db, runId, Batch(Node, 1, 1, now)));
            Assert.True(await RunTraceStore.AppendLiveAsync(db, runId, Batch(Node, 1, 2, now)));
            Assert.True(await RunTraceStore.AppendLiveAsync(db, runId, new RunTraceBatch(Node, 1, [], [new TraceStatementFailure(2, "boom")], [])));
            Assert.False(await RunTraceStore.AppendLiveAsync(db, runId, Batch("other", 1, 3, now)));
            Assert.False(await RunTraceStore.AppendLiveAsync(db, runId, Batch(Node, 2, 3, now)));
            var statements = await db.RunStatements.AsNoTracking().Where(s => s.RunId == runId).OrderBy(s => s.Ordinal).ToListAsync();
            Assert.Equal([1, 2], statements.Select(s => s.Ordinal));
            Assert.Equal([null, "boom"], statements.Select(s => s.Error));
            Assert.All(statements, s => Assert.Equal(repoId, s.RepoId));
            Assert.Equal(2, await db.RunEvents.CountAsync(e => e.RunId == runId));

            // The context resolution shares the fence: the holder is answered, a stranger is not.
            var request = new RunContextRequest(Node, 1, "pre", "Orders", true, true);
            Assert.True((await RunContextStore.ResolveAsync(db, runId, request)).Held);
            Assert.False((await RunContextStore.ResolveAsync(db, runId, request with { Attempt = 2 })).Held);

            // The lease lapses and the run is requeued: the interrupted attempt's trace goes with it, the old holder
            // is refused from then on, and the successor's trace starts clean at ordinal 1.
            Assert.True((await RunQueueStore.RequeueInterruptedAsync(db, runId, Node, 1, now)).Applied);
            Assert.Equal(0, await db.RunStatements.CountAsync(s => s.RunId == runId));
            Assert.Equal(0, await db.RunEvents.CountAsync(e => e.RunId == runId));
            Assert.False(await RunTraceStore.AppendLiveAsync(db, runId, Batch(Node, 1, 3, now)));
            Assert.NotNull(await RunQueueStore.MarkHandedOutAsync(db, runId, 1, "successor", now));
            Assert.True(await RunTraceStore.AppendLiveAsync(db, runId, Batch("successor", 2, 1, now)));
            Assert.Equal(1, await db.RunStatements.CountAsync(s => s.RunId == runId));
        }
        finally
        {
            await Cleanup(cs, repoId);
        }
    }

    private static (Guid RepoId, string FlowName) NewIds()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return (FlowIdentity.FromName("rq_" + suffix), "rq_flow_" + suffix);
    }

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

    private static async Task Cleanup(string cs, Guid repoId)
    {
        await using var db = CatalogDatabase.Create(cs);
        await db.RunStatements.Where(s => s.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunEvents.Where(e => e.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunFiles.Where(f => f.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunAssertions.Where(a => a.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunSurrogateKeys.Where(k => k.RepoId == repoId).ExecuteDeleteAsync();
        await db.RunHealthCheckMetrics.Where(m => m.RepoId == repoId).ExecuteDeleteAsync();
        await db.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync();
    }
}
