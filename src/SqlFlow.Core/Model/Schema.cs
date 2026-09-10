namespace SqlFlow.Core.Model;

/// <summary>A column as described by a source, in CLR terms (dialect-agnostic).</summary>
public sealed record SourceColumn
{
    public required string Name { get; init; }
    public required Type Type { get; init; }
    public int? MaxLength { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool IsNullable { get; init; } = true;

    /// <summary>
    /// Explicit target SQL type for columns whose type the engine knows exactly (e.g. an injected
    /// <c>varbinary(64)</c> hash key). When set it overrides CLR-to-SQL mapping. Null means infer from
    /// <see cref="Type"/>.
    /// </summary>
    public string? SqlType { get; init; }
}

/// <summary>A column expressed in target (SQL Server) terms.</summary>
public sealed record ColumnDefinition
{
    public required string Name { get; init; }
    public required string SqlType { get; init; }
    public bool IsNullable { get; init; } = true;
}

public sealed record TableSchema
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<ColumnDefinition> Columns { get; init; }
}

/// <summary>The difference between a desired schema and the live target.</summary>
public sealed record SchemaDelta
{
    public bool CreateTable { get; init; }
    public IReadOnlyList<ColumnDefinition> ColumnsToAdd { get; init; } = [];

    /// <summary>Existing target columns that must widen to accept the incoming data, each carrying the
    /// widened SQL type and the live column's nullability (widening never tightens NULL to NOT NULL). Empty
    /// on a create (the table is built at the desired shape) and always empty under the Create/Strict
    /// policies.</summary>
    public IReadOnlyList<ColumnDefinition> ColumnsToAlter { get; init; } = [];

    public bool HasChanges => CreateTable || ColumnsToAdd.Count > 0 || ColumnsToAlter.Count > 0;
}
