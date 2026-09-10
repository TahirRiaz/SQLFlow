namespace SqlFlow.Core.Lineage;

/// <summary>Where a lineage fact came from. The tiers cross-check each other; every edge carries its tier so
/// the graph is honest about what it knows in each connectivity posture.</summary>
public enum LineageTier
{
    /// <summary>Stated by a flow document (the YAML): what the author intends. Always available.</summary>
    Declared = 0,

    /// <summary>Extracted from the canonical run artifacts (run.json SqlTrace): what actually executed,
    /// stamped with the run. Available offline.</summary>
    Observed = 1,

    /// <summary>Expanded from live catalog metadata and module definitions (sys.sql_modules) parsed as
    /// T-SQL: what the database adds. Needs connectivity.</summary>
    Derived = 2,
}

/// <summary>What kind of catalog object a lineage node is, when known.</summary>
public enum LineageNodeKind
{
    Unknown = 0,
    Table = 1,
    View = 2,
    Procedure = 3,
    Function = 4,
    Trigger = 5,
    Synonym = 6,

    /// <summary>A file endpoint (a file flow's source, an export flow's destination).</summary>
    File = 7,

    /// <summary>A data subscriber: a report, workbook, notebook, or application that CONSUMES the warehouse.
    /// It is the far end of the graph, the only node kind that lives outside the databases SQLFlow moves data
    /// between, and it exists so "which dashboard breaks if I change this table" is a graph query rather than
    /// tribal knowledge.</summary>
    Subscriber = 8,
}

/// <summary>How a flow or module relates to an object (the DeltaForge typed-relation taxonomy).</summary>
public enum LineageRelation
{
    Reads = 0,
    Writes = 1,
    Creates = 2,

    /// <summary>Needs the object to exist (EXEC of a procedure, a TVF call, ALTER).</summary>
    Requires = 3,

    Destroys = 4,
}

/// <summary>One pipeline (flow document) participating in the graph.</summary>
public sealed record LineageFlowNode
{
    public required string Name { get; init; }

    /// <summary>The document kind: file/ing/exp/sp/inv/hc.</summary>
    public required string Kind { get; init; }

    /// <summary>The flow document path, relative to the scanned folder.</summary>
    public required string File { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's execution mode (today only a health-check flow can declare <c>mode: manual</c>).
    /// Manual flows are excluded from automatic dispatch (local batch membership, control-plane group
    /// expansion, the scheduler); a direct single-flow trigger runs them regardless.</summary>
    public Runs.ExecutionMode Mode { get; init; } = Runs.ExecutionMode.Auto;

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>): only production pipelines generate
    /// notification events. Execution is unaffected either way.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;
}

/// <summary>
/// One data subscriber participating in the graph: a report, workbook, notebook, or application that consumes
/// the warehouse. A subscriber is not a flow (it never runs and moves nothing) and not a database object, so it
/// carries its own node record; its <see cref="ObjectKey"/> is the identity of the
/// <see cref="LineageNodeKind.Subscriber"/> object node its read edges point from, which is how the consumption
/// side and the production side meet in one graph.
/// </summary>
public sealed record LineageSubscriberNode
{
    public required string Name { get; init; }

    /// <summary>What consumes the data: PowerBI, Tableau, Excel, Notebook, Application, or the estate's own label.</summary>
    public required string Type { get; init; }

    /// <summary>The subscriber's node key: the identity its <c>Reads</c> edges carry as <c>ViaModule</c>.</summary>
    public required string ObjectKey { get; init; }

    /// <summary>The subscriber library file that declares it, relative to the scanned folder.</summary>
    public required string File { get; init; }

    /// <summary>The team or person to contact before a breaking change to a table it reads.</summary>
    public string? Owner { get; init; }

    public string? Description { get; init; }

    /// <summary>Remarks about the subscriber's state: not refreshed since a given month, apparently superseded,
    /// could not be opened, an open question. Distinct from <see cref="Description"/>, which says what the
    /// subscriber is for.</summary>
    public string? Notes { get; init; }

    /// <summary>Where the subscriber lives (report URL, workbook path, repository).</summary>
    public string? Url { get; init; }

    /// <summary>The queries it runs, in declaration order: the evidence behind its read edges.</summary>
    public required IReadOnlyList<LineageSubscriberQuery> Queries { get; init; }
}

