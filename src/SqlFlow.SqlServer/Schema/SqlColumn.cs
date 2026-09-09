namespace SqlFlow.SqlServer.Schema;

/// <summary>What a column is in an evolved target, which drives how it is populated and ordered.</summary>
public enum ColumnRole
{
    /// <summary>A column read from the source and bulk-copied.</summary>
    Source,

    /// <summary>A computed projection column from flw.IngestionVirtual.</summary>
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
    /// <summary>Read from the source and bulk-copied into staging.</summary>
    BulkCopied,

    /// <summary>Computed by an expression (virtual, system, hash, identity); not bulk-copied.</summary>
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

    /// <summary>The T-SQL expression that produces the value, for a computed/virtual/system/hash column.</summary>
    public string? SelectExpression { get; init; }

    public bool IsIdentity { get; init; }

    public bool IsPrimaryKey { get; init; }
}
