namespace SqlFlow.Dispatch;

/// <summary>Everything the dispatcher needs to know about one run to place it: exactly the columns the queue's gates
/// read. <see cref="TargetPool"/> null means any node; a group member carries its <see cref="GroupId"/>, its
/// <see cref="GroupWave"/> (lower waves run first) and the group's concurrency cap; <see cref="Attempt"/> is how many
/// times the run has been handed out so far (the fencing token every outcome write presents), and
/// <see cref="CancelRequested"/> whether an operator has already asked for its death.</summary>
public sealed record DispatchRun(
    Guid RunId, Guid PipelineId, string? TargetPool, Guid? GroupId, int GroupWave, int? GroupMaxConcurrency,
    DateTime EnqueuedUtc, int Attempt, bool CancelRequested);

/// <summary>A queued ad-hoc compute task as the dispatcher sees it: the id, its pool routing, and its age. Tasks have
/// no ordering gates beyond the pool, so this is all placement needs.</summary>
public sealed record DispatchTask(Guid TaskId, string? TargetPool, DateTime EnqueuedUtc, bool CancelRequested);

/// <summary>A run the ledger records as executing on a node, loaded at startup or by reconcile so the dispatcher
/// can grant it a grace lease for that node to reattach to.</summary>
public sealed record RunningRunRecord(DispatchRun Run, string Node);

/// <summary>A compute task the ledger records as executing on a node.</summary>
public sealed record RunningTaskRecord(DispatchTask Task, string Node);

/// <summary>The dispatch-relevant rows of the journal: every queued and running run and task. Loaded once at
/// activation to rebuild the in-memory state.</summary>
public sealed record DispatchLedgerSnapshot(
    IReadOnlyList<DispatchRun> QueuedRuns, IReadOnlyList<RunningRunRecord> RunningRuns,
    IReadOnlyList<DispatchTask> QueuedTasks, IReadOnlyList<RunningTaskRecord> RunningTasks);

/// <summary>One executing run as the ledger records it, for reconcile: which node holds it at which attempt, and
/// whether a cancel request is pending on the row (an operator may stamp one directly against the journal).</summary>
public sealed record ActiveRunRef(Guid RunId, string? Node, int Attempt, bool CancelRequested);

/// <summary>One executing compute task as the ledger records it, for reconcile.</summary>
public sealed record ActiveTaskRef(Guid TaskId, string? Node, bool CancelRequested);

/// <summary>The ids of everything the ledger currently holds as queued or running, the cheap read reconcile diffs
/// against memory.</summary>
public sealed record DispatchActiveIds(
    IReadOnlyList<Guid> QueuedRunIds, IReadOnlyList<ActiveRunRef> RunningRuns,
    IReadOnlyList<Guid> QueuedTaskIds, IReadOnlyList<ActiveTaskRef> RunningTasks);

/// <summary>How a run ended, as its node reports it. <see cref="Completed"/> carries the run's artifact (run.json),
/// which decides success or failure; <see cref="Failed"/> is a run that produced no artifact at all (the flow file
/// was missing, the document failed to load, the worker threw); <see cref="Cancelled"/> is an operator cancel the
/// node honored.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RunOutcomeKind>))]
public enum RunOutcomeKind
{
    Completed,
    Failed,
    Cancelled,
}

/// <summary>How a compute task ended, as its node reports it.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<TaskOutcomeKind>))]
public enum TaskOutcomeKind
{
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>What became of an outcome write, so the node can log the difference between a recorded outcome, a run
/// driven <c>failed</c> because its artifact could not be read, and a write dropped by the claim fence.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<RunOutcomeStatus>))]
public enum RunOutcomeStatus
{
    /// <summary>The outcome was recorded.</summary>
    Recorded,

    /// <summary>The artifact was missing or corrupt; the run was driven to <c>failed</c> so it never lingers.</summary>
    ArtifactUnreadable,

    /// <summary>The run no longer carries the caller's lease (it was requeued after the lease expired and possibly
    /// handed to another node), so nothing was written: the current holder is authoritative.</summary>
    StaleClaim,
}

/// <summary>The ledger's answer to an outcome write: whether it applied, and which still-queued group members it
/// skipped as a consequence (a failed or cancelled member strands its dependents), so memory can drop them too.</summary>
public sealed record RunOutcomeRecord(RunOutcomeStatus Status, IReadOnlyList<Guid> SkippedRunIds);

/// <summary>The ledger's answer to a fenced disposition of an interrupted run (requeue, fail, cancel): whether the
/// row still carried the expired lease so the write applied, plus any dependents skipped.</summary>
public sealed record InterruptedRunRecord(bool Applied, IReadOnlyList<Guid> SkippedRunIds);

/// <summary>A node's heartbeat as the registry flushes it to the ledger: the node's name, build, pool, and how many
/// runs it is executing, stamped with when the node was last heard from.</summary>
public sealed record NodeHeartbeat(string Name, string? Version, string? Pool, int BusyRuns, DateTime LastSeenUtc);
