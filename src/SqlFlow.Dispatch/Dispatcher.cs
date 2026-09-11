using Microsoft.Extensions.Logging;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch;

/// <summary>
/// The dispatcher: the one authority over who executes what. It owns the in-memory <see cref="DispatchState"/> and
/// <see cref="NodeRegistry"/>, journals every placement through the <see cref="IDispatchLedger"/>, serves node
/// polls and outcome reports, and runs the housekeeping ticks (lease expiry, task expiry, node flush and prune,
/// reconcile). Exactly one dispatcher in an estate is active at a time (the host arbitrates through the ledger's
/// ownership lease); a passive one refuses node calls with <see cref="DispatchInactiveException"/>.
/// </summary>
/// <remarks>
/// Write ordering is what keeps the journal and memory consistent without a distributed transaction: an enqueue is
/// journaled before memory learns of it; a hand-out is reserved in memory, journaled, then confirmed, so the ledger
/// is never behind what a node holds; an outcome is journaled under the fence, then memory drops the run. Anything
/// that bypasses the in-process notify (a direct catalog cancel, an enqueue on a passive replica) is picked up by
/// reconcile, which acts on additions at once and on removals only when they persist across two passes, because
/// the ledger read and memory move independently.
/// </remarks>
public sealed partial class Dispatcher : IDisposable
{
    private readonly IDispatchLedger _ledger;
    private readonly DispatchOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<Dispatcher> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly SemaphoreSlim _expiryGate = new(1, 1);
    private readonly HashSet<Guid> _suspectRuns = [];
    private readonly HashSet<Guid> _suspectTasks = [];
    private volatile bool _active;
    private string? _owner;
    private DateTime? _activatedUtc;
    private DateTime? _lastReconcileUtc;
    private DateTime? _lastTickUtc;
    private DateTime? _lastNodeFlushUtc;
    private DateTime? _lastTaskExpiryUtc;
    private DateTime? _lastNodePruneUtc;

