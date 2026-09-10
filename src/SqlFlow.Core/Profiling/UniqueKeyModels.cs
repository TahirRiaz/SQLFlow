namespace SqlFlow.Core.Profiling;

/// <summary>
/// A distinct-tuple and null measurement of a column set over a working set of rows (a sample or the full table).
/// It is the primitive the search reasons over: a column set is a key exactly when it has no nulls and one row per
/// distinct tuple. <see cref="Exact"/> distinguishes a measurement taken against the whole table from one taken
/// against a sample, so a sampled candidate is never reported as proven without a full-table check.
/// </summary>
public sealed record SetMeasure
{
    /// <summary>The columns measured, in the order given.</summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>Rows in the working set the measurement ran over.</summary>
    public required long Scanned { get; init; }

    /// <summary>Distinct value tuples among the rows where no column in the set is null.</summary>
    public required long Distinct { get; init; }

    /// <summary>Rows where at least one column in the set is null (a null cannot take part in a unique key).</summary>
    public required long Nulls { get; init; }

    /// <summary>True when measured against the whole table, false when against a sample.</summary>
    public required bool Exact { get; init; }

    /// <summary>A key must have no nulls and exactly one row per distinct tuple over a non-empty set.</summary>
    public bool IsUnique => Nulls == 0 && Scanned > 0 && Distinct == Scanned;

    /// <summary>Non-null rows that collide with another on this tuple (0 exactly when the set is unique).</summary>
    public long Duplicates => Math.Max(0, Scanned - Nulls - Distinct);
}

/// <summary>An exact whole-table verdict for one column set, produced by a short-circuiting duplicate/null probe (no
/// full distinct count): the set is a key iff it has no null row and no repeated tuple.</summary>
public sealed record SetVerdict
{
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>True when the whole table has neither a null in the set nor any repeated tuple.</summary>
    public required bool IsUnique { get; init; }

    /// <summary>True when at least one row is null in some column of the set.</summary>
    public required bool HasNulls { get; init; }

    /// <summary>Rows the verdict was measured over (the whole table).</summary>
    public required long Rows { get; init; }
}

/// <summary>The cardinality of a single column: how many distinct non-null values it has and how many rows are null.
/// Drives the ranking that seeds composite search and is surfaced in the report.</summary>
public sealed record ColumnCardinality
{
    public required string Column { get; init; }

    public required long Distinct { get; init; }

    public required long Nulls { get; init; }

    public required long Scanned { get; init; }

    /// <summary>Distinct non-null values per non-null row: 1.0 means every value is unique, near 0 means highly
    /// repetitive. The ranking key for how identifying a column is.</summary>
    public double Selectivity => Scanned - Nulls > 0 ? (double)Distinct / (Scanned - Nulls) : 0d;
}

/// <summary>A candidate key: the column set, whether it is unique, and whether that verdict was confirmed against the
/// whole table (not just a sample). A non-unique candidate carries its duplicate count so a near miss is informative;
/// <see cref="Estimated"/> flags counts that come from the sample rather than an exact whole-table measurement.</summary>
public sealed record UniqueKeyCandidate
{
    public required IReadOnlyList<string> Columns { get; init; }

    public required bool IsUnique { get; init; }

    /// <summary>True when the verdict was measured against the whole table; false when only a sample was seen.</summary>
    public required bool Verified { get; init; }

    /// <summary>True when the set came from the store's own metadata (an enforced unique index or constraint) rather
    /// than from profiling the rows. A declared key is proven by the engine that maintains it, so it is reported
    /// verified without a scan.</summary>
    public bool Declared { get; init; }

    public required long Distinct { get; init; }

    public required long Nulls { get; init; }

    /// <summary>Rows the verdict was measured over.</summary>
    public required long Rows { get; init; }

    public required long Duplicates { get; init; }

    /// <summary>True when <see cref="Distinct"/>/<see cref="Duplicates"/> are sample estimates, not exact counts.</summary>
    public bool Estimated { get; init; }

    public double Selectivity => Rows - Nulls > 0 ? (double)Distinct / (Rows - Nulls) : 0d;
}

