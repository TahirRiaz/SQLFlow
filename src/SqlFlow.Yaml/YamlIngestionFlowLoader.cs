using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SqlFlow.Yaml;

/// <summary>
/// A parsed relational ingestion document (flowType: ing): the flow itself plus the document-local connection
/// registry and assertion definitions it declares. The connections become an in-memory data-source store and
/// the assertions an in-memory definition store, so the flow runs through the exact same resolver and runners
/// as full mode, with no control database anywhere.
/// </summary>
public sealed record IngestionDocument
{
    public required IngestionFlow Flow { get; init; }

    /// <summary>The document's named connections (the <c>connections:</c> block plus any synthesized from a
    /// direct <c>connection:</c> on source/target).</summary>
    public required IReadOnlyList<DataSource> Connections { get; init; }

    /// <summary>The document's inline data-quality assertion definitions.</summary>
    public required IReadOnlyList<AssertionDefinition> AssertionDefinitions { get; init; }

    /// <summary>The document's named invokes (the <c>invokes:</c> block), referenced by preInvoke/postInvoke.</summary>
    public IReadOnlyList<Core.Invoke.InvokeDefinition> Invokes { get; init; } = [];

    /// <summary>The document's named Azure service principals (the <c>servicePrincipals:</c> block).</summary>
    public IReadOnlyList<ServicePrincipalProfile> ServicePrincipals { get; init; } = [];

    /// <summary>The derived health-check flow expanded from the document's embedded <c>healthCheck:</c> block,
    /// or null when the document declares none. It monitors the flow's target table through the flow's own
    /// target connection and is a full sibling pipeline: the estate scan registers it, lineage orders it after
    /// the load, and it executes through the same hc runner as a standalone document. Embedded checks default
    /// to <c>mode: manual</c> (on demand); declare <c>mode: auto</c> to opt into scheduled/group execution.</summary>
    public HealthCheckFlow? HealthCheck { get; init; }
}

/// <summary>
/// Loads a relational (table to table) ingestion flow from YAML. YamlDotNet handles the grammar; this class is
/// the mapping/validation layer that turns the parsed document into a validated <see cref="IngestionDocument"/>.
/// Defaults follow the model's documented legacy defaults; dynamic schema evolution is ON unless turned off.
/// </summary>
public sealed class YamlIngestionFlowLoader
{
    private const string SourceConnectionName = "source";
    private const string TargetConnectionName = "target";

    // The unquoted-scalar option types the invoke parameters block (a plain true/42 stays a boolean/number,
    // a quoted scalar a string); every typed DTO property is unaffected by it.
    private readonly IDeserializer _deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .Build();

    public IngestionDocument LoadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FlowValidationException($"Pipeline file not found: '{path}'.");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public IngestionDocument Parse(string yaml, string source = "<inline>")
    {
        IngestionYaml? dto;
        try
        {
            dto = _deserializer.Deserialize<IngestionYaml>(yaml);
        }
        catch (YamlException ex)
        {
            throw new FlowValidationException($"{source}: invalid YAML - {ex.Message}", ex);
        }

        if (dto is null)
        {
            throw new FlowValidationException($"{source}: the document is empty.");
        }

        return Map(dto, source);
    }

    private static IngestionDocument Map(IngestionYaml y, string source)
    {
        var connections = YamlDocumentParts.MapConnections(y.Connections, source);

        var sourceYaml = y.Source ?? throw new FlowValidationException($"{source}: 'source' is required.");
        var targetYaml = y.Target ?? throw new FlowValidationException($"{source}: 'target' is required.");

        var sourceServer = ResolveEndpointConnection(sourceYaml, "source", SourceConnectionName, connections, source);
        var targetServer = ResolveEndpointConnection(targetYaml, "target", TargetConnectionName, connections, source);

        // The target of an ingestion flow is SQL Server by design (staging, schema evolution, and the upsert
        // are T-SQL); a foreign target is a configuration error caught at parse time, not deep in the run.
        YamlDocumentParts.RequireSqlServerConnection(connections, targetServer, "target", "an ingestion flow's target", source);

        var (assertionNames, assertionDefinitions) = MapAssertions(y.Assertions, source);
        var servicePrincipals = YamlInvokeParts.MapServicePrincipals(y.ServicePrincipals, source);
        var invokes = YamlInvokeParts.MapInvokes(y.Invokes, servicePrincipals, source);
        var name = string.IsNullOrWhiteSpace(y.Name) ? null : y.Name.Trim();

        var load = MapLoad(y.Load);
        var matchKeys = MapMatchKeys(y.MatchKeys, source);
        var systemColumns = MapSystemColumns(y.SystemColumns);
        var versioning = MapVersioning(y.Versioning, source);

        // Accepted by the schema (and the control-DB / legacy loaders) for fidelity but not yet implemented by
        // the engine, so a flow that sets it is rejected at validation rather than silently doing nothing.
        // (The engine also guards it at run time for the non-YAML load paths.)
        if (versioning.InsertUnknownDimensionRow)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.insertUnknownDimensionRow' is not yet implemented; seed the unknown-member row explicitly for now.");
        }

