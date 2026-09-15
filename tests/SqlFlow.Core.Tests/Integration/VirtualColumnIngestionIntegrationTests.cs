using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Virtual columns (legacy flw.IngestionVirtual) against the real sink: every declaration is read from the source
/// as its expression, so an added column and a replaced column both land computed values in the target, follow
/// the row on update, and can key the upsert. Gated on a reachable sink, like the sibling ingestion suites.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VirtualColumnIngestionIntegrationTests
{
    [SkippableFact]
    public async Task AddedAndReplacedVirtualColumns_LandComputedValues_AndFollowUpdates()
    {
        const int flowId = 61;
        var cs = IntegrationDb.Require();
        const string src = "_SfVc1_Src";
        const string trg = "_SfVc1_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [VehicleIdentity] bigint NULL, [Code] varchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1, 3610304181, 'abc'), (2, -1, 'def');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                VirtualColumns =
                [
                    // Added: not a source column, so it takes the declared type (the APC VehicleNo_DW rule).
                    new VirtualColumn
                    {
                        Name = "[VehicleNo]",
                        DataType = "int",
                        SelectExpression = "CAST(CASE WHEN [VehicleIdentity] = -1 THEN NULL ELSE SUBSTRING(CAST([VehicleIdentity] AS varchar(255)), 7, 4) END AS int)",
                    },
                    // Replaced: the source column's value is swapped for the expression in the same column.
                    new VirtualColumn { Name = "Code", SelectExpression = "UPPER([Code])" },
                ],
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(4181, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [VehicleNo] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Null(await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [VehicleNo] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal("ABC", await IntegrationDb.ScalarAsync<string>(cs, $"SELECT [Code] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("int", await IntegrationDb.ScalarAsync<string>(cs,
                $"SELECT TYPE_NAME(system_type_id) FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.{trg}') AND name = N'VehicleNo'"));

            // The source changes; the computed column is re-read with the row and the upsert carries it over.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [VehicleIdentity] = 3610309999 WHERE [Id] = 1;");
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(9999, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [VehicleNo] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}]"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task AVirtualColumn_KeysTheUpsert()
    {
        const int flowId = 62;
        var cs = IntegrationDb.Require();
        const string src = "_SfVc2_Src";
        const string trg = "_SfVc2_Trg";
        await Reset(cs, src, trg, flowId, "[Region] varchar(10) NOT NULL, [No] int NOT NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES ('north', 1, 10), ('south', 1, 20);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["BusinessKey"] },
                VirtualColumns =
                [
                    new VirtualColumn { Name = "BusinessKey", DataTypeExpression = "varchar(30)", SelectExpression = "CONCAT([Region], '-', [No])" },
                ],
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}]"));

            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Amount] = 11 WHERE [Region] = 'north';");
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}]"));
            Assert.Equal(11, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [BusinessKey] = 'north-1'"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await Cleanup(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task Cleanup(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
