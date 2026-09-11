using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// The in-memory queue's gates and lifecycle, exercised directly: ordering across pools, pool routing, one
/// execution per pipeline, wave order and an exact concurrency cap inside a group, reservations that occupy at
/// once, leases that renew or revoke, cancel marks, expiry, the reconcile diff, and the waiters. No clock, no
/// ledger, no I/O: every assertion is about the state machine alone.
/// </summary>
public sealed class DispatchStateTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Reserve_HandsOutOldestFirst_AcrossTheUntargetedAndServedPools()
    {
        var state = new DispatchState();
        var pooled = Make.Run(At(1), pool: "etl");
        var untargeted = Make.Run(At(2));
        var other = Make.Run(At(0), pool: "other");
        Assert.True(state.AddQueuedRun(untargeted));
        Assert.True(state.AddQueuedRun(pooled));
        Assert.True(state.AddQueuedRun(other));

        var picks = state.ReserveRuns("n1", ["etl"], 10);

        // The node serves "etl" and the untargeted pool; "other" is never offered. Order is by enqueue time.
        Assert.Equal([pooled.RunId, untargeted.RunId], picks.Select(p => p.RunId));
        Assert.All(picks, p => Assert.Equal(0, p.ExpectedAttempt));
    }

    [Fact]
    public void Reserve_NeverHandsOutMoreThanAsked_AndTheRestStaysQueued()
    {
        var state = new DispatchState();
        var runs = Enumerable.Range(0, 5).Select(i => Make.Run(At(i))).ToList();
        foreach (var run in runs)
        {
            state.AddQueuedRun(run);
        }

        var first = state.ReserveRuns("n1", [], 2);
        var second = state.ReserveRuns("n2", [], 2);
        var third = state.ReserveRuns("n3", [], 2);

        Assert.Equal(runs.Take(2).Select(r => r.RunId), first.Select(p => p.RunId));
        Assert.Equal(runs.Skip(2).Take(2).Select(r => r.RunId), second.Select(p => p.RunId));
        Assert.Equal([runs[4].RunId], third.Select(p => p.RunId));
        Assert.Empty(state.ReserveRuns("n4", [], 2));
    }

    [Fact]
    public void Reserve_SamePipeline_NeverTwoAtOnce_EvenInsideOneBatch()
    {
        var state = new DispatchState();
        var pipeline = Guid.NewGuid();
        var first = Make.Run(At(0), pipeline);
        var duplicate = Make.Run(At(1), pipeline);
        var unrelated = Make.Run(At(2));
        state.AddQueuedRun(first);
        state.AddQueuedRun(duplicate);
        state.AddQueuedRun(unrelated);

        // The duplicate is skipped, not blocking: the unrelated run behind it is handed out in the same batch.
        var picks = state.ReserveRuns("n1", [], 10);
        Assert.Equal([first.RunId, unrelated.RunId], picks.Select(p => p.RunId));

        // Confirming the lease and finishing the first frees the pipeline for the duplicate.
        Assert.True(state.ConfirmRunLease(first.RunId, "n1", 1, At(3), At(93)));
        Assert.Empty(state.ReserveRuns("n1", [], 10));
        Assert.True(state.RemoveRun(first.RunId));
        Assert.Equal([duplicate.RunId], state.ReserveRuns("n1", [], 10).Select(p => p.RunId));
    }

    [Fact]
    public void Reserve_GatesByWave_AndAppliesTheGroupCapExactly()
    {
        var state = new DispatchState();
        var group = Guid.NewGuid();
        var wave0 = Enumerable.Range(0, 3).Select(i => Make.Run(At(i), group: group, wave: 0, cap: 2)).ToList();
        var wave1 = Make.Run(At(10), group: group, wave: 1, cap: 2);
        foreach (var run in wave0)
        {
            state.AddQueuedRun(run);
        }

        state.AddQueuedRun(wave1);

        // Only two of the three wave-0 members fit under the cap, across two different nodes' reservations.
        var a = state.ReserveRuns("n1", [], 10);
        var b = state.ReserveRuns("n2", [], 10);
        Assert.Equal([wave0[0].RunId, wave0[1].RunId], a.Select(p => p.RunId));
        Assert.Empty(b);

        // One finishes: the third wave-0 member goes, the wave-1 member still waits for wave 0 to drain.
        state.ConfirmRunLease(wave0[0].RunId, "n1", 1, At(20), At(110));
        state.ConfirmRunLease(wave0[1].RunId, "n1", 1, At(20), At(110));
        state.RemoveRun(wave0[0].RunId);
        Assert.Equal([wave0[2].RunId], state.ReserveRuns("n2", [], 10).Select(p => p.RunId));
        Assert.Empty(state.ReserveRuns("n3", [], 10));

        // Wave 0 fully terminal: wave 1 becomes eligible.
        state.ConfirmRunLease(wave0[2].RunId, "n2", 1, At(21), At(111));
        state.RemoveRun(wave0[1].RunId);
        Assert.Empty(state.ReserveRuns("n3", [], 10));
        state.RemoveRun(wave0[2].RunId);
        Assert.Equal([wave1.RunId], state.ReserveRuns("n3", [], 10).Select(p => p.RunId));
    }

    [Fact]
    public void Reserve_WaveGate_CountsSkippedAndCancelledMembersAsTerminal()
    {
        var state = new DispatchState();
        var group = Guid.NewGuid();
        var anchor = Make.Run(At(0), group: group, wave: 0);
        var dependent = Make.Run(At(1), group: group, wave: 1);
        var independent = Make.Run(At(2), group: group, wave: 1);
        state.AddQueuedRun(anchor);
        state.AddQueuedRun(dependent);
        state.AddQueuedRun(independent);

        var pick = Assert.Single(state.ReserveRuns("n1", [], 10));
        state.ConfirmRunLease(pick.RunId, "n1", 1, At(3), At(93));
        // The anchor fails; the ledger skipped its dependent; memory drops both.
        state.RemoveRun(anchor.RunId);
        state.RemoveRun(dependent.RunId);
        Assert.Equal([independent.RunId], state.ReserveRuns("n1", [], 10).Select(p => p.RunId));
    }

    [Fact]
    public void ReleaseReservation_ReturnsTheRunToTheQueue_InItsOriginalOrder()
    {
        var state = new DispatchState();
        var first = Make.Run(At(0));
        var second = Make.Run(At(1));
        state.AddQueuedRun(first);
        state.AddQueuedRun(second);

        var picks = state.ReserveRuns("n1", [], 1);
        Assert.Equal(first.RunId, picks[0].RunId);
        state.ReleaseRunReservation(first.RunId);

        // Back at the head, still attempt 0, still ahead of the second.
        Assert.Equal([first.RunId, second.RunId], state.ReserveRuns("n2", [], 10).Select(p => p.RunId));
    }

    [Fact]
    public void ConfirmRunLease_RefusesAnUnreservedOrForeignRun()
    {
        var state = new DispatchState();
        var run = Make.Run(At(0));
        state.AddQueuedRun(run);
        Assert.False(state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(91)));

        state.ReserveRuns("n1", [], 1);
        Assert.False(state.ConfirmRunLease(run.RunId, "n2", 1, At(1), At(91)));
        Assert.True(state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(91)));
        Assert.False(state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(91)));
    }

    [Fact]
    public void RenewRunLeases_ExtendsHeldRuns_AndRevokesWhatTheNodeNoLongerHolds()
    {
        var state = new DispatchState();
        var mine = Make.Run(At(0));
        var lost = Make.Run(At(1));
        state.AddQueuedRun(mine);
        state.AddQueuedRun(lost);
        state.ReserveRuns("n1", [], 2);
        state.ConfirmRunLease(mine.RunId, "n1", 1, At(2), At(10));
        state.ConfirmRunLease(lost.RunId, "n1", 1, At(2), At(10));

        // A renewal before expiry extends both leases.
        Assert.Empty(state.RenewRunLeases("n1", [new HeldRun(mine.RunId, 1), new HeldRun(lost.RunId, 1)], At(100)));
        Assert.Empty(state.TakeExpiredRunLeases(At(50)));

        // "lost" lapses (the node stopped reporting it) and is requeued at the same attempt, then handed to n2 at
        // attempt 2: n1's claim to it at attempt 1 is revoked, as is a claim to a run that never existed.
        state.RenewRunLeases("n1", [new HeldRun(mine.RunId, 1)], At(200));
        var expired = Assert.Single(state.TakeExpiredRunLeases(At(150)));
        Assert.Equal(lost.RunId, expired.RunId);
        state.RequeueExpiredRun(lost.RunId);
        var pick = Assert.Single(state.ReserveRuns("n2", [], 1));
        Assert.Equal((lost.RunId, 1), (pick.RunId, pick.ExpectedAttempt));
        state.ConfirmRunLease(lost.RunId, "n2", 2, At(151), At(300));

        // A holding memory does not know at all (the node finished it while this poll was in flight) is ignored.
        var finished = Guid.NewGuid();
        var revoked = state.RenewRunLeases(
            "n1", [new HeldRun(mine.RunId, 1), new HeldRun(lost.RunId, 1), new HeldRun(finished, 1)], At(260));
        Assert.Equal([lost.RunId], revoked);

        // A renewal from another node never touches a lease it does not hold.
        Assert.Contains(lost.RunId, state.RenewRunLeases("n3", [new HeldRun(lost.RunId, 2)], At(400)));
        Assert.Empty(state.TakeExpiredRunLeases(At(199)));
    }

    [Fact]
    public void MarkRunCancel_RemovesAQueuedRun_AndFlagsAHeldOne()
    {
        var state = new DispatchState();
        var queued = Make.Run(At(0));
        var held = Make.Run(At(1));
        state.AddQueuedRun(queued);
        state.AddQueuedRun(held);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(queued.RunId, "n1", 1, At(2), At(92));

        Assert.Equal(CancelMark.Flagged, state.MarkRunCancel(queued.RunId));
        Assert.Equal([queued.RunId], state.CancelRequestedRunsHeldBy("n1"));
        Assert.Equal(CancelMark.RemovedQueued, state.MarkRunCancel(held.RunId));
        Assert.Equal(CancelMark.Unknown, state.MarkRunCancel(held.RunId));
        Assert.Empty(state.ReserveRuns("n2", [], 10));
    }

    [Fact]
    public void MarkGroupCancel_TouchesEveryMember_AndNothingElse()
    {
        var state = new DispatchState();
        var group = Guid.NewGuid();
        var a = Make.Run(At(0), group: group, wave: 0);
        var b = Make.Run(At(1), group: group, wave: 1);
        var outsider = Make.Run(At(2));
        state.AddQueuedRun(a);
        state.AddQueuedRun(b);
        state.AddQueuedRun(outsider);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(a.RunId, "n1", 1, At(3), At(93));

        Assert.Equal(2, state.MarkGroupCancel(group));
        Assert.Equal([a.RunId], state.CancelRequestedRunsHeldBy("n1"));
        Assert.Equal([outsider.RunId], state.ReserveRuns("n2", [], 10).Select(p => p.RunId));
    }

    [Fact]
    public void TakeExpiredRunLeases_MovesLapsedLeasesToExpiring_AndReturnsThemAgainUntilDispositioned()
    {
        var state = new DispatchState();
        var run = Make.Run(At(0));
        state.AddQueuedRun(run);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(61));

        Assert.Empty(state.TakeExpiredRunLeases(At(60)));
        var expired = Assert.Single(state.TakeExpiredRunLeases(At(61)));
        Assert.Equal(("n1", 1, false), (expired.Node, expired.Attempt, expired.CancelRequested));

        // Expiring: the node's renewal is refused (revoked), and the entry is offered again to a retrying pass.
        Assert.Contains(run.RunId, state.RenewRunLeases("n1", [new HeldRun(run.RunId, 1)], At(200)));
        Assert.Single(state.TakeExpiredRunLeases(At(62)));

        // Requeued: back in the queue at the same attempt, pipeline free.
        Assert.True(state.RequeueExpiredRun(run.RunId));
        Assert.False(state.RequeueExpiredRun(run.RunId));
        var pick = Assert.Single(state.ReserveRuns("n2", [], 1));
        Assert.Equal(1, pick.ExpectedAttempt);
    }

    [Fact]
    public void RemoveRun_FreesThePipelineAndTheGroupSlot_WhateverTheState()
    {
        var state = new DispatchState();
        var group = Guid.NewGuid();
        var pipeline = Guid.NewGuid();
        var first = Make.Run(At(0), pipeline, group: group, wave: 0, cap: 1);
        var second = Make.Run(At(1), pipeline, group: group, wave: 0, cap: 1);
        state.AddQueuedRun(first);
        state.AddQueuedRun(second);
        state.ReserveRuns("n1", [], 1);

        // Removed while merely reserved (the ledger refused the hand-out): both gates open again.
        Assert.True(state.RemoveRun(first.RunId));
        Assert.Equal([second.RunId], state.ReserveRuns("n1", [], 1).Select(p => p.RunId));
        Assert.False(state.RemoveRun(first.RunId));
    }

    [Fact]
    public void DiffRuns_ReportsWhatMemoryLacks_AndWhatTheLedgerNoLongerHolds()
    {
        var state = new DispatchState();
        var known = Make.Run(At(0));
        var leased = Make.Run(At(1));
        var gone = Make.Run(At(2));
        state.AddQueuedRun(known);
        state.AddQueuedRun(leased);
        state.AddQueuedRun(gone);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(known.RunId, "n1", 1, At(3), At(93));
        var reserved = state.ReserveRuns("n1", [], 1);
        Assert.Equal(leased.RunId, reserved[0].RunId);

        var unknownQueued = Guid.CreateVersion7();
        var unknownRunning = new ActiveRunRef(Guid.CreateVersion7(), "n9", 1, false);
        var diff = state.DiffRuns(
            [unknownQueued],
            [new ActiveRunRef(known.RunId, "n1", 1, true), unknownRunning]);

        Assert.Equal([unknownQueued], diff.UnknownQueued);
        Assert.Equal([unknownRunning], diff.UnknownRunning);
        Assert.Equal([known.RunId], diff.CancelRequested);
        // "gone" is queued in memory but absent from the ledger's queued set; "leased" is reserved (in flight) and
        // never reported; "known" is consistent.
        Assert.Equal([gone.RunId], diff.Stale);
    }

    [Fact]
    public void DiffRuns_ALeaseWhoseHolderOrAttemptDiffers_IsStale()
    {
        var state = new DispatchState();
        var run = Make.Run(At(0));
        state.AddQueuedRun(run);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(91));

        Assert.Empty(state.DiffRuns([], [new ActiveRunRef(run.RunId, "n1", 1, false)]).Stale);
        Assert.Equal([run.RunId], state.DiffRuns([], [new ActiveRunRef(run.RunId, "n2", 1, false)]).Stale);
        Assert.Equal([run.RunId], state.DiffRuns([], [new ActiveRunRef(run.RunId, "n1", 2, false)]).Stale);
        Assert.Equal([run.RunId], state.DiffRuns([run.RunId], []).Stale);
    }

    [Fact]
    public async Task WaitAsync_WakesAWaiterWantingWork_WhenARunItCouldTakeIsAdded()
    {
        var state = new DispatchState();
        var wait = state.WaitAsync("n1", ["etl"], wantsWork: true, TimeSpan.FromSeconds(10), CancellationToken.None);
        Assert.False(wait.IsCompleted);

        state.AddQueuedRun(Make.Run(At(0), pool: "other"));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted); // a pool the node does not serve

        state.AddQueuedRun(Make.Run(At(1), pool: "etl"));
        Assert.True(await wait);
        Assert.Equal(0, state.WaiterCount);
    }

    [Fact]
    public async Task WaitAsync_WakesOnlyAsManyWaitersAsRunsWereAdded_OldestFirst()
    {
        var state = new DispatchState();
        var first = state.WaitAsync("n1", [], true, TimeSpan.FromSeconds(10), CancellationToken.None);
        var second = state.WaitAsync("n2", [], true, TimeSpan.FromSeconds(10), CancellationToken.None);
        var third = state.WaitAsync("n3", [], true, TimeSpan.FromSeconds(10), CancellationToken.None);

        state.AddQueuedRun(Make.Run(At(0)));
        Assert.True(await first);
        await Task.Delay(50);
        Assert.False(second.IsCompleted);
        Assert.False(third.IsCompleted);

        // A completion may free any number of gated runs: everyone wanting work is woken.
        state.AddQueuedRun(Make.Run(At(1)));
        Assert.True(await second);
        var pick = Assert.Single(state.ReserveRuns("n2", [], 1));
        state.ConfirmRunLease(pick.RunId, "n2", 1, At(2), At(92));
        state.RemoveRun(pick.RunId);
        Assert.True(await third);
    }

    [Fact]
    public async Task WaitAsync_ASaturatedNodeIsWokenOnlyBySignalsForIt()
    {
        var state = new DispatchState();
        var run = Make.Run(At(0));
        state.AddQueuedRun(run);
        state.ReserveRuns("n1", [], 1);
        state.ConfirmRunLease(run.RunId, "n1", 1, At(1), At(91));

        var wait = state.WaitAsync("n1", [], wantsWork: false, TimeSpan.FromSeconds(10), CancellationToken.None);
        state.AddQueuedRun(Make.Run(At(2)));
        await Task.Delay(50);
        Assert.False(wait.IsCompleted);

        state.MarkRunCancel(run.RunId);
        Assert.True(await wait);
    }

    [Fact]
    public async Task WaitAsync_TimesOut_AndHonorsCancellation()
    {
        var state = new DispatchState();
        Assert.False(await state.WaitAsync("n1", [], true, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.False(await state.WaitAsync("n1", [], true, TimeSpan.Zero, CancellationToken.None));

        using var cts = new CancellationTokenSource();
        var wait = state.WaitAsync("n1", [], true, TimeSpan.FromSeconds(10), cts.Token);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(0, state.WaiterCount);
    }

    [Fact]
    public async Task Clear_DropsEverything_AndWakesEveryWaiter()
    {
        var state = new DispatchState();
        state.AddQueuedRun(Make.Run(At(0)));
        state.AddQueuedTask(Make.Task(At(0)));
        var wait = state.WaitAsync("n1", [], false, TimeSpan.FromSeconds(10), CancellationToken.None);

        state.Clear();

        Assert.True(await wait);
        Assert.Empty(state.ReserveRuns("n1", [], 10));
        Assert.Empty(state.ReserveTasks("n1", [], 10));
    }

    [Fact]
    public void Tasks_RouteByPool_ReserveOldestFirst_AndRenewOrRevoke()
    {
        var state = new DispatchState();
        var pooled = Make.Task(At(0), "onprem");
        var untargeted = Make.Task(At(1));
        state.AddQueuedTask(pooled);
        state.AddQueuedTask(untargeted);

        Assert.Equal([untargeted.TaskId], state.ReserveTasks("cloud", ["cloud"], 10));
        state.ReleaseTaskReservation(untargeted.TaskId);
        Assert.Equal([pooled.TaskId, untargeted.TaskId], state.ReserveTasks("n1", ["onprem"], 10));
        Assert.True(state.ConfirmTaskLease(pooled.TaskId, "n1", At(2), At(62)));
        state.ReleaseTaskReservation(untargeted.TaskId);

        Assert.Empty(state.RenewTaskLeases("n1", [pooled.TaskId], At(100)));
        Assert.Equal([untargeted.TaskId], state.RenewTaskLeases("n1", [untargeted.TaskId], At(100)));
        Assert.Empty(state.RenewTaskLeases("n1", [Guid.NewGuid()], At(100)));
        Assert.Equal(CancelMark.Flagged, state.MarkTaskCancel(pooled.TaskId));
        Assert.Equal([pooled.TaskId], state.CancelRequestedTasksHeldBy("n1"));
        Assert.Equal(CancelMark.RemovedQueued, state.MarkTaskCancel(untargeted.TaskId));
    }

    [Fact]
    public void Snapshot_ExplainsEveryGate_AndCountsPerPool()
    {
        var state = new DispatchState();
        var pipeline = Guid.NewGuid();
        var group = Guid.NewGuid();
        var running = Make.Run(At(0), pipeline);
        var pipelineBusy = Make.Run(At(1), pipeline);
        var waveGated = Make.Run(At(2), group: group, wave: 1, cap: 1);
        var capHolder = Make.Run(At(3), group: group, wave: 0, cap: 1);
        var capped = Make.Run(At(4), group: group, wave: 0, cap: 1);
        var orphanPool = Make.Run(At(5), pool: "nobody");
        foreach (var run in new[] { running, pipelineBusy, waveGated, capHolder, capped, orphanPool })
        {
            state.AddQueuedRun(run);
        }

        var picks = state.ReserveRuns("n1", [], 2);
        Assert.Equal([running.RunId, capHolder.RunId], picks.Select(p => p.RunId));
        state.ConfirmRunLease(running.RunId, "n1", 1, At(6), At(96));

        var snapshot = state.Snapshot(pool => pool.Length == 0 ? (1, 2) : (0, 0));

        var byId = snapshot.QueuedRuns.ToDictionary(r => r.RunId);
        Assert.Equal(DispatchBlockReasons.PipelineBusy, byId[pipelineBusy.RunId].Blocked);
        Assert.Equal(DispatchBlockReasons.WaveGated, byId[waveGated.RunId].Blocked);
        Assert.Equal(DispatchBlockReasons.GroupCap, byId[capped.RunId].Blocked);
        Assert.Equal(DispatchBlockReasons.NoEligibleNode, byId[orphanPool.RunId].Blocked);
        Assert.Equal(2, snapshot.LeasedRuns.Count);
        Assert.Contains(snapshot.LeasedRuns, r => r.RunId == capHolder.RunId && r.State == "reserved");
        Assert.Contains(snapshot.LeasedRuns, r => r.RunId == running.RunId && r.State == "leased" && r.Node == "n1");
        var defaultPool = Assert.Single(snapshot.Pools, p => p.Pool.Length == 0);
        Assert.Equal((3, 2, 1, 2), (defaultPool.QueuedRuns, defaultPool.LeasedRuns, defaultPool.OnlineNodes, defaultPool.FreeRunSlots));
        Assert.Single(snapshot.Pools, p => p.Pool == "nobody" && p.QueuedRuns == 1);
    }
    [Fact]
    public void CountEligibleQueuedRuns_CountsOnlyWhatANodeCouldTakeNow_PerPool()
    {
        var state = new DispatchState();
        var pipeline = Guid.NewGuid();
        var group = Guid.NewGuid();
        var free = Make.Run(At(0), pool: "etl");
        var busyPipeline = Make.Run(At(1), pipeline: pipeline, pool: "etl");
        var wave0 = Make.Run(At(2), pool: "etl", group: group, wave: 0, cap: 1);
        var wave0Sibling = Make.Run(At(3), pool: "etl", group: group, wave: 0, cap: 1);
        var wave1 = Make.Run(At(4), pool: "etl", group: group, wave: 1, cap: 1);
        var untargeted = Make.Run(At(5));
        foreach (var run in new[] { free, busyPipeline, wave0, wave0Sibling, wave1, untargeted })
        {
            state.AddQueuedRun(run);
        }

        // The pipeline is executing on some node: its queued run is gated. Wave 1 waits for wave 0; the cap only
        // bites once a wave-0 member is executing, so both wave-0 members are eligible right now.
        state.AddLeasedRun(Make.Run(At(0), pipeline: pipeline), "n9", T0, T0.AddMinutes(1));

        Assert.Equal(3, state.CountEligibleQueuedRuns("etl"));
        Assert.Equal(1, state.CountEligibleQueuedRuns(string.Empty));
        Assert.Equal(0, state.CountEligibleQueuedRuns("nobody"));

        // A node serving the pool takes its eligible work (and the untargeted run, which every node serves): the
        // cap now holds the sibling, so nothing is left eligible anywhere.
        var picks = state.ReserveRuns("n1", ["etl"], 10);
        Assert.Equal([free.RunId, wave0.RunId, untargeted.RunId], picks.Select(p => p.RunId));
        Assert.Equal(0, state.CountEligibleQueuedRuns("etl"));
        Assert.Equal(0, state.CountEligibleQueuedRuns(string.Empty));
    }
}
