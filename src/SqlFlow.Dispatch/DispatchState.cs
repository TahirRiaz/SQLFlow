using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch;

/// <summary>
/// The in-memory dispatch state: every queued and held run and compute task, the gates that decide what may be
/// handed out (pool routing, wave order inside a run group, the group concurrency cap, one execution per pipeline),
/// the leases nodes hold, and the waiters of long-polling nodes. Every mutation happens under one lock and takes
/// microseconds, so hundreds of nodes polling is a trivial load; nothing here touches I/O. The
/// <see cref="Dispatcher"/> drives it and writes the ledger between a reservation and its confirmation, which is
/// what keeps the journal never behind what a node holds.
/// </summary>
/// <remarks>
/// Entry lifecycle. A run is <c>Queued</c> (in its pool's ordered set), then <c>Reserved</c> for a node while the
/// hand-out is journaled, then <c>Leased</c> to that node until its outcome is recorded or its lease lapses, at
/// which point it is <c>Expiring</c> while the disposition is journaled and then either back to <c>Queued</c> or
/// gone. A reserved, leased or expiring run occupies its pipeline and counts against its group's cap; a queued run
/// counts only as a pending member of its wave. Orderings are by enqueue time then id, and a run that is not
/// eligible never blocks a later one that is.
/// </remarks>
public sealed class DispatchState
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RunEntry> _runs = [];
    private readonly Dictionary<string, SortedSet<RunEntry>> _queuedRunsByPool = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, int> _busyPipelines = [];
    private readonly Dictionary<Guid, GroupState> _groups = [];
    private readonly Dictionary<Guid, TaskEntry> _tasks = [];
    private readonly Dictionary<string, SortedSet<TaskEntry>> _queuedTasksByPool = new(StringComparer.Ordinal);
    private readonly List<Waiter> _waiters = [];

    /// <summary>Normalizes a pool reference to its key: the empty string for the untargeted pool.</summary>
    public static string PoolKey(string? pool) => string.IsNullOrWhiteSpace(pool) ? string.Empty : pool.Trim();

    // ------------------------------------------------------------------------------------------------ runs -------

    /// <summary>Adds a queued run. Returns false when the run is already known (a duplicate notify and a reconcile
    /// racing is the normal case, not an error).</summary>
    public bool AddQueuedRun(DispatchRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        lock (_gate)
        {
            if (_runs.ContainsKey(run.RunId))
            {
                return false;
            }

            var entry = RunEntry.From(run);
            _runs[run.RunId] = entry;
            QueuedSet(_queuedRunsByPool, entry.PoolKey).Add(entry);
            Group(entry)?.AddPending(entry.GroupWave);
            WakeWorkLocked(entry.PoolKey, 1);
            return true;
        }
    }

    /// <summary>Adds a run the ledger records as executing on a node, under a lease that lasts until
    /// <paramref name="leaseExpiresUtc"/> unless the node renews it. Used at activation and by reconcile so a node
    /// still executing across a dispatcher restart can reattach. Returns false when already known.</summary>
    public bool AddLeasedRun(DispatchRun run, string node, DateTime nowUtc, DateTime leaseExpiresUtc)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            if (_runs.ContainsKey(run.RunId))
            {
                return false;
            }

            var entry = RunEntry.From(run);
            entry.State = EntryState.Leased;
            entry.Node = node;
            entry.LeasedUtc = nowUtc;
            entry.LeaseExpiresUtc = leaseExpiresUtc;
            _runs[run.RunId] = entry;
            OccupyLocked(entry);
            Group(entry)?.AddPending(entry.GroupWave);
            return true;
        }
    }

    /// <summary>Picks up to <paramref name="maxCount"/> eligible runs for a node serving <paramref name="poolKeys"/>
    /// (plus the untargeted pool), oldest first across those pools, and reserves each for the node. A reservation
    /// occupies the run's pipeline and group slot at once, so the picks in one batch respect the gates among
    /// themselves. The caller journals each reservation and then confirms or releases it.</summary>
    public IReadOnlyList<RunReservation> ReserveRuns(string node, IReadOnlyList<string> poolKeys, int maxCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(poolKeys);
        if (maxCount <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            var picks = new List<RunEntry>();
            foreach (var entry in OrderedQueued(_queuedRunsByPool, poolKeys, RunOrder.Instance))
            {
                if (picks.Count >= maxCount)
                {
                    break;
                }

                if (IsEligibleLocked(entry))
                {
                    // Occupy at once so the next pick in this batch sees this pipeline busy and this group slot taken.
                    entry.State = EntryState.Reserved;
                    entry.Node = node;
                    OccupyLocked(entry);
                    picks.Add(entry);
                }
            }

            var reservations = new List<RunReservation>(picks.Count);
            foreach (var entry in picks)
            {
                QueuedSet(_queuedRunsByPool, entry.PoolKey).Remove(entry);
                reservations.Add(new RunReservation(entry.RunId, entry.Attempt));
            }

            return reservations;
        }
    }

    /// <summary>Turns a reservation into a lease after the ledger recorded the hand-out at <paramref name="attempt"/>.
    /// Returns false when the entry is no longer reserved for the node (the queue was cleared meanwhile).</summary>
    public bool ConfirmRunLease(Guid runId, string node, int attempt, DateTime nowUtc, DateTime leaseExpiresUtc)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry) || entry.State != EntryState.Reserved
                || !string.Equals(entry.Node, node, StringComparison.Ordinal))
            {
                return false;
            }

            entry.State = EntryState.Leased;
            entry.Attempt = attempt;
            entry.LeasedUtc = nowUtc;
            entry.LeaseExpiresUtc = leaseExpiresUtc;
            return true;
        }
    }

    /// <summary>Returns a reserved run to the queue (the ledger write failed transiently). A no-op unless reserved.</summary>
    public void ReleaseRunReservation(Guid runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry) || entry.State != EntryState.Reserved)
            {
                return;
            }

            RequeueLocked(entry);
        }
    }

    /// <summary>Removes a run in any state (its outcome was recorded, it was cancelled, skipped, or the ledger no
    /// longer holds it), freeing whatever it occupied. Returns false when unknown.</summary>
    public bool RemoveRun(Guid runId)
    {
        lock (_gate)
        {
            return RemoveRunLocked(runId);
        }
    }

    /// <summary>Whether the given node holds the run under a live lease at the given attempt: the in-memory half of
    /// the fence every per-run node call (a context request, a trace batch) presents. A lease already taken for
    /// expiry does not count, so a node presumed dead is refused from the moment its run is being dispositioned.</summary>
    public bool IsRunHeldBy(Guid runId, string node, int attempt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            return _runs.TryGetValue(runId, out var entry)
                && entry.State == EntryState.Leased
                && string.Equals(entry.Node, node, StringComparison.Ordinal)
                && entry.Attempt == attempt;
        }
    }

    /// <summary>Removes a run only if the given node holds it at the given attempt: the outcome write for it was
    /// dropped by the ledger's fence, so memory's view of the holder was stale and reconcile will re-derive it.</summary>
    public bool RemoveRunIfHeldBy(Guid runId, string node, int attempt)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry) || entry.State == EntryState.Queued
                || !string.Equals(entry.Node, node, StringComparison.Ordinal) || entry.Attempt != attempt)
            {
                return false;
            }

            return RemoveRunLocked(runId);
        }
    }

    /// <summary>Extends the leases of the runs a node reports holding and returns the ones it no longer holds (the
    /// lease lapsed and the run was requeued, possibly to another node), which the node must abort. A held run
    /// memory does not know at all is left alone: the node itself reported its outcome while this poll was in
    /// flight (so the run left memory), and there is nothing for it to abort.</summary>
    public IReadOnlyList<Guid> RenewRunLeases(string node, IReadOnlyList<HeldRun> holdings, DateTime leaseExpiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(holdings);
        if (holdings.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            List<Guid>? revoked = null;
            foreach (var held in holdings)
            {
                if (!_runs.TryGetValue(held.RunId, out var entry))
                {
                    continue;
                }

                if (entry.State == EntryState.Leased
                    && string.Equals(entry.Node, node, StringComparison.Ordinal) && entry.Attempt == held.Attempt)
                {
                    entry.LeaseExpiresUtc = leaseExpiresUtc;
                }
                else
                {
                    (revoked ??= []).Add(held.RunId);
                }
            }

            return revoked ?? [];
        }
    }

    /// <summary>The runs a node holds that an operator has asked to cancel.</summary>
    public IReadOnlyList<Guid> CancelRequestedRunsHeldBy(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            List<Guid>? ids = null;
            foreach (var entry in _runs.Values)
            {
                if (entry.CancelRequested && entry.State is EntryState.Leased or EntryState.Expiring
                    && string.Equals(entry.Node, node, StringComparison.Ordinal))
                {
                    (ids ??= []).Add(entry.RunId);
                }
            }

            return ids ?? [];
        }
    }

    /// <summary>Applies an operator cancel: a queued run leaves the queue outright; a reserved, leased or expiring
    /// run is flagged so its node hears the request on its next poll (which is woken at once).</summary>
    public CancelMark MarkRunCancel(Guid runId)
    {
        lock (_gate)
        {
            return MarkRunCancelLocked(runId);
        }
    }

    /// <summary>Applies an operator cancel to every member of a run group.</summary>
    public int MarkGroupCancel(Guid groupId)
    {
        lock (_gate)
        {
            var members = _runs.Values.Where(r => r.GroupId == groupId).Select(r => r.RunId).ToList();
            var affected = 0;
            foreach (var runId in members)
            {
                if (MarkRunCancelLocked(runId) != CancelMark.Unknown)
                {
                    affected++;
                }
            }

            return affected;
        }
    }

    /// <summary>Marks a leased run as cancel-requested without waking anything (reconcile found the flag on the
    /// ledger row). Returns true when the flag was newly set.</summary>
    public bool FlagRunCancelRequested(Guid runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry) || entry.CancelRequested)
            {
                return false;
            }

            entry.CancelRequested = true;
            if (entry.Node is { } node)
            {
                WakeNodeLocked(node);
            }

            return true;
        }
    }

    /// <summary>The leases that have lapsed as of <paramref name="nowUtc"/>, each moved to <c>Expiring</c> so a late
    /// renewal is refused (the node is told the run is revoked) while the caller journals the disposition. Entries
    /// already expiring (a previous disposition attempt failed) are returned again.</summary>
    public IReadOnlyList<ExpiredLease> TakeExpiredRunLeases(DateTime nowUtc)
    {
        lock (_gate)
        {
            List<ExpiredLease>? expired = null;
            foreach (var entry in _runs.Values)
            {
                if (entry.State == EntryState.Expiring
                    || (entry.State == EntryState.Leased && entry.LeaseExpiresUtc is { } expires && expires <= nowUtc))
                {
                    entry.State = EntryState.Expiring;
                    (expired ??= []).Add(new ExpiredLease(entry.RunId, entry.Node!, entry.Attempt, entry.CancelRequested));
                }
            }

            return expired ?? [];
        }
    }

    /// <summary>Returns an expiring run to the queue after the ledger requeued it: the consumed attempt stays, the
    /// holder is cleared. Returns false unless the run is expiring.</summary>
    public bool RequeueExpiredRun(Guid runId)
    {
        lock (_gate)
        {
            if (!_runs.TryGetValue(runId, out var entry) || entry.State != EntryState.Expiring)
            {
                return false;
            }

            RequeueLocked(entry);
            return true;
        }
    }

    /// <summary>Diffs memory's runs against what the ledger holds, returning what memory lacks and what it holds that
    /// the ledger no longer does. Reserved entries are in flight and never reported. The caller applies additions at
    /// once and removals only when they persist across two passes, because the ledger read and memory move
    /// independently.</summary>
    public RunDiff DiffRuns(IReadOnlyList<Guid> ledgerQueued, IReadOnlyList<ActiveRunRef> ledgerRunning)
    {
        ArgumentNullException.ThrowIfNull(ledgerQueued);
        ArgumentNullException.ThrowIfNull(ledgerRunning);
        lock (_gate)
        {
            var queuedSet = new HashSet<Guid>(ledgerQueued);
            var runningById = new Dictionary<Guid, ActiveRunRef>(ledgerRunning.Count);
            foreach (var running in ledgerRunning)
            {
                runningById[running.RunId] = running;
            }

            var unknownQueued = new List<Guid>();
            foreach (var id in ledgerQueued)
            {
                if (!_runs.ContainsKey(id))
                {
                    unknownQueued.Add(id);
                }
            }

            var unknownRunning = new List<ActiveRunRef>();
            var cancelFlags = new List<Guid>();
            foreach (var running in ledgerRunning)
            {
                if (!_runs.TryGetValue(running.RunId, out var entry))
                {
                    unknownRunning.Add(running);
                }
                else if (running.CancelRequested && !entry.CancelRequested)
                {
                    cancelFlags.Add(running.RunId);
                }
            }

            var stale = new List<Guid>();
            foreach (var entry in _runs.Values)
            {
                var consistent = entry.State switch
                {
                    EntryState.Queued => queuedSet.Contains(entry.RunId),
                    EntryState.Reserved => true,
                    _ => runningById.TryGetValue(entry.RunId, out var running)
                        && running.Attempt == entry.Attempt
                        && string.Equals(running.Node, entry.Node, StringComparison.Ordinal),
                };
                if (!consistent)
                {
                    stale.Add(entry.RunId);
                }
            }

            return new RunDiff(unknownQueued, unknownRunning, cancelFlags, stale);
        }
    }

    // ----------------------------------------------------------------------------------------------- tasks -------

    /// <summary>Adds a queued compute task. Returns false when already known.</summary>
    public bool AddQueuedTask(DispatchTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
        {
            if (_tasks.ContainsKey(task.TaskId))
            {
                return false;
            }

            var entry = TaskEntry.From(task);
            _tasks[task.TaskId] = entry;
            QueuedSet(_queuedTasksByPool, entry.PoolKey).Add(entry);
            WakeWorkLocked(entry.PoolKey, 1);
            return true;
        }
    }

    /// <summary>Adds a task the ledger records as executing on a node, under a grace lease. Returns false when known.</summary>
    public bool AddLeasedTask(DispatchTask task, string node, DateTime nowUtc, DateTime leaseExpiresUtc)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            if (_tasks.ContainsKey(task.TaskId))
            {
                return false;
            }

            var entry = TaskEntry.From(task);
            entry.State = EntryState.Leased;
            entry.Node = node;
            entry.LeasedUtc = nowUtc;
            entry.LeaseExpiresUtc = leaseExpiresUtc;
            _tasks[task.TaskId] = entry;
            return true;
        }
    }

    /// <summary>Reserves up to <paramref name="maxCount"/> queued tasks for a node, oldest first across its pools.</summary>
    public IReadOnlyList<Guid> ReserveTasks(string node, IReadOnlyList<string> poolKeys, int maxCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(poolKeys);
        if (maxCount <= 0)
        {
            return [];
        }

        lock (_gate)
        {
            var picks = new List<TaskEntry>();
            foreach (var entry in OrderedQueued(_queuedTasksByPool, poolKeys, TaskOrder.Instance))
            {
                if (picks.Count >= maxCount)
                {
                    break;
                }

                entry.State = EntryState.Reserved;
                entry.Node = node;
                picks.Add(entry);
            }

            var ids = new List<Guid>(picks.Count);
            foreach (var entry in picks)
            {
                QueuedSet(_queuedTasksByPool, entry.PoolKey).Remove(entry);
                ids.Add(entry.TaskId);
            }

            return ids;
        }
    }

    /// <summary>Turns a task reservation into a lease after the ledger recorded the hand-out.</summary>
    public bool ConfirmTaskLease(Guid taskId, string node, DateTime nowUtc, DateTime leaseExpiresUtc)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.State != EntryState.Reserved
                || !string.Equals(entry.Node, node, StringComparison.Ordinal))
            {
                return false;
            }

            entry.State = EntryState.Leased;
            entry.LeasedUtc = nowUtc;
            entry.LeaseExpiresUtc = leaseExpiresUtc;
            return true;
        }
    }

    /// <summary>Returns a reserved task to the queue. A no-op unless reserved.</summary>
    public void ReleaseTaskReservation(Guid taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.State != EntryState.Reserved)
            {
                return;
            }

            RequeueTaskLocked(entry);
        }
    }

    /// <summary>Removes a task in any state. Returns false when unknown.</summary>
    public bool RemoveTask(Guid taskId)
    {
        lock (_gate)
        {
            return RemoveTaskLocked(taskId);
        }
    }

    /// <summary>Removes a task only if the given node holds it (its outcome write was dropped by the fence).</summary>
    public bool RemoveTaskIfHeldBy(Guid taskId, string node)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.State == EntryState.Queued
                || !string.Equals(entry.Node, node, StringComparison.Ordinal))
            {
                return false;
            }

            return RemoveTaskLocked(taskId);
        }
    }

    /// <summary>Extends the leases of the tasks a node reports holding and returns the ones it no longer holds. A
    /// held task memory does not know at all is left alone, as for runs.</summary>
    public IReadOnlyList<Guid> RenewTaskLeases(string node, IReadOnlyList<Guid> holdings, DateTime leaseExpiresUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(holdings);
        if (holdings.Count == 0)
        {
            return [];
        }

        lock (_gate)
        {
            List<Guid>? revoked = null;
            foreach (var taskId in holdings)
            {
                if (!_tasks.TryGetValue(taskId, out var entry))
                {
                    continue;
                }

                if (entry.State == EntryState.Leased && string.Equals(entry.Node, node, StringComparison.Ordinal))
                {
                    entry.LeaseExpiresUtc = leaseExpiresUtc;
                }
                else
                {
                    (revoked ??= []).Add(taskId);
                }
            }

            return revoked ?? [];
        }
    }

    /// <summary>The tasks a node holds that an operator has asked to cancel.</summary>
    public IReadOnlyList<Guid> CancelRequestedTasksHeldBy(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            List<Guid>? ids = null;
            foreach (var entry in _tasks.Values)
            {
                if (entry.CancelRequested && entry.State is EntryState.Leased or EntryState.Expiring
                    && string.Equals(entry.Node, node, StringComparison.Ordinal))
                {
                    (ids ??= []).Add(entry.TaskId);
                }
            }

            return ids ?? [];
        }
    }

    /// <summary>Applies an operator cancel to a task, exactly as <see cref="MarkRunCancel"/> does for a run.</summary>
    public CancelMark MarkTaskCancel(Guid taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry))
            {
                return CancelMark.Unknown;
            }

            if (entry.State == EntryState.Queued)
            {
                RemoveTaskLocked(taskId);
                return CancelMark.RemovedQueued;
            }

            entry.CancelRequested = true;
            if (entry.State != EntryState.Reserved && entry.Node is { } node)
            {
                WakeNodeLocked(node);
            }

            return CancelMark.Flagged;
        }
    }

    /// <summary>Marks a held task cancel-requested (reconcile found the flag on the ledger row).</summary>
    public bool FlagTaskCancelRequested(Guid taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.CancelRequested)
            {
                return false;
            }

            entry.CancelRequested = true;
            if (entry.Node is { } node)
            {
                WakeNodeLocked(node);
            }

            return true;
        }
    }

    /// <summary>The task leases that have lapsed, each moved to <c>Expiring</c> while the caller journals the requeue.</summary>
    public IReadOnlyList<ExpiredTaskLease> TakeExpiredTaskLeases(DateTime nowUtc)
    {
        lock (_gate)
        {
            List<ExpiredTaskLease>? expired = null;
            foreach (var entry in _tasks.Values)
            {
                if (entry.State == EntryState.Expiring
                    || (entry.State == EntryState.Leased && entry.LeaseExpiresUtc is { } expires && expires <= nowUtc))
                {
                    entry.State = EntryState.Expiring;
                    (expired ??= []).Add(new ExpiredTaskLease(entry.TaskId, entry.Node!, entry.CancelRequested));
                }
            }

            return expired ?? [];
        }
    }

    /// <summary>Returns an expiring task to the queue after the ledger requeued it.</summary>
    public bool RequeueExpiredTask(Guid taskId)
    {
        lock (_gate)
        {
            if (!_tasks.TryGetValue(taskId, out var entry) || entry.State != EntryState.Expiring)
            {
                return false;
            }

            RequeueTaskLocked(entry);
            return true;
        }
    }

    /// <summary>Diffs memory's tasks against the ledger, like <see cref="DiffRuns"/>.</summary>
    public TaskDiff DiffTasks(IReadOnlyList<Guid> ledgerQueued, IReadOnlyList<ActiveTaskRef> ledgerRunning)
    {
        ArgumentNullException.ThrowIfNull(ledgerQueued);
        ArgumentNullException.ThrowIfNull(ledgerRunning);
        lock (_gate)
        {
            var queuedSet = new HashSet<Guid>(ledgerQueued);
            var runningById = new Dictionary<Guid, ActiveTaskRef>(ledgerRunning.Count);
            foreach (var running in ledgerRunning)
            {
                runningById[running.TaskId] = running;
            }

            var unknownQueued = ledgerQueued.Where(id => !_tasks.ContainsKey(id)).ToList();
            var unknownRunning = new List<ActiveTaskRef>();
            var cancelFlags = new List<Guid>();
            foreach (var running in ledgerRunning)
            {
                if (!_tasks.TryGetValue(running.TaskId, out var entry))
                {
                    unknownRunning.Add(running);
                }
                else if (running.CancelRequested && !entry.CancelRequested)
                {
                    cancelFlags.Add(running.TaskId);
                }
            }

            var stale = new List<Guid>();
            foreach (var entry in _tasks.Values)
            {
                var consistent = entry.State switch
                {
                    EntryState.Queued => queuedSet.Contains(entry.TaskId),
                    EntryState.Reserved => true,
                    _ => runningById.TryGetValue(entry.TaskId, out var running)
                        && string.Equals(running.Node, entry.Node, StringComparison.Ordinal),
                };
                if (!consistent)
                {
                    stale.Add(entry.TaskId);
                }
            }

            return new TaskDiff(unknownQueued, unknownRunning, cancelFlags, stale);
        }
    }

    // --------------------------------------------------------------------------------------------- waiters -------

    /// <summary>Parks a node's poll until work it could take appears (when <paramref name="wantsWork"/>), or a signal
    /// for the node arrives (a cancel, a revoked lease, a restart request), or <paramref name="timeout"/> elapses.
    /// Returns true when woken by a signal, false on timeout; a cancelled <paramref name="ct"/> propagates.</summary>
    public async Task<bool> WaitAsync(
        string node, IReadOnlyList<string> poolKeys, bool wantsWork, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(poolKeys);
        if (timeout <= TimeSpan.Zero)
        {
            return false;
        }

        var waiter = new Waiter(node, poolKeys, wantsWork);
        lock (_gate)
        {
            _waiters.Add(waiter);
        }

        try
        {
            await waiter.Signal.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        finally
        {
            lock (_gate)
            {
                _waiters.Remove(waiter);
            }
        }
    }

    /// <summary>Wakes every waiter (the dispatcher is deactivating, so each poll must re-check and answer).</summary>
    public void WakeAll()
    {
        lock (_gate)
        {
            foreach (var waiter in _waiters)
            {
                waiter.Signal.TrySetResult(true);
            }

            _waiters.Clear();
        }
    }

    /// <summary>Wakes a node's parked poll (a restart request arrived for it).</summary>
    public void WakeNode(string node)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            WakeNodeLocked(node);
        }
    }

    // -------------------------------------------------------------------------------------------- snapshot -------

    /// <summary>Drops every entry and wakes every waiter.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _runs.Clear();
            _queuedRunsByPool.Clear();
            _busyPipelines.Clear();
            _groups.Clear();
            _tasks.Clear();
            _queuedTasksByPool.Clear();
            foreach (var waiter in _waiters)
            {
                waiter.Signal.TrySetResult(true);
            }

            _waiters.Clear();
        }
    }

    /// <summary>How many polls are parked right now.</summary>
    public int WaiterCount
    {
        get
        {
            lock (_gate)
            {
                return _waiters.Count;
            }
        }
    }

    /// <summary>The whole queue with every gate and lease explained, plus per-pool counts. The caller supplies node
    /// capacity per pool so a run whose pool no online node serves is reported as such.</summary>
    public QueueSnapshot Snapshot(Func<string, (int OnlineNodes, int FreeRunSlots)> capacityOf)
    {
        ArgumentNullException.ThrowIfNull(capacityOf);
        lock (_gate)
        {
            var queuedRuns = new List<QueuedRunView>();
            var leasedRuns = new List<LeasedRunView>();
            var pools = new Dictionary<string, PoolCounts>(StringComparer.Ordinal);
            foreach (var entry in _runs.Values.OrderBy(r => r, RunOrder.Instance))
            {
                var counts = pools.TryGetValue(entry.PoolKey, out var c) ? c : pools[entry.PoolKey] = new PoolCounts();
                if (entry.State == EntryState.Queued)
                {
                    counts.QueuedRuns++;
                    queuedRuns.Add(new QueuedRunView(
                        entry.RunId, entry.PipelineId, entry.PoolKey, entry.GroupId, entry.GroupWave,
                        entry.GroupMaxConcurrency, entry.EnqueuedUtc, entry.Attempt, entry.CancelRequested,
                        BlockReasonLocked(entry, capacityOf)));
                }
                else
                {
                    counts.LeasedRuns++;
                    leasedRuns.Add(new LeasedRunView(
                        entry.RunId, entry.PipelineId, entry.PoolKey, entry.GroupId, entry.Node, entry.Attempt,
                        entry.LeasedUtc, entry.LeaseExpiresUtc, entry.CancelRequested, StateName(entry.State)));
                }
            }

            var queuedTasks = new List<QueuedTaskView>();
            var leasedTasks = new List<LeasedTaskView>();
            foreach (var entry in _tasks.Values.OrderBy(t => t, TaskOrder.Instance))
            {
                var counts = pools.TryGetValue(entry.PoolKey, out var c) ? c : pools[entry.PoolKey] = new PoolCounts();
                if (entry.State == EntryState.Queued)
                {
                    counts.QueuedTasks++;
                    var blocked = capacityOf(entry.PoolKey).OnlineNodes == 0
                        ? DispatchBlockReasons.NoEligibleNode
                        : DispatchBlockReasons.Eligible;
                    queuedTasks.Add(new QueuedTaskView(entry.TaskId, entry.PoolKey, entry.EnqueuedUtc, blocked));
                }
                else
                {
                    counts.LeasedTasks++;
                    leasedTasks.Add(new LeasedTaskView(
                        entry.TaskId, entry.PoolKey, entry.Node, entry.LeasedUtc, entry.LeaseExpiresUtc,
                        entry.CancelRequested, StateName(entry.State)));
                }
            }

            // The default pool is always listed, so an empty fleet still shows where untargeted work would go.
            pools.TryAdd(string.Empty, new PoolCounts());
            var poolViews = pools
                .OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p =>
                {
                    var (nodes, slots) = capacityOf(p.Key);
                    return new DispatchPoolView(
                        p.Key, p.Value.QueuedRuns, p.Value.LeasedRuns, p.Value.QueuedTasks, p.Value.LeasedTasks, nodes, slots);
                })
                .ToList();

            return new QueueSnapshot(poolViews, queuedRuns, leasedRuns, queuedTasks, leasedTasks, _waiters.Count);
        }
    }

    /// <summary>How many runs are queued and held per pool key, the terms an autoscaler's replica target is built
    /// from. Queued runs blocked by a gate still count: they are demand that will need a node.</summary>
    public (int Queued, int Leased) RunCounts(string poolKey)
    {
        ArgumentNullException.ThrowIfNull(poolKey);
        lock (_gate)
        {
            var queued = 0;
            var leased = 0;
            foreach (var entry in _runs.Values)
            {
                if (!string.Equals(entry.PoolKey, poolKey, StringComparison.Ordinal))
                {
                    continue;
                }

                if (entry.State == EntryState.Queued)
                {
                    queued++;
                }
                else
                {
                    leased++;
                }
            }

            return (queued, leased);
        }
    }

    /// <summary>How many queued runs of a pool a node could take right now: those no gate holds back (the pipeline
    /// is idle, every lower wave of the group is terminal, the group is under its cap). This is the demand term of
    /// an autoscaler's replica target, and it deliberately ignores whether any node is online: the absence of nodes
    /// is exactly what the target exists to correct, while a run a gate blocks would occupy no node even if one
    /// appeared.</summary>
    public int CountEligibleQueuedRuns(string poolKey)
    {
        ArgumentNullException.ThrowIfNull(poolKey);
        lock (_gate)
        {
            var eligible = 0;
            foreach (var entry in _runs.Values)
            {
                if (entry.State == EntryState.Queued
                    && string.Equals(entry.PoolKey, poolKey, StringComparison.Ordinal)
                    && IsEligibleLocked(entry))
                {
                    eligible++;
                }
            }

            return eligible;
        }
    }

    // ------------------------------------------------------------------------------------------- internals -------

    private bool IsEligibleLocked(RunEntry entry)
    {
        if (_busyPipelines.ContainsKey(entry.PipelineId))
        {
            return false;
        }

        if (Group(entry) is { } group)
        {
            if (group.LowestPendingWave != entry.GroupWave)
            {
                return false;
            }

            if (group.MaxConcurrency is { } cap && group.Running >= cap)
            {
                return false;
            }
        }

        return true;
    }

    private string BlockReasonLocked(RunEntry entry, Func<string, (int OnlineNodes, int FreeRunSlots)> capacityOf)
    {
        if (_busyPipelines.ContainsKey(entry.PipelineId))
        {
            return DispatchBlockReasons.PipelineBusy;
        }

        if (Group(entry) is { } group)
        {
            if (group.LowestPendingWave != entry.GroupWave)
            {
                return DispatchBlockReasons.WaveGated;
            }

            if (group.MaxConcurrency is { } cap && group.Running >= cap)
            {
                return DispatchBlockReasons.GroupCap;
            }
        }

        return capacityOf(entry.PoolKey).OnlineNodes == 0 ? DispatchBlockReasons.NoEligibleNode : DispatchBlockReasons.Eligible;
    }

    private GroupState? Group(RunEntry entry)
    {
        if (entry.GroupId is not { } groupId)
        {
            return null;
        }

        if (!_groups.TryGetValue(groupId, out var group))
        {
            group = new GroupState(entry.GroupMaxConcurrency);
            _groups[groupId] = group;
        }

        return group;
    }

    private void OccupyLocked(RunEntry entry)
    {
        _busyPipelines[entry.PipelineId] = _busyPipelines.GetValueOrDefault(entry.PipelineId) + 1;
        if (Group(entry) is { } group)
        {
            group.Running++;
        }
    }

    private void VacateLocked(RunEntry entry)
    {
        if (_busyPipelines.TryGetValue(entry.PipelineId, out var count))
        {
            if (count <= 1)
            {
                _busyPipelines.Remove(entry.PipelineId);
            }
            else
            {
                _busyPipelines[entry.PipelineId] = count - 1;
            }
        }

        if (Group(entry) is { } group)
        {
            group.Running--;
        }
    }

    private void RequeueLocked(RunEntry entry)
    {
        VacateLocked(entry);
        entry.State = EntryState.Queued;
        entry.Node = null;
        entry.LeasedUtc = null;
        entry.LeaseExpiresUtc = null;
        QueuedSet(_queuedRunsByPool, entry.PoolKey).Add(entry);
        WakeWorkLocked(entry.PoolKey, 1);
    }

    private bool RemoveRunLocked(Guid runId)
    {
        if (!_runs.Remove(runId, out var entry))
        {
            return false;
        }

        var wasOccupying = entry.State != EntryState.Queued;
        if (wasOccupying)
        {
            VacateLocked(entry);
        }
        else
        {
            QueuedSet(_queuedRunsByPool, entry.PoolKey).Remove(entry);
        }

        if (entry.GroupId is { } groupId && _groups.TryGetValue(groupId, out var group))
        {
            group.RemovePending(entry.GroupWave);
            if (group.IsEmpty)
            {
                _groups.Remove(groupId);
            }
        }

        if (wasOccupying)
        {
            // A freed pipeline or an advanced wave can make runs in any pool eligible: wake everyone wanting work.
            WakeWorkLocked(string.Empty, int.MaxValue);
        }

        return true;
    }

    private CancelMark MarkRunCancelLocked(Guid runId)
    {
        if (!_runs.TryGetValue(runId, out var entry))
        {
            return CancelMark.Unknown;
        }

        if (entry.State == EntryState.Queued)
        {
            RemoveRunLocked(runId);
            return CancelMark.RemovedQueued;
        }

        entry.CancelRequested = true;
        if (entry.State != EntryState.Reserved && entry.Node is { } node)
        {
            WakeNodeLocked(node);
        }

        return CancelMark.Flagged;
    }

    private void RequeueTaskLocked(TaskEntry entry)
    {
        entry.State = EntryState.Queued;
        entry.Node = null;
        entry.LeasedUtc = null;
        entry.LeaseExpiresUtc = null;
        QueuedSet(_queuedTasksByPool, entry.PoolKey).Add(entry);
        WakeWorkLocked(entry.PoolKey, 1);
    }

    private bool RemoveTaskLocked(Guid taskId)
    {
        if (!_tasks.Remove(taskId, out var entry))
        {
            return false;
        }

        if (entry.State == EntryState.Queued)
        {
            QueuedSet(_queuedTasksByPool, entry.PoolKey).Remove(entry);
        }

        return true;
    }

    /// <summary>Wakes parked polls that want work and serve <paramref name="poolKey"/> (the empty key reaches every
    /// waiter, since an untargeted run is eligible for any node), oldest waiter first, at most
    /// <paramref name="max"/> of them.</summary>
    private void WakeWorkLocked(string poolKey, int max)
    {
        if (_waiters.Count == 0 || max <= 0)
        {
            return;
        }

        var woken = 0;
        for (var i = 0; i < _waiters.Count && woken < max;)
        {
            var waiter = _waiters[i];
            if (waiter.WantsWork && (poolKey.Length == 0 || waiter.PoolKeys.Contains(poolKey, StringComparer.Ordinal)))
            {
                waiter.Signal.TrySetResult(true);
                _waiters.RemoveAt(i);
                woken++;
            }
            else
            {
                i++;
            }
        }
    }

    private void WakeNodeLocked(string node)
    {
        for (var i = 0; i < _waiters.Count;)
        {
            if (string.Equals(_waiters[i].Node, node, StringComparison.Ordinal))
            {
                _waiters[i].Signal.TrySetResult(true);
                _waiters.RemoveAt(i);
            }
            else
            {
                i++;
            }
        }
    }

    private static SortedSet<RunEntry> QueuedSet(Dictionary<string, SortedSet<RunEntry>> sets, string poolKey)
    {
        if (!sets.TryGetValue(poolKey, out var set))
        {
            set = new SortedSet<RunEntry>(RunOrder.Instance);
            sets[poolKey] = set;
        }

        return set;
    }

    private static SortedSet<TaskEntry> QueuedSet(Dictionary<string, SortedSet<TaskEntry>> sets, string poolKey)
    {
        if (!sets.TryGetValue(poolKey, out var set))
        {
            set = new SortedSet<TaskEntry>(TaskOrder.Instance);
            sets[poolKey] = set;
        }

        return set;
    }

    /// <summary>Walks the queued entries of the untargeted pool and every pool in <paramref name="poolKeys"/> as one
    /// sequence in enqueue order (a k-way merge over the per-pool ordered sets). Read-only: the caller mutates the
    /// sets only after the enumeration ends.</summary>
    private static IEnumerable<T> OrderedQueued<T>(
        Dictionary<string, SortedSet<T>> sets, IReadOnlyList<string> poolKeys, IComparer<T> order)
        where T : class
    {
        var heads = new List<IEnumerator<T>>();
        void Open(string key)
        {
            if (sets.TryGetValue(key, out var set) && set.Count > 0)
            {
                var enumerator = set.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    heads.Add(enumerator);
                }
            }
        }

        Open(string.Empty);
        foreach (var key in poolKeys)
        {
            if (key.Length > 0)
            {
                Open(key);
            }
        }

        while (heads.Count > 0)
        {
            var best = 0;
            for (var i = 1; i < heads.Count; i++)
            {
                if (order.Compare(heads[i].Current, heads[best].Current) < 0)
                {
                    best = i;
                }
            }

            yield return heads[best].Current;
            if (!heads[best].MoveNext())
            {
                heads.RemoveAt(best);
            }
        }
    }

    private static string StateName(EntryState state) => state switch
    {
        EntryState.Reserved => "reserved",
        EntryState.Expiring => "expiring",
        EntryState.Leased => "leased",
        _ => "queued",
    };

    private sealed class PoolCounts
    {
        public int QueuedRuns { get; set; }

        public int LeasedRuns { get; set; }

        public int QueuedTasks { get; set; }

        public int LeasedTasks { get; set; }
    }

    private sealed class Waiter(string node, IReadOnlyList<string> poolKeys, bool wantsWork)
    {
        public string Node { get; } = node;

        public IReadOnlyList<string> PoolKeys { get; } = poolKeys;

        public bool WantsWork { get; } = wantsWork;

        public TaskCompletionSource<bool> Signal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>The per-pool queue counts and every entry, as <see cref="DispatchState.Snapshot"/> reports them.</summary>
public sealed record QueueSnapshot(
    IReadOnlyList<DispatchPoolView> Pools,
    IReadOnlyList<QueuedRunView> QueuedRuns,
    IReadOnlyList<LeasedRunView> LeasedRuns,
    IReadOnlyList<QueuedTaskView> QueuedTasks,
    IReadOnlyList<LeasedTaskView> LeasedTasks,
    int Waiters);

/// <summary>A run picked for a node whose hand-out is being journaled: the id and the attempt the ledger row must
/// still carry for the write to apply.</summary>
public readonly record struct RunReservation(Guid RunId, int ExpectedAttempt);

/// <summary>A run whose lease lapsed: who held it, at which attempt, and whether a cancel was already pending, which
/// decides the disposition.</summary>
public readonly record struct ExpiredLease(Guid RunId, string Node, int Attempt, bool CancelRequested);

/// <summary>A compute task whose lease lapsed.</summary>
public readonly record struct ExpiredTaskLease(Guid TaskId, string Node, bool CancelRequested);

/// <summary>What an operator cancel did in memory.</summary>
public enum CancelMark
{
    /// <summary>The run or task is not in memory (already terminal, or not yet known).</summary>
    Unknown,

    /// <summary>It was queued and has been removed; no node ever saw it.</summary>
    RemovedQueued,

    /// <summary>It is held by a node, which will hear the request on its next poll.</summary>
    Flagged,
}

/// <summary>What reconcile found when diffing runs: ledger-queued runs memory lacks, ledger-running runs memory
/// lacks, held runs whose ledger row carries a cancel request memory has not seen, and memory entries the ledger no
/// longer agrees with.</summary>
public sealed record RunDiff(
    IReadOnlyList<Guid> UnknownQueued, IReadOnlyList<ActiveRunRef> UnknownRunning,
    IReadOnlyList<Guid> CancelRequested, IReadOnlyList<Guid> Stale);

/// <summary>What reconcile found when diffing tasks.</summary>
public sealed record TaskDiff(
    IReadOnlyList<Guid> UnknownQueued, IReadOnlyList<ActiveTaskRef> UnknownRunning,
    IReadOnlyList<Guid> CancelRequested, IReadOnlyList<Guid> Stale);

internal enum EntryState
{
    Queued,
    Reserved,
    Leased,
    Expiring,
}

internal sealed class RunEntry
{
    public required Guid RunId { get; init; }

    public required Guid PipelineId { get; init; }

    public required string PoolKey { get; init; }

    public Guid? GroupId { get; init; }

    public int GroupWave { get; init; }

    public int? GroupMaxConcurrency { get; init; }

    public required DateTime EnqueuedUtc { get; init; }

    public int Attempt { get; set; }

    public bool CancelRequested { get; set; }

    public EntryState State { get; set; }

    public string? Node { get; set; }

    public DateTime? LeasedUtc { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public static RunEntry From(DispatchRun run) => new()
    {
        RunId = run.RunId,
        PipelineId = run.PipelineId,
        PoolKey = DispatchState.PoolKey(run.TargetPool),
        GroupId = run.GroupId,
        GroupWave = run.GroupWave < 0 ? 0 : run.GroupWave,
        GroupMaxConcurrency = run.GroupMaxConcurrency is { } cap && cap >= 1 ? cap : null,
        EnqueuedUtc = run.EnqueuedUtc,
        Attempt = run.Attempt,
        CancelRequested = run.CancelRequested,
    };
}

internal sealed class TaskEntry
{
    public required Guid TaskId { get; init; }

    public required string PoolKey { get; init; }

    public required DateTime EnqueuedUtc { get; init; }

    public bool CancelRequested { get; set; }

    public EntryState State { get; set; }

    public string? Node { get; set; }

    public DateTime? LeasedUtc { get; set; }

    public DateTime? LeaseExpiresUtc { get; set; }

    public static TaskEntry From(DispatchTask task) => new()
    {
        TaskId = task.TaskId,
        PoolKey = DispatchState.PoolKey(task.TargetPool),
        EnqueuedUtc = task.EnqueuedUtc,
        CancelRequested = task.CancelRequested,
    };
}

/// <summary>A run group's live gate state: how many non-terminal members each wave still has (the lowest such wave
/// is the only one eligible), how many members are executing, and the cap on that.</summary>
internal sealed class GroupState(int? maxConcurrency)
{
    private readonly SortedDictionary<int, int> _pending = [];

    public int? MaxConcurrency { get; } = maxConcurrency;

    public int Running { get; set; }

    public int? LowestPendingWave
    {
        get
        {
            foreach (var wave in _pending.Keys)
            {
                return wave;
            }

            return null;
        }
    }

    public bool IsEmpty => _pending.Count == 0 && Running == 0;

    public void AddPending(int wave) => _pending[wave] = _pending.GetValueOrDefault(wave) + 1;

    public void RemovePending(int wave)
    {
        if (!_pending.TryGetValue(wave, out var count))
        {
            return;
        }

        if (count <= 1)
        {
            _pending.Remove(wave);
        }
        else
        {
            _pending[wave] = count - 1;
        }
    }
}

internal sealed class RunOrder : IComparer<RunEntry>
{
    public static readonly RunOrder Instance = new();

    public int Compare(RunEntry? x, RunEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byTime = x.EnqueuedUtc.CompareTo(y.EnqueuedUtc);
        return byTime != 0 ? byTime : x.RunId.CompareTo(y.RunId);
    }
}

internal sealed class TaskOrder : IComparer<TaskEntry>
{
    public static readonly TaskOrder Instance = new();

    public int Compare(TaskEntry? x, TaskEntry? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var byTime = x.EnqueuedUtc.CompareTo(y.EnqueuedUtc);
        return byTime != 0 ? byTime : x.TaskId.CompareTo(y.TaskId);
    }
}
