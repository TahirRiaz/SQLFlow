using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>Pure schema-diff logic: compares the desired schema against the live target.</summary>
public static class SchemaDiffer
{
    public static SchemaDelta Diff(TableSchema desired, TableSchema? actual, SchemaEvolution evolve, IColumnTypeReconciler reconciler)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(reconciler);

        if (actual is null)
        {
            return new SchemaDelta { CreateTable = true, ColumnsToAdd = desired.Columns };
        }

        var existing = actual.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var missing = desired.Columns.Where(c => !existing.ContainsKey(c.Name)).ToList();

        return evolve switch
        {
            SchemaEvolution.Create => new SchemaDelta(),
            SchemaEvolution.Widen => new SchemaDelta
            {
                ColumnsToAdd = missing,
                ColumnsToAlter = Widenings(desired, existing, reconciler),
            },
            SchemaEvolution.Strict => missing.Count == 0
                ? new SchemaDelta()
                : throw new SchemaDriftException(desired, missing),
            _ => throw new ArgumentOutOfRangeException(nameof(evolve), evolve, "Unknown schema evolution policy."),
        };
    }

    /// <summary>
    /// The existing columns whose live type is too narrow for the desired one: each widened to the merged
    /// type the reconciler returns. Monotonic (the reconciler only ever grows a type, never narrows), so a
    /// column that already fits yields no change and an incompatible cross-family difference is left alone.
    /// The ALTER keeps the live column's nullability, so widening never tightens a NULL column to NOT NULL.
    /// </summary>
    private static List<ColumnDefinition> Widenings(
        TableSchema desired,
        IReadOnlyDictionary<string, ColumnDefinition> existing,
        IColumnTypeReconciler reconciler)
    {
        var alters = new List<ColumnDefinition>();
        foreach (var column in desired.Columns)
        {
            if (!existing.TryGetValue(column.Name, out var current))
            {
                continue;
            }

            var widened = reconciler.WidenTo(current.SqlType, column.SqlType);
            if (widened is not null)
            {
                alters.Add(new ColumnDefinition { Name = current.Name, SqlType = widened, IsNullable = current.IsNullable });
            }
        }

        return alters;
    }
}
