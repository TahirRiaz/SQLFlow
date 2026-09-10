using System.Text;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Assembles the per-column inferred expressions into one runnable transform SELECT against the source
/// table. Pure string building, so it is fully testable and does not touch the database.
/// </summary>
public static class TransformSelectBuilder
{
    public static string Build(string schema, string table, IReadOnlyList<InferredColumn> columns, string? whereClause = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(columns);

        var builder = new StringBuilder("SELECT");
        if (columns.Count == 0)
        {
            builder.Append(" *");
        }
        else
        {
            builder.Append('\n');
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                builder.Append("    ").Append(column.SelectExpression).Append(" AS [").Append(Escape(column.ColumnName)).Append(']');
                builder.Append(i < columns.Count - 1 ? ",\n" : "\n");
            }
        }

        builder.Append("FROM [").Append(Escape(schema)).Append("].[").Append(Escape(table)).Append(']');

        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            builder.Append("\nWHERE ").Append(whereClause.Trim());
        }

        builder.Append(';');
        return builder.ToString();
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
