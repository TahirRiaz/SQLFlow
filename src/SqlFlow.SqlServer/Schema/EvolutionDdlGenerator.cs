using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>
/// Turns an <see cref="EvolutionPlan"/> into a classified <see cref="DdlBatch"/> for SQL Server: an
/// idempotent CREATE (guarded by OBJECT_ID), idempotent additive ADDs (guarded by COL_LENGTH, always
/// nullable on an existing table), and ALTER COLUMN widenings whose <see cref="DdlCost"/> comes from the
/// planner's footprint. A blocked plan throws, and a table-rewrite ALTER is refused unless the flow opted in,
/// so an expensive rewrite never reaches execution silently. Pure (text generation only); the lock and
/// transaction handling lives in the apply method. Dependent-index drop/recreate around a rewrite is added
/// with the live introspection slice.
/// </summary>
public static class EvolutionDdlGenerator
{
    public static DdlBatch Generate(RelationalObject target, EvolutionPlan plan, bool allowTableRewrite)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(plan);

        if (plan.IsBlocked)
        {
            throw new SqlFlowException(
                $"Schema evolution on '{target.QualifiedName}' is blocked by {plan.CriticalMismatches.Count} critical type mismatch(es): " +
                string.Join("; ", plan.CriticalMismatches.Select(m => $"[{m.Column}] {m.Reason}")));
        }

        if (plan.HasRewrite && RewritePolicy.Decide(allowTableRewrite) == RewriteHandling.RequireOptIn)
        {
            throw new SchemaRewriteNotPermittedException(target.QualifiedName, plan.RewriteColumns.Select(c => c.Name).ToList());
        }

        var qualified = Qualify(target);
        var literal = Literal(qualified);
        var statements = new List<DdlStatement>();

        if (plan.CreateTable)
        {
            // The target schema (pre, ods, ...) may not exist on a first-ever run; ensure it before the CREATE
            // TABLE so the create never fails with "schema does not exist". The SCHEMA_ID guard makes it a no-op
            // once the schema is present. It runs in the same metadata-only batch as the create.
            statements.Add(new DdlStatement
            {
                Text = EnsureSchemaStatement(target.Schema),
                Cost = DdlCost.MetadataOnly,
            });

            statements.Add(new DdlStatement
            {
                Text = CreateTableStatement(qualified, literal, target.Name, plan.CreateColumns),
                Cost = DdlCost.MetadataOnly,
                IsCreateTable = true,
            });

            return new DdlBatch { Schema = target.Schema, Table = target.Name, Statements = statements };
        }

        foreach (var add in plan.ColumnsToAdd)
        {
            statements.Add(new DdlStatement
            {
                Text = $"IF COL_LENGTH(N'{literal}', N'{Literal(add.Name)}') IS NULL " +
                       $"ALTER TABLE {qualified} ADD [{Escape(add.Name)}] {add.DataType.Render()} NULL;",
                Cost = DdlCost.MetadataOnly,
            });
        }

        foreach (var alter in plan.ColumnsToAlter)
        {
            statements.Add(new DdlStatement
            {
                Text = $"ALTER TABLE {qualified} ALTER COLUMN [{Escape(alter.Name)}] {alter.ToType.Render()} " +
                       $"{(alter.IsNullable ? "NULL" : "NOT NULL")};",
                Cost = alter.Footprint == ChangeFootprint.TableRewrite ? DdlCost.Rewrite : DdlCost.MetadataOnly,
            });
        }

        return new DdlBatch { Schema = target.Schema, Table = target.Name, Statements = statements };
    }

    private static string CreateTableStatement(string qualified, string literal, string table, IReadOnlyList<SqlColumn> columns)
    {
        var lines = columns.Select(ColumnDefinition).ToList();

        var primaryKey = columns.Where(c => c.IsPrimaryKey).ToList();
        if (primaryKey.Count > 0)
        {
            var keyList = string.Join(", ", primaryKey.Select(c => $"[{Escape(c.Name)}]"));
            lines.Add($"CONSTRAINT [PK_{Escape(table)}] PRIMARY KEY CLUSTERED ({keyList})");
        }

        var body = string.Join(",\n", lines.Select(l => "        " + l));

        var sb = new StringBuilder();
        sb.Append("IF OBJECT_ID(N'").Append(literal).Append("', N'U') IS NULL\n");
        sb.Append("BEGIN\n");
        sb.Append("    CREATE TABLE ").Append(qualified).Append(" (\n");
        sb.Append(body).Append('\n');
        sb.Append("    );\n");
        sb.Append("END;");
        return sb.ToString();
    }

    private static string ColumnDefinition(SqlColumn column)
    {
        var identity = column.IsIdentity ? " IDENTITY(1, 1)" : string.Empty;
        var nullability = column.IsNullable ? "NULL" : "NOT NULL";
        return $"[{Escape(column.Name)}] {column.DataType.Render()}{identity} {nullability}";
    }

    /// <summary>An idempotent CREATE SCHEMA guarded by SCHEMA_ID. CREATE SCHEMA must begin its own batch, so
    /// it is wrapped in EXEC to run inside the applier's metadata-only transaction alongside the create.</summary>
    private static string EnsureSchemaStatement(string schema)
        => $"IF SCHEMA_ID(N'{Literal(schema)}') IS NULL EXEC(N'CREATE SCHEMA [{Escape(schema)}]');";

    private static string Qualify(RelationalObject target) => $"[{Escape(target.Schema)}].[{Escape(target.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
