namespace SqlFlow.Dispatch;

/// <summary>Why a queued run is not being handed out right now, as the dispatch read surface reports it. Empty means
/// eligible: it will go to the next node with a free slot that serves its pool.</summary>
public static class DispatchBlockReasons
{
    public const string Eligible = "";

    /// <summary>Another run of the same pipeline is executing; this one waits its turn.</summary>
    public const string PipelineBusy = "pipeline-busy";

    /// <summary>A lower wave of the run's group has not finished.</summary>
    public const string WaveGated = "wave-gated";

    /// <summary>The run's group is executing as many members as its cap allows.</summary>
    public const string GroupCap = "group-cap";

    /// <summary>No online node serves the run's pool.</summary>
    public const string NoEligibleNode = "no-eligible-node";
}

/// <summary>Per-pool counts: the backlog, what is executing, and what capacity is online to take it.</summary>
public sealed record DispatchPoolView(
    string Pool, int QueuedRuns, int LeasedRuns, int QueuedTasks, int LeasedTasks, int OnlineNodes, int FreeRunSlots);

/// <summary>One queued run and the gate holding it back, if any.</summary>
public sealed record QueuedRunView(
    Guid RunId, Guid PipelineId, string Pool, Guid? GroupId, int GroupWave, int? GroupMaxConcurrency,
    DateTime EnqueuedUtc, int Attempt, bool CancelRequested, string Blocked);

/// <summary>One run handed to a node: who holds it, under which attempt, and until when unless renewed.
/// <see cref="State"/> is <c>leased</c>, <c>reserved</c> (a hand-out whose ledger write is in flight), or
/// <c>expiring</c> (the lease lapsed and the disposition write is in flight).</summary>
public sealed record LeasedRunView(
    Guid RunId, Guid PipelineId, string Pool, Guid? GroupId, string? Node, int Attempt, DateTime? LeasedUtc,
    DateTime? LeaseExpiresUtc, bool CancelRequested, string State);

/// <summary>One queued compute task and the gate holding it back, if any.</summary>
public sealed record QueuedTaskView(Guid TaskId, string Pool, DateTime EnqueuedUtc, string Blocked);

/// <summary>One compute task handed to a node.</summary>
public sealed record LeasedTaskView(
    Guid TaskId, string Pool, string? Node, DateTime? LeasedUtc, DateTime? LeaseExpiresUtc, bool CancelRequested, string State);

/// <summary>One node as the registry last heard from it.</summary>
public sealed record NodeView(
    string Name, string? Version, IReadOnlyList<string> Pools, int RunSlots, int FreeRunSlots, int TaskSlots,
    int FreeTaskSlots, DateTime FirstSeenUtc, DateTime LastSeenUtc, DateTime StartedUtc, DateTime? RestartRequestedUtc,
    bool Online);

/// <summary>The dispatcher as it sees itself: ownership, the last housekeeping passes, and the whole queue with
/// every gate and lease explained. This is the diagnostic surface behind <c>GET /api/v1/dispatch</c>.</summary>
public sealed record DispatchSnapshot(
    bool Active,
    string? Owner,
    DateTime? ActivatedUtc,
    DateTime? LastReconcileUtc,
    DateTime? LastTickUtc,
    int Waiters,
    IReadOnlyList<DispatchPoolView> Pools,
    IReadOnlyList<QueuedRunView> QueuedRuns,
    IReadOnlyList<LeasedRunView> LeasedRuns,
    IReadOnlyList<QueuedTaskView> QueuedTasks,
    IReadOnlyList<LeasedTaskView> LeasedTasks,
    IReadOnlyList<NodeView> Nodes);
