using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Files;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A file-ingestion pipeline whose source spec matches a given file: the flow that would ingest it. The
/// match is by the source's file-name glob (as the engine applies it) and, when a full path is given, the source
/// location or path mask. <see cref="PathConfirmed"/> is true when the path (not just the name) was matched, so a
/// caller can rank a definitively-located match above a name-only one.</summary>
public sealed record FilePipelineMatchDto(
    Guid PipelineId, string PipelineName, Guid RepoId, string RepoName,
    string SourceType, string? SourceLocation, string Pattern, bool PathConfirmed);

/// <summary>A lineage object as it appears in lists: the canonical identity and metadata, without the heavy module
/// body (<c>Definition</c>). Keyed by <see cref="Key"/>, the global identity that joins the same physical object
/// across every repo. <see cref="Level"/> is the object's depth in the estate-wide data-movement graph (0 for a
/// source nothing produces); null when the object takes part in no data movement.</summary>
public sealed record ObjectDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind, int? Level,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>A single lineage object with its full module body (<c>Definition</c>) and generating script
/// (<c>Script</c>) for the detail view; the definition is null for plain tables, an unconnected sync, or an
/// encrypted module, and the script is null when no tier saw the object created. <c>KeyColumns</c> is the
/// object's interpreted primary/business key (comma-joined, in key order) with <c>KeyOrigin</c> saying how it
/// was interpreted (Constraint / Declared / Merge); both null when nothing in the codebase names a key.</summary>
public sealed record ObjectDetailDto(
    string Key, string ServerRef, string? Database, string? Schema, string Name, string Kind, int? Level,
    string? Definition, string? Script, string? ScriptTier, DateTime? ScriptUpdatedUtc,
    string? KeyColumns, string? KeyOrigin,
    DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>One interpreted data-model relationship as seen FROM a dossier's object: the other side's identity
/// (key plus proper-cased location), this object's join columns (<c>OwnColumns</c>) positionally paired with
/// the other side's (<c>OtherColumns</c>), and the interpretation strength: <c>Origin</c> is Constraint (an
/// explicit FOREIGN KEY clause in the codebase) or Join (inferred from the equality predicates the code joins
/// on), <c>Occurrences</c> counts the distinct scripts that exhibited it (the canonical join path scores
/// highest), and <c>Tier</c> is the provenance of the strongest observation.</summary>
public sealed record ObjectRelationshipDto(
    string? Name, string Origin, string Tier, int Occurrences,
    string OtherObjectKey, string? OtherDatabase, string? OtherSchema, string OtherName,
    string OwnColumns, string OtherColumns,
    IReadOnlyList<string> Operators, IReadOnlyList<string> JoinTypes, bool IsRangeJoin);

/// <summary>
/// One hop of a join path: the two objects it connects and the columns it connects them on, positionally
/// paired, plus how the relationship was interpreted. <see cref="On"/> renders the equality list ready to
/// paste into an ON clause, using each side's object NAME as the alias.
/// </summary>
public sealed record JoinHopDto(
    string FromObjectKey, string FromName, string ToObjectKey, string ToName,
    IReadOnlyList<string> FromColumns, IReadOnlyList<string> ToColumns,
    string On, string Origin, string Tier, int Occurrences, string? ConstraintName,
    IReadOnlyList<string> JoinTypes, bool IsRangeJoin);

/// <summary>
/// One way to join the origin object to a target: the hops in order, and how well supported the route is.
/// <see cref="MinOccurrences"/> is the weakest link (a chain is only as canonical as its least-used hop), and
/// is what the alternatives are ranked by; a one-hop path through a declared foreign key outranks a two-hop
/// path assembled from rarely-used predicates.
/// </summary>
public sealed record JoinPathDto(
    string TargetObjectKey, string TargetName, string? TargetDatabase, string? TargetSchema,
    int HopCount, int MinOccurrences, bool IsDirect, IReadOnlyList<JoinHopDto> Hops);

/// <summary>
/// Every way the estate knows of to join one object to others. With no target, the alternatives are every
/// object reachable within the hop budget, nearest and best-supported first. With a target, they are the
/// distinct routes to it, INCLUDING several routes to the same target when the codebase joins those two
/// tables on more than one column set, because choosing between them is the caller's decision to make.
/// <see cref="Truncated"/> says a bound cut the search, so an empty or short list is never mistaken for
/// "there is no other way".
/// </summary>
public sealed record JoinPathsDto(
    string ObjectKey, string ObjectName, string? Target, int MaxHops,
    int PathCount, bool Truncated, IReadOnlyList<JoinPathDto> Paths, string Note);

/// <summary>One column of a lineage object; <c>Tier</c> records whether it was read live (Derived) or parsed
/// from the CREATE the run executed (Observed/Declared).</summary>
public sealed record ObjectColumnDto(int Ordinal, string Name, string? DataType, bool Nullable, string Tier);

/// <summary>The script for any lineage node in one consistent shape, whatever its kind: a pipeline's authored
/// YAML, a view's or stored procedure's/function's module body, or a table's generated CREATE TABLE. This is
/// the single "show me the code behind this node" contract the graph uses, so clicking any node - flow or
/// object - resolves through one endpoint. <c>Language</c> is <c>yaml</c> for a pipeline and <c>sql</c> for a
/// database object; <c>Source</c> records where the script came from (Authored for a pipeline, Module for a
/// sys.sql_modules body, or the script tier - Derived/Observed/Declared - for a generated table script).</summary>
public sealed record NodeScriptDto(
    string Key, string Kind, string Language, string? Script, string? Source, string? Name);

/// <summary>One attributed lineage fact: a flow (or a module body) relating to an object. The object's
/// database, schema, and kind are joined from the global object registry (proper-cased, unlike the normalized
/// key) so a graph can label the object with where it lives and classify it (a DB-managed view has no writing
/// pipeline, so its kind cannot be inferred from the edges alone); null for a file or a partially-resolved
/// identity the registry does not know.</summary>
public sealed record EdgeDto(
    long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule,
    string Relation, string ObjectKey, string ObjectName, string? ObjectDatabase, string? ObjectSchema, string Tier,
    string? ObjectKind = null);

/// <summary>Everything known about one object in a single payload: its identity and metadata, its columns, its
/// generating script and module body, and the lineage edges that reference it. This is the "ask about this
/// object" aggregate a model uses to reason about, or author SQL against, the object. Each list is bounded for
/// a stable payload.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
    Justification = "'Object' is the serialized JSON property name the GUI reads; renaming it would break the API contract.")]
public sealed record ObjectDossierDto(
    ObjectDetailDto Object,
    IReadOnlyList<ObjectColumnDto> Columns,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<ObjectRelationshipDto> References,
    IReadOnlyList<ObjectRelationshipDto> ReferencedBy,
    IReadOnlyList<ObjectSubscriberDto> Subscribers);

/// <summary>
/// One step of a lineage traversal: an object reached at <see cref="Depth"/> hops from the origin, and the flow
/// (or module body) that carries the data across the hop. Reading upstream, the via-flow WRITES the previous
/// level's object and READS this one; downstream mirrors it.
/// </summary>
public sealed record LineageStepDto(
    int Depth, string? PipelineId, string? FlowName, string? ViaModule,
    string ObjectKey, string ObjectName, string? Database, string? Schema, string? Kind);

/// <summary>
/// The transitive lineage of one object: everything upstream (where its data comes FROM, walked source-ward
/// through the flows and modules that write each level) and everything downstream (where its data GOES, walked
/// consumer-ward through the flows and modules that read each level), each as depth-annotated steps in BFS order.
/// <see cref="Truncated"/> reports that a cap cut the walk, so absence of a node is then not proof of absence.
/// </summary>
public sealed record ObjectLineageDto(
    string Key, string Name, string Kind, int Depth,
    IReadOnlyList<LineageStepDto> Upstream,
    IReadOnlyList<LineageStepDto> Downstream,
    bool Truncated);

/// <summary>The latest run of a producing flow: did the last population attempt work, when, and how much landed.</summary>
public sealed record ProducerRunDto(
    string RunId, string Status, DateTime? StartUtc, DateTime? EndUtc, long? RowsLoaded);

/// <summary>One schedule that fires a producing flow: the cadence behind "how often does this table update".
/// <c>Cron</c>/<c>IntervalSeconds</c> carry the clock (exactly one is set for a clock-driven schedule);
/// <c>AfterSchedules</c> is set instead when this schedule chains behind others (it fires when they complete, so
/// its cadence is theirs). <c>NextFireUtc</c> is the concrete next update time.</summary>
public sealed record ProducerScheduleDto(
    string ScheduleId, string Name, string? Cron, int? IntervalSeconds, string Timezone,
    bool Enabled, bool Paused, DateTime? NextFireUtc, DateTime? LastFireUtc,
    IReadOnlyList<string> AfterSchedules);

/// <summary>One flow that WRITES the object, with its latest run and the schedules that fire it. The unit of the
/// "how is this table populated" answer: the flow is the mechanism, the run is the last outcome, the schedules
/// are the cadence.</summary>
public sealed record ObjectProducerDto(
    string PipelineId, string FlowName, string FlowKind, string? Batch, Guid RepoId, string? RepoName,
    string Relation, string Tier, ProducerRunDto? LastRun, IReadOnlyList<ProducerScheduleDto> Schedules);

/// <summary>
/// How an object is populated and how often it updates, in one payload: every flow that writes it, each with its
/// latest run and firing schedules, plus the module edges (<c>ViaModules</c>) when the object is a view/procedure
/// whose content derives from other objects rather than from a flow. An object with no producers and no modules is
/// populated outside SQLFlow's knowledge (a manual load, an external writer), and that absence is the answer.
/// </summary>
public sealed record ObjectRefreshDto(
    string Key, string Name, string Kind,
    IReadOnlyList<ObjectProducerDto> Producers,
    IReadOnlyList<string> ViaModules);

/// <summary>One data subscriber that consumes an object: the answer to "who breaks if I change this table",
/// resolved from the object's read edges to the consumer behind them. <c>Queries</c> names the subscriber's
/// queries that actually reference this object, so the link is evidence rather than assertion.</summary>
public sealed record ObjectSubscriberDto(
    string Key, string Name, string Type, string? Owner, string? Description, string? Notes, string? Url,
    IReadOnlyList<string> Queries);

/// <summary>One data subscriber in the estate-wide list: what consumes the warehouse, who owns it, and how many
/// distinct objects its queries read.</summary>
public sealed record SubscriberDto(
    string Key, string Name, string Type, string? Owner, string? Description, string? Notes, string? Url,
    Guid RepoId, string File, int QueryCount, int ObjectCount, DateTime FirstSeenUtc, DateTime LastSeenUtc);

/// <summary>Everything known about one subscriber: its metadata, the queries it runs, and every warehouse
/// object those queries read, resolved to real names. This is the consumption-side twin of the object
/// dossier: the object dossier answers "who consumes me", this answers "what do I consume".</summary>
public sealed record SubscriberDossierDto(
    SubscriberDto Subscriber,
    IReadOnlyList<SubscriberQueryDto> Queries,
    IReadOnlyList<SubscriberObjectDto> Objects);

/// <summary>One query a subscriber runs, and the objects parsing it proved it reads.</summary>
public sealed record SubscriberQueryDto(
    int Ordinal, string Name, string ServerRef, string Sql, IReadOnlyList<string> ObjectKeys);

/// <summary>One warehouse object a subscriber reads, located and named from the global object registry, with
/// the subscriber's own queries that reference it.</summary>
public sealed record SubscriberObjectDto(
    string Key, string? Database, string? Schema, string Name, string Kind, int? Level,
    IReadOnlyList<string> Queries);

/// <summary>One repo whose lineage references an object: how many edges in that repo touch it, and whether any of
/// them writes/creates it (the repo where a flow populates it). The list is ranked so the writing repo comes
/// first, which is the repo the lineage graph opens on when a search result jumps to "how is this populated".</summary>
public sealed record ObjectRepoDto(Guid RepoId, string RepoName, int EdgeCount, bool Writes);

/// <summary>One flow-level dependency in a repo's execution plan: <c>ToFlow</c> waits for <c>FromFlow</c>.</summary>
public sealed record FlowDependencyDto(
    long Id, Guid RepoId, string FromFlow, string ToFlow, Guid FromPipelineId, Guid ToPipelineId, string ViaObjects);

/// <summary>One execution wave: the active pipelines that share a wave have no dependency between them and run
/// together; a later wave runs only after every earlier wave finishes. Wave -1 means lineage has not been computed
/// for those pipelines yet.</summary>
public sealed record WaveDto(int Wave, IReadOnlyList<WavePipelineDto> Pipelines);

/// <summary>One pipeline within a wave: its stable id and name.</summary>
public sealed record WavePipelineDto(Guid Id, string Name, string Kind);

/// <summary>One selectable project for the lineage graph's scope picker: a repo-root folder within a repo, the repo
/// it lives in (so the picker can show "repo / project" and disambiguate a folder name shared across repos), and
/// how many active flows it holds. The project is the seed the cross-repo lineage closure starts from.</summary>
public sealed record LineageProjectDto(Guid RepoId, string RepoName, string Project, int FlowCount);

/// <summary>One pipeline node in a project's cross-repo lineage closure. <c>IsSeed</c> marks a flow that belongs to
/// the selected project itself (its base objects seed the graph); a non-seed flow was reached downstream, possibly
/// in another repo, which is why <c>RepoId</c>/<c>RepoName</c> travel with every node. <c>Depth</c> is how many
/// downstream hops from the seed the flow sits at, so a client can tint or lay out by distance.
/// <c>LineageComplete</c> is false when the flow depends on a module (an executed procedure, a read view) whose
/// body was never harvested, so its true reads/writes are unknown; <c>IncompleteReason</c> says which module and
/// why, so the client renders "not derived yet" instead of presenting the gap as fact.</summary>
public sealed record ProjectGraphPipelineDto(
    Guid Id, string Name, string Kind, int Wave, Guid RepoId, string RepoName, string RelativePath,
    bool IsSeed, int Depth,
    bool LineageComplete = true, string? IncompleteReason = null);

