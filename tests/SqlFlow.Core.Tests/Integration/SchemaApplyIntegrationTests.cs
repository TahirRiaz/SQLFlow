using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the relational schema-evolution apply pipeline (plan -> generate -> ApplyDdlAsync) against the
/// real sink database, validating idempotent CREATE/ADD, metadata-only widen, an allowed table rewrite, and
/// a full end-to-end plan against a live target. Each test drops its own table first and at the end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaApplyIntegrationTests
{
    private static readonly SqlServerSchemaProvider Provider = new();
    private static readonly SchemaApplyOptions Options = new();
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static RelationalObject Target(string table) => new() { Database = "db", Schema = IntegrationDb.Schema, Name = table };

    private static SqlColumn Desired(string name, string type, bool nullable = true, bool identity = false, bool pk = false)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable, IsIdentity = identity, IsPrimaryKey = pk };

    private static async Task<IReadOnlyList<SqlColumn>?> ActualAsync(string connectionString, string table)
    {
        var schema = await Provider.GetTableSchemaAsync(connectionString, IntegrationDb.Schema, table);
        return schema?.Columns
            .Select(c => new SqlColumn { Name = c.Name, DataType = SqlDataType.Parse(c.SqlType), IsNullable = c.IsNullable })
            .ToList();
    }

    private static Task ApplyAsync(string connectionString, string table, EvolutionPlan plan, bool allowRewrite = false)
        => SqlServerSchemaProvider.ApplyDdlAsync(connectionString, EvolutionDdlGenerator.Generate(Target(table), plan, allowRewrite), Options);

    [SkippableFact]
    public async Task ApplyDdl_CreatesTable_AndIsIdempotent()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfApply_Create";
        await IntegrationDb.DropTableAsync(cs, table);

        var plan = new EvolutionPlan
        {
            CreateTable = true,
            CreateColumns = [Desired("Sk", "int", nullable: false, identity: true, pk: true), Desired("Name", "nvarchar(50)")],
        };

        await ApplyAsync(cs, table, plan);
        Assert.True(await IntegrationDb.TableExistsAsync(cs, table));

        // Re-applying the same CREATE is a no-op via the OBJECT_ID guard, no error.
        await ApplyAsync(cs, table, plan);
        Assert.True(await IntegrationDb.TableExistsAsync(cs, table));

        await IntegrationDb.DropTableAsync(cs, table);
    }

    [SkippableFact]
    public async Task ApplyDdl_AddsColumn_Idempotent()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfApply_Add";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL);");

        var plan = new EvolutionPlan { ColumnsToAdd = [Desired("Notes", "nvarchar(400)")] };

        await ApplyAsync(cs, table, plan);
        Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Notes"));

        // The COL_LENGTH guard makes a second apply a no-op.
        await ApplyAsync(cs, table, plan);
        Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Notes"));

        await IntegrationDb.DropTableAsync(cs, table);
    }

    [SkippableFact]
    public async Task ApplyDdl_WidensVariableText_MetadataOnly()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfApply_Widen";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Code] varchar(50) NULL);");

        var plan = new EvolutionPlan
        {
            ColumnsToAlter =
            [
                new ColumnAlter
                {
                    Name = "Code",
                    FromType = SqlDataType.Parse("varchar(50)"),
                    ToType = SqlDataType.Parse("varchar(100)"),
                    IsNullable = true,
                    Footprint = ChangeFootprint.MetadataOnly,
                },
            ],
        };

        await ApplyAsync(cs, table, plan);
        Assert.Equal("varchar(100)", await IntegrationDb.ColumnTypeAsync(cs, table, "Code"));

        await IntegrationDb.DropTableAsync(cs, table);
    }

    [SkippableFact]
    public async Task ApplyDdl_AppliesRewrite_WhenAllowed()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfApply_Rewrite";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Amount] int NULL);");

        var plan = new EvolutionPlan
        {
            ColumnsToAlter =
            [
                new ColumnAlter
                {
                    Name = "Amount",
                    FromType = SqlDataType.Parse("int"),
                    ToType = SqlDataType.Parse("bigint"),
                    IsNullable = true,
                    Footprint = ChangeFootprint.TableRewrite,
                },
            ],
        };

        await ApplyAsync(cs, table, plan, allowRewrite: true);
        Assert.Equal("bigint", await IntegrationDb.ColumnTypeAsync(cs, table, "Amount"));

        await IntegrationDb.DropTableAsync(cs, table);
    }

    [SkippableFact]
    public async Task PlanGenerateApply_EndToEnd_AgainstLiveTarget()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfApply_E2E";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL, [Name] varchar(50) NULL);");

        var actual = await ActualAsync(cs, table);
        Assert.NotNull(actual);

        var desired = new[]
        {
            Desired("Id", "int", nullable: false),
            Desired("Name", "varchar(100)"),  // widen
            Desired("Email", "nvarchar(200)"), // add
        };

        var plan = SchemaEvolutionPlanner.Plan(desired, actual, NoKeys);
        Assert.False(plan.IsBlocked);

        await ApplyAsync(cs, table, plan);

        Assert.Equal("varchar(100)", await IntegrationDb.ColumnTypeAsync(cs, table, "Name"));
        Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Email"));

        await IntegrationDb.DropTableAsync(cs, table);
    }
}
