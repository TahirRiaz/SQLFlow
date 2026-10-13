using Microsoft.Extensions.Logging;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Dispatch.ManagedWorkers;

/// <summary>One thing the fleet did, for the Nodes page and the log: when, what (<see cref="ManagedWorkerActions"/>),
/// for which pool (null for a worker whose pool is not managed any more) and worker, and why.</summary>
public sealed record ManagedWorkerAction(DateTime TimestampUtc, string Kind, string? Pool, string? Worker, string Message);

/// <summary>The kinds of <see cref="ManagedWorkerAction"/>.</summary>
public static class ManagedWorkerActions
{
    public const string Start = "start";
    public const string StartFailed = "start-failed";
    public const string Drain = "drain";
    public const string Stop = "stop";
    public const string StopFailed = "stop-failed";
    public const string Ended = "ended";
}

/// <summary>One managed pool as the last tick left it: the plan's view of it, how many starts in a row have failed, and
/// the last failure.</summary>
public sealed record ManagedPoolStatus(
    string Pool, int MinWorkers, int MaxWorkers, int Target, int Starting, int Ready, int Due, int Draining, int Busy,
    int EligibleRuns, int UnservedRuns, int RunSlotsPerWorker, DateTime? BlockedUntilUtc, int ConsecutiveFailures,
    string? LastError, int Silent = 0);

/// <summary>The managed fleet as this control plane sees it: whether it is the replica that ticks (the dispatch owner),
/// the launcher, when it last ticked and last heard from the launcher (and why not, when the last call failed), every
/// managed pool and worker, and the recent actions, newest first.</summary>
public sealed record ManagedFleetStatus(
    bool Active, string Launcher, DateTime? TakeoverUtc, DateTime? LastTickUtc, DateTime? LastListUtc, bool ListCurrent,
    string? ListError, IReadOnlyList<ManagedPoolStatus> Pools, IReadOnlyList<FleetWorkerView> Workers,
    IReadOnlyList<ManagedWorkerAction> Actions);

/// <summary>
/// The managed fleet: on every tick, while this control plane owns dispatch, it asks the launcher which workers exist,
/// reads the dispatcher's queue and fleet registry and the pools' floors, plans with <see cref="FleetPlanner"/>, and
/// carries the plan out: it drains workers through the dispatcher (they leave once they hold nothing), stops the ones
/// that cannot leave by themselves (draining them first), and starts new ones. Nothing it decides depends on memory a
/// restart loses: the launcher's list and the workers' polls rebuild the picture after a takeover.
/// </summary>
/// <remarks>
/// <para>Ownership: the dispatcher's ownership epoch is read before the registry is read and checked again after, and
/// before every action; when it changed (this replica lost dispatch, or lost and regained it), the tick stops where it
/// is, so nothing is ever drained, stopped or started on a registry that was cleared under it.</para>
/// <para>Failures: a launcher that cannot be asked leaves the last list in place, and the tick stops nothing and only
/// restores minimums. A failed start, a start the platform never lists, a worker that never polls, one that ends before
/// its first poll, or one that ends unasked within <see cref="ManagedFleetSettings.StableAfter"/> of it backs its pool
/// off (doubling from <see cref="ManagedFleetSettings.MinBackoff"/> to <see cref="ManagedFleetSettings.MaxBackoff"/>)
/// without ever giving up; one worker counts as one failure at most, and only a worker started after the pool's last
/// failure that keeps polling that long forgives it.</para>
/// <para>Time: every launcher call and the read of the pools' floors runs under
/// <see cref="ManagedFleetSettings.LauncherCallTimeout"/>; starts come before stops, each with a budget of that long
/// (what is left waits for the next tick), and every action is stamped with the clock when it is taken. A gap between
/// two ticks longer than a tick and a poll's freshness (the process stood still, or its clock jumped) restarts every
/// silence, start and idle timer, since nobody could be heard meanwhile. Errors are redacted before they are kept or
/// logged.</para>
/// </remarks>
public sealed partial class ManagedFleet : IDisposable
{
    /// <summary>How many current lists in a row must miss a start before it is given up as never made.</summary>
    private const int MaxListMisses = 3;

    /// <summary>How many fresh names a start tries before it gives up on finding one no worker has.</summary>
    private const int NameAttempts = 8;

