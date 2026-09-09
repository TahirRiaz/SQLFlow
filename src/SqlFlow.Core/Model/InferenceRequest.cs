namespace SqlFlow.Core.Model;

/// <summary>
/// A standalone inference job - the dedicated input to the infer process, independent of a pipeline
/// flow definition. Points at a loaded table to profile and carries the inference policy.
/// </summary>
public sealed record InferenceRequest
{
    public required string Connection { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public TypeInferencePolicy Policy { get; init; } = new();

    public string QualifiedName => $"[{Schema}].[{Table}]";
}
