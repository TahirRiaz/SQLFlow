using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// Draining a node: the managed fleet's only way to make a worker leave. A draining node is handed nothing, and it is
/// told to exit (through the restart flag every worker already honours) only on a poll that shows it holding nothing,
/// so a worker never leaves with a run in hand. With no node drained, every poll answers exactly as before.
/// </summary>
public sealed class DrainTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task DrainingANodeTheRegistryHasNotHeardFrom_DoesNothing()
    {
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), ManualClock.At());

        Assert.False(dispatcher.DrainNode("never-polled"));
        Assert.False(dispatcher.Nodes.IsDraining("never-polled"));
    }

    [Fact]
    public void AnInactiveDispatcher_DrainsNothing()
    {
        using var dispatcher = Make.Dispatcher(new FakeLedger(), ManualClock.At());

        Assert.False(dispatcher.DrainNode("n1"));
    }

    [Fact]
    public async Task ADrainingNode_IsHandedNothing_AndAnotherNodeTakesTheWork()
    {
        var ledger = new FakeLedger();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At());

        // Register both nodes without handing anything out yet.
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);
        Assert.True(dispatcher.DrainNode("n1"));

        var drained = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        Assert.Empty(drained.Runs);
        Assert.True(drained.RestartRequested); // it holds nothing, so it is told to leave

        var other = await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 4), CancellationToken.None);
        Assert.Equal(run.RunId, Assert.Single(other.Runs).RunId);
        Assert.Equal("n2", ledger.Runs[run.RunId].Node);
    }

    [Fact]
    public async Task ADrainingNodeHoldingARun_IsNotToldToExit_UntilTheRunIsDone()
    {
        var ledger = new FakeLedger();
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        ledger.AddQueued(Make.Run(T0.AddSeconds(1)));
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At());

        var first = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 1), CancellationToken.None);
        var held = Assert.Single(first.Runs);
        Assert.True(dispatcher.DrainNode("n1"));

        var whileBusy = await dispatcher.PollAsync(
            Make.Poll("n1", freeRuns: 3, holding: [new HeldRun(held.RunId, held.Attempt)]), CancellationToken.None);
        Assert.False(whileBusy.RestartRequested);
        Assert.Empty(whileBusy.Runs); // the second queued run stays for another node
        Assert.Empty(whileBusy.RevokedRuns); // its lease is renewed as usual

        await dispatcher.RecordRunOutcomeAsync(
            held.RunId, new RunOutcomeRequest("n1", held.Attempt, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);

        var done = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        Assert.True(done.RestartRequested);
        Assert.Empty(done.Runs);
        Assert.Equal("succeeded", ledger.Runs[held.RunId].Status);
    }

    [Fact]
    public async Task ADrainingNodeHoldingATask_IsNotToldToExit()
    {
        var ledger = new FakeLedger();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At());
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);
        dispatcher.DrainNode("n1");

        var answer = await dispatcher.PollAsync(
            Make.Poll("n1", freeRuns: 4, holdingTasks: [Guid.NewGuid()]), CancellationToken.None);

        Assert.False(answer.RestartRequested);
    }

    [Fact]
    public async Task ANodeThatKeepsPollingAfterTheExitFlag_IsToldAgainOnlyOncePerLongPoll()
    {
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), clock);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);
        dispatcher.DrainNode("n1");

        Assert.True((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).RestartRequested);
        Assert.False((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).RestartRequested);

        clock.Advance(dispatcher.Options.LongPoll);
        Assert.True((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).RestartRequested);

        var view = Assert.Single(dispatcher.Nodes.Snapshot(clock.GetUtcNow().UtcDateTime));
        Assert.Equal(T0, view.ExitSignalledUtc); // the first time it was told is what is kept
    }

    [Fact]
    public async Task DrainingAParkedIdleNode_WakesItsPoll_WithTheExitFlag()
    {
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), ManualClock.At());
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var parked = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4, waitSeconds: 30), cts.Token);
        await Task.Delay(50, CancellationToken.None);
        Assert.False(parked.IsCompleted);

        Assert.True(dispatcher.DrainNode("n1"));
        var answer = await parked;

        Assert.True(answer.RestartRequested);
        Assert.Empty(answer.Runs);
    }

    [Fact]
    public async Task ADrainingNode_ServesNoPool_SoAQueuedRunReadsNoEligibleNode()
    {
        var ledger = new FakeLedger();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At());
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);
        dispatcher.DrainNode("n1");
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        dispatcher.NotifyRunsEnqueued([run]);

        var queued = Assert.Single(dispatcher.Snapshot().QueuedRuns);
        Assert.Equal(DispatchBlockReasons.NoEligibleNode, queued.Blocked);
        Assert.Equal((0, 0), dispatcher.Nodes.Capacity(string.Empty, T0));
    }

    [Fact]
    public async Task ANewIncarnationUnderTheSameName_IsNotDraining()
    {
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), clock);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0, startedUtc: T0.AddHours(-1)), CancellationToken.None);
        dispatcher.DrainNode("n1");

        var replacement = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4, startedUtc: T0), CancellationToken.None);

        Assert.False(replacement.RestartRequested);
        Assert.False(dispatcher.Nodes.IsDraining("n1"));
    }

    [Fact]
    public async Task IdleTracking_StartsAtTheFirstEmptyPoll_AndEndsWithAHandOut()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);

        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        Assert.Equal(T0, View(dispatcher, clock).IdleSinceUtc);

        var run = Make.Run(T0);
        ledger.AddQueued(run);
        dispatcher.NotifyRunsEnqueued([run]);
        var handed = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        Assert.Single(handed.Runs);
        Assert.Null(View(dispatcher, clock).IdleSinceUtc);

        var held = handed.Runs[0];
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 3, holding: [new HeldRun(held.RunId, held.Attempt)]), CancellationToken.None);
        Assert.Null(View(dispatcher, clock).IdleSinceUtc);
        Assert.Equal(1, View(dispatcher, clock).BusyRuns);

        await dispatcher.RecordRunOutcomeAsync(
            held.RunId, new RunOutcomeRequest("n1", held.Attempt, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);
        Assert.Equal(T0.AddMinutes(3), View(dispatcher, clock).IdleSinceUtc);
    }

    [Fact]
    public async Task WithNothingDrained_APollAnswersAsItAlwaysHas()
    {
        var ledger = new FakeLedger();
        ledger.AddQueued(Make.Run(T0));
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At());

        var answer = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);

        Assert.Single(answer.Runs);
        Assert.False(answer.RestartRequested);
        var view = Assert.Single(dispatcher.Nodes.Snapshot(T0));
        Assert.Null(view.DrainRequestedUtc);
        Assert.Null(view.ExitSignalledUtc);
    }

    [Fact]
    public void TheExitSignal_NeedsADrainAndAnEmptyNode()
    {
        var registry = new NodeRegistry();
        registry.Touch(Make.Poll("n1"), [], T0);

        Assert.False(registry.TryTakeExitSignal("n1", holdsNothing: true, T0, TimeSpan.FromSeconds(30)));
        Assert.True(registry.Drain("n1", T0));
        Assert.False(registry.TryTakeExitSignal("n1", holdsNothing: false, T0, TimeSpan.FromSeconds(30)));
        Assert.True(registry.TryTakeExitSignal("n1", holdsNothing: true, T0, TimeSpan.FromSeconds(30)));
        Assert.False(registry.TryTakeExitSignal("unknown", holdsNothing: true, T0, TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ASecondDrain_KeepsTheFirstRequestTime()
    {
        var registry = new NodeRegistry();
        registry.Touch(Make.Poll("n1"), [], T0);

        registry.Drain("n1", T0);
        registry.Drain("n1", T0.AddMinutes(5));

        Assert.Equal(T0, Assert.Single(registry.Snapshot(T0)).DrainRequestedUtc);
    }

    [Fact]
    public async Task TheExitSignal_IsRecorded_EvenWhileAnOperatorRestartIsPending()
    {
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), clock);
        await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 0), CancellationToken.None);
        dispatcher.Nodes.SetRestartRequested("n1", T0);
        dispatcher.DrainNode("n1");

        var answer = await dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4), CancellationToken.None);

        Assert.True(answer.RestartRequested);
        Assert.Equal(T0, View(dispatcher, clock).ExitSignalledUtc); // so a node that ignores both is still stopped in time
    }

    [Fact]
    public async Task AWorkWakeThatReachesADrainingPoll_IsHandedOnToAPollThatCanTakeTheWork()
    {
        var state = new DispatchState();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var other = state.WaitAsync("n2", [], wantsWork: true, TimeSpan.FromSeconds(30), cts.Token);
        var notWanting = state.WaitAsync("n3", [], wantsWork: false, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(50, CancellationToken.None);

        state.PassOnWorkWake([]);

        Assert.Equal(PollWake.Work, await other);
        await Task.Delay(50, CancellationToken.None);
        Assert.False(notWanting.IsCompleted); // a poll that wants no work is never woken for work
        state.WakeAll();
        await notWanting;
    }

    [Fact]
    public async Task AWorkWakeThatChoseADrainingPoll_ReachesAPollThatTakesTheRun()
    {
        var ledger = new FakeLedger();
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, ManualClock.At(), Make.Options(leaseSeconds: 60, longPollSeconds: 10));
        var draining = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4, waitSeconds: 10), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 1);
        var other = dispatcher.PollAsync(Make.Poll("n2", freeRuns: 4, waitSeconds: 10), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 2);

        // The drain is marked without its wake, as when it lands between the parked poll's last pass and its wait: the
        // run's wake picks the oldest waiter, the draining one, which must hand it on.
        Assert.True(dispatcher.Nodes.Drain("n1", T0));
        var run = Make.Run(T0);
        ledger.AddQueued(run);
        dispatcher.NotifyRunsEnqueued([run]);

        var taken = await other.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(run.RunId, Assert.Single(taken.Runs).RunId);
        Assert.True((await draining.WaitAsync(TimeSpan.FromSeconds(5))).RestartRequested);
    }

    [Fact]
    public async Task TheDrainsOwnWake_IsNotHandedOn()
    {
        var clock = ManualClock.At();
        using var dispatcher = await Make.ActiveDispatcherAsync(new FakeLedger(), clock, Make.Options(leaseSeconds: 60, longPollSeconds: 30));
        var other = dispatcher.PollAsync(Make.Poll("n2", freeRuns: 4, waitSeconds: 30), CancellationToken.None);
        var drained = dispatcher.PollAsync(Make.Poll("n1", freeRuns: 4, waitSeconds: 30), CancellationToken.None);
        await Wait.UntilAsync(() => dispatcher.Snapshot().Waiters == 2);
        var parkedAt = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.True(dispatcher.DrainNode("n1"));
        Assert.True((await drained.WaitAsync(TimeSpan.FromSeconds(5))).RestartRequested);
        await Task.Delay(200, CancellationToken.None);

        // A poll that is woken passes through the registry again; n2 was not woken, so it was last seen when it parked.
        var n2 = Assert.Single(dispatcher.Nodes.Snapshot(clock.GetUtcNow().UtcDateTime), n => n.Name == "n2");
        Assert.Equal(parkedAt, n2.LastSeenUtc);
        Assert.False(other.IsCompleted);

        dispatcher.Deactivate();
        await Assert.ThrowsAsync<DispatchInactiveException>(() => other.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task AHandedOnWake_ReachesAPollOfTheRightPool()
    {
        var state = new DispatchState();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var heavy = state.WaitAsync("n2", ["heavy"], wantsWork: true, TimeSpan.FromSeconds(30), cts.Token);
        await Task.Delay(50, CancellationToken.None);

        state.PassOnWorkWake(["heavy"]);

        Assert.Equal(PollWake.Work, await heavy);
    }

    private static NodeView View(Dispatcher dispatcher, ManualClock clock)
        => Assert.Single(dispatcher.Nodes.Snapshot(clock.GetUtcNow().UtcDateTime));
}
