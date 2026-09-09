using System.Security.Cryptography;
using System.Text;
using SqlFlow.Core.Lineage;

namespace SqlFlow.Lineage.Collection;

/// <summary>One collected lineage fact, before graph assembly: a flow or module relating to an object,
/// with provenance. The collectors produce these; the builder merges them.</summary>
public sealed record LineageFact
{
    public string? Flow { get; init; }

    /// <summary>The module node key whose definition produced this fact (derived tier).</summary>
    public string? ViaModuleKey { get; init; }

    public required LineageRelation Relation { get; init; }

    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required LineageTier Tier { get; init; }

    public LineageNodeKind KindHint { get; init; } = LineageNodeKind.Unknown;

    public Guid? RunId { get; init; }

    public DateTime? ObservedAtUtc { get; init; }

    public string? Step { get; init; }
}

/// <summary>One flow document as collected: its node plus what the other collectors need to attribute and
/// cross-check facts (server identities per side, the document's own write time for staleness).</summary>
public sealed record CollectedFlow
{
    public required LineageFlowNode Node { get; init; }

    /// <summary>The server identity statements run against by default (the target side).</summary>
    public required string TargetServerRef { get; init; }

    /// <summary>The source-side server identity, when the kind has one (ing/exp).</summary>
    public string? SourceServerRef { get; init; }

    /// <summary>The flow's <c>schedule:</c> declaration, or null when it declares none: either an inline cadence or
    /// a membership reference to named schedules. Carried from the document so <see cref="CollectionResult.Schedules"/>
    /// can be resolved once the whole estate is in hand.</summary>
    public SqlFlow.Core.ScheduleSpec? Schedule { get; init; }

    /// <summary>Whether this flow belongs in the lineage graph (see
    /// <see cref="DocumentFlowHeader.ParticipatesInLineage"/>). A maintenance flow that runs on the estate rather
    /// than through it (<c>scm</c>) is collected so it becomes a pipeline row and a schedule member, but the
    /// graph builder leaves it out: no node, no edges, no wave, no batch membership.</summary>
    public bool ParticipatesInLineage { get; init; } = true;

    public required DateTime FileWriteUtc { get; init; }
}

/// <summary>
/// One named schedule the estate declares, together with the flows that joined it. Every schedule is named: a
/// <c>schedules.yaml</c> entry and a <c>name:</c>d inline block use their declared name, and an unnamed inline block
/// takes its declaring flow's name, so one identity rule covers all three and a fire always has a member set.
/// </summary>
public sealed record CollectedSchedule
{
    /// <summary>The schedule's name: its reference target and its identity in the catalog.</summary>
    public required string Name { get; init; }

    /// <summary>The cadence (cron or interval, time zone, enabled, catchup).</summary>
    public required SqlFlow.Core.ScheduleSpec Spec { get; init; }

    /// <summary>The repo-relative path of the file the cadence is written in: a <c>schedules.yaml</c> library file,
    /// or the flow document carrying the inline block. This is what the GUI shows as "where is this defined".</summary>
    public required string OriginFile { get; init; }

    /// <summary>The flow whose inline <c>schedule:</c> block declares this schedule; null when a library file does.
    /// With it the catalog can point at the declaring flow's stored (secret-redacted) YAML instead of keeping a
    /// second copy of the document.</summary>
    public string? OriginFlow { get; init; }

    /// <summary>The library file's text, carried only for a library-declared schedule: a flow document is already
    /// stored (redacted) on its pipeline row, but nothing else in the catalog holds a <c>schedules.yaml</c>.</summary>
    public string? LibraryYaml { get; init; }

    /// <summary>Where the definition came from, for warnings (a library file's relative path, or a flow and file).</summary>
    public string Origin => OriginFlow is null ? OriginFile : $"'{OriginFlow}' ({OriginFile})";

    /// <summary>The flow names that joined this schedule: the flow that declared it inline, plus every flow whose
    /// <c>schedule:</c> references it by name. A fire runs exactly this set, ordered by lineage wave. Empty when a
    /// library entry nothing references.</summary>
    public List<string> Members { get; } = [];
}