/// <summary>One object node of the drawable project graph: its key (the node id), display name, resolved kind
/// (<c>table</c>/<c>view</c>/<c>file</c>), where it lives (<c>database.schema</c>, null for a file), and whether
/// it sits on the depth-capped frontier with un-included consumers (so the client can offer to expand it).</summary>
public sealed record ProjectGraphObjectDto(string Key, string Name, string Kind, string? Location, bool Frontier);

/// <summary>One resolved, drawable edge of the project graph. <c>Source</c>/<c>Target</c> are node ids (a
/// pipeline id or an object key). <c>Label</c> is what the arrow says (<c>writes</c>/<c>creates</c>/<c>reads</c>/
/// <c>view</c> in the flows view; the flow name in the objects view). <c>PipelineId</c> is the flow the edge is
/// attributed to, for stable per-flow coloring; null for a DB-managed view's derivation edge, which no flow
/// maintains.</summary>
public sealed record ProjectGraphDrawEdgeDto(string Source, string Target, string Label, Guid? PipelineId);

/// <summary>A project's lineage as one cross-repo subgraph. <c>Pipelines</c> and <c>Edges</c> are the underlying
/// facts (reusing <see cref="EdgeDto"/>, whose <c>RepoId</c> records which repo attributed each fact);
/// <c>Objects</c>, <c>FlowGraph</c>, and <c>ObjectGraph</c> are the DRAWABLE graph derived from those facts
/// server-side, next to the data that knows the answers: nodes typed from the registry, view bodies wired to
/// their base tables, procedures excluded as code dependencies. A client renders and lays out this graph
/// verbatim; it never re-derives semantics from the facts. The walk crosses repo boundaries freely via the
/// global object keys: an object's origin repo is irrelevant to how data flows through it.</summary>
public sealed record ProjectGraphDto(
    IReadOnlyList<ProjectGraphPipelineDto> Pipelines,
    IReadOnlyList<EdgeDto> Edges,
    IReadOnlyList<string> Frontier,
    bool Truncated,
    IReadOnlyList<ProjectGraphObjectDto> Objects,
    IReadOnlyList<ProjectGraphDrawEdgeDto> FlowGraph,
    IReadOnlyList<ProjectGraphDrawEdgeDto> ObjectGraph);

/// <summary>One (server, database, schema) grouping in the catalog with how many objects it holds: the schema
/// hierarchy a caller browses to answer "what schemas exist" and "how big is each" before drilling into
/// objects. A null database/schema is an object whose identity was only partially resolved (an offline sync).</summary>
public sealed record SchemaDto(string ServerRef, string? Database, string? Schema, int ObjectCount);

/// <summary>One (server, database, schema, kind) grouping in the catalog with how many objects it holds: the
/// per-kind breakdown of a schema (its Tables, Views, Procedures, ...) a caller uses to render kind-grouped
/// folders under each schema without listing the objects themselves. A null database/schema is an object whose
/// identity was only partially resolved (an offline sync).</summary>
public sealed record SchemaKindCountDto(
    string ServerRef, string? Database, string? Schema, string Kind, int ObjectCount);

/// <summary>One file endpoint decomposed to its canonical parent for the source tree: the origin system
/// (<c>OriginKind</c> = AzureStorage / Sftp / Local / Other, <c>Origin</c> = the storage account, SFTP
/// <c>host:port</c>, or filesystem), the container (an Azure container or UNC share; null otherwise), the
/// folder path (slash-joined; null at the root), and the leaf name. <c>Key</c> is the object key, so selecting
/// a leaf loads its dossier. This is the file twin of <see cref="SchemaKindCountDto"/>: a file groups under
/// its storage account exactly as a table groups under its database.</summary>
public sealed record FileNodeDto(
    string Key, string OriginKind, string Origin, string? Container, string? Path, string Name);

/// <summary>One object a flow lands data into (a written or created target), for the provenance view of a
/// file source: where the data that came through this file ends up.</summary>
public sealed record LandingObjectDto(string Key, string? Database, string? Schema, string Name, string Kind);

/// <summary>One pipeline that reads a file source, with where it lands the data: the flow identity plus the
/// distinct database objects it writes or creates. This is the "source, through which pipeline, to where"
/// answer the catalog gives for a file.</summary>
public sealed record FileConsumerDto(
    Guid PipelineId, string Flow, string Kind, Guid RepoId, IReadOnlyList<LandingObjectDto> Lands);

/// <summary>One pipeline that produces a file (writes or creates it): where the file itself comes from (a
/// copy, an acquisition, an export), so provenance can chain upstream past the file.</summary>
public sealed record FileProducerDto(Guid PipelineId, string Flow, string Kind, Guid RepoId);

/// <summary>A file source's provenance in one payload: the pipelines that PRODUCE the file (where it comes
/// from) and the pipelines that CONSUME it, each with the tables the data lands in (where it goes). Answers
/// "what are the pipelines for this source, and where does the data land" without walking the whole graph.</summary>
public sealed record FileFlowsDto(
    IReadOnlyList<FileProducerDto> Producers, IReadOnlyList<FileConsumerDto> Consumers);

/// <summary>
/// The read API over the shadow catalog's lineage graph: objects (with their columns and module bodies), the
/// attributed lineage edges, the per-repo flow dependencies, and the computed execution waves. Every query is
/// read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>), projected to DTOs (the raw EF
/// entities never leave the host), and the listing endpoints are bounded by page size. The repo-scoped endpoints
/// first verify the repo exists and return 404 when it does not, so an unknown repo is reported (rather than
/// silently returning an empty result a caller could not distinguish from a real repo with no lineage yet).
/// </summary>
public static class LineageEndpoints
{
    public static RouteGroupBuilder MapLineageEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var lineage = group.MapGroup("/lineage").WithTags("Lineage");
        lineage.MapGet("/schemas", ListSchemasAsync).WithName("ListLineageSchemas");
        lineage.MapGet("/schemas/kinds", ListSchemaKindsAsync).WithName("ListLineageSchemaKinds");
        lineage.MapGet("/file-tree", ListFileTreeAsync).WithName("ListLineageFileTree");
        lineage.MapGet("/file-flows", GetFileFlowsAsync).WithName("GetLineageFileFlows");
        lineage.MapGet("/objects", ListObjectsAsync).WithName("ListLineageObjects");
        lineage.MapGet("/objects/detail", GetObjectAsync).WithName("GetLineageObject");
        lineage.MapGet("/objects/columns", GetObjectColumnsAsync).WithName("GetLineageObjectColumns");
        lineage.MapGet("/objects/repos", ListObjectReposAsync).WithName("ListLineageObjectRepos");
        lineage.MapGet("/objects/dossier", GetObjectDossierAsync).WithName("GetLineageObjectDossier");
        lineage.MapGet("/objects/refresh", GetObjectRefreshAsync).WithName("GetLineageObjectRefresh");
        lineage.MapGet("/objects/graph", GetObjectLineageAsync).WithName("GetLineageObjectGraph");
        lineage.MapGet("/objects/join-paths", GetJoinPathsAsync).WithName("GetLineageObjectJoinPaths");
        lineage.MapGet("/file-pipelines", MatchFilePipelinesAsync).WithName("MatchFilePipelines");
        lineage.MapGet("/script", GetNodeScriptAsync).WithName("GetLineageNodeScript");
        lineage.MapGet("/subscribers", ListSubscribersAsync).WithName("ListLineageSubscribers");
        lineage.MapGet("/subscribers/dossier", GetSubscriberDossierAsync).WithName("GetLineageSubscriberDossier");
        lineage.MapGet("/projects", ListProjectsAsync).WithName("ListLineageProjects");
        lineage.MapGet("/project-graph", GetProjectGraphAsync).WithName("GetLineageProjectGraph");

        var repos = group.MapGroup("/repos").WithTags("Lineage");
        repos.MapGet("/{repoId:guid}/lineage/edges", ListEdgesAsync).WithName("ListLineageEdges");
        repos.MapGet("/{repoId:guid}/waves", GetWavesAsync).WithName("GetExecutionWaves");
        repos.MapGet("/{repoId:guid}/dependencies", GetDependenciesAsync).WithName("GetFlowDependencies");