/// <summary>One query a subscriber runs, and the objects parsing it proved that query reads.</summary>
public sealed record LineageSubscriberQuery
{
    public required string Name { get; init; }

    /// <summary>The server identity the query runs against (the connection reference behind its alias).</summary>
    public required string ServerRef { get; init; }

    /// <summary>The query text as declared. Kept so the catalog can show WHY a table is linked to a report.</summary>
    public required string Sql { get; init; }

    /// <summary>The node keys this query reads, ordinal-sorted and deduplicated.</summary>
    public required IReadOnlyList<string> ObjectKeys { get; init; }
}

/// <summary>One catalog or file object participating in the graph. The key is the canonical node identity:
/// server reference, database, schema, and name, case-folded; raw spellings are preserved here.</summary>
public sealed record LineageObjectNode
{
    public required string Key { get; init; }

    /// <summary>The server identity: the connection REFERENCE the object was reached through (a
    /// ${env:...} reference, an @alias, or an inline string's redacted identity). Two documents naming the
    /// same reference name the same server; distinct references are distinct servers by construction.</summary>
    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public LineageNodeKind Kind { get; init; } = LineageNodeKind.Unknown;

    /// <summary>The module body (the <c>sys.sql_modules</c> definition) for a view/procedure/function/trigger,
    /// captured by the derived (connected) tier. Null for a plain table, an offline tier, or an encrypted module.</summary>
    public string? Definition { get; init; }

    /// <summary>The generating DDL of the object (the <c>CREATE TABLE</c>/<c>CREATE VIEW</c> we ran), captured
    /// from the observed run trace or a declared hook so the catalog holds the script offline. Null when no
    /// tier saw the object created (a pre-existing source table). Distinct from <see cref="Definition"/>: that
    /// is the live module body read from the database, this is the script the engine emitted.</summary>
    public string? Script { get; init; }

    /// <summary>The tier that supplied <see cref="Script"/>, or null when there is no script.</summary>
    public LineageTier? ScriptTier { get; init; }

    /// <summary>The object's columns: the derived (connected) tier reads them live; offline they are parsed
    /// from the CREATE TABLE the run executed. Empty when no tier saw them.</summary>
    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>The tier that supplied <see cref="Columns"/>, or null when there are none. The catalog uses it
    /// to keep a live (derived) dictionary from being overwritten by an offline (observed) one.</summary>
    public LineageTier? ColumnsTier { get; init; }

    /// <summary>The object's interpreted key columns (its primary/business key), in key order: parsed from an
    /// explicit PRIMARY KEY clause, declared by the loading flow's YAML key columns, or inferred from the
    /// MERGE that loads it. Empty when nothing in the codebase names a key.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>How <see cref="KeyColumns"/> was interpreted, or null when no key is known.</summary>
    public LineageModelOrigin? KeyOrigin { get; init; }

    /// <summary>Node-scoped findings: an encrypted module whose definition is unreadable, an ambiguous
    /// database resolution, a linked-server reference that could not be expanded.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>One column of a catalog object, as the derived (connected) tier read it from the live database.</summary>
public sealed record LineageColumn
{
    /// <summary>1-based column position.</summary>
    public required int Ordinal { get; init; }

    public required string Name { get; init; }

    /// <summary>The SQL Server type rendered with length/precision (for example <c>nvarchar(100)</c>).</summary>
    public string? DataType { get; init; }

    public bool Nullable { get; init; }
}

/// <summary>Where an object's inferred key (or a model relationship) came from. Warehouses rarely declare
/// physical constraints, so the data model is INTERPRETED from the codebase; the origin says how strong the
/// interpretation is.</summary>
public enum LineageModelOrigin
{
    /// <summary>An explicit PRIMARY KEY / FOREIGN KEY clause parsed from DDL in the codebase.</summary>
    Constraint = 0,

    /// <summary>Declared in a flow document (the YAML key columns of the target table).</summary>
    Declared = 1,

    /// <summary>The ON clause of a MERGE loading the table (its upsert match key).</summary>
    Merge = 2,

