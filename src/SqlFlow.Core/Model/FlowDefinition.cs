using SqlFlow.Core.Identity;

namespace SqlFlow.Core.Model;

/// <summary>A complete, validated pipeline definition.</summary>
public sealed record FlowDefinition
{
    public required string Name { get; init; }

    /// <summary>
    /// Stable, system-generated flow identity: a GUID computed deterministically from <see cref="Name"/>.
    /// Identical on every execution (unlike the per-run RunId), so logs, lineage, and run history join
    /// on it. It is not authored in YAML; renaming a flow yields a new identity by design.
    /// </summary>
    public Guid FlowId => FlowIdentity.FromName(Name);

    /// <summary>The batch (source system) this flow belongs to, the grouping label under which its runs report
    /// and its estate siblings execute jointly. Optional in YAML; a flow without one reports under the catalog's
    /// default batch.</summary>
    public string? Batch { get; init; }

    /// <summary>The flow's declared lifecycle (the YAML <c>lifecycle:</c>, production by default): a development
    /// flow runs exactly like a production one but never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    public required SourceSpec Source { get; init; }
    public required TargetSpec Target { get; init; }
    public SchemaPolicy Schema { get; init; } = new();
    public LoadPolicy Load { get; init; } = new();
    public TypeInferencePolicy Inference { get; init; } = new();

    /// <summary>SQL run on the target before the load (inline statements or <c>EXEC</c> a procedure).</summary>
    public IReadOnlyList<string> PreProcess { get; init; } = [];

    /// <summary>SQL run on the target after the load.</summary>
    public IReadOnlyList<string> PostProcess { get; init; } = [];

    /// <summary>
    /// Indexes the target table should have, as one or more <c>CREATE INDEX</c> statements. Applied
    /// only when the table is created during the run; on an existing table the load-time index manager
    /// disables and rebuilds whatever indexes are already present.
    /// </summary>
    public string? DesiredIndexes { get; init; }

    /// <summary>Incremental-load settings. When null the flow loads everything it reads.</summary>
    public IncrementalSpec? Incremental { get; init; }
}

/// <summary>
/// Incremental-load settings. Two mutually exclusive modes, both stateless (the target table is the state, so
/// no control database is needed):
/// <list type="bullet">
/// <item><b>File-date</b> (default): the engine probes the target for the high-water mark of
/// <see cref="DateColumn"/> and only reads source files dated after it (minus <see cref="OverlapDays"/>), so a
/// re-run picks up just the new files. Honored by the file readers (CSV/JSON/XML/Excel/Parquet).</item>
/// <item><b>Row-level watermark</b> (when <see cref="WatermarkColumn"/> is set): the engine probes the target
/// for <c>MAX(WatermarkColumn)</c> (minus <see cref="WatermarkOverlap"/>) and reads only source rows past it.
/// DuckDB pushes the bound into its scan (row-group skipping); every reader is then filtered row-by-row against
/// the same bound, so the result is identical regardless of source kind.</item>
/// </list>
/// </summary>
public sealed record IncrementalSpec
{
    /// <summary>
    /// Fully qualified table to probe for the watermark (e.g. <c>[db].[dbo].[Silver]</c>). Defaults to
    /// the flow's own target when omitted.
    /// </summary>
    public string? Table { get; init; }

    /// <summary>Date/datetime column whose MAX is the file-date watermark. Defaults to the file-date column.</summary>
    public string DateColumn { get; init; } = "FileDate_DW";

    /// <summary>Days subtracted from the file-date watermark to re-read a safety window. Default 0 (exact).</summary>
    public int OverlapDays { get; init; }

    /// <summary>
    /// When set, switches to row-level mode: the data column (present on both source and target) whose
    /// <c>MAX</c> on the target is the high-water mark, and against which source rows are filtered. Use a name
    /// that survives column-name cleanup unchanged (the common watermark columns - an id, a load timestamp, a
    /// commit version - already do), so the source predicate and the target probe address the same column.
    /// </summary>
    public string? WatermarkColumn { get; init; }

    /// <summary>
    /// Overlap window subtracted from the row-level watermark to re-read a safety margin: days for a
    /// date/datetime column, raw units for a numeric column, ignored for text. Default 0 (exact, <c>&gt; MAX</c>).
    /// </summary>
    public double WatermarkOverlap { get; init; }

