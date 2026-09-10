using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Proves the per-file (per-dataset) full replace (<c>load.reloadColumn</c>) against the real sink: the two-flow
/// file-landing behaviour where a resent file must fully replace its prior version in the ods table. The headline
/// case is the one the user posed: a landing table holds several files, one of them is a resend, and only that
/// file's rows must be purged and reloaded (including records the new version dropped, which a keyed upsert would
/// orphan) while the other files are left untouched. The runner-level tests drive the full pipeline
/// (introspect the source that stands in for the <c>v_</c> view, stage the incremental batch, purge, insert);
/// the direct-SQL tests pin the purge semantics (NULL-safety, multi-dataset batches). These tests skip when the
/// sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReloadColumnIntegrationTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    // The headline scenario. The source table stands in for the pre view [pre].[v<Table>]: it carries the file
    // provenance columns (FileName_DW = the full path, the dataset key; FileDate_DW = the incremental watermark)
    // alongside the data. Incremental reads only the resent file into staging, and reloadColumn purges exactly
    // that file from the target before re-inserting it.
    [SkippableFact]
    public async Task Resend_ReplacesOnlyThatFile_DroppingRemovedRecords_LeavingOthersUntouched()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 8801;
        const string src = "_SfReload_Src";
        const string trg = "_SfReload_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([FileName_DW] nvarchar(400) NULL, [FileDate_DW] int NOT NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");

        // Batch 1 (FileDate 100): two files, two rows each.
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([FileName_DW],[FileDate_DW],[Id],[Val]) VALUES " +
            "(N'/land/a.csv', 100, 1, 'a1-v1'), (N'/land/a.csv', 100, 2, 'a2-v1'), " +
            "(N'/land/b.csv', 100, 3, 'b3'),    (N'/land/b.csv', 100, 4, 'b4');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = Obj(src) },
                Target = new IngestionTarget { Server = "sink", Table = Obj(trg) },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"], ReloadColumn = "FileName_DW" },
                Incremental = new IncrementalPolicy { Columns = ["FileDate_DW"], OverlapDays = 0 },
            };

            // Run 1: target empty, no watermark -> reads all four rows, purge matches nothing, inserts all four.
            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, first.RowsDeleted);

            // The resend: a.csv comes back with a NEWER file date (200). Its new version changes id 1, DROPS id 2,
            // and adds id 5. b.csv is untouched (still date 100).
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}] WHERE [FileName_DW] = N'/land/a.csv';");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{src}] ([FileName_DW],[FileDate_DW],[Id],[Val]) VALUES " +
                "(N'/land/a.csv', 200, 1, 'a1-v2'), (N'/land/a.csv', 200, 5, 'a5-new');");

            // Run 2: incremental MAX(FileDate_DW)=100 -> reads only a.csv's date-200 rows into staging. reloadColumn
            // purges the target rows for a.csv (id 1 and id 2), then inserts the new version (id 1 v2, id 5).
            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);

            // a.csv fully replaced: id 1 updated, id 2 (dropped from the new version) is GONE, id 5 added.
            Assert.Equal("a1-v2", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal("a5-new", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 5"));

            // b.csv untouched.
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [FileName_DW] = N'/land/b.csv'"));
            Assert.Equal("b3", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 3"));

            // Two rows of a.csv were purged; the final table is b(2) + a(2) = 4.
            Assert.Equal(2, second.RowsDeleted);
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    // A keyless reload (no business key): the file is the whole unit of replacement, so re-reading a file fully
    // replaces its rows even without keyColumns and even when the row count shrinks.
    [SkippableFact]
    public async Task KeylessReload_FullyReplacesTheFile_OnEveryRun()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 8802;
        const string src = "_SfReloadKl_Src";
        const string trg = "_SfReloadKl_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([FileName_DW] nvarchar(400) NULL, [Val] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([FileName_DW],[Val]) VALUES (N'/land/x.csv', 'r1'), (N'/land/x.csv', 'r2'), (N'/land/x.csv', 'r3');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = Obj(src) },
                Target = new IngestionTarget { Server = "sink", Table = Obj(trg) },
                Load = new IngestionLoadPolicy { ReloadColumn = "FileName_DW" },
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));

            // The file is resent with fewer rows. A full read (no incremental) purges x.csv and reloads its two
            // rows: the target must SHRINK to 2, not keep the third orphaned row.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}];");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{src}] ([FileName_DW],[Val]) VALUES (N'/land/x.csv', 'r1-new'), (N'/land/x.csv', 'r2-new');");

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(3, second.RowsDeleted);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Val] LIKE '%-new'"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    private static async Task<long> RunPurgeAndInsertAsync(string cs, string trg, string stg, UpsertOptions options)
    {
        var statements = UpsertGenerator.GenerateStatements(Obj(trg), Obj(stg), options);
        long deleted = 0;
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var tx = (SqlTransaction)await connection.BeginTransactionAsync();
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement.Sql, connection, tx) { CommandTimeout = 0 };
            var affected = await command.ExecuteNonQueryAsync();
            if (statement.Kind == UpsertStatementKind.Purge)
            {
                deleted += affected;
            }
        }

        await tx.CommitAsync();
        return deleted;
    }

    // Rows with no file identity (NULL FileName_DW) must never be purged: they are not "the same file" as anything.
    [SkippableFact]
    public async Task Purge_NeverTouchesNullIdentityRows()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfReloadNull_Trg";
        const string stg = "_SfReloadNull_Stg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{trg}] ([FileName_DW] nvarchar(400) NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{stg}] ([FileName_DW] nvarchar(400) NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");

            // Target has a NULL-identity row plus one for a.csv; staging brings a NULL row too.
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([FileName_DW],[Id],[Val]) VALUES (NULL, 1, 'keep-null'), (N'/land/a.csv', 2, 'old-a');");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{stg}] ([FileName_DW],[Id],[Val]) VALUES (NULL, 3, 'stg-null'), (N'/land/a.csv', 4, 'new-a');");

            var deleted = await RunPurgeAndInsertAsync(cs, trg, stg, new UpsertOptions
            {
                DataColumns = ["FileName_DW", "Id", "Val"],
                KeyColumns = ["Id"],
                ReloadColumn = "FileName_DW",
            });

            // Only the a.csv target row is purged (1). The NULL-identity target row survives.
            Assert.Equal(1, deleted);
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [FileName_DW] IS NULL AND [Val] = 'keep-null'"));
            // The new a.csv row and the staged NULL-identity row are both inserted.
            Assert.Equal("new-a", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 4"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 3 AND [FileName_DW] IS NULL"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    // Several resends in one batch: every dataset present in staging is purged, and only those.
    [SkippableFact]
    public async Task Purge_ScopesToEveryDatasetInTheBatch()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfReloadMulti_Trg";
        const string stg = "_SfReloadMulti_Stg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{trg}] ([FileName_DW] nvarchar(400) NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{stg}] ([FileName_DW] nvarchar(400) NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");

            // Target holds three files. Staging resends a.csv and c.csv (not b.csv).
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{trg}] ([FileName_DW],[Id],[Val]) VALUES " +
                "(N'/a.csv', 1, 'a-old'), (N'/b.csv', 2, 'b-old'), (N'/c.csv', 3, 'c-old');");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([FileName_DW],[Id],[Val]) VALUES " +
                "(N'/a.csv', 1, 'a-new'), (N'/c.csv', 3, 'c-new');");

            var deleted = await RunPurgeAndInsertAsync(cs, trg, stg, new UpsertOptions
            {
                DataColumns = ["FileName_DW", "Id", "Val"],
                KeyColumns = ["Id"],
                ReloadColumn = "FileName_DW",
            });

            Assert.Equal(2, deleted);
            // b.csv untouched; a.csv and c.csv replaced.
            Assert.Equal("b-old", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal("a-new", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("c-new", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 3"));
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }
}