    public Dispatcher(IDispatchLedger ledger, DispatchOptions options, TimeProvider clock, ILogger<Dispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();
        _ledger = ledger;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>The queue this dispatcher drives. Exposed for the read surface and the test suite; hosts mutate it
    /// only through this class.</summary>
    public DispatchState Queue { get; } = new();

    /// <summary>The fleet as this dispatcher last heard from it.</summary>
    public NodeRegistry Nodes { get; } = new();

    /// <summary>Whether this dispatcher holds ownership and serves nodes.</summary>
    public bool IsActive => _active;

    /// <summary>The timing knobs in force.</summary>
    public DispatchOptions Options => _options;

    // ------------------------------------------------------------------------------------------ lifecycle -------

    /// <summary>Takes ownership: rebuilds memory from the ledger (queued rows queued, running rows leased to their
    /// node under a grace lease so a node still executing them reattaches on its next poll) and starts serving.</summary>
    public async Task ActivateAsync(string owner, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        var snapshot = await _ledger.LoadAsync(ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow().UtcDateTime;
        var grace = now + _options.Lease;

        Queue.Clear();
        Nodes.Clear();
        foreach (var run in snapshot.QueuedRuns)
        {
            Queue.AddQueuedRun(run);
        }

        foreach (var running in snapshot.RunningRuns)
        {
            Queue.AddLeasedRun(running.Run, running.Node, now, grace);
        }

        foreach (var task in snapshot.QueuedTasks)
        {
            Queue.AddQueuedTask(task);
        }

        foreach (var running in snapshot.RunningTasks)
        {
            Queue.AddLeasedTask(running.Task, running.Node, now, grace);
        }

        lock (_suspectRuns)
        {
            _suspectRuns.Clear();
            _suspectTasks.Clear();
        }

        _owner = owner;
        _activatedUtc = now;
        _lastReconcileUtc = now;
        _lastNodeFlushUtc = now;
        _lastTaskExpiryUtc = now;
        _lastNodePruneUtc = now;
        _active = true;
        LogActivated(owner, snapshot.QueuedRuns.Count, snapshot.RunningRuns.Count, snapshot.QueuedTasks.Count, snapshot.RunningTasks.Count);
    }

    /// <summary>Gives up ownership: stops serving, drops memory, and wakes every parked poll so each answers with
    /// the inactive error and its node retries against the new owner.</summary>
    public void Deactivate()
    {
        if (!_active)
        {
            return;
        }

        _active = false;
        Queue.Clear();
        Nodes.Clear();
        LogDeactivated(_owner ?? "(none)");
        _owner = null;
    }

    // ------------------------------------------------------------------------------------------- notifies -------

    /// <summary>Tells memory about runs the caller just journaled as queued. A passive dispatcher ignores it (the
    /// owner's reconcile picks the rows up).</summary>
    public void NotifyRunsEnqueued(IEnumerable<DispatchRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        if (!_active)
        {
            return;
        }

        foreach (var run in runs)
        {
            Queue.AddQueuedRun(run);
        }
    }

    /// <summary>Tells memory an operator cancelled a run (the ledger write already happened): a queued run leaves
    /// the queue, a held run's node hears the request on its next poll, which is woken at once.</summary>
    public CancelMark NotifyRunCancelled(Guid runId) => _active ? Queue.MarkRunCancel(runId) : CancelMark.Unknown;

    /// <summary>Tells memory an operator cancelled a whole run group.</summary>
    public int NotifyGroupCancelled(Guid groupId) => _active ? Queue.MarkGroupCancel(groupId) : 0;

    /// <summary>Tells memory about a compute task the caller just journaled as queued.</summary>
    public void NotifyTaskEnqueued(DispatchTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (_active)
        {
            Queue.AddQueuedTask(task);
        }
    }

    /// <summary>Tells memory an operator cancelled a compute task.</summary>
    public CancelMark NotifyTaskCancelled(Guid taskId) => _active ? Queue.MarkTaskCancel(taskId) : CancelMark.Unknown;

    /// <summary>Wakes a node's parked poll so it hears a restart request before its next scheduled poll. The request
    /// itself reaches memory on the next node flush, which reads it back from the ledger.</summary>
    public void NotifyNodeRestartRequested(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        if (_active)
        {
            Queue.WakeNode(node);
        }
    }

    // --------------------------------------------------------------------------------------------- polling -------

    /// <summary>Serves a node's poll: registers the heartbeat, renews the node's leases (reporting the ones it lost),
    /// collects pending cancels and a restart request, hands out as much eligible work as the node has room for, and
    /// otherwise parks the call until something changes or the wait elapses.</summary>
    public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Node);
        ArgumentNullException.ThrowIfNull(request.Pools);
        ArgumentNullException.ThrowIfNull(request.HoldingRuns);
        ArgumentNullException.ThrowIfNull(request.HoldingTasks);
        EnsureActive();

        var poolKeys = request.Pools
            .Select(DispatchState.PoolKey)
            .Where(k => k.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var freeRunSlots = Math.Max(0, request.FreeRunSlots);
        var freeTaskSlots = Math.Max(0, request.FreeTaskSlots);
        var wantsWork = freeRunSlots > 0 || freeTaskSlots > 0;
        var waitSeconds = Math.Clamp(request.WaitSeconds, 0, _options.LongPollSeconds);
        var deadline = _clock.GetUtcNow() + TimeSpan.FromSeconds(waitSeconds);

        while (true)
        {
            EnsureActive();
            var now = _clock.GetUtcNow().UtcDateTime;
            var leaseExpires = now + _options.Lease;
            Nodes.Touch(request, poolKeys, now);

            var revokedRuns = Queue.RenewRunLeases(request.Node, request.HoldingRuns, leaseExpires);
            var revokedTasks = Queue.RenewTaskLeases(request.Node, request.HoldingTasks, leaseExpires);
            var cancelRuns = Queue.CancelRequestedRunsHeldBy(request.Node);
            var cancelTasks = Queue.CancelRequestedTasksHeldBy(request.Node);
            var tasks = await HandOutTasksAsync(request.Node, poolKeys, freeTaskSlots, ct).ConfigureAwait(false);
            var runs = await HandOutRunsAsync(request.Node, poolKeys, freeRunSlots, ct).ConfigureAwait(false);
            var restart = Nodes.IsRestartPending(request.Node, request.StartedUtc);

            var response = new NodePollResponse(
                runs, tasks, cancelRuns, cancelTasks, revokedRuns, revokedTasks, restart, _options.LeaseSeconds);
            if (response.HasContent)
            {
                return response;
            }

            var remaining = deadline - _clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                return response;
            }

            // Park until work this node could take appears, a signal for it arrives, or the wait elapses; then
            // re-evaluate from the top, which also renews the leases again.
            await Queue.WaitAsync(request.Node, poolKeys, wantsWork, remaining, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<RunHandout>> HandOutRunsAsync(
        string node, IReadOnlyList<string> poolKeys, int maxCount, CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var reservations = Queue.ReserveRuns(node, poolKeys, maxCount);
        if (reservations.Count == 0)
        {
            return [];
        }

        var handed = new List<RunHandout>(reservations.Count);
        foreach (var reservation in reservations)
        {
            bool journaled;
            var now = _clock.GetUtcNow().UtcDateTime;
            try
            {
                journaled = await _ledger
                    .MarkRunHandedOutAsync(reservation.RunId, reservation.ExpectedAttempt, node, now, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The poll was abandoned mid-hand-out. The row may or may not have been written; either way the
                // run goes back to the queue in memory, and if the ledger did record the hand-out, reconcile finds
                // the running row with no lease, grants a grace lease, and the lease expiry requeues it.
                Queue.ReleaseRunReservation(reservation.RunId);
                throw;
            }
            catch (Exception ex)
            {
                // A transient ledger failure: the run stays queued and the next poll retries it.
                Queue.ReleaseRunReservation(reservation.RunId);
                LogHandOutFailed(reservation.RunId, node, ex.Message);
                continue;
            }

            if (!journaled)
            {
                // The ledger no longer holds the row as queued at this attempt (cancelled or removed directly):
                // memory was stale for this run, so drop it; reconcile re-adds it if it is in fact still queued.
                Queue.RemoveRun(reservation.RunId);
                LogHandOutDropped(reservation.RunId, node);
                continue;
            }

            var attempt = reservation.ExpectedAttempt + 1;
            if (!Queue.ConfirmRunLease(reservation.RunId, node, attempt, now, now + _options.Lease))
            {
                // Memory was cleared between the reservation and here (ownership changed). The ledger says this
                // node holds the run, so it does: the new owner's reconcile grants the grace lease it reattaches to.
                LogHandOutUnconfirmed(reservation.RunId, node);
            }

            handed.Add(new RunHandout(reservation.RunId, attempt));
        }

        return handed;
    }

    private async Task<IReadOnlyList<TaskHandout>> HandOutTasksAsync(
        string node, IReadOnlyList<string> poolKeys, int maxCount, CancellationToken ct)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        var reservations = Queue.ReserveTasks(node, poolKeys, maxCount);
        if (reservations.Count == 0)
        {
            return [];
        }

        var handed = new List<TaskHandout>(reservations.Count);
        foreach (var taskId in reservations)
        {
            bool journaled;
            var now = _clock.GetUtcNow().UtcDateTime;
            try
            {
                journaled = await _ledger.MarkTaskHandedOutAsync(taskId, node, now, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Queue.ReleaseTaskReservation(taskId);
                throw;
            }
            catch (Exception ex)
            {
                Queue.ReleaseTaskReservation(taskId);
                LogHandOutFailed(taskId, node, ex.Message);
                continue;
            }

            if (!journaled)
            {
                Queue.RemoveTask(taskId);
                LogHandOutDropped(taskId, node);
                continue;
            }

            if (!Queue.ConfirmTaskLease(taskId, node, now, now + _options.Lease))
            {
                LogHandOutUnconfirmed(taskId, node);
            }

            handed.Add(new TaskHandout(taskId));
        }

        return handed;
    }

    // -------------------------------------------------------------------------------------------- outcomes -------

    /// <summary>Records a run's outcome under the fence and drops it (and any dependents the ledger skipped) from
    /// memory. A stale report leaves the current holder's state alone.</summary>
    public async Task<RunOutcomeStatus> RecordRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Node);
        EnsureActive();

        var now = _clock.GetUtcNow().UtcDateTime;
        var record = await _ledger
            .RecordRunOutcomeAsync(runId, request.Node, request.Attempt, request.Outcome, request.Error, request.ArtifactJson, now, ct)
            .ConfigureAwait(false);
        if (record.Status == RunOutcomeStatus.StaleClaim)
        {
            // Memory may still attribute the run to this node (someone rewrote the row directly): converge on the
            // ledger, and let reconcile re-derive whoever holds it now.
            Queue.RemoveRunIfHeldBy(runId, request.Node, request.Attempt);
            LogStaleOutcome(runId, request.Node, request.Attempt);
            return record.Status;
        }

        Queue.RemoveRun(runId);
        foreach (var skipped in record.SkippedRunIds)
        {
            Queue.RemoveRun(skipped);
        }

        return record.Status;
    }

    /// <summary>Records a compute task's outcome under the node fence and drops it from memory.</summary>
    public async Task<bool> RecordTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Node);
        EnsureActive();

        var now = _clock.GetUtcNow().UtcDateTime;
        var recorded = await _ledger
            .RecordTaskOutcomeAsync(taskId, request.Node, request.Outcome, request.Error, request.ResultJson, now, ct)
            .ConfigureAwait(false);
        if (recorded)
        {
            Queue.RemoveTask(taskId);
        }
        else
        {
            Queue.RemoveTaskIfHeldBy(taskId, request.Node);
        }

        return recorded;
    }

