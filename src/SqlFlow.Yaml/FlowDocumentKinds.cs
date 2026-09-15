using SqlFlow.Core.Runs;

namespace SqlFlow.Yaml;

/// <summary>
/// A flow document of a kind a host registered (<see cref="IFlowDocumentKind"/>) rather than one the loader builds in.
/// It declares the header members every consumer of a flow document reads: the estate scan and the catalog sync (name,
/// kind, batch, the servers it reads and writes, lifecycle, lineage participation), the declared endpoints the proposal
/// preflight compares, the credential references the secret-hygiene check inspects, and whether a node can run it
/// from a single-file snapshot. With those, every consumer treats a registered flow like a built-in one without knowing
/// its kind. The loader stamps the document's schedule and execution mode from the envelope.
/// </summary>
public abstract record RegisteredFlowDocument : FlowDocument
{
    /// <summary>The flow's name: its pipeline identity and its run-history folder.</summary>
    public abstract string Name { get; }

    /// <summary>The document's <c>flowType</c> value.</summary>
    public abstract string Kind { get; }

    /// <summary>The flow's grouping label (<c>batch:</c>), or null when it declares none.</summary>
    public virtual string? Batch => null;

    /// <summary>The flow's declared lifecycle: production unless the document says otherwise. Only production
    /// pipelines generate notification events; execution is unaffected.</summary>
    public virtual FlowLifecycle Lifecycle => FlowLifecycle.Production;

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
