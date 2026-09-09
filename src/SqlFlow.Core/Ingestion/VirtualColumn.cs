namespace SqlFlow.Core.Ingestion;

/// <summary>
/// A computed projection column injected into a flow (one flw.IngestionVirtual row). It is produced by a
/// T-SQL expression and need not exist in the source.
/// </summary>
public sealed record VirtualColumn
{
    /// <summary>The output column name (legacy ColumnName). Optional; an expression may name itself.</summary>
    public string? Name { get; init; }

    /// <summary>The declared target data type (legacy DataType), for example <c>nvarchar(50)</c>.</summary>
    public string? DataType { get; init; }

    /// <summary>A T-SQL expression that yields the type (legacy DataTypeExp), for example
    /// <c>CAST('2022-01-01' AS DATE)</c>. An alternative to <see cref="DataType"/>.</summary>
    public string? DataTypeExpression { get; init; }

    /// <summary>The T-SQL select expression that produces the column value (legacy SelectExp).</summary>
    public required string SelectExpression { get; init; }
}
