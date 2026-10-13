namespace SqlFlow.Dispatch.ManagedWorkers;

/// <summary>What the fleet knows about one managed worker when it plans: its name, the pool its name places it in (null
/// when that pool is not managed any more), when it was started, what the launcher says about it, whether the launcher
/// listed it, whether this control plane started it and is still waiting to see it listed, whether this control plane
/// process started it (<see cref="LaunchedHere"/>), whether a control plane of this same build started it
/// (<see cref="SameBuild"/>, read from its name), whether it has polled since this control plane took over
/// (<see cref="HasPolled"/>), and when this control plane first saw the platform report it ended
/// (<see cref="EndedSeenUtc"/>).</summary>
public sealed record FleetWorker(
    string Name, string? Pool, DateTime? LaunchedUtc, LaunchedWorkerState LauncherState, bool Listed, bool Pending,
    bool LaunchedHere = false, bool SameBuild = true, bool HasPolled = false, DateTime? EndedSeenUtc = null);

/// <summary>Everything one plan is made from. <see cref="TakeoverUtc"/> is when this control plane last became the
/// dispatch owner: the fleet registry starts empty then, so no worker is judged silent or idle on time from before it.
/// <see cref="ResumedUtc"/> is when it last resumed after a pause in its own ticks (the process stood still, or its clock
/// jumped), which restarts the same timers, since nobody could be heard during the pause. <see cref="ListCurrent"/> is
/// false when the launcher could not be asked this tick and <see cref="Workers"/> holds what it said last.
/// <see cref="WorkerTimeLimit"/> is the longest the platform lets a worker run, when it limits it.</summary>
public sealed record FleetSnapshot(
    DateTime NowUtc,
    DateTime TakeoverUtc,
    IReadOnlyDictionary<string, int> GuiMinimum,
    IReadOnlyDictionary<string, int> EligibleRuns,
    IReadOnlyList<NodeView> Nodes,
    IReadOnlyList<FleetWorker> Workers,
    bool ListCurrent,
    IReadOnlyDictionary<string, DateTime> BlockedUntilUtc,
    TimeSpan? WorkerTimeLimit = null,
    DateTime? ResumedUtc = null)
{
    /// <summary>Since when this control plane has watched the fleet without a gap: the later of the takeover and the
    /// last resume. Silence, start and idle times count from here at the earliest.</summary>
    public DateTime WatchedSinceUtc => ResumedUtc is { } resumed && resumed > TakeoverUtc ? resumed : TakeoverUtc;
}

/// <summary>Where a managed worker stands in a plan.</summary>
public enum FleetWorkerPhase
{
    /// <summary>Started; has not polled yet (or not since this control plane took over).</summary>
    Starting,

    /// <summary>Polling and taking work.</summary>
    Ready,

    /// <summary>Polling and taking work, but to be replaced (too old, or started by another build): it keeps taking work
    /// until its replacement is ready.</summary>
    Due,

    /// <summary>Takes no new work; leaves once it holds nothing.</summary>
    Draining,

    /// <summary>Polled before, but not within the last minute: it takes no work, counts toward its pool's minimum only
    /// for <see cref="ManagedFleetSettings.SilentGrace"/> after its last poll, and is stopped if it stays silent until the
    /// start timeout.</summary>
    Silent,

    /// <summary>Never polled in time, went silent for good, or ignored the request to leave: stopped through the launcher.</summary>
    Lost,

    /// <summary>The platform reports it ended.</summary>
    Ended,

    /// <summary>Polls under a managed name, but this control plane's launcher does not have it: replaced like a due
    /// worker, then drained.</summary>
    Leftover,
}

/// <summary>A worker to start for a pool, and why.</summary>
public sealed record PlannedLaunch(string Pool, string Reason);

/// <summary>A worker to drain, and why. <see cref="Warning"/> marks a drain that should not happen in a healthy fleet
/// (a worker reached the platform's time limit before it could be replaced), which is logged as a warning.</summary>
public sealed record PlannedDrain(string Name, string? Pool, string Reason, bool Warning = false);

/// <summary>A worker to stop through the launcher, and why. <see cref="FailedStart"/> marks one this control plane
/// started in this ownership period that never polled, which backs its pool off.</summary>
public sealed record PlannedStop(string Name, string? Pool, string Reason, bool FailedStart);

