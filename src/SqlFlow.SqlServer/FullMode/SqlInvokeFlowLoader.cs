using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Invoke;
using SqlFlow.Core.Invoke.Legacy;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>Reads flw.Invoke rows and maps them to <see cref="InvokeDefinition"/> through the lossless mapper.
/// Columns are read by name, not position. Lookups by id, by the unique InvokeAlias (what Pre/PostInvokeAlias
/// hooks reference), and by batch are all supported.</summary>
public sealed class SqlInvokeFlowLoader : IInvokeFlowLoader
{
    private const string SelectColumns =
        "SELECT [FlowID],[Batch],[SysAlias],[trgServicePrincipalAlias],[InvokeAlias],[InvokeType]," +
        "[srcServicePrincipalAlias],[PipelineName],[RunbookName],[ParameterJSON]," +
        "[OnErrorResume],[DeactivateFromBatch],[ToObjectMK],[CreatedBy],[CreatedDate] FROM [flw].[Invoke] ";

    private readonly string _controlConnectionString;

    public SqlInvokeFlowLoader(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public async Task<InvokeDefinition> LoadByIdAsync(int flowId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(SelectColumns + "WHERE [FlowID] = @id;", connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });

        var row = await ReadAsync(command, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"No invoke flow with FlowID {flowId} exists in the control database.");
        return InvokeFlowMapper.FromLegacy(row);
    }

    public async Task<InvokeDefinition> LoadByAliasAsync(string invokeAlias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invokeAlias);

        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var command = new SqlCommand(SelectColumns + "WHERE [InvokeAlias] = @alias;", connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@alias", System.Data.SqlDbType.NVarChar, 250) { Value = invokeAlias });

        var row = await ReadAsync(command, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"No invoke flow with InvokeAlias '{invokeAlias}' exists in the control database.");
        return InvokeFlowMapper.FromLegacy(row);
    }

    public async Task<IReadOnlyList<InvokeDefinition>> LoadBatchAsync(string batch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);

        var flowIds = new List<int>();
        await using (var connection = new SqlConnection(_controlConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand(
                "SELECT [FlowID] FROM [flw].[Invoke] WHERE [Batch] = @batch AND ISNULL([DeactivateFromBatch], 0) = 0 ORDER BY [FlowID];",
                connection) { CommandTimeout = 0 };
            command.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.NVarChar, 70) { Value = batch });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flowIds.Add(reader.GetInt32(0));
            }
        }

        var flows = new List<InvokeDefinition>(flowIds.Count);
        foreach (var flowId in flowIds)
        {
            flows.Add(await LoadByIdAsync(flowId, ct).ConfigureAwait(false));
        }

        return flows;
    }

    private static async Task<LegacyInvokeRow?> ReadAsync(SqlCommand command, CancellationToken ct)
    {
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LegacyInvokeRow
        {
            FlowID = reader.GetInt32(reader.GetOrdinal("FlowID")),
            Batch = Str(reader, "Batch"),
            SysAlias = Str(reader, "SysAlias"),
            trgServicePrincipalAlias = Str(reader, "trgServicePrincipalAlias"),
            InvokeAlias = Str(reader, "InvokeAlias"),
            InvokeType = Str(reader, "InvokeType"),
            srcServicePrincipalAlias = Str(reader, "srcServicePrincipalAlias"),
            PipelineName = Str(reader, "PipelineName"),
            RunbookName = Str(reader, "RunbookName"),
            ParameterJSON = Str(reader, "ParameterJSON"),
            OnErrorResume = Bool(reader, "OnErrorResume"),
            DeactivateFromBatch = Bool(reader, "DeactivateFromBatch"),
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
