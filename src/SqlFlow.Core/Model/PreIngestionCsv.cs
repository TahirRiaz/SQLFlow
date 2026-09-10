namespace SqlFlow.Core.Model;

/// <summary>
/// Rich CSV ingestion metadata - a faithful, modernized port of the <c>flw.PreIngestionCSV</c> table.
/// Field names mirror the original columns so the metadata model is preserved across the rewrite.
/// The engine remains source-agnostic; this is the strongly-typed metadata the CSV reader consumes.
/// </summary>
public sealed record PreIngestionCsv : IFlowMetadata
{
    /// <summary>Stable flow identity (the universal key in the metadata store), identical on every execution.</summary>
    public Guid FlowId { get; init; }

    /// <summary>Batch this flow belongs to; groups flows for execution.</summary>
    public string? Batch { get; init; }

    /// <summary>Mandatory alias identifying the source system. Used to group flows.</summary>
    public required string SysAlias { get; init; }

    /// <summary>Alias of a registered service principal for source authentication.</summary>
    public string? ServicePrincipalAlias { get; init; }

    // --- Source location & file lifecycle ---

    /// <summary>Source path (local or ADLS Gen2 container path).</summary>
    public required string SrcPath { get; init; }

    /// <summary>
    /// Optional regular expression applied to each candidate file's full path (which includes the file
    /// name), so it can restrict by folder, by name, or both. Applied after the <see cref="SrcFile"/>
    /// glob and intersected with the date window. Empty means no path filtering.
    /// </summary>
    public string? SrcPathMask { get; init; }

    /// <summary>
    /// File-name glob used to enumerate candidate files (e.g. <c>*.csv</c>, the default). For regular
    /// expression matching on the name, use <see cref="SrcPathMask"/>, which matches the full path.
    /// </summary>
    public string? SrcFile { get; init; }

    /// <summary>Search sub-directories of the source path.</summary>
    public bool SearchSubDirectories { get; init; }

    /// <summary>If set, copy source file(s) here before ingestion.</summary>
    public string? CopyToPath { get; init; }

    /// <summary>Delete the source file after it has been ingested.</summary>
    public bool SrcDeleteIngested { get; init; }

    /// <summary>Delete the source file at the source path.</summary>
    public bool SrcDeleteAtPath { get; init; }

    /// <summary>If set, compress source file(s) to this path.</summary>
    public string? ZipToPath { get; init; }

    /// <summary>Source encoding (ASCII, Unicode, UTF32, UTF7, UTF8). Null = auto-detect.</summary>
    public string? SrcEncoding { get; init; }

    // --- Target ---

    /// <summary>Alias of the SQL Server instance hosting the target table.</summary>
    public string? TrgServer { get; init; }

    /// <summary>Fully qualified target as [Database].[Schema].[Object].</summary>
    public string? TrgDbSchTbl { get; init; }

    /// <summary>Desired index definition for the target table.</summary>
    public string? TrgDesiredIndex { get; init; }

    // --- Parsing ---

    /// <summary>Column delimiter.</summary>
    public string? ColumnDelimiter { get; init; }

    /// <summary>Text qualifier enclosing string values.</summary>
    public string? TextQualifier { get; init; }

    /// <summary>Fixed column widths (for fixed-width files).</summary>
    public string? ColumnWidths { get; init; }

    /// <summary>First row contains column headers.</summary>
    public bool FirstRowHasHeader { get; init; } = true;

    /// <summary>Character used to escape delimiters/qualifiers within a field.</summary>
    public string? EscapeCharacter { get; init; }

    /// <summary>Lines starting with this character are treated as comments.</summary>
    public string? CommentCharacter { get; init; }

    /// <summary>Expected number of columns; 0 disables the check.</summary>
    public int ExpectedColumnCount { get; init; }

    /// <summary>Use the first row's column count as the expected count.</summary>
    public bool FirstRowSetsExpectedColumnCount { get; init; }

    /// <summary>Rows to skip at the start of the data (after the header).</summary>
    public int SkipStartingDataRows { get; init; }

    /// <summary>Rows to skip at the end of the data (e.g. footers).</summary>
    public int SkipEndingDataRows { get; init; }

    /// <summary>Skip empty rows during ingestion.</summary>
    public bool SkipEmptyRows { get; init; } = true;

    /// <summary>Include the source file line number as a column.</summary>
    public bool IncludeFileLineNumber { get; init; }

    /// <summary>Trim whitespace from each value.</summary>
    public bool TrimResults { get; init; }

    /// <summary>Strip non-printable control characters.</summary>
    public bool StripControlChars { get; init; }

    /// <summary>Maximum read buffer size in bytes.</summary>
    public int MaxBufferSize { get; init; } = 1024;