/// <summary>
/// One declared data subscriber as collected: the consumer's metadata, the node key its read edges are
/// attributed to, and the queries that produced them. The read facts themselves are already in
/// <see cref="CollectionResult.Facts"/> (carrying <see cref="LineageFact.ViaModuleKey"/> = this node key), so
/// the graph builder needs this record only for the consumer's own node and for the per-query evidence: which
/// objects each individual query touches, which is what lets the catalog answer "why is this report linked to
/// that table" with the query rather than a shrug.
/// </summary>
public sealed record CollectedSubscriber
{
    public required Core.Subscribers.DataSubscriber Subscriber { get; init; }

    /// <summary>The subscriber's node key, before the builder's aliasing pass (a subscriber lives on the
    /// synthetic <see cref="ServerIdentity.Subscriber"/> server, so the pass is a no-op for it, but it goes
    /// through the same resolution as every other identity rather than assuming so).</summary>
    public required string NodeKey { get; init; }

    /// <summary>The subscriber library file that declares it, relative to the scanned folder.</summary>
    public required string File { get; init; }

    public required IReadOnlyList<CollectedSubscriberQuery> Queries { get; init; }
}

/// <summary>One subscriber query as collected: its text and the raw identities parsing it proved it reads.</summary>
public sealed record CollectedSubscriberQuery
{
    public required string Name { get; init; }

    public required string ServerRef { get; init; }

    public required string Sql { get; init; }

    /// <summary>The identities the query reads, as the SQL spelled them; the builder resolves each with the
    /// same completion and synonym follow every other identity gets.</summary>
    public required IReadOnlyList<ModelObjectRef> Objects { get; init; }
}

/// <summary>An object a collector saw created, with its generating DDL and (for a plain table) its columns.
/// The observed tier produces these from the run trace and the declared tier from a hook, so the catalog
/// attaches a script and an offline column dictionary to an object without a live connection. The graph
/// builder folds the highest-tier artifact per object onto the node.</summary>
public sealed record CollectedObjectArtifact
{
    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }

    public required LineageNodeKind Kind { get; init; }

    public string? Script { get; init; }

    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    public required LineageTier Tier { get; init; }
}

/// <summary>One inventoried catalog object (derived tier): the node-kind ground truth.</summary>
public sealed record CatalogObject
{
    public required string ServerRef { get; init; }

    public required string Database { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required LineageNodeKind Kind { get; init; }

    /// <summary>The module body (sys.sql_modules definition) for a view/procedure/function/trigger; null for a
    /// plain table or an encrypted module.</summary>
    public string? Definition { get; init; }

    /// <summary>The object's columns (tables/views/table-valued functions); empty otherwise.</summary>
    public IReadOnlyList<LineageColumn> Columns { get; init; } = [];

    /// <summary>A node-scoped finding (an encrypted module whose definition is unreadable).</summary>
    public string? Warning { get; init; }
}

/// <summary>One identity a data-model observation names: the raw parts as the script wrote them (the builder
/// resolves them to node keys with the same default-database completion and synonym follow the facts get).</summary>
public sealed record ModelObjectRef
{
    public required string ServerRef { get; init; }

    public string? Database { get; init; }

    public string? Schema { get; init; }

    public required string Name { get; init; }
}

/// <summary>One equality-join observation from a script: the two sides and their positionally-paired columns.
/// The data model of a warehouse is interpreted from these (constraints rarely exist physically), so every
/// distinct script exhibiting the same pair raises the relationship's confidence.</summary>
public sealed record CollectedJoin
{
    public required ModelObjectRef Left { get; init; }

    public required IReadOnlyList<string> LeftColumns { get; init; }

    public required ModelObjectRef Right { get; init; }

    public required IReadOnlyList<string> RightColumns { get; init; }

    /// <summary>The comparison operator per column pair, same arity as the column lists.</summary>
    public IReadOnlyList<string> Operators { get; init; } = [];

