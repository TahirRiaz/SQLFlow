namespace SqlFlow.Core.Model;

public enum FlowEventLevel
{
    Trace,

    /// <summary>The engine's decisions (resolved windows, column mappings, apply-mode choices), bridged from the
    /// canonical run log's Debug level. More detail than Info, but not the per-statement Trace chatter.</summary>
    Debug,

    Info,
    Warning,
    Error,
}

/// <summary>
/// A key event emitted by the engine as work happens, so a calling client (CLI/GUI) can show live
/// progress. Distinct from the post-run <see cref="TraceEntry"/> summary - these stream in real time.
/// </summary>
public sealed record FlowEvent
{
    /// <summary>Identifies the run this event belongs to, so concurrent runs stay separable.</summary>
    public Guid RunId { get; init; }

    /// <summary>Stable identity of the flow definition (same across runs); pairs with <see cref="RunId"/>.</summary>
    public Guid FlowId { get; init; }

    public string? FlowName { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public FlowEventLevel Level { get; init; } = FlowEventLevel.Info;
    public required string Message { get; init; }
    public string? Stage { get; init; }
    public long? Rows { get; init; }
    public double? ElapsedMs { get; init; }
}