/// <summary>One managed worker as the plan saw it.</summary>
public sealed record FleetWorkerView(
    string Name, string? Pool, FleetWorkerPhase Phase, DateTime? LaunchedUtc, bool Online, int BusyRuns, DateTime? IdleSinceUtc);

/// <summary>One managed pool as the plan saw it: its minimum (the setting, raised by the fleet controls), its ceiling,
/// the workers it wants that take work, its workers by phase, the runs a worker could take now and the part of them no
/// free slot covers, and until when its starts are held back after failures.</summary>
public sealed record FleetPoolView(
    string Pool, int MinWorkers, int MaxWorkers, int Target, int Starting, int Ready, int Due, int Draining, int Busy,
    int EligibleRuns, int UnservedRuns, int RunSlotsPerWorker, DateTime? BlockedUntilUtc, int Silent = 0);

/// <summary>The actions of one tick and the picture they were made from.</summary>
public sealed record FleetPlan(
    IReadOnlyList<PlannedLaunch> Launches,
    IReadOnlyList<PlannedDrain> Drains,
    IReadOnlyList<PlannedStop> Stops,
    IReadOnlyList<FleetPoolView> Pools,
    IReadOnlyList<FleetWorkerView> Workers);

/// <summary>
/// Turns a snapshot of the fleet into the starts, drains and stops that bring every managed pool to what it needs. Pure:
/// the same snapshot always gives the same plan, so every rule is tested on its own (docs/managed-workers-plan.md).
/// </summary>
/// <remarks>
/// The invariants the rules keep: a managed pool's minimum is at least 1, and nothing is drained or stopped for being
/// surplus, old or from another build while it would leave a pool with fewer Ready workers that polled recently than its
/// minimum; a worker is drained (never stopped) while it can still leave by itself, and a draining worker finishes what
/// it holds; only a worker that never polled, went silent for good, or ignored the request to leave is stopped; with a
/// stale launcher list nothing is stopped and only a pool's minimum is restored. While the control plane hears no node
/// at all (every node it knows went quiet together), the silence is most likely its own or the network's: no worker
/// that has polled is stopped for silence and nothing is started for demand, so the fleet never replaces itself
/// wholesale over a blip; the minimum is still restored once a silent worker's grace has passed. Two rules override the
/// minimum, because the alternative is worse: a worker near the platform's time limit drains before the platform ends it
/// mid-run, and a worker whose pool is no longer managed drains.
/// </remarks>
public static class FleetPlanner
{
    /// <summary>The most run slots one worker is believed to offer, whatever it reports; bounds every sum the plan makes.</summary>
    internal const int MaxSlotsPerWorker = 1024;

    /// <summary>Plans one tick.</summary>
    public static FleetPlan Plan(FleetSnapshot snapshot, ManagedFleetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);

        var nodes = new Dictionary<string, NodeView>(StringComparer.Ordinal);
        foreach (var node in snapshot.Nodes)
        {
            nodes[node.Name] = node;
        }

        var stops = new List<PlannedStop>();
        var drains = new List<PlannedDrain>();
        var views = new List<FleetWorkerView>(snapshot.Workers.Count);
        var phases = new Dictionary<string, FleetWorkerPhase>(StringComparer.Ordinal);
        var hardAge = HardDrainAge(snapshot.WorkerTimeLimit, settings.MaxLifetime);
        var deaf = HearsNoNode(snapshot, settings);

        foreach (var worker in snapshot.Workers)
        {
            nodes.TryGetValue(worker.Name, out var node);
            var phase = Classify(worker, node, snapshot, settings, hardAge, deaf, stops, drains);
            phases[worker.Name] = phase;
            views.Add(new FleetWorkerView(
                worker.Name, worker.Pool, phase, worker.LaunchedUtc ?? node?.StartedUtc, node?.Online ?? false,
                node?.BusyRuns ?? 0, node?.IdleSinceUtc));
        }

        var pools = new List<FleetPoolView>(settings.Pools.Count);
        var floorLaunches = new List<PlannedLaunch>();
        var demandLaunches = new List<PlannedLaunch>();
        foreach (var pool in settings.Pools)
        {
            pools.Add(PlanPool(pool, snapshot, settings, nodes, phases, deaf, floorLaunches, demandLaunches, drains));
        }

