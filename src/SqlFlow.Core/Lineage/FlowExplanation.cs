namespace SqlFlow.Core.Lineage;

/// <summary>One flow this flow waits for (or that waits for it), with the objects that mediate the dependency
/// and the wave the other flow sits in: the direct answer to "why does this run after that?".</summary>
public sealed record ExplainDependency
{
    public required string Flow { get; init; }

    public required int Wave { get; init; }

    /// <summary>The objects whose producer/consumer relationship creates the dependency.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One object this flow touches, with the relation, the tier that vouches for it, and the observing
/// run when it is an observed fact.</summary>
public sealed record ExplainEdge
{
    public required string ObjectName { get; init; }

    public required LineageRelation Relation { get; init; }

    public required LineageTier Tier { get; init; }

    public Guid? ObservedRunId { get; init; }

    public string? Step { get; init; }
}

/// <summary>
/// The traced rationale for one flow's place in the execution plan: its wave, whether it sits in a cycle, the
/// upstream flows it depends on (with the mediating objects and their waves), the downstream flows that depend
/// on it, and the objects it reads and writes with their tier provenance. Built purely from the
/// <see cref="LineageReport"/>, so <c>--explain</c> needs no extra computation.
/// </summary>
public sealed record FlowExplanation
{
    public required string Flow { get; init; }

    public required string Kind { get; init; }

    /// <summary>The flow's concurrency wave (1-based); 0 if the flow was not placed (should not happen for a
    /// known flow).</summary>
    public required int Wave { get; init; }

    public required bool InCycle { get; init; }

    /// <summary>The upstream flows this flow waits for: the reason it is not in an earlier wave.</summary>
    public required IReadOnlyList<ExplainDependency> DependsOn { get; init; }

    /// <summary>The downstream flows that wait for this one.</summary>
    public required IReadOnlyList<ExplainDependency> RequiredBy { get; init; }

    /// <summary>Objects this flow reads or requires.</summary>
    public required IReadOnlyList<ExplainEdge> Reads { get; init; }

    /// <summary>Objects this flow writes, creates, or destroys.</summary>
    public required IReadOnlyList<ExplainEdge> Writes { get; init; }
}