/// <summary>A column removed from the search before any row was read, with the reason: a type that cannot form a
/// practical key (LOB, floating point, CLR, rowversion, sql_variant, a non-persisted computed column), or a column
/// the live object no longer exposes. Surfaced in the report so an ignored column explains itself.</summary>
public sealed record ExcludedColumn
{
    public required string Column { get; init; }

    public required string Reason { get; init; }
}

/// <summary>The result of detection: the table's size, whether a sample was used, the per-column cardinalities, and
/// the ranked candidate keys (most trustworthy first). <see cref="Note"/> explains a sample or a no-key outcome.</summary>
public sealed record UniqueKeyReport
{
    /// <summary>The analyzed object's display name, set by the caller.</summary>
    public string? ObjectName { get; init; }

    public required long TotalRows { get; init; }

    /// <summary>Rows actually scanned during the search (equal to <see cref="TotalRows"/> unless sampled).</summary>
    public required long ScannedRows { get; init; }

    public required bool Sampled { get; init; }

    public required IReadOnlyList<ColumnCardinality> Columns { get; init; }

    public required IReadOnlyList<UniqueKeyCandidate> Candidates { get; init; }

    /// <summary>Columns removed from the search before any row was read, each with its reason (empty when all the
    /// requested columns were searchable). Populated by callers that know the exclusions, such as the CLI.</summary>
    public IReadOnlyList<ExcludedColumn> ExcludedColumns { get; init; } = [];

    public string? Note { get; init; }
}

/// <summary>Tuning for detection: the widest composite to try, how many candidates to return, how many top-ranked
/// columns to draw on when building composites, and whether to verify sampled candidates against the whole table.</summary>
public sealed record UniqueKeyOptions
{
    /// <summary>The widest composite key to consider.</summary>
    public int MaxKeyColumns { get; init; } = 4;

    /// <summary>How many candidates to return.</summary>
    public int MaxCandidates { get; init; } = 5;

    /// <summary>How many top-ranked columns to draw on when building composites.</summary>
    public int MaxScanColumns { get; init; } = 12;

    /// <summary>When a sample was used, confirm each surviving candidate against the whole table (an early-exit
    /// duplicate probe). Turning this off returns fast, sample-only candidates marked unverified.</summary>
    public bool Verify { get; init; } = true;
}

/// <summary>
/// The measurement surface the detector needs, shaped for a cheap search on a large table. <see cref="MeasureManyAsync"/>
/// measures many column sets in one pass so a whole greedy level costs a single scan of the working set (a sample when
/// the table is large); <see cref="VerifyAsync"/> confirms a finalist against the whole table with a short-circuiting
/// duplicate probe rather than a full distinct count. A backing implementation may hold resources (an open connection,
/// a materialized sample) for its lifetime, hence <see cref="IAsyncDisposable"/>. The totals are fixed on creation.
/// </summary>
public interface IUniquenessProbe : IAsyncDisposable
{
    /// <summary>Exact total rows in the table.</summary>
    long TotalRows { get; }

    /// <summary>Rows the sampled measurements scan; equal to <see cref="TotalRows"/> when not sampling.</summary>
    long WorkingRows { get; }

    /// <summary>True when the working set is a sample smaller than the whole table.</summary>
    bool Sampled { get; }

    /// <summary>Column sets the store itself already enforces as unique (an enabled, unfiltered unique index or
    /// constraint), empty when none exist or the store has no such metadata. A declared set is proven by the engine
    /// that maintains it, so the detector reports it without reading a single row.</summary>
    IReadOnlyList<IReadOnlyList<string>> DeclaredUniqueKeys { get; }

    /// <summary>Measures every given column set over the working set in a single pass (empty input returns empty).</summary>
    Task<IReadOnlyList<SetMeasure>> MeasureManyAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct = default);

    /// <summary>Confirms a column set against the whole table with a short-circuiting null/duplicate probe.</summary>
    Task<SetVerdict> VerifyAsync(IReadOnlyList<string> columns, CancellationToken ct = default);
}
