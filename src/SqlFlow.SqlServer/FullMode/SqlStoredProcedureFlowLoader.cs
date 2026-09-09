using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.StoredProcedures;
using SqlFlow.Core.StoredProcedures.Legacy;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>Reads flw.StoredProcedure rows and maps them to <see cref="StoredProcedureFlow"/> through the
/// lossless mapper. Columns are read by name, not position.</summary>
public sealed class SqlStoredProcedureFlowLoader : IStoredProcedureFlowLoader
{
    private readonly string _controlConnectionString;

    public SqlStoredProcedureFlowLoader(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public async Task<StoredProcedureFlow> LoadByIdAsync(int flowId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var row = await ReadAsync(connection, flowId, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"No stored-procedure flow with FlowID {flowId} exists in the control database.");
        return StoredProcedureFlowMapper.FromLegacy(row);
    }

    public async Task<IReadOnlyList<StoredProcedureFlow>> LoadBatchAsync(string batch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);

        var flowIds = new List<int>();
        await using (var connection = new SqlConnection(_controlConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand(
                "SELECT [FlowID] FROM [flw].[StoredProcedure] WHERE [Batch] = @batch AND ISNULL([DeactivateFromBatch], 0) = 0 ORDER BY [FlowID];",
                connection) { CommandTimeout = 0 };
            command.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.NVarChar, 70) { Value = batch });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flowIds.Add(reader.GetInt32(0));
            }
        }

        var flows = new List<StoredProcedureFlow>(flowIds.Count);
        foreach (var flowId in flowIds)
        {
            flows.Add(await LoadByIdAsync(flowId, ct).ConfigureAwait(false));
        }

        return flows;
    }

    private static async Task<LegacyStoredProcedureRow?> ReadAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT [FlowID],[Batch],[SysAlias],[trgServer],[trgDBSchSP],[OnErrorResume],[PostInvokeAlias],[Description]," +
            "[FlowType],[DeactivateFromBatch],[FromObjectMK],[ToObjectMK],[CreatedBy],[CreatedDate] " +
            "FROM [flw].[StoredProcedure] WHERE [FlowID] = @id;",
            connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LegacyStoredProcedureRow
        {
            FlowID = reader.GetInt32(reader.GetOrdinal("FlowID")),
            Batch = Str(reader, "Batch"),
            SysAlias = Str(reader, "SysAlias"),
            trgServer = Str(reader, "trgServer"),
            trgDBSchSP = Str(reader, "trgDBSchSP"),
            OnErrorResume = Bool(reader, "OnErrorResume"),
            PostInvokeAlias = Str(reader, "PostInvokeAlias"),
            Description = Str(reader, "Description"),
            FlowType = Str(reader, "FlowType"),
            DeactivateFromBatch = Bool(reader, "DeactivateFromBatch"),
            FromObjectMK = Int(reader, "FromObjectMK"),
            ToObjectMK = Int(reader, "ToObjectMK"),
            CreatedBy = Str(reader, "CreatedBy"),
            CreatedDate = Date(reader, "CreatedDate"),
        };
    }

    private static string? Str(SqlDataReader r, string c) { var o = r.GetOrdinal(c); return r.IsDBNull(o) ? null : r.GetString(o); }

    private static int? Int(SqlDataReader r, string c) { var o = r.GetOrdinal(c); return r.IsDBNull(o) ? null : r.GetInt32(o); }

    private static bool? Bool(SqlDataReader r, string c) { var o = r.GetOrdinal(c); return r.IsDBNull(o) ? null : r.GetBoolean(o); }

    private static DateTime? Date(SqlDataReader r, string c) { var o = r.GetOrdinal(c); return r.IsDBNull(o) ? null : r.GetDateTime(o); }
}
