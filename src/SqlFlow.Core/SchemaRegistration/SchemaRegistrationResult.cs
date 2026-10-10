using SqlFlow.Core.Lineage;

namespace SqlFlow.Core.SchemaRegistration;

/// <summary>
/// One table or view a schema registration read from its database: what the catalog records for it, exactly as
/// for an object a flow manages. <see cref="Script"/> is the table's reconstructed <c>CREATE TABLE</c> or the
/// view's stored definition, kept verbatim and never parsed.
/// </summary>
public sealed record RegisteredObject
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    /// <summary><see cref="LineageNodeKind.Table"/> or <see cref="LineageNodeKind.View"/>.</summary>
    public required LineageNodeKind Kind { get; init; }

    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>The primary key's columns in key order; empty for a view or a table without one.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>The generating script: <c>CREATE TABLE</c> for a table, the definition for a view. Null for a view
    /// created <c>WITH ENCRYPTION</c>, whose text the server does not reveal.</summary>
    public string? Script { get; init; }
}

/// <summary>
/// The outcome of one schema registration run: the database it read and every table and view it registered. This
/// is the run product serialized under <c>run.json</c>'s <c>result</c>, which is what the catalog projects into its
/// object registry, so a registration refreshes the catalog on every write-back path (a worker, the CLI, a sync).
/// The objects' server identity is the registering pipeline's source server, which the catalog already holds.
/// </summary>
public sealed record SchemaRegistrationResult
{
    public required Guid RunId { get; init; }

    public required string FlowName { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    /// <summary>The database that was read; null when the run failed before connecting.</summary>
    public string? Database { get; init; }

    public IReadOnlyList<RegisteredObject> Objects { get; init; } = [];

    public int Tables { get; init; }

    public int Views { get; init; }

    public int Columns { get; init; }

    /// <summary>Objects the server would not describe fully (an encrypted view), named rather than dropped.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public required DateTime StartedUtc { get; init; }

    public required DateTime EndedUtc { get; init; }

    public double DurationSeconds => Math.Round((EndedUtc - StartedUtc).TotalSeconds, 3);
}
