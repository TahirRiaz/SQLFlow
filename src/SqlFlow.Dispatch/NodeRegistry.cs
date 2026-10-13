using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch;

/// <summary>
/// The fleet as the dispatcher last heard from it, kept in memory and flushed to the ledger on a cadence. A poll
/// from any of hundreds of nodes updates only this map; the ledger row behind the fleet view is written once per
/// flush interval per node that polled, so a large fleet costs the catalog a few dozen updates a minute instead of
/// one per heartbeat. Restart requests travel the other way: an operator stamps the ledger row, the flush reads it
/// back, and the node hears it on its next poll.
/// <para>A node can also be drained: it gets no new work, and the first poll that shows it holding nothing tells it to
/// exit, through the same flag a restart uses, so a node finishes every run it was handed before it leaves. Draining
/// lives only in memory and only the managed fleet sets it; with nothing drained, a poll answers exactly as it would
/// without this.</para>
/// </summary>
public sealed class NodeRegistry
{
    /// <summary>How recently a node must have polled to count as online, the same window the fleet page applies to
    /// the ledger's last-seen. A node polls at least every long-poll interval, so several polls fit inside it.</summary>
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(60);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, NodeState> _nodes = new(StringComparer.Ordinal);

    /// <summary>Records a poll: the node's capacity, holdings and pools, and when it was heard. A first poll from a
    /// name registers it.</summary>
    public void Touch(NodePollRequest request, IReadOnlyList<string> poolKeys, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(poolKeys);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(request.Node, out var state))
            {
                state = new NodeState(request.Node, nowUtc);
                _nodes[request.Node] = state;
            }
            else if (state.StartedUtc != request.StartedUtc)
            {
                // A new incarnation under the same name: what was asked of the previous one (a drain, an exit) and
                // how long it sat idle do not carry over.
                state.DrainRequestedUtc = null;
                state.ExitSignalledUtc = null;
                state.LastExitSignalUtc = null;
                state.IdleSinceUtc = null;
            }

            state.Version = request.Version;
            state.PoolKeys = poolKeys;
            state.RunSlots = request.RunSlots;
            state.FreeRunSlots = request.FreeRunSlots;
            state.TaskSlots = request.TaskSlots;
            state.FreeTaskSlots = request.FreeTaskSlots;
            state.BusyRuns = request.HoldingRuns.Count;
            state.BusyTasks = request.HoldingTasks.Count;
            state.StartedUtc = request.StartedUtc;
            state.LastSeenUtc = nowUtc;
            if (state.BusyRuns > 0 || state.BusyTasks > 0)
            {
                state.IdleSinceUtc = null;
            }
            else
            {
                state.IdleSinceUtc ??= nowUtc;
            }

