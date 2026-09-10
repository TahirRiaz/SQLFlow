namespace SqlFlow.Core.Ingestion.Legacy;

/// <summary>
/// A raw row of legacy <c>flw.Ingestion</c>, with every column as its nullable database type so it can be
/// filled verbatim from a reader (or a test) and handed to <see cref="IngestionFlowMapper"/>. Property names
/// match the table exactly, including the legacy spellings (for example SkipUpdateExsisting), so the port is
/// a one-to-one read with no guesswork.
/// </summary>
public sealed class LegacyIngestionRow
{
    public int FlowID { get; set; }
    public string? Batch { get; set; }
    public string? SysAlias { get; set; }
    public string? srcServer { get; set; }
    public string? srcDBSchTbl { get; set; }
    public string? trgServer { get; set; }
    public string? trgDBSchTbl { get; set; }
    public string? trgDesiredIndex { get; set; }
    public bool? DeactivateFromBatch { get; set; }
    public bool? StreamData { get; set; }
    public int? NoOfThreads { get; set; }
    public string? KeyColumns { get; set; }
    public string? IncrementalColumns { get; set; }
    public string? IncrementalClauseExp { get; set; }
    public string? DateColumn { get; set; }
    public string? DataSetColumn { get; set; }
    public int? NoOfOverlapDays { get; set; }
    public bool? FetchMinValuesFromSrc { get; set; }
    public bool? SkipUpdateExsisting { get; set; }
    public bool? SkipInsertNew { get; set; }
    public int? FullLoad { get; set; }
    public bool? TruncateTrg { get; set; }
    public bool? TruncatePreTableOnCompletion { get; set; }
    public string? srcFilter { get; set; }
    public bool? srcFilterIsAppend { get; set; }
    public string? IdentityColumn { get; set; }
    public string? HashKeyColumns { get; set; }
    public string? HashKeyType { get; set; }
    public string? IgnoreColumns { get; set; }
    public string? IgnoreColumnsInHashkey { get; set; }
    public string? SysColumns { get; set; }
    public bool? ColumnStoreIndexOnTrg { get; set; }
    public bool? SyncSchema { get; set; }
    public bool? OnErrorResume { get; set; }
    public bool? OnSyncCleanColumnName { get; set; }
    public string? ReplaceInvalidCharsWith { get; set; }
    public bool? OnSyncConvertUnicodeDataType { get; set; }
    public string? CleanColumnNameSQLRegExp { get; set; }
    public bool? trgVersioning { get; set; }
    public bool? InsertUnknownDimRow { get; set; }
    public bool? TokenVersioning { get; set; }
    public int? TokenRetentionDays { get; set; }
    public string? PreProcessOnTrg { get; set; }
    public string? PostProcessOnTrg { get; set; }
    public string? PreInvokeAlias { get; set; }
    public string? PostInvokeAlias { get; set; }
    public string? Assertions { get; set; }
    public bool? MatchKeysInSrcTrg { get; set; }
    public bool? UseBatchUpsertToAvoideLockEscalation { get; set; }
    public int? BatchUpsertRowCount { get; set; }
    public bool? InitLoad { get; set; }
    public DateTime? InitLoadFromDate { get; set; }
    public DateTime? InitLoadToDate { get; set; }
    public string? InitLoadBatchBy { get; set; }
    public int? InitLoadBatchSize { get; set; }
    public string? InitLoadKeyColumn { get; set; }
    public int? InitLoadKeyMaxValue { get; set; }
    public int? BatchOrderBy { get; set; }
    public string? FlowType { get; set; }
    public string? Description { get; set; }
    public int? FromObjectMK { get; set; }
    public int? ToObjectMK { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}

/// <summary>A raw row of legacy <c>flw.IngestionVirtual</c> (a computed column belonging to a flow).</summary>
public sealed class LegacyIngestionVirtualRow
{
    public int VirtualID { get; set; }
    public int FlowID { get; set; }
    public string? ColumnName { get; set; }
    public string? DataType { get; set; }
    public string? DataTypeExp { get; set; }
    public string? SelectExp { get; set; }
}

/// <summary>A raw row of legacy <c>flw.SurrogateKey</c> (a surrogate-key config belonging to a flow). The
/// legacy Key Vault columns are deliberately absent: the surrogate connection is the <c>SurrogateServer</c>
/// alias resolved through flw.DataSource (which carries the secretless contract), not a plaintext secret.</summary>
public sealed class LegacySurrogateKeyRow
{
    public int SurrogateKeyID { get; set; }
    public int FlowID { get; set; }
    public string? SurrogateServer { get; set; }
    public string? SurrogateDbSchTbl { get; set; }
    public string? SurrogateColumn { get; set; }
    public string? KeyColumns { get; set; }
    public string? sKeyColumns { get; set; }
    public string? PreProcess { get; set; }
    public string? PostProcess { get; set; }
    public int? ToObjectMK { get; set; }
}