    /// <summary>How the two sides were joined where this observation was made.</summary>
    public string JoinType { get; init; } = Extraction.JoinTypes.Inner;

    public required Core.Lineage.LineageTier Tier { get; init; }

    /// <summary>The script the observation came from (a module key, a flow name, a trace step): the unit of
    /// occurrence counting, so one script repeating a join predicate counts once.</summary>
    public required string ScriptId { get; init; }
}

/// <summary>One key observation for a table: an explicit PRIMARY KEY clause parsed from DDL, the flow YAML's
/// declared key columns, or the ON clause of the MERGE that loads it.</summary>
public sealed record CollectedKeyHint
{
    public required ModelObjectRef Table { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required Core.Lineage.LineageModelOrigin Origin { get; init; }

    public required Core.Lineage.LineageTier Tier { get; init; }
}

/// <summary>One explicit FOREIGN KEY constraint parsed from DDL in the codebase (not read from a live system
/// catalog): the strongest form of model relationship.</summary>
public sealed record CollectedModelConstraint
{
    /// <summary>The constraint name, when the DDL named it.</summary>
    public string? Name { get; init; }

    public required ModelObjectRef From { get; init; }

    public required IReadOnlyList<string> FromColumns { get; init; }

    public required ModelObjectRef To { get; init; }

    public required IReadOnlyList<string> ToColumns { get; init; }

    public required Core.Lineage.LineageTier Tier { get; init; }
}

/// <summary>One synonym and what it points at (within reach of PARSENAME; a linked-server base keeps its
/// server part as an unresolvable warning).</summary>
public sealed record SynonymLink
{
    public required string ServerRef { get; init; }

    public required string Database { get; init; }

    public required string Schema { get; init; }

    public required string Name { get; init; }

    public string? TargetDatabase { get; init; }

    public string? TargetSchema { get; init; }

    public required string TargetName { get; init; }
}

/// <summary>What one collector hands the builder.</summary>
public sealed class CollectionResult
{
    public List<CollectedFlow> Flows { get; } = [];

    /// <summary>Every named schedule the estate declares, with the flows that joined it, resolved once the whole
    /// repo is in hand (a reference can point at a definition in any file). This is the authority on WHAT a fire
    /// runs: a schedule fires once and runs its member set as one wave-ordered group. Ordered by name.</summary>
    public List<CollectedSchedule> Schedules { get; } = [];

    /// <summary>Every data subscriber the estate declares, ordered by name: the consumption side of the graph.
    /// Empty when no subscriber library file exists.</summary>
    public List<CollectedSubscriber> Subscribers { get; } = [];

    public List<LineageFact> Facts { get; } = [];

    public List<CollectedObjectArtifact> ObjectArtifacts { get; } = [];

    /// <summary>Every server identity any document referenced, with its raw reference and provider kind
    /// (the derived tier connects to the SQL Server ones).</summary>
    public Dictionary<string, (string RawReference, Core.Connections.DataSourceKind Kind)> Servers { get; }
        = new(StringComparer.Ordinal);

    public List<CatalogObject> CatalogObjects { get; } = [];

    /// <summary>The interpreted data model's raw observations: join predicates, key hints, and explicit
    /// constraint clauses, all parsed from the codebase's SQL across the tiers.</summary>
    public List<CollectedJoin> Joins { get; } = [];

    public List<CollectedKeyHint> KeyHints { get; } = [];

    public List<CollectedModelConstraint> ModelConstraints { get; } = [];

    public List<SynonymLink> Synonyms { get; } = [];

    /// <summary>Server identities proven equal at connect time (two references resolving to the same
    /// canonical connection string): alias identity to canonical identity. Offline, distinct references
    /// stay distinct servers by construction; connected, the proof merges them.</summary>
    public Dictionary<string, string> ServerAliases { get; } = new(StringComparer.Ordinal);