    /// <summary>An equality join predicate observed in the codebase's SQL (JOIN ... ON / WHERE equi-join).</summary>
    Join = 3,
}

/// <summary>One interpreted data-model relationship between two catalog objects, distinct from the flow and
/// module lineage in <see cref="LineageEdge"/>: how the tables JOIN, not which flow moves data. Both ends are
/// node keys (<see cref="LineageObjectNode.Key"/>); the column lists pair positionally (FromColumns[i] joins
/// ToColumns[i]). A <see cref="LineageModelOrigin.Constraint"/> relationship was parsed from an explicit
/// FOREIGN KEY clause; a <see cref="LineageModelOrigin.Join"/> relationship was inferred from the equality
/// predicates the codebase actually joins on, with <see cref="Occurrences"/> counting how many distinct
/// scripts exhibited it (the canonical join path scores highest).</summary>
public sealed record LineageModelRelationship
{
    /// <summary>The constraint name, when parsed from an explicit FOREIGN KEY clause; null for an inferred join.</summary>
    public string? Name { get; init; }

    /// <summary>The referencing side's node key (for a join without key knowledge, simply the first side seen).</summary>
    public required string FromObjectKey { get; init; }

    /// <summary>The referencing columns, in predicate/constraint order.</summary>
    public required IReadOnlyList<string> FromColumns { get; init; }

    /// <summary>The referenced side's node key.</summary>
    public required string ToObjectKey { get; init; }

    public required IReadOnlyList<string> ToColumns { get; init; }

    /// <summary>The comparison operator per column pair, same arity as the column lists. Empty, or all "=",
    /// is a key match; a range join (an interval containment, typically a temporal dimension lookup) carries
    /// the real operators so a consumer can tell the two apart.</summary>
    public IReadOnlyList<string> Operators { get; init; } = [];

    /// <summary>The distinct join types the codebase uses for this relationship, in a stable order. More than
    /// one means different scripts disagree, which is worth knowing before writing another query: INNER where
    /// the estate writes LEFT silently drops rows.</summary>
    public IReadOnlyList<string> JoinTypes { get; init; } = [];

    public required LineageModelOrigin Origin { get; init; }

    public required LineageTier Tier { get; init; }

    /// <summary>How many distinct scripts exhibited this relationship (1 for an explicit constraint).</summary>
    public required int Occurrences { get; init; }
}

/// <summary>One attributed lineage fact: a flow or module relating to an object.</summary>
public sealed record LineageEdge
{
    /// <summary>The flow this fact belongs to; null for a module-derived fact.</summary>
    public string? Flow { get; init; }

    /// <summary>The module (view/procedure/function key) whose definition produced this fact; null for a
    /// flow-level fact.</summary>
    public string? ViaModule { get; init; }

    public required LineageRelation Relation { get; init; }

    /// <summary>The object the relation points at (a <see cref="LineageObjectNode.Key"/>).</summary>
    public required string ObjectKey { get; init; }

    public required LineageTier Tier { get; init; }

    /// <summary>The run that observed this fact (observed tier only).</summary>
    public Guid? ObservedRunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    /// <summary>The trace step that produced the statement (observed tier only).</summary>
    public string? Step { get; init; }
}

/// <summary>A flow-level dependency: ToFlow must wait for FromFlow, because of the named objects.</summary>
public sealed record LineageFlowDependency
{
    public required string FromFlow { get; init; }

    public required string ToFlow { get; init; }

    /// <summary>The object keys that mediate the dependency.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One concurrency wave: everything in it can run together once the previous wave finished.</summary>
public sealed record LineageWave
{
    public required int Wave { get; init; }

    public required IReadOnlyList<string> Flows { get; init; }
}

/// <summary>The objective of the calculation: what order, what runs concurrently, what must wait.</summary>
public sealed record LineageExecutionPlan
{
    public required IReadOnlyList<LineageWave> Waves { get; init; }

    /// <summary>Flows whose mutual order could not be resolved (dependency-cycle members). They are
    /// scheduled together in the final fallback wave, with the cycle reported: the DeltaForge behavior of
    /// degrading loudly instead of refusing to plan.</summary>
    public required IReadOnlyList<string> Unordered { get; init; }
}

/// <summary>A dependency cycle: ordering is undecidable for these flows until an edge is broken.</summary>
public sealed record LineageCycle
{
    /// <summary>The flows in the cycle, in path order (the last depends on the first).</summary>
    public required IReadOnlyList<string> Flows { get; init; }

