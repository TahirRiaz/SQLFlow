using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Export.Legacy;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>Reads flw.Export rows and maps them to <see cref="ExportFlow"/> through the lossless mapper.
/// Columns are read by name, not position.</summary>
public sealed class SqlExportFlowLoader : IExportFlowLoader
{
    private readonly string _controlConnectionString;

    public SqlExportFlowLoader(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public async Task<ExportFlow> LoadByIdAsync(int flowId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var row = await ReadAsync(connection, flowId, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"No export flow with FlowID {flowId} exists in the control database.");
        return ExportFlowMapper.FromLegacy(row);
    }

    public async Task<IReadOnlyList<ExportFlow>> LoadBatchAsync(string batch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);

        var flowIds = new List<int>();
        await using (var connection = new SqlConnection(_controlConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand(
                "SELECT [FlowID] FROM [flw].[Export] WHERE [Batch] = @batch AND ISNULL([DeactivateFromBatch], 0) = 0 ORDER BY [FlowID];",
                connection) { CommandTimeout = 0 };
            command.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.NVarChar, 250) { Value = batch });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flowIds.Add(reader.GetInt32(0));
            }
        }

        var flows = new List<ExportFlow>(flowIds.Count);
        foreach (var flowId in flowIds)
        {
            flows.Add(await LoadByIdAsync(flowId, ct).ConfigureAwait(false));
        }

        return flows;
    }

    private static async Task<LegacyExportRow?> ReadAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT [FlowID],[Batch],[SysAlias],[srcServer],[srcDBSchTbl],[srcWithHint],[srcFilter],[IncrementalColumn]," +
            "[DateColumn],[NoOfOverlapDays],[FromDate],[ToDate],[ExportBy],[ExportSize],[ServicePrincipalAlias],[trgPath]," +
            "[trgFileName],[trgFiletype],[trgEncoding],[CompressionType],[ColumnDelimiter],[TextQualifier]," +
            "[AddTimeStampToFileName],[Subfolderpattern],[NoOfThreads],[ZipTrg],[OnErrorResume],[PostInvokeAlias]," +
            "[DeactivateFromBatch],[FlowType],[FromObjectMK],[ToObjectMK],[CreatedBy],[CreatedDate] " +
            "FROM [flw].[Export] WHERE [FlowID] = @id;",
            connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LegacyExportRow
        {
            FlowID = reader.GetInt32(reader.GetOrdinal("FlowID")),
            Batch = Str(reader, "Batch"),
            SysAlias = Str(reader, "SysAlias"),
            srcServer = Str(reader, "srcServer"),
            srcDBSchTbl = Str(reader, "srcDBSchTbl"),
            srcWithHint = Str(reader, "srcWithHint"),
            srcFilter = Str(reader, "srcFilter"),
            IncrementalColumn = Str(reader, "IncrementalColumn"),
            DateColumn = Str(reader, "DateColumn"),
            NoOfOverlapDays = Int(reader, "NoOfOverlapDays"),
            FromDate = Date(reader, "FromDate"),
            ToDate = Date(reader, "ToDate"),
            ExportBy = Str(reader, "ExportBy"),
            ExportSize = Int(reader, "ExportSize"),
            ServicePrincipalAlias = Str(reader, "ServicePrincipalAlias"),
            trgPath = Str(reader, "trgPath"),
            trgFileName = Str(reader, "trgFileName"),
            trgFiletype = Str(reader, "trgFiletype"),
            trgEncoding = Str(reader, "trgEncoding"),
            CompressionType = Str(reader, "CompressionType"),
            ColumnDelimiter = Str(reader, "ColumnDelimiter"),
            TextQualifier = Str(reader, "TextQualifier"),
            AddTimeStampToFileName = Bool(reader, "AddTimeStampToFileName"),
            Subfolderpattern = Str(reader, "Subfolderpattern"),
            NoOfThreads = Int(reader, "NoOfThreads"),
            ZipTrg = Bool(reader, "ZipTrg"),
            OnErrorResume = Bool(reader, "OnErrorResume"),
            PostInvokeAlias = Str(reader, "PostInvokeAlias"),
            DeactivateFromBatch = Bool(reader, "DeactivateFromBatch"),
            FlowType = Str(reader, "FlowType"),
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
