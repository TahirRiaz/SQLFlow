using SqlFlow.Core.Connections;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// A database object a flow of a registered kind reads or writes, declared by the document so the estate scan turns it
/// into a declared lineage fact exactly as it does an ingestion flow's source and target: the server identity comes
/// from <see cref="ConnectionReference"/> by the same rule, so a registered flow reading the table an ingestion flow
/// writes lands on the same node and is ordered after it in waves.
/// </summary>
public sealed record DeclaredDataObject
{
    /// <summary><see cref="LineageRelation.Reads"/> or <see cref="LineageRelation.Writes"/>; the scan refuses any
    /// other relation.</summary>
    public required LineageRelation Relation { get; init; }

    /// <summary>The connection reference (<c>${env:...}</c>, <c>${keyvault:...}</c>, <c>@alias</c>) of the server the
    /// object lives on: the same value the flow's connection declares, never a resolved connection string.</summary>
    public required string ConnectionReference { get; init; }

    /// <summary>The object's database, or null when the flow does not know it (the graph completes it from the
    /// server's default database, as for a file flow's target).</summary>
    public string? Database { get; init; }

    /// <summary>The object's schema, or null when the flow does not know it.</summary>
    public string? Schema { get; init; }

    /// <summary>The object's name.</summary>
    public required string Name { get; init; }

    /// <summary>What the object is, when the flow knows; null takes the ingestion default (a read is of an object of
    /// unknown kind, a write is to a table).</summary>
    public LineageNodeKind? Kind { get; init; }

    /// <summary>The server's provider, for the server inventory the derived tier connects to.</summary>
    public DataSourceKind Provider { get; init; } = DataSourceKind.MSSQL;

    /// <summary>A read of a three-part <c>[Database].[Schema].[Object]</c> name (bracket-aware; the rightmost three
    /// parts are used) on the server <paramref name="connectionReference"/> names.</summary>
    public static DeclaredDataObject Reads(string connectionReference, string qualifiedName)
        => FromQualifiedName(LineageRelation.Reads, connectionReference, qualifiedName);

    /// <summary>A write to a three-part <c>[Database].[Schema].[Object]</c> name on the server
    /// <paramref name="connectionReference"/> names.</summary>
    public static DeclaredDataObject Writes(string connectionReference, string qualifiedName)
        => FromQualifiedName(LineageRelation.Writes, connectionReference, qualifiedName);

    /// <summary>A declaration of <paramref name="relation"/> on a three-part name. The name is parsed here (a name with
    /// fewer than three parts is a <see cref="SqlFlow.Core.SqlFlowException"/>); the relation and the connection
    /// reference are checked by the estate scan, which reports a refusal against the document's file.</summary>
    public static DeclaredDataObject FromQualifiedName(LineageRelation relation, string connectionReference, string qualifiedName)
    {
        var parsed = RelationalObject.Parse(qualifiedName);
        return new DeclaredDataObject
        {
            Relation = relation,
            ConnectionReference = connectionReference,
            Database = parsed.Database,
            Schema = parsed.Schema,
            Name = parsed.Name,
        };
    }
}

/// <summary>
/// A flow document of a kind a host registered (<see cref="IFlowDocumentKind"/>) rather than one the loader builds in.
/// It declares the header members every consumer of a flow document reads: the estate scan and the catalog sync (name,
/// kind, batch, the servers it reads and writes, lifecycle, lineage participation), the declared endpoints the proposal
/// preflight compares, the credential references the secret-hygiene check inspects, and whether a node can run it
/// from a single-file snapshot. With those, every consumer treats a registered flow like a built-in one without knowing
/// its kind. The loader stamps the document's schedule, execution mode and lifecycle from the envelope.
/// </summary>
public abstract record RegisteredFlowDocument : FlowDocument
{
    /// <summary>The flow's name: its pipeline identity and its run-history folder.</summary>
    public abstract string Name { get; }

    /// <summary>The document's <c>flowType</c> value.</summary>
    public abstract string Kind { get; }

    /// <summary>The flow's grouping label (<c>batch:</c>), or null when it declares none.</summary>
    public virtual string? Batch => null;

    /// <summary>The flow's declared lifecycle (the envelope's <c>lifecycle:</c> key, stamped by the loader): production
    /// unless the document says otherwise. Only production pipelines generate notification events; execution is
    /// unaffected.</summary>
    public FlowLifecycle Lifecycle { get; init; } = FlowLifecycle.Production;