    /// <summary>How long a start must have had to appear in the platform's list before it is given up as never made, so
    /// a list that lags behind the platform's own create call never costs a real start.</summary>
    private static readonly TimeSpan NeverListedAfter = TimeSpan.FromMinutes(1);

    private readonly Dispatcher _dispatcher;
    private readonly IWorkerLauncher _launcher;
    private readonly ManagedFleetSettings _settings;
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> _guiMinimum;
    private readonly TimeProvider _clock;
    private readonly ILogger<ManagedFleet> _logger;
    private readonly Dictionary<string, string> _poolByPart;
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly Lock _statusGate = new();
    private readonly LinkedList<ManagedWorkerAction> _actions = new();

    // What this control plane started and has not yet seen listed. Kept across a takeover, since a worker this process
    // started is still its own after it regains ownership.
    private readonly Dictionary<string, PendingLaunch> _pending = new(StringComparer.Ordinal);

    // Every worker this control plane process started, by name, with when it started it by this process's own clock.
    // Pruned with the other per-name bookkeeping once nothing reports the name.
    private readonly Dictionary<string, DateTime> _launchedHere = new(StringComparer.Ordinal);

    // Per-pool start health: failures in a row, until when starts wait, the last failure and when it happened.
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _blockedUntil = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastError = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastFailureUtc = new(StringComparer.Ordinal);

    // Per-name bookkeeping for the life of this process, pruned once nothing reports the name: when each worker first
    // polled this process, which workers have been up long enough to forgive their pool, which already counted as a
    // failed start (one worker is one failure at most), when the platform was first seen reporting each one ended, which
    // ended workers were accounted for, which names were seen running, which were asked to leave (drained, stopped,
    // restarted by an operator), and when each stop was last asked.
    private readonly Dictionary<string, DateTime> _firstPolledUtc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stable = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedStarts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _endedSeenUtc = new(StringComparer.Ordinal);
    private readonly HashSet<string> _accountedEnded = new(StringComparer.Ordinal);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly HashSet<string> _askedToLeave = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _stoppedAt = new(StringComparer.Ordinal);

    // Per ownership period, reset at a takeover: the workers that have polled this owner. One that has not is starting,
    // never silent, since the registry started empty.
    private readonly HashSet<string> _polledSinceTakeover = new(StringComparer.Ordinal);

    // Every name a worker has or may have (listed, pending, polling, started here), so a new name never repeats one.
    private readonly HashSet<string> _namesInUse = new(StringComparer.Ordinal);

    private DateTime? _takeoverUtc;
    private DateTime? _resumedUtc;
    private DateTime? _lastTickEndUtc;
    private IReadOnlyList<LaunchedWorker> _lastList = [];
    private bool _listCurrent;
    private DateTime? _lastListUtc;
    private string? _listError;
    private IReadOnlyDictionary<string, int> _lastGuiMinimum = new Dictionary<string, int>(StringComparer.Ordinal);
    private ManagedFleetStatus _status;

    /// <summary>Creates the fleet. <paramref name="guiMinimum"/> answers each pool's floor as the fleet controls set it
    /// (the always-on floor and an open manual override, already combined). One fleet lives as long as its control plane
    /// process: what it started is what this process started.</summary>
    public ManagedFleet(
        Dispatcher dispatcher, IWorkerLauncher launcher, ManagedFleetSettings settings,
        Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> guiMinimum, TimeProvider clock,
        ILogger<ManagedFleet> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(guiMinimum);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _launcher = launcher;
        _settings = settings;
        _guiMinimum = guiMinimum;
        _clock = clock;
        _logger = logger;
        _poolByPart = settings.Pools.ToDictionary(p => WorkerNames.PoolPart(p.Pool), p => p.Pool, StringComparer.Ordinal);
        _status = new ManagedFleetStatus(false, launcher.Name, null, null, null, false, null, [], [], []);
    }

    /// <summary>The launcher the fleet starts workers with.</summary>
    public string LauncherName => _launcher.Name;

    /// <summary>Makes a new worker's name from the prefix, the pool and the build tag. Replaced only by tests that need
    /// two names to collide.</summary>
    internal Func<string, string, string, string> NameFactory { get; set; } = WorkerNames.New;

