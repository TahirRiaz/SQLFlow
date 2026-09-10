using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Proves the dataset-partitioned upsert (legacy DataSetColumn loop) against the real sink: the behavior a flat
/// set-based upsert cannot express. Datasets are applied in ascending order, so a business key that recurs
/// across datasets ends at the value from the LAST dataset that carries it; duplicate keys within one dataset
/// collapse to a single row; a re-run detects no change; and the batched (lock-escalation) variant reaches the
/// identical final state as the unbatched one. The target key is a PRIMARY KEY, so any accidental double-insert
/// (the classic cross-dataset trap) fails loudly rather than silently duplicating. These tests skip when the
/// sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DataSetLoopUpsertIntegrationTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions LoopOptions(bool batch)
        => new()
        {
            DataColumns = ["F", "Id", "Name", "Amount"],
            KeyColumns = ["Id"],
            DataSetColumn = "F",
            BatchToAvoidLockEscalation = batch,
            BatchRowCount = 1,
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
            RowStatusColumn = "RowStatus_DW",
        };

    private static async Task CreateTargetAsync(string cs, string trg)
        => await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([F] nvarchar(20) NULL, [Id] int NOT NULL PRIMARY KEY, [Name] nvarchar(50) NULL, " +
            "[Amount] int NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL, [RowStatus_DW] char(1) NULL);");

    private static async Task CreateStagingAsync(string cs, string stg)
        => await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{stg}] ([F] nvarchar(20) NULL, [Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");

    private static async Task<(long Inserts, long Updates)> RunLoopAsync(string cs, bool batch, string trg, string stg)
    {
        var statement = Assert.Single(UpsertGenerator.GenerateStatements(Obj(trg), Obj(stg), LoopOptions(batch)));
        Assert.True(statement.CountFromResultSet);

        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(statement.Sql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync();
        long inserts = 0, updates = 0;
        if (await reader.ReadAsync())
        {
            inserts = Convert.ToInt64(reader["Inserts"], CultureInfo.InvariantCulture);
            updates = Convert.ToInt64(reader["Updates"], CultureInfo.InvariantCulture);
        }

        return (inserts, updates);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DataSets_AppliedInOrder_LastDatasetWins(bool batch)
    {
        var cs = IntegrationDb.Require();
        var trg = $"_SfDsLoop_Trg_{(batch ? "B" : "N")}";
        var stg = $"_SfDsLoop_Stg_{(batch ? "B" : "N")}";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await CreateTargetAsync(cs, trg);
            await CreateStagingAsync(cs, stg);

            // A pre-existing target row (exercises the update branch) and three datasets. Id 1 is carried by
            // both f1 and f2, so it must end at f2's values. Id 2 is only in f1; Id 3 only in f3.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([F],[Id],[Name],[Amount]) VALUES ('seed', 1, 'old', 1);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([F],[Id],[Name],[Amount]) VALUES " +
                "('f1', 1, 'a1', 10), ('f2', 1, 'a2', 20), ('f1', 2, 'b1', 30), ('f3', 3, 'c3', 40);");

            var (inserts, updates) = await RunLoopAsync(cs, batch, trg, stg);

            // Id 1 updated once per dataset that carries it (f1 then f2); Id 2 and Id 3 inserted.
            Assert.Equal(2, inserts);
            Assert.Equal(2, updates);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            Assert.Equal("a2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal(20, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("f2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [F] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("b1", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal("c3", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 3"));

            // System columns stamped on the right branch.
            Assert.Equal("U", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [RowStatus_DW] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("I", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [RowStatus_DW] FROM [dbo].[{trg}] WHERE [Id] = 3"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    [SkippableFact]
    public async Task DuplicateKeyWithinOneDataset_CollapsesToOneRow()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfDsLoop_DupTrg";
        const string stg = "_SfDsLoop_DupStg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await CreateTargetAsync(cs, trg);
            await CreateStagingAsync(cs, stg);

            // Same (dataset, key) twice: clean dedup keeps exactly one row (a PRIMARY KEY violation would
            // otherwise fire).
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([F],[Id],[Name],[Amount]) VALUES ('f1', 1, 'dup-a', 10), ('f1', 1, 'dup-b', 20);");

            var (inserts, updates) = await RunLoopAsync(cs, batch: false, trg, stg);

            Assert.Equal(1, inserts);
            Assert.Equal(0, updates);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    [SkippableFact]
    public async Task ReRun_WithUnchangedStaging_IsNoOp()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfDsLoop_RerunTrg";
        const string stg = "_SfDsLoop_RerunStg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await CreateTargetAsync(cs, trg);
            await CreateStagingAsync(cs, stg);
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([F],[Id],[Name],[Amount]) VALUES ('f1', 1, 'a', 10), ('f2', 2, 'b', 20);");

            var first = await RunLoopAsync(cs, batch: false, trg, stg);
            Assert.Equal(2, first.Inserts);
            Assert.Equal(0, first.Updates);

            var updatedBefore = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");

            var second = await RunLoopAsync(cs, batch: false, trg, stg);
            Assert.Equal(0, second.Inserts);
            Assert.Equal(0, second.Updates);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            var updatedAfter = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");
            Assert.Equal(updatedBefore, updatedAfter);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    [SkippableFact]
    public async Task NullDatasetValue_IsItsOwnPartition()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfDsLoop_NullTrg";
        const string stg = "_SfDsLoop_NullStg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await CreateTargetAsync(cs, trg);
            await CreateStagingAsync(cs, stg);

            // A NULL dataset value must be handled as its own partition, not dropped or crash the NULL-safe join.
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([F],[Id],[Name],[Amount]) VALUES (NULL, 1, 'n', 10), ('f1', 2, 'b', 20);");

            var (inserts, updates) = await RunLoopAsync(cs, batch: false, trg, stg);

            Assert.Equal(2, inserts);
            Assert.Equal(0, updates);
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [F] IS NULL AND [Id] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    [SkippableFact]
    public async Task BatchedAndUnbatched_ReachIdenticalFinalState()
    {
        var cs = IntegrationDb.Require();
        const string trgN = "_SfDsLoop_EqN";
        const string trgB = "_SfDsLoop_EqB";
        const string stg = "_SfDsLoop_EqStg";
        await IntegrationDb.DropTableAsync(cs, trgN);
        await IntegrationDb.DropTableAsync(cs, trgB);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await CreateTargetAsync(cs, trgN);
            await CreateTargetAsync(cs, trgB);
            await CreateStagingAsync(cs, stg);

            // A key recurring across datasets, several datasets, and a duplicate within a dataset: the batched
            // (BatchRowCount = 1, so every row is its own window) and unbatched paths must agree exactly.
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([F],[Id],[Name],[Amount]) VALUES " +
                "('f1', 1, 'a1', 10), ('f2', 1, 'a2', 20), ('f3', 1, 'a3', 30), " +
                "('f1', 2, 'b1', 40), ('f2', 3, 'c2', 50), ('f2', 3, 'c2dup', 60), ('f3', 4, 'd3', 70);");

            await RunLoopAsync(cs, batch: false, trgN, stg);
            await RunLoopAsync(cs, batch: true, trgB, stg);

            var diff = await IntegrationDb.ScalarAsync<int?>(cs,
                $"""
                 SELECT COUNT(*) FROM (
                     SELECT [F],[Id],[Name],[Amount] FROM [dbo].[{trgN}]
                     EXCEPT
                     SELECT [F],[Id],[Name],[Amount] FROM [dbo].[{trgB}]
                 ) AS d
                 """);
            Assert.Equal(0, diff);
            Assert.Equal(await IntegrationDb.RowCountAsync(cs, trgN), await IntegrationDb.RowCountAsync(cs, trgB));

            // Id 1 (in f1, f2, f3) ends at f3; Id 3's within-dataset duplicate collapsed to one row.
            Assert.Equal("a3", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trgN}] WHERE [Id] = 1"));
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trgN));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trgN);
            await IntegrationDb.DropTableAsync(cs, trgB);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }
}
