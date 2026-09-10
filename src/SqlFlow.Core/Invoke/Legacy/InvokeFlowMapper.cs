namespace SqlFlow.Core.Invoke.Legacy;

/// <summary>
/// Maps a legacy <c>flw.Invoke</c> row into the V3 <see cref="InvokeDefinition"/>. Pure and lossless: every
/// column has a destination, a legacy NULL resolves to the documented table default, a missing required value
/// (InvokeAlias) fails fast, and the InvokeType code is validated (a removed host-execution code or an unknown
/// code throws rather than silently becoming a runnable type). The service-principal aliases collapse to
/// secretless <c>@alias</c> references.
/// </summary>
public static class InvokeFlowMapper
{
    public static InvokeDefinition FromLegacy(LegacyInvokeRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new InvokeDefinition
        {
            FlowId = row.FlowID,
            Batch = NullIfBlank(row.Batch),
            SysAlias = NullIfBlank(row.SysAlias),
            InvokeAlias = Require(row.InvokeAlias, row.FlowID, "InvokeAlias"),
            InvokeType = InvokeTypeCodes.Parse(row.InvokeType),
            PipelineName = NullIfBlank(row.PipelineName),
            RunbookName = NullIfBlank(row.RunbookName),
            ParameterJson = NullIfBlank(row.ParameterJSON),
            OnErrorResume = row.OnErrorResume ?? true,
            DeactivateFromBatch = row.DeactivateFromBatch ?? false,
            TargetServicePrincipalReference = Reference(row.trgServicePrincipalAlias),
            SourceServicePrincipalReference = Reference(row.srcServicePrincipalAlias),
            ToObjectMK = row.ToObjectMK,
            CreatedBy = NullIfBlank(row.CreatedBy),
            CreatedDate = row.CreatedDate,
        };
    }

    private static string Require(string? value, int flowId, string column)
        => NullIfBlank(value) ?? throw new SqlFlowException($"Invoke flow {flowId} is missing the required '{column}'.");

    private static string? Reference(string? alias)
    {
        var value = NullIfBlank(alias);
        return value is null ? null : "@" + value;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
