namespace SqlFlow.Core.StoredProcedures.Legacy;

/// <summary>
/// A raw row of legacy <c>flw.StoredProcedure</c>, every column as its nullable database type, filled verbatim
/// from a reader (or a test) and handed to <see cref="StoredProcedureFlowMapper"/>. Property names match the
/// table exactly.
/// </summary>
public sealed class LegacyStoredProcedureRow
{
    public int FlowID { get; set; }
    public string? Batch { get; set; }
    public string? SysAlias { get; set; }
    public string? trgServer { get; set; }
    public string? trgDBSchSP { get; set; }
    public bool? OnErrorResume { get; set; }
    public string? PostInvokeAlias { get; set; }
    public string? Description { get; set; }
    public string? FlowType { get; set; }
    public bool? DeactivateFromBatch { get; set; }
    public int? FromObjectMK { get; set; }
    public int? ToObjectMK { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}
