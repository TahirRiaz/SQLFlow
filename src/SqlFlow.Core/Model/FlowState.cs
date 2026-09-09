namespace SqlFlow.Core.Model;

/// <summary>
/// Per-flow runtime memory. In lightweight mode this is never persisted; in full mode it is the
/// generator input that enables optimal incremental code generation.
/// </summary>
public sealed record FlowState
{
    public required string FlowName { get; init; }
    public string? Watermark { get; init; }
    public DateTimeOffset LastRunUtc { get; init; }
    public string? LastStatus { get; init; }
}
