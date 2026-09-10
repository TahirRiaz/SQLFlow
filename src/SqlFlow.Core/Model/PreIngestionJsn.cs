namespace SqlFlow.Core.Model;

/// <summary>
/// Rich JSON ingestion metadata: a modernized port of the <c>flw.PreIngestionJSN</c> table. The shared
/// file-source, provenance, and synthetic-key fields match the CSV and XLS readers (one code path); the
/// JSON-specific part replaces the original cryptic <c>JsonToDataTableCode</c> blob with a declarative,
/// path-based flatten configuration (root path, include / exclude / json paths, separator, depth, array
/// handling, column mappings). Field names mirror the original columns where they carry over.
/// </summary>
public sealed record PreIngestionJsn : IFlowMetadata
{
    /// <summary>Stable flow identity (the universal key in the metadata store), identical on every execution.</summary>
    public Guid FlowId { get; init; }

    public string? Batch { get; init; }
    public required string SysAlias { get; init; }
    public string? ServicePrincipalAlias { get; init; }

    // --- Source location & file lifecycle ---
    public required string SrcPath { get; init; }
    public string? SrcPathMask { get; init; }
    public string? SrcFile { get; init; }
    public bool SearchSubDirectories { get; init; }
    public string? CopyToPath { get; init; }
    public bool SrcDeleteIngested { get; init; }
    public bool SrcDeleteAtPath { get; init; }
    public string? ZipToPath { get; init; }

    // --- JSON flatten specifics (replaces JsonToDataTableCode) ---

    /// <summary>JSONPath the records live under (e.g. <c>$.data.records</c>). <c>$</c> = the document root.</summary>
    public string RootPath { get; init; } = "$";

    /// <summary>Comma-separated whitelist of paths to flatten. Empty = flatten everything.</summary>
    public string? IncludePaths { get; init; }

    /// <summary>Comma-separated paths whose subtree is dropped from the output.</summary>
    public string? ExcludePaths { get; init; }

    /// <summary>Comma-separated paths kept verbatim as a single JSON-string column.</summary>
    public string? JsonPaths { get; init; }

    /// <summary>Comma-separated array paths to explode: each element becomes its own output row.</summary>
    public string? ExplodePaths { get; init; }

    /// <summary>Schema-evolution aliases mapping version-specific paths to one column: <c>col=$.a|$.b; col2=$.c|$.d</c>.</summary>
    public string? PathAliases { get; init; }

    /// <summary>Semicolon-separated <c>jsonPath=columnName</c> overrides.</summary>
    public string? ColumnMappings { get; init; }

    /// <summary>Separator joining nested keys into a column name (default <c>_</c>).</summary>
    public string Separator { get; init; } = "_";

    /// <summary>Maximum nesting depth flattened into columns; deeper values become JSON strings.</summary>
    public int MaxDepth { get; init; } = 10;

    /// <summary>
    /// Bound on how many rows one record may explode into before the read fails loudly. Mirrors
    /// <c>JsonPathFlattener.DefaultMaxRowsPerRecord</c> (SqlFlow.Sources cannot be referenced from here).
    /// </summary>
    public int MaxRowsPerRecord { get; init; } = 1_000_000;

    /// <summary>How unconfigured arrays become a column: to_json (default), first_element, join, count, skip.</summary>
    public string ArrayHandling { get; init; } = "to_json";

    /// <summary>Separator used when <see cref="ArrayHandling"/> is <c>join</c> (default comma).</summary>
    public string JoinSeparator { get; init; } = ",";

    /// <summary>Expected number of columns; 0 disables the check.</summary>
    public int ExpectedColumnCount { get; init; }

    /// <summary>Include the source record line number as a column.</summary>
    public bool IncludeFileLineNumber { get; init; }

    /// <summary>Include the file path with the file name in the FileName_DW column.</summary>
    public bool ShowPathWithFileName { get; init; }

    // --- Provenance column toggles (default on) ---
    public bool IncludeFileName { get; init; } = true;
    public bool IncludeFileDate { get; init; } = true;
    public bool IncludeFileRowDate { get; init; } = true;
    public bool IncludeFileSize { get; init; } = true;
    public bool IncludeDataSet { get; init; } = true;
    public bool IncludeRowNumber { get; init; } = true;

    // --- Synthetic keys (V3 generic feature) ---
    public bool IncludeHashKey { get; init; }
    public string? HashKeyColumns { get; init; }
    public string HashKeyType { get; init; } = "SHA2_512";
    public bool IncludeConcatKey { get; init; }
    public string? ConcatKeyColumns { get; init; }
    public string ConcatKeySeparator { get; init; } = "|";

    // --- Target ---
    public string? TrgServer { get; init; }
    public string? TrgDbSchTbl { get; init; }
    public string? TrgDesiredIndex { get; init; }

    // --- Schema & types ---
    public bool SyncSchema { get; init; } = true;
    public string? DefaultColDataType { get; init; }
    public bool FetchDataTypes { get; init; }

    // --- Processing hooks ---
    public string? PreFilter { get; init; }
    public string? PreProcessOnTrg { get; init; }
    public string? PostProcessOnTrg { get; init; }
    public string? PreInvokeAlias { get; init; }