    /// <summary>Ignore the watermark and read everything (a forced full reload), for either mode.</summary>
    public bool FullLoad { get; init; }
}

/// <summary>
/// A source descriptor that is agnostic to the kind of system being read - relational
/// (SQL Server, PostgreSQL, MySQL, ODBC, …) or non-relational (CSV, JSON, Parquet, Excel, XML,
/// REST, NoSQL, …). <see cref="Type"/> selects an <see cref="Abstractions.ISourceReader"/>;
/// <see cref="Location"/> is the path/URI/connection reference; <see cref="Options"/> carries
/// reader-specific settings. Adding a new source type requires implementing one interface - no
/// change to the model or the engine.
/// </summary>
public sealed record SourceSpec
{
    /// <summary>Open-ended discriminator, e.g. "csv", "json", "parquet", "ado", "rest".</summary>
    public required string Type { get; init; }

    /// <summary>A path, URI, or named connection reference, interpreted by the reader.</summary>
    public string? Location { get; init; }

    /// <summary>Reader-specific options (e.g. delimiter, query, root path).</summary>
    public IReadOnlyDictionary<string, string?> Options { get; init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
}

public sealed record TargetSpec
{
    public required string Connection { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }

    public string QualifiedName => $"[{Escape(Schema)}].[{Escape(Table)}]";

    private static string Escape(string part) => part.Replace("]", "]]", StringComparison.Ordinal);
}

public enum SchemaEvolution
{
    /// <summary>Create the table if missing; never alter an existing table.</summary>
    Create,

    /// <summary>Create if missing; add new source columns to an existing table.</summary>
    Widen,

    /// <summary>Create if missing; fail if the source has columns the target lacks.</summary>
    Strict,
}

public sealed record ColumnOverride
{
    public string? Type { get; init; }
    public bool? Nullable { get; init; }
}

public sealed record SchemaPolicy
{
    public SchemaEvolution Evolve { get; init; } = SchemaEvolution.Widen;

    /// <summary>
    /// Type used for raw-layer (untyped/string) columns when creating the target. Mirrors SQLFlow's
    /// <c>DefaultColDataType</c> / SysCFG default of <c>varchar(255)</c>; override per flow as needed.
    /// </summary>
    public string DefaultColumnType { get; init; } = "varchar(255)";

    public IReadOnlyDictionary<string, ColumnOverride> Overrides { get; init; } =
        new Dictionary<string, ColumnOverride>(StringComparer.OrdinalIgnoreCase);
}

public enum LoadMode
{
    Append,
    TruncateLoad,
}

public sealed record LoadPolicy
{
    public LoadMode Mode { get; init; } = LoadMode.Append;
    public int BatchSize { get; init; } = 50_000;
    public bool TableLock { get; init; } = true;

    /// <summary>Script &amp; drop non-clustered indexes before the load, then recreate them after.</summary>
    public bool ManageIndexes { get; init; }

    /// <summary>
    /// Reset (truncate) a chained landing target at the start of the next run once every direct consumer has
    /// consolidated it (default true). The chained landing pattern
    /// (<c>file -&gt; [pre].[&lt;Table&gt;] -&gt; view [pre].[v_&lt;Table&gt;] -&gt; silver</c>) makes the landing table pure
    /// staging: after the downstream flows have merged its rows into their own durable tables, keeping the
    /// history in the landing table only makes it grow without bound. The reset applies only when the
    /// control plane authorizes it for the run (see <see cref="Runs.LandingReset"/>): the flow appends
    /// (<see cref="LoadMode.Append"/>), generates the typed view, the target existed before the run, and
    /// EVERY flow that directly reads the typed view has completed a successful run after this flow's last
    /// successful load, proving the staged rows were delivered one phase downstream (bronze is freed when
    /// silver has the data; what gold holds is irrelevant). A direct CLI run, a flow with no consumers, or a
    /// consumer that has not caught up leaves the table untouched. Set false to keep a landing table's
    /// history across runs (for example while a report still reads the base table directly).
    /// </summary>
    public bool ResetWhenConsolidated { get; init; } = true;
}
