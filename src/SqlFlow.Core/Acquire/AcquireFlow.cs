using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Acquire;

/// <summary>
/// A validated generic API-acquisition flow (<c>flowType: acq</c>): fetch from a third-party system and land the
/// <b>raw</b> response payloads as files in the data lake, one file per page/iteration, preserving the original
/// format byte-for-byte. It performs no CSV/flatten transform: downstream SQLFlow file flows (json/csv/xml) ingest
/// the landed files into SQL. This is the consolidation of the estate's Azure Automation runbooks into one
/// declarative, testable, schedulable engine.
/// </summary>
public sealed record AcquireFlow
{
    public required string Name { get; init; }

    /// <summary>Stable, system-generated identity derived from <see cref="Name"/> (never authored in YAML).</summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) grouping label under which this flow's runs report.</summary>
    public string? Batch { get; init; }

    /// <summary>
    /// The endpoints this flow acquires, in declaration order. One flow fetches as many endpoint+landing pairs as it
    /// declares (the multi-item <c>items:</c> form, mirroring a <c>cpy</c> flow's steps): the single-endpoint case is a
    /// one-element list built from the top-level <c>source</c>/<c>landing</c>. Always at least one. Every item shares the
    /// flow's connection envelope (transport, base URL, auth, reliability) via <see cref="Source"/>; each item carries its
    /// own request, pagination, fan-out, and landing.
    /// </summary>
    public required IReadOnlyList<AcquireItem> Items { get; init; }

    /// <summary>
    /// The shared connection envelope, identical across every item (transport, base URL, auth, reliability, transport
    /// options). Exposed off the first item because the engine resolves the HTTP client and authentication once per run,
    /// not once per item. Never varies per item.
    /// </summary>
    public AcquireSource Source => Items[0].Source;

    /// <summary>Incremental resume settings; null means the flow fetches its full declared window every run. Valid only on
    /// the single-item form: the run watermark is one value per run, so a multi-endpoint flow expresses incrementality
    /// through its items' date-window fan-out instead.</summary>
    public AcquireIncremental? Incremental { get; init; }

    /// <summary>
    /// Declared runtime parameters: name to default value. Each becomes a template variable (<c>{name}</c>)
    /// usable anywhere templates render (request path/query/headers/body, iteration bounds, landing paths).
    /// A caller (the CLI, the control plane trigger, or the debugger) can override any of them per run; a
    /// parameter declared with a null default has no fallback and must be supplied at run time.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Params { get; init; }
        = new Dictionary<string, string?>(StringComparer.Ordinal);
}

/// <summary>
/// One endpoint an acquisition flow fetches: a <see cref="Source"/> (the connection envelope plus this endpoint's request,
/// pagination, and fan-out) paired with the <see cref="Landing"/> its raw payloads are written to. A single-endpoint flow
/// has one item; a multi-endpoint flow has one per <c>items:</c> entry, each landing to its own raw-zone location so the
/// downstream file/pre flow that reads that location binds to it in lineage.
/// </summary>
public sealed record AcquireItem
{
    /// <summary>An optional stable label for this endpoint (e.g. <c>bikes</c>, <c>alert</c>), surfaced in run logs. When
    /// omitted it defaults to the landing's leaf folder. Distinct across a flow's items.</summary>
    public string? Name { get; init; }

    public required AcquireSource Source { get; init; }

    public required AcquireLanding Landing { get; init; }
}

/// <summary>The transport a source speaks.</summary>
public enum AcquireTransport
{
    /// <summary>HTTP(S): REST, GraphQL, SOAP-over-POST. The dominant transport.</summary>
    Http,

    /// <summary>SFTP file download.</summary>
    Sftp,

    /// <summary>Azure Storage Table entity query.</summary>
    AzureTable,
}

/// <summary>
/// The source side of an acquisition flow: which transport, where, how to authenticate, what to request, how to
/// page, how to fan out, and the reliability envelope. HTTP uses <see cref="Auth"/>/<see cref="Request"/>/
/// <see cref="Pagination"/>; the file/table transports use <see cref="Transport"/>-specific options carried in
/// <see cref="Options"/> and the shared <see cref="Iterations"/>/<see cref="Reliability"/>.
/// </summary>
public sealed record AcquireSource
{
    public AcquireTransport Transport { get; init; } = AcquireTransport.Http;