    /// <summary>The connection reference (<c>${env:...}</c>, <c>${keyvault:...}</c>) of the server the flow reads
    /// from, or null when its source is not a server (files, an external API).</summary>
    public virtual string? SourceConnectionReference => null;

    /// <summary>The connection reference of the server the flow writes to, or null when its target is not a server
    /// (files, an external API).</summary>
    public virtual string? TargetConnectionReference => null;

    /// <summary>What the flow reads, as its declared endpoint and pipeline row show it: a location, an object name or
    /// an endpoint, never a credential. Null when the kind declares no source side.</summary>
    public virtual string? SourceReference => null;

    /// <summary>What the flow writes to, as its declared endpoint and pipeline row show it. Null when the kind declares
    /// no target side.</summary>
    public virtual string? TargetReference => null;

    /// <summary>Every value in the document that is meant to be a credential reference, keyed by what it configures,
    /// so the loader can warn when one is a literal secret instead. The value itself is never echoed by the warning.</summary>
    public virtual IEnumerable<KeyValuePair<string, string>> CredentialReferences => [];

    /// <summary>True when the document cannot execute from a bare single-file snapshot because it addresses sibling
    /// files in the repository tree through a relative local path; such a document runs from a full git
    /// materialization.</summary>
    public virtual bool RequiresRepoTree => false;

    /// <summary>Whether the flow belongs in the lineage graph: true for a flow that moves catalog data, false for one
    /// that runs on the estate rather than through it.</summary>
    public virtual bool ParticipatesInLineage => true;

    /// <summary>The database objects the flow reads and writes. The estate scan turns each into a declared lineage fact
    /// on the node an ingestion flow naming the same object uses, so the execution plan orders the flow after the flows
    /// that write what it reads, and before the flows that read what it writes. Empty for a kind that relates to no
    /// database object; a declaration the scan cannot use skips the document with a warning.</summary>
    public virtual IReadOnlyList<DeclaredDataObject> DeclaredObjects => [];
}

/// <summary>
/// A flow kind a host registers with the loader, keyed by its <c>flowType</c>. Kinds are registered in the host's
/// composition root, so the YAML layer never depends on the engines that implement them; a registered kind may not
/// claim a built-in <c>flowType</c>.
/// </summary>
public interface IFlowDocumentKind
{
    /// <summary>The <c>flowType</c> value that selects this kind (compared case-insensitively).</summary>
    string FlowType { get; }

    /// <summary>One phrase naming what the kind does, for the loader's unknown-kind message.</summary>
    string Description { get; }

    /// <summary>Parses the document body; <paramref name="source"/> labels errors (the file path, or "&lt;inline&gt;").
    /// A failure is a <see cref="SqlFlow.Core.FlowValidationException"/> naming the source.</summary>
    RegisteredFlowDocument Parse(string yaml, string source);

    /// <summary>The operations a run of this kind can perform, its default first. Empty for a kind with one implicit
    /// operation, which then accepts no <see cref="RunParameters.Operation"/>.</summary>
    IReadOnlyList<FlowKindOperation> Operations { get; }

    /// <summary>Validates a run's kind arguments (<see cref="RunParameters.Operation"/>, already known to be one of
    /// <see cref="Operations"/> when set, <see cref="RunParameters.Values"/> and <see cref="RunParameters.Payload"/>)
    /// at the trust boundary: a trigger, a schedule, the CLI and the executor all call it through
    /// <see cref="YamlDocumentLoader.ValidateRunParameters"/>. A refusal is a <see cref="SqlFlow.Core.SqlFlowException"/>
    /// whose message names the offending argument.</summary>
    void ValidateParameters(RunParameters parameters);
}

/// <summary>
/// A family of documents a registered kind owns beside its flows, keyed by a top-level <c>documentType</c> (a
/// delivery kind's mapping documents, say). The CLI validates such a document under its own type, and the proposal
/// preflight checks it, instead of reporting it as a flow document with no <c>flowType</c>.
/// </summary>
public interface ICompanionDocumentKind
{
    /// <summary>The <c>documentType</c> value that selects this family (compared case-insensitively).</summary>
    string DocumentType { get; }

    /// <summary>One phrase naming what the documents are, for the loader's unknown-type message.</summary>
    string Description { get; }

    /// <summary>Parses the document and returns its display name; a failure is a
    /// <see cref="SqlFlow.Core.FlowValidationException"/> naming the source.</summary>
    string ParseCompanion(string yaml, string source);
}
