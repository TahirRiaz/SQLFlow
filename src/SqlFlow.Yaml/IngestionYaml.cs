namespace SqlFlow.Yaml;

// Binding-only DTOs for the relational ingestion document (flowType: ing). Every property is nullable; all
// defaults and validation live in YamlIngestionFlowLoader, which maps these into the immutable
// SqlFlow.Core.Ingestion records.

internal sealed class IngestionYaml
{
    public string? FlowType { get; set; }
    public string? Name { get; set; }
    public string? Batch { get; set; }
    public string? Lifecycle { get; set; }
    public string? Description { get; set; }

    /// <summary>Each value is either a plain string (a SQL Server connection reference, the back-compatible
    /// form) or a map with 'provider' and 'connection' keys; the loader disambiguates.</summary>
    public Dictionary<string, object>? Connections { get; set; }
    public IngestionEndpointYaml? Source { get; set; }
    public IngestionEndpointYaml? Target { get; set; }
    public IngestionLoadYaml? Load { get; set; }
    public IngestionMatchKeysYaml? MatchKeys { get; set; }
    public IngestionChangeYaml? Change { get; set; }
    public IngestionSystemColumnsYaml? SystemColumns { get; set; }
    public IngestionSchemaYaml? Schema { get; set; }
    public IngestionIncrementalYaml? Incremental { get; set; }
    public IngestionInitLoadYaml? InitLoad { get; set; }
    public IngestionVersioningYaml? Versioning { get; set; }

    /// <summary>The pre-ingestion transform block, shared shape with the file flow's (see
    /// <see cref="TransformYaml"/>): inference + authored per-column transforms + the view toggle.</summary>
    public TransformYaml? Transform { get; set; }

    public string? PreProcess { get; set; }
    public string? PostProcess { get; set; }
    public string? PreInvoke { get; set; }
    public string? PostInvoke { get; set; }
    public Dictionary<string, InvokeBlockYaml>? Invokes { get; set; }
    public Dictionary<string, ServicePrincipalYaml>? ServicePrincipals { get; set; }
    public List<IngestionVirtualColumnYaml>? VirtualColumns { get; set; }
    public List<IngestionAssertionYaml>? Assertions { get; set; }
    public List<IngestionSurrogateKeyYaml>? SurrogateKeys { get; set; }

    /// <summary>The embedded ML health check over this flow's target table: the same declaration body as a
    /// standalone hc document, minus target/connections (both come from the flow). Expands into a derived hc
    /// pipeline that runs on demand by default (mode: manual).</summary>
    public IngestionHealthCheckYaml? HealthCheck { get; set; }
}

/// <summary>The embedded <c>healthCheck:</c> block of an ingestion flow. The monitored table is always the
/// flow's target (that is the point of embedding); everything else mirrors the standalone hc document.</summary>
internal sealed class IngestionHealthCheckYaml
{
    /// <summary>The derived flow's name; defaults to <c>&lt;flowName&gt;_hc</c>.</summary>
    public string? Name { get; set; }

    public string? Description { get; set; }

    /// <summary>manual (default: the embedded check runs only when triggered from the GUI/API/CLI) or auto
    /// (schedules and batch/node group runs execute it like any flow, ordered after the load by lineage).</summary>
    public string? Mode { get; set; }

    public string? DateColumn { get; set; }

    /// <summary>The single-metric shorthand: an aggregate expression. Mutually exclusive with 'metrics'.</summary>
    public string? BaseValue { get; set; }

    public List<HealthCheckMetricYaml>? Metrics { get; set; }

    public string? Filter { get; set; }

    public int? MaturityDays { get; set; }

    public string? SentinelDateFloor { get; set; }

    public HealthCheckMlYaml? Ml { get; set; }

    public List<string>? Holidays { get; set; }
}

internal sealed class IngestionEndpointYaml
{
    public string? Server { get; set; }
    public string? Connection { get; set; }

    /// <summary>The provider of a direct <c>connection:</c> (mssql | azdb | mysql | postgres); SQL Server
    /// when omitted. Ignored when <c>server:</c> references a declared connection (which carries its own).</summary>
    public string? Provider { get; set; }

    public string? Object { get; set; }
    public string? Table { get; set; }

    // Source-only refinements.
    public string? Filter { get; set; }
    public bool? FilterIsAppend { get; set; }
    public string? IncrementalClause { get; set; }
    public List<string>? IgnoreColumns { get; set; }
    public string? DataSetColumn { get; set; }

    // Target-only refinements.
    public bool? TruncateBeforeLoad { get; set; }
    public bool? ColumnStoreIndex { get; set; }
    public string? IdentityColumn { get; set; }
    public string? DesiredIndexes { get; set; }
}

