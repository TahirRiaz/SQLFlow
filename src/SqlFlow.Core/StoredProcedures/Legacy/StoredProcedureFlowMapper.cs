using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.StoredProcedures.Legacy;

/// <summary>
/// Maps a legacy <c>flw.StoredProcedure</c> row into the V3 <see cref="StoredProcedureFlow"/> model. Pure and
/// lossless: every column has a destination, a legacy NULL resolves to the documented table default, and a
/// missing required value (trgServer / trgDBSchSP / SysAlias) fails fast with a precise, flow-identified error.
/// </summary>
public static class StoredProcedureFlowMapper
{
    public static StoredProcedureFlow FromLegacy(LegacyStoredProcedureRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return new StoredProcedureFlow
        {
            FlowId = row.FlowID,
            Batch = NullIfBlank(row.Batch),
            SysAlias = Require(row.SysAlias, row.FlowID, "SysAlias"),
            Server = Require(row.trgServer, row.FlowID, "trgServer"),
            Procedure = ParseObject(row.trgDBSchSP, row.FlowID, "trgDBSchSP"),
            OnErrorResume = row.OnErrorResume ?? true,
            PostInvokeAlias = NullIfBlank(row.PostInvokeAlias),
            Description = NullIfBlank(row.Description),
            FlowType = NullIfBlank(row.FlowType) ?? "sp",
            DeactivateFromBatch = row.DeactivateFromBatch ?? false,
            FromObjectMK = row.FromObjectMK,
            ToObjectMK = row.ToObjectMK,
            CreatedBy = NullIfBlank(row.CreatedBy),
            CreatedDate = row.CreatedDate,
        };
    }

    private static string Require(string? value, int flowId, string column)
        => NullIfBlank(value) ?? throw new SqlFlowException($"Stored-procedure flow {flowId} is missing the required '{column}'.");

    private static RelationalObject ParseObject(string? name, int flowId, string column)
    {
        var value = NullIfBlank(name)
            ?? throw new SqlFlowException($"Stored-procedure flow {flowId} is missing the required '{column}'.");
        try
        {
            return RelationalObject.Parse(value);
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"Stored-procedure flow {flowId}, column '{column}': {ex.Message}", ex);
        }
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
