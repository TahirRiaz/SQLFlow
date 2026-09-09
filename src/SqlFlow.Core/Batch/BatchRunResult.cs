namespace SqlFlow.Core.Batch;

/// <summary>The terminal state of one batch member.</summary>
public enum BatchMemberStatus
{
    /// <summary>Ran and succeeded.</summary>
    Succeeded = 0,

    /// <summary>Ran and failed.</summary>
    Failed = 1,

    /// <summary>Ran and failed, but the failure was ignored (the member is in <c>ignoreErrors</c>): it neither
    /// stopped the batch nor blocked its dependents.</summary>
    FailedIgnored = 2,

    /// <summary>Not run because a member it depends on failed, or because the batch stopped before its wave.</summary>
    Skipped = 3,

    /// <summary>Declared in the batch but deactivated this run (the <c>inactive</c> list).</summary>
    Inactive = 4,

    /// <summary>Declared <c>mode: manual</c> by its own document: a batch never runs it; it executes only when
    /// triggered directly (the GUI's run button, a single-flow API trigger, or a direct CLI run).</summary>
    Manual = 5,
}

/// <summary>The outcome of one member within the batch, with the wave it was scheduled into.</summary>
public sealed record BatchMemberResult
{
    public required string FlowName { get; init; }

    public required string FlowKind { get; init; }

    /// <summary>The member flow file, relative to the batch directory.</summary>
    public required string File { get; init; }

    public required int Wave { get; init; }

    public required BatchMemberStatus Status { get; init; }

    public string? Error { get; init; }

    /// <summary>The member run's id (null when it was skipped or inactive, so never ran).</summary>
    public Guid? RunId { get; init; }

    /// <summary>The member's own run-history folder, when it ran.</summary>
    public string? RunDirectory { get; init; }

    public double DurationSeconds { get; init; }
}

/// <summary>One executed wave: the members it scheduled, in order.</summary>
public sealed record BatchWaveResult
{
    public required int Wave { get; init; }

    public required IReadOnlyList<string> Members { get; init; }
}

/// <summary>
/// The outcome of a batch run: the computed waves, every member's terminal state, and whether the batch as a
/// whole succeeded. Serialized to <c>batch.json</c> in the run history. Success means no member failed in a way
/// that mattered: a failure that was ignored (<c>ignoreErrors</c>) does not, by itself, fail the batch.
/// </summary>
public sealed record BatchRunResult
{
    public required Guid RunId { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public required string BatchName { get; init; }

    /// <summary>The error mode the batch ran under, echoed for the artifact reader.</summary>
    public required string OnError { get; init; }

    public required IReadOnlyList<BatchWaveResult> Waves { get; init; }

    public required IReadOnlyList<BatchMemberResult> Members { get; init; }

    /// <summary>Members whose mutual order could not be resolved (a dependency cycle among members); they were
    /// run sequentially in a final fallback wave.</summary>
    public IReadOnlyList<string> Unordered { get; init; } = [];

    public IReadOnlyList<string> Warnings { get; init; } = [];

    public int Succeeded { get; init; }
    public int Failed { get; init; }
    public int Skipped { get; init; }
    public int Inactive { get; init; }

    /// <summary>Members excluded because their own document declares <c>mode: manual</c>.</summary>
    public int Manual { get; init; }

    public double DurationSeconds { get; init; }
}
