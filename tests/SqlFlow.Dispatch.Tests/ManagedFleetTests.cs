using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.ManagedWorkers;
using SqlFlow.Dispatch.Protocol;
using Xunit;

namespace SqlFlow.Dispatch.Tests;

/// <summary>
/// The managed fleet end to end, on a manual clock: a real dispatcher over the fake journal, a fake launcher, and
/// simulated workers that start, poll, execute what they are handed, and exit when told. Each test runs the loop a
/// platform would see and checks what the fleet started, drained and stopped, and that no run was ever cut off.
/// </summary>
public sealed class ManagedFleetTests
{
    private static readonly DateTime T0 = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>The options the control plane's API writes JSON with.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task TheStatus_NamesEachWorkersPhase_AsTheApiWritesIt()
    {
        // The Nodes page reads each worker's phase as its name ("Ready"); a number broke the whole page.
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromMinutes(1));

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(sim.Fleet.Status(), Web));
        var worker = Assert.Single(json.RootElement.GetProperty("workers").EnumerateArray());
        Assert.Equal(System.Text.Json.JsonValueKind.String, worker.GetProperty("phase").ValueKind);
        Assert.Equal("Ready", worker.GetProperty("phase").GetString());
    }

    [Fact]
    public void EveryPhase_IsWrittenAndReadByName()
    {
        foreach (var phase in Enum.GetValues<FleetWorkerPhase>())
        {
            var view = new FleetWorkerView("worker-default-0123456789", null, phase, T0, true, 0, null);
            var text = System.Text.Json.JsonSerializer.Serialize(view, Web);

            Assert.Contains($"\"phase\":\"{phase}\"", text, StringComparison.Ordinal);
            Assert.Equal(view, System.Text.Json.JsonSerializer.Deserialize<FleetWorkerView>(text, Web));
        }
    }

    [Fact]
    public async Task AFleetAtZero_StartsItsMinimum_AndHoldsIt()
    {
        await using var sim = await Sim.StartAsync();

        await sim.TickAsync();
        Assert.Single(sim.Launcher.Workers);

        await sim.RunAsync(TimeSpan.FromMinutes(30));

        var worker = Assert.Single(sim.Launcher.Workers.Values);
        Assert.Equal(LaunchedWorkerState.Running, worker.State);
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).Ready);
        Assert.Empty(sim.Launcher.Stops);
    }

    [Fact]
    public async Task ABurst_ScalesUp_EveryRunCompletes_ThenIdleSurplusLeaves_DownToTheMinimum()
    {
        await using var sim = await Sim.StartAsync(max: 10);
        await sim.RunAsync(TimeSpan.FromSeconds(30)); // the minimum worker is up

        sim.Enqueue(24, runFor: TimeSpan.FromMinutes(2));
        await sim.RunAsync(TimeSpan.FromSeconds(30));
        Assert.True(sim.Launcher.Workers.Count >= 6, $"expected at least 6 workers for 24 runs of 4 slots, saw {sim.Launcher.Workers.Count}");
        Assert.True(sim.Launcher.Workers.Count <= 10);

        await sim.RunAsync(TimeSpan.FromMinutes(4));
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));

        await sim.RunAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, sim.Launcher.Workers.Values.Count(w => w.State != LaunchedWorkerState.Ended));
        Assert.Equal(0, sim.SeveredRuns);
        Assert.Empty(sim.Launcher.Stops);
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Drain);
    }

    [Fact]
    public async Task ABusySurplusWorker_IsNotDrained_UntilItsRunIsDone()
    {
        await using var sim = await Sim.StartAsync(max: 4);
        await sim.RunAsync(TimeSpan.FromSeconds(30));

        sim.Enqueue(8, runFor: TimeSpan.FromSeconds(30));
        sim.Enqueue(1, runFor: TimeSpan.FromMinutes(20));
        await sim.RunAsync(TimeSpan.FromMinutes(12));

        Assert.Equal(1, sim.Ledger.Runs.Values.Count(r => r.Status == "running"));
        var holder = sim.Launcher.Workers.Values.Single(w => w.Held.Count > 0);
        Assert.Equal(LaunchedWorkerState.Running, holder.State);

        await sim.RunAsync(TimeSpan.FromMinutes(20));
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));
        Assert.Equal(0, sim.SeveredRuns);
        Assert.Equal(1, sim.Launcher.Workers.Values.Count(w => w.State != LaunchedWorkerState.Ended));
    }

    [Fact]
    public async Task ACrashedWorker_IsReplaced_AndItsRunRequeued()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromMinutes(11)); // up long enough that its end is not a failed start
        sim.Enqueue(1, runFor: TimeSpan.FromMinutes(30));
        await sim.RunAsync(TimeSpan.FromSeconds(10));

        var victim = sim.Launcher.Workers.Values.Single(w => w.State == LaunchedWorkerState.Running);
        sim.Crash(victim.Name);
        await sim.RunAsync(TimeSpan.FromSeconds(10));

        var replacement = Assert.Single(sim.Launcher.Workers.Values, w => w.State != LaunchedWorkerState.Ended);
        Assert.NotEqual(victim.Name, replacement.Name);
        Assert.Equal(0, Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures);

        // Its node was still inside the online minute when the platform reported it ended, but it never polled again:
        // that is no worker that "still polls", so it is not drained for it.
        Assert.DoesNotContain(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Drain && a.Worker == victim.Name);

        await sim.RunAsync(TimeSpan.FromMinutes(40));
        Assert.Equal("succeeded", Assert.Single(sim.Ledger.Runs.Values).Status);
        Assert.Equal(2, Assert.Single(sim.Ledger.Runs.Values).Attempt); // the lapsed lease requeued it once
    }

    [Fact]
    public async Task FailedStarts_BackThePoolOff_AndAWorkerThatKeepsPollingClearsIt()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.FailLaunches = 3;

        await sim.RunAsync(TimeSpan.FromSeconds(9));
        Assert.Equal(1, sim.Launcher.LaunchAttempts);
        var status = Assert.Single(sim.Fleet.Status().Pools);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Contains("refused", status.LastError, StringComparison.Ordinal);

        await sim.RunAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(4, sim.Launcher.LaunchAttempts); // 10 s, 20 s, 40 s apart, then it starts
        await sim.RunAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(3, Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures); // polling, but not long enough yet

        await sim.RunAsync(TimeSpan.FromMinutes(6));
        status = Assert.Single(sim.Fleet.Status().Pools);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.Null(status.LastError);
        Assert.Equal(1, status.Ready);
        Assert.Equal(3, sim.Fleet.Status().Actions.Count(a => a.Kind == ManagedWorkerActions.StartFailed));
    }

    [Fact]
    public async Task AWorkerThatDiesBeforeAnyListSeesItRunning_StillBacksThePoolOff()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.EndAtOnce = true; // every job the platform starts has already failed by the next list

        await sim.RunAsync(TimeSpan.FromSeconds(30));

        // Without the back-off this would be one start per tick (ten in 30 s); with it, 10 s then 20 s apart.
        Assert.InRange(sim.Launcher.LaunchAttempts, 2, 3);
        var status = Assert.Single(sim.Fleet.Status().Pools);
        Assert.True(status.ConsecutiveFailures >= 2);
        Assert.Contains("before its first poll", status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkerThatNeverPolls_IsStopped_AndReplaced()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.NextNeverPolls = true;

        await sim.RunAsync(TimeSpan.FromMinutes(9));
        Assert.Single(sim.Launcher.Workers);
        Assert.Empty(sim.Launcher.Stops);

        await sim.RunAsync(TimeSpan.FromMinutes(2));
        Assert.Single(sim.Launcher.Stops);
        await sim.RunAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(1, sim.Launcher.Workers.Values.Count(w => w.State == LaunchedWorkerState.Running));
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Stop && a.Message.Contains("has not polled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATakeover_StopsNothing_AndStartsNothingBesideTheWorkersAlreadyThere()
    {
        await using var sim = await Sim.StartAsync(min: 2);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(2, sim.Launcher.Workers.Count);

        // Ownership is lost and regained: the registry starts empty, and the workers have not polled the new owner yet.
        sim.Dispatcher.Deactivate();
        sim.Clock.Advance(TimeSpan.FromSeconds(5));
        await sim.Dispatcher.ActivateAsync("owner-2", CancellationToken.None);
        await sim.TickAsync();

        Assert.Equal(2, sim.Launcher.Workers.Count);
        Assert.Empty(sim.Launcher.Stops);
        Assert.Equal(2, Assert.Single(sim.Fleet.Status().Pools).Starting);

        await sim.RunAsync(TimeSpan.FromMinutes(15));
        Assert.Equal(2, sim.Launcher.Workers.Count);
        Assert.Empty(sim.Launcher.Stops);
    }

    [Fact]
    public async Task ARedeploy_ReplacesEveryWorker_WithoutCuttingARunOff()
    {
        await using var sim = await Sim.StartAsync(min: 2);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        sim.Enqueue(2, runFor: TimeSpan.FromMinutes(5));
        await sim.RunAsync(TimeSpan.FromSeconds(10));
        var before = sim.Launcher.Workers.Keys.ToHashSet();

        // A new control plane process of another build starts (a redeploy): every worker of the old build is replaced.
        await sim.RestartControlPlaneAsync(newBuild: true);
        await sim.RunAsync(TimeSpan.FromMinutes(10));

        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal(1, r.Attempt));
        Assert.Equal(0, sim.SeveredRuns);
        var running = sim.Launcher.Workers.Values.Where(w => w.State != LaunchedWorkerState.Ended).Select(w => w.Name).ToList();
        Assert.Equal(2, running.Count);
        Assert.DoesNotContain(running, before.Contains);
        Assert.Empty(sim.Launcher.Stops);
    }

    [Fact]
    public async Task AFailoverToTheSameBuild_KeepsEveryWorker()
    {
        await using var sim = await Sim.StartAsync(min: 2);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        sim.Enqueue(4, runFor: TimeSpan.FromMinutes(5));
        await sim.RunAsync(TimeSpan.FromSeconds(10));
        var before = sim.Launcher.Workers.Keys.ToHashSet();

        await sim.RestartControlPlaneAsync(newBuild: false);
        await sim.RunAsync(TimeSpan.FromMinutes(10));

        Assert.Equal(before, sim.Launcher.Workers.Keys.ToHashSet()); // nothing started
        Assert.All(sim.Launcher.Workers.Values, w => Assert.NotEqual(LaunchedWorkerState.Ended, w.State));
        Assert.DoesNotContain(sim.Fleet.Status().Actions, a => a.Kind is ManagedWorkerActions.Drain or ManagedWorkerActions.Stop);
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));
    }

    [Fact]
    public async Task OwnershipLostInTheMiddleOfATick_StopsAndStartsNothing()
    {
        await using var sim = await Sim.StartAsync(min: 2);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        sim.Enqueue(2, runFor: TimeSpan.FromMinutes(5));
        await sim.RunAsync(TimeSpan.FromSeconds(10));
        var launched = sim.Launcher.LaunchAttempts;

        // Long after the takeover, the floors read during a tick takes so long that dispatch ownership is lost (and
        // regained) meanwhile: the registry the tick would read is empty, and every worker would look never-polled.
        sim.Clock.Advance(TimeSpan.FromMinutes(20));
        sim.OnFloorsRead = async () =>
        {
            sim.Dispatcher.Deactivate();
            await sim.Dispatcher.ActivateAsync("owner-again", CancellationToken.None);
        };
        await sim.TickAsync();
        sim.OnFloorsRead = null;

        Assert.Empty(sim.Launcher.Stops);
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);
        Assert.All(sim.Launcher.Workers.Values, w => Assert.Equal(LaunchedWorkerState.Running, w.State));
    }

    [Fact]
    public async Task AStartThePlatformNeverMade_StopsCountingAsStarting_AfterThreeListsAndAMinute_AndCountsOnce()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.NextMayHaveStartedButDidNot = true; // the start times out and nothing was created

        // The status is the picture a tick planned from, so the start shows from the tick after it.
        await sim.RunAsync(TimeSpan.FromSeconds(6));
        Assert.Empty(sim.Launcher.Workers);
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).Starting); // counted as starting for now

        // Many lists have missed it by now, but a list may lag behind the platform: it is still given the minute.
        await sim.RunAsync(TimeSpan.FromSeconds(45));
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).Starting);
        Assert.DoesNotContain(sim.Fleet.Status().Actions, a => a.Message.Contains("never listed", StringComparison.Ordinal));

        await sim.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.StartFailed && a.Message.Contains("never listed", StringComparison.Ordinal));
        Assert.Single(sim.Launcher.Workers); // the minimum was restored well before the start timeout
        Assert.Equal(LaunchedWorkerState.Running, sim.Launcher.Workers.Values.Single().State);

        // The timed-out start and its never being listed are one failed worker, not two.
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures);
    }

    [Fact]
    public async Task AStartThatMayHaveBeenMade_IsGivenUp_AfterTheStartTimeout_EvenWhileTheLauncherCannotList()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.FailList = true;
        sim.Launcher.NextMayHaveStartedButDidNot = true;

        await sim.RunAsync(TimeSpan.FromMinutes(9));
        Assert.Equal(1, sim.Launcher.LaunchAttempts); // counted as starting: the minimum waits for it
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).Starting);

        await sim.RunAsync(TimeSpan.FromMinutes(2));
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.StartFailed && a.Message.Contains("could not be asked", StringComparison.Ordinal));
        Assert.Equal(2, sim.Launcher.LaunchAttempts); // given up, so the minimum starts another
        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures);

        // When the launcher answers again, nothing is stopped for the start that was never made.
        sim.Launcher.FailList = false;
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Empty(sim.Launcher.Stops);
        Assert.Equal(1, sim.Launcher.Workers.Values.Count(w => w.State == LaunchedWorkerState.Running));
    }

    [Fact]
    public async Task AWorkerThatNeverPolls_AndIsStopped_IsOneFailedStart_NotTwo()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.NextNeverPolls = true;

        // Stopped for never polling (a failed start), then listed as ended before its first poll: one failure.
        await sim.RunAsync(TimeSpan.FromMinutes(11));
        Assert.Single(sim.Launcher.Stops);
        await sim.RunAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures);
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Ended && a.Worker == sim.Launcher.Stops[0]);
    }

    [Fact]
    public async Task ASlowCrashLoop_StillBacksThePoolOff()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.CrashAfterPolls = 60; // each worker runs about three minutes, then dies

        await sim.RunAsync(TimeSpan.FromMinutes(30));

        var status = Assert.Single(sim.Fleet.Status().Pools);
        Assert.True(status.ConsecutiveFailures >= 5, $"expected the back-off to keep climbing, saw {status.ConsecutiveFailures}");
        Assert.Contains("after its first poll, unasked", status.LastError, StringComparison.Ordinal);
        Assert.True(sim.Launcher.LaunchAttempts <= 8, $"expected the waits to grow, saw {sim.Launcher.LaunchAttempts} starts");
    }

    [Fact]
    public async Task AnOwnershipFlap_DoesNotForgiveStartsThatKeepFailing()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromMinutes(11)); // one long-lived, healthy worker

        sim.Launcher.FailLaunches = 1000;
        sim.GuiMinimum[string.Empty] = 2;
        await sim.RunAsync(TimeSpan.FromMinutes(2));
        var failures = Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures;
        Assert.True(failures >= 3);

        sim.Dispatcher.Deactivate();
        sim.Clock.Advance(TimeSpan.FromSeconds(5));
        await sim.Dispatcher.ActivateAsync("owner-2", CancellationToken.None);
        await sim.RunAsync(TimeSpan.FromMinutes(12));

        // The healthy worker polls the new owner for far longer than the stability window, but it started before the
        // failures, so it proves nothing about the starts that keep failing.
        Assert.True(Assert.Single(sim.Fleet.Status().Pools).ConsecutiveFailures > failures);
    }

    [Fact]
    public async Task AFleetWideBlip_StartsNothing_AndEveryRunStillCompletes()
    {
        await using var sim = await Sim.StartAsync(min: 2, max: 10);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        var launched = sim.Launcher.LaunchAttempts;

        // Every worker goes quiet at once (a restarting proxy, say) while runs arrive that they have room for.
        foreach (var worker in sim.Launcher.Workers.Values)
        {
            worker.Paused = true;
        }

        sim.Enqueue(8, runFor: TimeSpan.FromSeconds(30));
        await sim.RunAsync(TimeSpan.FromSeconds(110));
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);
        Assert.Empty(sim.Launcher.Stops);

        foreach (var worker in sim.Launcher.Workers.Values)
        {
            worker.Paused = false;
        }

        await sim.RunAsync(TimeSpan.FromMinutes(3));
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));
    }

    [Fact]
    public async Task AFleetWideSilencePastTheGrace_StartsOnlyTheMinimum_AndStopsNothing()
    {
        await using var sim = await Sim.StartAsync(min: 2, max: 10);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        var launched = sim.Launcher.LaunchAttempts;

        foreach (var worker in sim.Launcher.Workers.Values)
        {
            worker.Paused = true;
        }

        // Once no worker has polled for longer than a long poll, the control plane hears nobody; runs arrive.
        await sim.RunAsync(TimeSpan.FromSeconds(51));
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);
        sim.Enqueue(40, runFor: TimeSpan.FromSeconds(30));

        // Past the grace (two minutes after the last poll) the minimum is restored, but the forty queued runs start
        // nothing while nobody is heard.
        await sim.RunAsync(TimeSpan.FromSeconds(72));
        Assert.Equal(launched + 2, sim.Launcher.LaunchAttempts);
        Assert.Empty(sim.Launcher.Stops);
    }

    [Fact]
    public async Task APauseOfTheControlPlane_StopsNothing_AndStartsNothing()
    {
        await using var sim = await Sim.StartAsync(min: 2);
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        var launched = sim.Launcher.LaunchAttempts;

        // The process stands still for twelve minutes; the first tick after it runs before any worker polls again.
        sim.Clock.Advance(TimeSpan.FromMinutes(12));
        await sim.TickAsync();
        Assert.Empty(sim.Launcher.Stops);
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);

        await sim.RunAsync(TimeSpan.FromMinutes(15));
        Assert.Empty(sim.Launcher.Stops);
        Assert.Equal(launched, sim.Launcher.LaunchAttempts);
    }

    [Fact]
    public async Task AWorkerThePlatformSaysEnded_ButThatKeepsPolling_IsDrained_ThenStoppedAgain()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromSeconds(30));
        var zombie = sim.Launcher.Workers.Values.Single();

        // The platform reports it ended (a job stuck stopping, its process still alive), and it keeps polling.
        zombie.State = LaunchedWorkerState.Ended;
        zombie.PollsWhenEnded = true;
        await sim.RunAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Drain && a.Worker == zombie.Name && a.Message.Contains("still polls", StringComparison.Ordinal));

        await sim.RunAsync(TimeSpan.FromMinutes(6));
        Assert.Contains(zombie.Name, sim.Launcher.Stops);
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.Stop && a.Message.Contains("asked to leave", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartsComeBeforeStops_AndStopsHaveABudget()
    {
        await using var sim = await Sim.StartAsync(min: 3);
        sim.Launcher.AllNeverPoll = true;
        await sim.RunAsync(TimeSpan.FromMinutes(9));
        sim.Launcher.AllNeverPoll = false;
        sim.Launcher.StopTakes = TimeSpan.FromSeconds(40); // a slow platform: two stops spend the tick's budget

        // The tick that gives up on all three starts their replacements first, then stops two; the third waits a tick.
        await sim.RunAsync(TimeSpan.FromSeconds(63));
        Assert.Equal(2, sim.Launcher.Stops.Count);
        var firstStop = sim.Launcher.Calls.FindIndex(c => c.StartsWith("stop", StringComparison.Ordinal));
        Assert.Equal(6, sim.Launcher.Calls.Take(firstStop).Count(c => c.StartsWith("start", StringComparison.Ordinal)));

        await sim.RunAsync(TimeSpan.FromSeconds(6));
        Assert.Equal(3, sim.Launcher.Stops.Distinct().Count());
    }

    [Fact]
    public async Task ANewWorkerName_NeverRepeatsOneInUse()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromSeconds(30));
        var taken = sim.Launcher.Workers.Keys.Single();
        var names = new Queue<string>([taken, taken, "worker-default-0000aaaaaa"]);
        sim.Fleet.NameFactory = (_, _, _) => names.Count > 0 ? names.Dequeue() : taken;

        sim.GuiMinimum[string.Empty] = 2;
        await sim.RunAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(new HashSet<string>(["worker-default-0000aaaaaa", taken]), sim.Launcher.Workers.Keys.ToHashSet());
        Assert.Equal(2, sim.Launcher.LaunchAttempts);

        // With every name taken, no start is attempted under a name in use.
        sim.GuiMinimum[string.Empty] = 3;
        await sim.RunAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, sim.Launcher.LaunchAttempts);
        Assert.Contains(sim.Fleet.Status().Actions, a => a.Kind == ManagedWorkerActions.StartFailed && a.Message.Contains("no unused worker name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AWorkerThatCrashesSoonAfterItsFirstPoll_BacksThePoolOff()
    {
        await using var sim = await Sim.StartAsync();
        sim.Launcher.CrashAfterPolls = 3; // each worker polls a few times, then dies

        await sim.RunAsync(TimeSpan.FromMinutes(2));

        // Without the back-off it would be replaced after every crash; with it, the waits double.
        Assert.InRange(sim.Launcher.LaunchAttempts, 2, 5);
        var status = Assert.Single(sim.Fleet.Status().Pools);
        Assert.True(status.ConsecutiveFailures >= 2);
        Assert.Contains("after its first poll, unasked", status.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWorkerIsDrainedBeforeItIsStopped()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromSeconds(30));
        var worker = sim.Launcher.Workers.Values.Single();

        // It goes silent while the platform still runs it, long enough to be stopped.
        worker.Paused = true;
        await sim.RunAsync(TimeSpan.FromMinutes(11));

        Assert.Contains(worker.Name, sim.Launcher.Stops);
        Assert.True(sim.Dispatcher.Nodes.IsDraining(worker.Name)); // had it answered again, it would have been handed nothing
    }

    [Fact]
    public async Task ALauncherThatCannotList_StopsNothing_AndRecovers()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromSeconds(30));

        sim.Launcher.FailList = true;
        await sim.RunAsync(TimeSpan.FromMinutes(20));
        var status = sim.Fleet.Status();
        Assert.False(status.ListCurrent);
        Assert.Contains("cannot be reached", status.ListError, StringComparison.Ordinal);
        Assert.Single(sim.Launcher.Workers);
        Assert.Empty(sim.Launcher.Stops);

        sim.Launcher.FailList = false;
        await sim.RunAsync(TimeSpan.FromSeconds(10));
        Assert.True(sim.Fleet.Status().ListCurrent);
        Assert.Null(sim.Fleet.Status().ListError);
    }

    [Fact]
    public async Task APassiveReplica_NeverCallsTheLauncher()
    {
        await using var sim = await Sim.StartAsync();
        sim.Dispatcher.Deactivate();

        await sim.RunAsync(TimeSpan.FromMinutes(5));

        Assert.Equal(0, sim.Launcher.ListCalls);
        Assert.Empty(sim.Launcher.Workers);
        Assert.False(sim.Fleet.Status().Active);
    }

    [Fact]
    public async Task TheGuiFloor_StartsMore_AndLoweringItLetsThemLeave()
    {
        await using var sim = await Sim.StartAsync();
        await sim.RunAsync(TimeSpan.FromSeconds(30));

        sim.GuiMinimum[string.Empty] = 3;
        await sim.RunAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(3, sim.Launcher.Workers.Values.Count(w => w.State == LaunchedWorkerState.Running));

        sim.GuiMinimum[string.Empty] = 0;
        await sim.RunAsync(TimeSpan.FromMinutes(7));
        Assert.Equal(1, sim.Launcher.Workers.Values.Count(w => w.State != LaunchedWorkerState.Ended));
    }

    [Fact]
    public async Task ANamedPool_IsStartedWithItsPool_AndServesItsRuns()
    {
        await using var sim = await Sim.StartAsync(named: [new ManagedPoolSettings("heavy", 1, 3)]);
        await sim.RunAsync(TimeSpan.FromSeconds(30));

        var heavy = Assert.Single(sim.Launcher.Workers.Values, w => w.Pool == "heavy");
        Assert.StartsWith("worker-heavy-", heavy.Name, StringComparison.Ordinal);

        sim.Enqueue(2, runFor: TimeSpan.FromSeconds(30), pool: "heavy");
        await sim.RunAsync(TimeSpan.FromMinutes(2));
        Assert.All(sim.Ledger.Runs.Values, r => Assert.Equal("succeeded", r.Status));
        Assert.All(sim.Ledger.Runs.Values, r => Assert.StartsWith("worker-heavy-", r.Node, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheActionHistory_IsBounded_NewestFirst()
    {
        await using var sim = await Sim.StartAsync(recentActions: 3);
        sim.Launcher.FailLaunches = 10;

        await sim.RunAsync(TimeSpan.FromMinutes(20));

        var actions = sim.Fleet.Status().Actions;
        Assert.Equal(3, actions.Count);
        Assert.True(actions[0].TimestampUtc >= actions[^1].TimestampUtc);
    }

    // --------------------------------------------------------------------------------------------- harness -----

    /// <summary>A fake platform: it records what it is asked, starts workers after a short delay, and can be told to
    /// refuse starts, to start a worker that never polls, or to stop answering its list.</summary>
    private sealed class FakeLauncher(ManualClock clock) : IWorkerLauncher
    {
        public Dictionary<string, SimWorker> Workers { get; } = new(StringComparer.Ordinal);

        public List<string> Stops { get; } = [];

        public int FailLaunches { get; set; }

        public bool NextNeverPolls { get; set; }

        public bool FailList { get; set; }

        /// <summary>Every job started from now on has failed before the next list.</summary>
        public bool EndAtOnce { get; set; }

        /// <summary>The next start times out after the request was sent, and the platform created nothing.</summary>
        public bool NextMayHaveStartedButDidNot { get; set; }

        /// <summary>Every job started from now on dies after this many polls (0: never).</summary>
        public int CrashAfterPolls { get; set; }

        /// <summary>Every job started while set never polls.</summary>
        public bool AllNeverPoll { get; set; }

        /// <summary>How long each stop takes on the platform: the clock moves on by this much during the call.</summary>
        public TimeSpan StopTakes { get; set; }

        /// <summary>Every start and stop, in order: "start name" or "stop name".</summary>
        public List<string> Calls { get; } = [];

        public int LaunchAttempts { get; private set; }

        public int ListCalls { get; private set; }

        public string Name => "fake";

        public Task<IReadOnlyList<LaunchedWorker>> ListAsync(CancellationToken ct)
        {
            ListCalls++;
            if (FailList)
            {
                throw new WorkerLauncherException("the fake platform cannot be reached");
            }

            return Task.FromResult<IReadOnlyList<LaunchedWorker>>(
                [.. Workers.Values.Select(w => new LaunchedWorker(w.Name, w.State, w.LaunchedUtc, w.Detail))]);
        }

        public Task LaunchAsync(WorkerLaunch launch, CancellationToken ct)
        {
            LaunchAttempts++;
            if (FailLaunches > 0)
            {
                FailLaunches--;
                throw new WorkerLauncherException("the fake platform refused the job");
            }

            if (NextMayHaveStartedButDidNot)
            {
                NextMayHaveStartedButDidNot = false;
                throw new WorkerLauncherException("the fake platform did not answer in time", mayHaveStarted: true);
            }

            Calls.Add($"start {launch.Name}");
            var worker = new SimWorker(launch.Name, launch.Pool, clock.GetUtcNow().UtcDateTime)
            {
                NeverPolls = NextNeverPolls || AllNeverPoll,
                CrashAfterPolls = CrashAfterPolls,
            };
            if (EndAtOnce)
            {
                worker.State = LaunchedWorkerState.Ended;
                worker.Detail = "Failed: the container exited with code 1";
            }

            Workers[launch.Name] = worker;
            NextNeverPolls = false;
            return Task.CompletedTask;
        }

        public Task StopAsync(string workerName, CancellationToken ct)
        {
            Calls.Add($"stop {workerName}");
            clock.Advance(StopTakes);
            Stops.Add(workerName);
            if (Workers.TryGetValue(workerName, out var worker))
            {
                worker.State = LaunchedWorkerState.Ended;
                worker.Detail = "stopped";
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>One simulated worker: it starts a few seconds after launch, polls with its free slots, holds each run it
    /// is handed for the run's duration, and on the exit flag finishes what it holds and exits.</summary>
    private sealed class SimWorker(string name, string pool, DateTime launchedUtc)
    {
        public string Name { get; } = name;

        public string Pool { get; } = pool;

        public DateTime LaunchedUtc { get; } = launchedUtc;

        public LaunchedWorkerState State { get; set; } = LaunchedWorkerState.Starting;

        public string? Detail { get; set; }

        public bool NeverPolls { get; init; }

        /// <summary>Dies after this many polls (0: never).</summary>
        public int CrashAfterPolls { get; init; }

        public int Polls { get; set; }

        /// <summary>Stops polling while set, though the platform still runs it.</summary>
        public bool Paused { get; set; }

        /// <summary>Keeps polling after the platform reports it ended, ignoring every request to leave.</summary>
        public bool PollsWhenEnded { get; set; }

        public bool Exiting { get; set; }

        public DateTime StartedUtc { get; set; }

        public Dictionary<Guid, (int Attempt, DateTime FinishUtc)> Held { get; } = [];
    }

    private sealed class Sim : IAsyncDisposable
    {
        private static readonly TimeSpan Step = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(6);
        private readonly Dictionary<Guid, TimeSpan> _durations = [];
        private ManagedFleetSettings _settings = null!;

        private Sim(FakeLedger ledger, ManualClock clock, Dispatcher dispatcher)
        {
            Ledger = ledger;
            Clock = clock;
            Dispatcher = dispatcher;
            Launcher = new FakeLauncher(clock);
        }

        public FakeLedger Ledger { get; }

        public ManualClock Clock { get; }

        public Dispatcher Dispatcher { get; }

        public FakeLauncher Launcher { get; }

        public ManagedFleet Fleet { get; private set; } = null!;

        public Dictionary<string, int> GuiMinimum { get; } = new(StringComparer.Ordinal);

        /// <summary>Runs while the fleet reads the pools' floors, the slow catalog read of a tick.</summary>
        public Func<Task>? OnFloorsRead { get; set; }

        /// <summary>Runs a worker's lease lapsed under: a run cut off while a worker still held it.</summary>
        public int SeveredRuns { get; private set; }

        public static async Task<Sim> StartAsync(
            int min = 1, int max = 10, IReadOnlyList<ManagedPoolSettings>? named = null, int recentActions = 200)
        {
            var ledger = new FakeLedger();
            var clock = ManualClock.At();
            var dispatcher = await Make.ActiveDispatcherAsync(ledger, clock);
            var sim = new Sim(ledger, clock, dispatcher);
            sim._settings = new ManagedFleetSettings
            {
                Pools = [new ManagedPoolSettings(string.Empty, min, max), .. named ?? []],
                RecentActions = recentActions,
            };
            sim._settings.Validate("Test:ManagedWorkers");
            sim.Fleet = sim.NewFleet();
            return sim;
        }

        private ManagedFleet NewFleet()
            => new(
                Dispatcher, Launcher, _settings,
                async _ =>
                {
                    if (OnFloorsRead is { } hook)
                    {
                        await hook();
                    }

                    return new Dictionary<string, int>(GuiMinimum, StringComparer.Ordinal);
                },
                Clock, NullLogger<ManagedFleet>.Instance);

        /// <summary>A new control plane process (a new fleet, and a new dispatch ownership period), of another build when
        /// <paramref name="newBuild"/> (a redeploy) or of the same one (a failover).</summary>
        public async Task RestartControlPlaneAsync(bool newBuild)
        {
            Fleet.Dispose();
            Dispatcher.Deactivate();
            Clock.Advance(TimeSpan.FromSeconds(5));
            await Dispatcher.ActivateAsync("owner-after-restart", CancellationToken.None);
            if (newBuild)
            {
                _settings = new ManagedFleetSettings
                {
                    Pools = _settings.Pools,
                    RecentActions = _settings.RecentActions,
                    BuildTag = _settings.BuildTag == "1111" ? "2222" : "1111",
                };
            }

            Fleet = NewFleet();
        }

        public void Enqueue(int count, TimeSpan runFor, string? pool = null)
        {
            var runs = new List<DispatchRun>();
            for (var i = 0; i < count; i++)
            {
                var run = Make.Run(Clock.GetUtcNow().UtcDateTime.AddMilliseconds(i), pool: pool);
                Ledger.AddQueued(run);
                _durations[run.RunId] = runFor;
                runs.Add(run);
            }

            Dispatcher.NotifyRunsEnqueued(runs);
        }

        public void Crash(string name)
        {
            var worker = Launcher.Workers[name];
            worker.State = LaunchedWorkerState.Ended;
            worker.Detail = "crashed";
            worker.Held.Clear();
        }

        public Task TickAsync() => Fleet.TickAsync(CancellationToken.None);

        /// <summary>Runs the loop for <paramref name="span"/>: each step the clock moves, the leases are housekept,
        /// every worker polls, and the fleet ticks.</summary>
        public async Task RunAsync(TimeSpan span)
        {
            var end = Clock.GetUtcNow() + span;
            while (Clock.GetUtcNow() < end)
            {
                Clock.Advance(Step);
                await Dispatcher.TickAsync(CancellationToken.None);
                foreach (var worker in Launcher.Workers.Values.ToList())
                {
                    await StepWorkerAsync(worker);
                }

                await Fleet.TickAsync(CancellationToken.None);
            }
        }

        private async Task StepWorkerAsync(SimWorker worker)
        {
            var now = Clock.GetUtcNow().UtcDateTime;
            if (worker.State == LaunchedWorkerState.Ended && !worker.PollsWhenEnded)
            {
                return;
            }

            if (worker.State == LaunchedWorkerState.Starting)
            {
                if (now - worker.LaunchedUtc < StartDelay)
                {
                    return;
                }

                worker.State = LaunchedWorkerState.Running;
                worker.StartedUtc = now;
            }

            if (worker.NeverPolls || worker.Paused)
            {
                return;
            }

            if (worker.CrashAfterPolls > 0 && worker.Polls >= worker.CrashAfterPolls)
            {
                worker.State = LaunchedWorkerState.Ended;
                worker.Detail = "Failed: the container exited with code 137";
                return;
            }

            worker.Polls++;

            foreach (var (runId, (attempt, finish)) in worker.Held.ToList())
            {
                if (finish <= now)
                {
                    var status = await Dispatcher.RecordRunOutcomeAsync(
                        runId, new RunOutcomeRequest(worker.Name, attempt, RunOutcomeKind.Completed, null, Make.Artifact), CancellationToken.None);
                    if (status == RunOutcomeStatus.StaleClaim)
                    {
                        SeveredRuns++;
                    }

                    worker.Held.Remove(runId);
                }
            }

            if (worker.Exiting && worker.Held.Count == 0)
            {
                worker.State = LaunchedWorkerState.Ended;
                worker.Detail = "exited";
                return;
            }

            var free = worker.Exiting ? 0 : 4 - worker.Held.Count;
            var answer = await Dispatcher.PollAsync(
                Make.Poll(
                    worker.Name, freeRuns: free, pools: worker.Pool.Length == 0 ? [] : [worker.Pool],
                    holding: [.. worker.Held.Select(h => new HeldRun(h.Key, h.Value.Attempt))], startedUtc: worker.StartedUtc),
                CancellationToken.None);

            foreach (var revoked in answer.RevokedRuns)
            {
                worker.Held.Remove(revoked);
                SeveredRuns++;
            }

            foreach (var run in answer.Runs)
            {
                worker.Held[run.RunId] = (run.Attempt, now + _durations[run.RunId]);
            }

            if (answer.RestartRequested && !worker.PollsWhenEnded)
            {
                worker.Exiting = true;
                if (worker.Held.Count == 0)
                {
                    worker.State = LaunchedWorkerState.Ended;
                    worker.Detail = "exited";
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            Fleet.Dispose();
            Dispatcher.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