    // --- Incremental window ---
    public string? InitFromFileDate { get; init; }
    public string? InitToFileDate { get; init; }

    /// <summary>Exclusive lower bound on file modified date, injected by the engine from the incremental watermark.</summary>
    public string? IncrementalAfterDate { get; init; }

    // --- Batch / orchestration ---
    public bool OnErrorResume { get; init; } = true;
    public int BatchOrderBy { get; init; }
    public bool DeactivateFromBatch { get; init; }
    public bool EnableEventExecution { get; init; }
    public int NoOfThreads { get; init; } = 4;

    /// <summary>Flow type discriminator (always "jsn").</summary>
    public string FlowType { get; init; } = "jsn";

    // --- Lineage ---
    public int? FromObjectMk { get; init; }
    public int? ToObjectMk { get; init; }

    // --- Audit ---
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }

    /// <summary>Binds the rich metadata from a generic <see cref="SourceSpec"/> (the engine envelope).</summary>
    public static PreIngestionJsn FromSource(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var o = source.Options;

        return new PreIngestionJsn
        {
            FlowId = o.TryGetValue("flowId", out var flowId) && Guid.TryParse(flowId, out var parsed) ? parsed : Guid.Empty,
            SysAlias = o.GetString("sysAlias", "default"),
            ServicePrincipalAlias = NullableOption(o, "servicePrincipalAlias"),
            SrcPath = source.Location ?? o.GetString("srcPath", string.Empty),
            SrcPathMask = NullableOption(o, "srcPathMask"),
            SrcFile = NullableOption(o, "srcFile"),
            SearchSubDirectories = o.GetBool("searchSubDirectories", false),
            CopyToPath = NullableOption(o, "copyToPath"),
            ZipToPath = NullableOption(o, "zipToPath"),
            SrcDeleteIngested = o.GetBool("srcDeleteIngested", false),
            SrcDeleteAtPath = o.GetBool("srcDeleteAtPath", false),
            RootPath = o.GetString("rootPath", "$"),
            IncludePaths = NullableOption(o, "includePaths"),
            ExcludePaths = NullableOption(o, "excludePaths"),
            JsonPaths = NullableOption(o, "jsonPaths"),
            ExplodePaths = NullableOption(o, "explodePaths"),
            PathAliases = NullableOption(o, "pathAliases"),
            ColumnMappings = NullableOption(o, "columnMappings"),
            Separator = o.GetString("separator", "_"),
            MaxDepth = o.GetInt("maxDepth", 10),
            MaxRowsPerRecord = o.GetInt("maxRowsPerRecord", 1_000_000),
            ArrayHandling = o.GetString("arrayHandling", "to_json"),
            JoinSeparator = o.GetString("joinSeparator", ","),
            ExpectedColumnCount = o.GetInt("expectedColumnCount", 0),
            IncludeFileLineNumber = o.GetBool("includeFileLineNumber", false),
            ShowPathWithFileName = o.GetBool("showPathWithFileName", false),
            IncludeFileName = o.GetBool("includeFileName", true),
            IncludeFileDate = o.GetBool("includeFileDate", true),
            IncludeFileRowDate = o.GetBool("includeFileRowDate", true),
            IncludeFileSize = o.GetBool("includeFileSize", true),
            IncludeDataSet = o.GetBool("includeDataSet", true),
            IncludeRowNumber = o.GetBool("includeRowNumber", true),
            IncludeHashKey = o.GetBool("includeHashKey", false),
            HashKeyColumns = NullableOption(o, "hashKeyColumns"),
            HashKeyType = o.GetString("hashKeyType", "SHA2_512"),
            IncludeConcatKey = o.GetBool("includeConcatKey", false),
            ConcatKeyColumns = NullableOption(o, "concatKeyColumns"),
            ConcatKeySeparator = o.GetString("concatKeySeparator", "|"),
            TrgServer = NullableOption(o, "trgServer"),
            TrgDbSchTbl = NullableOption(o, "trgDBSchTbl"),
            TrgDesiredIndex = NullableOption(o, "trgDesiredIndex"),
            SyncSchema = o.GetBool("syncSchema", true),
            DefaultColDataType = NullableOption(o, "defaultColDataType"),
            FetchDataTypes = o.GetBool("fetchDataTypes", false),
            PreFilter = NullableOption(o, "preFilter"),
            PreProcessOnTrg = NullableOption(o, "preProcessOnTrg"),
            PostProcessOnTrg = NullableOption(o, "postProcessOnTrg"),
            PreInvokeAlias = NullableOption(o, "preInvokeAlias"),
            InitFromFileDate = NullableOption(o, "initFromFileDate"),
            InitToFileDate = NullableOption(o, "initToFileDate"),
            IncrementalAfterDate = NullableOption(o, "incrementalAfterDate"),
            OnErrorResume = o.GetBool("onErrorResume", true),
            DeactivateFromBatch = o.GetBool("deactivateFromBatch", false),
            EnableEventExecution = o.GetBool("enableEventExecution", false),
            NoOfThreads = o.GetInt("noOfThreads", 4),
        };
    }

    private static string? NullableOption(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