    // ---------------------------------------------------------------------------------------- housekeeping -------

    /// <summary>One housekeeping pass, meant to run about once a second: dispositions lapsed leases, expires stale
    /// tasks, flushes the node registry, prunes silent nodes, and reconciles memory with the ledger on their
    /// cadences. Each step is isolated, so one failing step never blocks the others.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        if (!_active)
        {
            return;
        }

        var now = _clock.GetUtcNow().UtcDateTime;
        _lastTickUtc = now;

        await RunStepAsync("lease expiry", () => ExpireLeasesAsync(now, ct)).ConfigureAwait(false);

        if (IsDue(_lastTaskExpiryUtc, TimeSpan.FromMinutes(1), now))
        {
            _lastTaskExpiryUtc = now;
            await RunStepAsync("task expiry", () => ExpireTasksAsync(now, ct)).ConfigureAwait(false);
        }

        if (IsDue(_lastNodeFlushUtc, _options.NodeFlush, now))
        {
            _lastNodeFlushUtc = now;
            await RunStepAsync("node flush", () => FlushNodesAsync(ct)).ConfigureAwait(false);
        }

        if (_options.NodeRetention > TimeSpan.Zero && IsDue(_lastNodePruneUtc, TimeSpan.FromHours(1), now))
        {
            _lastNodePruneUtc = now;
            await RunStepAsync("node prune", () => PruneNodesAsync(now, ct)).ConfigureAwait(false);
        }

