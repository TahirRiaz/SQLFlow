using SqlFlow.Dispatch;
using SqlFlow.Dispatch.ManagedWorkers;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// The managed fleet's rules, one snapshot at a time (docs/managed-workers-plan.md): every managed pool keeps at least
/// its minimum, demand starts only what free slots do not cover, idle surplus leaves after its idle time, a worker due
/// for replacement is replaced before it stops taking work, nothing is stopped while it can still leave by itself or
/// while the launcher's picture is stale, and nothing a takeover cannot know is held against a worker.
/// </summary>
public sealed class FleetPlannerTests
{
    private static readonly DateTime Now = new(2026, 10, 13, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = Now.AddHours(-2);

    private static ManagedFleetSettings Settings(
        int min = 1, int max = 10, int maxLaunches = 20, bool replaceOnStart = true, IReadOnlyList<ManagedPoolSettings>? named = null)
        => new()
        {
            Pools = [new ManagedPoolSettings(string.Empty, min, max), .. named ?? []],
            MaxLaunchesPerTick = maxLaunches,
            ReplaceWorkersOnStart = replaceOnStart,
        };

    private static FleetSnapshot Snapshot(
        IReadOnlyList<FleetWorker>? workers = null, IReadOnlyList<NodeView>? nodes = null,
        IReadOnlyDictionary<string, int>? eligible = null, IReadOnlyDictionary<string, int>? gui = null,
        bool listCurrent = true, DateTime? takeover = null, IReadOnlyDictionary<string, DateTime>? blocked = null,
        TimeSpan? timeLimit = null, DateTime? resumed = null)
        => new(
            Now, takeover ?? LongAgo, gui ?? new Dictionary<string, int>(),
            eligible ?? new Dictionary<string, int>(), nodes ?? [], workers ?? [], listCurrent,
            blocked ?? new Dictionary<string, DateTime>(), timeLimit, resumed);

    /// <summary>A worker this control plane started <paramref name="ageMinutes"/> ago, which the launcher lists as
    /// running (or in <paramref name="state"/>).</summary>
    private static FleetWorker Listed(
        string name, string pool = "", int ageMinutes = 30, LaunchedWorkerState state = LaunchedWorkerState.Running,
        bool launchedHere = true)
        => new(name, pool, Now.AddMinutes(-ageMinutes), state, Listed: true, Pending: false, LaunchedHere: launchedHere);

    /// <summary>A node that polled just now: <paramref name="busy"/> runs held of <paramref name="slots"/>, idle since
    /// <paramref name="idleMinutes"/> ago when it holds nothing.</summary>
    private static NodeView Node(
        string name, string pool = "", int slots = 4, int busy = 0, int idleMinutes = 0, bool online = true,
        DateTime? lastSeen = null, DateTime? drain = null, DateTime? exitSignalled = null, int startedMinutesAgo = 30,
        DateTime? restartRequested = null, int? free = null)
        => new(
            name, "test", pool.Length == 0 ? [] : [pool], slots, free ?? (slots - busy), 2, 2, Now.AddMinutes(-startedMinutesAgo),
            lastSeen ?? (online ? Now : Now.AddMinutes(-5)), Now.AddMinutes(-startedMinutesAgo), restartRequested, online, busy, 0,
            busy == 0 ? Now.AddMinutes(-idleMinutes) : null, drain, exitSignalled);

    private static Dictionary<string, int> Pools(params (string Pool, int Count)[] entries)
        => entries.ToDictionary(e => e.Pool, e => e.Count, StringComparer.Ordinal);

    // ------------------------------------------------------------------------------------------------ minimum ----

    [Fact]
    public void AnEmptyPool_StartsItsMinimum()
    {
        var plan = FleetPlanner.Plan(Snapshot(), Settings(min: 2));

        Assert.Equal(2, plan.Launches.Count);
        Assert.All(plan.Launches, l => Assert.Equal(string.Empty, l.Pool));
        Assert.Contains("at least 2", plan.Launches[0].Reason, StringComparison.Ordinal);
        Assert.Equal(2, Assert.Single(plan.Pools).Target);
    }

    [Fact]
    public void APoolAtItsMinimumWithNothingQueued_IsLeftAlone()
    {
        var plan = FleetPlanner.Plan(Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001", idleMinutes: 60)]), Settings());

        Assert.Empty(plan.Launches);
        Assert.Empty(plan.Drains);
        Assert.Empty(plan.Stops);
    }

    [Fact]
    public void TheGuiFloor_RaisesTheMinimum_UpToTheMaximum()
    {
        Assert.Equal(3, FleetPlanner.Plan(Snapshot(gui: Pools(("", 3))), Settings()).Launches.Count);
        Assert.Equal(4, FleetPlanner.Plan(Snapshot(gui: Pools(("", 50))), Settings(max: 4)).Launches.Count);
        Assert.Single(FleetPlanner.Plan(Snapshot(gui: Pools(("", 0))), Settings()).Launches);
    }

    [Fact]
    public void ANamedPool_KeepsItsOwnMinimum()
    {
        var settings = Settings(named: [new ManagedPoolSettings("heavy", 1, 4)]);
        var plan = FleetPlanner.Plan(Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001")]), settings);

        var launch = Assert.Single(plan.Launches);
        Assert.Equal("heavy", launch.Pool);
    }

    [Fact]
    public void AnEndedWorker_IsNotCounted_SoTheMinimumIsRestored()
    {
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001", state: LaunchedWorkerState.Ended)]), Settings());

        Assert.Single(plan.Launches);
        Assert.Equal(FleetWorkerPhase.Ended, Assert.Single(plan.Workers).Phase);
    }

    // ------------------------------------------------------------------------------------------------- demand ----

    [Fact]
    public void QueuedRunsNoFreeSlotCovers_StartWorkersForTheRest()
    {
        // One worker with 4 free slots, 10 eligible runs: 6 have no slot, which is 2 workers of 4.
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001")], eligible: Pools(("", 10))),
            Settings());

        Assert.Equal(2, plan.Launches.Count);
        Assert.Contains("6 queued run(s) have no free slot", plan.Launches[0].Reason, StringComparison.Ordinal);
        Assert.Equal(6, Assert.Single(plan.Pools).UnservedRuns);
    }

    [Fact]
    public void QueuedRunsAnIdleWorkerCanTake_StartNothing()
    {
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001")], eligible: Pools(("", 3))),
            Settings());

        Assert.Empty(plan.Launches);
    }

    [Fact]
    public void WorkersStillStarting_CountAsCapacity_SoNoSecondWaveIsStarted()
    {
        // One busy worker, two starting with 4 slots each: 8 queued runs are covered.
        var workers = new[]
        {
            Listed("worker-default-0000000001"),
            Listed("worker-default-0000000002", ageMinutes: 1, state: LaunchedWorkerState.Starting),
            Listed("worker-default-0000000003", ageMinutes: 1, state: LaunchedWorkerState.Starting),
        };
        var plan = FleetPlanner.Plan(
            Snapshot(workers, [Node("worker-default-0000000001", busy: 4)], eligible: Pools(("", 8))), Settings());

        Assert.Empty(plan.Launches);
        Assert.Equal(2, Assert.Single(plan.Pools).Starting);
    }

    [Fact]
    public void Demand_NeverTakesAPoolAboveItsMaximum()
    {
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001", busy: 4)], eligible: Pools(("", 100))),
            Settings(max: 3));

        Assert.Equal(2, plan.Launches.Count);
    }