    /// <summary>The default database of each server identity's connection, when known: DB_NAME() recorded by
    /// the connected tier, or the reference's Initial Catalog resolved offline. The builder completes a
    /// database-less fact (a two-part reference) against this map, mirroring how the engine itself resolves
    /// such a name at execution time; engines without a database concept never appear here, so their
    /// identities keep the empty segment by design.</summary>
    public Dictionary<string, string> ServerDefaultDatabases { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    /// <summary>Server identities whose DERIVED collection was requested but failed (unreachable, unresolvable
    /// secret): the connected pass produced no module facts for them, so a consumer persisting this result must
    /// treat previously-derived knowledge for these servers as still authoritative rather than wiping it with
    /// the degraded pass. Case-insensitive: server references are spelled by authors.</summary>
    public HashSet<string> DegradedServers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folds another collector's result into this one.</summary>
    public void Merge(CollectionResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Flows.AddRange(other.Flows);
        Subscribers.AddRange(other.Subscribers);
        Facts.AddRange(other.Facts);
        ObjectArtifacts.AddRange(other.ObjectArtifacts);
        CatalogObjects.AddRange(other.CatalogObjects);
        Joins.AddRange(other.Joins);
        KeyHints.AddRange(other.KeyHints);
        ModelConstraints.AddRange(other.ModelConstraints);
        Synonyms.AddRange(other.Synonyms);
        Warnings.AddRange(other.Warnings);
        DegradedServers.UnionWith(other.DegradedServers);
        foreach (var (key, value) in other.Servers)
        {
            Servers.TryAdd(key, value);
        }

        foreach (var (alias, canonical) in other.ServerAliases)
        {
            ServerAliases.TryAdd(alias, canonical);
        }

        foreach (var (serverRef, database) in other.ServerDefaultDatabases)
        {
            ServerDefaultDatabases.TryAdd(serverRef, database);
        }
    }
}

/// <summary>THE node-identity rule: server reference, database, schema, and name, case-folded, joined with
/// '|' (absent parts empty). One rule for collectors, builder, and queries; nothing else may restate it.</summary>
public static class NodeKey
{
    public static string For(string serverRef, string? database, string? schema, string name)
        => string.Join('|',
            serverRef.ToLowerInvariant(),
            database?.ToLowerInvariant() ?? string.Empty,
            schema?.ToLowerInvariant() ?? string.Empty,
            name.ToLowerInvariant());
}

/// <summary>
/// The server side of node identity: the connection REFERENCE a document used, normalized. Equal references
/// are the same server by construction (the canonical contract makes references shared names:
/// ${env:SQLFLOW_CONN_DWH} in two documents is one server). An inline literal is identified by a content
/// hash, never by its text: a connection string can carry credentials and must not reach a report.
/// </summary>
public static class ServerIdentity
{
    /// <summary>The identity of file endpoints (a file flow's source, an export destination).</summary>
    public const string FileSystem = "file";

    /// <summary>The identity data subscribers live on. A report or workbook belongs to no server SQLFlow
    /// connects to, but it still needs a node identity built by the one rule, so it gets a synthetic one; the
    /// segment can never collide with a real reference, which is always a <c>${...}</c>, an <c>@alias</c>, or
    /// an <c>inline:</c> hash.</summary>
    public const string Subscriber = "subscriber";

    public static string From(string connectionReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionReference);

        var trimmed = connectionReference.Trim();

        // Pass-through ONLY for a value that IS a reference in its entirety: one ${...} token, or one
        // @alias token. A hybrid like "${env:HOST};Password=..." is a literal carrying a secret and must
        // hash like any other literal, never echo.
        var isWholeReference =
            (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
             && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1)
            || (trimmed.StartsWith('@') && !trimmed.Any(char.IsWhiteSpace) && !trimmed.Contains(';', StringComparison.Ordinal)
                && !trimmed.Contains('=', StringComparison.Ordinal));
        if (isWholeReference)
        {
            return trimmed;
        }

        // An inline literal: hash-identified so equal strings still match without ever echoing content.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.ToLowerInvariant()));
        return "inline:" + Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
    }
}