        return group;
    }

    /// <summary>
    /// The object kinds that are NOT part of the database hierarchy and must never fold into it: a file
    /// endpoint belongs to a storage account, and a data subscriber belongs to no server at all. Both carry a
    /// null database and schema, so leaving them in makes the schema endpoints invent a nameless "unresolved"
    /// database holding them. Each has its own branch (<c>/lineage/file-tree</c> and
    /// <c>/lineage/subscribers</c>), so this is where the split belongs: filtering it in one client leaves
    /// every other caller, the MCP server included, showing the phantom.
    /// </summary>
    private static readonly string[] NonDatabaseKinds = ["File", "Subscriber"];

    private static async Task<Ok<IReadOnlyList<SchemaDto>>> ListSchemasAsync(
        CatalogDbContext db, string? serverRef, string? database, CancellationToken ct)
    {
        var query = db.Objects.AsNoTracking().Where(o => !NonDatabaseKinds.Contains(o.Kind));
        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        // The whole schema hierarchy in one response (no paging): the distinct (server, database, schema)
        // groupings are bounded by the estate's real schema count, not the object count, so this cannot grow
        // without bound. Ordered so the hierarchy reads top-down and deterministically.
        var groups = await query
            .GroupBy(o => new { o.ServerRef, o.Database, o.Schema })
            .Select(g => new SchemaDto(g.Key.ServerRef, g.Key.Database, g.Key.Schema, g.Count()))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal)
            .ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SchemaDto>>(ordered);
    }

    private static async Task<Ok<IReadOnlyList<SchemaKindCountDto>>> ListSchemaKindsAsync(
        CatalogDbContext db, string? serverRef, string? database, string? schema, CancellationToken ct)
    {
        var query = db.Objects.AsNoTracking().Where(o => !NonDatabaseKinds.Contains(o.Kind));
        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            query = query.Where(o => o.Schema == schema);
        }

        // The whole per-kind breakdown in one response (no paging): the distinct (server, database, schema, kind)
        // groupings are bounded by the estate's schema count times the handful of object kinds, not the object
        // count, so this cannot grow without bound. Ordered so the hierarchy reads top-down and deterministically.
        var groups = await query
            .GroupBy(o => new { o.ServerRef, o.Database, o.Schema, o.Kind })
            .Select(g => new SchemaKindCountDto(
                g.Key.ServerRef, g.Key.Database, g.Key.Schema, g.Key.Kind, g.Count()))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(s => s.ServerRef, StringComparer.Ordinal)
            .ThenBy(s => s.Database, StringComparer.Ordinal)
            .ThenBy(s => s.Schema, StringComparer.Ordinal)
            .ThenBy(s => s.Kind, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SchemaKindCountDto>>(ordered);
    }

    /// <summary>
    /// Every file endpoint in the catalog, each decomposed to its canonical parent (storage account / SFTP
    /// server / UNC share / local filesystem, then container, folders, and leaf), so the GUI folds them into a
    /// source tree the twin of the database tree: origin > container > folder > file. Returned in one response
    /// (no paging): file nodes are the distinct file LOCATIONS flows declare (a folder a flow reads is one
    /// node, not one per physical blob), so the set is bounded by the estate's flow count, not its data volume.
    /// The origin is parsed with <see cref="FileOrigin"/>, which every flow type's file identity funnels
    /// through, so no flow kind is missed and a malformed identity still yields a shown node.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<FileNodeDto>>> ListFileTreeAsync(CatalogDbContext db, CancellationToken ct)
    {
        var files = await db.Objects.AsNoTracking()
            .Where(o => o.Kind == "File")
            .Select(o => new { o.Key, o.Name })
            .ToListAsync(ct).ConfigureAwait(false);

        // Parse the Name (the clean canonical identity, e.g. az://account/container/path), not the Key: the
        // Key is the node identity 'file|||<name>' (server-reference-prefixed and case-folded), whose prefix
        // would defeat the scheme detection and lower-cased blob path would lose case. The Key still rides
        // along as the leaf's object id so selecting it opens the dossier.
        var nodes = files
            .Select(file =>
            {
                var origin = FileOrigin.Parse(file.Name);
                return new FileNodeDto(
                    file.Key, origin.Kind.ToString(), origin.Origin, origin.Container, origin.Path, origin.Name);
            })
            .OrderBy(n => n.Origin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Container, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<FileNodeDto>>(nodes);
    }

    /// <summary>
    /// A file source's provenance: the pipelines that produce it and the pipelines that consume it, each
    /// consumer with the database objects it lands the data in. Resolved from the flow-attributed lineage
    /// edges on this object key (across every repo, since a file identity is global): a Writes/Creates edge is
    /// a producer, a Reads/Requires edge a consumer, and a consumer's landing is that flow's own Writes/Creates
    /// edges onto non-file objects. Bounded: a file is touched by a handful of flows, each landing a handful of
    /// tables. Answers "what pipelines use this source and where does the data land" in one call.
    /// </summary>
    private static async Task<Ok<FileFlowsDto>> GetFileFlowsAsync(CatalogDbContext db, string key, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Ok(new FileFlowsDto([], []));
        }

        // Every flow-attributed edge on the file, across repos: its relation tells producer from consumer.
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key && e.PipelineId != null)
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.Relation })
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            return TypedResults.Ok(new FileFlowsDto([], []));
        }

        var producerIds = edges.Where(e => e.Relation is "Writes" or "Creates").Select(e => e.PipelineId).Distinct().ToList();
        var consumerIds = edges.Where(e => e.Relation is "Reads" or "Requires").Select(e => e.PipelineId).Distinct().ToList();
        var pipelineIds = producerIds.Concat(consumerIds).Distinct().ToList();

        // The involved flows' identities (name/kind/repo) in one lookup.
        var pipelines = (await db.Pipelines.AsNoTracking()
                .Where(p => pipelineIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.Kind, p.RepoId })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(p => p.Id);

        // Each consumer's landing: the non-file objects it writes or creates, joined to the registry for names.
        var landings = consumerIds.Count == 0
            ? []
            : await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId != null && consumerIds.Contains(e.PipelineId!.Value)
                    && (e.Relation == "Writes" || e.Relation == "Creates"))
                .Join(db.Objects.AsNoTracking().Where(o => o.Kind != "File"), e => e.ObjectKey, o => o.Key,
                    (e, o) => new { PipelineId = e.PipelineId!.Value, o.Key, o.Database, o.Schema, o.Name, o.Kind })
                .Distinct()
                .ToListAsync(ct).ConfigureAwait(false);

        var landsByPipeline = landings
            .GroupBy(l => l.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LandingObjectDto>)g
                    .Select(l => new LandingObjectDto(l.Key, l.Database, l.Schema, l.Name, l.Kind))
                    .OrderBy(l => l.Database, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Schema, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var producers = producerIds
            .Where(pipelines.ContainsKey)
            .Select(id => new FileProducerDto(id, pipelines[id].Name, pipelines[id].Kind, pipelines[id].RepoId))
            .OrderBy(p => p.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var consumers = consumerIds
            .Where(pipelines.ContainsKey)
            .Select(id => new FileConsumerDto(
                id, pipelines[id].Name, pipelines[id].Kind, pipelines[id].RepoId,
                landsByPipeline.TryGetValue(id, out var lands) ? lands : []))
            .OrderBy(c => c.Flow, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new FileFlowsDto(producers, consumers));
    }

    private static async Task<Ok<PagedResult<ObjectDto>>> ListObjectsAsync(
        CatalogDbContext db, string? name, string? serverRef, string? database, string? schema, string? kind,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Objects.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(o => o.Name.Contains(name));
        }

        if (!string.IsNullOrWhiteSpace(serverRef))
        {
            query = query.Where(o => o.ServerRef == serverRef);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(o => o.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(schema))
        {
            query = query.Where(o => o.Schema == schema);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(o => o.Kind == kind);
        }

        // Dependency order: by level (an object's depth in the data-movement graph, sources first), objects
        // outside the movement graph (null level) last, then by identity so a page boundary is deterministic.
        var ordered = query
            .OrderBy(o => o.Level == null)
            .ThenBy(o => o.Level)
            .ThenBy(o => o.Database).ThenBy(o => o.Schema).ThenBy(o => o.Name).ThenBy(o => o.Key);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(o => new ObjectDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.FirstSeenUtc, o.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<ObjectDetailDto>, ProblemHttpResult>> GetObjectAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.Definition, o.Script,
                o.ScriptTier, o.ScriptUpdatedUtc, o.KeyColumns, o.KeyOrigin, o.FirstSeenUtc, o.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("object", key) : TypedResults.Ok(dto);
    }

    /// <summary>
    /// The unified script accessor: <c>GET /api/v1/lineage/script?key=&lt;node&gt;</c> returns the code behind any
    /// node in the graph, resolving the identifier across both sides of the bipartite graph. A catalog object
    /// (table/view/procedure/function/trigger) is matched by its node <c>Key</c> and returns its module body
    /// (<c>Definition</c>) when it has one, else its generated script (a table's CREATE TABLE); a pipeline is
    /// matched by its flow name, or by its stable id when the key is a GUID, and returns its authored YAML. So a
    /// single call serves every node kind the lineage graph draws. A pipeline key has one more facet:
    /// <c>view=object</c> returns the SQL of the database object the flow executes instead of the YAML (an sp
    /// flow's procedure, resolved through the flow's <c>Requires</c> lineage edge), so a client can offer both
    /// "the flow definition" and "the code it runs" for the same node.
    /// </summary>
    private static async Task<Results<Ok<NodeScriptDto>, ProblemHttpResult>> GetNodeScriptAsync(
        string key, string? view, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (an object node key or a pipeline name/id).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // A database object's script in the endpoint's shape: the module body is the authoritative source for a
        // view/procedure/function/trigger; a table has no module body, so its generated CREATE TABLE script is
        // returned instead. Shared by the direct object-key path and the pipeline view=object path.
        async Task<NodeScriptDto?> ObjectScriptAsync(string objectKey)
        {
            var obj = await db.Objects.AsNoTracking().Where(o => o.Key == objectKey)
                .Select(o => new { o.Key, o.Kind, o.Name, o.Definition, o.Script, o.ScriptTier })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return obj is null
                ? null
                : new NodeScriptDto(
                    obj.Key, obj.Kind, "sql", obj.Definition ?? obj.Script,
                    obj.Definition is not null ? "Module" : obj.ScriptTier, obj.Name);
        }

        var direct = await ObjectScriptAsync(key).ConfigureAwait(false);
        if (direct is not null)
        {
            return TypedResults.Ok(direct);
        }

        // A pipeline (flow) node: matched by its name, or by its stable id when the caller passed a GUID.
        var isId = Guid.TryParse(key, out var pipelineId);
        var pipe = await db.Pipelines.AsNoTracking()
            .Where(p => p.Name == key || (isId && p.Id == pipelineId))
            .Select(p => new { p.Id, p.Name, p.Kind, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipe is not null)
        {
            if (!string.Equals(view, "object", StringComparison.OrdinalIgnoreCase))
            {
                return TypedResults.Ok(new NodeScriptDto(pipe.Name, "pipeline", "yaml", pipe.Yaml, "Authored", pipe.Name));
            }

            // view=object: the code the flow runs rather than its YAML. The flow's `Requires` edge names the
            // executed object (an sp flow declares exactly one, its procedure). When the object registry has no
            // row yet (its server was never connected-synced) the response still identifies the object, with a
            // null script, so the client can render its "no captured script" guidance instead of an error.
            var required = await db.LineageEdges.AsNoTracking()
                .Where(e => e.PipelineId == pipe.Id && e.Relation == "Requires")
                .OrderBy(e => e.Id)
                .Select(e => new { e.ObjectKey, e.ObjectName })
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (required is null)
            {
                return NotFound("executed database object for flow", pipe.Name);
            }

            var executed = await ObjectScriptAsync(required.ObjectKey).ConfigureAwait(false)
                ?? new NodeScriptDto(required.ObjectKey, "Procedure", "sql", null, null, required.ObjectName);
            return TypedResults.Ok(executed);
        }

        return NotFound("script for node", key);
    }

    private static async Task<Results<Ok<PagedResult<ObjectColumnDto>>, ProblemHttpResult>> GetObjectColumnsAsync(
        string key, CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var exists = await db.Objects.AsNoTracking().AnyAsync(o => o.Key == key, ct).ConfigureAwait(false);
        if (!exists)
        {
            return NotFound("object", key);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        // A wide table's column list can be large, so it is paged; ordered by the captured ordinal, then name as a
        // stable secondary key so a page boundary is deterministic.
        var ordered = db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == key)
            .OrderBy(c => c.Ordinal).ThenBy(c => c.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var columns = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(c => new ObjectColumnDto(c.Ordinal, c.Name, c.DataType, c.Nullable, c.Tier))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<ObjectColumnDto>(columns, p, size, total));
    }

    /// <summary>
    /// The repos whose lineage references an object, ranked so the repo that populates it comes first. An object
    /// key is global (the same physical table appears in every repo that reads or writes it), but the lineage
    /// graph is drawn per repo, so a client jumping from a search hit to "how is this object populated" needs to
    /// know which repo's graph to open. Grouping the object's edges by repo answers that in one call: the repo
    /// with a <c>Writes</c>/<c>Creates</c> edge (a flow producing the object) is ranked first, then by how much of
    /// the object's lineage lives in the repo. An empty list means the object has no recorded lineage edges, so it
    /// appears in no graph.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<ObjectRepoDto>>, ProblemHttpResult>> ListObjectReposAsync(
        string? key, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return TypedResults.Problem(
                detail: "A 'key' query parameter is required (a lineage object key).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Count the object's edges per repo and flag whether any of them writes/creates it (the producing repo).
        var groups = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key)
            .GroupBy(e => e.RepoId)
            .Select(g => new
            {
                RepoId = g.Key,
                EdgeCount = g.Count(),
                Writes = g.Any(e => e.Relation == "Writes" || e.Relation == "Creates"),
            })
            .ToListAsync(ct).ConfigureAwait(false);
        if (groups.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<ObjectRepoDto>>(Array.Empty<ObjectRepoDto>());
        }

        // Resolve the repo names in one round-trip (the repo set is small and bounded by the estate's repos).
        var repoIds = groups.Select(g => g.RepoId).ToList();
        var names = await db.Repos.AsNoTracking()
            .Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        // Rank: the repo that populates the object first (the "how is it populated" view the graph opens on), then
        // by how much of its lineage lives there, then by name so the order is stable.
        var ranked = groups
            .Select(g => new ObjectRepoDto(
                g.RepoId,
                names.TryGetValue(g.RepoId, out var name) ? name : g.RepoId.ToString(),
                g.EdgeCount,
                g.Writes))
            .OrderByDescending(r => r.Writes)
            .ThenByDescending(r => r.EdgeCount)
            .ThenBy(r => r.RepoName, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<ObjectRepoDto>>(ranked);
    }

    /// <summary>
    /// Resolve which file-ingestion pipelines would ingest a given file, by matching it against the source spec
    /// each file flow declares (the spec already in the catalog, so no run is required). The match mirrors the
    /// engine's file selection: the source's file-name glob (<c>source.options.srcFile</c>, defaulting per source
    /// type) is applied to the file NAME with the same matcher the engine's cloud store uses
    /// (<c>FileSystemName.MatchesSimpleExpression</c>); when a
    /// full path is given it is further constrained by the path mask (<c>source.options.srcPathMask</c>, a regex
    /// over the path) or, absent a mask, by the source <c>location</c> the path must sit under. A bare file name
    /// (no path) matches on the glob alone and can return several flows, which the caller disambiguates. This is
    /// the definitional "which flow feeds this file" answer, complementing the historical run-to-file record.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<FilePipelineMatchDto>>, ProblemHttpResult>> MatchFilePipelinesAsync(
        string? file, CatalogDbContext db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            return TypedResults.Problem(
                detail: "A 'file' query parameter is required (a file name or full path).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Normalize to forward slashes; the file name is the last path segment, the rest is the (optional) path.
        var input = file.Trim().Replace('\\', '/');
        var slash = input.LastIndexOf('/');
        var fileName = slash >= 0 ? input[(slash + 1)..] : input;
        var hasPath = slash >= 0;
        if (fileName.Length == 0)
        {
            return TypedResults.Problem(
                detail: "The 'file' value has no file name to match.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        // Only file flows have a file source spec; scan the active ones (bounded by the estate's file flows).
        var flows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Kind == "file" && p.Active)
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.Id, p.Name, p.RepoId, RepoName = r.Name, p.DefinitionJson })
            .ToListAsync(ct).ConfigureAwait(false);

        var matches = new List<(FilePipelineMatchDto Dto, bool Confirmed)>();
        foreach (var flow in flows)
        {
            var source = ExtractFileSource(flow.DefinitionJson);
            if (source is null)
            {
                continue;
            }

            var glob = string.IsNullOrWhiteSpace(source.Glob) ? FileSelection.DefaultPattern(source.Type) : source.Glob!;
            // The engine applies the glob to the file name; reuse the one shared matcher (the exact BCL matcher its
            // cloud store uses) so the read API and the lineage collector select files identically.
            if (!FileSelection.NameMatchesGlob(glob, fileName))
            {
                continue;
            }

            // Confirm against the path when one was supplied: the path mask (a regex over the path) takes priority;
            // otherwise the path must sit under the source location (both normalized). A location carrying an
            // unresolved ${...} reference cannot be compared, so it does not constrain the match.
            bool? pathConfirmed = null;
            if (hasPath)
            {
                if (!string.IsNullOrWhiteSpace(source.Mask))
                {
                    pathConfirmed = FileSelection.SafeRegexMatch(source.Mask!, input);
                }
                else if (!string.IsNullOrWhiteSpace(source.Location) && !source.Location!.Contains("${", StringComparison.Ordinal))
                {
                    var loc = source.Location!.Replace('\\', '/').TrimEnd('/');
                    pathConfirmed = loc.Length > 0
                        && input.Contains(loc, StringComparison.OrdinalIgnoreCase);
                }
            }

            // A path constraint we could evaluate and that failed excludes the flow; a glob-only match (no path, or
            // no constraint to check) is still returned, flagged as unconfirmed so the caller can rank it lower.
            if (pathConfirmed == false)
            {
                continue;
            }

            matches.Add((
                new FilePipelineMatchDto(
                    flow.Id, flow.Name, flow.RepoId, flow.RepoName, source.Type, source.Location, glob,
                    pathConfirmed == true),
                pathConfirmed == true));
        }

        var ordered = matches
            .OrderByDescending(m => m.Confirmed)
            .ThenBy(m => m.Dto.RepoName, StringComparer.Ordinal)
            .ThenBy(m => m.Dto.PipelineName, StringComparer.Ordinal)
            .Select(m => m.Dto)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<FilePipelineMatchDto>>(ordered);
    }

    /// <summary>The parsed file source of a flow: what a file must match to be ingested by it.</summary>
    private sealed record FileSource(string Type, string? Location, string? Glob, string? Mask);

    /// <summary>Reads a file flow's source spec out of its stored <c>DefinitionJson</c> (the parsed flow), reaching
    /// <c>flow.source</c> and its options. Returns null when the document is not a file flow or carries no source.</summary>
    private static FileSource? ExtractFileSource(string definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            if (!doc.RootElement.TryGetProperty("flow", out var flow)
                || flow.ValueKind != JsonValueKind.Object
                || !flow.TryGetProperty("source", out var source)
                || source.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var type = StringProp(source, "type") ?? "";
            var location = StringProp(source, "location");
            string? glob = null, mask = null, srcPath = null;
            if (source.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Object)
            {
                glob = StringProp(options, "srcFile");
                mask = StringProp(options, "srcPathMask");
                srcPath = StringProp(options, "srcPath");
            }

            // The source root is either the endpoint location or the srcPath option (the loader accepts either).
            location ??= srcPath;
            // Nothing to match on (neither a type to default a pattern from, nor an explicit glob): not a usable
            // file source.
            if (type.Length == 0 && string.IsNullOrEmpty(glob))
            {
                return null;
            }

            return new FileSource(type, location, glob, mask);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringProp(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>The maximum rows each list of the dossier returns: an object's columns and its edges are
    /// bounded by the object, but a hot object can be referenced by many flows across many repos, so each
    /// collection is capped for a stable, single-response payload (the paged endpoints serve the full sets).</summary>
    private const int MaxDossierRows = 500;

    /// <summary>The node-key prefix every data subscriber carries: subscribers live on a synthetic server
    /// identity, so a key starting with this is a consumer, never a database object. Mirrors the <c>file|</c>
    /// prefix the drawable graph already keys file endpoints by.</summary>
    private const string SubscriberKeyPrefix = "subscriber|";

    private static async Task<Results<Ok<ObjectDossierDto>, ProblemHttpResult>> GetObjectDossierAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var detail = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new ObjectDetailDto(
                o.Key, o.ServerRef, o.Database, o.Schema, o.Name, o.Kind, o.Level, o.Definition, o.Script,
                o.ScriptTier, o.ScriptUpdatedUtc, o.KeyColumns, o.KeyOrigin, o.FirstSeenUtc, o.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (detail is null)
        {
            return NotFound("object", key);
        }

        var columns = await db.ObjectColumns.AsNoTracking()
            .Where(c => c.ObjectKey == key)
            .OrderBy(c => c.Ordinal).ThenBy(c => c.Name)
            .Take(MaxDossierRows)
            .Select(c => new ObjectColumnDto(c.Ordinal, c.Name, c.DataType, c.Nullable, c.Tier))
            .ToListAsync(ct).ConfigureAwait(false);

        // Object-level edges reference the key across every repo (the object is global): both what reads it and
        // what writes it, so the model sees the flows on both sides. Every edge here points at THE dossier
        // object, so its database/schema come straight from the detail row (no join needed).
        var edges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key)
            .OrderBy(e => e.Relation).ThenBy(e => e.Flow).ThenBy(e => e.Id)
            .Take(MaxDossierRows)
            .Select(e => new EdgeDto(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName,
                detail.Database, detail.Schema, e.Tier, detail.Kind))
            .ToListAsync(ct).ConfigureAwait(false);

        // The interpreted data model, both directions. Rows are repo-scoped (each repo's code exhibits its own
        // observations), so the same relationship is deduplicated here by its identity, keeping the strongest
        // interpretation (constraint over join), the highest occurrence count, and the first constraint name.
        var rawRelationships = await db.ObjectRelationships.AsNoTracking()
            .Where(r => r.FromObjectKey == key || r.ToObjectKey == key)
            .OrderBy(r => r.Id)
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);

        var deduped = rawRelationships
            .GroupBy(r => (r.FromObjectKey, r.FromColumns, r.ToObjectKey, r.ToColumns, r.Origin, r.Operators), r => r)
            .Select(g => new RelationshipAggregate(
                g.Select(r => r.Name).FirstOrDefault(n => n is not null),
                g.Key.FromObjectKey, g.Key.FromColumns, g.Key.ToObjectKey, g.Key.ToColumns, g.Key.Origin,
                g.Select(r => r.Tier).OrderByDescending(TierRank).First(),
                g.Max(r => r.Occurrences),
                g.Key.Operators,
                // Repos are deduplicated here, so the join types union across them for the same reason they
                // union across scripts: two repos disagreeing is still a disagreement worth showing.
                string.Join(",", g.SelectMany(r => SplitColumns(r.JoinTypes))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(t => t, StringComparer.Ordinal))))
            .ToList();

        var otherKeys = deduped
            .Select(r => r.FromObjectKey == key ? r.ToObjectKey : r.FromObjectKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var locations = await LoadObjectLocationsAsync(db, otherKeys, ct).ConfigureAwait(false);

        ObjectRelationshipDto Project(RelationshipAggregate r, bool outgoing)
        {
            var otherKey = outgoing ? r.ToObjectKey : r.FromObjectKey;
            var found = locations.TryGetValue(otherKey, out var other);
            // Reading the relationship from the other end inverts every comparison, so the operators mirror
            // with the columns; leaving them unmirrored would render a temporal join backwards.
            var operators = SplitColumns(r.Operators);
            return new ObjectRelationshipDto(
                r.Name, r.Origin, r.Tier, r.Occurrences,
                otherKey, other.Database, other.Schema, found ? other.Name : otherKey,
                OwnColumns: outgoing ? r.FromColumns : r.ToColumns,
                OtherColumns: outgoing ? r.ToColumns : r.FromColumns,
                Operators: outgoing ? operators : operators.Select(MirrorComparison).ToArray(),
                JoinTypes: SplitColumns(r.JoinTypes),
                IsRangeJoin: operators.Count > 0);
        }

        var references = deduped.Where(r => r.FromObjectKey == key)
            .Select(r => Project(r, outgoing: true))
            .OrderByDescending(r => r.Occurrences).ThenBy(r => r.OtherName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var referencedBy = deduped.Where(r => r.ToObjectKey == key && r.FromObjectKey != key)
            .Select(r => Project(r, outgoing: false))
            .OrderByDescending(r => r.Occurrences).ThenBy(r => r.OtherName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Who consumes this object. The read edges already carry it, but a raw subscriber node key tells a person
        // nothing; this resolves them to the reports and their owners, which is the whole point of the model.
        var subscribers = await LoadObjectSubscribersAsync(db, key, ct).ConfigureAwait(false);

        return TypedResults.Ok(new ObjectDossierDto(detail, columns, edges, references, referencedBy, subscribers));
    }

    /// <summary>The lineage relations that mean a flow POPULATES the object (as opposed to reading or merely
    /// requiring it).</summary>
    private static readonly string[] WritingRelations = ["Writes", "Creates"];

    /// <summary>The deepest transitive walk the graph endpoint performs; deeper requests are clamped, and the
    /// answer says the depth it actually used.</summary>
    private const int MaxTraversalDepth = 8;

    /// <summary>The most steps one direction of a traversal returns. A hub object (an audit table every flow
    /// touches) would otherwise explode the walk; past this the answer is marked truncated instead of unbounded.</summary>
    private const int MaxTraversalSteps = 400;

    /// <summary>
    /// The transitive lineage of one object, walked breadth-first through the edge table. Each hop alternates
    /// between objects and the flows/modules that connect them: upstream finds who WRITES the frontier objects and
    /// then what those writers READ; downstream finds who READS the frontier and then what those readers WRITE.
    /// This is the traversal behind "where does this table's data come from" and "what breaks downstream", which
    /// the single-object dossier cannot answer: its edges stop at one hop.
    /// </summary>
    private static async Task<Results<Ok<ObjectLineageDto>, ProblemHttpResult>> GetObjectLineageAsync(
        string key, CatalogDbContext db, string? direction, int? depth, CancellationToken ct)
    {
        var identity = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new { o.Key, o.Name, o.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (identity is null)
        {
            return NotFound("object", key);
        }

        var dir = (direction ?? "both").Trim().ToUpperInvariant();
        if (dir is not ("BOTH" or "UPSTREAM" or "DOWNSTREAM"))
        {
            return TypedResults.Problem(
                detail: "direction must be 'upstream', 'downstream', or 'both' (the default).",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid lineage request");
        }

        var levels = Math.Clamp(depth ?? 3, 1, MaxTraversalDepth);
        var truncated = false;

        List<LineageStepDto> upstream = [];
        List<LineageStepDto> downstream = [];
        if (dir is "BOTH" or "UPSTREAM")
        {
            (upstream, var cut) = await TraverseAsync(db, key, upstream: true, levels, ct).ConfigureAwait(false);
            truncated |= cut;
        }

        if (dir is "BOTH" or "DOWNSTREAM")
        {
            (downstream, var cut) = await TraverseAsync(db, key, upstream: false, levels, ct).ConfigureAwait(false);
            truncated |= cut;
        }

        return TypedResults.Ok(new ObjectLineageDto(
            identity.Key, identity.Name, identity.Kind, levels, upstream, downstream, truncated));
    }

    /// <summary>
    /// One direction of the walk. Per level, two set queries (never per-node queries): the connecting flows and
    /// modules of the whole frontier, then everything on their far side. Objects already visited are not re-walked,
    /// so a diamond (two flows landing in one table) reports each object once, at its shortest distance.
    /// </summary>
    private static async Task<(List<LineageStepDto> Steps, bool Truncated)> TraverseAsync(
        CatalogDbContext db, string originKey, bool upstream, int levels, CancellationToken ct)
    {
        // Walking upstream: who WRITES the frontier, and what do those writers READ. Downstream mirrors it.
        var frontierRelations = upstream ? WritingRelations : new[] { "Reads" };
        var farSideRelations = upstream ? new[] { "Reads" } : WritingRelations;

        var steps = new List<LineageStepDto>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { originKey };
        var frontier = new List<string> { originKey };
        var truncated = false;

        for (var level = 1; level <= levels && frontier.Count > 0 && !truncated; level++)
        {
            // The flows and modules touching the frontier from the connecting side.
            var connectors = await ConnectorsQuery(db, frontier, frontierRelations)
                .Take(MaxTraversalSteps + 1)
                .ToListAsync(ct).ConfigureAwait(false);
            if (connectors.Count > MaxTraversalSteps)
            {
                truncated = true;
                connectors.RemoveAt(connectors.Count - 1);
            }

            var pipelineIds = connectors.Where(c => c.PipelineId != null).Select(c => c.PipelineId!.Value)
                .Distinct().ToList();
            var modules = connectors.Where(c => c.PipelineId == null && c.ViaModule != null).Select(c => c.ViaModule!)
                .Distinct().ToList();
            if (pipelineIds.Count == 0 && modules.Count == 0)
            {
                break;
            }

            // Everything on those connectors' far side.
            var farEdges = await FarSideQuery(db, pipelineIds, modules, farSideRelations)
                .Take(MaxTraversalSteps + 1)
                .ToListAsync(ct).ConfigureAwait(false);
            if (farEdges.Count > MaxTraversalSteps)
            {
                truncated = true;
                farEdges.RemoveAt(farEdges.Count - 1);
            }

            var next = new List<string>();
            var levelSteps = new List<(string? PipelineId, string? Flow, string? ViaModule, string Key, string Name)>();
            foreach (var edge in farEdges)
            {
                if (!visited.Add(edge.ObjectKey))
                {
                    continue;
                }

                next.Add(edge.ObjectKey);
                levelSteps.Add((edge.PipelineId?.ToString(), edge.Flow, edge.ViaModule, edge.ObjectKey, edge.ObjectName));
            }

            // One identity lookup for the whole level, so each step names where its object lives.
            var locations = await LoadObjectLocationsAsync(db, next, ct).ConfigureAwait(false);
            foreach (var (pipelineId, flow, viaModule, objectKey, objectName) in levelSteps)
            {
                var found = locations.TryGetValue(objectKey, out var location);
                steps.Add(new LineageStepDto(
                    level, pipelineId, flow, viaModule, objectKey,
                    found ? location.Name : objectName,
                    location.Database, location.Schema, location.Kind));

                if (steps.Count >= MaxTraversalSteps)
                {
                    return (steps, Truncated: true);
                }
            }

            frontier = next;
        }

        return (steps, truncated);
    }

    /// <summary>The distinct flows/modules relating to any frontier object with one of the given relations
    /// (internal so the translation test can render it to SQL).</summary>
    internal static IQueryable<TraversalConnector> ConnectorsQuery(
        CatalogDbContext db, List<string> frontier, string[] relations)
        => db.LineageEdges.AsNoTracking()
            .Where(e => frontier.Contains(e.ObjectKey) && relations.Contains(e.Relation))
            .Select(e => new TraversalConnector(e.PipelineId, e.Flow, e.ViaModule))
            .Distinct();

    /// <summary>The edges on the far side of a set of connectors: what those flows/modules relate to with the
    /// given relations (internal so the translation test can render it to SQL).</summary>
    internal static IQueryable<TraversalEdge> FarSideQuery(
        CatalogDbContext db, List<Guid> pipelineIds, List<string> modules, string[] relations)
        => db.LineageEdges.AsNoTracking()
            .Where(e => relations.Contains(e.Relation)
                && ((e.PipelineId != null && pipelineIds.Contains(e.PipelineId.Value))
                    || (e.PipelineId == null && e.ViaModule != null && modules.Contains(e.ViaModule))))
            .OrderBy(e => e.ObjectName).ThenBy(e => e.Id)
            .Select(e => new TraversalEdge(e.PipelineId, e.Flow, e.ViaModule, e.ObjectKey, e.ObjectName));

    /// <summary>One distinct flow/module attribution in a traversal level.</summary>
    internal sealed record TraversalConnector(Guid? PipelineId, string? Flow, string? ViaModule);

    /// <summary>One far-side edge of a traversal level.</summary>
    internal sealed record TraversalEdge(
        Guid? PipelineId, string? Flow, string? ViaModule, string ObjectKey, string ObjectName);

    /// <summary>The newest run of each of the given pipelines, resolved server-side (a grouped top-1, translated
    /// to a windowed query; internal so the translation test can render it to SQL).</summary>
    internal static IQueryable<CatalogRun> LatestRunsQuery(CatalogDbContext db, List<Guid> pipelineIds)
        => db.Runs.AsNoTracking()
            .Where(r => pipelineIds.Contains(r.PipelineId))
            .GroupBy(r => r.PipelineId)
            .Select(g => g.OrderByDescending(r => r.StartUtc).ThenByDescending(r => r.RunId).First());

    /// <summary>
    /// How an object is populated and how often it updates: the writing flows resolved from the object's lineage
    /// edges, each joined to its latest run and to every schedule that fires it (including chained schedules,
    /// whose cadence is their parents'). A view or procedure with no writing flow reports its module edges
    /// instead, pointing the caller at the derivation to read.
    /// </summary>
    private static async Task<Results<Ok<ObjectRefreshDto>, ProblemHttpResult>> GetObjectRefreshAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var identity = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new { o.Key, o.Name, o.Kind })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (identity is null)
        {
            return NotFound("object", key);
        }

        // Flow-attributed writing edges: one producer per distinct flow, keeping the strongest edge per flow
        // (a flow can carry both a Declared and an Observed fact for the same object).
        var writeEdges = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key && e.PipelineId != null && WritingRelations.Contains(e.Relation))
            .OrderBy(e => e.Flow).ThenBy(e => e.Id)
            .Take(MaxDossierRows)
            .Select(e => new { PipelineId = e.PipelineId!.Value, e.Flow, e.RepoId, e.Relation, e.Tier })
            .ToListAsync(ct).ConfigureAwait(false);
        var producers = writeEdges
            .GroupBy(e => e.PipelineId)
            .Select(g => g.First())
            .ToList();

        // Module-derived write edges (a view's SELECT, a procedure's INSERT) have no pipeline; naming the module
        // tells the caller where the derivation lives when no flow writes the object directly.
        var viaModules = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == key && e.PipelineId == null && e.ViaModule != null
                && WritingRelations.Contains(e.Relation))
            .OrderBy(e => e.ViaModule)
            .Select(e => e.ViaModule!)
            .Distinct()
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);

        if (producers.Count == 0)
        {
            return TypedResults.Ok(new ObjectRefreshDto(
                identity.Key, identity.Name, identity.Kind, [], viaModules));
        }

        var pipelineIds = producers.Select(p => p.PipelineId).ToList();

        // The flow identities behind the edges; a producer whose pipeline has left the estate still reports from
        // its edge (name from the edge, no batch/repo detail), so history does not hide a former writer.
        var pipelines = await db.Pipelines.AsNoTracking()
            .Where(p => pipelineIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Kind, p.Batch, p.RepoId })
            .ToListAsync(ct).ConfigureAwait(false);
        var pipelineById = pipelines.ToDictionary(p => p.Id);

        var repoIds = pipelines.Select(p => p.RepoId)
            .Concat(producers.Select(p => p.RepoId))
            .Distinct()
            .ToList();
        var repoNames = await db.Repos.AsNoTracking()
            .Where(r => repoIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.Name, ct).ConfigureAwait(false);

        // Latest run per producing pipeline, resolved server-side (one row per pipeline, newest StartUtc).
        var latestRuns = await LatestRunsQuery(db, pipelineIds).ToListAsync(ct).ConfigureAwait(false);
        var runByPipeline = latestRuns.ToDictionary(r => r.PipelineId);

        // Every schedule each producer is a member of, with the schedule's cadence and its parents (a chained
        // schedule has no clock of its own; its parents are its cadence).
        var memberships = await (
                from m in db.ScheduleMembers.AsNoTracking()
                join s in db.Schedules.AsNoTracking() on m.ScheduleId equals s.Id
                where pipelineIds.Contains(m.PipelineId)
                select new
                {
                    m.PipelineId,
                    s.Id, s.Name, s.Cron, s.IntervalSeconds, s.Timezone, s.Enabled, s.Paused,
                    s.NextFireUtc, s.LastFireUtc,
                })
            .ToListAsync(ct).ConfigureAwait(false);

        var scheduleIds = memberships.Select(m => m.Id).Distinct().ToList();
        var parents = await db.ScheduleParents.AsNoTracking()
            .Where(p => scheduleIds.Contains(p.ScheduleId))
            .OrderBy(p => p.Ordinal)
            .ToListAsync(ct).ConfigureAwait(false);
        var parentsBySchedule = parents
            .GroupBy(p => p.ScheduleId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(p => p.ParentName).ToList());

        var schedulesByPipeline = memberships
            .GroupBy(m => m.PipelineId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<ProducerScheduleDto>)g
                    .Select(m => new ProducerScheduleDto(
                        m.Id.ToString(), m.Name, m.Cron, m.IntervalSeconds, m.Timezone, m.Enabled, m.Paused,
                        m.NextFireUtc, m.LastFireUtc,
                        parentsBySchedule.GetValueOrDefault(m.Id, [])))
                    .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList());

        var producerDtos = producers
            .Select(p =>
            {
                var pipeline = pipelineById.GetValueOrDefault(p.PipelineId);
                var run = runByPipeline.GetValueOrDefault(p.PipelineId);
                var repoId = pipeline?.RepoId ?? p.RepoId;
                return new ObjectProducerDto(
                    p.PipelineId.ToString(),
                    pipeline?.Name ?? p.Flow ?? string.Empty,
                    pipeline?.Kind ?? run?.FlowKind ?? string.Empty,
                    pipeline?.Batch,
                    repoId,
                    repoNames.GetValueOrDefault(repoId),
                    p.Relation,
                    p.Tier,
                    run is null
                        ? null
                        : new ProducerRunDto(
                            run.RunId.ToString(), run.Status, run.StartUtc, run.EndUtc, run.RowsLoaded),
                    schedulesByPipeline.GetValueOrDefault(p.PipelineId, []));
            })
            .OrderBy(p => p.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return TypedResults.Ok(new ObjectRefreshDto(
            identity.Key, identity.Name, identity.Kind, producerDtos, viaModules));
    }

    /// <summary>
    /// The estate's data subscribers: every report, workbook, notebook, and application declared as consuming
    /// the warehouse, with how much of it each one reads. Filterable by <c>type</c> (the consuming tool) and by
    /// a free-text <c>search</c> over the name and owner, which is how a person actually looks a dashboard up.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<SubscriberDto>>> ListSubscribersAsync(
        CatalogDbContext db, string? type, string? search, CancellationToken ct)
    {
        var query = db.Subscribers.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(s => s.Type == type);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(s => s.Name.Contains(term)
                || (s.Owner != null && s.Owner.Contains(term))
                || (s.Description != null && s.Description.Contains(term))
                || (s.Notes != null && s.Notes.Contains(term))
                || (s.Url != null && s.Url.Contains(term)));
        }

        var rows = await query
            .OrderBy(s => s.Name)
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return TypedResults.Ok<IReadOnlyList<SubscriberDto>>([]);
        }

        var keys = rows.Select(s => s.ObjectKey).ToList();
        var queryCounts = (await db.SubscriberQueries.AsNoTracking()
                .Where(q => keys.Contains(q.SubscriberKey))
                .GroupBy(q => q.SubscriberKey)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(x => x.Key, x => x.Count, StringComparer.Ordinal);

        // How much of the warehouse each subscriber touches comes from the EDGES, not from the stored query
        // text: the edges are what the parser actually resolved, deduplicated across a subscriber's queries.
        var objectCounts = (await db.LineageEdges.AsNoTracking()
                .Where(e => e.Flow == null && e.ViaModule != null && keys.Contains(e.ViaModule))
                .Select(e => new { Key = e.ViaModule!, e.ObjectKey })
                .Distinct()
                .GroupBy(x => x.Key)
                .Select(g => new { Key = g.Key, Count = g.Count() })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(x => x.Key, x => x.Count, StringComparer.Ordinal);

        var result = rows
            .Select(s => new SubscriberDto(
                s.ObjectKey, s.Name, s.Type, s.Owner, s.Description, s.Notes, s.Url, s.RepoId, s.File,
                queryCounts.GetValueOrDefault(s.ObjectKey),
                objectCounts.GetValueOrDefault(s.ObjectKey),
                s.FirstSeenUtc, s.LastSeenUtc))
            .ToList();
        return TypedResults.Ok<IReadOnlyList<SubscriberDto>>(result);
    }

    /// <summary>
    /// Everything one subscriber consumes: its queries, and every warehouse object those queries read, named and
    /// located from the global object registry. The per-object query list comes from the stored per-query
    /// breakdown, so the answer to "why does this report depend on that table" is the query that says so.
    /// </summary>
    private static async Task<Results<Ok<SubscriberDossierDto>, ProblemHttpResult>> GetSubscriberDossierAsync(
        string key, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.Subscribers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ObjectKey == key, ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound("subscriber", key);
        }

        var queryRows = await db.SubscriberQueries.AsNoTracking()
            .Where(q => q.SubscriberKey == key)
            .OrderBy(q => q.Ordinal)
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);

        var queries = queryRows
            .Select(q => new SubscriberQueryDto(q.Ordinal, q.Name, q.ServerRef, q.Sql, SplitKeys(q.ObjectKeys)))
            .ToList();

        // The object side: every key any of the queries read, with the queries that read it. Built from the
        // per-query breakdown so a table read by three of a report's datasets says so.
        var queriesByObject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var query in queries)
        {
            foreach (var objectKey in query.ObjectKeys)
            {
                if (!queriesByObject.TryGetValue(objectKey, out var names))
                {
                    names = [];
                    queriesByObject[objectKey] = names;
                }

                if (!names.Contains(query.Name, StringComparer.Ordinal))
                {
                    names.Add(query.Name);
                }
            }
        }

        var levels = (await db.Objects.AsNoTracking()
                .Where(o => queriesByObject.Keys.Contains(o.Key))
                .Select(o => new { o.Key, o.Level })
                .ToListAsync(ct).ConfigureAwait(false))
            .ToDictionary(x => x.Key, x => x.Level, StringComparer.Ordinal);
        var locations = await LoadObjectLocationsAsync(db, queriesByObject.Keys.ToList(), ct).ConfigureAwait(false);

        var objects = queriesByObject
            .Select(entry =>
            {
                var found = locations.TryGetValue(entry.Key, out var location);
                return new SubscriberObjectDto(
                    entry.Key, location.Database, location.Schema,
                    found ? location.Name : entry.Key,
                    location.Kind ?? "Unknown",
                    levels.GetValueOrDefault(entry.Key),
                    entry.Value);
            })
            .OrderBy(o => o.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var subscriber = new SubscriberDto(
            row.ObjectKey, row.Name, row.Type, row.Owner, row.Description, row.Notes, row.Url, row.RepoId, row.File,
            queries.Count, objects.Count, row.FirstSeenUtc, row.LastSeenUtc);
        return TypedResults.Ok(new SubscriberDossierDto(subscriber, queries, objects));
    }

    /// <summary>Splits a stored newline-joined object-key list back into its keys, dropping blanks (a query that
    /// resolved nothing stores an empty string).</summary>
    private static IReadOnlyList<string> SplitKeys(string joined)
        => string.IsNullOrEmpty(joined)
            ? []
            : joined.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The subscribers consuming one object, for its dossier: the read edges attributed to a subscriber
    /// node, joined to the consumer rows and to the individual queries that name the object.</summary>
    private static async Task<IReadOnlyList<ObjectSubscriberDto>> LoadObjectSubscribersAsync(
        CatalogDbContext db, string objectKey, CancellationToken ct)
    {
        // A subscriber's facts are module-attributed (no flow), so its edges are exactly the flowless ones whose
        // ViaModule is a known subscriber key.
        var moduleKeys = await db.LineageEdges.AsNoTracking()
            .Where(e => e.ObjectKey == objectKey && e.Flow == null && e.ViaModule != null)
            .Select(e => e.ViaModule!)
            .Distinct()
            .ToListAsync(ct).ConfigureAwait(false);
        if (moduleKeys.Count == 0)
        {
            return [];
        }

        var rows = await db.Subscribers.AsNoTracking()
            .Where(s => moduleKeys.Contains(s.ObjectKey))
            .OrderBy(s => s.Name)
            .Take(MaxDossierRows)
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            return [];
        }

        var keys = rows.Select(s => s.ObjectKey).ToList();
        var queryRows = await db.SubscriberQueries.AsNoTracking()
            .Where(q => keys.Contains(q.SubscriberKey))
            .OrderBy(q => q.Ordinal)
            .Select(q => new { q.SubscriberKey, q.Name, q.ObjectKeys })
            .ToListAsync(ct).ConfigureAwait(false);

        var namingQueries = queryRows
            .Where(q => SplitKeys(q.ObjectKeys).Contains(objectKey, StringComparer.Ordinal))
            .GroupBy(q => q.SubscriberKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(q => q.Name).ToList(), StringComparer.Ordinal);

        return rows
            .Select(s => new ObjectSubscriberDto(
                s.ObjectKey, s.Name, s.Type, s.Owner, s.Description, s.Notes, s.Url,
                namingQueries.GetValueOrDefault(s.ObjectKey, [])))
            .ToList();
    }

    /// <summary>Ranks a relationship tier string for the dossier's cross-repo dedupe: Derived is the live
    /// database's view, Observed a run's, Declared the author's, mirroring <see cref="Core.Lineage.LineageTier"/>.</summary>
    private static int TierRank(string tier) => tier switch
    {
        "Derived" => 2,
        "Observed" => 1,
        _ => 0,
    };

    /// <summary>One deduplicated data-model relationship while the dossier folds the per-repo rows.</summary>
    private sealed record RelationshipAggregate(
        string? Name, string FromObjectKey, string FromColumns, string ToObjectKey, string ToColumns,
        string Origin, string Tier, int Occurrences, string Operators, string JoinTypes);

    private static async Task<Results<Ok<PagedResult<EdgeDto>>, ProblemHttpResult>> ListEdgesAsync(
        Guid repoId, CatalogDbContext db, Guid? pipelineId, string? objectKey, string? relation, string? tier,
        int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.LineageEdges.AsNoTracking().Where(e => e.RepoId == repoId);
        if (pipelineId is { } pid)
        {
            query = query.Where(e => e.PipelineId == pid);
        }

        if (!string.IsNullOrWhiteSpace(objectKey))
        {
            query = query.Where(e => e.ObjectKey == objectKey);
        }

        if (!string.IsNullOrWhiteSpace(relation))
        {
            query = query.Where(e => e.Relation == relation);
        }

        if (!string.IsNullOrWhiteSpace(tier))
        {
            query = query.Where(e => e.Tier == tier);
        }

        var ordered = query.OrderBy(e => e.ObjectName).ThenBy(e => e.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        // Left-join the global object registry for each edge's database/schema (proper-cased, unlike the
        // normalized key), so graph clients can label objects with where they live; an edge whose object row
        // is missing (a race with identity healing) still returns, with nulls.
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .GroupJoin(db.Objects.AsNoTracking(), e => e.ObjectKey, o => o.Key, (e, objects) => new { e, objects })
            .SelectMany(x => x.objects.DefaultIfEmpty(), (x, o) => new EdgeDto(
                x.e.Id, x.e.RepoId, x.e.Flow, x.e.PipelineId, x.e.ViaModule, x.e.Relation, x.e.ObjectKey,
                x.e.ObjectName, o != null ? o.Database : null, o != null ? o.Schema : null, x.e.Tier,
                o != null ? o.Kind : null))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<EdgeDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<IReadOnlyList<WaveDto>>, ProblemHttpResult>> GetWavesAsync(
        Guid repoId, CatalogDbContext db, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        // The whole execution plan is returned in one response (no paging): both the wave count and the pipelines
        // within them are bounded by the repo's active pipeline count, which is the same set listed elsewhere, so
        // this cannot grow without bound for a given repo.
        var rows = await db.Pipelines.AsNoTracking()
            .Where(x => x.RepoId == repoId && x.Active)
            .OrderBy(x => x.Wave).ThenBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new { x.Wave, x.Id, x.Name, x.Kind })
            .ToListAsync(ct).ConfigureAwait(false);

        var waves = rows
            .GroupBy(x => x.Wave)
            .Select(g => new WaveDto(
                g.Key,
                g.Select(x => new WavePipelineDto(x.Id, x.Name, x.Kind)).ToList()))
            .ToList();
        return TypedResults.Ok<IReadOnlyList<WaveDto>>(waves);
    }

    private static async Task<Results<Ok<PagedResult<FlowDependencyDto>>, ProblemHttpResult>> GetDependenciesAsync(
        Guid repoId, CatalogDbContext db, Guid? pipelineId, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await RepoExistsAsync(db, repoId, ct).ConfigureAwait(false))
        {
            return NotFound("repo", repoId);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.FlowDependencies.AsNoTracking().Where(d => d.RepoId == repoId);
        // Narrowed to one flow's edges (both directions) when requested: a flow detail view needs "what this flow
        // waits for" and "what it unblocks" without paging through the whole repo's dependency list.
        if (pipelineId is { } pid)
        {
            query = query.Where(d => d.FromPipelineId == pid || d.ToPipelineId == pid);
        }

        // A dense estate can have many flow-to-flow dependencies, so this list is paged; ordered by the flow pair,
        // then row id as a stable secondary key so a page boundary is deterministic.
        var ordered = query.OrderBy(d => d.FromFlow).ThenBy(d => d.ToFlow).ThenBy(d => d.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var deps = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(d => new FlowDependencyDto(
                d.Id, d.RepoId, d.FromFlow, d.ToFlow, d.FromPipelineId, d.ToPipelineId, d.ViaObjects))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<FlowDependencyDto>(deps, p, size, total));
    }

    /// <summary>Every selectable project across all repos for the lineage graph's scope picker, each with its repo
    /// (name + id) and active-flow count, sorted by repo then project. The project is derived per flow from its
    /// repo-relative path (see <see cref="ProjectPath"/>) and reduced in memory: the distinct (repo, project) pairs
    /// are bounded by the estate's folder count, not its flow count, so the whole list is returned unpaged.</summary>
    private static async Task<Ok<IReadOnlyList<LineageProjectDto>>> ListProjectsAsync(
        CatalogDbContext db, CancellationToken ct)
    {
        var rows = await db.Pipelines.AsNoTracking()
            .Where(p => p.Active)
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.RepoId, RepoName = r.Name, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var projects = rows
            .GroupBy(x => new { x.RepoId, x.RepoName, Project = ProjectPath.Of(x.RelativePath) })
            .Select(g => new LineageProjectDto(g.Key.RepoId, g.Key.RepoName, g.Key.Project, g.Count()))
            .OrderBy(x => x.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Project, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<LineageProjectDto>>(projects);
    }

    // The closure's guard rails: how far downstream a walk runs by default and at most, and the most pipeline nodes
    // one response carries. The frontier lets a client push past these deliberately (by expanding a node) rather
    // than dumping the whole estate at once, which is the readability problem the project scope exists to solve.
    private const int DefaultGraphDepth = 3;
    private const int MaxGraphDepth = 12;
    private const int MaxGraphPipelines = 500;

    /// <summary>One lineage fact reduced to what the closure walk needs plus what it must echo back as an
    /// <see cref="EdgeDto"/>; the object's database/schema are attached afterward from the global registry.</summary>
    private sealed record EdgeRow(
        long Id, Guid RepoId, string? Flow, Guid? PipelineId, string? ViaModule, string Relation,
        string ObjectKey, string ObjectName, string Tier);

    /// <summary>
    /// A project's lineage as a single cross-repo subgraph. The seed is the selected project's active flows (the base
    /// objects they read and the tables they write); from there the walk follows data strictly downstream, crossing
    /// repo boundaries freely because objects are global and an object's origin repo does not gate how data flows
    /// through it. The walk is bounded two ways so a widely-consumed project cannot return the whole estate: a hop
    /// depth (<paramref name="depth"/>, clamped) and a node cap. Objects at the depth boundary that still have
    /// un-included consumers are returned as the <c>Frontier</c>, and <paramref name="expand"/> re-seeds the walk from
    /// specific nodes (an object key or a pipeline id) so a client can push past the boundary node by node.
    /// </summary>
    private static async Task<Results<Ok<ProjectGraphDto>, ProblemHttpResult>> GetProjectGraphAsync(
        CatalogDbContext db, Guid? repoId, string? project, int? depth, string[]? expand, CancellationToken ct)
    {
        // The seed is either a project (a repo-root folder) or, for a deep-link onto a specific node, one or more
        // expand tokens. A GUID token is a pipeline re-seeded at depth 0; any other token is an object key whose
        // producers (its immediate upstream) and consumers (downstream) both re-enter the walk, so a jumped-to object
        // shows where it comes from and where it goes without a separate whole-repo drawing path.
        var expandPipelineIds = new HashSet<Guid>();
        var expandObjectKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in expand ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            if (Guid.TryParse(token, out var pid))
            {
                expandPipelineIds.Add(pid);
            }
            else
            {
                expandObjectKeys.Add(token);
            }
        }

        var hasProject = !string.IsNullOrWhiteSpace(project);
        var hasExpand = expandPipelineIds.Count > 0 || expandObjectKeys.Count > 0;

        if (repoId is { } checkRepo && !await RepoExistsAsync(db, checkRepo, ct).ConfigureAwait(false))
        {
            return NotFound("repo", checkRepo);
        }

        if (!hasProject && !hasExpand)
        {
            return TypedResults.Problem(
                detail: "Provide a 'project' (a repo-root folder) or an 'expand' node to seed the lineage graph.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "No seed");
        }

        if (hasProject && repoId is null)
        {
            return TypedResults.Problem(
                detail: "A 'repoId' is required with a 'project': a project is a folder within one repo.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Missing repoId");
        }

        var maxDepth = Math.Clamp(depth ?? DefaultGraphDepth, 1, MaxGraphDepth);

        // Seed pipelines: the selected project's active flows in its repo. The project is derived in memory (a path
        // split SQL should not carry), so only this repo's (id, path) pairs are pulled, not the whole table.
        var seedPipelineIds = new HashSet<Guid>();
        if (hasProject)
        {
            var repoPipelines = await db.Pipelines.AsNoTracking()
                .Where(p => p.RepoId == repoId!.Value && p.Active)
                .Select(p => new { p.Id, p.RelativePath })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var p in repoPipelines)
            {
                if (ProjectPath.Of(p.RelativePath) == project)
                {
                    seedPipelineIds.Add(p.Id);
                }
            }
        }

        if (seedPipelineIds.Count == 0 && expandPipelineIds.Count == 0 && expandObjectKeys.Count == 0)
        {
            return TypedResults.Ok(new ProjectGraphDto(
                Array.Empty<ProjectGraphPipelineDto>(), Array.Empty<EdgeDto>(), Array.Empty<string>(), false,
                Array.Empty<ProjectGraphObjectDto>(), Array.Empty<ProjectGraphDrawEdgeDto>(),
                Array.Empty<ProjectGraphDrawEdgeDto>()));
        }

        // The whole estate's edges, once, minimally projected. This is the universal graph the walk runs over; it is
        // bounded by the edge count (the same set the per-repo edges endpoint pages through, summed across repos) and
        // reduced to in-memory adjacency below so the traversal itself hits no database.
        var edgeRows = await db.LineageEdges.AsNoTracking()
            .Select(e => new EdgeRow(
                e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName, e.Tier))
            .ToListAsync(ct).ConfigureAwait(false);

        var writesByPipeline = new Dictionary<Guid, List<string>>();
        var readsByPipeline = new Dictionary<Guid, List<string>>();
        var consumersByObject = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        var producersByObject = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        // A subscriber's reads are attributed via ViaModule, never ObjectKey (a subscriber is not a data object
        // and carries no PipelineId), so they fall outside the pipeline-keyed dictionaries above. Indexed
        // separately by subscriber key so an 'expand' seeded on a subscriber node (a graph-search hit) can find
        // what it reads, mirroring how a normal object's producers/consumers are looked up below.
        var readsBySubscriber = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var e in edgeRows)
        {
            if (e.PipelineId is not { } pid)
            {
                if (e.Flow is null && e.ViaModule is not null && e.Relation == "Reads"
                    && e.ViaModule.StartsWith(SubscriberKeyPrefix, StringComparison.Ordinal))
                {
                    (readsBySubscriber.TryGetValue(e.ViaModule, out var sr)
                        ? sr : readsBySubscriber[e.ViaModule] = new List<string>()).Add(e.ObjectKey);
                }

                continue;
            }

            if (e.Relation is "Writes" or "Creates")
            {
                (writesByPipeline.TryGetValue(pid, out var w) ? w : writesByPipeline[pid] = new List<string>())
                    .Add(e.ObjectKey);
                (producersByObject.TryGetValue(e.ObjectKey, out var pr)
                    ? pr : producersByObject[e.ObjectKey] = new List<Guid>()).Add(pid);
            }
            else if (e.Relation == "Reads")
            {
                // A read makes the flow a consumer of that object (the downstream link) and records the object as
                // one of the flow's source inputs (shown as a base node, but never walked further upstream).
                (readsByPipeline.TryGetValue(pid, out var r) ? r : readsByPipeline[pid] = new List<string>())
                    .Add(e.ObjectKey);
                (consumersByObject.TryGetValue(e.ObjectKey, out var c)
                    ? c : consumersByObject[e.ObjectKey] = new List<Guid>()).Add(pid);
            }
        }

        var pipelineDepth = new Dictionary<Guid, int>();
        var includedObjects = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Guid Pipeline, int Depth)>();
        var truncated = false;

        bool TryEnqueue(Guid pid, int d)
        {
            if (pipelineDepth.TryGetValue(pid, out var existing))
            {
                if (existing <= d)
                {
                    return true;
                }

                pipelineDepth[pid] = d;
                queue.Enqueue((pid, d));
                return true;
            }

            if (pipelineDepth.Count >= MaxGraphPipelines)
            {
                truncated = true;
                return false;
            }

            pipelineDepth[pid] = d;
            queue.Enqueue((pid, d));
            return true;
        }

        foreach (var pid in seedPipelineIds)
        {
            TryEnqueue(pid, 0);
        }

        foreach (var pid in expandPipelineIds)
        {
            TryEnqueue(pid, 0);
        }

        foreach (var key in expandObjectKeys)
        {
            // A subscriber node is not a data object: it has no producers/consumers of its own, only queries
            // that READ other objects (attributed via ViaModule). Expanding one must resolve THOSE read targets
            // and pull each one's producer in, or the walk finds nothing and the graph renders empty, exactly as
            // if the subscriber had no lineage at all.
            if (key.StartsWith(SubscriberKeyPrefix, StringComparison.Ordinal))
            {
                if (readsBySubscriber.TryGetValue(key, out var readObjects))
                {
                    foreach (var objectKey in readObjects)
                    {
                        includedObjects.Add(objectKey);
                        if (producersByObject.TryGetValue(objectKey, out var readObjectProducers))
                        {
                            foreach (var p in readObjectProducers)
                            {
                                TryEnqueue(p, 0);
                            }
                        }
                    }
                }

                continue;
            }

            includedObjects.Add(key);
            // Its producer(s) sit one hop upstream (seeded at depth 0 so their own sources come in too); its
            // consumer(s) one hop downstream. Seeding both makes a jumped-to object show its full local context and,
            // for a frontier expand, simply continues the walk past it (the producer is already included, a no-op).
            if (producersByObject.TryGetValue(key, out var producers))
            {
                foreach (var p in producers)
                {
                    TryEnqueue(p, 0);
                }
            }

            if (consumersByObject.TryGetValue(key, out var consumers))
            {
                foreach (var c in consumers)
                {
                    TryEnqueue(c, 1);
                }
            }
        }

        while (queue.Count > 0)
        {
            var (pid, d) = queue.Dequeue();
            if (pipelineDepth[pid] < d)
            {
                // A shorter path to this pipeline was found after it was queued; the shallower visit does the work.
                continue;
            }

            if (readsByPipeline.TryGetValue(pid, out var reads))
            {
                foreach (var key in reads)
                {
                    includedObjects.Add(key);
                }
            }

            if (!writesByPipeline.TryGetValue(pid, out var writes))
            {
                continue;
            }

            foreach (var key in writes)
            {
                includedObjects.Add(key);
                if (!consumersByObject.TryGetValue(key, out var consumers) || consumers.Count == 0)
                {
                    continue;
                }

                if (d + 1 > maxDepth)
                {
                    frontier.Add(key);
                    continue;
                }

                foreach (var consumer in consumers)
                {
                    if (!TryEnqueue(consumer, d + 1))
                    {
                        frontier.Add(key);
                    }
                }
            }
        }

        var includedPipes = new HashSet<Guid>(pipelineDepth.Keys);
        var ids = includedPipes.ToList();
        var pipeRows = await db.Pipelines.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Join(db.Repos.AsNoTracking(), p => p.RepoId, r => r.Id,
                (p, r) => new { p.Id, p.Name, p.Kind, p.Wave, p.RepoId, RepoName = r.Name, p.RelativePath })
            .ToListAsync(ct).ConfigureAwait(false);

        var locations = await LoadObjectLocationsAsync(db, includedObjects, ct).ConfigureAwait(false);

        // ---- Lineage completeness, decided from the dataset itself. A flow that EXECUTES a module (Requires) or
        // READS a view whose body was never harvested has unknown reads/writes: the graph would confidently draw
        // it edgeless (or source-less) when the truth is "not derived yet". A module counts as harvested when any
        // edge row cites it as a via-module OR the registry stored its definition (a harvested body that
        // references no catalog object still proves the harvest ran). The reason travels to the client so the
        // graph can say WHY instead of rendering incompleteness as fact.
        var expandedModules = new HashSet<string>(
            edgeRows.Where(e => e.ViaModule is not null).Select(e => e.ViaModule!), StringComparer.Ordinal);
        var moduleCandidates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in edgeRows)
        {
            if (e.PipelineId is { } pid && includedPipes.Contains(pid) && !expandedModules.Contains(e.ObjectKey)
                && (e.Relation == "Requires"
                    || (e.Relation == "Reads" && locations.TryGetValue(e.ObjectKey, out var l) && l.Kind == "View")))
            {
                moduleCandidates.Add(e.ObjectKey);
            }
        }

        var harvestedDefinitions = new HashSet<string>(StringComparer.Ordinal);
        if (moduleCandidates.Count > 0)
        {
            var candidateList = moduleCandidates.ToList();
            var defined = await db.Objects.AsNoTracking()
                .Where(o => candidateList.Contains(o.Key) && o.Definition != null)
                .Select(o => o.Key)
                .ToListAsync(ct).ConfigureAwait(false);
            harvestedDefinitions.UnionWith(defined);
        }

        var incompleteReasons = new Dictionary<Guid, string>();
        foreach (var e in edgeRows)
        {
            if (e.PipelineId is not { } pid || !includedPipes.Contains(pid) || incompleteReasons.ContainsKey(pid)
                || !moduleCandidates.Contains(e.ObjectKey) || harvestedDefinitions.Contains(e.ObjectKey))
            {
                continue;
            }

            if (e.Relation == "Requires")
            {
                incompleteReasons[pid] =
                    $"The body of '{e.ObjectName}' has not been harvested (connected lineage has not reached its server), so this flow's reads and writes are unknown.";
            }
            else if (e.Relation == "Reads")
            {
                incompleteReasons[pid] =
                    $"The body of view '{e.ObjectName}' has not been harvested (connected lineage has not reached its server), so its source tables are unknown.";
            }
        }

        var pipelines = pipeRows
            .Select(p => new ProjectGraphPipelineDto(
                p.Id, p.Name, p.Kind, p.Wave, p.RepoId, p.RepoName, p.RelativePath,
                seedPipelineIds.Contains(p.Id), pipelineDepth[p.Id],
                !incompleteReasons.ContainsKey(p.Id), incompleteReasons.GetValueOrDefault(p.Id)))
            .OrderBy(p => p.Depth)
            .ThenBy(p => p.RepoName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        var includedEdges = edgeRows
            .Where(e => includedObjects.Contains(e.ObjectKey)
                && (e.PipelineId is null || includedPipes.Contains(e.PipelineId.Value)))
            .ToList();
        var edges = includedEdges
            .Select(e =>
            {
                locations.TryGetValue(e.ObjectKey, out var loc);
                return new EdgeDto(
                    e.Id, e.RepoId, e.Flow, e.PipelineId, e.ViaModule, e.Relation, e.ObjectKey, e.ObjectName,
                    loc.Database, loc.Schema, e.Tier, loc.Kind);
            })
            .ToList();

        // Only report a frontier object that genuinely has a consumer we left out; an object can be marked while a
        // shorter path still pulled all its consumers in, and that is not something to offer expanding.
        var openFrontier = frontier
            .Where(key => consumersByObject.TryGetValue(key, out var consumers)
                && consumers.Any(c => !includedPipes.Contains(c)))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        // The consumption side of the drawable graph. A subscriber is referenced as a ViaModule, never as an
        // object key, so it is absent from the registry lookup above and needs its display name fetched here;
        // without it the canvas would draw the case-folded node key at the end of the chain.
        var subscriberKeys = includedEdges
            .Where(e => e.Flow is null && e.ViaModule is not null
                && e.ViaModule.StartsWith(SubscriberKeyPrefix, StringComparison.Ordinal))
            .Select(e => e.ViaModule!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var subscriberNames = new Dictionary<string, string>(StringComparer.Ordinal);
        if (subscriberKeys.Count > 0)
        {
            var named = await db.Subscribers.AsNoTracking()
                .Where(s => subscriberKeys.Contains(s.ObjectKey))
                .Select(s => new { s.ObjectKey, s.Name })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in named)
            {
                subscriberNames.TryAdd(row.ObjectKey, row.Name);
            }
        }

        var (objects, flowGraph, objectGraph) = DeriveDrawableGraph(
            includedEdges, includedObjects, includedPipes, locations,
            pipeRows.ToDictionary(p => p.Id, p => p.Name), openFrontier, subscriberNames);

        return TypedResults.Ok(new ProjectGraphDto(
            pipelines, edges, openFrontier, truncated, objects, flowGraph, objectGraph));
    }

    /// <summary>
    /// Derives the DRAWABLE project graph from the included fact rows: the single, server-side interpretation
    /// every renderer consumes verbatim (the GUI lays out and paints; it re-derives nothing). Rules: a flow's
    /// <c>Writes</c>/<c>Creates</c> draw flow-to-object, its <c>Reads</c> object-to-flow; a module whose body
    /// reads base objects is a VIEW when a flow maintains it (the generated transform view) or the registry
    /// knows it as one (a DB-managed fact/dim or compatibility view), and its data path draws base-to-view, so
    /// a view is never wired to the file its maintaining flow read; a <c>Requires</c> (an executed procedure)
    /// is a code dependency, not data movement, and draws nothing. The objects view composes the same facts as
    /// object-to-object movement per flow. A data subscriber is a module whose body reads but which no flow
    /// maintains, so it draws base-to-subscriber and terminates the chain: the graph ends where the data is
    /// actually consumed, not at the last table SQLFlow writes. Self-edges never draw; the first spelling of a
    /// duplicate wins.
    /// </summary>
    private static (List<ProjectGraphObjectDto> Objects, List<ProjectGraphDrawEdgeDto> FlowGraph, List<ProjectGraphDrawEdgeDto> ObjectGraph) DeriveDrawableGraph(
        IReadOnlyList<EdgeRow> includedEdges,
        IReadOnlyCollection<string> includedObjects,
        IReadOnlyCollection<Guid> includedPipes,
        IReadOnlyDictionary<string, (string? Database, string? Schema, string Name, string? Kind)> locations,
        IReadOnlyDictionary<Guid, string> pipelineNameById,
        IReadOnlyList<string> openFrontier,
        IReadOnlyDictionary<string, string> subscriberNames)
    {
        var frontierSet = new HashSet<string>(openFrontier, StringComparer.Ordinal);
        var nameByKey = new Dictionary<string, string>(StringComparer.Ordinal);
        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);
        var writeOwner = new Dictionary<string, Guid>();
        var moduleReads = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var byPipeline = new Dictionary<Guid, (List<EdgeRow> Reads, List<EdgeRow> Writes)>();
        foreach (var e in includedEdges)
        {
            nameByKey.TryAdd(e.ObjectKey, e.ObjectName);
            if (e.PipelineId is { } pid)
            {
                if (!byPipeline.TryGetValue(pid, out var group))
                {
                    group = (new List<EdgeRow>(), new List<EdgeRow>());
                    byPipeline[pid] = group;
                }

                if (e.Relation == "Reads")
                {
                    group.Reads.Add(e);
                }
                else if (e.Relation is "Writes" or "Creates")
                {
                    group.Writes.Add(e);
                    writtenKeys.Add(e.ObjectKey);
                    writeOwner.TryAdd(e.ObjectKey, pid);
                }
            }
            else if (e.ViaModule is not null && e.Relation == "Reads")
            {
                (moduleReads.TryGetValue(e.ViaModule, out var bases)
                    ? bases : moduleReads[e.ViaModule] = new HashSet<string>(StringComparer.Ordinal)).Add(e.ObjectKey);
            }
        }

        // A module with body reads is a view worth wiring when a flow maintains it OR the registry kind says
        // View; an unwritten module with no known kind could be a procedure, which draws nothing. A subscriber
        // is a module too (its queries read), but never a view: it is drawn as the consuming leaf instead.
        var subscriberKeys = moduleReads.Keys
            .Where(subscriberNames.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);
        var viewKeys = moduleReads.Keys
            .Where(key => !subscriberKeys.Contains(key))
            .Where(key => writtenKeys.Contains(key)
                || (locations.TryGetValue(key, out var l) && string.Equals(l.Kind, "View", StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.Ordinal);

        string KindOf(string key)
        {
            if (subscriberKeys.Contains(key))
            {
                return "subscriber";
            }

            if (key.StartsWith("file|", StringComparison.Ordinal))
            {
                // A "file" node whose identity is a remote endpoint is the acquisition's external SOURCE, not a
                // lake file: caption it by what it is so the graph reads "api -> flow -> file -> table".
                var name = key[(key.LastIndexOf('|') + 1)..];
                if (name.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return "api";
                }

                return name.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase) ? "sftp" : "file";
            }

            if (locations.TryGetValue(key, out var l) && !string.IsNullOrEmpty(l.Kind)
                && !string.Equals(l.Kind, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                return l.Kind!.ToLowerInvariant();
            }

            return viewKeys.Contains(key) ? "view" : "table";
        }

        var includedSet = includedObjects as ISet<string> ?? new HashSet<string>(includedObjects, StringComparer.Ordinal);

        // A subscriber node is drawn only when something it reads is actually on the canvas; otherwise it would
        // float unattached in a project it consumes nothing from.
        var drawnSubscribers = subscriberKeys
            .Where(key => moduleReads[key].Any(includedSet.Contains))
            .ToHashSet(StringComparer.Ordinal);

        var objects = includedObjects.Concat(drawnSubscribers)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key =>
            {
                locations.TryGetValue(key, out var loc);
                var location = key.StartsWith("file|", StringComparison.Ordinal) || drawnSubscribers.Contains(key)
                    ? null
                    : string.Join(".", new[] { loc.Database, loc.Schema }.Where(part => !string.IsNullOrEmpty(part)));
                return new ProjectGraphObjectDto(
                    key,
                    subscriberNames.GetValueOrDefault(key) ?? loc.Name ?? nameByKey.GetValueOrDefault(key, key),
                    KindOf(key),
                    string.IsNullOrEmpty(location) ? null : location,
                    frontierSet.Contains(key));
            })
            .ToList();

        // ---- Flows view: flow-to-object movement plus base-to-view derivation. ------------------------------
        var flowGraph = new List<ProjectGraphDrawEdgeDto>();
        var seenFlow = new HashSet<(string, string)>();
        void AddFlowEdge(string source, string target, string label, Guid? pid)
        {
            if (source != target && seenFlow.Add((source, target)))
            {
                flowGraph.Add(new ProjectGraphDrawEdgeDto(source, target, label, pid));
            }
        }

        // ---- Objects view: object-to-object movement per flow plus the same view derivation. ----------------
        var objectGraph = new List<ProjectGraphDrawEdgeDto>();
        var seenObject = new HashSet<(string, string, string)>();
        void AddObjectEdge(string source, string target, string label, Guid? pid)
        {
            if (source != target && seenObject.Add((source, target, label)))
            {
                objectGraph.Add(new ProjectGraphDrawEdgeDto(source, target, label, pid));
            }
        }

        foreach (var pid in includedPipes.OrderBy(id => id))
        {
            if (!byPipeline.TryGetValue(pid, out var group))
            {
                continue;
            }

            var flowName = pipelineNameById.GetValueOrDefault(pid, pid.ToString());
            foreach (var write in group.Writes)
            {
                if (!viewKeys.Contains(write.ObjectKey))
                {
                    AddFlowEdge(pid.ToString(), write.ObjectKey, write.Relation == "Creates" ? "creates" : "writes", pid);
                }
            }

            foreach (var read in group.Reads)
            {
                AddFlowEdge(read.ObjectKey, pid.ToString(), "reads", pid);
                foreach (var write in group.Writes)
                {
                    if (!viewKeys.Contains(write.ObjectKey))
                    {
                        AddObjectEdge(read.ObjectKey, write.ObjectKey, flowName, pid);
                    }
                }
            }
        }

        foreach (var viewKey in viewKeys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!includedSet.Contains(viewKey))
            {
                continue;
            }

            Guid? owner = writeOwner.TryGetValue(viewKey, out var pid) ? pid : null;
            var label = owner is { } o ? pipelineNameById.GetValueOrDefault(o, "view") : "view";
            foreach (var baseKey in moduleReads[viewKey].OrderBy(key => key, StringComparer.Ordinal))
            {
                if (includedSet.Contains(baseKey))
                {
                    AddFlowEdge(baseKey, viewKey, "view", owner);
                    AddObjectEdge(baseKey, viewKey, label, owner);
                }
            }
        }

        // The consuming leaf: every object a drawn subscriber reads points at it. No pipeline maintains a
        // subscriber, so the edge carries no pipeline id and takes no per-flow colour.
        foreach (var subscriberKey in drawnSubscribers.OrderBy(key => key, StringComparer.Ordinal))
        {
            var label = subscriberNames.GetValueOrDefault(subscriberKey) ?? "consumes";
            foreach (var baseKey in moduleReads[subscriberKey].OrderBy(key => key, StringComparer.Ordinal))
            {
                if (includedSet.Contains(baseKey))
                {
                    AddFlowEdge(baseKey, subscriberKey, "consumed by", null);
                    AddObjectEdge(baseKey, subscriberKey, label, null);
                }
            }
        }

        return (objects, flowGraph, objectGraph);
    }

    /// <summary>The database/schema/name/kind for a set of object keys from the global registry, chunked so the
    /// IN-list never exceeds the provider's parameter limit. Keys the registry does not know (a race with identity
    /// healing) are simply absent, and the caller falls back to nulls, exactly as the edges endpoint does.</summary>
    private static async Task<Dictionary<string, (string? Database, string? Schema, string Name, string? Kind)>> LoadObjectLocationsAsync(
        CatalogDbContext db, IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        var result = new Dictionary<string, (string?, string?, string, string?)>(StringComparer.Ordinal);
        const int chunk = 1000;
        var all = keys.ToArray();
        for (var i = 0; i < all.Length; i += chunk)
        {
            var slice = all.Skip(i).Take(chunk).ToList();
            var rows = await db.Objects.AsNoTracking()
                .Where(o => slice.Contains(o.Key))
                .Select(o => new { o.Key, o.Database, o.Schema, o.Name, o.Kind })
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var row in rows)
            {
                result[row.Key] = (row.Database, row.Schema, row.Name, row.Kind);
            }
        }

        return result;
    }

    private static Task<bool> RepoExistsAsync(CatalogDbContext db, Guid repoId, CancellationToken ct)
        => db.Repos.AsNoTracking().AnyAsync(r => r.Id == repoId, ct);

    private static ProblemHttpResult NotFound(string resource, string key)
        => TypedResults.Problem(
            detail: $"No {resource} with key '{key}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");
    /// <summary>The deepest join chain the search will assemble. Beyond three hops a "join path" stops being
    /// advice and starts being a suggestion to build a query nobody should write by hand.</summary>
    private const int MaxJoinHops = 4;

    private const int DefaultJoinHops = 2;

    /// <summary>The most alternatives returned. Enough to show the real choices, bounded so a hub table with
    /// hundreds of relationships cannot return a payload nothing can read.</summary>
    private const int MaxJoinPaths = 50;

    /// <summary>The most objects the walk will expand. A hub dimension joins to a great deal.</summary>
    private const int MaxJoinFrontier = 400;

    /// <summary>
    /// Answers "how can I join this table, and what are the alternatives": a breadth-first walk of the
    /// interpreted relationship graph from one object, returning each distinct route as an ordered list of
    /// hops with a ready-to-paste ON clause per hop.
    ///
    /// Breadth-first matters here: the shortest route to a target is found first, and a direct join is always
    /// preferred over a chain through a bridge table. Several routes to the SAME target are kept rather than
    /// deduplicated, because two tables joined on different column sets in different parts of the codebase is
    /// exactly the ambiguity a caller needs to see and resolve, not something to silently pick a winner for.
    /// </summary>
    private static async Task<Results<Ok<JoinPathsDto>, ProblemHttpResult>> GetJoinPathsAsync(
        string key, CatalogDbContext db, string? target, int? maxHops, CancellationToken ct)
    {
        var origin = await db.Objects.AsNoTracking().Where(o => o.Key == key)
            .Select(o => new { o.Key, o.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (origin is null)
        {
            return TypedResults.Problem(
                detail: $"No lineage object has the key '{key}'.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var hopBudget = Math.Clamp(maxHops ?? DefaultJoinHops, 1, MaxJoinHops);
        var wanted = string.IsNullOrWhiteSpace(target) ? null : target.Trim();

        // Names for rendering, filled in as objects are discovered.
        var names = new Dictionary<string, (string Name, string? Database, string? Schema)>(StringComparer.Ordinal)
        {
            [origin.Key] = (origin.Name, null, null),
        };

        var paths = new List<JoinPathDto>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { origin.Key };
        var frontier = new List<(string Key, List<JoinHopDto> Hops)> { (origin.Key, []) };
        var truncated = false;

        for (var hop = 0; hop < hopBudget && frontier.Count > 0 && paths.Count < MaxJoinPaths; hop++)
        {
            var frontierKeys = frontier.Select(f => f.Key).Distinct(StringComparer.Ordinal).ToList();
            var relationships = await db.ObjectRelationships.AsNoTracking()
                .Where(r => frontierKeys.Contains(r.FromObjectKey) || frontierKeys.Contains(r.ToObjectKey))
                .OrderByDescending(r => r.Occurrences)
                .Take(MaxJoinFrontier)
                .ToListAsync(ct).ConfigureAwait(false);

            if (relationships.Count == MaxJoinFrontier)
            {
                truncated = true;
            }

            await LoadNamesAsync(db, relationships, names, ct).ConfigureAwait(false);

            var next = new List<(string Key, List<JoinHopDto> Hops)>();
            foreach (var (currentKey, hopsSoFar) in frontier)
            {
                foreach (var relationship in relationships)
                {
                    // A relationship is usable in either direction: joining A to B and B to A are the same
                    // predicate read from opposite ends.
                    var forward = string.Equals(relationship.FromObjectKey, currentKey, StringComparison.Ordinal);
                    var backward = string.Equals(relationship.ToObjectKey, currentKey, StringComparison.Ordinal);
                    if (!forward && !backward)
                    {
                        continue;
                    }

                    var otherKey = forward ? relationship.ToObjectKey : relationship.FromObjectKey;
                    if (string.Equals(otherKey, currentKey, StringComparison.Ordinal))
                    {
                        continue; // a self-relationship is not a route anywhere
                    }

                    if (hopsSoFar.Any(h => string.Equals(h.FromObjectKey, otherKey, StringComparison.Ordinal)))
                    {
                        continue; // never revisit an object already on this chain
                    }

                    var ownColumns = SplitColumns(forward ? relationship.FromColumns : relationship.ToColumns);
                    var otherColumns = SplitColumns(forward ? relationship.ToColumns : relationship.FromColumns);
                    var currentName = names.TryGetValue(currentKey, out var c) ? c.Name : currentKey;
                    var otherName = names.TryGetValue(otherKey, out var o) ? o.Name : otherKey;

                    // Walking the relationship backwards inverts every comparison, so the operators mirror
                    // along with the columns. Rendering "d.ValidFrom >= f.Dato" where the estate wrote
                    // "f.Dato >= d.ValidFrom" would invert a temporal join and silently select the wrong rows.
                    var operators = SplitColumns(relationship.Operators);
                    if (!forward)
                    {
                        operators = operators.Select(MirrorComparison).ToArray();
                    }

                    var hopDto = new JoinHopDto(
                        currentKey, currentName, otherKey, otherName, ownColumns, otherColumns,
                        RenderOn(currentName, ownColumns, otherName, otherColumns, operators),
                        relationship.Origin, relationship.Tier, relationship.Occurrences, relationship.Name,
                        SplitColumns(relationship.JoinTypes), operators.Count > 0);

                    var chain = new List<JoinHopDto>(hopsSoFar) { hopDto };

                    if (wanted is null || Matches(otherKey, otherName, wanted))
                    {
                        var location = names.TryGetValue(otherKey, out var loc) ? loc : (otherName, null, null);
                        paths.Add(new JoinPathDto(
                            otherKey, otherName, location.Database, location.Schema,
                            chain.Count, chain.Min(h => h.Occurrences), chain.Count == 1, chain));

                        if (paths.Count >= MaxJoinPaths)
                        {
                            truncated = true;
                            break;
                        }
                    }

                    // Keep walking through an object even after recording a route to it: a longer chain can
                    // still be the only way to reach something further out.
                    if (visited.Add(otherKey))
                    {
                        next.Add((otherKey, chain));
                    }
                }

                if (paths.Count >= MaxJoinPaths)
                {
                    break;
                }
            }

            frontier = next;
        }

        // Best first: fewest hops, then the strongest weakest-link, then a declared constraint over an
        // inferred join, so the route a caller should reach for is the one they read first.
        var ranked = paths
            .OrderBy(p => p.HopCount)
            .ThenByDescending(p => p.MinOccurrences)
            .ThenByDescending(p => p.Hops.Any(h => string.Equals(h.Origin, "Constraint", StringComparison.OrdinalIgnoreCase)))
            .ThenBy(p => p.TargetName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var note = ranked.Count switch
        {
            0 when wanted is not null =>
                $"No join path was found from {origin.Name} to '{wanted}' within {hopBudget} hop(s). Raise " +
                "maxHops, or accept that the codebase does not relate them: do NOT invent a join condition " +
                "from matching column names.",
            0 =>
                $"No relationship is recorded for {origin.Name} at all. Either nothing in the codebase joins " +
                "it, or the code that does has not been synced.",
            _ =>
                "Ordered best first: fewest hops, then the weakest link's occurrence count, then a declared " +
                "constraint over an inferred join. Several routes to the same target mean the codebase joins " +
                "those tables on more than one column set; choose deliberately rather than taking the first.",
        };

        return TypedResults.Ok(new JoinPathsDto(
            origin.Key, origin.Name, wanted, hopBudget, ranked.Count, truncated, ranked, note));
    }

    /// <summary>Loads the display identity of every object a relationship batch touches, in one round trip.</summary>
    private static async Task LoadNamesAsync(
        CatalogDbContext db, IReadOnlyList<CatalogObjectRelationship> relationships,
        Dictionary<string, (string Name, string? Database, string? Schema)> names, CancellationToken ct)
    {
        var missing = relationships
            .SelectMany(r => new[] { r.FromObjectKey, r.ToObjectKey })
            .Where(k => !names.ContainsKey(k))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (missing.Count == 0)
        {
            return;
        }

        var found = await db.Objects.AsNoTracking()
            .Where(o => missing.Contains(o.Key))
            .Select(o => new { o.Key, o.Name, o.Database, o.Schema })
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var item in found)
        {
            names[item.Key] = (item.Name, item.Database, item.Schema);
        }
    }

    /// <summary>The same comparison read from the other side: reading "a &gt;= b" from b's end is
    /// "b &lt;= a". Applied whenever a relationship is projected from the opposite direction.</summary>
    private static string MirrorComparison(string comparison) => comparison switch
    {
        ">" => "<",
        ">=" => "<=",
        "<" => ">",
        "<=" => ">=",
        _ => comparison,
    };

    private static IReadOnlyList<string> SplitColumns(string columns)
        => columns.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// The predicate list for one hop, using each side's object name as the alias. An empty operator list is
    /// an equi-join, which is the common case; a range join carries one operator per pair, so a temporal
    /// lookup renders as the interval containment it actually is rather than as a key match. Column counts can
    /// legitimately differ if a relationship was recorded oddly, so the pairing stops at the shorter side
    /// rather than throwing.
    /// </summary>
    private static string RenderOn(
        string leftName, IReadOnlyList<string> leftColumns, string rightName, IReadOnlyList<string> rightColumns,
        IReadOnlyList<string> operators)
        => string.Join(" AND ", leftColumns
            .Zip(rightColumns, (l, r) => (Left: l, Right: r))
            .Select((pair, i) =>
                $"{leftName}.{pair.Left} {(i < operators.Count ? operators[i] : "=")} {rightName}.{pair.Right}"));

    /// <summary>Case-insensitive substring match on either the object's name or its full key, so a caller can
    /// name a target the short way they think of it.</summary>
    private static bool Matches(string objectKey, string objectName, string wanted)
        => objectName.Contains(wanted, StringComparison.OrdinalIgnoreCase)
            || objectKey.Contains(wanted, StringComparison.OrdinalIgnoreCase);
}
