using SqlFlow.Core.Abstractions;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// The SQL Server column-type reconciler for the file-ingestion (pre) schema-evolution path. Parses both
/// types and applies the same monotonic widening the table-to-table ingestion path uses
/// (<see cref="SqlTypeResolution"/>), so a target column only ever grows to fit the incoming data and an
/// incompatible cross-family change is left alone rather than forced. Reusing one resolution keeps both
/// evolution paths on identical widening rules.
/// </summary>
public sealed class SqlServerColumnTypeReconciler : IColumnTypeReconciler
{
    public string? WidenTo(string existingType, string desiredType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(existingType);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredType);

        var existing = SqlDataType.Parse(existingType);
        var desired = SqlDataType.Parse(desiredType);
        var resolution = SqlTypeResolution.Resolve(existing, desired);
        return resolution.Action == SchemaChangeAction.Alter ? resolution.Merged!.Render() : null;
    }
}
