using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// The coordinator over a fake journal and a manual clock: every hand-out is journaled before a node sees it, every
/// outcome is fenced, lapsed leases are dispositioned exactly as a dead node's runs always were, a long poll parks
/// and wakes, cancels reach the holding node at once, reconcile heals what bypassed the notify, ownership changes
/// are honored, and a journal that refuses or fails a write never leaves memory and the ledger disagreeing.
/// </summary>
public sealed class DispatcherTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Poll_HandsOutAJournaledRun_AndTheOutcomeCompletesIt()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 2), CancellationToken.None);

        var handout = Assert.Single(poll.Runs);
        Assert.Equal((run.RunId, 1), (handout.RunId, handout.Attempt));
        Assert.Equal(("running", "n1", 1), (ledger.Runs[run.RunId].Status, ledger.Runs[run.RunId].Node, ledger.Runs[run.RunId].Attempt));
        Assert.Contains($"handout:{run.RunId}:n1:0", ledger.Calls);

        var status = await dispatcher.RecordRunOutcomeAsync(
            run.RunId, new RunOutcomeRequest("n1", 1, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);

        Assert.Equal(RunOutcomeStatus.Recorded, status);
        Assert.Equal("succeeded", ledger.Runs[run.RunId].Status);
        Assert.Empty(dispatcher.Snapshot().LeasedRuns);
        Assert.Empty((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).Runs);
    }

    [Fact]
    public async Task Poll_NeverHandsOutMoreThanTheFreeSlots_AndNothingWithNone()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        for (var i = 0; i < 5; i++)
        {
            ledger.AddQueued(Make.Run(T0.AddSeconds(i)));
        }

        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        Assert.Equal(2, (await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 2), CancellationToken.None)).Runs.Count);
        Assert.Empty((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None)).Runs);
        Assert.Equal(3, (await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 10), CancellationToken.None)).Runs.Count);
        Assert.Equal(5, ledger.Runs.Values.Count(r => r.Status == "running"));
    }

    [Fact]
    public async Task Poll_WhenTheLedgerRefusesTheHandOut_DropsTheRunAndHandsOutTheNext()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var cancelledDirectly = Make.Run(T0);
        var next = Make.Run(T0.AddSeconds(1));
        ledger.AddQueued(cancelledDirectly).Status = "cancelled";
        ledger.AddQueued(next);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        // Memory still believes the first is queued (it was cancelled straight in the journal after activation).
        dispatcher.Queue.AddQueuedRun(cancelledDirectly);

        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        // The refused reservation is dropped from memory; the batch asked for one run and got none from it, and
        // the very next poll hands out the survivor.
        Assert.Empty(poll.Runs);
        Assert.DoesNotContain(dispatcher.Snapshot().QueuedRuns, r => r.RunId == cancelledDirectly.RunId);
        var again = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);
        Assert.Equal(next.RunId, Assert.Single(again.Runs).RunId);
    }

    [Fact]
    public async Task Poll_WhenTheLedgerWriteThrows_LeavesTheRunQueuedForTheNextPoll()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        ledger.FailNextWrite = new InvalidOperationException("catalog unreachable");

        var first = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);
        Assert.Empty(first.Runs);
        Assert.Equal("queued", ledger.Runs[run.RunId].Status);
        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == run.RunId && r.Blocked == DispatchBlockReasons.Eligible);

        var second = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);
        Assert.Equal(run.RunId, Assert.Single(second.Runs).RunId);
    }

    [Fact]
    public async Task Poll_AbandonedMidHandOut_ReleasesTheReservation()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        using var hold = new SemaphoreSlim(0, 1);
        ledger.HoldWrites = hold;
        using var cts = new CancellationTokenSource();

        var poll = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), cts.Token);
        await Wait.UntilAsync(() => dispatcher.Snapshot().LeasedRuns.Any(r => r.RunId == run.RunId && r.State == "reserved"));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => poll);

        // The journal write was abandoned before it applied: the row is still queued, and memory no longer
        // reserves the run, so the next poll hands it out afresh.
        ledger.HoldWrites = null;
        Assert.Equal("queued", ledger.Runs[run.RunId].Status);
        var view = dispatcher.Snapshot();
        Assert.DoesNotContain(view.LeasedRuns, r => r.State == "reserved");
        Assert.Contains(view.QueuedRuns, r => r.RunId == run.RunId);
        Assert.Equal(run.RunId, Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs).RunId);
    }

    [Fact]
    public async Task RecordRunOutcome_WithAStaleAttempt_IsDroppedAndTheHolderKeepsTheRun()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        var wrongAttempt = await dispatcher.RecordRunOutcomeAsync(
            run.RunId, new RunOutcomeRequest("n1", 7, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);
        var wrongNode = await dispatcher.RecordRunOutcomeAsync(
            run.RunId, new RunOutcomeRequest("n2", 1, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);

        Assert.Equal(RunOutcomeStatus.StaleClaim, wrongAttempt);
        Assert.Equal(RunOutcomeStatus.StaleClaim, wrongNode);
        Assert.Equal("running", ledger.Runs[run.RunId].Status);
        Assert.Contains(dispatcher.Snapshot().LeasedRuns, r => r.RunId == run.RunId && r.Node == "n1");
    }

    [Fact]
    public async Task RecordRunOutcome_AFailedMember_DropsTheSkippedDependentsFromMemory()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var group = Guid.NewGuid();
        var anchor = Make.Run(T0, group: group, wave: 0);
        var dependent = Make.Run(T0.AddSeconds(1), group: group, wave: 1);
        var independent = Make.Run(T0.AddSeconds(2), group: group, wave: 1);
        ledger.AddQueued(anchor).Dependents.Add(dependent.RunId);
        ledger.AddQueued(dependent);
        ledger.AddQueued(independent);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var handout = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 5), CancellationToken.None)).Runs);
        Assert.Equal(anchor.RunId, handout.RunId);

        await dispatcher.RecordRunOutcomeAsync(
            anchor.RunId, new RunOutcomeRequest("n1", 1, RunOutcomeKind.Failed, "boom", null), CancellationToken.None);

        Assert.Equal("skipped", ledger.Runs[dependent.RunId].Status);
        var remaining = Assert.Single(dispatcher.Snapshot().QueuedRuns);
        Assert.Equal(independent.RunId, remaining.RunId);
        Assert.Equal(independent.RunId, Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 5), CancellationToken.None)).Runs).RunId);
    }

    [Fact]
    public async Task RecordRunOutcome_UnreadableArtifact_FailsTheRunSoItNeverLingers()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        var status = await dispatcher.RecordRunOutcomeAsync(
            run.RunId, new RunOutcomeRequest("n1", 1, RunOutcomeKind.Completed, null, """{ "corrupt": true }"""), CancellationToken.None);

        Assert.Equal(RunOutcomeStatus.ArtifactUnreadable, status);
        Assert.Equal("failed", ledger.Runs[run.RunId].Status);
        Assert.Empty(dispatcher.Snapshot().LeasedRuns);
    }

    [Fact]
    public async Task Tick_RequeuesALapsedLease_AndTheNodeIsToldItsHoldingIsRevoked()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var handout = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None)).Runs);

        // A renewal inside the lease keeps it; silence past the lease loses it.
        clock.Advance(TimeSpan.FromSeconds(50));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holding: [new HeldRun(run.RunId, 1)]), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(50));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Contains(dispatcher.Snapshot().LeasedRuns, r => r.RunId == run.RunId);

        clock.Advance(TimeSpan.FromSeconds(11));
        await dispatcher.TickAsync(CancellationToken.None);

        Assert.Equal(("queued", 1), (ledger.Runs[run.RunId].Status, ledger.Runs[run.RunId].Attempt));
        Assert.Contains($"requeue:{run.RunId}:n1:1", ledger.Calls);
        // The zombie's next poll learns the run is gone; a successor takes it at attempt 2.
        var zombie = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holding: [new HeldRun(run.RunId, handout.Attempt)]), CancellationToken.None);
        Assert.Equal([run.RunId], zombie.RevokedRuns);
        var successor = Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs);
        Assert.Equal((run.RunId, 2), (successor.RunId, successor.Attempt));
        // And the zombie's late outcome is dropped by the fence.
        Assert.Equal(RunOutcomeStatus.StaleClaim, await dispatcher.RecordRunOutcomeAsync(
            run.RunId, new RunOutcomeRequest("n1", 1, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None));
        Assert.Equal("running", ledger.Runs[run.RunId].Status);
    }

    [Fact]
    public async Task Tick_FailsALapsedLease_OnceTheAttemptBudgetIsExhausted()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0, attempt: 2);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var handout = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None)).Runs);
        Assert.Equal(3, handout.Attempt);

        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);

        Assert.Equal("failed", ledger.Runs[run.RunId].Status);
        Assert.Contains($"fail-interrupted:{run.RunId}:n1:3", ledger.Calls);
        Assert.Empty(dispatcher.Snapshot().LeasedRuns);
        Assert.Empty(dispatcher.Snapshot().QueuedRuns);
    }

    [Fact]
    public async Task Tick_RecordsALapsedLeaseCancelled_WhenACancelWasPending()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);
        ledger.Runs[run.RunId].CancelRequested = true;
        Assert.Equal(CancelMark.Flagged, dispatcher.NotifyRunCancelled(run.RunId));

        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);

        Assert.Equal("cancelled", ledger.Runs[run.RunId].Status);
        Assert.Contains($"cancel-interrupted:{run.RunId}:n1:1", ledger.Calls);
    }

    [Fact]
    public async Task Tick_WhenTheDispositionWriteFails_RetriesOnTheNextTick()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(61));
        ledger.FailNextWrite = new InvalidOperationException("catalog unreachable");
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal("running", ledger.Runs[run.RunId].Status);
        Assert.Contains(dispatcher.Snapshot().LeasedRuns, r => r.RunId == run.RunId && r.State == "expiring");

        clock.Advance(TimeSpan.FromSeconds(1));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal("queued", ledger.Runs[run.RunId].Status);
        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == run.RunId);
    }

    [Fact]
    public async Task Poll_ParksUntilARunIsEnqueued_ThenHandsItOutAtOnce()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));

        var poll = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, waitSeconds: 10), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 1);
        Assert.False(poll.IsCompleted);

        var run = Make.Run(T0);
        ledger.AddQueued(run);
        dispatcher.NotifyRunsEnqueued([run]);

        var response = await poll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(run.RunId, Assert.Single(response.Runs).RunId);
        Assert.Equal(0, dispatcher.Snapshot().Waiters);
    }

    [Fact]
    public async Task Poll_ASaturatedNodeIsWokenByACancelForARunItHolds()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        var parked = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holding: [new HeldRun(run.RunId, 1)], waitSeconds: 10), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 1);
        Assert.False(parked.IsCompleted);

        ledger.Runs[run.RunId].CancelRequested = true;
        dispatcher.NotifyRunCancelled(run.RunId);

        var response = await parked.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([run.RunId], response.CancelRuns);
        Assert.Empty(response.Runs);
    }

    [Fact]
    public async Task Poll_TimesOutEmpty_WhenNothingHappens()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));

        // The manual clock never advances, so the wait is bounded only by the real-time timeout the wait uses.
        var poll = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, waitSeconds: 1), CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        var response = await poll.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(response.HasContent);
    }

    [Fact]
    public async Task NotifyRunCancelled_RemovesAQueuedRunBeforeAnyNodeSeesIt()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run).Status = "cancelled";
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        dispatcher.Queue.AddQueuedRun(run);

        Assert.Equal(CancelMark.RemovedQueued, dispatcher.NotifyRunCancelled(run.RunId));
        Assert.Empty((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None)).Runs);
    }

    [Fact]
    public async Task Activate_RebuildsFromTheLedger_AndANodeReattachesToItsRunningRun()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var queued = Make.Run(T0);
        var running = Make.Run(T0.AddSeconds(1), attempt: 1);
        ledger.AddQueued(queued);
        ledger.AddRunning(running, "n1");
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));

        var view = dispatcher.Snapshot();
        Assert.Single(view.QueuedRuns, r => r.RunId == queued.RunId);
        Assert.Single(view.LeasedRuns, r => r.RunId == running.RunId && r.Node == "n1" && r.Attempt == 1);

        // The node that was executing it reattaches by reporting it held, and the grace lease is renewed.
        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holding: [new HeldRun(running.RunId, 1)]), CancellationToken.None);
        Assert.Empty(poll.RevokedRuns);
        clock.Advance(TimeSpan.FromSeconds(59));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal("running", ledger.Runs[running.RunId].Status);
        Assert.Equal(RunOutcomeStatus.Recorded, await dispatcher.RecordRunOutcomeAsync(
            running.RunId, new RunOutcomeRequest("n1", 1, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None));
    }

    [Fact]
    public async Task Activate_ARunningRunNobodyReattachesTo_IsRequeuedWhenTheGraceLapses()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var running = Make.Run(T0, attempt: 1);
        ledger.AddRunning(running, "dead-node");
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));

        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);

        Assert.Equal("queued", ledger.Runs[running.RunId].Status);
        var successor = Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs);
        Assert.Equal(2, successor.Attempt);
    }

    [Fact]
    public async Task Deactivate_WakesParkedPolls_WhichThenFailAsInactive_AndNotifiesAreIgnored()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var parked = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, waitSeconds: 10), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 1);

        dispatcher.Deactivate();

        await Assert.ThrowsAsync<DispatchInactiveException>(() => parked.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<DispatchInactiveException>(() => dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None));
        dispatcher.NotifyRunsEnqueued([Make.Run(T0)]);
        Assert.False(dispatcher.IsActive);
        Assert.Empty(dispatcher.Snapshot().QueuedRuns);
    }

    [Fact]
    public async Task Reconcile_AddsQueuedRowsMemoryMissed_AndFlagsCancelsStampedDirectly()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var held = Make.Run(T0);
        ledger.AddQueued(held);
        dispatcher.NotifyRunsEnqueued([held]);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);

        // Enqueued on another replica (no notify) and cancelled straight against the journal.
        var bypassed = Make.Run(T0.AddSeconds(1));
        ledger.AddQueued(bypassed);
        ledger.Runs[held.RunId].CancelRequested = true;

        await dispatcher.ReconcileAsync(CancellationToken.None);

        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == bypassed.RunId);
        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holding: [new HeldRun(held.RunId, 1)]), CancellationToken.None);
        Assert.Equal([held.RunId], poll.CancelRuns);
    }

    [Fact]
    public async Task Reconcile_RemovesAnEntryOnlyWhenItIsStaleInTwoConsecutivePasses()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        // Cancelled straight against the journal: memory still queues it.
        ledger.Runs[run.RunId].Status = "cancelled";
        await dispatcher.ReconcileAsync(CancellationToken.None);
        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == run.RunId);

        await dispatcher.ReconcileAsync(CancellationToken.None);
        Assert.Empty(dispatcher.Snapshot().QueuedRuns);
    }

    [Fact]
    public async Task Reconcile_ATransientDisagreement_IsNeverActedOn()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        ledger.Runs[run.RunId].Status = "cancelled";
        await dispatcher.ReconcileAsync(CancellationToken.None);
        // The disagreement heals before the second pass (the journal shows it queued again).
        ledger.Runs[run.RunId].Status = "queued";
        await dispatcher.ReconcileAsync(CancellationToken.None);
        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == run.RunId);

        // Stale again later: the suspicion did not carry over, so it takes two fresh passes.
        ledger.Runs[run.RunId].Status = "cancelled";
        await dispatcher.ReconcileAsync(CancellationToken.None);
        Assert.Contains(dispatcher.Snapshot().QueuedRuns, r => r.RunId == run.RunId);
    }

    [Fact]
    public async Task Reconcile_AdoptsARunningRowWithNoLease_UnderGrace()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var orphan = Make.Run(T0, attempt: 1);
        ledger.AddRunning(orphan, "n7");

        await dispatcher.ReconcileAsync(CancellationToken.None);

        Assert.Single(dispatcher.Snapshot().LeasedRuns, r => r.RunId == orphan.RunId && r.Node == "n7");
        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal("queued", ledger.Runs[orphan.RunId].Status);
    }

    [Fact]
    public async Task Tick_FlushesNodeHeartbeatsOnItsCadence_AndRelaysARestartRequest()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var options = Make.Options(leaseSeconds: 60, longPollSeconds: 10);
        options.NodeFlushSeconds = 5;
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, options);
        var started = clock.GetUtcNow().UtcDateTime;
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, startedUtc: started), CancellationToken.None);

        await dispatcher.TickAsync(CancellationToken.None);
        Assert.DoesNotContain("heartbeat:n1", ledger.Calls);
        clock.Advance(TimeSpan.FromSeconds(5));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Contains("heartbeat:n1", ledger.Calls);
        Assert.Equal(1, ledger.Calls.Count(c => c == "heartbeat:n1"));

        // A restart stamped after the node started is relayed on the next flush; one stamped before is ignored.
        ledger.RequestRestart("n1", started.AddSeconds(-10));
        clock.Advance(TimeSpan.FromSeconds(5));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, startedUtc: started), CancellationToken.None);
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.False((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, startedUtc: started), CancellationToken.None)).RestartRequested);

        ledger.RequestRestart("n1", started.AddSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(5));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.True((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, startedUtc: started), CancellationToken.None)).RestartRequested);
    }

    [Fact]
    public async Task Tasks_AreHandedOut_Completed_AndExpiredByTheTick()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var options = Make.Options(leaseSeconds: 60, longPollSeconds: 10);
        options.TaskQueuedExpiryMinutes = 15;
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, options);
        var fresh = Make.Task(clock.GetUtcNow().UtcDateTime);
        var stale = Make.Task(clock.GetUtcNow().UtcDateTime.AddMinutes(-20), "nobody");
        ledger.AddQueuedTask(fresh);
        ledger.AddQueuedTask(stale);
        dispatcher.NotifyTaskEnqueued(fresh);
        dispatcher.NotifyTaskEnqueued(stale);

        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, freeTasks: 2), CancellationToken.None);
        Assert.Equal(fresh.TaskId, Assert.Single(poll.Tasks).TaskId);
        Assert.True(await dispatcher.RecordTaskOutcomeAsync(
            fresh.TaskId, new TaskOutcomeRequest("n1", TaskOutcomeKind.Succeeded, null, "{}"), CancellationToken.None));
        Assert.Equal("succeeded", ledger.Tasks[fresh.TaskId].Status);

        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.Equal("failed", ledger.Tasks[stale.TaskId].Status);
        Assert.Empty(dispatcher.Snapshot().QueuedTasks);
    }

    [Fact]
    public async Task Tasks_ALapsedLeaseIsRequeued_AndTheNodeIsToldItWasRevoked()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var task = Make.Task(clock.GetUtcNow().UtcDateTime);
        ledger.AddQueuedTask(task);
        dispatcher.NotifyTaskEnqueued(task);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, freeTasks: 1), CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);

        Assert.Equal("queued", ledger.Tasks[task.TaskId].Status);
        var zombie = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, holdingTasks: [task.TaskId]), CancellationToken.None);
        Assert.Equal([task.TaskId], zombie.RevokedTasks);
        Assert.False(await dispatcher.RecordTaskOutcomeAsync(
            task.TaskId, new TaskOutcomeRequest("n1", TaskOutcomeKind.Succeeded, null, "{}"), CancellationToken.None));
    }

    [Fact]
    public async Task Poll_HandsOutTheExecutionSpec_ReadFromTheLedgerAsPartOfTheJournaling()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        var row = ledger.AddQueued(run);
        row.Spec = Make.Spec("customers_02_ing", "feedbeef");
        var task = Make.Task(T0);
        ledger.AddQueuedTask(task).Spec = new TaskSpec("listObjects", "@dwh", """{"schema":"dbo"}""");
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        var poll = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1, freeTasks: 1), CancellationToken.None);

        var handout = Assert.Single(poll.Runs);
        Assert.Same(row.Spec, handout.Spec);
        Assert.Equal(
            ("customers_02_ing", "feedbeef", "flows/customers_02_ing.flow.yaml", "${env:GIT_TOKEN}"),
            (handout.Spec.FlowName, handout.Spec.FlowVersionHash, handout.Spec.PipelineRelativePath, handout.Spec.CredentialReference));
        Assert.True(handout.Spec.Parameters.IsDefault);
        var taskHandout = Assert.Single(poll.Tasks);
        Assert.Equal(("listObjects", "@dwh"), (taskHandout.Spec.Operation, taskHandout.Spec.SourceRef));
    }

    [Fact]
    public async Task FlowVersion_IsServedFromTheLedger_AndRefusedWhilePassive()
    {
        var ledger = new FakeLedger();
        ledger.FlowVersions["abc123"] = "name: orders_01_ing\n";
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        Assert.Equal("name: orders_01_ing\n", await dispatcher.LoadFlowVersionAsync("abc123", CancellationToken.None));
        Assert.Null(await dispatcher.LoadFlowVersionAsync("missing", CancellationToken.None));

        dispatcher.Deactivate();
        await Assert.ThrowsAsync<DispatchInactiveException>(() => dispatcher.LoadFlowVersionAsync("abc123", CancellationToken.None));
    }

    [Fact]
    public async Task RunContext_IsAnsweredOnlyForTheHolder_AndNotHeldForAStaleOrUnknownLease()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        var row = ledger.AddQueued(run);
        row.Context = new RunContextResponse(
            true,
            new RelationalObject { Database = "dwh", Schema = "ods", Name = "Orders" },
            new LandingReset { Authorized = true, Reason = "every consumer caught up" });
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var handout = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None)).Runs);

        var request = new RunContextRequest("n1", handout.Attempt, "pre", "Orders", true, true);
        var answer = await dispatcher.ResolveRunContextAsync(run.RunId, request, CancellationToken.None);

        Assert.True(answer.Held);
        Assert.Equal("[dwh].[ods].[Orders]", answer.WatermarkSourceTable!.QualifiedName);
        Assert.True(answer.LandingReset!.Authorized);
        Assert.Contains($"context:{run.RunId}:n1:1", ledger.Calls);

        // A stale attempt, another node, or an unknown run is refused in memory: the ledger is never asked.
        var asked = ledger.Calls.Count(c => c.StartsWith("context:", StringComparison.Ordinal));
        Assert.False((await dispatcher.ResolveRunContextAsync(run.RunId, request with { Attempt = 2 }, CancellationToken.None)).Held);
        Assert.False((await dispatcher.ResolveRunContextAsync(run.RunId, request with { Node = "n2" }, CancellationToken.None)).Held);
        Assert.False((await dispatcher.ResolveRunContextAsync(Guid.NewGuid(), request, CancellationToken.None)).Held);
        Assert.Equal(asked, ledger.Calls.Count(c => c.StartsWith("context:", StringComparison.Ordinal)));

        // Once the lease lapses and the run is requeued, the old holder is refused too.
        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.False((await dispatcher.ResolveRunContextAsync(run.RunId, request, CancellationToken.None)).Held);
    }

    [Fact]
    public async Task Trace_IsAppendedUnderTheFence_AndRefusedOnceTheLeaseIsGone()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var run = Make.Run(T0);
        var row = ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock, Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var handout = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None)).Runs);

        Assert.True(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n1", handout.Attempt), CancellationToken.None));
        Assert.True(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n1", handout.Attempt, ordinal: 2), CancellationToken.None));
        Assert.Equal([1, 2], row.Trace.Select(b => b.Statements[0].Ordinal));

        // A superseded or foreign holder's batch is refused in memory and never reaches the journal.
        var writes = ledger.WriteCount;
        Assert.False(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n1", handout.Attempt + 1), CancellationToken.None));
        Assert.False(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n2", handout.Attempt), CancellationToken.None));
        Assert.Equal(writes, ledger.WriteCount);
        Assert.Equal(2, row.Trace.Count);

        // The lease lapses: the run is requeued and the old holder's stream ends; the successor's is accepted.
        clock.Advance(TimeSpan.FromSeconds(61));
        await dispatcher.TickAsync(CancellationToken.None);
        Assert.False(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n1", handout.Attempt, ordinal: 3), CancellationToken.None));
        var successor = Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs);
        Assert.True(await dispatcher.AppendRunTraceAsync(run.RunId, Make.Trace("n2", successor.Attempt), CancellationToken.None));
        Assert.Equal(("n2", 2), (row.Trace[^1].Node, row.Trace[^1].Attempt));
    }

    [Fact]
    public void Options_RejectALeaseShorterThanTwoLongPolls_AndOtherImpossibleSettings()
    {
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { LeaseSeconds = 30, LongPollSeconds = 30 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { LongPollSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { LongPollSeconds = NodeProtocol.MaxWaitSeconds + 1, LeaseSeconds = 1000 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { ReconcileSeconds = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { OwnershipTtlSeconds = 10, OwnershipRenewSeconds = 10 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new DispatchOptions { MaxExecutionAttempts = 0 }.Validate());
        new DispatchOptions().Validate();
    }
}
