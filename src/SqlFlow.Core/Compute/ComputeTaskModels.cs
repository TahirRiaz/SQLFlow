using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core.Comparison;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Quality;
using SqlFlow.Core.Query;

namespace SqlFlow.Core.Compute;

/// <summary>
/// The ad-hoc datasource compute operations a control-plane client can request. Each is a short camelCase
/// string (self-describing in the queue table and in API payloads, like <c>RunStatuses</c>); the set is closed
/// so the trust boundary can refuse anything it does not know how to execute.
/// </summary>
public static class ComputeOperations
{
    /// <summary>Open the resolved connection and report the server version: the cheapest end-to-end proof that
    /// the reference resolves, the network path exists, and the credentials work.</summary>
    public const string TestConnection = "testConnection";

    /// <summary>List the databases visible on the connection (provider-specific catalog query).</summary>
    public const string ListDatabases = "listDatabases";

    /// <summary>List the schemas of one database (or the connection's current database).</summary>
    public const string ListSchemas = "listSchemas";

    /// <summary>List tables/views in a database/schema scope, filtered and paged.</summary>
    public const string ListObjects = "listObjects";

    /// <summary>Search objects by name across the database.</summary>
    public const string SearchObjects = "searchObjects";

    /// <summary>Full structured introspection of one table or view: columns, indexes, constraints.</summary>
    public const string IntrospectObject = "introspectObject";

    /// <summary>Profile one table/view for its minimal unique key(s) (SQL Server / Azure SQL sources only).</summary>
    public const string DetectUniqueKey = "detectUniqueKey";

    /// <summary>Report the engine's missing-index advisories (<c>sys.dm_db_missing_index_*</c>) for the scoped
    /// database, ranked by estimated improvement, with a ready-to-review CREATE INDEX suggestion per advisory
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string MissingIndexes = "missingIndexes";

    /// <summary>Report statistics freshness (<c>sys.dm_db_stats_properties</c>) for the scoped database: rows
    /// modified since the last update, sample rates, and which statistics have gone stale, with an UPDATE
    /// STATISTICS suggestion per stale entry (SQL Server / Azure SQL sources only).</summary>
    public const string StatisticsHealth = "statisticsHealth";

    /// <summary>Report per-index read/write usage (<c>sys.dm_db_index_usage_stats</c>) for the scoped database,
    /// surfacing write-only and never-read indexes that cost maintenance without serving queries
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string IndexUsage = "indexUsage";

    /// <summary>Report the plan cache's most expensive statements (<c>sys.dm_exec_query_stats</c>) ranked by
    /// total elapsed time, with per-statement execution counts, CPU, and I/O
    /// (SQL Server / Azure SQL sources only).</summary>
    public const string TopQueries = "topQueries";

    /// <summary>Check one table for duplicate rows on its key, using the key the TABLE declares. READ-ONLY:
    /// it groups and counts. Gated by <c>ControlPlane:DataOps:Enabled</c>.</summary>
    public const string DuplicateKeys = "duplicateKeys";

    /// <summary>Run one APPROVED read-only business query. The statement reaches this operation only after a
    /// human saw it and a one-time plan token was redeemed, so the queue row is the record of an approved
    /// query, never of an ad-hoc one. Gated by <c>ControlPlane:DataOps:Enabled</c>.</summary>
    public const string RunQuery = "runQuery";

    /// <summary>Compare the current estate against the OLD production baseline over a configured linked
    /// server: object inventory, one object's shape, or one object's rows. Read-only on both estates and
    /// gated by <c>ControlPlane:DataOps:Enabled</c>.</summary>
    public const string CompareBaseline = "compareBaseline";

    /// <summary>Every operation this build understands, for validation messages.</summary>
    public static readonly string[] All =
        [TestConnection, ListDatabases, ListSchemas, ListObjects, SearchObjects, IntrospectObject, DetectUniqueKey,
         MissingIndexes, StatisticsHealth, IndexUsage, TopQueries, DuplicateKeys, CompareBaseline,
         RunQuery];

    /// <summary>The operations the DataOps feature switch gates. Off, the control plane refuses them with a
    /// clear "not enabled" problem instead of queueing a task no policy permits.</summary>
    public static bool IsDataOps(string? operation)
        => operation is DuplicateKeys or CompareBaseline or RunQuery;