    /// <summary>
    /// The base endpoint: an HTTP(S) origin (e.g. <c>https://api.acme.com</c>), an <c>sftp://host[:port]</c> URL,
    /// or the Azure Table account URL. Interpreted per <see cref="Transport"/>. (S3 object copy is a <c>cpy</c> flow.)
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>HTTP authentication. Ignored by non-HTTP transports, which carry credentials in <see cref="Options"/>.</summary>
    public AcquireAuth Auth { get; init; } = new() { Type = AcquireAuthType.None };

    /// <summary>The HTTP request template. Required for the HTTP transport.</summary>
    public AcquireRequest? Request { get; init; }

    /// <summary>HTTP pagination strategy. Defaults to a single page.</summary>
    public AcquirePagination Pagination { get; init; } = new();

    /// <summary>
    /// Fan-out iterations applied outer-to-inner. Each combination of iteration values produces one request
    /// pipeline (which may itself page). Empty means a single request pipeline.
    /// </summary>
    public IReadOnlyList<AcquireIteration> Iterations { get; init; } = [];

    public AcquireReliability Reliability { get; init; } = new();

    /// <summary>Transport-specific options (SFTP/Table credentials and locators, secret references included).</summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

// ---------------------------------------------------------------------------------------------------------------
// Authentication
// ---------------------------------------------------------------------------------------------------------------

/// <summary>The HTTP authentication strategy. Secret material is always a <c>${...}</c> reference, never inline.</summary>
public enum AcquireAuthType
{
    None,

    /// <summary>A static secret sent in a request header (name from <see cref="AcquireAuth.HeaderName"/>).</summary>
    ApiKeyHeader,

    /// <summary>A static secret sent as a query parameter (name from <see cref="AcquireAuth.ParamName"/>).</summary>
    ApiKeyQuery,

    /// <summary><c>Authorization: &lt;prefix&gt;&lt;token&gt;</c> with a static token secret (prefix default <c>Bearer </c>).</summary>
    Bearer,

    /// <summary>HTTP Basic with a username/password pair of secret references.</summary>
    Basic,

    /// <summary>OAuth2 client-credentials grant against <see cref="AcquireAuth.Token"/>.</summary>
    OAuth2ClientCredentials,

    /// <summary>OAuth2 refresh-token exchange against <see cref="AcquireAuth.Token"/>.</summary>
    OAuth2RefreshToken,

    /// <summary>
    /// A generic token exchange: POST <see cref="AcquireAuth.Token"/> and read the access token out of the JSON
    /// response by path. Covers the Kolumbus-style pre-formed-form-body exchange and any bespoke token endpoint.
    /// </summary>
    TokenExchange,
}

/// <summary>Where a token endpoint is discovered from.</summary>
public sealed record AcquireAuth
{
    public required AcquireAuthType Type { get; init; }

    /// <summary>Primary secret reference: the token (Bearer/api-key), the password (Basic), or the client secret / refresh token (OAuth).</summary>
    public string? SecretRef { get; init; }

    /// <summary>Secondary secret reference: the username (Basic) or the client id (OAuth) when carried as a secret.</summary>
    public string? SecondarySecretRef { get; init; }

    /// <summary>Header name for <see cref="AcquireAuthType.ApiKeyHeader"/> (e.g. <c>X-Api-Key</c>, <c>X-Authorization</c>).</summary>
    public string? HeaderName { get; init; }

    /// <summary>Query-parameter name for <see cref="AcquireAuthType.ApiKeyQuery"/> (e.g. <c>token</c>).</summary>
    public string? ParamName { get; init; }

    /// <summary>The prefix prepended to a Bearer / api-key header value. Default <c>Bearer </c> for Bearer, empty otherwise.</summary>
    public string? ValuePrefix { get; init; }