    /// <summary>The objects mediating the cycle's edges.</summary>
    public required IReadOnlyList<string> ViaObjects { get; init; }
}

/// <summary>One flow name declared by more than one document. The graph collapses the colliding documents to a
/// single flow node (first wins) and warns, so the report still plans; this record keeps the collision in a
/// structured form so a stricter consumer (batch membership) can refuse it without parsing warning text.</summary>
public sealed record LineageDuplicateFlowName
{
    /// <summary>The shared flow name (the casing of the first declaring document).</summary>
    public required string Name { get; init; }

    /// <summary>Every document that declared the name, as repository-relative paths, ordinal-sorted.</summary>
    public required IReadOnlyList<string> Files { get; init; }
}

/// <summary>
/// The canonical lineage artifact (<c>lineage.json</c>): the aggregated graph of every pipeline and object,
/// with full provenance, plus the execution plan derived from it. Like the run artifacts, the envelope is a
/// stable, versioned contract: the file can be charted, diffed, or bulk-loaded without per-version parsing,
/// and full mode persists the same shape to the control database later.
/// </summary>
public sealed record LineageReport
{
    /// <summary>The current lineage.json schema version written by this build. Version 2 adds the object
    /// <see cref="LineageObjectNode.Script"/> and its offline columns; version 3 adds the interpreted data
    /// model (the per-object <see cref="LineageObjectNode.KeyColumns"/> and the <see cref="Relationships"/>
    /// list); version 4 adds the consumption side (<see cref="Subscribers"/> and their read edges). Each
    /// version is additive: an older reader sees the same shape plus new fields it can ignore.</summary>
    public const int CurrentSchemaVersion = 4;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required DateTime GeneratedAtUtc { get; init; }

    /// <summary>The scanned flow directory.</summary>
    public required string FlowDirectory { get; init; }

    /// <summary>Which tiers fed this graph (the connectivity posture of the computation).</summary>
    public required IReadOnlyList<LineageTier> TiersUsed { get; init; }

    public required IReadOnlyList<LineageFlowNode> Flows { get; init; }

    public required IReadOnlyList<LineageObjectNode> Objects { get; init; }

    public required IReadOnlyList<LineageEdge> Edges { get; init; }

    /// <summary>The consumption side of the estate: every declared data subscriber, sorted by name, with the
    /// queries that link it to the warehouse. Their read edges are in <see cref="Edges"/> like any other fact
    /// (carrying the subscriber's node key as <c>ViaModule</c>), so "what consumes this table" needs no special
    /// query path. Empty when the estate declares no <c>subscribers.yaml</c>; not required in the envelope so a
    /// version-3 document still deserializes.</summary>
    public IReadOnlyList<LineageSubscriberNode> Subscribers { get; init; } = [];

    /// <summary>The interpreted data model: every PK/FK-style relationship the codebase's SQL exhibits
    /// (explicit constraint clauses plus inferred join predicates), both ends resolved to node keys and
    /// aggregated with occurrence counts. Not required in the envelope so a version-2 document (which
    /// predates the field) still deserializes.</summary>
    public IReadOnlyList<LineageModelRelationship> Relationships { get; init; } = [];

    public required IReadOnlyList<LineageFlowDependency> FlowDependencies { get; init; }

    public required LineageExecutionPlan ExecutionPlan { get; init; }

    public required IReadOnlyList<LineageCycle> Cycles { get; init; }

    /// <summary>Flow names declared by more than one document, with every declaring file. The graph keeps only
    /// the first declaration (the other facts merge under it) and also warns; this is the same collision in a
    /// structured form. Empty when every flow name is unique.</summary>
    public required IReadOnlyList<LineageDuplicateFlowName> DuplicateFlowNames { get; init; }

    /// <summary>Graph-scoped findings: multi-writer objects, stale observations, parse warnings, unhandled
    /// statement kinds. Never silently dropped.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Server identities whose DERIVED collection was requested but failed (unreachable server,
    /// unresolvable secret): this computation carries no module knowledge for them, so a consumer persisting
    /// the report must keep previously-derived edges for these servers instead of wiping them with the
    /// degraded pass. Empty when the derived tier was not requested or every server was reached.</summary>
    public IReadOnlyList<string> DegradedDerivedServers { get; init; } = [];
}