    /// <summary>Maximum rows to read; 0 = all.</summary>
    public int MaxRows { get; init; }

    /// <summary>Include the file path with the file name in the FileName_DW column.</summary>
    public bool ShowPathWithFileName { get; init; }

    // --- System (provenance) column toggles: which DW columns to inject. Default on. ---

    /// <summary>Inject the FileName_DW provenance column.</summary>
    public bool IncludeFileName { get; init; } = true;

    /// <summary>Inject the FileDate_DW provenance column (source file modified date).</summary>
    public bool IncludeFileDate { get; init; } = true;

    /// <summary>Inject the FileRowDate_DW provenance column (ingest timestamp).</summary>
    public bool IncludeFileRowDate { get; init; } = true;

    /// <summary>Inject the FileSize_DW provenance column.</summary>
    public bool IncludeFileSize { get; init; } = true;

    /// <summary>Inject the DataSet_DW provenance column (dataset/partition date).</summary>
    public bool IncludeDataSet { get; init; } = true;

    /// <summary>Inject the RowNumber_DW provenance column (row count within each file).</summary>
    public bool IncludeRowNumber { get; init; } = true;

    // --- Schema & types ---

    /// <summary>Synchronize the target schema with the source (schema evolution).</summary>
    public bool SyncSchema { get; init; } = true;

    /// <summary>Override default column data type when creating target columns.</summary>
    public string? DefaultColDataType { get; init; }

    /// <summary>Infer/assert source column data types.</summary>
    public bool FetchDataTypes { get; init; }

    // --- Row hash key (synthetic dedup key when the file has no business key) ---

    /// <summary>
    /// Inject a per-row hash key column (HashKey_DW) computed at ingestion time. Useful when the source
    /// has no business key: the hash becomes a synthetic key for change detection and deduplication.
    /// </summary>
    public bool IncludeHashKey { get; init; }

    /// <summary>
    /// Comma-separated source columns to feed the row hash. Empty means all source columns. Order is
    /// significant and is honoured as listed.
    /// </summary>
    public string? HashKeyColumns { get; init; }

    /// <summary>Hash algorithm for the row key: SHA2_512 (default), SHA2_256, SHA1, or MD5.</summary>
    public string HashKeyType { get; init; } = "SHA2_512";

    // --- Concatenation key (readable composite key built from chosen columns) ---

    /// <summary>
    /// Inject a per-row concatenation key column (ConcatKey_DW) built at ingestion time by joining the
    /// chosen columns with <see cref="ConcatKeySeparator"/>. A readable composite key for joins or dedup.
    /// </summary>
    public bool IncludeConcatKey { get; init; }

    /// <summary>
    /// Comma-separated source columns to concatenate. Empty means all source columns. Order is
    /// significant and is honoured as listed.
    /// </summary>
    public string? ConcatKeyColumns { get; init; }

    /// <summary>Separator placed between concatenated values. Default is a vertical bar.</summary>
    public string ConcatKeySeparator { get; init; } = "|";

    // --- Incremental directives (consumed by the incremental load, not the raw file ingestion) ---

    /// <summary>
    /// Force a full (re)load instead of an incremental one. Not used while loading the raw file into
    /// staging; it is an instruction for the downstream incremental load.
    /// </summary>
    public bool FullLoad { get; init; }

    // --- Processing hooks ---

    /// <summary>WHERE clause appended to the generated transformation view.</summary>
    public string? PreFilter { get; init; }

    /// <summary>Stored procedure run on the target before ingestion.</summary>
    public string? PreProcessOnTrg { get; init; }

    /// <summary>Stored procedure run on the target after ingestion.</summary>
    public string? PostProcessOnTrg { get; init; }

    /// <summary>Registered invoke alias to run before ingestion.</summary>
    public string? PreInvokeAlias { get; init; }

    // --- Incremental window ---

    /// <summary>Earliest file creation date to include (inclusive).</summary>
    public string? InitFromFileDate { get; init; }

    /// <summary>Latest file creation date to include (inclusive).</summary>
    public string? InitToFileDate { get; init; }

    /// <summary>
    /// Exclusive lower bound on file modified date, injected by the engine from the incremental
    /// watermark: only files strictly newer than this are read. Not authored directly in YAML.
    /// </summary>
    public string? IncrementalAfterDate { get; init; }

    // --- Batch / orchestration ---

    /// <summary>Continue on row errors (errors are logged).</summary>
    public bool OnErrorResume { get; init; } = true;

    /// <summary>Execution order within a batch.</summary>
    public int BatchOrderBy { get; init; }

    /// <summary>Exclude this flow from batch execution.</summary>
    public bool DeactivateFromBatch { get; init; }

