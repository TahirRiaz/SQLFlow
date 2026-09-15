namespace SqlFlow.SqlServer.Schema;

/// <summary>What a column is in an evolved target, which drives how it is populated and ordered.</summary>
public enum ColumnRole
{
    /// <summary>A column read from the source and bulk-copied.</summary>
    Source,

    /// <summary>A virtual column from flw.IngestionVirtual: read from the source as its select expression and
    /// bulk-copied like a source column.</summary>
    Virtual,

    /// <summary>An injected system audit column (the _DW family).</summary>
    System,

    /// <summary>The injected change-detection hash key column.</summary>
    HashKey,

    /// <summary>The injected identity column.</summary>
    Identity,
}

/// <summary>How a column's value is produced.</summary>
public enum ColumnOrigin
{
    /// <summary>Produced by the source read and bulk-copied into staging: a source column, or a virtual column's
    /// expression.</summary>
    BulkCopied,

    /// <summary>Maintained by the engine on the target (system, hash, identity); not part of the source read.</summary>
    Computed,
}

/// <summary>
/// A target column in structured form, the unit the schema-evolution planner and DDL generator work on.
/// Carries the structured <see cref="SqlDataType"/> (so widening is possible), the role and origin (so the
/// upsert select-list and the bulk-copy mapping are correct), and identity/key facts.
/// </summary>
public sealed record SqlColumn
{
    public required string Name { get; init; }

    public required SqlDataType DataType { get; init; }

    public bool IsNullable { get; init; } = true;

    public ColumnRole Role { get; init; } = ColumnRole.Source;

    public ColumnOrigin Origin { get; init; } = ColumnOrigin.BulkCopied;

    /// <summary>The source-dialect select expression that produces a virtual column's value; null otherwise.</summary>
    public string? SelectExpression { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsPrimaryKey { get; init; }
}
