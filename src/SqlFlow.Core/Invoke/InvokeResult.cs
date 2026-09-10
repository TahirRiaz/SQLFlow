namespace SqlFlow.Core.Invoke;

/// <summary>The outcome of dispatching one invoke flow: timing, the captured output streams, and success.</summary>
public sealed record InvokeResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required string InvokeAlias { get; init; }

    public required InvokeType InvokeType { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>The captured output of the run (typically a run id and final status), or null when the executor
    /// produces none.</summary>
    public string? StandardOutput { get; init; }

    /// <summary>Any captured error detail, or null when none.</summary>
    public string? StandardError { get; init; }

    /// <summary>The failure message (already redacted of any secret), or null on success.</summary>
    public string? Error { get; init; }
}

/// <summary>The raw result an <see cref="IInvokeExecutor"/> hands back to the dispatcher: the two captured
/// output streams. The executor throws on failure; the dispatcher times the call and builds the
/// <see cref="InvokeResult"/>.</summary>
public sealed record InvokeExecution
{
    public string? StandardOutput { get; init; }

    public string? StandardError { get; init; }
}
