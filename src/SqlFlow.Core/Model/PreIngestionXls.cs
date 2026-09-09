namespace SqlFlow.Core.Model;

/// <summary>
/// Rich Excel ingestion metadata - a faithful, modernized port of the <c>flw.PreIngestionXLS</c> table.
/// Field names mirror the original columns. The XLS-specific fields (<see cref="SheetName"/>,
/// <see cref="SheetRange"/>, <see cref="UseSheetIndex"/>) map onto the Excel reader; everything else
/// matches the shared file-ingestion pattern. Provenance and synthetic-key toggles are exposed (V3
/// generic features) so XLS gets the same capabilities as CSV.
/// </summary>
public sealed record PreIngestionXls : IFlowMetadata
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

    // --- Excel specifics ---

    /// <summary>Worksheet to read. Empty = the first worksheet. With <see cref="UseSheetIndex"/> this is a 0-based index.</summary>
    public string? SheetName { get; init; }

    /// <summary>Optional cell range to read, e.g. <c>A1:D100</c>. Empty = the whole used sheet.</summary>
    public string? SheetRange { get; init; }

    /// <summary>Interpret <see cref="SheetName"/> as a 0-based sheet index instead of a name.</summary>
    public bool UseSheetIndex { get; init; }

    /// <summary>First row contains column headers.</summary>
    public bool FirstRowHasHeader { get; init; } = true;

    /// <summary>Expected number of columns; 0 disables the check.</summary>
    public int ExpectedColumnCount { get; init; }

    /// <summary>Include the source row line number as a column.</summary>
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

    /// <summary>Flow type discriminator (always "xls").</summary>
    public string FlowType { get; init; } = "xls";

    // --- Lineage ---
    public int? FromObjectMk { get; init; }
    public int? ToObjectMk { get; init; }

    // --- Audit ---
    public string? CreatedBy { get; init; }
    public DateTime? CreatedDate { get; init; }

    /// <summary>Binds the rich metadata from a generic <see cref="SourceSpec"/> (the engine envelope).</summary>
    public static PreIngestionXls FromSource(SourceSpec source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var o = source.Options;

        return new PreIngestionXls
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
            SheetName = NullableOption(o, "sheetName"),
            SheetRange = NullableOption(o, "sheetRange"),
            UseSheetIndex = o.GetBool("useSheetIndex", false),
            FirstRowHasHeader = o.GetBool("header", o.GetBool("firstRowHasHeader", true)),
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
