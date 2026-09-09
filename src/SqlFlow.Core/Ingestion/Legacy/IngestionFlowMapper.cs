using SqlFlow.Core;

namespace SqlFlow.Core.Ingestion.Legacy;

/// <summary>
/// Maps a legacy <c>flw.Ingestion</c> row (with its <c>flw.IngestionVirtual</c> children) into the V3
/// <see cref="IngestionFlow"/> model. Pure and lossless: every column has a destination, and a legacy NULL
/// resolves to the documented table default so a ported flow behaves exactly as it did. Values the model
/// cannot represent (an unrecognized system column) fail fast with a precise, flow-identified error rather
/// than being silently dropped.
/// </summary>
public static class IngestionFlowMapper
{
    private const string DefaultSystemColumns = "InsertedDate_DW,UpdatedDate_DW";
    private const string DefaultAssertions = "CheckEmptyTable,CheckFreshnessDaily";

    public static IngestionFlow FromLegacy(
        LegacyIngestionRow row,
        IEnumerable<LegacyIngestionVirtualRow>? virtualColumns = null,
        IEnumerable<LegacySurrogateKeyRow>? surrogateKeys = null,
        LegacyMatchKeyRow? matchKey = null)
    {
        ArgumentNullException.ThrowIfNull(row);

        var virtuals = (virtualColumns ?? [])
            .Where(v => v.FlowID == row.FlowID)
            .Select(MapVirtual)
            .ToList();

        var skeys = (surrogateKeys ?? [])
            .Where(s => s.FlowID == row.FlowID)
            .Select(s => MapSurrogateKey(s, row.FlowID))
            .ToList();

        var matchKeys = MapMatchKey(matchKey, row.FlowID);
        var systemColumns = MapSystemColumns(row.SysColumns, row.FlowID);
        if ((row.MatchKeysInSrcTrg ?? false) && matchKeys.Action == MatchKeyAction.Tag)
        {
            // Tag mode soft-deletes by stamping DeletedDate_DW, so the column must exist on the target; the
            // legacy engine auto-added it at match time, V3 lets the schema builder create it with the rest.
            systemColumns = systemColumns with { DeletedDate = true };
        }

        return new IngestionFlow
        {
            FlowId = row.FlowID,
            Batch = NullIfBlank(row.Batch),
            SysAlias = NullIfBlank(row.SysAlias),
            Description = NullIfBlank(row.Description),
            FlowType = NullIfBlank(row.FlowType) ?? "ing",
            DeactivateFromBatch = row.DeactivateFromBatch ?? false,
            BatchOrderBy = row.BatchOrderBy,
            OnErrorResume = row.OnErrorResume ?? true,
            FromObjectMK = row.FromObjectMK,
            ToObjectMK = row.ToObjectMK,
            CreatedBy = NullIfBlank(row.CreatedBy),
            CreatedDate = row.CreatedDate,

            Source = new IngestionSource
            {
                Server = Require(row.srcServer, row.FlowID, "srcServer"),
                Table = ParseObject(row.srcDBSchTbl, row.FlowID, "srcDBSchTbl"),
                Filter = NullIfBlank(row.srcFilter),
                FilterIsAppend = row.srcFilterIsAppend ?? true,
                IncrementalClause = NullIfBlank(row.IncrementalClauseExp),
                IgnoreColumns = IngestionText.ParseList(row.IgnoreColumns),
                DataSetColumn = NullIfBlank(row.DataSetColumn),
            },

            Target = new IngestionTarget
            {
                Server = Require(row.trgServer, row.FlowID, "trgServer"),
                Table = ParseObject(row.trgDBSchTbl, row.FlowID, "trgDBSchTbl"),
                TruncateBeforeLoad = row.TruncateTrg ?? false,
                DesiredIndexes = NullIfBlank(row.trgDesiredIndex),
                ColumnStoreIndex = row.ColumnStoreIndexOnTrg ?? false,
                IdentityColumn = NullIfBlank(row.IdentityColumn),
            },

            VirtualColumns = virtuals,
            SurrogateKeys = skeys,

            Load = new IngestionLoadPolicy
            {
                KeyColumns = IngestionText.ParseList(row.KeyColumns),
                SkipUpdateExisting = row.SkipUpdateExsisting ?? false,
                SkipInsertNew = row.SkipInsertNew ?? false,
                MatchKeysInSourceAndTarget = row.MatchKeysInSrcTrg ?? false,
                BatchUpsertToAvoidLockEscalation = row.UseBatchUpsertToAvoideLockEscalation ?? false,
                BatchUpsertRowCount = row.BatchUpsertRowCount ?? 2000,
                StreamData = row.StreamData ?? true,
                Threads = (row.NoOfThreads ?? 0) > 0 ? row.NoOfThreads : null,
                TruncatePreTableOnCompletion = row.TruncatePreTableOnCompletion ?? false,
            },

            Change = new ChangePolicy
            {
                HashColumns = IngestionText.ParseList(row.HashKeyColumns),
                HashType = NullIfBlank(row.HashKeyType),
                IgnoreColumnsInHash = IngestionText.ParseList(row.IgnoreColumnsInHashkey),
            },

            MatchKeys = matchKeys,

            SystemColumns = systemColumns,

            SchemaSync = new SchemaSyncPolicy
            {
                Sync = row.SyncSchema ?? true,
                CleanColumnNames = row.OnSyncCleanColumnName ?? false,
                CleanColumnNameRegex = NullIfBlank(row.CleanColumnNameSQLRegExp),
                ReplaceInvalidCharsWith = NullIfBlank(row.ReplaceInvalidCharsWith),
                ConvertUnicodeToNonUnicode = row.OnSyncConvertUnicodeDataType ?? false,
            },

            Incremental = new IncrementalPolicy
            {
                Columns = IngestionText.ParseList(row.IncrementalColumns),
                DateColumn = NullIfBlank(row.DateColumn),
                OverlapDays = row.NoOfOverlapDays ?? 7,
                FullLoad = (row.FullLoad ?? 0) != 0,
                FetchMinValuesFromSource = row.FetchMinValuesFromSrc ?? false,
            },

            InitLoad = new InitLoadPolicy
            {
                Enabled = row.InitLoad ?? false,
                FromDate = ToDateOnly(row.InitLoadFromDate),
                ToDate = ToDateOnly(row.InitLoadToDate),
                BatchBy = NullIfBlank(row.InitLoadBatchBy),
                BatchSize = row.InitLoadBatchSize,
                KeyColumn = NullIfBlank(row.InitLoadKeyColumn),
                KeyMaxValue = row.InitLoadKeyMaxValue,
            },

            Versioning = new VersioningPolicy
            {
                // A legacy trgVersioning row carries no configuration beyond the flag: flw.GetVersioningScript
                // hardcoded the ValidFrom_DW/ValidTo_DW period column names and took the history schema from
                // flw.SysCFG's Schema06Version ("ver" in the estate), which are the TemporalPolicy defaults. The
                // one legacy value that is not a default is the period precision: legacy emitted datetime2(0),
                // so a control-DB-sourced flow keeps it for fidelity with the tables that engine produced.
                Temporal = new TemporalPolicy
                {
                    Enabled = row.trgVersioning ?? false,
                    PeriodPrecision = 0,
                },
                InsertUnknownDimensionRow = row.InsertUnknownDimRow ?? false,
                TokenVersioning = row.TokenVersioning ?? false,
                TokenRetentionDays = row.TokenRetentionDays,
            },

            // A legacy NULL means "use the default assertions"; an explicit empty string means none.
            Assertions = IngestionText.ParseList(row.Assertions ?? DefaultAssertions),

            Process = new ProcessPolicy
            {
                PreProcessOnTarget = NullIfBlank(row.PreProcessOnTrg),
                PostProcessOnTarget = NullIfBlank(row.PostProcessOnTrg),
                PreInvokeAlias = NullIfBlank(row.PreInvokeAlias),
                PostInvokeAlias = NullIfBlank(row.PostInvokeAlias),
            },
        };
    }

