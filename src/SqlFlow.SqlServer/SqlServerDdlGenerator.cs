using System.Text;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer;

/// <summary>Generates SQL Server DDL for a schema delta (CREATE TABLE / ALTER TABLE ADD).</summary>
public sealed class SqlServerDdlGenerator : IDdlGenerator
{
    public IReadOnlyList<string> Generate(TargetSpec target, SchemaDelta delta)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(delta);

        if (!delta.HasChanges)
        {
            return [];
        }

        if (delta.CreateTable)
        {
            var builder = new StringBuilder();
            builder.Append("CREATE TABLE ").Append(target.QualifiedName).AppendLine(" (");
            for (var i = 0; i < delta.ColumnsToAdd.Count; i++)
            {
                builder.Append("    ").Append(Column(delta.ColumnsToAdd[i], forNewTable: true));
                builder.AppendLine(i < delta.ColumnsToAdd.Count - 1 ? "," : string.Empty);
            }

            builder.Append(");");

            // The target schema (pre, ods, ...) may not exist yet on a first-ever run; ensure it before the
            // CREATE TABLE so the create never fails with "schema does not exist". The guard makes it a no-op
            // when the schema is already present (dbo, or a schema created by a prior run). Each statement runs
            // as its own batch, which CREATE SCHEMA requires; the EXEC wrapper satisfies that.
            return [EnsureSchemaStatement(target.Schema), builder.ToString()];
        }

        // Columns added to an existing (potentially populated) table must be NULLable: there is no
        // default value to backfill existing rows with. Widenings then grow existing columns to fit the
        // incoming data (varchar(255) -> varchar(4000), int -> bigint, ...) so the bulk load never overflows
        // a column that a narrower earlier run created; the ALTER carries the live column's nullability, so a
        // widening never tightens a NULL column to NOT NULL.
        var statements = new List<string>(delta.ColumnsToAdd.Count + delta.ColumnsToAlter.Count);
        foreach (var add in delta.ColumnsToAdd)
        {
            statements.Add($"ALTER TABLE {target.QualifiedName} ADD {Column(add, forNewTable: false)};");
        }

        foreach (var alter in delta.ColumnsToAlter)
        {
            var nullability = alter.IsNullable ? "NULL" : "NOT NULL";
            statements.Add($"ALTER TABLE {target.QualifiedName} ALTER COLUMN [{Escape(alter.Name)}] {alter.SqlType} {nullability};");
        }

        return statements;
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    /// <summary>An idempotent CREATE SCHEMA guarded by SCHEMA_ID, matching the pattern the ingestion runner
    /// and control-plane provisioning already use. CREATE SCHEMA must be the first (only) statement in its
    /// batch, so it is wrapped in EXEC to run inside the shared DDL transaction.</summary>
    private static string EnsureSchemaStatement(string schema)
    {
        var literal = schema.Replace("'", "''", StringComparison.Ordinal);
        var escaped = schema.Replace("]", "]]", StringComparison.Ordinal);
        return $"IF SCHEMA_ID(N'{literal}') IS NULL EXEC(N'CREATE SCHEMA [{escaped}]');";
    }

    private static string Column(ColumnDefinition column, bool forNewTable)
    {
        var nullability = forNewTable
            ? (column.IsNullable ? "NULL" : "NOT NULL")
            : "NULL";

        return $"[{column.Name}] {column.SqlType} {nullability}";
    }
}
