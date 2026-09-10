namespace SqlFlow.SqlServer.Schema;

/// <summary>The lock and data footprint of a single schema change at the SQL Server level.</summary>
public enum ChangeFootprint
{
    /// <summary>Recorded in metadata only; no data pages touched; the schema-modification lock is held for
    /// microseconds. Safe to apply inline.</summary>
    MetadataOnly,

    /// <summary>Forces a full in-place row rewrite under a schema-modification lock held for the whole
    /// rewrite, blocking every reader, writer, and pipeline on THIS table. Never applied silently inline.</summary>
    TableRewrite,
}

/// <summary>A widening change to an existing target column (ALTER COLUMN), preserving its nullability.</summary>
public sealed record ColumnAlter
{
    public required string Name { get; init; }
    public required SqlDataType FromType { get; init; }
    public required SqlDataType ToType { get; init; }

    /// <summary>The nullability to keep (the existing column's), since evolution never tightens nullability.</summary>
    public bool IsNullable { get; init; }

    /// <summary>The lock/data footprint of this ALTER, set by <see cref="ChangeFootprintClassifier"/>.</summary>
    public required ChangeFootprint Footprint { get; init; }
}

/// <summary>A type change that cannot be applied safely on a key, hash, or identity column. Blocks the load.</summary>
public sealed record CriticalMismatch
{
    public required string Column { get; init; }
    public required string Reason { get; init; }
}

public enum DriftKind
{
    /// <summary>A column present in the target but absent from the source; retained, never dropped.</summary>
    ExtraTargetColumn,

    /// <summary>An incompatible type change on an ordinary column; the target is left unchanged.</summary>
    IncompatibleOrdinaryColumn,

    /// <summary>The desired column is NOT NULL but the target column is nullable; left nullable (never tightened).</summary>
    NullabilityNotTightened,
}

/// <summary>A non-blocking observation about the target schema, surfaced for visibility.</summary>
public sealed record DriftFinding
{
    public required DriftKind Kind { get; init; }
    public required string Column { get; init; }
    public string? Detail { get; init; }
}

/// <summary>
/// The plan for evolving a target table to the desired schema: create it, add new columns, widen existing
/// columns, and the critical mismatches (which block the load) and non-blocking drift. Produced by
/// <see cref="SchemaEvolutionPlanner"/> and turned into DDL by the evolution DDL generator.
/// </summary>
public sealed record EvolutionPlan
{
    public bool CreateTable { get; init; }
    public IReadOnlyList<SqlColumn> CreateColumns { get; init; } = [];
    public IReadOnlyList<SqlColumn> ColumnsToAdd { get; init; } = [];
    public IReadOnlyList<ColumnAlter> ColumnsToAlter { get; init; } = [];
    public IReadOnlyList<CriticalMismatch> CriticalMismatches { get; init; } = [];
    public IReadOnlyList<DriftFinding> Drift { get; init; } = [];

    /// <summary>True when a key, hash, or identity column has an incompatible type change; the load must not proceed.</summary>
    public bool IsBlocked => CriticalMismatches.Count > 0;

    public bool HasChanges => CreateTable || ColumnsToAdd.Count > 0 || ColumnsToAlter.Count > 0;

    /// <summary>The alters that force a full table rewrite (the subset of <see cref="ColumnsToAlter"/> with a
    /// <see cref="ChangeFootprint.TableRewrite"/> footprint), which the executor must not apply silently.</summary>
    public IReadOnlyList<ColumnAlter> RewriteColumns =>
        ColumnsToAlter.Where(a => a.Footprint == ChangeFootprint.TableRewrite).ToList();

    public bool HasRewrite => ColumnsToAlter.Any(a => a.Footprint == ChangeFootprint.TableRewrite);
}