    [Fact]
    public void ATick_StartsNoMoreThanItsCap_AndServesMinimumsFirst()
    {
        var settings = Settings(max: 50, maxLaunches: 5, named: [new ManagedPoolSettings("heavy", 2, 10)]);
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001", busy: 4)], eligible: Pools(("", 400))),
            settings);

        Assert.Equal(5, plan.Launches.Count);
        Assert.Equal(["heavy", "heavy", "", "", ""], plan.Launches.Select(l => l.Pool));
    }

    [Fact]
    public void ANamedPoolsWorkers_ServeTheDefaultPoolsRuns_ButNotTheOtherWayRound()
    {
        var settings = Settings(named: [new ManagedPoolSettings("heavy", 1, 4)]);
        var workers = new[] { Listed("worker-default-0000000001"), Listed("worker-heavy-0000000002", "heavy") };
        var nodes = new[] { Node("worker-default-0000000001", busy: 4), Node("worker-heavy-0000000002", "heavy") };

        // Four untargeted runs fit the heavy worker's free slots; four heavy runs do not fit a default worker's.
        var untargeted = FleetPlanner.Plan(Snapshot(workers, nodes, eligible: Pools(("", 4))), settings);
        Assert.Empty(untargeted.Launches);

        var nodesBusyHeavy = new[] { Node("worker-default-0000000001"), Node("worker-heavy-0000000002", "heavy", busy: 4) };
        var targeted = FleetPlanner.Plan(Snapshot(workers, nodesBusyHeavy, eligible: Pools(("heavy", 4))), settings);
        Assert.Equal("heavy", Assert.Single(targeted.Launches).Pool);
    }

    [Fact]
    public void AnUnmanagedNodesFreeSlots_CountAsCapacity_AndItIsNeverDrained()
    {
        var plan = FleetPlanner.Plan(
            Snapshot(
                [Listed("worker-default-0000000001")],
                [Node("worker-default-0000000001", busy: 4), Node("control-plane-host", idleMinutes: 120)],
                eligible: Pools(("", 4))),
            Settings());

        Assert.Empty(plan.Launches);
        Assert.DoesNotContain(plan.Drains, d => d.Name == "control-plane-host");
        Assert.Single(plan.Workers); // only managed workers are the fleet's
    }

    // ---------------------------------------------------------------------------------------------- shrinking ----

    [Fact]
    public void IdleSurplus_Drains_OldestFirst_DownToTheMinimum()
    {
        var workers = new[]
        {
            Listed("worker-default-0000000001", ageMinutes: 90),
            Listed("worker-default-0000000002", ageMinutes: 60),
            Listed("worker-default-0000000003", ageMinutes: 30),
        };
        var nodes = workers.Select(w => Node(w.Name, idleMinutes: 6)).ToArray();

        var plan = FleetPlanner.Plan(Snapshot(workers, nodes), Settings());

        Assert.Equal(["worker-default-0000000001", "worker-default-0000000002"], plan.Drains.Select(d => d.Name));
        Assert.All(plan.Drains, d => Assert.Contains("idle", d.Reason, StringComparison.Ordinal));
        Assert.Empty(plan.Launches);
    }

    [Fact]
    public void AWorkerIdleForLessThanTheIdleTime_Stays()
    {
        var workers = new[] { Listed("worker-default-0000000001"), Listed("worker-default-0000000002") };
        var nodes = workers.Select(w => Node(w.Name, idleMinutes: 4)).ToArray();

        Assert.Empty(FleetPlanner.Plan(Snapshot(workers, nodes), Settings()).Drains);
    }

    [Fact]
    public void IdleTime_CountsFromTheTakeoverAtTheEarliest()
    {
        var workers = new[] { Listed("worker-default-0000000001"), Listed("worker-default-0000000002") };
        var nodes = workers.Select(w => Node(w.Name, idleMinutes: 60)).ToArray();

        Assert.Empty(FleetPlanner.Plan(Snapshot(workers, nodes, takeover: Now.AddMinutes(-2)), Settings()).Drains);
    }

    [Fact]
    public void ABusyWorker_IsNeverDrainedForBeingSurplus()
    {
        var workers = new[] { Listed("worker-default-0000000001", ageMinutes: 90), Listed("worker-default-0000000002") };
        var nodes = new[] { Node("worker-default-0000000001", busy: 1), Node("worker-default-0000000002", idleMinutes: 30) };

        var plan = FleetPlanner.Plan(Snapshot(workers, nodes), Settings());

        Assert.Equal("worker-default-0000000002", Assert.Single(plan.Drains).Name);
    }

    [Fact]
    public void NothingIsDrained_WhileRunsAreQueuedForThePool()
    {
        var workers = new[] { Listed("worker-default-0000000001"), Listed("worker-default-0000000002") };
        var nodes = workers.Select(w => Node(w.Name, idleMinutes: 60)).ToArray();

        Assert.Empty(FleetPlanner.Plan(Snapshot(workers, nodes, eligible: Pools(("", 1))), Settings()).Drains);
    }

    [Fact]
    public void TheMinimumWorker_IsNeverDrained_HoweverLongItIdles()
    {
        var plan = FleetPlanner.Plan(
            Snapshot([Listed("worker-default-0000000001")], [Node("worker-default-0000000001", idleMinutes: 600)]), Settings(min: 1));

        Assert.Empty(plan.Drains);
    }

    // -------------------------------------------------------------------------------------------- replacement ----

    [Fact]
    public void AWorkerPastItsLifetime_IsReplacedFirst_AndKeepsTakingWorkMeanwhile()
    {
        var worker = Listed("worker-default-0000000001", ageMinutes: 13 * 60);
        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name, startedMinutesAgo: 13 * 60)]), Settings());

        Assert.Single(plan.Launches);
        Assert.Empty(plan.Drains);
        Assert.Equal(FleetWorkerPhase.Due, Assert.Single(plan.Workers).Phase);
    }

    [Fact]
    public void ADueWorker_Drains_OnceItsReplacementPolls()
    {
        var old = Listed("worker-default-0000000001", ageMinutes: 13 * 60);
        var fresh = Listed("worker-default-0000000002", ageMinutes: 2);
        var plan = FleetPlanner.Plan(
            Snapshot([old, fresh], [Node(old.Name, busy: 2, startedMinutesAgo: 13 * 60), Node(fresh.Name, startedMinutesAgo: 2)]),
            Settings());

        var drain = Assert.Single(plan.Drains);
        Assert.Equal(old.Name, drain.Name);
        Assert.Contains("older than 12 h", drain.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Launches);
    }

    [Fact]
    public void AMinimumEqualToTheMaximum_StillGetsItsReplacement()
    {
        var worker = Listed("worker-default-0000000001", ageMinutes: 13 * 60);
        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name, startedMinutesAgo: 13 * 60)]), Settings(min: 1, max: 1));

        Assert.Single(plan.Launches);
    }

    [Fact]
    public void WorkersOfAnotherBuild_AreReplaced_WhenSoConfigured()
    {
        // Another build of the control plane started it (its name carries that build's tag), however recently.
        var worker = Listed("worker-default-ffff000001", ageMinutes: 1, launchedHere: false) with { SameBuild = false };
        var nodes = new[] { Node(worker.Name) };

        var replacing = FleetPlanner.Plan(Snapshot([worker], nodes), Settings());
        Assert.Equal(FleetWorkerPhase.Due, Assert.Single(replacing.Workers).Phase);
        Assert.Single(replacing.Launches);
        Assert.Empty(replacing.Drains); // replaced first

        var keeping = FleetPlanner.Plan(Snapshot([worker], nodes), Settings(replaceOnStart: false));
        Assert.Equal(FleetWorkerPhase.Ready, Assert.Single(keeping.Workers).Phase);
        Assert.Empty(keeping.Launches);
    }

    [Fact]
    public void AWorkerOfThisBuild_StartedByAnotherProcess_IsKept_SoAFailoverKeepsTheFleet()
    {
        var worker = Listed("worker-default-0000000001", ageMinutes: 300, launchedHere: false);

        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name)]), Settings());

        Assert.Equal(FleetWorkerPhase.Ready, Assert.Single(plan.Workers).Phase);
        Assert.Empty(plan.Launches);
        Assert.Empty(plan.Drains);
    }

    [Fact]
    public void AWorkerThisControlPlaneStarted_IsNeverReplacedForBeingOld_BeforeItsLifetime()
    {
        var worker = Listed("worker-default-0000000001", ageMinutes: 600);

        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name)]), Settings());

        Assert.Equal(FleetWorkerPhase.Ready, Assert.Single(plan.Workers).Phase);
    }

    // ------------------------------------------------------------------------------------------------- stops -----

    [Fact]
    public void AWorkerThatHasNotPolledYet_IsStarting_UntilItsTimeout()
    {
        var starting = new FleetWorker("worker-default-0000000001", "", Now.AddMinutes(-3), LaunchedWorkerState.Starting, true, false);

        var plan = FleetPlanner.Plan(Snapshot([starting]), Settings());

        Assert.Equal(FleetWorkerPhase.Starting, Assert.Single(plan.Workers).Phase);
        Assert.Empty(plan.Launches);
        Assert.Empty(plan.Stops);
    }

    [Fact]
    public void AWorkerThatNeverPolls_IsStopped_AsAFailedStart()
    {
        var stuck = new FleetWorker("worker-default-0000000001", "", Now.AddMinutes(-11), LaunchedWorkerState.Starting, true, false, LaunchedHere: true);

        var plan = FleetPlanner.Plan(Snapshot([stuck], takeover: Now.AddHours(-1)), Settings());

        var stop = Assert.Single(plan.Stops);
        Assert.True(stop.FailedStart);
        Assert.Contains("has not polled", stop.Reason, StringComparison.Ordinal);
        Assert.Single(plan.Launches); // and the minimum is restored
    }

    [Fact]
    public void AWorkerStartedBeforeATakeover_ThatNeverPollsTheNewOwner_IsStopped_ButNotAsAFailedStart()
    {
        // This process started it in an earlier ownership period; it may have polled the owner in between.
        var earlier = new FleetWorker(
            "worker-default-0000000001", "", Now.AddHours(-2), LaunchedWorkerState.Running, true, false, LaunchedHere: true);

        var plan = FleetPlanner.Plan(Snapshot([earlier], takeover: Now.AddMinutes(-11)), Settings());

        var stop = Assert.Single(plan.Stops);
        Assert.False(stop.FailedStart);
        Assert.Contains("since this control plane took over", stop.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterATakeover_AWorkerIsGivenItsWholeTimeoutToPollTheNewOwner()
    {
        var old = new FleetWorker("worker-default-0000000001", "", Now.AddHours(-5), LaunchedWorkerState.Running, true, false);

        var plan = FleetPlanner.Plan(Snapshot([old], takeover: Now.AddMinutes(-1)), Settings());

        Assert.Empty(plan.Stops);
        Assert.Empty(plan.Launches); // counted as starting, so nothing is started beside it
        Assert.Equal(FleetWorkerPhase.Starting, Assert.Single(plan.Workers).Phase);
    }

    [Fact]
    public void AWorkerSilentPastTheTimeout_IsStopped_ButNotAsAFailedStart()
    {
        var worker = Listed("worker-default-0000000001");
        var healthy = Listed("worker-default-0000000002");
        var plan = FleetPlanner.Plan(
            Snapshot(
                [worker, healthy], [Node(worker.Name, online: false, lastSeen: Now.AddMinutes(-11)), Node(healthy.Name)],
                takeover: Now.AddHours(-1)),
            Settings());

        var stop = Assert.Single(plan.Stops);
        Assert.Equal(worker.Name, stop.Name);
        Assert.False(stop.FailedStart);
        Assert.Contains("silent", stop.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ASilentWorkerPastItsGrace_IsNotCountedTowardTheMinimum_SoAReplacementStarts_ButItIsNotStoppedYet()
    {
        var worker = Listed("worker-default-0000000001");
        var plan = FleetPlanner.Plan(
            Snapshot([worker], [Node(worker.Name, online: false, lastSeen: Now.AddMinutes(-3))]), Settings());

        Assert.Empty(plan.Stops);
        Assert.Equal(FleetWorkerPhase.Silent, Assert.Single(plan.Workers).Phase);
        Assert.Single(plan.Launches);
        Assert.Equal(1, Assert.Single(plan.Pools).Silent);
    }

    [Fact]
    public void AWorkerSilentForLessThanItsGrace_StillCountsTowardTheMinimum_SoABlipStartsNothing()
    {
        var worker = Listed("worker-default-0000000001");
        var plan = FleetPlanner.Plan(
            Snapshot([worker], [Node(worker.Name, online: false, lastSeen: Now.AddSeconds(-90))], eligible: Pools(("", 3))),
            Settings());

        Assert.Equal(FleetWorkerPhase.Silent, Assert.Single(plan.Workers).Phase);
        Assert.Empty(plan.Launches);
        Assert.Empty(plan.Stops);
    }

    [Fact]
    public void WhileTheControlPlaneHearsNoNode_NoWorkerIsStoppedForSilence_AndOnlyTheMinimumIsRestored()
    {
        // Every node the control plane knows went quiet together, long ago: the silence is its own or the network's.
        var a = Listed("worker-default-0000000001");
        var b = Listed("worker-default-0000000002");
        var nodes = new[]
        {
            Node(a.Name, online: false, lastSeen: Now.AddMinutes(-11)),
            Node(b.Name, online: false, lastSeen: Now.AddMinutes(-11)),
        };

        var plan = FleetPlanner.Plan(Snapshot([a, b], nodes, eligible: Pools(("", 40)), takeover: Now.AddHours(-1)), Settings(min: 2));

        Assert.Empty(plan.Stops);
        Assert.All(plan.Workers, w => Assert.Equal(FleetWorkerPhase.Silent, w.Phase));
        Assert.Equal(2, plan.Launches.Count); // the minimum, past the grace; nothing for the 40 queued runs
        Assert.All(plan.Launches, l => Assert.Contains("at least 2", l.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void OneNodeStillPolling_IsEnough_ForTheSilentOnesToBeStopped_AndForDemandToStart()
    {
        var silent = Listed("worker-default-0000000001");
        var healthy = Listed("worker-default-0000000002");
        var nodes = new[] { Node(silent.Name, online: false, lastSeen: Now.AddMinutes(-11)), Node(healthy.Name, busy: 4) };

        var plan = FleetPlanner.Plan(Snapshot([silent, healthy], nodes, eligible: Pools(("", 8)), takeover: Now.AddHours(-1)), Settings());

        Assert.Equal(silent.Name, Assert.Single(plan.Stops).Name);
        Assert.Equal(2, plan.Launches.Count);
    }

    [Fact]
    public void AFleetWithNoNodeYet_StartsForDemandAtOnce()
    {
        var plan = FleetPlanner.Plan(Snapshot(eligible: Pools(("", 8))), Settings());

        Assert.Equal(2, plan.Launches.Count);
    }

    [Fact]
    public void AfterAResume_SilenceCountsFromTheResume_SoAPauseStopsAndStartsNothing()
    {
        // The control plane stood still for twelve minutes; its workers have not had a chance to poll again yet.
        var a = Listed("worker-default-0000000001");
        var b = Listed("worker-default-0000000002");
        var nodes = new[]
        {
            Node(a.Name, online: false, lastSeen: Now.AddMinutes(-12)),
            Node(b.Name, online: false, lastSeen: Now.AddMinutes(-12)),
        };

        var plan = FleetPlanner.Plan(
            Snapshot([a, b], nodes, eligible: Pools(("", 8)), takeover: Now.AddHours(-1), resumed: Now.AddSeconds(-10)), Settings(min: 2));

        Assert.Empty(plan.Stops);
        Assert.Empty(plan.Launches);
        Assert.Empty(plan.Drains);
    }

    [Fact]
    public void AWorkerThatPolledAndThenLeftTheRegistry_IsSilent_NeverANeverPolledStart()
    {
        var gone = Listed("worker-default-0000000001", ageMinutes: 60) with { HasPolled = true };

        var silent = FleetPlanner.Plan(Snapshot([gone], takeover: Now.AddMinutes(-5)), Settings());
        Assert.Equal(FleetWorkerPhase.Silent, Assert.Single(silent.Workers).Phase);
        Assert.Empty(silent.Stops);

        var lost = FleetPlanner.Plan(Snapshot([gone], takeover: Now.AddMinutes(-11)), Settings());
        var stop = Assert.Single(lost.Stops);
        Assert.False(stop.FailedStart);
        Assert.Contains("silent", stop.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleButStillOnlineWorker_IsNotWhatAPoolIsLeftWith()
    {
        // Two idle workers, one of which last polled 50 s ago (inside the online minute, outside a long poll): the fresh
        // one is the only one that counts, so neither is drained for being surplus.
        var quiet = Listed("worker-default-0000000001", ageMinutes: 90);
        var fresh = Listed("worker-default-0000000002", ageMinutes: 30);
        var plan = FleetPlanner.Plan(
            Snapshot([quiet, fresh], [Node(quiet.Name, idleMinutes: 30, lastSeen: Now.AddSeconds(-50)), Node(fresh.Name, idleMinutes: 30)]),
            Settings());

        Assert.Empty(plan.Drains);
    }

    [Fact]
    public void WhenAPoolCanSpareAWorker_TheQuietOneGoesFirst()
    {
        var quiet = Listed("worker-default-0000000001", ageMinutes: 10);
        var fresh1 = Listed("worker-default-0000000002", ageMinutes: 90);
        var fresh2 = Listed("worker-default-0000000003", ageMinutes: 80);
        var plan = FleetPlanner.Plan(
            Snapshot(
                [quiet, fresh1, fresh2],
                [Node(quiet.Name, idleMinutes: 30, lastSeen: Now.AddSeconds(-50)), Node(fresh1.Name, idleMinutes: 30), Node(fresh2.Name, idleMinutes: 30)]),
            Settings());

        Assert.Equal(["worker-default-0000000001"], plan.Drains.Select(d => d.Name));
    }

    [Fact]
    public void AWorkerThatIgnoresTheRequestToLeave_IsStopped()
    {
        var worker = Listed("worker-default-0000000001");
        var node = Node(worker.Name, drain: Now.AddMinutes(-7), exitSignalled: Now.AddMinutes(-6));

        var plan = FleetPlanner.Plan(Snapshot([worker], [node]), Settings());

        Assert.Contains("still polling", Assert.Single(plan.Stops).Reason, StringComparison.Ordinal);
        Assert.Single(plan.Launches); // the minimum does not count it
    }

    [Fact]
    public void AWorkerAnOperatorRestarted_IsLeaving_AndDoesNotCountTowardTheMinimum()
    {
        var worker = Listed("worker-default-0000000001");
        var node = Node(worker.Name, busy: 1, restartRequested: Now.AddMinutes(-1));

        var plan = FleetPlanner.Plan(Snapshot([worker], [node]), Settings());

        Assert.Equal(FleetWorkerPhase.Draining, Assert.Single(plan.Workers).Phase);
        Assert.Single(plan.Launches);
        Assert.Empty(plan.Drains);
    }

    [Fact]
    public void AWorkerThePlatformSaysEnded_ButThatStillPolls_IsDrained_AndNotCounted()
    {
        var worker = Listed("worker-default-0000000001", state: LaunchedWorkerState.Ended) with { EndedSeenUtc = Now.AddSeconds(-30) };

        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name)]), Settings());

        Assert.Contains("still polls", Assert.Single(plan.Drains).Reason, StringComparison.Ordinal);
        Assert.Single(plan.Launches);
    }

    [Fact]
    public void AWorkerThatJustEnded_IsNotDrained_ForTheLastPollItMadeBeforeItEnded()
    {
        // Still inside the online minute, but its last poll came before the platform reported it ended.
        var worker = Listed("worker-default-0000000001", state: LaunchedWorkerState.Ended) with { EndedSeenUtc = Now };

        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name, lastSeen: Now.AddSeconds(-10))]), Settings());

        Assert.Empty(plan.Drains);
        Assert.Equal(FleetWorkerPhase.Ended, Assert.Single(plan.Workers).Phase);
        Assert.Single(plan.Launches);
    }

    [Fact]
    public void AnEndedWorkerThatKeepsPollingAfterItsDrain_IsStoppedAgain()
    {
        var worker = Listed("worker-default-0000000001", state: LaunchedWorkerState.Ended) with { EndedSeenUtc = Now.AddMinutes(-10) };

        var plan = FleetPlanner.Plan(Snapshot([worker], [Node(worker.Name, drain: Now.AddMinutes(-6))]), Settings());

        Assert.Empty(plan.Drains);
        Assert.Contains("after it was asked to leave", Assert.Single(plan.Stops).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ADrainingWorker_FinishingItsRuns_IsLeftToLeave()
    {
        var worker = Listed("worker-default-0000000001");
        var node = Node(worker.Name, busy: 2, drain: Now.AddHours(-1));

        var plan = FleetPlanner.Plan(Snapshot([worker], [node]), Settings());

        Assert.Empty(plan.Stops);
        Assert.Equal(FleetWorkerPhase.Draining, Assert.Single(plan.Workers).Phase);
    }

    // ------------------------------------------------------------------------------------------ stale pictures ----

    [Fact]
    public void WithAStaleList_NothingIsStopped_AndOnlyTheMinimumIsRestored()
    {
        var stuck = new FleetWorker("worker-default-0000000001", "", Now.AddMinutes(-30), LaunchedWorkerState.Starting, true, false);
        var plan = FleetPlanner.Plan(Snapshot([stuck], eligible: Pools(("", 50)), listCurrent: false, takeover: Now.AddHours(-1)), Settings());

        Assert.Empty(plan.Stops);
        Assert.Single(plan.Launches); // the stuck worker is lost, so the minimum asks for one; demand asks for none
        Assert.Contains("at least 1", plan.Launches[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeftoverWorker_IsReplacedFirst_ThenDrained_AndOnlyWhenTheListIsCurrent()
    {
        var leftover = new FleetWorker("worker-default-0000000009", "", null, LaunchedWorkerState.Running, Listed: false, Pending: false);
        var nodes = new[] { Node(leftover.Name) };

        // Alone, it keeps taking work while its replacement starts.
        var alone = FleetPlanner.Plan(Snapshot([leftover], nodes), Settings());
        Assert.Equal(FleetWorkerPhase.Leftover, Assert.Single(alone.Workers).Phase);
        Assert.Empty(alone.Drains);
        Assert.Single(alone.Launches);

        // Once the replacement polls, it drains.
        var fresh = Listed("worker-default-0000000001", ageMinutes: 1);
        var replaced = FleetPlanner.Plan(Snapshot([leftover, fresh], [.. nodes, Node(fresh.Name)]), Settings());
        Assert.Contains("does not have it", Assert.Single(replaced.Drains).Reason, StringComparison.Ordinal);
        Assert.Empty(replaced.Launches);

        // With a stale list it may simply not be listed yet, so it is not drained for being unlisted. (With
        // ReplaceWorkersOnStart it is still replaced as a worker this control plane did not start, which is just as safe.)
        var stale = FleetPlanner.Plan(
            Snapshot([leftover, fresh], [.. nodes, Node(fresh.Name)], listCurrent: false), Settings(replaceOnStart: false));
        Assert.Empty(stale.Drains);
        Assert.Equal(FleetWorkerPhase.Ready, stale.Workers.Single(w => w.Name == leftover.Name).Phase);
    }

    [Fact]
    public void AWorkerThisControlPlaneStarted_IsTrustedBeforeItIsListed()
    {
        var pending = new FleetWorker("worker-default-0000000001", "", Now.AddSeconds(-20), LaunchedWorkerState.Starting, Listed: false, Pending: true, LaunchedHere: true);

        var plan = FleetPlanner.Plan(Snapshot([pending], [Node(pending.Name)]), Settings());

        Assert.Empty(plan.Drains);
        Assert.Equal(FleetWorkerPhase.Ready, Assert.Single(plan.Workers).Phase);
    }

    [Fact]
    public void AWorkerOfAPoolNoLongerManaged_IsDrained()
    {
        var orphan = new FleetWorker("worker-gone-0000000001", null, Now.AddMinutes(-30), LaunchedWorkerState.Running, true, false);

        var plan = FleetPlanner.Plan(Snapshot([orphan], [Node(orphan.Name, "gone")]), Settings());

        Assert.Contains("no longer managed", Assert.Single(plan.Drains).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkerNearThePlatformsTimeLimit_DrainsWhateverTheMinimum_SoItIsNeverCutOff()
    {
        // A 24 h job limit and a 12 h lifetime drain at 18 h: the only worker is busy and its replacements never came.
        var old = Listed("worker-default-0000000001", ageMinutes: 19 * 60);
        var plan = FleetPlanner.Plan(
            Snapshot([old], [Node(old.Name, busy: 2, startedMinutesAgo: 19 * 60)], timeLimit: TimeSpan.FromHours(24)),
            Settings());

        var drain = Assert.Single(plan.Drains);
        Assert.Contains("time limit", drain.Reason, StringComparison.Ordinal);
        Assert.True(drain.Warning); // it should have been replaced long before
        Assert.Single(plan.Launches);

        // At 17 h it is only due: it keeps working until a replacement is ready.
        var younger = Listed("worker-default-0000000001", ageMinutes: 17 * 60);
        var due = FleetPlanner.Plan(
            Snapshot([younger], [Node(younger.Name, busy: 2, startedMinutesAgo: 17 * 60)], timeLimit: TimeSpan.FromHours(24)),
            Settings());
        Assert.Empty(due.Drains);
        Assert.Equal(FleetWorkerPhase.Due, Assert.Single(due.Workers).Phase);
    }

    [Theory]
    [InlineData(null, 12, null)]
    [InlineData(24, 12, 18)]
    [InlineData(14, 12, 13)]
    [InlineData(8, 12, 6)]
    public void TheHardDrainAge_SitsBetweenTheLifetimeAndTheLimit(int? limitHours, int lifetimeHours, int? expectedHours)
    {
        var age = FleetPlanner.HardDrainAge(
            limitHours is { } h ? TimeSpan.FromHours(h) : null, TimeSpan.FromHours(lifetimeHours));
        Assert.Equal(expectedHours is { } e ? TimeSpan.FromHours(e) : null, age);
    }

    [Fact]
    public void ARecycleUnderLoad_ReplacesEveryBusyWorker_AndDrainsThemAsReplacementsArrive()
    {
        // Five busy workers of another build, nothing ready: five replacements start.
        var old = Enumerable.Range(1, 5)
            .Select(i => Listed($"worker-default-ffff00000{i}", ageMinutes: 60, launchedHere: false) with { SameBuild = false })
            .ToList();
        var oldNodes = old.Select(w => Node(w.Name, busy: 3)).ToList();
        var first = FleetPlanner.Plan(Snapshot(old, oldNodes), Settings());
        Assert.Equal(5, first.Launches.Count);
        Assert.Empty(first.Drains);

        // Two replacements polled (idle), three still starting: two old workers drain, nothing more starts.
        var fresh = Enumerable.Range(1, 2).Select(i => Listed($"worker-default-00000{i}0000", ageMinutes: 1)).ToList();
        var starting = Enumerable.Range(1, 3)
            .Select(i => Listed($"worker-default-00000{i}1111", ageMinutes: 0, state: LaunchedWorkerState.Starting))
            .ToList();
        var second = FleetPlanner.Plan(
            Snapshot([.. old, .. fresh, .. starting], [.. oldNodes, .. fresh.Select(w => Node(w.Name, startedMinutesAgo: 1))]),
            Settings());
        Assert.Empty(second.Launches);
        Assert.Equal(2, second.Drains.Count);
        Assert.All(second.Drains, d => Assert.Contains("another build", d.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public void AnIdleRecycle_ReplacesOnlyTheMinimum_ThenDrainsEveryOldWorker()
    {
        var old = Enumerable.Range(1, 4)
            .Select(i => Listed($"worker-default-ffff00000{i}", ageMinutes: 60, launchedHere: false) with { SameBuild = false })
            .ToList();
        var oldNodes = old.Select(w => Node(w.Name, idleMinutes: 10)).ToList();

        Assert.Single(FleetPlanner.Plan(Snapshot(old, oldNodes), Settings()).Launches);

        var fresh = Listed("worker-default-0000000001", ageMinutes: 1);
        var plan = FleetPlanner.Plan(Snapshot([.. old, fresh], [.. oldNodes, Node(fresh.Name, startedMinutesAgo: 1)]), Settings());
        Assert.Equal(4, plan.Drains.Count);
        Assert.Empty(plan.Launches);
    }

    [Fact]
    public void AbsurdSlotCounts_NeverOverflowThePlan()
    {
        var a = Listed("worker-default-0000000001");
        var b = Listed("worker-default-0000000002");
        var nodes = new[] { Node(a.Name, slots: int.MaxValue, free: int.MaxValue), Node(b.Name, slots: int.MaxValue, free: int.MaxValue) };

        var plan = FleetPlanner.Plan(Snapshot([a, b], nodes, eligible: Pools(("", int.MaxValue))), Settings(max: 10));

        Assert.True(plan.Launches.Count <= 8);
        Assert.Equal(FleetPlanner.MaxSlotsPerWorker, Assert.Single(plan.Pools).RunSlotsPerWorker);
    }

    [Fact]
    public void ABackedOffPool_StartsNothing()
    {
        var plan = FleetPlanner.Plan(Snapshot(blocked: Pools(("", 0)).ToDictionary(p => p.Key, _ => Now.AddMinutes(1))), Settings());

        Assert.Empty(plan.Launches);
        Assert.Equal(Now.AddMinutes(1), Assert.Single(plan.Pools).BlockedUntilUtc);
    }
}