    /// <summary>The settings the fleet runs with.</summary>
    public ManagedFleetSettings Settings => _settings;

    /// <summary>The fleet as the last tick left it.</summary>
    public ManagedFleetStatus Status()
    {
        lock (_statusGate)
        {
            return _status;
        }
    }

    /// <summary>One tick. Does nothing on a replica that does not own dispatch. Never throws for a launcher or catalog
    /// failure (those are recorded and retried on the next tick); only <paramref name="ct"/> ends it early.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!await _tickGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // the previous tick is still running; the next one picks up from where it ends
        }

        try
        {
            await TickCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task TickCoreAsync(CancellationToken ct)
    {
        if (!_dispatcher.IsActive)
        {
            BecomePassive();
            _lastTickEndUtc = null; // a takeover starts its own timers; the passive time is no pause
            return;
        }

        // A gap between ticks far longer than the cadence means this process stood still (a pause, a starved thread pool)
        // or its clock jumped: nobody could be heard meanwhile, so every timer restarts from here, as at a takeover.
        var started = _clock.GetUtcNow().UtcDateTime;
        if (_lastTickEndUtc is { } lastEnd && started - lastEnd > _settings.Tick + _settings.PollFreshness)
        {
            _resumedUtc = started;
            LogResumed((int)(started - lastEnd).TotalSeconds);
        }

        try
        {
            await TickWhileOwnerAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _lastTickEndUtc = _clock.GetUtcNow().UtcDateTime;
        }
    }

    private async Task TickWhileOwnerAsync(CancellationToken ct)
    {
        // The slow calls first: neither depends on ownership, and the picture is taken only after them.
        await RefreshListAsync(ct).ConfigureAwait(false);
        await RefreshGuiMinimumAsync(ct).ConfigureAwait(false);

        // Ownership, then the registry, then ownership again: a registry read across a change of ownership is never used.
        var epoch = _dispatcher.OwnershipEpoch;
        if (_dispatcher.ActivatedUtc is not { } activated)
        {
            BecomePassive();
            return;
        }

        if (_takeoverUtc != activated)
        {
            // A new ownership period: the registry starts empty, so which workers have polled this owner is learned again.
            // Everything else this process knows about a worker stays true.
            _takeoverUtc = activated;
            _polledSinceTakeover.Clear();
            LogTakeover(activated, _launcher.Name, _settings.Pools.Count);
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        var nodes = _dispatcher.Nodes.Snapshot(now);
        var eligible = _settings.Pools.ToDictionary(
            p => p.Pool, p => _dispatcher.Queue.CountEligibleQueuedRuns(p.Pool), StringComparer.Ordinal);
        if (!Owns(epoch))
        {
            LogOwnershipChanged();
            return;
        }

        var workers = BuildWorkers(nodes, now);
        var snapshot = new FleetSnapshot(
            now, activated, _lastGuiMinimum, eligible, nodes, workers, _listCurrent,
            new Dictionary<string, DateTime>(_blockedUntil, StringComparer.Ordinal), _launcher.WorkerTimeLimit, _resumedUtc);
        var plan = FleetPlanner.Plan(snapshot, _settings);

        if (await ActAsync(plan, epoch, ct).ConfigureAwait(false))
        {
            Publish(_clock.GetUtcNow().UtcDateTime, active: true, plan);
        }
    }

    /// <summary>Carries a plan out, checking before every action that this replica still owns dispatch under the epoch the
    /// plan was made in. Drains first (they only mark the registry), then starts (so a pool below its minimum never waits
    /// behind stops; floor starts lead), then stops. Starts and stops each get a budget of
    /// <see cref="ManagedFleetSettings.LauncherCallTimeout"/>, and what one does not reach waits for the next tick. False
    /// when it stopped because ownership changed.</summary>
    private async Task<bool> ActAsync(FleetPlan plan, long epoch, CancellationToken ct)
    {
        foreach (var drain in plan.Drains)
        {
            if (!Owns(epoch))
            {
                LogOwnershipChanged();
                return false;
            }

            if (_dispatcher.DrainNode(drain.Name))
            {
                _askedToLeave.Add(drain.Name);
                Record(ManagedWorkerActions.Drain, drain.Pool, drain.Name, drain.Reason, drain.Warning);
            }
        }

        var deadline = _clock.GetUtcNow() + _settings.LauncherCallTimeout;
        var refused = new HashSet<string>(StringComparer.Ordinal);
        var deferred = 0;
        foreach (var launch in plan.Launches)
        {
            if (!Owns(epoch))
            {
                LogOwnershipChanged();
                return false;
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                deferred++;
                continue;
            }

            if (!refused.Contains(launch.Pool) && !await LaunchAsync(launch, ct).ConfigureAwait(false))
            {
                // One failure holds the pool back for the rest of this tick; the back-off decides when it tries again.
                refused.Add(launch.Pool);
            }
        }

        if (deferred > 0)
        {
            LogDeferred(deferred, "start(s)", (int)_settings.LauncherCallTimeout.TotalSeconds);
        }

        deadline = _clock.GetUtcNow() + _settings.LauncherCallTimeout;
        deferred = 0;
        foreach (var stop in plan.Stops)
        {
            if (!Owns(epoch))
            {
                LogOwnershipChanged();
                return false;
            }

            if (_clock.GetUtcNow() >= deadline)
            {
                deferred++;
                continue;
            }

            await StopAsync(stop, ct).ConfigureAwait(false);
        }

        if (deferred > 0)
        {
            LogDeferred(deferred, "stop(s)", (int)_settings.LauncherCallTimeout.TotalSeconds);
        }

        return true;
    }

    private bool Owns(long epoch) => _dispatcher.IsActive && _dispatcher.OwnershipEpoch == epoch;

    private void BecomePassive()
    {
        if (_takeoverUtc is not null)
        {
            LogPassive();
            _takeoverUtc = null;
            _resumedUtc = null;
        }

        Publish(_clock.GetUtcNow().UtcDateTime, active: false, plan: null);
    }

    private async Task RefreshListAsync(CancellationToken ct)
    {
        try
        {
            using var call = CallBudget(ct);
            _lastList = await _launcher.ListAsync(call.Token).ConfigureAwait(false);
            if (!_listCurrent && _listError is not null)
            {
                LogListRecovered(_launcher.Name);
            }

            _listCurrent = true;
            _listError = null;
            _lastListUtc = _clock.GetUtcNow().UtcDateTime;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException
                ? $"the launcher did not answer within {(int)_settings.LauncherCallTimeout.TotalSeconds} s"
                : SecretHygiene.RedactedMessage(ex);
            if (_listError != error)
            {
                LogListFailed(_launcher.Name, error);
            }

            _listCurrent = false;
            _listError = error;
        }
    }

    private async Task RefreshGuiMinimumAsync(CancellationToken ct)
    {
        try
        {
            using var call = CallBudget(ct);
            _lastGuiMinimum = await _guiMinimum(call.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The floors set in the GUI only ever raise a minimum; the configured minimum still holds without them.
            LogGuiMinimumFailed(ex is OperationCanceledException
                ? $"the read did not finish within {(int)_settings.LauncherCallTimeout.TotalSeconds} s"
                : SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>The managed workers: everything the launcher listed under a managed name, what this control plane
    /// started and has not seen listed yet, and any node polling under a managed name that neither knows. Also does the
    /// bookkeeping the picture feeds: who polled, which starts were never made, which workers ended and how.</summary>
    private List<FleetWorker> BuildWorkers(IReadOnlyList<NodeView> nodes, DateTime now)
    {
        var listedByName = new Dictionary<string, LaunchedWorker>(StringComparer.Ordinal);
        foreach (var listed in _lastList)
        {
            listedByName[listed.Name] = listed;
        }

        // Who has polled comes first, so a worker that polled and then ended between two ticks is never mistaken for one
        // that failed to start. A worker this process started after its pool's last failure, polling for long enough,
        // forgives the pool: it shows that starts work again.
        foreach (var node in nodes)
        {
            if (WorkerNames.PoolPartOf(node.Name, _settings.NamePrefix) is not { } part)
            {
                continue;
            }

            _polledSinceTakeover.Add(node.Name);
            if (!_firstPolledUtc.TryGetValue(node.Name, out var first))
            {
                first = now;
                _firstPolledUtc[node.Name] = first;
            }

            if (node.RestartRequestedUtc is { } restart && restart > node.StartedUtc)
            {
                _askedToLeave.Add(node.Name); // an operator restart: its end is asked for, not a failure
            }

            if (node.Online && _launchedHere.TryGetValue(node.Name, out var launchedAt) && now - first >= _settings.StableAfter
                && _stable.Add(node.Name) && PoolOf(part) is { } pool && _failures.ContainsKey(pool)
                && launchedAt >= _lastFailureUtc.GetValueOrDefault(pool))
            {
                _failures.Remove(pool);
                _blockedUntil.Remove(pool);
                _lastError.Remove(pool);
                LogPoolRecovered(PoolLabel(pool));
            }
        }

        ForgetStartsNeverMade(listedByName, nodes, now);

        var workers = new Dictionary<string, FleetWorker>(StringComparer.Ordinal);
        foreach (var listed in listedByName.Values)
        {
            if (WorkerNames.PoolPartOf(listed.Name, _settings.NamePrefix) is not { } part)
            {
                continue;
            }

            var pool = PoolOf(part);
            var pending = _pending.TryGetValue(listed.Name, out var mine);
            if (_listCurrent && pending)
            {
                _pending.Remove(listed.Name); // listed now: the list is its record from here on
            }

            // This process's own record of when it started the worker wins over the platform's clock.
            var here = _launchedHere.TryGetValue(listed.Name, out var launchedHere);
            var launched = here ? launchedHere : listed.LaunchedUtc ?? (pending ? mine!.LaunchedUtc : null);
            AccountFor(listed, pool, here ? launchedHere : null, now);
            workers[listed.Name] = Worker(listed.Name, pool, launched, listed.State, listed: true, pending: false, here);
        }

        foreach (var (name, launch) in _pending.ToList())
        {
            if (!workers.ContainsKey(name))
            {
                workers[name] = Worker(name, launch.Pool, launch.LaunchedUtc, LaunchedWorkerState.Starting, listed: false, pending: true, launchedHere: true);
            }
        }

        foreach (var node in nodes)
        {
            if (WorkerNames.PoolPartOf(node.Name, _settings.NamePrefix) is { } part && !workers.ContainsKey(node.Name))
            {
                var here = _launchedHere.TryGetValue(node.Name, out var launchedHere);
                workers[node.Name] = Worker(
                    node.Name, PoolOf(part), here ? launchedHere : null, LaunchedWorkerState.Running, listed: false, pending: false, here);
            }
        }

        if (_listCurrent)
        {
            Prune(workers.Keys, nodes);
        }

        _namesInUse.Clear();
        _namesInUse.UnionWith(workers.Keys);
        _namesInUse.UnionWith(nodes.Select(n => n.Name));
        _namesInUse.UnionWith(_launchedHere.Keys);
        return [.. workers.Values];
    }

    private FleetWorker Worker(
        string name, string? pool, DateTime? launchedUtc, LaunchedWorkerState state, bool listed, bool pending, bool launchedHere)
        => new(
            name, pool, launchedUtc, state, listed, pending, launchedHere,
            SameBuild: string.Equals(WorkerNames.BuildTagOf(name, _settings.NamePrefix), _settings.BuildTag, StringComparison.Ordinal),
            HasPolled: _polledSinceTakeover.Contains(name),
            EndedSeenUtc: _endedSeenUtc.TryGetValue(name, out var ended) ? ended : null);

    /// <summary>Gives up on a start that neither appeared nor polled, so the pool is not held below its minimum waiting for
    /// it. With a current list: once <see cref="MaxListMisses"/> lists in a row and at least
    /// <see cref="NeverListedAfter"/> have missed it, it was never made (a launch answered with an error after which
    /// nothing was created). With no current list: once it has had the whole start timeout, whatever the platform did
    /// with it; should it appear later, it is judged as any worker this process did not start. Either way it is a failed
    /// start, counted once.</summary>
    private void ForgetStartsNeverMade(Dictionary<string, LaunchedWorker> listed, IReadOnlyList<NodeView> nodes, DateTime now)
    {
        var polling = nodes.Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (name, launch) in _pending.ToList())
        {
            if (polling.Contains(name) || (_listCurrent && listed.ContainsKey(name)))
            {
                launch.Misses = 0;
                continue;
            }

            var age = now - launch.LaunchedUtc;
            string message;
            if (_listCurrent)
            {
                if (++launch.Misses < MaxListMisses || age < NeverListedAfter)
                {
                    continue;
                }

                message = $"the platform never listed it ({launch.Misses} lists over {(int)age.TotalSeconds} s); it was not started";
            }
            else if (age >= _settings.StartTimeout)
            {
                message = $"it has not polled in the {(int)_settings.StartTimeout.TotalMinutes} min it was given to start, and the platform could not be asked about it; it no longer counts as starting";
            }
            else
            {
                continue;
            }

            _pending.Remove(name);
            _launchedHere.Remove(name);
            Fail(launch.Pool, name, now, message);
            Record(ManagedWorkerActions.StartFailed, launch.Pool, name, message);
        }
    }

    /// <summary>Forgets names nothing reports any more (out of the platform's history, never polling), so a long
    /// ownership period does not grow the bookkeeping without bound.</summary>
    private void Prune(IEnumerable<string> workers, IReadOnlyList<NodeView> nodes)
    {
        var live = new HashSet<string>(workers, StringComparer.Ordinal);
        live.UnionWith(nodes.Select(n => n.Name));
        _stable.IntersectWith(live);
        _failedStarts.IntersectWith(live);
        _known.IntersectWith(live);
        _accountedEnded.IntersectWith(live);
        _askedToLeave.IntersectWith(live);
        _polledSinceTakeover.IntersectWith(live);
        foreach (var name in _firstPolledUtc.Keys.Where(n => !live.Contains(n)).ToList())
        {
            _firstPolledUtc.Remove(name);
        }

        foreach (var name in _endedSeenUtc.Keys.Where(n => !live.Contains(n)).ToList())
        {
            _endedSeenUtc.Remove(name);
        }

        foreach (var name in _stoppedAt.Keys.Where(n => !live.Contains(n)).ToList())
        {
            _stoppedAt.Remove(name);
        }

        foreach (var name in _launchedHere.Keys.Where(n => !live.Contains(n) && !_pending.ContainsKey(n)).ToList())
        {
            _launchedHere.Remove(name);
        }
    }

    /// <summary>Notes a listed worker this process has seen, and records one that ended. A worker this control plane
    /// started in this ownership period, and did not ask to leave, is a failed start when it ended before its first poll,
    /// however quickly (one that dies before any list shows it running is still this control plane's own start), or when
    /// it ended within <see cref="ManagedFleetSettings.StableAfter"/> of its first poll. <paramref name="launchedHereUtc"/>
    /// is when this process started it, null when it did not.</summary>
    private void AccountFor(LaunchedWorker listed, string? pool, DateTime? launchedHereUtc, DateTime now)
    {
        if (listed.State != LaunchedWorkerState.Ended)
        {
            _known.Add(listed.Name);
            return;
        }

        _endedSeenUtc.TryAdd(listed.Name, now);
        if ((!_known.Contains(listed.Name) && launchedHereUtc is null) || !_accountedEnded.Add(listed.Name))
        {
            return; // ended before this process ever saw it, and not started by it; or already accounted for
        }

        var detail = string.IsNullOrWhiteSpace(listed.Detail) ? "it ended" : $"it ended: {listed.Detail}";
        var ours = launchedHereUtc is { } launched && launched >= _takeoverUtc && pool is not null;
        var asked = _askedToLeave.Contains(listed.Name) || _stoppedAt.ContainsKey(listed.Name);
        var polled = _firstPolledUtc.TryGetValue(listed.Name, out var first);
        if (ours && !asked && !polled)
        {
            Fail(pool!, listed.Name, now, $"{detail} before its first poll");
            Record(ManagedWorkerActions.StartFailed, pool, listed.Name, $"{detail} before its first poll");
            return;
        }

        if (ours && !asked && polled && now - first < _settings.StableAfter)
        {
            var message = $"{detail} {(int)(now - first).TotalSeconds} s after its first poll, unasked";
            Fail(pool!, listed.Name, now, message);
            Record(ManagedWorkerActions.StartFailed, pool, listed.Name, message);
            return;
        }

        Record(ManagedWorkerActions.Ended, pool, listed.Name, detail);
    }

    private async Task StopAsync(PlannedStop stop, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        if (_stoppedAt.TryGetValue(stop.Name, out var asked) && now - asked < _settings.ExitTimeout)
        {
            return; // asked recently; the platform needs time to end it
        }

        // Drained first: a worker that answers again between the stop and the platform's kill is handed nothing.
        _dispatcher.DrainNode(stop.Name);
        _askedToLeave.Add(stop.Name);
        _stoppedAt[stop.Name] = now;
        _pending.Remove(stop.Name);
        if (stop.FailedStart && stop.Pool is not null)
        {
            Fail(stop.Pool, stop.Name, now, $"{stop.Name} {stop.Reason}");
        }

        try
        {
            using var call = CallBudget(ct);
            await _launcher.StopAsync(stop.Name, call.Token).ConfigureAwait(false);
            Record(ManagedWorkerActions.Stop, stop.Pool, stop.Name, stop.Reason);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = ex is OperationCanceledException
                ? $"the launcher did not answer within {(int)_settings.LauncherCallTimeout.TotalSeconds} s"
                : SecretHygiene.RedactedMessage(ex);
            Record(ManagedWorkerActions.StopFailed, stop.Pool, stop.Name, $"{stop.Reason}; the stop failed: {error}");
        }
    }

    /// <summary>Starts one worker; false when the start failed (the pool is then backed off).</summary>
    private async Task<bool> LaunchAsync(PlannedLaunch launch, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;

        // A name no worker has: two processes polling under one name would share every lease and could never be drained.
        string? name = null;
        for (var attempt = 0; attempt < NameAttempts && name is null; attempt++)
        {
            var candidate = NameFactory(_settings.NamePrefix, launch.Pool, _settings.BuildTag);
            if (_namesInUse.Add(candidate))
            {
                name = candidate;
            }
        }

        if (name is null)
        {
            var message = $"no unused worker name was found in {NameAttempts} tries";
            Fail(launch.Pool, null, now, message);
            Record(ManagedWorkerActions.StartFailed, launch.Pool, null, message);
            return false;
        }

        // Tracked before the call: a start the platform accepted but answered too late for is still counted as starting,
        // so it is never started twice.
        _pending[name] = new PendingLaunch(launch.Pool, now);
        _launchedHere[name] = now;
        try
        {
            using var call = CallBudget(ct);
            await _launcher.LaunchAsync(new WorkerLaunch(name, launch.Pool), call.Token).ConfigureAwait(false);
            Record(ManagedWorkerActions.Start, launch.Pool, name, launch.Reason);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var mayHaveStarted = ex is OperationCanceledException or WorkerLauncherException { MayHaveStarted: true };
            if (!mayHaveStarted)
            {
                _pending.Remove(name);
                _launchedHere.Remove(name);
            }

            var error = ex is OperationCanceledException
                ? $"the launcher did not answer within {(int)_settings.LauncherCallTimeout.TotalSeconds} s"
                : SecretHygiene.RedactedMessage(ex);
            var message = mayHaveStarted ? $"{error} (it may still start; it is counted as starting until a list shows otherwise)" : error;
            Fail(launch.Pool, name, _clock.GetUtcNow().UtcDateTime, message);
            Record(ManagedWorkerActions.StartFailed, launch.Pool, name, message);
            return false;
        }
    }

    /// <summary>Counts a failed start against <paramref name="pool"/> and backs it off. A worker counts once at most,
    /// however many ways its failure shows (a launch that timed out and was then never listed, a stop for never polling
    /// followed by the platform reporting it ended), so the back-off climbs once per failed worker.</summary>
    private void Fail(string pool, string? worker, DateTime now, string error)
    {
        if (worker is not null && !_failedStarts.Add(worker))
        {
            return;
        }

        var failures = _failures.GetValueOrDefault(pool) + 1;
        _failures[pool] = failures;
        var exponent = Math.Min(failures - 1, 16);
        var delay = TimeSpan.FromTicks(Math.Min(
            _settings.MaxBackoff.Ticks, _settings.MinBackoff.Ticks * (1L << exponent)));
        _blockedUntil[pool] = now + delay;
        _lastError[pool] = error;
        _lastFailureUtc[pool] = now;
        LogPoolBackedOff(PoolLabel(pool), failures, (int)delay.TotalSeconds, error);
    }

    private void Record(string kind, string? pool, string? worker, string message, bool warning = false)
    {
        var action = new ManagedWorkerAction(_clock.GetUtcNow().UtcDateTime, kind, pool, worker, message);
        lock (_statusGate)
        {
            _actions.AddFirst(action);
            while (_actions.Count > _settings.RecentActions)
            {
                _actions.RemoveLast();
            }
        }

        if (warning || kind is ManagedWorkerActions.StartFailed or ManagedWorkerActions.StopFailed)
        {
            LogActionWarning(kind, PoolLabel(action.Pool), worker ?? "-", message);
        }
        else
        {
            LogAction(kind, PoolLabel(action.Pool), worker ?? "-", message);
        }
    }

    private void Publish(DateTime now, bool active, FleetPlan? plan)
    {
        var pools = plan is null
            ? []
            : plan.Pools.Select(p => new ManagedPoolStatus(
                p.Pool, p.MinWorkers, p.MaxWorkers, p.Target, p.Starting, p.Ready, p.Due, p.Draining, p.Busy,
                p.EligibleRuns, p.UnservedRuns, p.RunSlotsPerWorker, p.BlockedUntilUtc,
                _failures.GetValueOrDefault(p.Pool), _lastError.GetValueOrDefault(p.Pool), p.Silent)).ToList();
        lock (_statusGate)
        {
            _status = new ManagedFleetStatus(
                active, _launcher.Name, _takeoverUtc, now, _lastListUtc, _listCurrent, _listError, pools,
                plan?.Workers ?? [], [.. _actions]);
        }
    }

    private CancellationTokenSource CallBudget(CancellationToken ct)
    {
        var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(_settings.LauncherCallTimeout);
        return call;
    }

    public void Dispose() => _tickGate.Dispose();

    private string? PoolOf(string part) => _poolByPart.TryGetValue(part, out var pool) ? pool : null;

    private static string PoolLabel(string? pool) => pool is null ? "(not managed)" : pool.Length == 0 ? "default" : pool;

    /// <summary>A start this control plane made and has not seen listed: its pool, when, and how many current lists in a
    /// row have missed it.</summary>
    private sealed class PendingLaunch(string pool, DateTime launchedUtc)
    {
        public string Pool { get; } = pool;

        public DateTime LaunchedUtc { get; } = launchedUtc;

        public int Misses { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: this control plane took over dispatch at {TakeoverUtc:o} and manages {PoolCount} pool(s) through the '{Launcher}' launcher.")]
    private partial void LogTakeover(DateTime takeoverUtc, string launcher, int poolCount);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: this control plane no longer owns dispatch; it starts and stops nothing until it does again.")]
    private partial void LogPassive();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: dispatch ownership changed during a tick; the tick stopped before acting on a picture that may be stale, and the next one starts afresh.")]
    private partial void LogOwnershipChanged();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: {Count} {What} wait for the next tick; this tick spent the {BudgetSeconds} s it gives them.")]
    private partial void LogDeferred(int count, string what, int budgetSeconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: this control plane did not tick for {Seconds} s (the process stood still, or its clock jumped); every worker's silence, start and idle time counts from now.")]
    private partial void LogResumed(int seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: the '{Launcher}' launcher could not list its workers ({Error}); nothing is stopped and only pool minimums are restored until it answers.")]
    private partial void LogListFailed(string launcher, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: the '{Launcher}' launcher answers again.")]
    private partial void LogListRecovered(string launcher);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: the pool floors set in the GUI could not be read ({Error}); the last ones read, and the configured minimums, still apply.")]
    private partial void LogGuiMinimumFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: pool '{Pool}' has failed to start a worker {Failures} time(s) in a row; next try in {DelaySeconds} s. Last failure: {Error}")]
    private partial void LogPoolBackedOff(string pool, int failures, int delaySeconds, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: a new worker of pool '{Pool}' has kept polling; its failed starts are forgiven.")]
    private partial void LogPoolRecovered(string pool);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: {Kind} pool '{Pool}' worker '{Worker}': {Message}")]
    private partial void LogAction(string kind, string pool, string worker, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: {Kind} pool '{Pool}' worker '{Worker}': {Message}")]
    private partial void LogActionWarning(string kind, string pool, string worker, string message);
}
