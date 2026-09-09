using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>
/// Merges the three sources of column typing into one ordered projection for the transformation view: the
/// authored <see cref="ColumnTransform"/> entries (YAML, the source of truth), the inferred columns from
/// profiling (when inference is on), and a raw pass-through for every remaining column. Precedence is fixed: an
/// authored transform wins over inference for the same column, inference fills the columns no author named, and
/// a column neither authored nor inferred passes through untyped. The result feeds
/// <see cref="TransformSelectBuilder"/>, so the view and the typed target it drives are built from one resolved
/// set. Pure and deterministic - no database, no clock - so the merge is fully unit-testable.
/// </summary>
public static class ColumnTransformResolver
{
    /// <summary>
    /// Resolves the ordered view projection over <paramref name="rawColumns"/> (the raw landing table's columns,
    /// in source order). <paramref name="inferred"/> is the profiling result keyed by raw column name (empty when
    /// inference did not run). Columns marked <see cref="ColumnTransform.ExcludeFromView"/> are dropped from the
    /// projection; <see cref="ColumnTransform.Virtual"/> columns (no raw counterpart) are appended after the raw
    /// columns. Final ordering is by <see cref="ColumnTransform.SortOrder"/> when set, else natural position.
    /// </summary>
    public static IReadOnlyList<InferredColumn> Resolve(
        IReadOnlyList<string> rawColumns,
        TypeInferencePolicy policy,
        IReadOnlyList<InferredColumn>? inferred = null)
    {
        ArgumentNullException.ThrowIfNull(rawColumns);
        ArgumentNullException.ThrowIfNull(policy);

        var declaredByName = new Dictionary<string, ColumnTransform>(StringComparer.OrdinalIgnoreCase);
        foreach (var transform in policy.Columns)
        {
            declaredByName[transform.Name] = transform;
        }

        var inferredByName = (inferred ?? [])
            .GroupBy(c => c.ColumnName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var ordered = new List<(InferredColumn Column, int? SortOrder, int Natural)>();
        var natural = 0;
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Raw columns first, in source order: an authored transform for the column, else the inferred column,
        // else an untyped pass-through. A column the author dropped (excludeFromView) is recorded as emitted so
        // inference/pass-through do not re-add it, but it is not projected.
        foreach (var raw in rawColumns)
        {
            emitted.Add(raw);
            if (declaredByName.TryGetValue(raw, out var declared))
            {
                if (declared.ExcludeFromView)
                {
                    continue;
                }

                ordered.Add((FromDeclared(declared), declared.SortOrder, natural++));
            }
            else if (inferredByName.TryGetValue(raw, out var inferredColumn))
            {
                ordered.Add((inferredColumn, null, natural++));
            }
            else
            {
                ordered.Add((PassThrough(raw), null, natural++));
            }
        }

        // Virtual and any authored transform whose name is not a raw column (a computed/derived output): append
        // after the raw columns, in declaration order, honouring excludeFromView.
        foreach (var transform in policy.Columns)
        {
            if (emitted.Contains(transform.Name))
            {
                continue;
            }

            emitted.Add(transform.Name);
            if (transform.ExcludeFromView)
            {
                continue;
            }

            ordered.Add((FromDeclared(transform), transform.SortOrder, natural++));
        }

        return ordered
            .OrderBy(e => e.SortOrder ?? e.Natural)
            .ThenBy(e => e.Natural)
            .Select(e => e.Column)
            .ToList();
    }

    /// <summary>Builds the view column for an authored transform: the substituted expression (or a CAST when only
    /// a type is given, or a verbatim virtual expression), aliased to the output name.</summary>
    private static InferredColumn FromDeclared(ColumnTransform transform)
    {
        var outputName = string.IsNullOrWhiteSpace(transform.Alias) ? transform.Name : transform.Alias!;
        var columnReference = $"[{Escape(transform.Name)}]";

        string expression;
        if (transform.Virtual)
        {
            // A virtual column has no source column, so its expression is used verbatim (validation already
            // rejected @ColName here).
            expression = transform.Expression!;
        }
        else if (transform.Expression is not null)
        {
            expression = ColumnTransformExpression.Substitute(transform.Expression, columnReference);
        }
        else
        {
            // Type-only transform: a straight cast of the raw column to the declared type.
            expression = $"CAST({columnReference} AS {transform.Type})";
        }

        return new InferredColumn
        {
            ColumnName = outputName,
            DataType = transform.Type ?? string.Empty,
            SelectExpression = expression,
            Converted = transform.Expression is not null || transform.Type is not null,
        };
    }

    /// <summary>An untyped pass-through: the raw column selected as-is (no cast), keeping its name.</summary>
    private static InferredColumn PassThrough(string column) => new()
    {
        ColumnName = column,
        DataType = string.Empty,
        SelectExpression = $"[{Escape(column)}]",
        Converted = false,
    };

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
