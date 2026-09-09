using System.Text;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Builds the <c>CREATE OR ALTER VIEW</c> statement for the typed transformation view over the raw landing
/// table. The view's SELECT is the resolved projection (see <see cref="ColumnTransformResolver"/>): every column
/// cast/renamed/computed as authored or inferred, in the resolved order. Pure string building - no database - so
/// the emitted DDL is fully testable. <c>CREATE OR ALTER</c> makes re-running a flow idempotent: the view is
/// refreshed to the current projection on each run, which is what keeps dynamic schema evolution working.
/// </summary>
public static class TransformViewBuilder
{
    /// <summary>
    /// Builds <c>CREATE OR ALTER VIEW [viewSchema].[viewName] AS SELECT &lt;projection&gt; FROM
    /// [rawSchema].[rawTable]</c>. <paramref name="columns"/> is the resolved, ordered projection; an empty
    /// projection selects <c>*</c> (a pass-through view). <paramref name="whereClause"/> is appended verbatim as
    /// a pre-filter when set (the legacy PreFilter).
    /// </summary>
    public static string Build(
        string viewSchema,
        string viewName,
        string rawSchema,
        string rawTable,
        IReadOnlyList<InferredColumn> columns,
        string? whereClause = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(viewSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawSchema);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawTable);
        ArgumentNullException.ThrowIfNull(columns);

        var builder = new StringBuilder();
        builder.Append("CREATE OR ALTER VIEW [").Append(Escape(viewSchema)).Append("].[").Append(Escape(viewName)).Append("]\n");
        builder.Append("AS\n");
        builder.Append("SELECT");
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

        builder.Append("FROM [").Append(Escape(rawSchema)).Append("].[").Append(Escape(rawTable)).Append(']');
        if (!string.IsNullOrWhiteSpace(whereClause))
        {
            builder.Append("\nWHERE ").Append(whereClause.Trim());
        }

        builder.Append(';');
        return builder.ToString();
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
