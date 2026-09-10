using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Ingestion.Legacy;

namespace SqlFlow.SqlServer.FullMode;

/// <summary>
/// The full-mode flow loader: reads a normalized flw.Ingestion row (with its flw.IngestionVirtual children) and
/// maps it to an <see cref="IngestionFlow"/> through the existing lossless <see cref="IngestionFlowMapper"/>.
/// This is the only code that knows the flw.Ingestion column shape; columns are read by name (not position) so
/// an unrelated added column does not shift anything, and the mapper's fail-fast on a missing required column or
/// an unknown SysColumns value is preserved.
/// </summary>
public sealed class SqlIngestionFlowLoader : IFlowLoader
{
    private readonly string _controlConnectionString;

    public SqlIngestionFlowLoader(string controlConnectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(controlConnectionString);
        _controlConnectionString = controlConnectionString;
    }

    public async Task<IngestionFlow> LoadByIdAsync(int flowId, CancellationToken ct = default)
    {
        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var row = await ReadFlowAsync(connection, flowId, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"No ingestion flow with FlowID {flowId} exists in the control database.");
        var virtuals = await ReadVirtualsAsync(connection, flowId, ct).ConfigureAwait(false);
        var surrogateKeys = await ReadSurrogateKeysAsync(connection, flowId, ct).ConfigureAwait(false);
        var matchKey = await ReadMatchKeyAsync(connection, flowId, ct).ConfigureAwait(false);
        return IngestionFlowMapper.FromLegacy(row, virtuals, surrogateKeys, matchKey);
    }

    public async Task<IngestionFlow> LoadByAliasAsync(string sysAlias, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sysAlias);

        await using var connection = new SqlConnection(_controlConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        int flowId;
        await using (var command = new SqlCommand("SELECT TOP (1) [FlowID] FROM [flw].[Ingestion] WHERE [SysAlias] = @alias ORDER BY [FlowID];", connection) { CommandTimeout = 0 })
        {
            command.Parameters.Add(new SqlParameter("@alias", System.Data.SqlDbType.NVarChar, 250) { Value = sysAlias });
            var scalar = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is null or DBNull)
            {
                throw new SqlFlowException($"No ingestion flow with SysAlias '{sysAlias}' exists in the control database.");
            }

            flowId = (int)scalar;
        }

