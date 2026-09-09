using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.Core.Engine;

/// <summary>Builds the desired target schema from inferred source columns plus authored overrides.</summary>
public static class DesiredSchemaBuilder
{
    public static TableSchema Build(
        TargetSpec target,
        IReadOnlyList<SourceColumn> sourceColumns,
        SchemaPolicy policy,
        ISqlTypeMapper mapper)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sourceColumns);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(mapper);

        var columns = new List<ColumnDefinition>(sourceColumns.Count);
        foreach (var column in sourceColumns)
        {
            policy.Overrides.TryGetValue(column.Name, out var columnOverride);
            columns.Add(mapper.Map(column, columnOverride, policy.DefaultColumnType));
        }

        return new TableSchema { Schema = target.Schema, Table = target.Table, Columns = columns };
    }
}