    private static VirtualColumn MapVirtual(LegacyIngestionVirtualRow row)
    {
        var selectExpression = row.SelectExp?.Trim();
        if (string.IsNullOrEmpty(selectExpression))
        {
            throw new SqlFlowException(
                $"flw.IngestionVirtual row {row.VirtualID} (FlowID {row.FlowID}) has an empty SelectExp; a virtual column requires a select expression.");
        }

        return new VirtualColumn
        {
            Name = NullIfBlank(row.ColumnName),
            DataType = NullIfBlank(row.DataType),
            DataTypeExpression = NullIfBlank(row.DataTypeExp),
            SelectExpression = selectExpression,
        };
    }

    private static SurrogateKeySpec MapSurrogateKey(LegacySurrogateKeyRow row, int flowId)
    {
        var keyColumns = IngestionText.ParseList(row.KeyColumns);
        if (keyColumns.Count == 0)
        {
            throw new SqlFlowException(
                $"flw.SurrogateKey row {row.SurrogateKeyID} (FlowID {flowId}) has no KeyColumns; a surrogate key requires at least one business-key column.");
        }

        var surrogateColumn = NullIfBlank(row.SurrogateColumn)
            ?? throw new SqlFlowException($"flw.SurrogateKey row {row.SurrogateKeyID} (FlowID {flowId}) has no SurrogateColumn.");

        return new SurrogateKeySpec
        {
            SurrogateKeyId = row.SurrogateKeyID,
            FlowId = flowId,
            Server = NullIfBlank(row.SurrogateServer),
            SurrogateTable = ParseObject(row.SurrogateDbSchTbl, flowId, "SurrogateDbSchTbl"),
            SurrogateColumn = surrogateColumn,
            KeyColumns = keyColumns,
            SKeyColumns = IngestionText.ParseList(row.sKeyColumns),
            PreProcess = NullIfBlank(row.PreProcess),
            PostProcess = NullIfBlank(row.PostProcess),
            ToObjectMK = row.ToObjectMK,
        };
    }

