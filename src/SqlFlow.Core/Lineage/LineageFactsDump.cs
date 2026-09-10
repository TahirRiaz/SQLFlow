using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Lineage;

/// <summary>One raw collected fact, before the graph merge: a flow or module relating to an object, with its
/// tier and observation provenance. The dump of these is the debugging view of "what each tier contributed"
/// prior to identity unification and synonym resolution.</summary>
public sealed record LineageRawFact
{
    /// <summary>The flow this fact belongs to; null for a module-derived fact.</summary>
    public string? Flow { get; init; }

    /// <summary>The module node key whose definition produced this fact (derived tier); null for a flow fact.</summary>
    public string? ViaModule { get; init; }

    public required LineageRelation Relation { get; init; }

    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    /// <summary>The canonical node key (server|database|schema|name, case-folded) this fact points at, so a
    /// raw fact can be cross-referenced with the merged report's objects.</summary>
    public required string NodeKey { get; init; }

    public required LineageTier Tier { get; init; }

    public LineageNodeKind Kind { get; init; } = LineageNodeKind.Unknown;

    /// <summary>The run that observed this fact (observed tier only).</summary>
    public Guid? ObservedRunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    /// <summary>The trace step that produced the statement (observed tier only).</summary>
    public string? Step { get; init; }
}

/// <summary>One inventoried catalog object in the dump (derived tier).</summary>
public sealed record LineageDumpCatalogObject
{
    public required string ServerRef { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public required LineageNodeKind Kind { get; init; }
    public string? Warning { get; init; }
}

/// <summary>One synonym link in the dump (derived tier).</summary>
public sealed record LineageDumpSynonym
{
    public required string ServerRef { get; init; }
    public required string Database { get; init; }
    public required string Schema { get; init; }
    public required string Name { get; init; }
    public string? TargetDatabase { get; init; }
    public string? TargetSchema { get; init; }
    public required string TargetName { get; init; }
}

/// <summary>One referenced server in the dump. <see cref="Reference"/> is secret-safe: a whole
/// <c>${...}</c>/<c>@alias</c> reference is shown verbatim, an inline literal (which may carry credentials) is
/// never echoed, only its content-hash identity.</summary>
public sealed record LineageDumpServer
{
    public required string Identity { get; init; }
    public required string Reference { get; init; }
    public required DataSourceKind Kind { get; init; }
}

/// <summary>
/// The raw collected facts of a lineage computation, BEFORE the graph merge: the debugging companion to the
/// canonical <see cref="LineageReport"/>. Written to <c>.sqlflow/lineage/facts.json</c> on demand
/// (<c>--dump-facts</c>) so an operator can see exactly which tier produced which fact, diff what
/// <c>--connect</c> adds over the offline tiers, and trace a surprising dependency back to its source. Like the
/// report, it is deterministically ordered and carries no resting secret (server references are redacted).
/// </summary>
public sealed record LineageFactsDump
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTime GeneratedAtUtc { get; init; }

    public required string FlowDirectory { get; init; }

    public required IReadOnlyList<LineageTier> TiersUsed { get; init; }

    public required IReadOnlyList<LineageRawFact> Facts { get; init; }

    public required IReadOnlyList<LineageDumpCatalogObject> CatalogObjects { get; init; }

    public required IReadOnlyList<LineageDumpSynonym> Synonyms { get; init; }

    public required IReadOnlyList<LineageDumpServer> Servers { get; init; }

    /// <summary>Server identities proven equal at connect time (alias identity to canonical identity).</summary>
    public required IReadOnlyDictionary<string, string> ServerAliases { get; init; }

    public required IReadOnlyList<string> Warnings { get; init; }
}
