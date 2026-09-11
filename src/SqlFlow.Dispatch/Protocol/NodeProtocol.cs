namespace SqlFlow.Dispatch.Protocol;

/// <summary>The wire contract between a compute node and the control plane's dispatcher. Every call originates
/// from the node (it needs no inbound connectivity): one poll that is at once the heartbeat, the lease renewal,
/// the cancel channel and the hand-out, and one outcome report per finished run or task. The same records travel
/// in-process when the control plane hosts its own node, so there is exactly one protocol.</summary>
public static class NodeProtocol
{
    /// <summary>The route prefix under <c>/api/v1</c> every node call lives beneath.</summary>
    public const string RoutePrefix = "/api/v1/node";

    /// <summary>The scope a bearer credential must carry to speak this protocol.</summary>
    public const string Scope = "node";

    /// <summary>The largest run artifact (run.json) a node may report; larger ones are recorded as unreadable so a
    /// runaway trace can never stall the control plane. The same bound the artifact sync applies.</summary>
    public const long MaxArtifactBytes = 64L * 1024 * 1024;

    /// <summary>The longest a node may ask a poll to wait for work. The server caps a larger request to this, and
    /// the node's own client budgets its request timeout from it. Kept well under every proxy idle timeout in the
    /// estate.</summary>
    public const int MaxWaitSeconds = 60;
}

/// <summary>A run the node is executing, identified by the lease it holds (run id plus the attempt the hand-out
/// carried); reported on every poll so the lease is renewed.</summary>
public sealed record HeldRun(Guid RunId, int Attempt);

/// <summary>The node's poll: who it is, what it serves, how much room it has, and what it holds.
/// <para><see cref="FreeRunSlots"/> and <see cref="FreeTaskSlots"/> bound how much the response may hand out; a
/// saturated node polls with zero of each and the call is then a pure heartbeat and cancel check.
/// <see cref="RunSlots"/> and <see cref="TaskSlots"/> are the node's capacity, so the fleet's total capacity is
/// known without configuration. <see cref="HoldingRuns"/> and <see cref="HoldingTasks"/> renew the node's leases;
/// anything the dispatcher no longer attributes to the node comes back revoked. <see cref="StartedUtc"/> lets a
/// restart request older than this incarnation be ignored. <see cref="WaitSeconds"/> is how long the node is
/// willing to wait for work or a signal before an empty answer.</para></summary>
public sealed record NodePollRequest(
    string Node,
    string? Version,
    IReadOnlyList<string> Pools,
    int RunSlots,
    int FreeRunSlots,
    int TaskSlots,
    int FreeTaskSlots,
    IReadOnlyList<HeldRun> HoldingRuns,
    IReadOnlyList<Guid> HoldingTasks,
    DateTime StartedUtc,
    int WaitSeconds);

/// <summary>A run handed to the node: the id and the attempt this hand-out consumed, which is the fencing token the
/// node presents on the outcome report.</summary>
public sealed record RunHandout(Guid RunId, int Attempt);

/// <summary>A compute task handed to the node.</summary>
public sealed record TaskHandout(Guid TaskId);

/// <summary>What the dispatcher answers a poll with: new work (never more than the free slots reported), the
/// held runs and tasks an operator has asked to cancel, the held ones whose lease has been revoked (the node must
/// abort them at once, another node may already be executing them), whether an operator asked this node to restart,
/// and how long the granted leases last.</summary>
public sealed record NodePollResponse(
    IReadOnlyList<RunHandout> Runs,
    IReadOnlyList<TaskHandout> Tasks,
    IReadOnlyList<Guid> CancelRuns,
    IReadOnlyList<Guid> CancelTasks,
    IReadOnlyList<Guid> RevokedRuns,
    IReadOnlyList<Guid> RevokedTasks,
    bool RestartRequested,
    int LeaseSeconds)
{
    /// <summary>An answer carrying nothing at all.</summary>
    public static NodePollResponse Empty(int leaseSeconds) => new([], [], [], [], [], [], false, leaseSeconds);

    /// <summary>Whether the answer carries anything the node has to act on.</summary>
    public bool HasContent =>
        Runs.Count > 0 || Tasks.Count > 0 || CancelRuns.Count > 0 || CancelTasks.Count > 0
        || RevokedRuns.Count > 0 || RevokedTasks.Count > 0 || RestartRequested;
}

/// <summary>A node's report that a run ended. <see cref="Attempt"/> and <see cref="Node"/> are the fence: the
/// write applies only while the run still carries exactly that lease. <see cref="ArtifactJson"/> is the run.json the
/// engine wrote (required for <see cref="RunOutcomeKind.Completed"/>), from which the control plane projects the
/// result and the trace tail; <see cref="Error"/> is the reason for a <see cref="RunOutcomeKind.Failed"/> report.</summary>
public sealed record RunOutcomeRequest(string Node, int Attempt, RunOutcomeKind Outcome, string? Error, string? ArtifactJson);

/// <summary>The dispatcher's answer to a run outcome report.</summary>
public sealed record RunOutcomeResponse(RunOutcomeStatus Status);

/// <summary>A node's report that a compute task ended: the result document on success, the reason on failure.</summary>
public sealed record TaskOutcomeRequest(string Node, TaskOutcomeKind Outcome, string? Error, string? ResultJson);

/// <summary>The dispatcher's answer to a task outcome report: whether the task still belonged to the node and the
/// write applied.</summary>
public sealed record TaskOutcomeResponse(bool Recorded);