            state.Dirty = true;
        }
    }

    /// <summary>Records that a poll just handed the node <paramref name="runs"/> runs and <paramref name="tasks"/> tasks,
    /// so until its next poll it is not idle and has that many fewer free slots. What it holds (the busy counts the
    /// fleet view is flushed with) still comes from its own polls only.</summary>
    public void MarkHandedOut(string node, int runs, int tasks)
    {
        lock (_gate)
        {
            if (_nodes.TryGetValue(node, out var state))
            {
                state.IdleSinceUtc = null;
                state.FreeRunSlots = Math.Max(0, state.FreeRunSlots - runs);
                state.FreeTaskSlots = Math.Max(0, state.FreeTaskSlots - tasks);
            }
        }
    }

    /// <summary>Drains a node: from now on it gets no new work, and once a poll shows it holding nothing, the answer
    /// tells it to exit. Returns false when the registry has not heard from the node (there is nothing to drain yet) and
    /// true otherwise, including when it was already draining (the first request's time is kept).</summary>
    public bool Drain(string node, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        lock (_gate)
        {
            if (!_nodes.TryGetValue(node, out var state))
            {
                return false;
            }

            state.DrainRequestedUtc ??= nowUtc;
            return true;
        }
    }

    /// <summary>Whether the node is draining (it gets no new work).</summary>
    public bool IsDraining(string node)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(node, out var state) && state.DrainRequestedUtc is not null;
        }
    }

    /// <summary>Whether a poll answer should tell the node to exit now: it is draining and the poll shows it holding
    /// nothing (counting anything handed out on this same poll as held). The first signal is recorded. A node that keeps
    /// polling after being told is told again only once <paramref name="resignalAfter"/> has passed since the last
    /// time, so a node that does not honour the flag parks like any idle node instead of spinning on immediate
    /// answers; the managed fleet stops such a node through its launcher.</summary>
    public bool TryTakeExitSignal(string node, bool holdsNothing, DateTime nowUtc, TimeSpan resignalAfter)
    {
        if (!holdsNothing)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_nodes.TryGetValue(node, out var state) || state.DrainRequestedUtc is null)
            {
                return false;
            }

            if (state.LastExitSignalUtc is { } last && nowUtc - last < resignalAfter)
            {
                return false;
            }

            state.ExitSignalledUtc ??= nowUtc;
            state.LastExitSignalUtc = nowUtc;
            return true;
        }
    }

    /// <summary>Whether an operator's restart request is pending for the node and newer than its current
    /// incarnation, so a stale request left on the row never bounces the replacement.</summary>
    public bool IsRestartPending(string node, DateTime startedUtc)
    {
        lock (_gate)
        {
            return _nodes.TryGetValue(node, out var state)
                && state.RestartRequestedUtc is { } requested && requested > startedUtc;
        }
    }

    /// <summary>Stores what the ledger reported as the node's pending restart request (null clears it). Returns true
    /// when a request newer than the node's start is now pending, so the caller can wake the node's poll.</summary>
    public bool SetRestartRequested(string node, DateTime? requestedUtc)
    {
        lock (_gate)
        {
            if (!_nodes.TryGetValue(node, out var state))
            {
                return false;
            }

            state.RestartRequestedUtc = requestedUtc;
            return requestedUtc is { } requested && requested > state.StartedUtc;
        }
    }

    /// <summary>The heartbeats to flush: every node that polled since the last flush, each stamped with its last poll
    /// time. Clears the dirty marks, so a node that stops polling is flushed once more and then no longer.</summary>
    public IReadOnlyList<NodeHeartbeat> TakeDirty()
    {
        lock (_gate)
        {
            var beats = new List<NodeHeartbeat>();
            foreach (var state in _nodes.Values)
            {
                if (!state.Dirty)
                {
                    continue;
                }

                state.Dirty = false;
                beats.Add(new NodeHeartbeat(
                    state.Name, state.Version, state.PoolKeys.Count > 0 ? state.PoolKeys[0] : string.Empty,
                    state.BusyRuns, state.RunSlots, state.LastSeenUtc));
            }

            return beats;
        }
    }

    /// <summary>Forgets nodes not heard from since <paramref name="olderThanUtc"/>. Returns the names removed.</summary>
    public IReadOnlyList<string> Expire(DateTime olderThanUtc)
    {
        lock (_gate)
        {
            var removed = new List<string>();
            foreach (var (name, state) in _nodes)
            {
                if (state.LastSeenUtc < olderThanUtc)
                {
                    removed.Add(name);
                }
            }

            foreach (var name in removed)
            {
                _nodes.Remove(name);
            }

            return removed;
        }
    }

    /// <summary>How many online nodes serve a pool, and how many free run slots they reported between them. An
    /// untargeted run (the empty pool key) can go to any online node, so every node serves that key. A draining node
    /// takes no new work, so it serves no pool.</summary>
    public (int OnlineNodes, int FreeRunSlots) Capacity(string poolKey, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(poolKey);
        var onlineSince = nowUtc - OnlineWindow;
        lock (_gate)
        {
            var nodes = 0;
            var slots = 0;
            foreach (var state in _nodes.Values)
            {
                if (state.LastSeenUtc < onlineSince || !state.Serves(poolKey) || state.DrainRequestedUtc is not null)
                {
                    continue;
                }

                nodes++;
                slots += state.FreeRunSlots;
            }

            return (nodes, slots);
        }
    }

    /// <summary>Every registered node, most recently seen first.</summary>
    public IReadOnlyList<NodeView> Snapshot(DateTime nowUtc)
    {
        var onlineSince = nowUtc - OnlineWindow;
        lock (_gate)
        {
            return _nodes.Values
                .OrderByDescending(n => n.LastSeenUtc)
                .ThenBy(n => n.Name, StringComparer.Ordinal)
                .Select(n => new NodeView(
                    n.Name, n.Version, n.PoolKeys, n.RunSlots, n.FreeRunSlots, n.TaskSlots, n.FreeTaskSlots,
                    n.FirstSeenUtc, n.LastSeenUtc, n.StartedUtc, n.RestartRequestedUtc, n.LastSeenUtc >= onlineSince,
                    n.BusyRuns, n.BusyTasks, n.IdleSinceUtc, n.DrainRequestedUtc, n.ExitSignalledUtc))
                .ToList();
        }
    }

    /// <summary>Drops every node (the dispatcher deactivated; the successor rebuilds its own view from polls).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _nodes.Clear();
        }
    }

    private sealed class NodeState(string name, DateTime firstSeenUtc)
    {
        public string Name { get; } = name;

        public DateTime FirstSeenUtc { get; } = firstSeenUtc;

        public string? Version { get; set; }

        public IReadOnlyList<string> PoolKeys { get; set; } = [];

        public int RunSlots { get; set; }

        public int FreeRunSlots { get; set; }

        public int TaskSlots { get; set; }

        public int FreeTaskSlots { get; set; }

        public int BusyRuns { get; set; }

        public int BusyTasks { get; set; }

        public DateTime StartedUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public DateTime? RestartRequestedUtc { get; set; }

        /// <summary>When the node last stopped holding anything; null while it holds work.</summary>
        public DateTime? IdleSinceUtc { get; set; }

        /// <summary>When the node was drained; null while it takes work.</summary>
        public DateTime? DrainRequestedUtc { get; set; }

        /// <summary>When a poll answer first told the draining node to exit.</summary>
        public DateTime? ExitSignalledUtc { get; set; }

        /// <summary>When a poll answer last told the draining node to exit.</summary>
        public DateTime? LastExitSignalUtc { get; set; }

        public bool Dirty { get; set; }

        /// <summary>Every node serves the untargeted (empty) pool; a named pool only its members.</summary>
        public bool Serves(string poolKey) => poolKey.Length == 0 || PoolKeys.Contains(poolKey, StringComparer.Ordinal);
    }
}