    /// <summary>The warehouse-health subset: DMV probes authored in T-SQL, so they require a SQL Server family
    /// source, exactly like <see cref="DetectUniqueKey"/>.</summary>
    public static bool IsWarehouseHealth(string? operation)
        => operation is MissingIndexes or StatisticsHealth or IndexUsage or TopQueries;

    public static bool IsKnown(string? operation)
        => operation is TestConnection or ListDatabases or ListSchemas or ListObjects or SearchObjects
            or IntrospectObject or DetectUniqueKey || IsWarehouseHealth(operation) || IsDataOps(operation);
}

/// <summary>
/// The complete, self-contained description of one queued compute task: the operation, the connection
/// REFERENCE it runs against (never a secret; the executing node resolves it from its own environment, exactly
/// like a flow run), and the operation's arguments. This is the one contract shared by the control-plane
/// endpoint (which validates it at the trust boundary and serializes it onto the queue row) and the worker
/// (which deserializes and executes it), so the two ends can never drift.
/// </summary>
public sealed record ComputeTaskPayload
{
    /// <summary>The queue rows carry this payload as compact JSON; camelCase with enum names, matching the
    /// product's other JSON surfaces (run.json, DefinitionJson).</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>One of <see cref="ComputeOperations"/>.</summary>
    public required string Operation { get; init; }

    /// <summary>The connection reference to resolve on the executing node: a whole <c>${env:...}</c> /
    /// <c>${keyvault:...}</c> reference or an <c>@alias</c>. Inline connection strings are deliberately not
    /// accepted on this ad-hoc path (see <see cref="Validate"/>): the estate's flows declare them through
    /// reviewed git, not through an interactive API.</summary>
    public required string SourceRef { get; init; }

    /// <summary>The provider of the reference (MSSQL / AZDB / MySQL / PostgreSQL / Oracle). Null defaults to
    /// SQL Server for a <c>${...}</c> reference; an <c>@alias</c> always takes its kind from the registry.</summary>
    public DataSourceKind? ProviderKind { get; init; }

    /// <summary>The database scope; null means the connection's current database.</summary>
    public string? Database { get; init; }

    /// <summary>The schema scope (listObjects) or the object's schema (introspectObject / detectUniqueKey).</summary>
    public string? Schema { get; init; }

    /// <summary>The object name for introspectObject / detectUniqueKey.</summary>
    public string? ObjectName { get; init; }

    /// <summary>A name filter for listing operations (parameterized LIKE; wildcards escaped by the reader).</summary>
    public string? NameLike { get; init; }

    /// <summary>The search text for searchObjects.</summary>
    public string? SearchTerm { get; init; }

    public bool IncludeTables { get; init; } = true;

    public bool IncludeViews { get; init; } = true;

    public bool IncludeSystem { get; init; }

    public int Offset { get; init; }

    public int Limit { get; init; } = 200;

    /// <summary>detectUniqueKey sampling: null auto-samples large tables, 0 forces a full scan, a positive
    /// value sets an explicit sample size.</summary>
    public int? SampleSize { get; init; }

    /// <summary>detectUniqueKey: the widest composite key to consider.</summary>
    public int MaxKeyColumns { get; init; } = 4;

    /// <summary>detectUniqueKey: how many candidates to report.</summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>detectUniqueKey: confirm sampled candidates against the whole table.</summary>
    public bool VerifyCandidates { get; init; } = true;

    /// <summary>detectUniqueKey: answer from an enforced unique index/constraint without reading rows.</summary>
    public bool TrustDeclaredKeys { get; init; } = true;

    /// <summary>runQuery: the approved statement. It is placed here by the control plane when a plan token is
    /// redeemed, never by a client: the request that starts a run carries a token, not SQL.</summary>
    public string? Sql { get; init; }

    /// <summary>runQuery: the most rows the result carries before it is marked truncated.</summary>
    public int? MaxRows { get; init; }

    /// <summary>runQuery: the command timeout.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>duplicateKeys: the columns that identify one real row. Left empty the check reads the key
    /// the TABLE declares; this is where the answer to a report's <c>question</c> comes back when the table
    /// declares no usable key.</summary>
    public IReadOnlyList<string>? Columns { get; init; }

    /// <summary>compareBaseline: what to compare (inventory, schema, or data).</summary>
    public BaselineCompareMode? CompareMode { get; init; }

    /// <summary>compareBaseline: the linked server reaching the OLD estate. Allowlisted by the control plane's
    /// configuration at enqueue; the node re-runs every shape and fragment check but takes the allowlist
    /// decision as already made, exactly as a queued run takes its connection reference as already reviewed.</summary>
    public string? LinkedServer { get; init; }

