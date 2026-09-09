namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Plans how to evolve a target table to the desired schema. For a missing target it creates everything. For
/// an existing target it adds new columns and widens existing ones via <see cref="SqlTypeResolution"/>
/// (monotonic: never narrows). An incompatible type change is a critical mismatch (and blocks the load) when
/// it lands on a key, hash, or identity column; otherwise it is non-blocking drift and the target is left
/// alone. Target-only columns are never dropped, and a nullable target column is never tightened to NOT NULL.
/// Pure.
/// </summary>
public static class SchemaEvolutionPlanner
{
    public static EvolutionPlan Plan(
        IReadOnlyList<SqlColumn> desired,
        IReadOnlyList<SqlColumn>? actual,
        IReadOnlySet<string> keyColumns)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(keyColumns);

        if (actual is null)
        {
            return new EvolutionPlan { CreateTable = true, CreateColumns = desired };
        }

        var actualByName = actual.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var add = new List<SqlColumn>();
        var alter = new List<ColumnAlter>();
        var critical = new List<CriticalMismatch>();
        var drift = new List<DriftFinding>();

        foreach (var column in desired)
        {
            if (!actualByName.TryGetValue(column.Name, out var existing))
            {
                add.Add(column);
                continue;
            }

            var resolution = SqlTypeResolution.Resolve(existing.DataType, column.DataType);
            switch (resolution.Action)
            {
                case SchemaChangeAction.Keep:
                    break;

                case SchemaChangeAction.Alter:
                    alter.Add(new ColumnAlter
                    {
                        Name = existing.Name,
                        FromType = existing.DataType,
                        ToType = resolution.Merged!,
                        IsNullable = existing.IsNullable, // never tighten an existing column's nullability
                        Footprint = ChangeFootprintClassifier.Classify(existing.DataType, resolution.Merged!),
                    });
                    break;

                case SchemaChangeAction.Incompatible:
                    if (IsCritical(column, keyColumns))
                    {
                        critical.Add(new CriticalMismatch { Column = column.Name, Reason = resolution.Reason! });
                    }
                    else
                    {
                        drift.Add(new DriftFinding
                        {
                            Kind = DriftKind.IncompatibleOrdinaryColumn,
                            Column = column.Name,
                            Detail = resolution.Reason,
                        });
                    }

                    break;

                default:
                    throw new InvalidOperationException($"Unhandled resolution action '{resolution.Action}'.");
            }

            if (!column.IsNullable && existing.IsNullable)
            {
                drift.Add(new DriftFinding
                {
                    Kind = DriftKind.NullabilityNotTightened,
                    Column = column.Name,
                    Detail = "desired NOT NULL but the target column is nullable; left nullable (never tightened on an existing table)",
                });
            }
        }

        var desiredNames = desired.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in actual)
        {
            if (!desiredNames.Contains(existing.Name))
            {
                drift.Add(new DriftFinding
                {
                    Kind = DriftKind.ExtraTargetColumn,
                    Column = existing.Name,
                    Detail = "present in the target, absent from the source; retained (never dropped)",
                });
            }
        }

        return new EvolutionPlan
        {
            ColumnsToAdd = add,
            ColumnsToAlter = alter,
            CriticalMismatches = critical,
            Drift = drift,
        };
    }

    private static bool IsCritical(SqlColumn column, IReadOnlySet<string> keyColumns)
        => column.Role is ColumnRole.HashKey or ColumnRole.Identity
           || column.IsPrimaryKey
           || keyColumns.Contains(column.Name);
}