        // A pool below its minimum is served before any pool's demand, and no tick starts more than the cap.
        var launches = floorLaunches.Concat(demandLaunches).Take(settings.MaxLaunchesPerTick).ToList();
        return new FleetPlan(launches, drains, stops, pools, views);
    }

    /// <summary>The age at which a worker drains whatever its pool's minimum, so it finishes its runs before the
    /// platform's time limit ends it: halfway between the lifetime and the limit, or three quarters of a limit no longer
    /// than the lifetime. None without a limit.</summary>
    internal static TimeSpan? HardDrainAge(TimeSpan? limit, TimeSpan maxLifetime)
    {
        if (limit is not { } hard || hard <= TimeSpan.Zero)
        {
            return null;
        }

        return hard > maxLifetime ? maxLifetime + ((hard - maxLifetime) / 2) : hard * 3 / 4;
    }

    /// <summary>Whether this control plane hears no node at all: it knows at least one, and none has polled within
    /// <see cref="ManagedFleetSettings.PollFreshness"/> (counted from <see cref="FleetSnapshot.WatchedSinceUtc"/> at the
    /// earliest). Every node going quiet together points at the control plane or the network between them, not at the
    /// workers, and a new worker would not get through either.</summary>
    internal static bool HearsNoNode(FleetSnapshot snapshot, ManagedFleetSettings settings)
    {
        var watched = snapshot.WatchedSinceUtc;
        return snapshot.Nodes.Count > 0
            && snapshot.Nodes.All(n => snapshot.NowUtc - Later(n.LastSeenUtc, watched) > settings.PollFreshness);
    }

    private static FleetWorkerPhase Classify(
        FleetWorker worker, NodeView? node, FleetSnapshot snapshot, ManagedFleetSettings settings, TimeSpan? hardAge,
        bool deaf, List<PlannedStop> stops, List<PlannedDrain> drains)
    {
        var now = snapshot.NowUtc;
        var watched = snapshot.WatchedSinceUtc;
        var canStop = snapshot.ListCurrent && (worker.Listed || worker.Pending);

        if (worker.LauncherState == LaunchedWorkerState.Ended)
        {
            // The platform says it ended. A node still online then is nearly always just its last poll inside the online
            // window; only one that has polled since the platform reported it ended is still running. That one gets no
            // more work, and is stopped again if it keeps polling.
            if (node is { Online: true } && worker.EndedSeenUtc is { } ended && node.LastSeenUtc > ended)
            {
                if (node.DrainRequestedUtc is not { } drained)
                {
                    drains.Add(new PlannedDrain(worker.Name, worker.Pool, "the platform reports it ended, but it still polls"));
                }
                else if (now - drained >= settings.ExitTimeout && canStop)
                {
                    stops.Add(new PlannedStop(
                        worker.Name, worker.Pool,
                        $"the platform reports it ended, but it still polls {Minutes(now - drained)} after it was asked to leave",
                        FailedStart: false));
                }
            }

            return FleetWorkerPhase.Ended;
        }

        if (node is null)
        {
            var since = Later(worker.LaunchedUtc ?? watched, watched);
            if (now - since < settings.StartTimeout)
            {
                // Polled earlier in this ownership period but gone from the registry: it is silent, not starting.
                return worker.HasPolled ? FleetWorkerPhase.Silent : FleetWorkerPhase.Starting;
            }

            if (worker.HasPolled)
            {
                // A name that has polled since the takeover is never stopped as one that never polled, and is not stopped
                // for silence while the control plane hears nobody.
                if (canStop && !deaf)
                {
                    stops.Add(new PlannedStop(worker.Name, worker.Pool, $"it has been silent for {Minutes(now - since)}", FailedStart: false));
                }

                return deaf ? FleetWorkerPhase.Silent : FleetWorkerPhase.Lost;
            }

            if (canStop)
            {
                // Only a worker this process started in this ownership period was watched for its whole start, so only
                // its silence is a failed start; one started before a takeover may have polled the owner in between.
                var failedStart = worker.LaunchedHere && worker.LaunchedUtc is { } launched && launched >= snapshot.TakeoverUtc;
                stops.Add(new PlannedStop(
                    worker.Name, worker.Pool,
                    worker.LaunchedUtc is { } at && at >= snapshot.TakeoverUtc
                        ? $"it has not polled in the {Minutes(settings.StartTimeout)} it was given to start"
                        : $"it has not polled in the {Minutes(settings.StartTimeout)} since this control plane took over",
                    failedStart));
            }

            return FleetWorkerPhase.Lost;
        }

        var restarting = node.RestartRequestedUtc is { } restart && restart > node.StartedUtc;
        if (node.DrainRequestedUtc is not null || restarting)
        {
            // Leaving (drained, or restarted by an operator): it takes no new work and does not count toward the minimum.
            if (node.Online && node.ExitSignalledUtc is { } told && now - told >= settings.ExitTimeout)
            {
                if (canStop)
                {
                    stops.Add(new PlannedStop(
                        worker.Name, worker.Pool, $"it was told to leave {Minutes(now - told)} ago and is still polling", FailedStart: false));
                }

                return FleetWorkerPhase.Lost;
            }

            if (!node.Online && !deaf && now - Later(node.LastSeenUtc, watched) >= settings.StartTimeout)
            {
                if (canStop)
                {
                    stops.Add(new PlannedStop(
                        worker.Name, worker.Pool, $"it went silent while leaving {Minutes(now - node.LastSeenUtc)} ago", FailedStart: false));
                }

                return FleetWorkerPhase.Lost;
            }

            return FleetWorkerPhase.Draining;
        }

        if (!node.Online)
        {
            // Polled before but silent now: a network blip passes, a dead worker does not. It takes no work; it counts
            // toward the minimum for a short grace (PlanPool), then a replacement starts; at the timeout it is stopped,
            // unless the control plane hears nobody at all.
            if (deaf || now - Later(node.LastSeenUtc, watched) < settings.StartTimeout)
            {
                return FleetWorkerPhase.Silent;
            }

            if (canStop)
            {
                stops.Add(new PlannedStop(
                    worker.Name, worker.Pool, $"it has been silent for {Minutes(now - node.LastSeenUtc)}", FailedStart: false));
            }

            return FleetWorkerPhase.Lost;
        }

        if (worker.Pool is null)
        {
            drains.Add(new PlannedDrain(worker.Name, null, "its pool is no longer managed"));
            return FleetWorkerPhase.Draining;
        }

        var age = now - (worker.LaunchedUtc ?? node.StartedUtc);
        if (hardAge is { } hard && age >= hard)
        {
            // Its replacement should have taken over long ago (at MaxLifetime): reaching this means starts are failing or
            // the pool was too busy to let it go, so it is drained as a warning.
            drains.Add(new PlannedDrain(
                worker.Name, worker.Pool,
                $"it is {Hours(age)} old and must finish its runs before the platform's time limit ends it; it was not replaced in time",
                Warning: true));
            return FleetWorkerPhase.Draining;
        }

        if (snapshot.ListCurrent && !worker.Listed && !worker.Pending)
        {
            // Not this launcher's (one an earlier control plane started that the platform no longer lists, or one started
            // by hand under a managed name): it keeps taking work until the pool has its replacement, then drains.
            return FleetWorkerPhase.Leftover;
        }

        if (age >= settings.MaxLifetime || (settings.ReplaceWorkersOnStart && !worker.SameBuild))
        {
            return FleetWorkerPhase.Due;
        }

        return FleetWorkerPhase.Ready;
    }

    private static FleetPoolView PlanPool(
        ManagedPoolSettings pool, FleetSnapshot snapshot, ManagedFleetSettings settings, Dictionary<string, NodeView> nodes,
        Dictionary<string, FleetWorkerPhase> phases, bool deaf, List<PlannedLaunch> floorLaunches,
        List<PlannedLaunch> demandLaunches, List<PlannedDrain> drains)
    {
        var now = snapshot.NowUtc;
        var key = pool.Pool;
        var mine = snapshot.Workers.Where(w => string.Equals(w.Pool, key, StringComparison.Ordinal)).ToList();
        List<FleetWorker> Of(params FleetWorkerPhase[] wanted) => mine.Where(w => wanted.Contains(phases[w.Name])).ToList();
        var ready = Of(FleetWorkerPhase.Ready);
        var due = Of(FleetWorkerPhase.Due, FleetWorkerPhase.Leftover);
        var starting = Of(FleetWorkerPhase.Starting).Count;
        var draining = Of(FleetWorkerPhase.Draining).Count;
        var silentWorkers = Of(FleetWorkerPhase.Silent);
        var silent = silentWorkers.Count;

        // A worker that went quiet only moments ago is still treated as present: it counts toward the minimum, and the free
        // slots it last reported toward the capacity for queued runs, though nothing is handed to it. So a blip that
        // silences a pool for less than the grace starts nothing; past it, the worker is replaced.
        var inGrace = silentWorkers
            .Select(w => nodes.TryGetValue(w.Name, out var node) ? node : null)
            .OfType<NodeView>()
            .Where(n => now - Later(n.LastSeenUtc, snapshot.WatchedSinceUtc) < settings.SilentGrace)
            .ToList();
        var silentInGrace = inGrace.Count;

        var guiMinimum = snapshot.GuiMinimum.GetValueOrDefault(key);
        var minimum = Math.Min(pool.MaxWorkers, Math.Max(pool.MinWorkers, guiMinimum));

        // What one worker of the pool executes at once: what its own workers report, else any node serving the pool,
        // else the runtime default. Every count a node reports is bounded, so no sum here can overflow.
        var serving = snapshot.Nodes
            .Where(n => n.Online && n.DrainRequestedUtc is null && !(n.RestartRequestedUtc > n.StartedUtc) && Serves(n, key))
            .ToList();
        var ownSlots = ready.Concat(due).Select(w => Slots(nodes[w.Name])).DefaultIfEmpty(0).Max();
        var slots = ownSlots > 0 ? ownSlots : serving.Select(Slots).Where(s => s > 0).DefaultIfEmpty(settings.DefaultRunSlots).Max();
        slots = Math.Clamp(slots, 1, MaxSlotsPerWorker);

        // Capacity already there for the queued runs: free slots on every node that takes this pool's work now, and the
        // slots of the workers still starting. Only what that does not cover asks for a new worker, so a run an idle
        // worker is about to take never starts one.
        var eligible = Math.Max(0, snapshot.EligibleRuns.GetValueOrDefault(key));
        var capacity = serving.Sum(n => (long)FreeSlots(n)) + ((long)starting * slots) + inGrace.Sum(n => (long)FreeSlots(n));
        var unserved = (int)Math.Min(int.MaxValue, Math.Max(0L, eligible - capacity));

        // Workers that take work and are staying (and the silent ones still in their grace), and replacements for the busy
        // workers being replaced that no idle Ready or starting worker can stand in for, so a recycle never leaves the load
        // to the minimum; then the target.
        var taking = ready.Count + starting;
        var present = taking + silentInGrace;
        var dueBusy = due.Count(w => IsBusy(nodes[w.Name]));
        var idleReady = ready.Count(w => !IsBusy(nodes[w.Name]));
        var replacements = Math.Max(0, dueBusy - idleReady - starting);
        var forDemand = (long)present + Ceiling(unserved, slots) + replacements;
        var target = (int)Math.Max(minimum, Math.Min(pool.MaxWorkers, forDemand));
        var blockedUntil = snapshot.BlockedUntilUtc.TryGetValue(key, out var until) && until > now ? until : (DateTime?)null;

        var toFloor = Math.Max(0, minimum - present);
        var toDemand = Math.Max(0, target - present - toFloor);
        if (!snapshot.ListCurrent || deaf)
        {
            // What the platform runs is not known this tick, or the control plane hears no worker at all: restore the
            // minimum only, never start for demand on a picture that may be wrong.
            toDemand = 0;
        }

        if (blockedUntil is null)
        {
            for (var i = 0; i < toFloor; i++)
            {
                floorLaunches.Add(new PlannedLaunch(key, $"the pool keeps at least {minimum} worker(s) and has {present}"));
            }

            for (var i = 0; i < toDemand; i++)
            {
                demandLaunches.Add(new PlannedLaunch(
                    key, i < replacements ? "a busy worker is being replaced" : $"{unserved} queued run(s) have no free slot"));
            }
        }

        // Only Ready workers that polled within the freshness window count as present: a worker that has gone quiet but
        // is still inside the online window is never what a pool is left with.
        var fresh = ready.Count(w => now - nodes[w.Name].LastSeenUtc <= settings.PollFreshness);

        // Replacing: due and leftover workers drain only while the pool has its minimum of fresh Ready workers, and only
        // as many as the workers that are staying can carry: the load (busy workers and the queued runs) is never left to
        // fewer workers than it needs. Idle ones first, then the oldest.
        if (due.Count > 0 && fresh >= minimum)
        {
            var busyTakers = ready.Concat(due).Count(w => IsBusy(nodes[w.Name]));
            var wanted = Math.Max(minimum, (int)Math.Min(pool.MaxWorkers, (long)busyTakers + Ceiling(eligible, slots)));
            var allowed = Math.Clamp(ready.Count + due.Count - wanted, 0, due.Count);
            foreach (var worker in due
                .OrderBy(w => IsBusy(nodes[w.Name]))
                .ThenBy(w => w.LaunchedUtc ?? nodes[w.Name].StartedUtc)
                .ThenBy(w => w.Name, StringComparer.Ordinal)
                .Take(allowed))
            {
                var started = worker.LaunchedUtc ?? nodes[worker.Name].StartedUtc;
                var reason = phases[worker.Name] == FleetWorkerPhase.Leftover
                    ? "replaced: it polls under a managed name, but this control plane's launcher does not have it"
                    : now - started >= settings.MaxLifetime
                        ? $"replaced: it is older than {Hours(settings.MaxLifetime)}"
                        : "replaced: it was started by another build of the control plane";
                drains.Add(new PlannedDrain(worker.Name, key, reason));
            }
        }

        // Shrinking: only with nothing eligible queued for the pool, only workers idle long enough (counted from the
        // takeover or a resume at the earliest), the quiet ones and then the oldest first, and never below the minimum of
        // fresh Ready workers.
        if (eligible == 0 && fresh > minimum)
        {
            var idle = ready
                .Select(w => (Worker: w, Node: nodes[w.Name]))
                .Where(x => !IsBusy(x.Node) && x.Node.IdleSinceUtc is { } since
                    && now - Later(since, snapshot.WatchedSinceUtc) >= settings.Idle)
                .OrderBy(x => now - x.Node.LastSeenUtc <= settings.PollFreshness)
                .ThenBy(x => x.Worker.LaunchedUtc ?? x.Node.StartedUtc)
                .ThenBy(x => x.Worker.Name, StringComparer.Ordinal)
                .Take(fresh - minimum);
            foreach (var (worker, _) in idle)
            {
                drains.Add(new PlannedDrain(worker.Name, key, $"idle for {Minutes(settings.Idle)} or more with nothing queued"));
            }
        }

        var busy = ready.Concat(due).Count(w => nodes[w.Name].BusyRuns > 0);
        return new FleetPoolView(
            key, minimum, pool.MaxWorkers, target, starting, ready.Count, due.Count, draining, busy, eligible, unserved, slots,
            blockedUntil, silent);
    }

    /// <summary>Whether a node holds or was just handed work.</summary>
    private static bool IsBusy(NodeView node) => node.BusyRuns > 0 || node.BusyTasks > 0 || node.IdleSinceUtc is null;

    private static int Slots(NodeView node) => Math.Clamp(node.RunSlots, 0, MaxSlotsPerWorker);

    private static int FreeSlots(NodeView node) => Math.Clamp(node.FreeRunSlots, 0, Slots(node));

    /// <summary>Every node takes untargeted runs (the default pool); a named pool's only its members.</summary>
    private static bool Serves(NodeView node, string poolKey)
        => poolKey.Length == 0 || node.Pools.Contains(poolKey, StringComparer.Ordinal);

    private static long Ceiling(long value, int divisor) => (Math.Max(0, value) + divisor - 1) / divisor;

    private static DateTime Later(DateTime a, DateTime b) => a > b ? a : b;

    private static string Minutes(TimeSpan span)
        => span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min" : $"{Math.Max(0, (int)span.TotalSeconds)} s";

    private static string Hours(TimeSpan span) => $"{(int)span.TotalHours} h";
}