    /// <summary>compareBaseline: the database on the linked server.</summary>
    public string? BaselineDatabase { get; init; }

    /// <summary>compareBaseline: the schema on the old side when it differs from <see cref="Schema"/>.</summary>
    public string? BaselineSchema { get; init; }

    /// <summary>compareBaseline: the object on the old side when the name differs (the ported table is
    /// routinely renamed to match its V3 source, with a compatibility view keeping the old name).</summary>
    public string? BaselineObjectName { get; init; }

    /// <summary>compareBaseline, data mode: the LOGICAL key identifying one real-world reading on BOTH
    /// estates. Not the surrogate key, which each estate assigns independently.</summary>
    public IReadOnlyList<string>? KeyExpressions { get; init; }

    /// <summary>compareBaseline, data mode: the columns compared for value parity; empty means every column
    /// except the identity, the bare key columns, and anything matching the exclusion pattern.</summary>
    public IReadOnlyList<string>? CompareColumns { get; init; }

    /// <summary>compareBaseline, data mode: a LIKE pattern for columns excluded from value parity by default,
    /// so the provenance and audit columns do not report a mismatch on every row.</summary>
    public string? ExcludeColumnPattern { get; init; }

    /// <summary>compareBaseline, data mode: a predicate applied to BOTH sides, without the WHERE keyword.</summary>
    public string? Where { get; init; }

    /// <summary>compareBaseline, data mode: how many example keys each anti-join direction carries.</summary>
    public int SampleRows { get; init; } = 5;

    /// <summary>The widest page a task may request; larger asks are a request error, not a silent clamp, so the
    /// caller learns the real bound.</summary>
    public const int MaxLimit = 1000;

    public const int MaxSourceRefLength = 512;

    public const int MaxIdentifierLength = 256;

    public const int MaxSearchTermLength = 256;

    /// <summary>The largest explicit detectUniqueKey sample (rows). Above this the measurement cost stops being
    /// an interactive ask; the auto-sample (null) already handles huge tables.</summary>
    public const int MaxSampleSize = 10_000_000;

