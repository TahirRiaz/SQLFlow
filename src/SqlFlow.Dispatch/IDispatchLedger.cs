using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Dispatch;

/// <summary>
/// The journal behind the dispatcher. Memory is authoritative for every placement decision; the ledger is what
/// makes those decisions durable and visible, written through with plain conditional updates and read only at
/// activation and by reconcile. Every write that changes who holds a run is fenced on the (node, attempt) pair the
/// hand-out recorded, so a write from a superseded holder affects nothing and reports that. It is also the one
/// place a node's execution reaches the catalog through: the spec a hand-out carries, the snapshotted flow
/// version, the lineage facts a run depends on, and the live trace rows, all brokered by the dispatcher so a node
/// needs nothing but the control plane. Implementations must be safe to call concurrently (every call opens its
/// own unit of work) and must never depend on a database feature beyond conditional UPDATE, INSERT and SELECT.
/// </summary>
public interface IDispatchLedger
{
    /// <summary>Every queued and running run and task, for rebuilding memory at activation.</summary>
    Task<DispatchLedgerSnapshot> LoadAsync(CancellationToken ct);

    /// <summary>The ids of everything currently queued or running, for the periodic diff against memory.</summary>
    Task<DispatchActiveIds> ListActiveAsync(CancellationToken ct);

    /// <summary>The full placement rows for the given queued run ids (those reconcile found unknown to memory).
    /// Rows no longer queued are omitted.</summary>
    Task<IReadOnlyList<DispatchRun>> LoadQueuedRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct);

    /// <summary>The placement rows for the given queued task ids; rows no longer queued are omitted.</summary>
    Task<IReadOnlyList<DispatchTask>> LoadQueuedTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct);

    /// <summary>The placement rows and recorded holders for the given running run ids (those reconcile found with
    /// no lease in memory). Rows no longer running, or with no holder recorded, are omitted.</summary>
    Task<IReadOnlyList<RunningRunRecord>> LoadRunningRunsAsync(IReadOnlyCollection<Guid> runIds, CancellationToken ct);

    /// <summary>The placement rows and recorded holders for the given running task ids; rows no longer running, or
    /// with no holder recorded, are omitted.</summary>
    Task<IReadOnlyList<RunningTaskRecord>> LoadRunningTasksAsync(IReadOnlyCollection<Guid> taskIds, CancellationToken ct);

    /// <summary>Records that <paramref name="node"/> now executes the run: <c>queued</c> to <c>running</c>, the
    /// attempt advanced from <paramref name="expectedAttempt"/> to one more, and returns the execution spec the
    /// hand-out carries to the node. The spec is read before the row is written, so a read that fails leaves the
    /// run queued with no attempt consumed. Returns null when the row is gone or was not queued at that attempt
    /// (cancelled or removed meanwhile), in which case the run must not be handed out.</summary>
    Task<RunSpec?> MarkRunHandedOutAsync(Guid runId, int expectedAttempt, string node, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records a run's outcome under the fence: completion from its artifact, a failure with a reason, or an
    /// honored cancel. A dropped write reports <see cref="RunOutcomeStatus.StaleClaim"/>.</summary>
    Task<RunOutcomeRecord> RecordRunOutcomeAsync(
        Guid runId, string node, int attempt, RunOutcomeKind outcome, string? failure, string? artifactJson,
        DateTime nowUtc, CancellationToken ct);

    /// <summary>Puts an interrupted run (its lease expired) back to <c>queued</c> with the claim cleared, keeping the
    /// consumed attempt. Fenced: applies only while the row still carries the expired lease.</summary>
    Task<InterruptedRunRecord> RequeueInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct);

    /// <summary>Fails an interrupted run that has exhausted its attempt budget, skipping its dependents. Fenced.</summary>
    Task<InterruptedRunRecord> FailInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records an interrupted run cancelled because an operator's cancel was already pending when its node
    /// went silent, skipping its dependents. Fenced.</summary>
    Task<InterruptedRunRecord> CancelInterruptedRunAsync(Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records that <paramref name="node"/> now executes the task and returns the spec the hand-out
    /// carries. Returns null when the row was gone or no longer queued.</summary>
    Task<TaskSpec?> MarkTaskHandedOutAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct);

    /// <summary>Records a task's outcome. Fenced on the node; returns false when the write was dropped.</summary>
    Task<bool> RecordTaskOutcomeAsync(
        Guid taskId, string node, TaskOutcomeKind outcome, string? failure, string? resultJson, DateTime nowUtc, CancellationToken ct);

    /// <summary>Puts an interrupted task back to <c>queued</c>. Fenced on the node.</summary>
    Task<bool> RequeueInterruptedTaskAsync(Guid taskId, string node, DateTime nowUtc, CancellationToken ct);

    /// <summary>Fails every task queued since before <paramref name="queuedBefore"/> (no node ever took it) or
    /// executing since before <paramref name="runningBefore"/> (its node is presumed lost), each with a precise
    /// reason, and returns their ids so memory drops them.</summary>
    Task<IReadOnlyList<Guid>> ExpireTasksAsync(DateTime queuedBefore, DateTime runningBefore, DateTime nowUtc, CancellationToken ct);

    /// <summary>The YAML text of the snapshotted flow version with the given content hash, or null when no such
    /// version is staged (the node then falls back to the run's commit pin or its local checkout).</summary>
    Task<string?> LoadFlowVersionAsync(string contentHash, CancellationToken ct);

    /// <summary>Resolves the lineage facts a run's execution depends on, relative to the flow's own target: the one
    /// unambiguous downstream table for a downstream-anchored watermark, and the landing-reset verdict for a chained
    /// landing table, each only when the request asks for it. The run's own parameters (a backfill window refuses a
    /// reset) are read from the journal. Answers <see cref="RunContextResponse.NotHeld"/> when the run does not carry
    /// the requester's lease.</summary>
    Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct);

    /// <summary>Appends a batch of live trace rows (statements, statement failures, events) to the run, fenced on
    /// the (node, attempt) pair the batch presents. Returns false, writing nothing, when the run no longer carries
    /// that lease.</summary>
    Task<bool> AppendRunTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct);

    /// <summary>Records a node's heartbeat and returns the pending restart request stamped on its row, if any.</summary>
    Task<DateTime?> RecordNodeHeartbeatAsync(NodeHeartbeat heartbeat, CancellationToken ct);

    /// <summary>Removes fleet-registry rows not heard from since <paramref name="olderThanUtc"/>.</summary>
    Task<int> PruneNodesAsync(DateTime olderThanUtc, CancellationToken ct);

    /// <summary>Acquires or renews the single dispatch ownership lease for <paramref name="owner"/>: succeeds when the
    /// lease is free, expired, or already held by this owner. Exactly one owner holds it at a time.</summary>
    Task<bool> TryAcquireOwnershipAsync(string owner, DateTime nowUtc, TimeSpan ttl, CancellationToken ct);

    /// <summary>Releases the ownership lease if <paramref name="owner"/> holds it, so a successor need not wait out
    /// the TTL.</summary>
    Task ReleaseOwnershipAsync(string owner, CancellationToken ct);
}
