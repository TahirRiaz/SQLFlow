using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Catalog;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the full relational schema-sync loop (introspect -> plan -> generate -> apply) against the real
/// sink database via <see cref="SchemaSyncService"/>. Each test drops its own table first and at the end.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaSyncIntegrationTests
{
    private static readonly IReadOnlySet<string> NoKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static SchemaSyncService Service() => new(new SqlServerCatalogReader());

    private static SqlColumn Col(string name, string type, bool nullable = true)
        => new() { Name = name, DataType = SqlDataType.Parse(type), IsNullable = nullable };

    [SkippableFact]
    public async Task Evolve_WidensAndAdds_AgainstLiveTarget_Idempotently()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfSync_E2E";
        await IntegrationDb.DropTableAsync(cs, table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] ([Id] int NOT NULL, [Name] varchar(50) NULL);");

        try
        {
            var service = Service();
            var target = new RelationalObject { Database = "db", Schema = "dbo", Name = table };
            var desired = new[]
            {
                Col("Id", "int", nullable: false),
                Col("Name", "varchar(100)"), // widen
                Col("Email", "nvarchar(200)"), // add
            };

            var outcome = await service.EvolveAsync(cs, target, desired, NoKeys, allowTableRewrite: false, new SchemaApplyOptions());

            Assert.True(outcome.Plan.HasChanges);
            Assert.NotEmpty(outcome.AppliedStatements);
            Assert.Equal("varchar(100)", await IntegrationDb.ColumnTypeAsync(cs, table, "Name"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, table, "Email"));

            // A second evolve sees the live (already-evolved) target and plans no changes.
            var outcome2 = await service.EvolveAsync(cs, target, desired, NoKeys, allowTableRewrite: false, new SchemaApplyOptions());
            Assert.False(outcome2.Plan.HasChanges);
            Assert.Empty(outcome2.AppliedStatements);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Evolve_CreatesTable_WhenTargetMissing()
    {
        var cs = IntegrationDb.Require();
        const string table = "_SfSync_Create";
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var service = Service();
            var target = new RelationalObject { Database = "db", Schema = "dbo", Name = table };
            var desired = new[] { Col("Id", "int", nullable: false), Col("Name", "nvarchar(50)") };

            var outcome = await service.EvolveAsync(cs, target, desired, NoKeys, allowTableRewrite: false, new SchemaApplyOptions());

            Assert.True(outcome.Plan.CreateTable);
            Assert.Contains(outcome.AppliedStatements, s => s.IsCreateTable);
            Assert.True(await IntegrationDb.TableExistsAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }
}
