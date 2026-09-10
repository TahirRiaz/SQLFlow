namespace SqlFlow.Core.Batch;

/// <summary>What a batch does when a member fails. The choice the user makes per batch: stop everything, or
/// keep going. A member named under <see cref="BatchFlow.IgnoreErrors"/> is exempt from this entirely.</summary>
public enum BatchErrorMode
{
    /// <summary>Stop the batch when a member fails: the current wave finishes (so the barrier stays clean and
    /// nothing in the next layer half-starts), then no further wave runs. The default, and the safe choice.</summary>
    Stop = 0,

    /// <summary>Keep going past a failure: every member that does not depend on the failed one still runs, across
    /// all remaining waves. Members that (transitively) depend on a failed member are skipped, because running a
    /// consumer on a producer's missing or stale output is worse than not running it.</summary>
    Continue = 1,
}

/// <summary>Whether the batch connects to the databases to derive ordering from view/procedure bodies.</summary>
public enum BatchConnectMode
{
    /// <summary>Connect only when a member needs it for correct ordering: a stored-procedure flow, whose real
    /// reads and writes live in the module body, not the document. The default.</summary>
    Auto = 0,

    /// <summary>Always derive ordering from live module definitions (catches view-on-view source chains too).</summary>
    Always = 1,

    /// <summary>Never connect: order from the declared (and observed) tiers only.</summary>
    Never = 2,
}

/// <summary>
/// A batch flow (flowType: batch): an ordered, wave-concurrent run of a set of member flows. Lineage computes
/// which members may run together (a wave) and which must wait; every member in a wave runs concurrently and
/// the whole wave must finish before the next wave starts. The batch owns no connections of its own; each member
/// carries its own. Members are selected by include/exclude globs over the batch document's directory, with an
/// optional inactive list (the deactivate-from-batch flag) that keeps a member declared but skips it this run.
/// Failure handling is explicit: <see cref="OnError"/> stops or continues the whole batch, and
/// <see cref="IgnoreErrors"/> marks individual members whose failure is non-fatal.
/// </summary>
public sealed record BatchFlow
{
    public required int FlowId { get; init; }

    public required string SysAlias { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public string? Description { get; init; }

    /// <summary>Globs (relative to the batch document's directory) selecting member flow files. A member that is
    /// itself a batch document is never included.</summary>
    public required IReadOnlyList<string> Include { get; init; }

    /// <summary>Globs removing files from the included set entirely.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>Globs marking members that stay declared but are skipped this run (reported as inactive). Their
    /// dependents still run: an inactive member is assumed handled outside the batch, not failed.</summary>
    public IReadOnlyList<string> Inactive { get; init; } = [];

    /// <summary>Globs (matched against member flow files) whose failure is non-fatal: the failure is recorded but
    /// never triggers <see cref="BatchErrorMode.Stop"/> and never blocks the member's dependents. The
    /// per-pipeline "this one is allowed to fail" switch.</summary>
    public IReadOnlyList<string> IgnoreErrors { get; init; } = [];

    public BatchErrorMode OnError { get; init; } = BatchErrorMode.Stop;

    /// <summary>The maximum number of members running at once within a wave; 0 means unbounded (the whole wave).</summary>
    public int MaxParallel { get; init; }

    public BatchConnectMode Connect { get; init; } = BatchConnectMode.Auto;
}