    /// <summary>The token-endpoint description for the OAuth / exchange auth types.</summary>
    public AcquireTokenEndpoint? Token { get; init; }
}

/// <summary>How to obtain and apply an access token for the OAuth / token-exchange auth types.</summary>
public sealed record AcquireTokenEndpoint
{
    /// <summary>The token endpoint URL. When <see cref="DiscoveryUrl"/> is set this is resolved from OIDC discovery instead.</summary>
    public string? Url { get; init; }

    /// <summary>An OIDC <c>.well-known/openid-configuration</c> URL whose <c>token_endpoint</c> supplies <see cref="Url"/>.</summary>
    public string? DiscoveryUrl { get; init; }

    /// <summary>HTTP method for the token request. Default <c>POST</c>.</summary>
    public string Method { get; init; } = "POST";

    /// <summary>How the token request body is encoded: <c>form</c> (urlencoded) or <c>json</c>.</summary>
    public AcquireBodyKind BodyKind { get; init; } = AcquireBodyKind.Form;

    /// <summary>
    /// The token-request body fields (e.g. <c>grant_type</c>, <c>client_id</c>, <c>scope</c>, <c>audience</c>,
    /// <c>refreshToken</c>). Values may be <c>${...}</c> secret references and <c>{token}</c> templates.
    /// </summary>
    public IReadOnlyDictionary<string, string> Body { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// A pre-formed body sent verbatim (e.g. a Key Vault secret that already holds the full urlencoded form). When
    /// set, <see cref="Body"/> is ignored. Resolved for <c>${...}</c> references before sending.
    /// </summary>
    public string? RawBody { get; init; }

    /// <summary>Extra headers on the token request (e.g. an explicit <c>Content-Type</c>).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Send the client id/secret as an HTTP Basic header on the token request rather than in the body.</summary>
    public bool BasicAuthClient { get; init; }

    /// <summary>The JSON path in the token response holding the access token. Default <c>access_token</c>.</summary>
    public string TokenPath { get; init; } = "access_token";

    /// <summary>How the resolved token is applied to data requests: as a Bearer header (default) or a named header.</summary>
    public string? ApplyHeaderName { get; init; }

    /// <summary>The prefix on the applied token header value. Default <c>Bearer </c>.</summary>
    public string ApplyPrefix { get; init; } = "Bearer ";

    /// <summary>Re-acquire the token for every outer iteration rather than once per run (some issuers scope tokens per window).</summary>
    public bool RefreshPerIteration { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Request
// ---------------------------------------------------------------------------------------------------------------

/// <summary>How a request body is encoded.</summary>
public enum AcquireBodyKind
{
    None,

    /// <summary>application/json.</summary>
    Json,

    /// <summary>application/x-www-form-urlencoded.</summary>
    Form,

    /// <summary>A GraphQL query, wrapped as <c>{ "query": ..., "variables": ... }</c> and sent as JSON.</summary>
    GraphQl,

    /// <summary>A SOAP envelope sent as text/xml.</summary>
    Soap,

    /// <summary>An opaque body sent with an explicit Content-Type.</summary>
    Raw,
}

/// <summary>The HTTP request template. Path/query/header/body values support <c>{token}</c> substitution from the
/// per-request context (iteration variables, the watermark, and built-in date tokens).</summary>
public sealed record AcquireRequest
{
    public string Method { get; init; } = "GET";

    /// <summary>The path (and optional inline query) appended to the source base URL. Supports <c>{placeholder}</c> tokens.</summary>
    public string Path { get; init; } = string.Empty;

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, string> Query { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public AcquireBodyKind BodyKind { get; init; } = AcquireBodyKind.None;

    /// <summary>The literal/templated body for JSON/GraphQL/SOAP/Raw bodies.</summary>
    public string? Body { get; init; }

    /// <summary>Body fields for a <see cref="AcquireBodyKind.Form"/> body.</summary>
    public IReadOnlyDictionary<string, string> BodyFields { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Explicit Content-Type for a <see cref="AcquireBodyKind.Raw"/> body.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// The RESPONSE body's actual character set (e.g. <c>iso-8859-1</c>), overriding whatever charset the
    /// server declares, for endpoints that label their payload wrongly (the vegvesen /export endpoint sends
    /// <c>charset=UTF-8</c> headers over ISO-8859-1 bytes). When set, the body is transcoded from this
    /// encoding to UTF-8 before landing; null (the default) trusts the declared charset.
    /// </summary>
    public string? ResponseCharset { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Pagination
// ---------------------------------------------------------------------------------------------------------------

public enum AcquirePaginationStrategy
{
    /// <summary>A single request.</summary>
    None,

    /// <summary>Increment a page-number query param until an empty page or <see cref="AcquirePagination.MaxPages"/>.</summary>
    Page,

    /// <summary>Advance an offset by a fixed limit until an empty page or the page cap.</summary>
    Offset,

    /// <summary>Follow a cursor read out of the JSON response body until it is absent.</summary>
    CursorBody,

    /// <summary>Follow the RFC 5988 <c>Link: &lt;url&gt;; rel="next"</c> response header until it is absent.</summary>
    LinkHeader,

    /// <summary>
    /// Keyset pagination on a monotonically increasing id: send <c>idAfter</c> starting from the max already landed
    /// (or a configured seed), advance to the max id observed in each page, stop on a terminal status or an empty page.
    /// </summary>
    Keyset,
}

public sealed record AcquirePagination
{
    public AcquirePaginationStrategy Strategy { get; init; } = AcquirePaginationStrategy.None;

    /// <summary>Runaway-loop guard: the maximum number of pages fetched per request pipeline. Default 50.</summary>
    public int MaxPages { get; init; } = 50;

    // Page strategy
    public string PageParam { get; init; } = "page";
    public int StartPage { get; init; } = 1;

    /// <summary>
    /// Binds the page number to this template variable instead of sending it as a query parameter, for services that
    /// carry paging in the request BODY rather than the URL (a SOAP envelope's <c>&lt;PageNo&gt;</c> is the standard
    /// case). The request body already renders <c>{token}</c> placeholders, so the page simply becomes one of them.
    /// Null (the default) keeps the page in the query string.
    /// </summary>
    public string? PageVariable { get; init; }

    // Offset strategy
    public string OffsetParam { get; init; } = "offset";
    public string LimitParam { get; init; } = "limit";
    public int Limit { get; init; } = 100;

    // Cursor-body strategy
    public string CursorParam { get; init; } = "cursor";

    /// <summary>The JSON path the next cursor / next id is read from (cursor-body and keyset strategies).</summary>
    public string? CursorPath { get; init; }

    // Keyset strategy
    public string KeysetParam { get; init; } = "idAfter";

    /// <summary>The JSON path (relative to each record) of the id advanced by keyset pagination.</summary>
    public string? KeysetIdPath { get; init; }

    /// <summary>
    /// Read the next keyset id from this RESPONSE HEADER instead of the JSON body. Some sequential "give me the
    /// record after id X" feeds return one record per call whose body is a binary file (e.g. an XLSX report) and
    /// carry the record's own id in a response header (e.g. <c>X-Entur-Report-Id</c>); there is no JSON to read the
    /// next id from. When set, each page advances <see cref="KeysetParam"/> to this header's value and stops on the
    /// terminal <see cref="StopOnStatus"/> or when the header is absent/unchanged. Numeric ids compare as integers,
    /// so the run watermark (and thus the next run's resume point) is the max id seen, not the lexicographic max.
    /// </summary>
    public string? KeysetIdHeader { get; init; }

    /// <summary>An HTTP status that terminates keyset/page pagination without being an error (e.g. 202 "no more").</summary>
    public int? StopOnStatus { get; init; }

    /// <summary>The path to the records used to detect an empty page: a JSON path (default: the whole body is the
    /// array, or a common wrapper key) or, for an XML response, an XPath selecting the record elements. An XML page
    /// has no array to auto-locate, so a body-paged XML source must set this to be able to stop on its last page.</summary>
    public string? RecordsPath { get; init; }
}

// ---------------------------------------------------------------------------------------------------------------
// Iteration (fan-out)
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireIterationKind
{
    /// <summary>Walk a date window in fixed steps, binding a from/to variable pair per step.</summary>
    DateWindow,

    /// <summary>Iterate a static list of values, binding one variable per element.</summary>
    List,

    /// <summary>Fetch a list of ids from a prior request and iterate (optionally batched) over them.</summary>
    IdsFrom,
}

public enum AcquireWindowGranularity
{
    Hour,
    Day,
    Month,
}

/// <summary>One fan-out dimension. The engine expands the cartesian product of all iterations (outer-to-inner) and
/// runs one request pipeline per combination, binding each iteration's variables into the request context.</summary>
public sealed record AcquireIteration
{
    public required AcquireIterationKind Kind { get; init; }

    /// <summary>The context variable this iteration binds (for <see cref="AcquireIterationKind.List"/> /
    /// <see cref="AcquireIterationKind.IdsFrom"/>), e.g. <c>operatorId</c> or <c>bikeId</c>.</summary>
    public string? Variable { get; init; }

    // --- DateWindow ---
    public AcquireWindowGranularity Granularity { get; init; } = AcquireWindowGranularity.Day;

    /// <summary>Inclusive window start. Relative expressions (<c>now-3d</c>, <c>yesterday</c>, <c>startOfMonth</c>) or an ISO date. When omitted, resolved from the incremental watermark or defaults.</summary>
    public string? From { get; init; }

    /// <summary>Exclusive window end. Relative expressions or an ISO date. Default <c>now</c>.</summary>
    public string? To { get; init; }

    /// <summary>The context variable bound to each step's lower bound (default <c>window.from</c>).</summary>
    public string FromVariable { get; init; } = "window.from";

    /// <summary>The context variable bound to each step's upper bound (default <c>window.to</c>).</summary>
    public string ToVariable { get; init; } = "window.to";

    /// <summary>
    /// Minutes by which each step's bound UPPER edge is pushed past the step boundary, making consecutive windows
    /// overlap. The cursor still advances by a whole step, so the windows stay aligned and none is skipped; only the
    /// value bound to <see cref="ToVariable"/> reaches further.
    /// <para>This exists for an endpoint that returns only the records fully CONTAINED in the requested window. An
    /// event straddling a step boundary (a session that starts on the last day of a month and ends on the first of
    /// the next) is then in neither window and is lost with no error, permanently. An overlap of at least the longest
    /// possible event duration puts every straddling event inside the earlier window. Overlapping windows return the
    /// boundary records twice, which is harmless: the landed files are keyed by the step's own start, and the keyed
    /// merge downstream is idempotent.</para>
    /// <para>The last step is never extended past the window's own upper bound, so an overlap cannot make a run
    /// request a period the caller did not ask for. 0 (the default) keeps the windows strictly abutting.</para>
    /// </summary>
    public int OverlapMinutes { get; init; }

    // --- List ---
    public IReadOnlyList<string> Values { get; init; } = [];

    // --- IdsFrom ---
    /// <summary>The request that produces the id list (a full request template).</summary>
    public AcquireRequest? IdRequest { get; init; }

    /// <summary>
    /// Selects the ids from the id request's response: a JSON path for a JSON response (<c>$[*].BikeId</c>) or an
    /// XPath for an XML one (<c>//Quest/QuestId</c>), chosen by what the discovery response actually is. When
    /// <see cref="IdBindings"/> is set this instead selects the record ELEMENTS the bindings are read from.
    /// </summary>
    public string? IdPath { get; init; }

    /// <summary>
    /// Binds several variables per discovered record instead of one value per id: each entry is a variable name and
    /// a path relative to the record <see cref="IdPath"/> selected. This is what a fan-out needs when the follow-up
    /// request takes more than the id itself, for example a SOAP service whose per-entity call must echo back both
    /// the entity id and a per-entity security token from the discovery response. Empty (the default) keeps the
    /// single-variable behavior. Incompatible with <see cref="BatchSize"/> greater than one, since a batch of joined
    /// ids has no single record to read the other variables from.
    /// </summary>
    public IReadOnlyDictionary<string, string> IdBindings { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>When &gt; 1, ids are grouped into batches of this size and the variable is bound to a joined batch.</summary>
    public int BatchSize { get; init; } = 1;

    /// <summary>The separator joining a batch of ids into the bound variable (default comma).</summary>
    public string BatchSeparator { get; init; } = ",";
}

// ---------------------------------------------------------------------------------------------------------------
// Reliability
// ---------------------------------------------------------------------------------------------------------------

public sealed record AcquireReliability
{
    /// <summary>Per-request timeout. Default 100s.</summary>
    public int TimeoutSeconds { get; init; } = 100;

    public AcquireRetry Retry { get; init; } = new();

    /// <summary>Outbound request rate cap (requests/second) via a token bucket. 0 or less means unlimited.</summary>
    public double RateLimitRps { get; init; }

    /// <summary>Max in-flight fan-out fetches per item: the engine runs the item's date-window x id expansion (one
    /// landed file per combination) this many at a time instead of strictly one after another. The rate limiter
    /// still caps the aggregate request rate, so concurrency only lets a run saturate that cap rather than paying a
    /// full round-trip latency per file; on a wide per-id fan-out (thousands of files) that is the difference
    /// between latency-bound and rate-bound. 1 is strictly sequential. Clamped to at least 1; default 8.</summary>
    public int Concurrency { get; init; } = 8;

    /// <summary>Hard cap on a single response held in memory before landing. Default 500 MB.</summary>
    public long MaxResponseBytes { get; init; } = 500L * 1024 * 1024;

    public bool VerifyTls { get; init; } = true;

    /// <summary>SSRF host allowlist. Empty means "any public host" (private / loopback / link-local / metadata IPs are always blocked).</summary>
    public IReadOnlyList<string> UrlAllowlist { get; init; } = [];

    /// <summary>
    /// Non-retryable HTTP statuses that mark a single fan-out request as a tolerated SKIP instead of failing the run.
    /// A wide date x id sweep routinely carries ids the endpoint no longer accepts (a decommissioned route, a closed
    /// account), and one stale id must not cost the whole sweep. Each skipped request is logged with the endpoint's
    /// own error body and counted, so a newly-broken id is still visible rather than silent. Empty by default: every
    /// non-2xx fails the run, which is the right default for a single-request feed.
    /// </summary>
    public IReadOnlyList<int> SkipStatusCodes { get; init; } = [];
}

public sealed record AcquireRetry
{
    public int MaxAttempts { get; init; } = 4;
    public int BaseDelayMs { get; init; } = 500;
    public int MaxDelayMs { get; init; } = 30_000;

    /// <summary>Honor a server <c>Retry-After</c> header as a floor on the backoff (bounded by <see cref="MaxDelayMs"/>).</summary>
    public bool HonorRetryAfter { get; init; } = true;
}

// ---------------------------------------------------------------------------------------------------------------
// Landing (raw sink)
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireCompression
{
    None,
    Gzip,
}

/// <summary>How a fetched payload is written to the raw zone, preserving its original format.</summary>
public sealed record AcquireLanding
{
    /// <summary>
    /// The raw-zone base location: an ADLS Gen2 / Blob URI (<c>abfss://fs@account.dfs.core.windows.net/base</c> or
    /// the https form) or a local/UNC path. The per-run/per-page path is composed under it.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>
    /// The relative path (under <see cref="Target"/>) for a fetched payload, minus extension. Supports date tokens
    /// (<c>{yyyy}</c>, <c>{MM}</c>, <c>{dd}</c>, <c>{yyyyMMdd}</c>), iteration variables (<c>{operatorId}</c>,
    /// <c>{window.from}</c>), and <c>{page}</c>/<c>{runId}</c>. When it does not vary per page, the page index is
    /// appended so pages never collide.
    /// </summary>
    public required string PathTemplate { get; init; }

    /// <summary>The output format: <c>auto</c> (derive extension from the response content-type / declared format),
    /// or an explicit <c>json</c>/<c>xml</c>/<c>csv</c>/<c>txt</c>/<c>bin</c>.</summary>
    public string Format { get; init; } = "auto";

    public AcquireCompression Compression { get; init; } = AcquireCompression.None;

    /// <summary>Overwrite an existing landed file; when false, a run that would collide fails rather than clobber.</summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>Also write a <c>&lt;file&gt;.headers.json</c> sidecar of the (redacted) response headers per page.</summary>
    public bool PersistHeaders { get; init; }

    /// <summary>Skip landing a payload whose record array is empty (or whose body is empty), rather than writing a zero-row file.</summary>
    public bool SkipEmpty { get; init; } = true;

    /// <summary>Skip rewriting a landed file whose target already holds byte-identical content, so a fetch that
    /// returned the same file does not bump its last-modified time and re-trigger downstream ingestion. On by default.
    /// Turn it off to write every fetched payload unconditionally, avoiding the per-file hash comparison. Has no effect
    /// when <see cref="Overwrite"/> is false.</summary>
    public bool SkipUnchanged { get; init; } = true;

    /// <summary>
    /// Data-protection rules applied to the RAW payload before it is written, in declaration order. Empty means the
    /// payload lands verbatim. Format-aware: json, jsonl, xml, and csv payloads are protectable (the rule path is a
    /// JSON path, an XML element path with an optional trailing <c>@attribute</c>, or a CSV header column name); a
    /// payload in any other format, or one that fails to parse, fails the run rather than landing unprotected, so
    /// requested protection can never be silently skipped. See <see cref="AcquireProtectRule"/>.
    /// </summary>
    public IReadOnlyList<AcquireProtectRule> Protect { get; init; } = [];
}

// ---------------------------------------------------------------------------------------------------------------
// Incremental
// ---------------------------------------------------------------------------------------------------------------

public enum AcquireWatermarkSource
{
    /// <summary>Derive the resume point by inspecting what is already landed in the lake (max id / latest partition).</summary>
    Lake,

    /// <summary>Track the max of a response field across a run and persist it in the run record for the next run.</summary>
    Response,

    /// <summary>
    /// Read the resume point from the LOADED data with a scalar query the flow supplies. Use it when the landed
    /// file names cannot encode the resume value (a feed partitioned by entity and page rather than by time) and a
    /// run-record value would be too fragile, since that one is node-local and does not survive a redeploy or an
    /// edit to the flow. The loaded table survives both and is the state the pipeline is actually tracking.
    /// </summary>
    Sql,
}

/// <summary>Incremental resume configuration for keyset/date-window flows.</summary>
public sealed record AcquireIncremental
{
    public AcquireWatermarkSource Source { get; init; } = AcquireWatermarkSource.Response;

    /// <summary>The response field whose max is the watermark (response source), e.g. <c>updated_at</c> or <c>reportId</c>.</summary>
    public string? Column { get; init; }

    /// <summary>The context variable the resolved watermark is bound to (so the request/iteration can send it), e.g. <c>idAfter</c>.</summary>
    public string? BindVariable { get; init; }

    /// <summary>A seed used on the first run when no prior watermark exists.</summary>
    public string? Seed { get; init; }

    /// <summary>The connection the <see cref="AcquireWatermarkSource.Sql"/> probe reads from. A connection
    /// reference like any other, so the target it points at is the flow's choice, not the engine's.</summary>
    public string? Connection { get; init; }

    /// <summary>
    /// The query whose result is the resume point, for the <see cref="AcquireWatermarkSource.Sql"/> source. Taken
    /// verbatim: the engine never composes it and knows nothing of the table or column involved, so the shape of the
    /// query (and the format of what it returns) is entirely the flow's to decide. Returning no row, or NULL, leaves
    /// the flow on its <see cref="Seed"/>.
    /// <para>
    /// One column is a single watermark for the whole flow. TWO columns is one watermark per entity - the first
    /// column matched against <see cref="KeyVariable"/>, the second the resume point - which is what a fan-out feed
    /// needs so each entity resumes from its own position instead of every one being dragged back to the oldest.
    /// </para>
    /// </summary>
    public string? Query { get; init; }

    /// <summary>
    /// The fan-out variable a per-entity watermark is keyed by (e.g. <c>questId</c>, <c>vehicleId</c>): for each
    /// combination of the fan-out, the row whose key equals this variable's value supplies
    /// <see cref="BindVariable"/>, and an entity with no row falls back to <see cref="Seed"/>. Null means the
    /// watermark is global to the flow.
    /// </summary>
    public string? KeyVariable { get; init; }
}