        if (IsDue(_lastReconcileUtc, _options.Reconcile, now))
        {
            await RunStepAsync("reconcile", () => ReconcileAsync(ct)).ConfigureAwait(false);
        }
    }

    private async Task RunStepAsync(string step, Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogStepFailed(step, ex.Message);
        }
    }

    private static bool IsDue(DateTime? last, TimeSpan interval, DateTime now) => last is null || now - last.Value >= interval;

    /// <summary>Dispositions every lapsed lease exactly as a dead node's runs were always dispositioned: requeue
    /// while attempts remain, record cancelled when an operator's cancel was pending, fail once the budget is
    /// exhausted. Every ledger write is fenced, so a node completing in the same instant wins cleanly.</summary>
    private async Task ExpireLeasesAsync(DateTime now, CancellationToken ct)
    {
        if (!await _expiryGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return; // a previous pass is still journaling its dispositions
        }

        try
        {
            foreach (var expired in Queue.TakeExpiredRunLeases(now))
            {
                if (expired.CancelRequested)
                {
                    var cancelled = await _ledger
                        .CancelInterruptedRunAsync(expired.RunId, expired.Node, expired.Attempt, now, ct)
                        .ConfigureAwait(false);
                    Queue.RemoveRun(expired.RunId);
                    RemoveSkipped(cancelled.SkippedRunIds);
                    LogLeaseExpiredCancelled(expired.RunId, expired.Node, cancelled.Applied);
                }
                else if (expired.Attempt < _options.MaxExecutionAttempts)
                {
                    var requeued = await _ledger
                        .RequeueInterruptedRunAsync(expired.RunId, expired.Node, expired.Attempt, now, ct)
                        .ConfigureAwait(false);
                    if (requeued.Applied)
                    {
                        Queue.RequeueExpiredRun(expired.RunId);
                    }
                    else
                    {
                        Queue.RemoveRun(expired.RunId);
                    }

                    LogLeaseExpiredRequeued(expired.RunId, expired.Node, expired.Attempt, requeued.Applied);
                }
                else
                {
                    var failed = await _ledger
                        .FailInterruptedRunAsync(expired.RunId, expired.Node, expired.Attempt, now, ct)
                        .ConfigureAwait(false);
                    Queue.RemoveRun(expired.RunId);
                    RemoveSkipped(failed.SkippedRunIds);
                    LogLeaseExpiredFailed(expired.RunId, expired.Node, expired.Attempt, failed.Applied);
                }
            }

            foreach (var expired in Queue.TakeExpiredTaskLeases(now))
            {
                var requeued = await _ledger
                    .RequeueInterruptedTaskAsync(expired.TaskId, expired.Node, now, ct)
                    .ConfigureAwait(false);
                if (requeued)
                {
                    Queue.RequeueExpiredTask(expired.TaskId);
                }
                else
                {
                    Queue.RemoveTask(expired.TaskId);
                }

                LogTaskLeaseExpired(expired.TaskId, expired.Node, requeued);
            }
        }
        finally
        {
            _expiryGate.Release();
        }
    }

    private void RemoveSkipped(IReadOnlyList<Guid> skipped)
    {
        foreach (var runId in skipped)
        {
            Queue.RemoveRun(runId);
        }
    }

    private async Task ExpireTasksAsync(DateTime now, CancellationToken ct)
    {
        var expired = await _ledger
            .ExpireTasksAsync(now - _options.TaskQueuedExpiry, now - _options.TaskRunningExpiry, now, ct)
            .ConfigureAwait(false);
        foreach (var taskId in expired)
        {
            Queue.RemoveTask(taskId);
        }

        if (expired.Count > 0)
        {
            LogTasksExpired(expired.Count);
        }
    }

    private async Task FlushNodesAsync(CancellationToken ct)
    {
        foreach (var beat in Nodes.TakeDirty())
        {
            var restart = await _ledger.RecordNodeHeartbeatAsync(beat, ct).ConfigureAwait(false);
            if (Nodes.SetRestartRequested(beat.Name, restart))
            {
                Queue.WakeNode(beat.Name);
            }
        }
    }

    private async Task PruneNodesAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now - _options.NodeRetention;
        Nodes.Expire(cutoff);
        var pruned = await _ledger.PruneNodesAsync(cutoff, ct).ConfigureAwait(false);
        if (pruned > 0)
        {
            LogNodesPruned(pruned, _options.NodeRetentionHours);
        }
    }

    /// <summary>Diffs memory against the ledger. Additions (queued rows memory lacks, running rows without a lease,
    /// cancel flags stamped directly on rows) apply at once; removals (entries the ledger no longer agrees with)
    /// apply only when the same entry is stale in two consecutive passes, which no in-flight transition can be.</summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        if (!_active || !await _reconcileGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var active = await _ledger.ListActiveAsync(ct).ConfigureAwait(false);
            var now = _clock.GetUtcNow().UtcDateTime;
            var grace = now + _options.Lease;

            var runDiff = Queue.DiffRuns(active.QueuedRunIds, active.RunningRuns);
            if (runDiff.UnknownQueued.Count > 0)
            {
                foreach (var run in await _ledger.LoadQueuedRunsAsync(runDiff.UnknownQueued, ct).ConfigureAwait(false))
                {
                    Queue.AddQueuedRun(run);
                }
            }

            if (runDiff.UnknownRunning.Count > 0)
            {
                // A running row memory has no lease for: the previous owner handed it out, or a hand-out was
                // journaled and then abandoned. Lease it to the recorded node under grace so the node reattaches on
                // its next poll; if nobody does, the expiry dispositions it exactly as any lost lease.
                var ids = runDiff.UnknownRunning.Select(r => r.RunId).ToList();
                foreach (var running in await _ledger.LoadRunningRunsAsync(ids, ct).ConfigureAwait(false))
                {
                    if (Queue.AddLeasedRun(running.Run, running.Node, now, grace))
                    {
                        LogReconcileAdopted("run", running.Run.RunId, running.Node);
                    }
                }
            }

            foreach (var runId in runDiff.CancelRequested)
            {
                Queue.FlagRunCancelRequested(runId);
            }

            var taskDiff = Queue.DiffTasks(active.QueuedTaskIds, active.RunningTasks);
            if (taskDiff.UnknownQueued.Count > 0)
            {
                foreach (var task in await _ledger.LoadQueuedTasksAsync(taskDiff.UnknownQueued, ct).ConfigureAwait(false))
                {
                    Queue.AddQueuedTask(task);
                }
            }

            if (taskDiff.UnknownRunning.Count > 0)
            {
                var ids = taskDiff.UnknownRunning.Select(t => t.TaskId).ToList();
                foreach (var running in await _ledger.LoadRunningTasksAsync(ids, ct).ConfigureAwait(false))
                {
                    if (Queue.AddLeasedTask(running.Task, running.Node, now, grace))
                    {
                        LogReconcileAdopted("task", running.Task.TaskId, running.Node);
                    }
                }
            }

            foreach (var taskId in taskDiff.CancelRequested)
            {
                Queue.FlagTaskCancelRequested(taskId);
            }

            ApplyStale(runDiff.Stale, taskDiff.Stale);
            _lastReconcileUtc = now;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private void ApplyStale(IReadOnlyList<Guid> staleRuns, IReadOnlyList<Guid> staleTasks)
    {
        lock (_suspectRuns)
        {
            var confirmedRuns = staleRuns.Where(_suspectRuns.Contains).ToList();
            _suspectRuns.Clear();
            _suspectRuns.UnionWith(staleRuns);
            foreach (var runId in confirmedRuns)
            {
                _suspectRuns.Remove(runId);
                if (Queue.RemoveRun(runId))
                {
                    LogReconcileRemoved("run", runId);
                }
            }

            var confirmedTasks = staleTasks.Where(_suspectTasks.Contains).ToList();
            _suspectTasks.Clear();
            _suspectTasks.UnionWith(staleTasks);
            foreach (var taskId in confirmedTasks)
            {
                _suspectTasks.Remove(taskId);
                if (Queue.RemoveTask(taskId))
                {
                    LogReconcileRemoved("task", taskId);
                }
            }
        }
    }

    // -------------------------------------------------------------------------------------------- snapshot -------

    /// <summary>The dispatcher as it sees itself right now.</summary>
    public DispatchSnapshot Snapshot()
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var queue = Queue.Snapshot(poolKey => Nodes.Capacity(poolKey, now));
        return new DispatchSnapshot(
            _active, _owner, _activatedUtc, _lastReconcileUtc, _lastTickUtc, queue.Waiters, queue.Pools,
            queue.QueuedRuns, queue.LeasedRuns, queue.QueuedTasks, queue.LeasedTasks, Nodes.Snapshot(now));
    }

    /// <summary>The replica-target terms for a pool: queued runs and the online nodes holding work.</summary>
    public (int QueuedRuns, int LeasedRuns, int OnlineNodes, int FreeRunSlots) PoolDemand(string? pool)
    {
        var key = DispatchState.PoolKey(pool);
        var (queued, leased) = Queue.RunCounts(key);
        var (nodes, slots) = Nodes.Capacity(key, _clock.GetUtcNow().UtcDateTime);
        return (queued, leased, nodes, slots);
    }

    private void EnsureActive()
    {
        if (!_active)
        {
            throw new DispatchInactiveException();
        }
    }

    public void Dispose()
    {
        _reconcileGate.Dispose();
        _expiryGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatcher activated as '{Owner}': {QueuedRuns} queued run(s), {RunningRuns} running run(s) leased to their nodes under grace, {QueuedTasks} queued task(s), {RunningTasks} running task(s).")]
    private partial void LogActivated(string owner, int queuedRuns, int runningRuns, int queuedTasks, int runningTasks);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatcher '{Owner}' deactivated; parked polls released so their nodes retry against the new owner.")]
    private partial void LogDeactivated(string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-out of {Id} to node '{Node}' could not be journaled ({Error}); it stays queued for the next poll.")]
    private partial void LogHandOutFailed(Guid id, string node, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hand-out of {Id} to node '{Node}' dropped: the ledger no longer holds it as queued (cancelled or removed directly).")]
    private partial void LogHandOutDropped(Guid id, string node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hand-out of {Id} to node '{Node}' was journaled but memory was cleared before it could be confirmed (ownership changed); the new owner reconciles it.")]
    private partial void LogHandOutUnconfirmed(Guid id, string node);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: outcome from node '{Node}' at attempt {Attempt} dropped by the fence; the run was requeued after its lease lapsed and the current holder is authoritative.")]
    private partial void LogStaleOutcome(Guid runId, string node, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: lease held by '{Node}' lapsed with a cancel pending; recorded cancelled (applied: {Applied}).")]
    private partial void LogLeaseExpiredCancelled(Guid runId, string node, bool applied);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: lease held by '{Node}' lapsed at attempt {Attempt}; requeued for another node (applied: {Applied}).")]
    private partial void LogLeaseExpiredRequeued(Guid runId, string node, int attempt, bool applied);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: lease held by '{Node}' lapsed at attempt {Attempt}, the last allowed; failed (applied: {Applied}).")]
    private partial void LogLeaseExpiredFailed(Guid runId, string node, int attempt, bool applied);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Compute task {TaskId}: lease held by '{Node}' lapsed; requeued (applied: {Applied}).")]
    private partial void LogTaskLeaseExpired(Guid taskId, string node, bool applied);

    [LoggerMessage(Level = LogLevel.Information, Message = "Expired {Count} compute task(s) that waited or ran too long.")]
    private partial void LogTasksExpired(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pruned {Count} node(s) silent for more than {RetentionHours}h from the fleet registry.")]
    private partial void LogNodesPruned(int count, int retentionHours);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconcile removed {Kind} {Id}: the ledger no longer holds it as memory did.")]
    private partial void LogReconcileRemoved(string kind, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Reconcile adopted running {Kind} {Id} held by node '{Node}' under a grace lease.")]
    private partial void LogReconcileAdopted(string kind, Guid id, string node);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispatcher housekeeping step '{Step}' failed: {Error}")]
    private partial void LogStepFailed(string step, string error);
}
