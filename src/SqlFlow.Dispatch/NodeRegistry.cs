using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch;

/// <summary>
/// The fleet as the dispatcher last heard from it, kept in memory and flushed to the ledger on a cadence. A poll
/// from any of hundreds of nodes updates only this map; the ledger row behind the fleet view is written once per
/// flush interval per node that polled, so a large fleet costs the catalog a few dozen updates a minute instead of
/// one per heartbeat. Restart requests travel the other way: an operator stamps the ledger row, the flush reads it
/// back, and the node hears it on its next poll.
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
            state.Dirty = true;
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
                    state.BusyRuns, state.LastSeenUtc));
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
    /// untargeted run (the empty pool key) can go to any online node, so every node serves that key.</summary>
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
                if (state.LastSeenUtc < onlineSince || !state.Serves(poolKey))
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
                    n.FirstSeenUtc, n.LastSeenUtc, n.StartedUtc, n.RestartRequestedUtc, n.LastSeenUtc >= onlineSince))
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

        public bool Dirty { get; set; }

        /// <summary>Every node serves the untargeted (empty) pool; a named pool only its members.</summary>
        public bool Serves(string poolKey) => poolKey.Length == 0 || PoolKeys.Contains(poolKey, StringComparer.Ordinal);
    }
}
