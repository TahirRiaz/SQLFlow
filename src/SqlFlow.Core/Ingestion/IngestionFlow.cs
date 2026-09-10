namespace SqlFlow.Core.Ingestion;

/// <summary>The source side of an ingestion flow: a connection-registry alias, a three-part object, and the
/// read-shaping filters.</summary>
public sealed record IngestionSource
{
    /// <summary>The flw.SysDataSource alias (legacy srcServer). The connection reference is <c>@</c> + this.</summary>
    public required string Server { get; init; }

    public required RelationalObject Table { get; init; }             // srcDBSchTbl

    public string? Filter { get; init; }                              // srcFilter
    public bool FilterIsAppend { get; init; } = true;                 // srcFilterIsAppend
    public string? IncrementalClause { get; init; }                   // IncrementalClauseExp
    public IReadOnlyList<string> IgnoreColumns { get; init; } = [];    // IgnoreColumns
    public string? DataSetColumn { get; init; }                       // DataSetColumn

    /// <summary>The connection reference understood by the connection resolver.</summary>
    public string ConnectionReference => "@" + Server;
}

/// <summary>The target side of an ingestion flow: a connection-registry alias, a three-part object, and the
/// target-shaping options.</summary>
public sealed record IngestionTarget
{
    /// <summary>The flw.SysDataSource alias (legacy trgServer). The connection reference is <c>@</c> + this.</summary>
    public required string Server { get; init; }

    public required RelationalObject Table { get; init; }             // trgDBSchTbl

    public bool TruncateBeforeLoad { get; init; }                     // TruncateTrg
    public string? DesiredIndexes { get; init; }                      // trgDesiredIndex
    public bool ColumnStoreIndex { get; init; }                       // ColumnStoreIndexOnTrg
    public string? IdentityColumn { get; init; }                      // IdentityColumn

    /// <summary>The connection reference understood by the connection resolver.</summary>
    public string ConnectionReference => "@" + Server;
}

/// <summary>
/// A complete relational (SQL to SQL Server) ingestion flow: the V3 model of one flw.Ingestion row joined to
/// its flw.IngestionVirtual children. It is distinct from the file-oriented FlowDefinition because the source
/// is a live relational system addressed through the connection registry, and the load surface (keyed
/// update/insert, change detection, schema sync, system columns, versioning) is far richer. Immutable.
/// </summary>
public sealed record IngestionFlow
{
    /// <summary>The legacy numeric identity (flw.Ingestion.FlowID).</summary>
    public int FlowId { get; init; }

    public string? Batch { get; init; }              // Batch

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;
    public string? SysAlias { get; init; }           // SysAlias
    public string? Description { get; init; }        // Description
    public string FlowType { get; init; } = "ing";   // FlowType
    public bool DeactivateFromBatch { get; init; }   // DeactivateFromBatch
    public int? BatchOrderBy { get; init; }          // BatchOrderBy
    public bool OnErrorResume { get; init; } = true; // OnErrorResume
    public int? FromObjectMK { get; init; }          // FromObjectMK (lineage)
    public int? ToObjectMK { get; init; }            // ToObjectMK (lineage)
    public string? CreatedBy { get; init; }          // CreatedBy
    public DateTime? CreatedDate { get; init; }      // CreatedDate

    public required IngestionSource Source { get; init; }
    public required IngestionTarget Target { get; init; }

    public IReadOnlyList<VirtualColumn> VirtualColumns { get; init; } = [];

    public IReadOnlyList<SurrogateKeySpec> SurrogateKeys { get; init; } = [];

    public IngestionLoadPolicy Load { get; init; } = new();
    public MatchKeyPolicy MatchKeys { get; init; } = new();
    public ChangePolicy Change { get; init; } = new();
    public SystemColumnsPolicy SystemColumns { get; init; } = new();
    public SchemaSyncPolicy SchemaSync { get; init; } = new();
    public IncrementalPolicy Incremental { get; init; } = new();
    public InitLoadPolicy InitLoad { get; init; } = new();
    public VersioningPolicy Versioning { get; init; } = new();
    public IReadOnlyList<string> Assertions { get; init; } = [];   // Assertions
    public ProcessPolicy Process { get; init; } = new();

    /// <summary>
    /// The pre-ingestion transform policy: inference and authored per-column transforms projected into the typed
    /// transformation view (<c>[schema].[v&lt;Table&gt;]</c>) refreshed over the target after the load. Used by
    /// external-database landings (the flow's target is the pre/staging table, and the downstream chained flow
    /// reads the view); a native SQL-to-SQL flow leaves this at its default (inference off, no columns), which
    /// generates nothing.
    /// </summary>
    public Model.TypeInferencePolicy Transform { get; init; } = new();
}
