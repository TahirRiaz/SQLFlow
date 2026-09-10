using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the InitLoad backfill against the real sink: chunked source reads bound the staged rows to the
/// configured window (by month and by integer key), in-window and NULL-keyed rows land, out-of-window rows do
/// not, and staging is dropped on success. InitLoad changes only how staging is filled; the upsert is unchanged.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InitLoadIntegrationTests
{
    [SkippableFact]
    public async Task InitLoad_ByMonth_StagesWindowAndNullDate_ExcludesOutOfWindow()
    {
        const int flowId = 24;
        var cs = IntegrationDb.Require();
        const string src = "_SfInit_Src";
        const string trg = "_SfInit_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] date NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"""
            INSERT INTO [dbo].[{src}] ([Id],[OrderDate],[Amount]) VALUES
              (1,'2023-11-20',10),(2,'2023-12-15',20),(3,'2024-01-05',30),(4,'2024-02-09',40),
              (5,NULL,50),(6,'2020-01-01',60);
            """);

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                Incremental = new IncrementalPolicy { DateColumn = "OrderDate" },
                InitLoad = new InitLoadPolicy { Enabled = true, BatchBy = "M", BatchSize = 1, FromDate = new DateOnly(2023, 11, 1), ToDate = new DateOnly(2024, 2, 28) },
            };

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);

            // In-window rows (1-4) plus the NULL-date row (5); the out-of-window 2020 row (6) is excluded.
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 5"));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 6"));
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task InitLoad_ByKey_StagesBuckets_ExcludesAboveMax()
    {
        const int flowId = 25;
        var cs = IntegrationDb.Require();
        const string src = "_SfInitK_Src";
        const string trg = "_SfInitK_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] ([Id],[Amount]) VALUES (1,10),(2,20),(3,30),(150,40);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                InitLoad = new InitLoadPolicy { Enabled = true, BatchBy = "K", KeyColumn = "Id", KeyMaxValue = 100, BatchSize = 50 },
            };

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);

            // Ids 1-3 are within [0,100]; Id 150 is above KeyMaxValue and is excluded.
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 150"));
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId, string sourceColumns)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ({sourceColumns});");
    }

    private static async Task Cleanup(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
