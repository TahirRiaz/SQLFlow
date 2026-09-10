using SqlFlow.Core.Acquire;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Translate;

/// <summary>
/// A validated translation flow (<c>flowType: trl</c>): read a SQL Server query result, map each row (or the whole
/// result set) through a declared JSON template into arbitrarily shaped documents, write those documents to a file
/// destination (local or cloud), and/or deliver them to a remote HTTP API. The template dialect is fully generic:
/// any nesting, arrays driven by bound child datasets, constants, typed leaves. The source server is a
/// connection-registry alias, exactly as an export flow's is. Immutable.
/// </summary>
public sealed record TranslateFlow
{
    public int FlowId { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public required string SysAlias { get; init; }

    /// <summary>The connection alias of the source server the query and every dataset query run against.</summary>
    public required string SrcServer { get; init; }

    /// <summary>The primary query. Each row (default) or the whole result set becomes one document.</summary>
    public required string Query { get; init; }

    /// <summary>Named child result sets the template's <c>$forEach</c> arrays draw from, keyed to the enclosing
    /// scope by their bind columns. Empty when the template needs no child rows.</summary>
    public IReadOnlyList<TranslateDataset> Datasets { get; init; } = [];

    /// <summary>The document grain: one document per primary row, or one for the whole result set.</summary>
    public TranslateDocumentGrain DocumentsPer { get; init; } = TranslateDocumentGrain.Row;

    /// <summary>The document-level null handling every <see cref="TranslateNullPolicy.Inherit"/> leaf follows.</summary>
    public TranslateDocumentNulls Nulls { get; init; } = TranslateDocumentNulls.Omit;

    /// <summary>The declared JSON shape.</summary>
    public required TranslateNode Template { get; init; }

    /// <summary>Where the rendered documents are written. Always present: the saved files are the durable record
    /// of the translation, and the invoke step delivers exactly what was saved.</summary>
    public required TranslateOutput Output { get; init; }

    /// <summary>The optional API delivery step, running strictly AFTER every file is written: it reads the saved
    /// files back and posts their content, so what was delivered is byte-for-byte what sits on disk, and a failed
    /// delivery leaves the files in place for a re-run to re-post. Null when the flow only writes files.</summary>
    public TranslateInvoke? Invoke { get; init; }

    public string FlowType { get; init; } = "trl";

    /// <summary>The source connection reference understood by the resolver.</summary>
    public string ConnectionReference => "@" + SrcServer;
}

/// <summary>The document grain of a translation flow.</summary>
public enum TranslateDocumentGrain
{
    /// <summary>One document per primary-query row (the default). The row's columns are the root scope.</summary>
    Row,

    /// <summary>One document for the whole result set. The primary rows are addressable as the reserved
    /// <c>$forEach: rows</c> dataset; no columns are in scope at the root.</summary>
    ResultSet,
}

/// <summary>Document-level null handling, inherited by every leaf that does not declare its own policy.</summary>
public enum TranslateDocumentNulls
{
    /// <summary>A NULL-valued leaf leaves its property out of the object (the default; sparse documents).</summary>
    Omit,

    /// <summary>A NULL-valued leaf emits an explicit JSON null (fixed-shape documents).</summary>
    Include,
}

/// <summary>
/// One named child result set: its rows are grouped by the bind columns, and a <c>$forEach</c> or <c>$row</c>
/// over this dataset selects the group whose bind values equal the same-named columns of the enclosing scope.
/// Bind columns must exist in both the dataset's own result and the enclosing scope, which is what makes
/// nesting datasets under datasets work without any extra declaration. An EMPTY bind declares a
/// single-instance dataset: it is not keyed to any scope and resolves to all of its rows anywhere, which is
/// what a header block (via <c>$row</c>) or a global repeater (via <c>$forEach</c>) reads from.
/// </summary>
public sealed record TranslateDataset
{
    public required string Name { get; init; }

    /// <summary>The dataset query, run once per flow run against the flow's source connection.</summary>
    public required string Query { get; init; }

    /// <summary>The join columns matching a dataset row to its enclosing scope, compared by invariant string
    /// representation. Empty means single-instance: the dataset resolves to all of its rows at any scope.</summary>
    public IReadOnlyList<string> Bind { get; init; } = [];
}

/// <summary>How rendered documents are laid out on the destination.</summary>
public enum TranslateOutputMode
{
    /// <summary>One <c>.json</c> file per document, named by the file-name template.</summary>
    FilePerDocument,

