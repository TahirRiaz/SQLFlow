namespace SqlFlow.Core.Invoke.Legacy;

/// <summary>
/// A raw row of legacy <c>flw.Invoke</c>, every column as its nullable database type, filled verbatim from a
/// reader (or a test) and handed to <see cref="InvokeFlowMapper"/>. Property names match the table exactly.
/// </summary>
public sealed class LegacyInvokeRow
{
    public int FlowID { get; set; }
    public string? Batch { get; set; }
    public string? SysAlias { get; set; }
    public string? trgServicePrincipalAlias { get; set; }
    public string? InvokeAlias { get; set; }
    public string? InvokeType { get; set; }
    public string? srcServicePrincipalAlias { get; set; }
    public string? PipelineName { get; set; }
    public string? RunbookName { get; set; }
    public string? ParameterJSON { get; set; }
    public bool? OnErrorResume { get; set; }
    public bool? DeactivateFromBatch { get; set; }
    public int? ToObjectMK { get; set; }
    public string? CreatedBy { get; set; }
    public DateTime? CreatedDate { get; set; }
}