        var row = await ReadFlowAsync(connection, flowId, ct).ConfigureAwait(false)!;
        var virtuals = await ReadVirtualsAsync(connection, flowId, ct).ConfigureAwait(false);
        var surrogateKeys = await ReadSurrogateKeysAsync(connection, flowId, ct).ConfigureAwait(false);
        var matchKey = await ReadMatchKeyAsync(connection, flowId, ct).ConfigureAwait(false);
        return IngestionFlowMapper.FromLegacy(row!, virtuals, surrogateKeys, matchKey);
    }

    public async Task<IReadOnlyList<IngestionFlow>> LoadBatchAsync(string batch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batch);

        var flowIds = new List<int>();
        await using (var connection = new SqlConnection(_controlConnectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new SqlCommand(
                "SELECT [FlowID] FROM [flw].[Ingestion] WHERE [Batch] = @batch AND ISNULL([DeactivateFromBatch], 0) = 0 " +
                "ORDER BY ISNULL([BatchOrderBy], 2147483647), [FlowID];",
                connection) { CommandTimeout = 0 };
            command.Parameters.Add(new SqlParameter("@batch", System.Data.SqlDbType.NVarChar, 250) { Value = batch });
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                flowIds.Add(reader.GetInt32(0));
            }
        }

        var flows = new List<IngestionFlow>(flowIds.Count);
        foreach (var flowId in flowIds)
        {
            flows.Add(await LoadByIdAsync(flowId, ct).ConfigureAwait(false));
        }

        return flows;
    }

    private static async Task<LegacyIngestionRow?> ReadFlowAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(FlowQuery, connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ReadIngestionRow(reader) : null;
    }

    private static async Task<IReadOnlyList<LegacySurrogateKeyRow>> ReadSurrogateKeysAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT [SurrogateKeyID],[FlowID],[SurrogateServer],[SurrogateDbSchTbl],[SurrogateColumn],[KeyColumns]," +
            "[sKeyColumns],[PreProcess],[PostProcess],[ToObjectMK] FROM [flw].[SurrogateKey] WHERE [FlowID] = @id ORDER BY [SurrogateKeyID];",
            connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });

        var rows = new List<LegacySurrogateKeyRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new LegacySurrogateKeyRow
            {
                SurrogateKeyID = reader.GetInt32(0),
                FlowID = reader.GetInt32(1),
                SurrogateServer = Str(reader, "SurrogateServer"),
                SurrogateDbSchTbl = Str(reader, "SurrogateDbSchTbl"),
                SurrogateColumn = Str(reader, "SurrogateColumn"),
                KeyColumns = Str(reader, "KeyColumns"),
                sKeyColumns = Str(reader, "sKeyColumns"),
                PreProcess = Str(reader, "PreProcess"),
                PostProcess = Str(reader, "PostProcess"),
                ToObjectMK = Int(reader, "ToObjectMK"),
            });
        }

        return rows;
    }

    private static async Task<IReadOnlyList<LegacyIngestionVirtualRow>> ReadVirtualsAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT [VirtualID],[FlowID],[ColumnName],[DataType],[DataTypeExp],[SelectExp] FROM [flw].[IngestionVirtual] WHERE [FlowID] = @id ORDER BY [VirtualID];",
            connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });

        var rows = new List<LegacyIngestionVirtualRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows.Add(new LegacyIngestionVirtualRow
            {
                VirtualID = reader.GetInt32(0),
                FlowID = reader.GetInt32(1),
                ColumnName = Str(reader, "ColumnName"),
                DataType = Str(reader, "DataType"),
                DataTypeExp = Str(reader, "DataTypeExp"),
                SelectExp = Str(reader, "SelectExp"),
            });
        }

        return rows;
    }

    private static async Task<LegacyMatchKeyRow?> ReadMatchKeyAsync(SqlConnection connection, int flowId, CancellationToken ct)
    {
        await using var command = new SqlCommand(
            "SELECT TOP (1) [MatchKeyID],[FlowID],[Batch],[SysAlias],[srcServer],[srcDatabase],[srcSchema],[srcObject]," +
            "[trgServer],[trgDBSchTbl],[DeactivateFromBatch],[KeyColumns],[DateColumn],[ActionType],[ActionThresholdPercent]," +
            "[IgnoreDeletedRowsAfter],[srcFilter],[trgFilter],[OnErrorResume],[PreProcessOnTrg],[PostProcessOnTrg]," +
            "[Description],[ToObjectMK],[CreatedBy],[CreatedDate] " +
            "FROM [flw].[MatchKey] WHERE [FlowID] = @id ORDER BY [MatchKeyID];",
            connection) { CommandTimeout = 0 };
        command.Parameters.Add(new SqlParameter("@id", System.Data.SqlDbType.Int) { Value = flowId });
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new LegacyMatchKeyRow
        {
            MatchKeyID = reader.GetInt32(reader.GetOrdinal("MatchKeyID")),
            FlowID = reader.GetInt32(reader.GetOrdinal("FlowID")),
            Batch = Str(reader, "Batch"),
            SysAlias = Str(reader, "SysAlias"),
            srcServer = Str(reader, "srcServer"),
            srcDatabase = Str(reader, "srcDatabase"),
            srcSchema = Str(reader, "srcSchema"),
            srcObject = Str(reader, "srcObject"),
            trgServer = Str(reader, "trgServer"),
            trgDBSchTbl = Str(reader, "trgDBSchTbl"),
            DeactivateFromBatch = Bool(reader, "DeactivateFromBatch"),
            KeyColumns = Str(reader, "KeyColumns"),
            DateColumn = Str(reader, "DateColumn"),
            ActionType = Str(reader, "ActionType"),
            ActionThresholdPercent = Int(reader, "ActionThresholdPercent"),
            IgnoreDeletedRowsAfter = Int(reader, "IgnoreDeletedRowsAfter"),
            srcFilter = Str(reader, "srcFilter"),
            trgFilter = Str(reader, "trgFilter"),
            OnErrorResume = Bool(reader, "OnErrorResume"),
            PreProcessOnTrg = Str(reader, "PreProcessOnTrg"),
            PostProcessOnTrg = Str(reader, "PostProcessOnTrg"),
            Description = Str(reader, "Description"),
            ToObjectMK = Int(reader, "ToObjectMK"),
            CreatedBy = Str(reader, "CreatedBy"),
            CreatedDate = Date(reader, "CreatedDate"),
        };
    }

    private static LegacyIngestionRow ReadIngestionRow(SqlDataReader r) => new()
    {
        FlowID = r.GetInt32(r.GetOrdinal("FlowID")),
        Batch = Str(r, "Batch"),
        SysAlias = Str(r, "SysAlias"),
        srcServer = Str(r, "srcServer"),
        srcDBSchTbl = Str(r, "srcDBSchTbl"),
        trgServer = Str(r, "trgServer"),
        trgDBSchTbl = Str(r, "trgDBSchTbl"),
        trgDesiredIndex = Str(r, "trgDesiredIndex"),
        DeactivateFromBatch = Bool(r, "DeactivateFromBatch"),
        StreamData = Bool(r, "StreamData"),
        NoOfThreads = Int(r, "NoOfThreads"),
        KeyColumns = Str(r, "KeyColumns"),
        IncrementalColumns = Str(r, "IncrementalColumns"),
        IncrementalClauseExp = Str(r, "IncrementalClauseExp"),
        DateColumn = Str(r, "DateColumn"),
        DataSetColumn = Str(r, "DataSetColumn"),
        NoOfOverlapDays = Int(r, "NoOfOverlapDays"),
        FetchMinValuesFromSrc = Bool(r, "FetchMinValuesFromSrc"),
        SkipUpdateExsisting = Bool(r, "SkipUpdateExsisting"),
        SkipInsertNew = Bool(r, "SkipInsertNew"),
        FullLoad = Int(r, "FullLoad"),
        TruncateTrg = Bool(r, "TruncateTrg"),
        TruncatePreTableOnCompletion = Bool(r, "TruncatePreTableOnCompletion"),
        srcFilter = Str(r, "srcFilter"),
        srcFilterIsAppend = Bool(r, "srcFilterIsAppend"),
        IdentityColumn = Str(r, "IdentityColumn"),
        HashKeyColumns = Str(r, "HashKeyColumns"),
        HashKeyType = Str(r, "HashKeyType"),
        IgnoreColumns = Str(r, "IgnoreColumns"),
        IgnoreColumnsInHashkey = Str(r, "IgnoreColumnsInHashkey"),
        SysColumns = Str(r, "SysColumns"),
        ColumnStoreIndexOnTrg = Bool(r, "ColumnStoreIndexOnTrg"),
        SyncSchema = Bool(r, "SyncSchema"),
        OnErrorResume = Bool(r, "OnErrorResume"),
        OnSyncCleanColumnName = Bool(r, "OnSyncCleanColumnName"),
        ReplaceInvalidCharsWith = Str(r, "ReplaceInvalidCharsWith"),
        OnSyncConvertUnicodeDataType = Bool(r, "OnSyncConvertUnicodeDataType"),
        CleanColumnNameSQLRegExp = Str(r, "CleanColumnNameSQLRegExp"),
        trgVersioning = Bool(r, "trgVersioning"),
        InsertUnknownDimRow = Bool(r, "InsertUnknownDimRow"),
        TokenVersioning = Bool(r, "TokenVersioning"),
        TokenRetentionDays = Int(r, "TokenRetentionDays"),
        PreProcessOnTrg = Str(r, "PreProcessOnTrg"),
        PostProcessOnTrg = Str(r, "PostProcessOnTrg"),
        PreInvokeAlias = Str(r, "PreInvokeAlias"),
        PostInvokeAlias = Str(r, "PostInvokeAlias"),
        Assertions = Str(r, "Assertions"),
        MatchKeysInSrcTrg = Bool(r, "MatchKeysInSrcTrg"),
        UseBatchUpsertToAvoideLockEscalation = Bool(r, "UseBatchUpsertToAvoideLockEscalation"),
        BatchUpsertRowCount = Int(r, "BatchUpsertRowCount"),
        InitLoad = Bool(r, "InitLoad"),
        InitLoadFromDate = Date(r, "InitLoadFromDate"),
        InitLoadToDate = Date(r, "InitLoadToDate"),
        InitLoadBatchBy = Str(r, "InitLoadBatchBy"),
        InitLoadBatchSize = Int(r, "InitLoadBatchSize"),
        InitLoadKeyColumn = Str(r, "InitLoadKeyColumn"),
        InitLoadKeyMaxValue = Int(r, "InitLoadKeyMaxValue"),
        BatchOrderBy = Int(r, "BatchOrderBy"),
        FlowType = Str(r, "FlowType"),
        Description = Str(r, "Description"),
        FromObjectMK = Int(r, "FromObjectMK"),
        ToObjectMK = Int(r, "ToObjectMK"),
        CreatedBy = Str(r, "CreatedBy"),
        CreatedDate = Date(r, "CreatedDate"),
    };

    private static string? Str(SqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetString(o);
    }

    private static int? Int(SqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetInt32(o);
    }

    private static bool? Bool(SqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetBoolean(o);
    }

    private static DateTime? Date(SqlDataReader r, string column)
    {
        var o = r.GetOrdinal(column);
        return r.IsDBNull(o) ? null : r.GetDateTime(o);
    }

    private const string FlowQuery =
        "SELECT [FlowID],[Batch],[SysAlias],[srcServer],[srcDBSchTbl],[trgServer],[trgDBSchTbl],[trgDesiredIndex]," +
        "[DeactivateFromBatch],[StreamData],[NoOfThreads],[KeyColumns],[IncrementalColumns],[IncrementalClauseExp]," +
        "[DateColumn],[DataSetColumn],[NoOfOverlapDays],[FetchMinValuesFromSrc],[SkipUpdateExsisting],[SkipInsertNew]," +
        "[FullLoad],[TruncateTrg],[TruncatePreTableOnCompletion],[srcFilter],[srcFilterIsAppend],[IdentityColumn]," +
        "[HashKeyColumns],[HashKeyType],[IgnoreColumns],[IgnoreColumnsInHashkey],[SysColumns],[ColumnStoreIndexOnTrg]," +
        "[SyncSchema],[OnErrorResume],[OnSyncCleanColumnName],[ReplaceInvalidCharsWith],[OnSyncConvertUnicodeDataType]," +
        "[CleanColumnNameSQLRegExp],[trgVersioning],[InsertUnknownDimRow],[TokenVersioning],[TokenRetentionDays]," +
        "[PreProcessOnTrg],[PostProcessOnTrg],[PreInvokeAlias],[PostInvokeAlias],[Assertions],[MatchKeysInSrcTrg]," +
        "[UseBatchUpsertToAvoideLockEscalation],[BatchUpsertRowCount],[InitLoad],[InitLoadFromDate],[InitLoadToDate]," +
        "[InitLoadBatchBy],[InitLoadBatchSize],[InitLoadKeyColumn],[InitLoadKeyMaxValue],[BatchOrderBy],[FlowType]," +
        "[Description],[FromObjectMK],[ToObjectMK],[CreatedBy],[CreatedDate] " +
        "FROM [flw].[Ingestion] WHERE [FlowID] = @id;";
}
