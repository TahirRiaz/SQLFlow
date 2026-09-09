namespace SqlFlow.Core.Model;

/// <summary>What happened to one index during desired-index application.</summary>
public enum IndexActionKind
{
    /// <summary>The index was created on the target.</summary>
    Created,

    /// <summary>The index could not be created; see <see cref="IndexAction.Detail"/>.</summary>
    Failed,
}

/// <summary>The outcome of applying one desired index, surfaced into the run trace.</summary>
public sealed record IndexAction
{
    public required string IndexName { get; init; }
    public string? Table { get; init; }
    public required IndexActionKind Kind { get; init; }
    public string? Detail { get; init; }

    /// <summary>The executed (or attempted) CREATE INDEX statement, surfaced for the run's SQL trace.</summary>
    public string? Sql { get; init; }
}