    /// <summary>Serializes the payload to the compact JSON stored on the queue row.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Deserializes and validates a queue row's payload. Throws <see cref="SqlFlowException"/> when the
    /// JSON is malformed or the payload is invalid: a queue row is data from the database, so the worker treats
    /// it as a trust boundary rather than assuming the enqueuer validated it.</summary>
    public static ComputeTaskPayload FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        ComputeTaskPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ComputeTaskPayload>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SqlFlowException($"The compute task's arguments are not valid JSON: {ex.Message}", ex);
        }

        if (payload is null)
        {
            throw new SqlFlowException("The compute task's arguments deserialized to nothing.");
        }

        payload.Validate();
        return payload;
    }

    /// <summary>
    /// Validates the payload for its operation. Called at BOTH ends: the control-plane endpoint (so a bad ask
    /// is a 400, never a queued task doomed to fail) and the worker on deserialization (the queue row is data
    /// from the database). Throws <see cref="SqlFlowException"/> with the precise field named.
    /// </summary>
    public void Validate()
    {
        if (!ComputeOperations.IsKnown(Operation))
        {
            throw new SqlFlowException(
                $"Unknown compute operation '{Operation}'. Valid operations: {string.Join(", ", ComputeOperations.All)}.");
        }

        if (string.IsNullOrWhiteSpace(SourceRef))
        {
            throw new SqlFlowException("A compute task requires a non-blank source reference.");
        }

        if (SourceRef.Length > MaxSourceRefLength)
        {
            throw new SqlFlowException($"The source reference is longer than {MaxSourceRefLength} characters.");
        }

        if (!IsWholeReference(SourceRef))
        {
            throw new SqlFlowException(
                "The source must be a whole ${env:...} / ${keyvault:...} reference or an @alias. Inline " +
                "connection strings are not accepted on the ad-hoc compute path; declare the connection as a " +
                "reference (the estate's flows resolve it the same way).");
        }

        ValidateIdentifier(Database, nameof(Database));
        ValidateIdentifier(Schema, nameof(Schema));
        ValidateIdentifier(ObjectName, nameof(ObjectName));
        ValidateIdentifier(NameLike, nameof(NameLike));

        if (Offset < 0)
        {
            throw new SqlFlowException("offset must be zero or positive.");
        }

        if (Limit is < 1 or > MaxLimit)
        {
            throw new SqlFlowException($"limit must be between 1 and {MaxLimit}.");
        }

        switch (Operation)
        {
            case ComputeOperations.SearchObjects:
                if (string.IsNullOrWhiteSpace(SearchTerm))
                {
                    throw new SqlFlowException("searchObjects requires a non-blank searchTerm.");
                }

                if (SearchTerm.Length > MaxSearchTermLength || HasControlCharacters(SearchTerm))
                {
                    throw new SqlFlowException(
                        $"searchTerm must be at most {MaxSearchTermLength} characters with no control characters.");
                }

                break;

            case ComputeOperations.ListObjects:
                if (!IncludeTables && !IncludeViews)
                {
                    throw new SqlFlowException("listObjects with both tables and views excluded can never return anything.");
                }

                break;

            case ComputeOperations.IntrospectObject or ComputeOperations.DetectUniqueKey:
                if (string.IsNullOrWhiteSpace(Schema) || string.IsNullOrWhiteSpace(ObjectName))
                {
                    throw new SqlFlowException($"{Operation} requires both schema and objectName.");
                }

                break;

            case ComputeOperations.DuplicateKeys:
                // Projecting the request is itself the validation, so there is one rule set rather than a copy.
                ToDuplicateKeyRequest();
                break;

            case ComputeOperations.CompareBaseline:
                ToComparisonRequest(LinkedServer is null ? [] : [LinkedServer]);
                break;

            case ComputeOperations.RunQuery:
                ToQueryRunRequest();
                break;
        }

        if (ComputeOperations.IsWarehouseHealth(Operation))
        {
            // The probes are T-SQL over SQL Server DMVs. An @alias resolves its kind on the node; the executor
            // re-checks the RESOLVED kind there, so a MySQL alias still fails precisely.
            if (ProviderKind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
            {
                throw new SqlFlowException(
                    $"{Operation} reads SQL Server dynamic management views; the source must be SQL Server or " +
                    "Azure SQL (kind mssql or azdb).");
            }
        }

        if (Operation == ComputeOperations.DetectUniqueKey)
        {
            // The profiling is T-SQL, so only SQL Server family sources qualify. An @alias resolves its kind on
            // the node; the executor re-checks the RESOLVED kind there, so a MySQL alias still fails precisely.
            if (ProviderKind is DataSourceKind.MySQL or DataSourceKind.PostgreSQL or DataSourceKind.Oracle)
            {
                throw new SqlFlowException(
                    "detectUniqueKey profiles with T-SQL; the source must be SQL Server or Azure SQL (kind mssql or azdb).");
            }

            if (SampleSize is < 0 or (> 0 and < 1000) or > MaxSampleSize)
            {
                throw new SqlFlowException(
                    $"sampleSize must be 0 (full scan), omitted (auto), or between 1000 and {MaxSampleSize}.");
            }

            if (MaxKeyColumns is < 1 or > 8)
            {
                throw new SqlFlowException("maxKeyColumns must be between 1 and 8.");
            }

            if (MaxCandidates is < 1 or > 20)
            {
                throw new SqlFlowException("maxCandidates must be between 1 and 20.");
            }
        }
    }

    /// <summary>
    /// Projects the payload onto the duplicate-key contract and validates it. The projection IS the
    /// validation, so the control plane and the node cannot disagree about what the check accepts. Throws
    /// <see cref="SqlFlowException"/> naming the offending field.
    /// </summary>
    public DuplicateKeyRequest ToDuplicateKeyRequest()
    {
        if (Operation != ComputeOperations.DuplicateKeys)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.DuplicateKeys} operation carries a duplicate-key request.");
        }

        if (string.IsNullOrWhiteSpace(Schema) || string.IsNullOrWhiteSpace(ObjectName))
        {
            throw new SqlFlowException(
                $"{ComputeOperations.DuplicateKeys} checks one table, so both schema and objectName are required.");
        }

        return new DuplicateKeyRequest
        {
            Schema = Schema.Trim(),
            ObjectName = ObjectName.Trim(),
            Database = NullIfBlank(Database),
            Columns = Columns ?? [],
            Limit = Limit,
        }.Validate(ProviderKind);
    }

    /// <summary>
    /// Projects the payload onto the query-run contract and validates its bounds. The STATEMENT is validated
    /// separately by the provider's read-only guard, which parses T-SQL; this checks only what Core can.
    /// </summary>
    public QueryRunRequest ToQueryRunRequest()
    {
        if (Operation != ComputeOperations.RunQuery)
        {
            throw new SqlFlowException($"Only the {ComputeOperations.RunQuery} operation carries a query.");
        }

        if (string.IsNullOrWhiteSpace(Sql))
        {
            throw new SqlFlowException(
                "runQuery carries the approved statement. A client does not send SQL here: it prepares a query, " +
                "a person approves it, and the control plane redeems the token into this payload.");
        }

        return new QueryRunRequest
        {
            Sql = Sql,
            Database = NullIfBlank(Database),
            MaxRows = MaxRows ?? QueryRunRequest.DefaultMaxRows,
            TimeoutSeconds = TimeoutSeconds ?? QueryRunRequest.DefaultTimeoutSeconds,
        }.Validate();
    }

    /// <summary>
    /// Projects the payload onto the comparison contract and validates it, returning the normalized request.
    /// </summary>
    /// <param name="allowedLinkedServers">
    /// The linked servers policy permits. The control plane passes its configured allowlist; the node passes
    /// the payload's own linked server, because the allowlist decision was made and recorded at enqueue while
    /// every shape and fragment check still has to run again on the queue row.
    /// </param>
    public BaselineComparisonRequest ToComparisonRequest(IReadOnlyCollection<string> allowedLinkedServers)
    {
        if (Operation != ComputeOperations.CompareBaseline)
        {
            throw new SqlFlowException(
                $"Only the {ComputeOperations.CompareBaseline} operation carries a comparison request.");
        }

        if (CompareMode is not { } mode)
        {
            throw new SqlFlowException(
                $"{ComputeOperations.CompareBaseline} requires a compareMode: inventory, schema, or data.");
        }

        if (string.IsNullOrWhiteSpace(LinkedServer))
        {
            throw new SqlFlowException(
                "compareBaseline reaches the old estate through a linked server; linkedServer is required.");
        }

        if (string.IsNullOrWhiteSpace(BaselineDatabase))
        {
            throw new SqlFlowException("compareBaseline requires baselineDatabase, the database on the linked server.");
        }

        if (string.IsNullOrWhiteSpace(Schema))
        {
            throw new SqlFlowException("compareBaseline requires schema, the schema on the current estate.");
        }

        var request = new BaselineComparisonRequest
        {
            Mode = mode,
            LinkedServer = LinkedServer.Trim(),
            BaselineDatabase = BaselineDatabase.Trim(),
            Schema = Schema.Trim(),
            ObjectName = NullIfBlank(ObjectName),
            BaselineSchema = NullIfBlank(BaselineSchema),
            BaselineObjectName = NullIfBlank(BaselineObjectName),
            KeyExpressions = KeyExpressions ?? [],
            CompareColumns = CompareColumns ?? [],
            ExcludeColumnPattern = string.IsNullOrWhiteSpace(ExcludeColumnPattern)
                ? BaselineComparisonRequest.DefaultExcludeColumnPattern
                : ExcludeColumnPattern,
            Where = NullIfBlank(Where),
            Limit = Limit,
            SampleRows = SampleRows,
        };

        return request.Validate(allowedLinkedServers);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True when the value IS a reference in its entirety: one <c>${...}</c> token, or one <c>@alias</c> token.
    /// The same shape rule the lineage server-identity uses: a hybrid like <c>"${env:HOST};Password=..."</c> is
    /// a literal, not a reference, and is refused here.
    /// </summary>
    public static bool IsWholeReference(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return false;
        }

        if (trimmed.StartsWith("${", StringComparison.Ordinal) && trimmed.EndsWith('}')
            && trimmed.IndexOf('}', StringComparison.Ordinal) == trimmed.Length - 1)
        {
            return true;
        }

        return trimmed.StartsWith('@') && trimmed.Length > 1
            && !trimmed.Any(char.IsWhiteSpace)
            && !trimmed.Contains(';', StringComparison.Ordinal)
            && !trimmed.Contains('=', StringComparison.Ordinal);
    }

    private static void ValidateIdentifier(string? value, string field)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length > MaxIdentifierLength)
        {
            throw new SqlFlowException($"{field} is longer than {MaxIdentifierLength} characters.");
        }

        if (HasControlCharacters(value))
        {
            throw new SqlFlowException($"{field} contains control characters.");
        }
    }

    private static bool HasControlCharacters(string value) => value.Any(char.IsControl);
}
