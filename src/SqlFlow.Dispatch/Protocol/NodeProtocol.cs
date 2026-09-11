using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;

namespace SqlFlow.Dispatch.Protocol;

/// <summary>The wire contract between a compute node and the control plane's dispatcher. Every call originates
/// from the node (it needs no inbound connectivity): one poll that is at once the heartbeat, the lease renewal,
/// the cancel channel and the hand-out; one outcome report per finished run or task; and, for a run in flight, the
/// flow-version fetch, the lineage-context resolution and the trace batches that let a node execute with nothing
/// but a control-plane URL and a token. The same records travel in-process when the control plane hosts its own
/// node, so there is exactly one protocol.</summary>
public static class NodeProtocol
{
    /// <summary>The route prefix under <c>/api/v1</c> every node call lives beneath.</summary>
    public const string RoutePrefix = "/api/v1/node";

    /// <summary>The scope a bearer credential must carry to speak this protocol.</summary>
    public const string Scope = "node";

    /// <summary>The request header a node stamps its name on, so the control plane's rate limiter can give each
    /// node its own window even when a whole fleet presents one shared node token.</summary>
    public const string NodeHeader = "X-SqlFlow-Node";

    /// <summary>The largest run artifact (run.json) a node may report; larger ones are recorded as unreadable so a
    /// runaway trace can never stall the control plane. The same bound the artifact sync applies.</summary>
    public const long MaxArtifactBytes = 64L * 1024 * 1024;

    /// <summary>The largest trace batch a node may post. The node's feed flushes well below it (see
    /// <see cref="MaxTraceBatchItems"/> and <see cref="MaxTraceBatchChars"/>), so only a single pathological
    /// statement can ever reach it, and that one is refused rather than buffered.</summary>
    public const long MaxTraceBatchBytes = 16L * 1024 * 1024;

    /// <summary>How many statements plus events a node packs into one trace batch before flushing early.</summary>
    public const int MaxTraceBatchItems = 200;

    /// <summary>How many characters of SQL and message text a node packs into one trace batch before flushing
    /// early, so a run generating very large statements posts more, smaller batches instead of one huge one.</summary>
    public const int MaxTraceBatchChars = 2 * 1024 * 1024;

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

/// <summary>Everything a node needs to execute a handed-out run, resolved by the control plane from the catalog
/// at hand-out time so the node never opens a catalog connection: the flow's identity and where its file lives
/// (the repo's remote and the node-local root path, the pipeline's repo-relative path), the exact version to run
/// (a commit pin, and the content hash of the snapshotted YAML the node fetches through the protocol), the run's
/// substitution parameters, and the reference (never the value) of the git credential a pinned run may need.
/// Fields that describe rows which may have left the catalog since the enqueue are nullable, and the node reports
/// the precise reason when one is missing.</summary>
public sealed record RunSpec(
    Guid? RepoId,
    Guid PipelineId,
    string FlowName,
    string? RepoName,
    string? RepoRemoteUrl,
    string? RepoRootPath,
    string? PipelineRelativePath,
    string? CommitSha,
    string? FlowVersionHash,
    RunParameters Parameters,
    string? CredentialReference,
    string? CredentialUsername);

/// <summary>A run handed to the node: the id, the attempt this hand-out consumed (the fencing token the node
/// presents on every later call for the run), and the execution spec.</summary>
public sealed record RunHandout(Guid RunId, int Attempt, RunSpec Spec);

/// <summary>Everything a node needs to execute a handed-out compute task: the operation, the connection reference
/// to resolve on the node (never a secret), and the validated payload as compact JSON.</summary>
public sealed record TaskSpec(string Operation, string SourceRef, string ArgumentsJson);

/// <summary>A compute task handed to the node.</summary>
public sealed record TaskHandout(Guid TaskId, TaskSpec Spec);

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

/// <summary>A snapshotted flow version served to a node: the content hash it asked for and the exact YAML text the
/// enqueue staged under it. Content-addressed, so a node caches it forever.</summary>
public sealed record FlowVersionResponse(string ContentHash, string Yaml);

/// <summary>A node's request for the lineage facts a run's execution depends on, made once it has parsed the flow
/// document and knows the flow participates: the downstream-anchored watermark table for an incremental flow
/// (<see cref="ResolveWatermark"/>) and the landing-reset verdict for a chained landing flow
/// (<see cref="ResolveLandingReset"/>). <see cref="TargetSchema"/> and <see cref="TargetTable"/> name the flow's
/// own target, which both resolutions are relative to. <see cref="Node"/> and <see cref="Attempt"/> are the fence:
/// the dispatcher answers only while the run still carries exactly that lease.</summary>
public sealed record RunContextRequest(
    string Node, int Attempt, string TargetSchema, string TargetTable, bool ResolveWatermark, bool ResolveLandingReset);

/// <summary>The dispatcher's answer to a context request. <see cref="Held"/> is false when the run no longer
/// carries the caller's lease, in which case the node aborts the execution and reports nothing. Otherwise
/// <see cref="WatermarkSourceTable"/> is the one unambiguous downstream table (null leaves the probe on the flow's own
/// target) and <see cref="LandingReset"/> the verdict (null when the flow has no consumers in the lineage graph, or
/// the node did not ask).</summary>
public sealed record RunContextResponse(bool Held, RelationalObject? WatermarkSourceTable, LandingReset? LandingReset)
{
    /// <summary>The answer for a caller whose lease the dispatcher no longer honors.</summary>
    public static RunContextResponse NotHeld { get; } = new(false, null, null);
}

/// <summary>One generated SQL statement in a trace batch, in the run's execution order.</summary>
public sealed record TraceStatement(int Ordinal, DateTime TimestampUtc, string Step, string Sql, string? Error);

/// <summary>The error a statement already streamed raised when it executed, stamped onto its row.</summary>
public sealed record TraceStatementFailure(int Ordinal, string Error);

/// <summary>One canonical run event in a trace batch, in publication order.</summary>
public sealed record TraceEvent(
    int Ordinal, DateTime TimestampUtc, string Level, string? Step, string Message, long? Rows, double? ElapsedMs);

/// <summary>A batch of the trace a node streams while a run executes: the statements generated and the events
/// published since the previous batch, plus the failures stamped onto statements that were streamed earlier. Order
/// inside each list is the run's own order. <see cref="Node"/> and <see cref="Attempt"/> are the fence: rows are
/// written only while the run still carries exactly that lease, so a node presumed dead can never pollute the
/// trace of the execution that replaced it.</summary>
public sealed record RunTraceBatch(
    string Node,
    int Attempt,
    IReadOnlyList<TraceStatement> Statements,
    IReadOnlyList<TraceStatementFailure> StatementFailures,
    IReadOnlyList<TraceEvent> Events)
{
    /// <summary>Whether the batch carries anything at all.</summary>
    public bool IsEmpty => Statements.Count == 0 && StatementFailures.Count == 0 && Events.Count == 0;
}

/// <summary>The dispatcher's answer to a trace batch: whether the rows were written. False means the run no longer
/// carries the caller's lease, and the node's feed stops for that run.</summary>
public sealed record RunTraceResponse(bool Accepted);

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
