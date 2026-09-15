using SqlFlow.Core.Runs;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// Fan-out run groups in the dispatcher: the pipeline gate compares execution families, so a root's members are handed
/// out beside the root while any other run of the flow waits until the whole family has left; every fan-out call is
/// fenced on the root's lease; a repeated request rejoins the members already enqueued; a root that ends takes its
/// unfinished members with it; and a request no dispatcher could honor is refused before anything is journaled.
/// </summary>
public sealed class FanOutTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    private static readonly RunParameters[] TwoLoads = [new() { Operation = "load" }, new() { Operation = "load" }];

    [Fact]
    public void TheGate_AdmitsARootsMembers_ButNoRunOfAnotherFamily()
    {
        var state = new DispatchState();
        var pipeline = Guid.NewGuid();
        var root = Make.Run(T0, pipeline: pipeline);
        Assert.True(state.AddLeasedRun(root, "n1", T0, T0.AddMinutes(1)));

        var stranger = Make.Run(T0, pipeline: pipeline);
        var otherFamily = Make.Run(T0, pipeline: pipeline) with { FanOutRoot = Guid.NewGuid() };
        var first = Make.Run(T0.AddSeconds(1), pipeline: pipeline) with { FanOutRoot = root.RunId };
        var second = Make.Run(T0.AddSeconds(2), pipeline: pipeline) with { FanOutRoot = root.RunId };
        foreach (var run in new[] { stranger, otherFamily, first, second })
        {
            state.AddQueuedRun(run);
        }

        var picks = state.ReserveRuns("n2", [], 4);

        Assert.Equal([first.RunId, second.RunId], picks.Select(p => p.RunId));
    }

    [Fact]
    public void AnotherRunOfTheFlow_WaitsUntilTheWholeFamilyHasLeftThePipeline()
    {
        var state = new DispatchState();
        var pipeline = Guid.NewGuid();
        var root = Make.Run(T0, pipeline: pipeline);
        var member = Make.Run(T0, pipeline: pipeline) with { FanOutRoot = root.RunId };
        state.AddLeasedRun(root, "n1", T0, T0.AddMinutes(1));
        state.AddLeasedRun(member, "n2", T0, T0.AddMinutes(1));
        var stranger = Make.Run(T0.AddSeconds(1), pipeline: pipeline);
        state.AddQueuedRun(stranger);

        Assert.Empty(state.ReserveRuns("n3", [], 1));

        state.RemoveRun(root.RunId);
        Assert.Empty(state.ReserveRuns("n3", [], 1));

        state.RemoveRun(member.RunId);
        Assert.Equal([stranger.RunId], state.ReserveRuns("n3", [], 1).Select(p => p.RunId));
    }

    [Fact]
    public async Task AFanOut_IsFencedOnTheRootsLease_RejoinsOnRepeat_AndItsMembersRunBesideTheRoot()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var now = clock.GetUtcNow().UtcDateTime;
        var pipeline = Guid.NewGuid();
        var root = Make.Run(now, pipeline: pipeline);
        ledger.AddQueued(root);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var attempt = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).Runs).Attempt;

        var stranger = Make.Run(now, pipeline: pipeline);
        ledger.AddQueued(stranger);
        dispatcher.NotifyRunsEnqueued([stranger]);

        // Neither another node nor a stale attempt may fan the root out, and nothing is journaled for them.
        Assert.False((await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n2", attempt, TwoLoads), CancellationToken.None)).Held);
        Assert.False((await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt + 1, TwoLoads), CancellationToken.None)).Held);
        Assert.DoesNotContain(ledger.Calls, call => call.StartsWith("fan-out:", StringComparison.Ordinal));

        var fanOut = await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, TwoLoads), CancellationToken.None);
        Assert.True(fanOut.Held);
        Assert.NotNull(fanOut.GroupId);
        Assert.Equal(2, fanOut.RunIds.Count);

        // A repeated request (a retried call, or the root executing again) gets the same members back.
        var again = await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, TwoLoads), CancellationToken.None);
        Assert.Equal(fanOut.GroupId, again.GroupId);
        Assert.Equal(fanOut.RunIds, again.RunIds);

        // The members are handed out while the root runs; the other run of the flow is not.
        var handed = await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 3), CancellationToken.None);
        Assert.Equal(fanOut.RunIds.Order(), handed.Runs.Select(r => r.RunId).Order());

        var members = await dispatcher.LoadFanOutStateAsync(
            root.RunId, fanOut.GroupId.Value, new FanOutFence("n1", attempt), CancellationToken.None);
        Assert.True(members.Held);
        Assert.Equal([1, 2], members.Members.Select(m => m.Slot));
        Assert.All(members.Members, m => Assert.Equal("running", m.Status));
        Assert.False((await dispatcher.LoadFanOutStateAsync(
            root.RunId, fanOut.GroupId.Value, new FanOutFence("n1", attempt + 1), CancellationToken.None)).Held);
    }

    [Fact]
    public async Task ARootThatEnds_TakesItsUnfinishedMembersWithIt()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var now = clock.GetUtcNow().UtcDateTime;
        var root = Make.Run(now);
        ledger.AddQueued(root);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var attempt = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).Runs).Attempt;
        var fanOut = await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, TwoLoads), CancellationToken.None);
        var running = Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs);
        var queued = Assert.Single(fanOut.RunIds, id => id != running.RunId);

        Assert.Equal(RunOutcomeStatus.Recorded, await dispatcher.RecordRunOutcomeAsync(
            root.RunId, new RunOutcomeRequest("n1", attempt, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None));

        // The queued member is gone and the running one's node hears the cancel on its next poll.
        Assert.Equal("cancelled", ledger.Runs[queued].Status);
        Assert.Empty((await dispatcher.PollAsync(Make.Poll("n3", freeRuns: 1), CancellationToken.None)).Runs);
        var heard = await dispatcher.PollAsync(
            Make.Poll("n2", freeRuns: 0, holding: [new HeldRun(running.RunId, running.Attempt)]), CancellationToken.None);
        Assert.Equal([running.RunId], heard.CancelRuns);
    }

    [Fact]
    public async Task ARootsOwnCancel_OfItsFanOut_CancelsQueuedMembersAndFlagsRunningOnes()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var root = Make.Run(clock.GetUtcNow().UtcDateTime);
        ledger.AddQueued(root);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var attempt = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).Runs).Attempt;
        var fanOut = await dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, TwoLoads), CancellationToken.None);
        var running = Assert.Single((await dispatcher.PollAsync(Make.Poll("n2", freeRuns: 1), CancellationToken.None)).Runs);

        Assert.False((await dispatcher.CancelFanOutAsync(
            root.RunId, fanOut.GroupId!.Value, new FanOutFence("n2", attempt), CancellationToken.None)).Held);
        var cancelled = await dispatcher.CancelFanOutAsync(
            root.RunId, fanOut.GroupId.Value, new FanOutFence("n1", attempt), CancellationToken.None);

        Assert.Equal((true, 1, 1), (cancelled.Held, cancelled.CancelledQueued, cancelled.RequestedRunning));
        var heard = await dispatcher.PollAsync(
            Make.Poll("n2", freeRuns: 1, holding: [new HeldRun(running.RunId, running.Attempt)]), CancellationToken.None);
        Assert.Equal([running.RunId], heard.CancelRuns);
        Assert.Empty(heard.Runs);
    }

    [Fact]
    public async Task ARequestNoDispatcherCouldHonor_IsRefusedBeforeAnythingIsJournaled()
    {
        var ledger = new FakeLedger();
        var clock = ManualClock.At();
        var root = Make.Run(clock.GetUtcNow().UtcDateTime);
        ledger.AddQueued(root);
        using var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
        var attempt = Assert.Single((await dispatcher.PollAsync(Make.Poll("n1"), CancellationToken.None)).Runs).Attempt;

        var empty = await Assert.ThrowsAsync<FanOutRefusedException>(() =>
            dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, []), CancellationToken.None));
        Assert.Contains("at least one member", empty.Message, StringComparison.Ordinal);

        var mixed = await Assert.ThrowsAsync<FanOutRefusedException>(() => dispatcher.EnqueueFanOutAsync(
            root.RunId,
            new FanOutRequest("n1", attempt, [new RunParameters { Operation = "load" }, new RunParameters { Operation = "check" }]),
            CancellationToken.None));
        Assert.Contains("member 2 asks for 'check' where member 1 asks for 'load'", mixed.Message, StringComparison.Ordinal);

        var tooMany = Enumerable.Repeat(RunParameters.None, NodeProtocol.MaxFanOutMembers + 1).ToList();
        var overflow = await Assert.ThrowsAsync<FanOutRefusedException>(() =>
            dispatcher.EnqueueFanOutAsync(root.RunId, new FanOutRequest("n1", attempt, tooMany), CancellationToken.None));
        Assert.Contains($"at most {NodeProtocol.MaxFanOutMembers} members", overflow.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(ledger.Calls, call => call.StartsWith("fan-out:", StringComparison.Ordinal));
    }
}
