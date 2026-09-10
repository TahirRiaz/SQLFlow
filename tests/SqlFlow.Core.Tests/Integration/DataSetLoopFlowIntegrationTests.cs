using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end coverage of the dataset-partitioned load through the full <see cref="IngestionFlowRunner"/>: a
/// relational source whose rows carry a dataset column is staged and applied one dataset at a time, and a key
/// that recurs across datasets ends at the last dataset carrying it. This exercises the whole wiring the
/// generator-level tests do not: the YAML-shaped load policy, the staging bulk copy, and the
/// result-set count reader that attributes the loop's Inserts/Updates back onto the run result. Skips when the
/// sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataSetLoopFlowIntegrationTests
{
    private const int FlowId = 91;

    private static IngestionFlow BuildFlow(string src, string trg) => new()
    {
        FlowId = FlowId,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy
        {
            KeyColumns = ["Id"],
            DataSetColumn = "F",
        },
        SystemColumns = new SystemColumnsPolicy
        {
            InsertedDate = true,
            UpdatedDate = true,
            RowStatus = true,
        },
    };

    [SkippableFact]
    public async Task DataSetLoad_AppliesDatasetsInOrder_LastWins()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfDsFlow_Src";
        const string trg = "_SfDsFlow_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([F] nvarchar(20) NULL, [Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");

        // Id 1 is carried by datasets f1 and f2; applied in order, it must end at f2. Id 2 is only in f1, Id 3
        // only in f3.
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([F],[Id],[Name],[Amount]) VALUES " +
            "('f1', 1, 'a1', 10), ('f2', 1, 'a2', 20), ('f1', 2, 'b1', 30), ('f3', 3, 'c3', 40);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var result = await runner.RunAsync(BuildFlow(src, trg));

            Assert.True(result.Success, result.Error);
            Assert.Equal(3, result.RowsInserted);
            Assert.Equal(1, result.RowsUpdated);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            Assert.Equal("a2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("f2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [F] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("b1", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal("c3", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 3"));

            // Re-running the same source inserts nothing (every key already exists) and leaves the final state
            // stable. Note updates do NOT go to zero here: Id 1 is carried by two datasets with different values
            // in one load, so f1 rewrites it then f2 rewrites it back on every run. That ping-pong is inherent to
            // multi-dataset keys; the strict no-op only holds when each key belongs to a single dataset (proven in
            // DataSetLoopUpsertIntegrationTests.ReRun_WithUnchangedStaging_IsNoOp). What must stay invariant is the
            // committed state.
            var rerun = await runner.RunAsync(BuildFlow(src, trg));
            Assert.True(rerun.Success, rerun.Error);
            Assert.Equal(0, rerun.RowsInserted);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal("a2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("f2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [F] FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
        }
    }
}