    /// <summary>Enable event-driven execution.</summary>
    public bool EnableEventExecution { get; init; }

    /// <summary>Degree of parallelism for ingestion.</summary>
    public int NoOfThreads { get; init; } = 4;

    /// <summary>Flow type discriminator (always "csv").</summary>
    public string FlowType { get; init; } = "csv";

    // --- Lineage ---

    /// <summary>Source object master key (lineage/ordering).</summary>
    public int? FromObjectMk { get; init; }

    /// <summary>Target object master key (lineage/ordering).</summary>
    public int? ToObjectMk { get; init; }

    // --- Audit ---

    public string? CreatedBy { get; init; }

    public DateTime? CreatedDate { get; init; }

    /// <summary>
    /// Binds the rich metadata from a generic <see cref="SourceSpec"/> (the engine envelope). YAML
    /// option keys map onto the original column names; <see cref="SourceSpec.Location"/> is the path.
    /// </summary>
    public static PreIngestionCsv FromSource(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var o = source.Options;

        return new PreIngestionCsv
        {
            FlowId = o.TryGetValue("flowId", out var flowId) && Guid.TryParse(flowId, out var parsed) ? parsed : Guid.Empty,
            SysAlias = o.GetString("sysAlias", "default"),
            SrcPath = source.Location ?? o.GetString("srcPath", string.Empty),
            SrcPathMask = NullableOption(o, "srcPathMask"),
            SrcFile = NullableOption(o, "srcFile"),
            SearchSubDirectories = o.GetBool("searchSubDirectories", false),
            CopyToPath = NullableOption(o, "copyToPath"),
            ZipToPath = NullableOption(o, "zipToPath"),
            SrcDeleteIngested = o.GetBool("srcDeleteIngested", false),
            SrcDeleteAtPath = o.GetBool("srcDeleteAtPath", false),
            ColumnDelimiter = o.GetString("delimiter", o.GetString("columnDelimiter", ",")),
            TextQualifier = o.GetString("textQualifier", o.GetString("qualifier", "\"")),
            EscapeCharacter = NullableOption(o, "escapeCharacter"),
            CommentCharacter = NullableOption(o, "commentCharacter"),
            ColumnWidths = NullableOption(o, "columnWidths"),
            FirstRowHasHeader = o.GetBool("header", o.GetBool("firstRowHasHeader", true)),
            FirstRowSetsExpectedColumnCount = o.GetBool("firstRowSetsExpectedColumnCount", false),
            SrcEncoding = NullableOption(o, "encoding") ?? NullableOption(o, "srcEncoding"),
            SkipStartingDataRows = o.GetInt("skipStartingDataRows", 0),
            SkipEndingDataRows = o.GetInt("skipEndingDataRows", 0),
            SkipEmptyRows = o.GetBool("skipEmptyRows", true),
            TrimResults = o.GetBool("trimResults", o.GetBool("trim", false)),
            StripControlChars = o.GetBool("stripControlChars", false),
            IncludeFileLineNumber = o.GetBool("includeFileLineNumber", false),
            ShowPathWithFileName = o.GetBool("showPathWithFileName", false),
            IncludeFileName = o.GetBool("includeFileName", true),
            IncludeFileDate = o.GetBool("includeFileDate", true),
            IncludeFileRowDate = o.GetBool("includeFileRowDate", true),
            IncludeFileSize = o.GetBool("includeFileSize", true),
            IncludeDataSet = o.GetBool("includeDataSet", true),
            IncludeRowNumber = o.GetBool("includeRowNumber", true),
            MaxBufferSize = o.GetInt("maxBufferSize", 1024),
            MaxRows = o.GetInt("maxRows", 0),
            ExpectedColumnCount = o.GetInt("expectedColumnCount", 0),
            DefaultColDataType = NullableOption(o, "defaultColDataType"),
            SyncSchema = o.GetBool("syncSchema", true),
            TrgDbSchTbl = NullableOption(o, "trgDBSchTbl"),
            InitFromFileDate = NullableOption(o, "initFromFileDate"),
            InitToFileDate = NullableOption(o, "initToFileDate"),
            IncrementalAfterDate = NullableOption(o, "incrementalAfterDate"),
            IncludeHashKey = o.GetBool("includeHashKey", false),
            HashKeyColumns = NullableOption(o, "hashKeyColumns"),
            HashKeyType = o.GetString("hashKeyType", "SHA2_512"),
            IncludeConcatKey = o.GetBool("includeConcatKey", false),
            ConcatKeyColumns = NullableOption(o, "concatKeyColumns"),
            ConcatKeySeparator = o.GetString("concatKeySeparator", "|"),
            FullLoad = o.GetBool("fullLoad", false),
        };
    }

    private static string? NullableOption(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
}
