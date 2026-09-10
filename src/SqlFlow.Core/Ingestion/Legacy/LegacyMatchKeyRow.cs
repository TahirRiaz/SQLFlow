namespace SqlFlow.Core.Ingestion.Legacy;

/// <summary>
/// One raw <c>flw.MatchKey</c> row, property names matching the table columns exactly, every column as its
/// nullable database type. The ingestion-integrated key-match pass consumes the per-flow policy fields
/// (ActionType, ActionThresholdPercent, IgnoreDeletedRowsAfter, KeyColumns, DateColumn, srcFilter, trgFilter);
/// the standalone-flow addressing columns are carried losslessly for the future mkey flow type.
/// </summary>
public sealed class LegacyMatchKeyRow
{
    public int MatchKeyID { get; set; }
    public int FlowID { get; set; }
    public string? Batch { get; set; }
    public string? SysAlias { get; set; }
    public string? srcServer { get; set; }
    public string? srcDatabase { get; set; }
    public string? srcSchema { get; set; }
    public string? srcObject { get; set; }
    public string? trgServer { get; set; }
    public string? trgDBSchTbl { get; set; }
    public bool? DeactivateFromBatch { get; set; }
    public string? KeyColumns { get; set; }
    public string? DateColumn { get; set; }
    public string? ActionType { get; set; }
    public int? ActionThresholdPercent { get; set; }
    public int? IgnoreDeletedRowsAfter { get; set; }
    public string? srcFilter { get; set; }
    public string? trgFilter { get; set; }
    public bool? OnErrorResume { get; set; }
    public string? PreProcessOnTrg { get; set; }
    public string? PostProcessOnTrg { get; set; }
    public string? Description { get; set; }
    public int? ToObjectMK { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}
