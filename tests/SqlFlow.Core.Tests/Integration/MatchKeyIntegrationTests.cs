using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the key-match (deleted-row detection) pass end-to-end against the real sink. Every test
/// starts from a clean pair of tables, runs one or more ingestion passes, and asserts the target rows
/// affected match exactly what the policy says. These tests skip when the sink is not reachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MatchKeyIntegrationTests
{
    private const int FlowId = 88;

    private static IngestionFlow BuildFlow(
        string src,
        string trg,
        MatchKeyAction action = MatchKeyAction.Tag,
        int thresholdPercent = 100,
        bool rowStatus = false)
    {
        return new IngestionFlow
        {
            FlowId = FlowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy
            {
                KeyColumns = ["Id"],
                MatchKeysInSourceAndTarget = true,
            },
            MatchKeys = new MatchKeyPolicy
            {
                Action = action,
                ActionThresholdPercent = thresholdPercent,
            },
            SystemColumns = new SystemColumnsPolicy
            {
                InsertedDate = true,
                UpdatedDate = true,
                DeletedDate = action == MatchKeyAction.Tag,
                RowStatus = rowStatus,
            },
        };
    }

    [SkippableFact]
    public async Task TagMode_SoftDeletes_RowsGoneFromSource()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_TagSrc";
        const string trg = "_SfMK_TagTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob'),(3,'Cy');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = BuildFlow(src, trg);

            // First run: 3 rows loaded, none deleted (source and target match).
            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, first.RowsDeleted);

            // Remove row 2 from source; second run should tag it.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id = 2;");
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(1, second.RowsDeleted);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // Soft-deleted row has DeletedDate_DW set; the other two do not.
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "DeletedDate_DW"));
            var taggedCount = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(1, taggedCount);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task DeleteMode_HardDeletes_RowsGoneFromSource()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_DelSrc";
        const string trg = "_SfMK_DelTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob'),(3,'Cy');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = BuildFlow(src, trg, MatchKeyAction.Delete);

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, first.RowsDeleted);

            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id IN (1,2);");
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(2, second.RowsDeleted);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task TagMode_Resurrection_UnTagsRowWhenKeyReappears()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_ResSrc";
        const string trg = "_SfMK_ResTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = BuildFlow(src, trg);

            // Load both rows.
            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);

            // Remove row 2: second run tags it.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id = 2;");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);
            Assert.Equal(1, r2.RowsDeleted);

            var taggedAfterDelete = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(1, taggedAfterDelete);

            // Re-add row 2: third run should un-tag it (resurrection) and report no deleted rows.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (2,'Bob');");
            var r3 = await runner.RunAsync(flow);
            Assert.True(r3.Success, r3.Error);
            Assert.Equal(0, r3.RowsDeleted);

            var taggedAfterResurrect = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(0, taggedAfterResurrect);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task ThresholdBreached_ActionSkipped_RowsUnchanged()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_ThrSrc";
        const string trg = "_SfMK_ThrTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob'),(3,'Cy'),(4,'Dan'),(5,'Eve');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            // Load all 5 rows, threshold is 10% (very low).
            var flow = BuildFlow(src, trg, thresholdPercent: 10);
            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, trg));

            // Remove 4 of 5 rows from source: 80% of the target would be tagged, well above 10%.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id IN (2,3,4,5);");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);

            // Action was skipped: all rows remain untagged.
            Assert.Equal(0, r2.RowsDeleted);
            var tagged = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(0, tagged);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task TagMode_RowStatusColumn_StampedD_ResetU_OnResurrection()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_RSSrc";
        const string trg = "_SfMK_RSTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = BuildFlow(src, trg, rowStatus: true);

            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);

            // Remove row 2: should stamp DeletedDate_DW and RowStatus_DW = 'D'.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id = 2;");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);
            Assert.Equal(1, r2.RowsDeleted);

            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "RowStatus_DW"));
            var statusAfterTag = await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [RowStatus_DW] FROM [dbo].[{trg}] WHERE [Id] = 2");
            Assert.Equal("D", statusAfterTag);

            // Re-add row 2: resurrection should clear DeletedDate_DW and reset RowStatus_DW = 'U'.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (2,'Bob');");
            var r3 = await runner.RunAsync(flow);
            Assert.True(r3.Success, r3.Error);

            var statusAfterResurrect = await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [RowStatus_DW] FROM [dbo].[{trg}] WHERE [Id] = 2");
            Assert.Equal("U", statusAfterResurrect);
            var deletedDate = await IntegrationDb.ScalarAsync<DateTime?>(cs,
                $"SELECT [DeletedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 2");
            Assert.Null(deletedDate);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task CompositeKey_TagsOnlyRowsWhoseCombinedKeyIsGone()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_CkSrc";
        const string trg = "_SfMK_CkTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Region] nvarchar(10) NOT NULL, [Val] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'NA',10),(1,'EU',20),(2,'NA',30);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id", "Region"], MatchKeysInSourceAndTarget = true },
                MatchKeys = new MatchKeyPolicy { Action = MatchKeyAction.Tag, ActionThresholdPercent = 100 },
                SystemColumns = new SystemColumnsPolicy { InsertedDate = true, UpdatedDate = true, DeletedDate = true },
            };

            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // Remove (1,'EU'): only that composite key vanishes.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id = 1 AND Region = 'EU';");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);
            Assert.Equal(1, r2.RowsDeleted);

            var tagged = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(1, tagged);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task TargetFilter_ScopesWhichRowsCanBeTagged()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_TfSrc";
        const string trg = "_SfMK_TfTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        // Two regions; the flow targets only 'EU'.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Region] nvarchar(10) NOT NULL, [Val] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'NA',10),(2,'EU',20),(3,'EU',30);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"], MatchKeysInSourceAndTarget = true },
                MatchKeys = new MatchKeyPolicy
                {
                    Action = MatchKeyAction.Tag,
                    ActionThresholdPercent = 100,
                    TargetFilter = "AND [Region] = 'EU'",
                },
                SystemColumns = new SystemColumnsPolicy { InsertedDate = true, UpdatedDate = true, DeletedDate = true },
            };

            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // Remove EU row 3 from source; NA row 1 vanishes too but is outside the targetFilter.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Id IN (1,3);");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);

            // Only the EU row (Id=3) should be tagged; NA row (Id=1) is outside the target filter.
            Assert.Equal(1, r2.RowsDeleted);
            var taggedId3 = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 3 AND [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(1, taggedId3);
            var taggedId1 = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1 AND [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(0, taggedId1);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task FullSourceWipe_WithThreshold100_DeletesAll()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_WipeSrc";
        const string trg = "_SfMK_WipeTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann'),(2,'Bob'),(3,'Cy');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = BuildFlow(src, trg, MatchKeyAction.Delete, thresholdPercent: 100);

            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // Wipe source entirely: 100% of target would be deleted, threshold=100 means ">100" is needed to
            // breach, so exactly 100% (= 100, not > 100) is allowed through.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}];");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);
            Assert.Equal(3, r2.RowsDeleted);
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    [SkippableFact]
    public async Task MatchKeysKeyColumns_OverridesLoadKeyColumns()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfMK_OvrSrc";
        const string trg = "_SfMK_OvrTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await DropMatchKeyTablesAsync(cs);

        // The load key is Id, but matchKeys uses (Id, Region) as the match key.
        // Deleting (1,'EU') should tag only that combined key on the target.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Region] nvarchar(10) NOT NULL, [Val] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'NA',10),(1,'EU',20);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"], MatchKeysInSourceAndTarget = true },
                MatchKeys = new MatchKeyPolicy
                {
                    Action = MatchKeyAction.Tag,
                    ActionThresholdPercent = 100,
                    KeyColumns = ["Id", "Region"],
                },
                SystemColumns = new SystemColumnsPolicy { InsertedDate = true, UpdatedDate = true, DeletedDate = true },
            };

            var r1 = await runner.RunAsync(flow);
            Assert.True(r1.Success, r1.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));

            // Remove (1,'EU') from source; (1,'NA') stays.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE Region = 'EU';");
            var r2 = await runner.RunAsync(flow);
            Assert.True(r2.Success, r2.Error);
            Assert.Equal(1, r2.RowsDeleted);

            var tagged = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [DeletedDate_DW] IS NOT NULL");
            Assert.Equal(1, tagged);
            var notTagged = await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Region] = 'NA' AND [DeletedDate_DW] IS NULL");
            Assert.Equal(1, notTagged);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await DropMatchKeyTablesAsync(cs);
        }
    }

    private static Task DropMatchKeyTablesAsync(string cs)
        => IntegrationDb.ExecuteAsync(cs,
            $"DECLARE @sql nvarchar(max) = N''; " +
            "SELECT @sql += 'DROP TABLE [raw].[' + t.name + '];' FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            $"WHERE s.name = 'raw' AND t.name LIKE 'mkey[_]%[_]{FlowId}'; " +
            "IF LEN(@sql) > 0 EXEC sys.sp_executesql @sql;");
}