    /// <summary>One <c>.jsonl</c> file: one compact document per line (the default; the lake-friendly layout).</summary>
    JsonLines,

    /// <summary>One <c>.json</c> file holding a single JSON array of all documents.</summary>
    Array,
}

/// <summary>The file output of a translation flow.</summary>
public sealed record TranslateOutput
{
    /// <summary>The destination folder: a local path, a <c>file://</c> URI, or an Azure Blob/ADLS URI. The same
    /// destination registry as an export flow (CanHandle selects local vs cloud).</summary>
    public required string Path { get; init; }

    public TranslateOutputMode Mode { get; init; } = TranslateOutputMode.JsonLines;

    /// <summary>The file name (no extension). Per-document mode renders <c>{Column}</c> tokens against each
    /// document's root row and appends the document index when the template carries no token, so names never
    /// collide. Null defaults to the flow name.</summary>
    public string? FileName { get; init; }

    /// <summary>Append a <c>_yyyyMMddHHmmss</c> run timestamp to single-file names, so repeated runs land side by
    /// side instead of overwriting. Off by default: the flow's contract is a deterministic overwrite, so the
    /// destination always mirrors the latest translation and the invoke step posts exactly that. Ignored by
    /// per-document mode, whose names vary per document.</summary>
    public bool AddTimestamp { get; init; }

    /// <summary>Write indented JSON (per-document and array modes; JSON Lines is always compact).</summary>
    public bool Indent { get; init; }
}

/// <summary>
/// The API delivery step. It runs after the output phase and posts the SAVED documents: per-document files post
/// one request per file (or grouped into array batches), a JSON Lines file posts its lines in batches, and an
/// array file posts as one request holding the whole saved array. Document content streams back from the
/// destination rather than being held across the run.
/// </summary>
public sealed record TranslateInvoke
{
    /// <summary>The request URL. Supports <c>${...}</c> secret references, and <c>{Column}</c> tokens from each
    /// document's root row when the output is per-document with a batch size of 1 (the only layout where a
    /// request has exactly one row scope).</summary>
    public required string Url { get; init; }

    public string Method { get; init; } = "POST";

    /// <summary>Extra request headers. Values may carry <c>${...}</c> secret references. Content-Type defaults to
    /// <c>application/json</c> unless declared here.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>HTTP authentication, the exact same surface as an acquisition flow's <c>source.auth</c>.</summary>
    public AcquireAuth Auth { get; init; } = new() { Type = AcquireAuthType.None };

    /// <summary>Documents per request. 1 (the default) sends each document as its own request body; above 1 the
    /// body is a JSON array of up to this many documents. Meaningful for the per-document and JSON Lines output
    /// modes; an array output always posts as one request.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>Wrap the request body's documents in an envelope object under this key (for example OSDU's
    /// <c>{"records": [...]}</c>). The wrapped body is always an array, even for a single document.</summary>
    public string? EnvelopeKey { get; init; }

    /// <summary>The reliability envelope, the exact same surface as an acquisition flow's
    /// <c>source.reliability</c>: timeout, retry, rate limit, TLS verification, URL allowlist, tolerated
    /// per-request skip statuses, and request concurrency (the loader defaults concurrency to 1, so documents
    /// deliver in order unless the flow opts into parallel requests).</summary>
    public AcquireReliability Reliability { get; init; } = new() { Concurrency = 1 };
}

/// <summary>The outcome of one translation run.</summary>
public sealed record TranslateRunResult
{
    public required Guid RunId { get; init; }

    public required int FlowId { get; init; }

    public required bool Success { get; init; }

    public DateTime StartTimeUtc { get; init; }

    public DateTime EndTimeUtc { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>Primary-query rows read.</summary>
    public long TotalRows { get; init; }

    /// <summary>Documents rendered from those rows.</summary>
    public long Documents { get; init; }

    /// <summary>Files written, with per-file document counts and sizes (the catalog's per-run file drill-down
    /// reads exactly this shape).</summary>
    public IReadOnlyList<ExportedFile> Files { get; init; } = [];

    /// <summary>API requests delivered successfully.</summary>
    public long RequestsSent { get; init; }

    /// <summary>API requests tolerated as skips via <c>reliability.skipStatusCodes</c>.</summary>
    public long RequestsSkipped { get; init; }

    /// <summary>Every SQL statement the run executed (the primary query and each dataset query), in execution
    /// order. Always captured, success or failure.</summary>
    public IReadOnlyList<SqlTraceEntry> SqlTrace { get; init; } = [];

    public string? Error { get; init; }
}