internal sealed class IngestionLoadYaml
{
    public List<string>? KeyColumns { get; set; }
    public bool? SkipUpdateExisting { get; set; }
    public bool? SkipInsertNew { get; set; }
    public bool? MatchKeysInSourceAndTarget { get; set; }
    public bool? BatchUpsert { get; set; }
    public int? BatchUpsertRowCount { get; set; }
    public string? DataSetColumn { get; set; }
    public string? ReloadColumn { get; set; }
    public bool? StreamData { get; set; }
    public int? Threads { get; set; }
    public bool? KeepStagingTable { get; set; }
    public bool? TruncateStagingOnCompletion { get; set; }
    public bool? TruncateSourceWhenConsolidated { get; set; }
}

internal sealed class IngestionChangeYaml
{
    public List<string>? HashColumns { get; set; }
    public string? HashType { get; set; }
    public List<string>? IgnoreColumnsInHash { get; set; }
}

internal sealed class IngestionSystemColumnsYaml
{
    public bool? InsertedDate { get; set; }
    public bool? UpdatedDate { get; set; }
    public bool? DeletedDate { get; set; }
    public bool? RowStatus { get; set; }
}

internal sealed class IngestionSchemaYaml
{
    public bool? Sync { get; set; }
    public bool? CleanColumnNames { get; set; }
    public string? CleanColumnNameRegex { get; set; }
    public string? ReplaceInvalidCharsWith { get; set; }
    public bool? ConvertUnicodeToNonUnicode { get; set; }
    public bool? AllowTableRewrite { get; set; }
}

internal sealed class IngestionIncrementalYaml
{
    public List<string>? Columns { get; set; }
    public string? DateColumn { get; set; }
    public int? OverlapDays { get; set; }
    public int? Lookback { get; set; }
    public bool? FullLoad { get; set; }
    public bool? FetchMinValuesFromSource { get; set; }
}

internal sealed class IngestionInitLoadYaml
{
    public bool? Enabled { get; set; }
    public string? FromDate { get; set; }
    public string? ToDate { get; set; }
    public string? BatchBy { get; set; }
    public int? BatchSize { get; set; }
    public string? KeyColumn { get; set; }
    public int? KeyMaxValue { get; set; }
}

internal sealed class IngestionVersioningYaml
{
    /// <summary>The legacy trgVersioning shorthand: enables versioning.temporal with every default.</summary>
    public bool? TemporalHistory { get; set; }

    public IngestionTemporalYaml? Temporal { get; set; }
    public bool? InsertUnknownDimensionRow { get; set; }
    public bool? TokenVersioning { get; set; }
    public int? TokenRetentionDays { get; set; }
    public IngestionScd2Yaml? Scd2 { get; set; }
}

internal sealed class IngestionTemporalYaml
{
    public bool? Enabled { get; set; }
    public string? HistorySchema { get; set; }
    public string? HistoryTable { get; set; }
    public string? ValidFromColumn { get; set; }
    public string? ValidToColumn { get; set; }
    public bool? HiddenPeriodColumns { get; set; }
    public int? PeriodPrecision { get; set; }
    public int? RetentionDays { get; set; }
}

internal sealed class IngestionScd2Yaml
{
    public bool? Enabled { get; set; }
    public string? ValidFromColumn { get; set; }
    public string? ValidToColumn { get; set; }
    public string? CurrentFlagColumn { get; set; }
    public List<string>? TrackedColumns { get; set; }
}

internal sealed class IngestionMatchKeysYaml
{
    /// <summary>'tag' (soft delete, default) or 'delete' (hard delete).</summary>
    public string? Action { get; set; }

    /// <summary>Override the flow's load.keyColumns for the match comparison. Empty inherits them.</summary>
    public List<string>? KeyColumns { get; set; }

    /// <summary>Skip the action when the affected percentage exceeds this (0-100, default 20).</summary>
    public int? ThresholdPercent { get; set; }

    /// <summary>Tag only rows whose dateColumn is within this many months. Null applies the tag to all.</summary>
    public int? IgnoreDeletedRowsAfterMonths { get; set; }

    /// <summary>The date column for the ignore window (falls back to incremental.dateColumn).</summary>
    public string? DateColumn { get; set; }

    /// <summary>A predicate bounding the source key read (raw-append, carries its own leading AND).</summary>
    public string? SourceFilter { get; set; }

    /// <summary>A predicate bounding which target rows the pass may touch (raw-append).</summary>
    public string? TargetFilter { get; set; }
}

internal sealed class IngestionVirtualColumnYaml
{
    public string? Name { get; set; }
    public string? DataType { get; set; }
    public string? DataTypeExpression { get; set; }
    public string? Expression { get; set; }
}

internal sealed class IngestionAssertionYaml
{
    public string? Name { get; set; }
    public string? Expression { get; set; }

    /// <summary>auto (default) evaluates with every ingestion run; manual reserves the assertion for an
    /// on-demand assertions-only run.</summary>
    public string? Mode { get; set; }
}

internal sealed class IngestionSurrogateKeyYaml
{
    public string? Server { get; set; }
    public string? Table { get; set; }
    public string? Column { get; set; }
    public List<string>? KeyColumns { get; set; }
    public List<string>? SKeyColumns { get; set; }
    public string? PreProcess { get; set; }
    public string? PostProcess { get; set; }
}
