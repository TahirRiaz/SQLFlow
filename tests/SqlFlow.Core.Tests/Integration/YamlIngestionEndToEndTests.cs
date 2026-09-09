using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The YAML-mode promise, end to end: a flow authored as YAML text runs against the real sink with NO control
/// database anywhere (no flw.* tables read or written). Covers the keyed upsert across two runs, dynamic schema
/// evolution when the source grows a column, inline assertions, and a local surrogate key, all through the same
/// without-database composition the CLI uses.
/// </summary>
[Trait("Category", "Integration")]
public sealed class YamlIngestionEndToEndTests
{
    private static readonly YamlIngestionFlowLoader Loader = new();

    private static async Task<IngestionRunResult> RunAsync(string yaml)
    {
        var document = Loader.Parse(yaml);
        var runner = WithoutDatabaseIngestion.BuildRunner(document.Connections, document.AssertionDefinitions);
        return await runner.RunAsync(document.Flow, new IngestionRunOptions { ExecMode = "test" });
    }

    private static string FlowYaml(string name, string src, string trg, string extraSections = "") => $$"""
        flowType: ing
        name: {{name}}
        connections:
          sink: ${env:SQLFlowSinkConStr}
        source:
          server: sink
          object: db.dbo.{{src}}
        target:
          server: sink
          object: db.dbo.{{trg}}
        load:
          keyColumns: [Id]
        {{extraSections}}
        """;

    [SkippableFact]
    public async Task YamlFlow_Upserts_AcrossTwoRuns()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYml_Src";
        const string trg = "_SfYml_Trg";
        await Reset(cs, src, trg, "_SfYml");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

            var first = await RunAsync(FlowYaml("_SfYml", src, trg));
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            // Second run: one changed row, one new row; the keyed upsert must not duplicate.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Val] = 'a2' WHERE [Id] = 1; INSERT INTO [dbo].[{src}] VALUES (3,'c');");
            var second = await RunAsync(FlowYaml("_SfYml", src, trg));

            Assert.True(second.Success, second.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(1, second.RowsInserted);
            Assert.Equal(1, second.RowsUpdated);
            Assert.Equal("a2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await Reset(cs, src, trg, "_SfYml");
        }
    }

    [SkippableFact]
    public async Task YamlFlow_EvolvesTarget_WhenSourceGrowsAColumn()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYmlEv_Src";
        const string trg = "_SfYmlEv_Trg";
        await Reset(cs, src, trg, "_SfYmlEv");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");
            var first = await RunAsync(FlowYaml("_SfYmlEv", src, trg));
            Assert.True(first.Success, first.Error);

            // The flagship: the source grows a column; the next run widens the target automatically.
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ADD [Region] nvarchar(10) NULL;");
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Region] = 'EU';");
            var second = await RunAsync(FlowYaml("_SfYmlEv", src, trg));

            Assert.True(second.Success, second.Error);
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID('dbo.{trg}') AND name = 'Region'"));
            Assert.Equal("EU", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Region] FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await Reset(cs, src, trg, "_SfYmlEv");
        }
    }

    [SkippableFact]
    public async Task YamlFlow_RunsInlineAssertions_AndLocalSurrogateKey()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfYmlAsk_Src";
        const string trg = "_SfYmlAsk_Trg";
        const string dim = "_SfYmlAsk_Dim";
        await Reset(cs, src, trg, "_SfYmlAsk");
        await IntegrationDb.DropTableAsync(cs, dim);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [CustomerID] int NOT NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,100),(2,200),(3,100);");

            // The surrogate table's Database part is used literally (cross-database DDL), so it must be real.
            var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");

            var result = await RunAsync(FlowYaml("_SfYmlAsk", src, trg, $"""
                assertions:
                  - name: RowCount
                    expression: SELECT COUNT(*) FROM @TableName
                surrogateKeys:
                  - table: {dbName}.dbo.{dim}
                    column: CustomerKey
                    keyColumns: [CustomerID]
                """));

            Assert.True(result.Success, result.Error);

            // The inline assertion ran through the same AssertionRunner as full mode.
            var assertion = Assert.Single(result.Assertions);
            Assert.Equal("RowCount", assertion.Name);
            Assert.True(assertion.Evaluated, assertion.Error);
            Assert.Equal("3", assertion.Result);

            // The local surrogate key generated one key per distinct business key and stamped the target.
            var key = Assert.Single(result.SurrogateKeys);
            Assert.True(key.Executed, key.Error);
            Assert.Equal(2, key.KeysGenerated);
            Assert.Equal(3, key.RowsStamped);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, dim));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustomerKey] IS NULL"));
        }
        finally
        {
            await Reset(cs, src, trg, "_SfYmlAsk");
            await IntegrationDb.DropTableAsync(cs, dim);
        }
    }

    private static async Task Reset(string cs, string src, string trg, string flowName)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);

        // Drop any staging leftovers for this flow's deterministic id (canonical work tables in raw).
        var flowId = Loader.Parse(FlowYaml(flowName, src, trg)).Flow.FlowId;
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