    private static MatchKeyPolicy MapMatchKey(LegacyMatchKeyRow? row, int flowId)
    {
        if (row is null)
        {
            return new MatchKeyPolicy();
        }

        var thresholdPercent = row.ActionThresholdPercent ?? 20;
        if (thresholdPercent is < 0 or > 100)
        {
            throw new SqlFlowException(
                $"flw.MatchKey row {row.MatchKeyID} (FlowID {flowId}) has ActionThresholdPercent {thresholdPercent}; it must be 0 to 100.");
        }

        return new MatchKeyPolicy
        {
            Action = NullIfBlank(row.ActionType)?.Trim().ToUpperInvariant() switch
            {
                null or "TAG" => MatchKeyAction.Tag,
                "DELETE" => MatchKeyAction.Delete,
                _ => throw new SqlFlowException(
                    $"flw.MatchKey row {row.MatchKeyID} (FlowID {flowId}) has an unknown ActionType '{row.ActionType}'. Valid values: Tag, Delete."),
            },
            ActionThresholdPercent = thresholdPercent,
            IgnoreDeletedRowsAfterMonths = row.IgnoreDeletedRowsAfter,
            DateColumn = NullIfBlank(row.DateColumn),
            KeyColumns = IngestionText.ParseList(row.KeyColumns),
            SourceFilter = NullIfBlank(row.srcFilter),
            TargetFilter = NullIfBlank(row.trgFilter),
        };
    }

    private static SystemColumnsPolicy MapSystemColumns(string? sysColumns, int flowId)
    {
        // NULL means the table default; an explicit empty string means no system columns.
        var names = IngestionText.ParseList(sysColumns ?? DefaultSystemColumns);
        var policy = new SystemColumnsPolicy { InsertedDate = false, UpdatedDate = false };

        foreach (var name in names)
        {
            policy = name.ToUpperInvariant() switch
            {
                "INSERTEDDATE_DW" => policy with { InsertedDate = true },
                "UPDATEDDATE_DW" => policy with { UpdatedDate = true },
                "DELETEDDATE_DW" => policy with { DeletedDate = true },
                "ROWSTATUS_DW" => policy with { RowStatus = true },
                _ => throw new SqlFlowException(
                    $"Ingestion flow {flowId} has an unknown system column '{name}' in SysColumns. " +
                    "Valid values: InsertedDate_DW, UpdatedDate_DW, DeletedDate_DW, RowStatus_DW."),
            };
        }

        return policy;
    }

    private static string Require(string? value, int flowId, string column)
        => NullIfBlank(value) ?? throw new SqlFlowException($"Ingestion flow {flowId} is missing the required '{column}'.");

    private static RelationalObject ParseObject(string? name, int flowId, string column)
    {
        var value = NullIfBlank(name)
            ?? throw new SqlFlowException($"Ingestion flow {flowId} is missing the required '{column}'.");
        try
        {
            return RelationalObject.Parse(value);
        }
        catch (SqlFlowException ex)
        {
            throw new SqlFlowException($"Ingestion flow {flowId}, column '{column}': {ex.Message}", ex);
        }
    }

    private static DateOnly? ToDateOnly(DateTime? value) => value is { } date ? DateOnly.FromDateTime(date) : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
