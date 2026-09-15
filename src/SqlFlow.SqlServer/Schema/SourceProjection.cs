using SqlFlow.Core.Connections;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// One item of an ingestion flow's source read. A plain entry reads a source column as itself; a virtual entry
/// (a legacy flw.IngestionVirtual row) reads a select expression evaluated by the source database, aliased to
/// <see cref="SourceName"/>. Either way the bulk copy maps the reader column <see cref="SourceName"/> onto the
/// staging column <see cref="TargetName"/>, so every projection lands in staging as a data column.
/// </summary>
public sealed record SourceProjection
{
    /// <summary>The column name the source read exposes: the raw source column name, or the virtual column's name.</summary>
    public required string SourceName { get; init; }

    /// <summary>The staging and target column name (after name cleanup, for a source column).</summary>
    public required string TargetName { get; init; }

    /// <summary>The source-dialect select expression of a virtual column; null for a plain source column.</summary>
    public string? Expression { get; init; }

    /// <summary>
    /// Renders the select-list item in the source's own SQL: the quoted column, or the expression aliased to the
    /// quoted name. <paramref name="alias"/> replaces <see cref="SourceName"/> as the rendered name, for a read that
    /// addresses the column by the spelling the flow declared.
    /// </summary>
    public string Render(ISourceSqlDialect dialect, string? alias = null)
    {
        ArgumentNullException.ThrowIfNull(dialect);
        var name = dialect.QuoteIdentifier(alias ?? SourceName);
        return Expression is null ? name : $"{Expression} AS {name}";
    }
}
