using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises index synchronization against the real sink: the canonical create-time indexes (unique key,
/// date, UpdatedDate_DW), the declared trgDesiredIndex, the clustered columnstore option, and the parse-error
/// degrade-to-warning policy. Indexes are created only on the run that creates the target, so a re-run does
/// not duplicate or fail.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IndexSyncIntegrationTests
{
    [SkippableFact]
    public async Task CanonicalAndDeclaredIndexes_CreatedOnce()
    {
        const int flowId = 16;
        var cs = IntegrationDb.Require();
        const string src = "_SfIdx_Src";
        const string trg = "_SfIdx_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Region] nvarchar(10) NULL, [OrderDate] datetime2(3) NULL, [Amount] int NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'EU','2026-01-10',100),(2,'US','2026-01-20',200),(3,'EU','2026-01-15',300);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget
                {
                    Server = "sink",
                    Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg },
                    DesiredIndexes = $"CREATE INDEX IX_Region ON [dbo].[{trg}] (Region) INCLUDE (Amount);",
                },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
                Incremental = new IncrementalPolicy { DateColumn = "OrderDate" },
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);

            Assert.True(await IntegrationDb.IndexExistsAsync(cs, trg, "NCI_KeyColumn"));
            Assert.Equal(1, await ScalarIntAsync(cs, $"SELECT CONVERT(int, is_unique) FROM sys.indexes WHERE object_id = OBJECT_ID('[dbo].[{trg}]') AND name = 'NCI_KeyColumn'"));
            Assert.Equal("NONCLUSTERED", await ScalarStringAsync(cs, $"SELECT type_desc FROM sys.indexes WHERE object_id = OBJECT_ID('[dbo].[{trg}]') AND name = 'NCI_KeyColumn'"));
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, trg, "NCI_DateColumn"));
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, trg, "NCI_UpdatedDate_DW"));

            Assert.True(await IntegrationDb.IndexExistsAsync(cs, trg, "IX_Region"));
            Assert.Equal(1, await ScalarIntAsync(cs,
                $"SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id WHERE i.object_id = OBJECT_ID('[dbo].[{trg}]') AND i.name = 'IX_Region' AND ic.is_included_column = 1 AND c.name = 'Amount'"));
            Assert.Contains(first.IndexActions, a => a.IndexName == "IX_Region" && a.Kind == IndexActionKind.Created);

            // Re-run after a source change: the target already exists, so neither canonical nor declared
            // indexes are re-applied. They remain exactly once and the run does not fail on a duplicate create.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'EU','2026-02-01',400);");
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Empty(second.IndexActions);
            Assert.Equal(1, await ScalarIntAsync(cs, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('[dbo].[{trg}]') AND name = 'NCI_KeyColumn'"));
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task ColumnStore_Flow_CreatesClusteredColumnstore()
    {
        const int flowId = 17;
        var cs = IntegrationDb.Require();
        const string src = "_SfCci_Src";
        const string trg = "_SfCci_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg }, ColumnStoreIndex = true },
                Load = new IngestionLoadPolicy(),
            };

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(1, await ScalarIntAsync(cs, $"SELECT COUNT(*) FROM sys.indexes WHERE object_id = OBJECT_ID('[dbo].[{trg}]') AND type_desc = 'CLUSTERED COLUMNSTORE'"));
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task MalformedDesiredIndex_DoesNotRollBackTheLoad()
    {
        const int flowId = 18;
        var cs = IntegrationDb.Require();
        const string src = "_SfIdxBad_Src";
        const string trg = "_SfIdxBad_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget
                {
                    Server = "sink",
                    Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg },
                    DesiredIndexes = "THIS IS NOT A VALID INDEX SCRIPT",
                },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            };

            var result = await runner.RunAsync(flow);
            Assert.True(result.Success, result.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Contains(result.IndexActions, a => a.Kind == IndexActionKind.Failed);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    private static Task<int?> ScalarIntAsync(string cs, string sql) => IntegrationDb.ScalarAsync<int?>(cs, sql);

    private static Task<string?> ScalarStringAsync(string cs, string sql) => IntegrationDb.ScalarAsync<string?>(cs, sql);

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
