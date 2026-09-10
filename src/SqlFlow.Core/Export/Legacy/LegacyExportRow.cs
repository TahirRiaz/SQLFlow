namespace SqlFlow.Core.Export.Legacy;

/// <summary>
/// A raw row of legacy <c>flw.Export</c>, every column as its nullable database type, filled verbatim from a
/// reader (or a test) and handed to <see cref="ExportFlowMapper"/>. Property names match the table exactly. The
/// legacy Azure credential columns are absent: the destination is the <c>ServicePrincipalAlias</c> resolved
/// through the connection registry (secretless), not plaintext secrets.
/// </summary>
public sealed class LegacyExportRow
{
    public int FlowID { get; set; }
    public string? Batch { get; set; }
    public string? SysAlias { get; set; }
    public string? srcServer { get; set; }
    public string? srcDBSchTbl { get; set; }
    public string? srcWithHint { get; set; }
    public string? srcFilter { get; set; }
    public string? IncrementalColumn { get; set; }
    public string? DateColumn { get; set; }
    public int? NoOfOverlapDays { get; set; }
    public DateTime? FromDate { get; set; }
    public DateTime? ToDate { get; set; }
    public string? ExportBy { get; set; }
    public int? ExportSize { get; set; }
    public string? ServicePrincipalAlias { get; set; }
    public string? trgPath { get; set; }
    public string? trgFileName { get; set; }
    public string? trgFiletype { get; set; }
    public string? trgEncoding { get; set; }
    public string? CompressionType { get; set; }
    public string? ColumnDelimiter { get; set; }
    public string? TextQualifier { get; set; }
    public bool? AddTimeStampToFileName { get; set; }
    public string? Subfolderpattern { get; set; }
    public int? NoOfThreads { get; set; }
    public bool? ZipTrg { get; set; }
    public bool? OnErrorResume { get; set; }
    public string? PostInvokeAlias { get; set; }
    public bool? DeactivateFromBatch { get; set; }
    public string? FlowType { get; set; }
    public int? FromObjectMK { get; set; }
    public int? ToObjectMK { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}
