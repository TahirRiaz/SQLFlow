using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Drives a full relational ingestion run against the real sink: introspect source, rebuild the flow's
/// canonical staging table, bulk-copy source into staging, evolve and create the target, then upsert. A second
/// run after the source changes proves the keyed update/insert and that staging is cleaned up on success.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionFlowRunnerIntegrationTests
{
    private const int FlowId = 7;

    [SkippableFact]
    public async Task RelationalRun_CreatesTarget_StagesAndUpserts_ThenIncrementsOnReRun()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfIng_Src";
        const string trg = "_SfIng_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 100), (2, 'Bob', 200);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            };

            // First run creates the target and loads both rows.
            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsStaged);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal("Ann", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "InsertedDate_DW"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "UpdatedDate_DW"));
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [InsertedDate_DW] IS NOT NULL"));
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, FlowId));

            // Change the source: modify a row and add a new one.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Amount] = 999 WHERE [Id] = 1;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] ([Id],[Name],[Amount]) VALUES (3, 'Cy', 300);");

            // Second run upserts: row 1 updated, row 3 inserted, row 2 untouched.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(999, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("Cy", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 3"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1 AND [UpdatedDate_DW] IS NOT NULL"));
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, FlowId));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
        }
    }
}