        // TRUNCATE TABLE is not a supported operation on a system-versioned table: SQL Server refuses it
        // outright. Legacy silently skipped the truncate when trgVersioning was on, which left the flow author
        // believing a full reload had happened when the load had in fact appended. Reject the contradiction
        // instead of picking a winner behind their back.
        if (versioning.Temporal.Enabled && (targetYaml.TruncateBeforeLoad ?? false))
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal' cannot be combined with 'target.truncateBeforeLoad'. SQL Server does not " +
                "allow TRUNCATE TABLE on a system-versioned table, and a full reload would in any case contradict keeping a " +
                "complete row history. Remove one of the two.");
        }

        if (versioning.Scd2.Enabled)
        {
            // SCD2 maintains a version per business key, so it needs the keys; it appends new versions, so a
            // truncate would erase the history.
            if (load.KeyColumns.Count == 0)
            {
                throw new FlowValidationException(
                    $"{source}: 'versioning.scd2' requires 'load.keyColumns' (the business key the dimension versions by).");
            }

            if (targetYaml.TruncateBeforeLoad ?? false)
            {
                throw new FlowValidationException(
                    $"{source}: 'versioning.scd2' cannot be combined with 'target.truncateBeforeLoad'; truncating would erase the dimension history.");
            }
        }

        // Per-file replace (load.reloadColumn) is a purge-then-insert scoped to the incoming batch. It supersedes
        // the keyed upsert, so it cannot be layered on the mechanisms that assume that upsert: the ordered
        // dataset-upsert loop, SCD2 history, the match-key delete pass, or a full-reload truncate (which already
        // wipes everything, making a per-file purge meaningless). Reject the combinations loudly at parse time.
        if (!string.IsNullOrWhiteSpace(load.ReloadColumn))
        {
            if (!string.IsNullOrWhiteSpace(load.DataSetColumn))
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' (per-file replace) cannot be combined with 'load.dataSetColumn' (the ordered dataset-upsert loop); use one or the other.");
            }

            if (versioning.Scd2.Enabled)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'versioning.scd2'; a per-file purge would erase the dimension history.");
            }

            if (load.MatchKeysInSourceAndTarget)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'load.matchKeysInSourceAndTarget'; both delete target rows and their scopes conflict (per-file vs. whole-source key reconciliation).");
            }

            if (targetYaml.TruncateBeforeLoad ?? false)
            {
                throw new FlowValidationException(
                    $"{source}: 'load.reloadColumn' cannot be combined with 'target.truncateBeforeLoad'; truncating already replaces the whole target, so a per-file purge is redundant.");
            }
        }

        // Tag mode soft-deletes by stamping DeletedDate_DW; the column must be on the target. Auto-enable it
        // here (same as the legacy mapper does) so the user does not need to set both flags independently.
        if (load.MatchKeysInSourceAndTarget && matchKeys.Action == MatchKeyAction.Tag)
        {
            systemColumns = systemColumns with { DeletedDate = true };
        }

        var flow = new IngestionFlow
        {
            FlowId = name is null ? 0 : StableFlowId(name),
            SysAlias = name,
            Batch = NullIfBlank(y.Batch),
            Lifecycle = YamlDocumentParts.ParseLifecycle(y.Lifecycle, source),
            Description = string.IsNullOrWhiteSpace(y.Description) ? null : y.Description,
            Source = new IngestionSource
            {
                Server = sourceServer,
                Table = ParseObject(sourceYaml, "source", source),
                Filter = NullIfBlank(sourceYaml.Filter),
                FilterIsAppend = sourceYaml.FilterIsAppend ?? true,
                IncrementalClause = NullIfBlank(sourceYaml.IncrementalClause),
                IgnoreColumns = sourceYaml.IgnoreColumns ?? [],
                DataSetColumn = NullIfBlank(sourceYaml.DataSetColumn),
            },
            Target = new IngestionTarget
            {
                Server = targetServer,
                Table = ParseObject(targetYaml, "target", source),
                TruncateBeforeLoad = targetYaml.TruncateBeforeLoad ?? false,
                ColumnStoreIndex = targetYaml.ColumnStoreIndex ?? false,
                IdentityColumn = NullIfBlank(targetYaml.IdentityColumn),
                DesiredIndexes = NullIfBlank(targetYaml.DesiredIndexes),
            },
            Load = load,
            MatchKeys = matchKeys,
            Change = MapChange(y.Change),
            SystemColumns = systemColumns,
            SchemaSync = MapSchema(y.Schema),
            Incremental = MapIncremental(y.Incremental),
            InitLoad = MapInitLoad(y.InitLoad, source),
            Versioning = versioning,
            Process = new ProcessPolicy
            {
                PreProcessOnTarget = NullIfBlank(y.PreProcess),
                PostProcessOnTarget = NullIfBlank(y.PostProcess),
                PreInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PreInvoke, "preInvoke", invokes, source),
                PostInvokeAlias = YamlInvokeParts.ResolveHookAlias(y.PostInvoke, "postInvoke", invokes, source),
            },
            VirtualColumns = MapVirtualColumns(y.VirtualColumns, source),
            SurrogateKeys = MapSurrogateKeys(y.SurrogateKeys, connections, source),
            Assertions = assertionNames,
            Transform = YamlFlowLoader.MapInference(y.Transform, source),
        };

        return new IngestionDocument
        {
            Flow = flow,
            Connections = connections.Values.ToList(),
            AssertionDefinitions = assertionDefinitions,
            Invokes = invokes.Values.ToList(),
            ServicePrincipals = servicePrincipals.Values.ToList(),
            HealthCheck = MapHealthCheck(y.HealthCheck, flow, source),
        };
    }

    /// <summary>
    /// Expands the embedded <c>healthCheck:</c> block into the derived hc flow. The monitored table and the
    /// connection are the flow's own target (embedding means "watch what this flow loads"); the declaration
    /// body validates through the same shared parts as a standalone hc document, so both surfaces accept and
    /// refuse exactly the same shapes. The derived flow defaults to <c>mode: manual</c>: an embedded check is
    /// an on-demand instrument unless the author explicitly opts it into automatic dispatch.
    /// </summary>
    private static HealthCheckFlow? MapHealthCheck(IngestionHealthCheckYaml? hc, IngestionFlow flow, string source)
    {
        if (hc is null)
        {
            return null;
        }

        // The derived flow needs its own stable identity (state folder, run history, catalog pipeline), which
        // is name-derived; an anonymous parent flow has nothing to derive it from.
        if (flow.SysAlias is null)
        {
            throw new FlowValidationException(
                $"{source}: 'healthCheck' requires the flow to declare 'name:' (the derived check is named '<name>_hc').");
        }

        var name = NullIfBlank(hc.Name)?.Trim() ?? $"{flow.SysAlias}_hc";
        if (string.Equals(name, flow.SysAlias, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException(
                $"{source}: 'healthCheck.name' must differ from the flow's own name '{flow.SysAlias}' (the check is a sibling pipeline).");
        }

        var dateColumn = NullIfBlank(hc.DateColumn)?.Trim()
            ?? throw new FlowValidationException(
                $"{source}: 'healthCheck.dateColumn' is required (the date column the metrics are grouped by).");

        var ml = YamlHealthCheckParts.MapMl(hc.Ml, source, "healthCheck.");
        var maturityDays = YamlHealthCheckParts.MapMaturityDays(hc.MaturityDays, source, "healthCheck.");
        var sentinelFloor = YamlDocumentParts.ParseDate(hc.SentinelDateFloor, "healthCheck.sentinelDateFloor", source)
            ?? new DateOnly(1990, 1, 1);

        return new HealthCheckFlow
        {
            FlowId = StableFlowId(name),
            SysAlias = name,
            Batch = flow.Batch,
            Server = flow.Target.Server,
            Target = flow.Target.Table,
            DateColumn = dateColumn,
            Metrics = YamlHealthCheckParts.MapMetrics(hc.BaseValue, hc.Metrics, source, "healthCheck."),
            FilterCriteria = NullIfBlank(hc.Filter)?.Trim(),
            MaxExperimentSeconds = ml.MaxExperimentSeconds,
            AnomalyThreshold = ml.AnomalyThreshold,
            EsdAlpha = ml.EsdAlpha,
            MaxAnomalyFraction = ml.MaxAnomalyFraction,
            MaturityDays = maturityDays,
            SentinelDateFloor = sentinelFloor,
            // The embedded default is MANUAL, deliberately inverted from the standalone document: embedding is
            // "keep everything about this table in one file, run the check when asked".
            Mode = hc.Mode is null ? ExecutionMode.Manual : YamlDocumentParts.ParseExecutionMode(hc.Mode, "healthCheck.mode", source),
            Training = ml.Training,
            RetrainAfterDays = ml.RetrainAfterDays,
            Holidays = YamlHealthCheckParts.MapHolidays(hc.Holidays, source, "healthCheck."),
            Description = NullIfBlank(hc.Description),
        };
    }

    /// <summary>Endpoint resolution over the shared parts, with this document's endpoint DTO.</summary>
    private static string ResolveEndpointConnection(
        IngestionEndpointYaml endpoint, string section, string syntheticName,
        Dictionary<string, DataSource> connections, string source)
        => YamlDocumentParts.ResolveEndpointConnection(
            endpoint.Server, endpoint.Connection, endpoint.Provider, section, syntheticName, connections, source);

    private static RelationalObject ParseObject(IngestionEndpointYaml endpoint, string section, string source)
    {
        var raw = NullIfBlank(endpoint.Object) ?? NullIfBlank(endpoint.Table)
            ?? throw new FlowValidationException(
                $"{source}: '{section}.object' is required (a three-part name like Database.Schema.Table, or " +
                "Database.Table for MySQL, where the database is the schema).");

        // A two-part name (no brackets) means database.table: natural for MySQL, where the schema part IS the
        // database; the parts double up so the three-part model stays uniform.
        if (!raw.Contains('[', StringComparison.Ordinal))
        {
            var parts = raw.Split('.', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts.All(p => p.Length > 0))
            {
                raw = $"{parts[0]}.{parts[0]}.{parts[1]}";
            }
        }

        return YamlDocumentParts.ParseQualifiedObject(raw, $"{section}.object", source);
    }

    private static IngestionLoadPolicy MapLoad(IngestionLoadYaml? y)
    {
        if (y is null)
        {
            return new IngestionLoadPolicy();
        }

        return new IngestionLoadPolicy
        {
            KeyColumns = y.KeyColumns ?? [],
            SkipUpdateExisting = y.SkipUpdateExisting ?? false,
            SkipInsertNew = y.SkipInsertNew ?? false,
            MatchKeysInSourceAndTarget = y.MatchKeysInSourceAndTarget ?? false,
            BatchUpsertToAvoidLockEscalation = y.BatchUpsert ?? false,
            BatchUpsertRowCount = y.BatchUpsertRowCount ?? 2000,
            DataSetColumn = string.IsNullOrWhiteSpace(y.DataSetColumn) ? null : y.DataSetColumn.Trim(),
            ReloadColumn = string.IsNullOrWhiteSpace(y.ReloadColumn) ? null : y.ReloadColumn.Trim(),
            StreamData = y.StreamData ?? true,
            Threads = y.Threads is > 0 ? y.Threads : null,
            KeepStagingTable = y.KeepStagingTable ?? false,
            TruncatePreTableOnCompletion = y.TruncateStagingOnCompletion ?? false,
            TruncateSourceWhenConsolidated = y.TruncateSourceWhenConsolidated ?? false,
        };
    }

    private static MatchKeyPolicy MapMatchKeys(IngestionMatchKeysYaml? y, string source)
    {
        if (y is null)
        {
            return new MatchKeyPolicy();
        }

        var action = NullIfBlank(y.Action)?.Trim().ToUpperInvariant() switch
        {
            null or "TAG" => MatchKeyAction.Tag,
            "DELETE" => MatchKeyAction.Delete,
            _ => throw new FlowValidationException(
                $"{source}: 'matchKeys.action' must be 'tag' or 'delete', got '{y.Action}'."),
        };

        var thresholdPercent = y.ThresholdPercent ?? 20;
        if (thresholdPercent is < 0 or > 100)
        {
            throw new FlowValidationException(
                $"{source}: 'matchKeys.thresholdPercent' must be 0 to 100, got {thresholdPercent}.");
        }

        if (y.IgnoreDeletedRowsAfterMonths is not null && string.IsNullOrWhiteSpace(y.DateColumn))
        {
            throw new FlowValidationException(
                $"{source}: 'matchKeys.ignoreDeletedRowsAfterMonths' requires 'matchKeys.dateColumn'.");
        }

        return new MatchKeyPolicy
        {
            Action = action,
            ActionThresholdPercent = thresholdPercent,
            IgnoreDeletedRowsAfterMonths = y.IgnoreDeletedRowsAfterMonths,
            DateColumn = NullIfBlank(y.DateColumn),
            KeyColumns = y.KeyColumns ?? [],
            SourceFilter = NullIfBlank(y.SourceFilter),
            TargetFilter = NullIfBlank(y.TargetFilter),
        };
    }

    private static ChangePolicy MapChange(IngestionChangeYaml? y)
    {
        if (y is null)
        {
            return new ChangePolicy();
        }

        return new ChangePolicy
        {
            HashColumns = y.HashColumns ?? [],
            HashType = NullIfBlank(y.HashType),
            IgnoreColumnsInHash = y.IgnoreColumnsInHash ?? [],
        };
    }

    private static SystemColumnsPolicy MapSystemColumns(IngestionSystemColumnsYaml? y)
    {
        if (y is null)
        {
            return new SystemColumnsPolicy();
        }

        return new SystemColumnsPolicy
        {
            InsertedDate = y.InsertedDate ?? true,
            UpdatedDate = y.UpdatedDate ?? true,
            DeletedDate = y.DeletedDate ?? false,
            RowStatus = y.RowStatus ?? false,
        };
    }

    private static SchemaSyncPolicy MapSchema(IngestionSchemaYaml? y)
    {
        if (y is null)
        {
            return new SchemaSyncPolicy();
        }

        return new SchemaSyncPolicy
        {
            Sync = y.Sync ?? true,
            CleanColumnNames = y.CleanColumnNames ?? false,
            CleanColumnNameRegex = NullIfBlank(y.CleanColumnNameRegex),
            ReplaceInvalidCharsWith = NullIfBlank(y.ReplaceInvalidCharsWith),
            ConvertUnicodeToNonUnicode = y.ConvertUnicodeToNonUnicode ?? false,
            AllowTableRewrite = y.AllowTableRewrite ?? false,
        };
    }

    private static IncrementalPolicy MapIncremental(IngestionIncrementalYaml? y)
    {
        if (y is null)
        {
            return new IncrementalPolicy();
        }

        return new IncrementalPolicy
        {
            Columns = y.Columns ?? [],
            DateColumn = NullIfBlank(y.DateColumn),
            OverlapDays = y.OverlapDays ?? 7,
            Lookback = y.Lookback ?? 0,
            FullLoad = y.FullLoad ?? false,
            FetchMinValuesFromSource = y.FetchMinValuesFromSource ?? false,
        };
    }

    private static InitLoadPolicy MapInitLoad(IngestionInitLoadYaml? y, string source)
    {
        if (y is null)
        {
            return new InitLoadPolicy();
        }

        return new InitLoadPolicy
        {
            Enabled = y.Enabled ?? false,
            FromDate = ParseDate(y.FromDate, "initLoad.fromDate", source),
            ToDate = ParseDate(y.ToDate, "initLoad.toDate", source),
            BatchBy = NullIfBlank(y.BatchBy),
            BatchSize = y.BatchSize,
            KeyColumn = NullIfBlank(y.KeyColumn),
            KeyMaxValue = y.KeyMaxValue,
        };
    }

    private static VersioningPolicy MapVersioning(IngestionVersioningYaml? y, string source)
    {
        if (y is null)
        {
            return new VersioningPolicy();
        }

        var temporal = MapTemporal(y, source);
        var scd2 = MapScd2(y.Scd2, source);

        // Two independent history mechanisms on one table would record every change twice: the database's own
        // row versions AND an engine-maintained period row. They are also semantically different (system time
        // versus the load's notion of validity), so one flow must choose.
        if (temporal.Enabled && scd2.Enabled)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal' and 'versioning.scd2' cannot both be enabled. System-versioned history " +
                "keeps every row version in a separate history table maintained by SQL Server, while SCD2 keeps versions in " +
                "the target itself; enabling both records each change twice. Choose one.");
        }

        return new VersioningPolicy
        {
            Temporal = temporal,
            InsertUnknownDimensionRow = y.InsertUnknownDimensionRow ?? false,
            TokenVersioning = y.TokenVersioning ?? false,
            TokenRetentionDays = y.TokenRetentionDays,
            Scd2 = scd2,
        };
    }

    /// <summary>
    /// Maps the temporal block, honoring the legacy <c>temporalHistory: true</c> shorthand (which is what a
    /// ported <c>trgVersioning</c> flow carries) as an alias for <c>temporal.enabled</c>. Both surfaces
    /// converge on one <see cref="TemporalPolicy"/> before any engine code sees the flow, so there is a single
    /// execution path; only the authoring surface is two-sided.
    /// </summary>
    private static TemporalPolicy MapTemporal(IngestionVersioningYaml y, string source)
    {
        var t = y.Temporal;

        // An explicit `temporalHistory: false` next to `temporal.enabled: true` is a contradiction the author
        // needs to resolve; a merely absent shorthand is not.
        if (y.TemporalHistory == false && t?.Enabled == true)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporalHistory: false' contradicts 'versioning.temporal.enabled: true'. " +
                "'temporalHistory' is the shorthand for 'temporal.enabled'; set one of them.");
        }

        var enabled = (y.TemporalHistory ?? false) || (t?.Enabled ?? false);
        if (t is null)
        {
            return new TemporalPolicy { Enabled = enabled };
        }

        var validFrom = NullIfBlank(t.ValidFromColumn) ?? TemporalPolicy.DefaultValidFromColumn;
        var validTo = NullIfBlank(t.ValidToColumn) ?? TemporalPolicy.DefaultValidToColumn;

        if (string.Equals(validFrom, validTo, StringComparison.OrdinalIgnoreCase))
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal' validFromColumn and validToColumn must be two distinct names " +
                $"(both are '{validFrom}'); a SYSTEM_TIME period needs one column for each end.");
        }

        var historySchema = NullIfBlank(t.HistorySchema) ?? TemporalPolicy.DefaultHistorySchema;
        var historyTable = NullIfBlank(t.HistoryTable);

        // SQL Server only accepts a two-part history name: the history table always lives in the target's own
        // database, so a dotted name here is a misunderstanding worth catching at authoring time.
        if (historySchema.Contains('.', StringComparison.Ordinal) || (historyTable?.Contains('.', StringComparison.Ordinal) ?? false))
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal' historySchema/historyTable must be plain, undotted names. SQL Server " +
                "requires the history table to live in the same database as the target and accepts only a two-part name.");
        }

        var precision = t.PeriodPrecision ?? TemporalPolicy.DefaultPeriodPrecision;
        if (precision is < 0 or > 7)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal.periodPrecision' must be between 0 and 7 (got {precision}); it is the " +
                "datetime2 fractional-second scale of the two period columns.");
        }

        if (t.RetentionDays is { } days && days <= 0)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.temporal.retentionDays' must be a positive number of days (got {days}); omit it " +
                "to keep history forever.");
        }

        return new TemporalPolicy
        {
            Enabled = enabled,
            HistorySchema = historySchema,
            HistoryTable = historyTable,
            ValidFromColumn = validFrom,
            ValidToColumn = validTo,
            HiddenPeriodColumns = t.HiddenPeriodColumns ?? true,
            PeriodPrecision = precision,
            RetentionDays = t.RetentionDays,
        };
    }

    private static Scd2Policy MapScd2(IngestionScd2Yaml? y, string source)
    {
        if (y is null)
        {
            return new Scd2Policy();
        }

        var validFrom = NullIfBlank(y.ValidFromColumn) ?? "ValidFrom_DW";
        var validTo = NullIfBlank(y.ValidToColumn) ?? "ValidTo_DW";
        var current = NullIfBlank(y.CurrentFlagColumn) ?? "IsCurrent_DW";

        // The three period columns must be distinct, or the engine would write one over another.
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { validFrom, validTo, current };
        if (distinct.Count != 3)
        {
            throw new FlowValidationException(
                $"{source}: 'versioning.scd2' validFromColumn, validToColumn, and currentFlagColumn must be three distinct names " +
                $"(got '{validFrom}', '{validTo}', '{current}').");
        }

        return new Scd2Policy
        {
            Enabled = y.Enabled ?? false,
            ValidFromColumn = validFrom,
            ValidToColumn = validTo,
            CurrentFlagColumn = current,
            TrackedColumns = y.TrackedColumns?.Select(NullIfBlank).Where(c => c is not null).Select(c => c!).ToList() ?? [],
        };
    }

    private static IReadOnlyList<VirtualColumn> MapVirtualColumns(List<IngestionVirtualColumnYaml>? items, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var columns = new List<VirtualColumn>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var expression = NullIfBlank(item.Expression)
                ?? throw new FlowValidationException($"{source}: 'virtualColumns[{i}].expression' is required.");
            columns.Add(new VirtualColumn
            {
                Name = NullIfBlank(item.Name),
                DataType = NullIfBlank(item.DataType),
                DataTypeExpression = NullIfBlank(item.DataTypeExpression),
                SelectExpression = expression,
            });
        }

        return columns;
    }

    private static (IReadOnlyList<string> Names, IReadOnlyList<AssertionDefinition> Definitions) MapAssertions(
        List<IngestionAssertionYaml>? items, string source)
    {
        if (items is null || items.Count == 0)
        {
            return ([], []);
        }

        var names = new List<string>(items.Count);
        var definitions = new List<AssertionDefinition>(items.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var name = NullIfBlank(item.Name)
                ?? throw new FlowValidationException($"{source}: 'assertions[{i}].name' is required.");
            var expression = NullIfBlank(item.Expression)
                ?? throw new FlowValidationException($"{source}: 'assertions[{i}].expression' is required.");
            if (!seen.Add(name))
            {
                throw new FlowValidationException($"{source}: assertion '{name}' is declared more than once.");
            }

            names.Add(name);
            definitions.Add(new AssertionDefinition
            {
                Name = name,
                Expression = expression,
                Mode = YamlDocumentParts.ParseExecutionMode(item.Mode, $"assertions[{i}].mode", source),
            });
        }

        return (names, definitions);
    }

    private static IReadOnlyList<SurrogateKeySpec> MapSurrogateKeys(
        List<IngestionSurrogateKeyYaml>? items, Dictionary<string, DataSource> connections, string source)
    {
        if (items is null || items.Count == 0)
        {
            return [];
        }

        var specs = new List<SurrogateKeySpec>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var table = NullIfBlank(item.Table)
                ?? throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].table' is required.");
            var column = NullIfBlank(item.Column)
                ?? throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].column' is required.");
            if (item.KeyColumns is null || item.KeyColumns.Count == 0)
            {
                throw new FlowValidationException($"{source}: 'surrogateKeys[{i}].keyColumns' is required.");
            }

            var server = NullIfBlank(item.Server);
            if (server is not null && !connections.ContainsKey(server))
            {
                throw new FlowValidationException(
                    $"{source}: 'surrogateKeys[{i}].server' references '{server}', which is not declared under 'connections:'. " +
                    "Leave it unset to generate keys on the target connection.");
            }

            var surrogateTable = YamlDocumentParts.ParseQualifiedObject(table, $"surrogateKeys[{i}].table", source);

            specs.Add(new SurrogateKeySpec
            {
                Server = server,
                SurrogateTable = surrogateTable,
                SurrogateColumn = column,
                KeyColumns = item.KeyColumns,
                SKeyColumns = item.SKeyColumns ?? [],
                PreProcess = NullIfBlank(item.PreProcess),
                PostProcess = NullIfBlank(item.PostProcess),
            });
        }

        return specs;
    }

    private static DateOnly? ParseDate(string? value, string field, string source)
        => YamlDocumentParts.ParseDate(value, field, source);

    private static int StableFlowId(string name) => YamlDocumentParts.StableFlowId(name);

    private static string? NullIfBlank(string? value) => YamlDocumentParts.NullIfBlank(value);
}
