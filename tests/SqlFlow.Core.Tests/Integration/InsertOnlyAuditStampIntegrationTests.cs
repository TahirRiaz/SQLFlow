using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The two load paths that only ever insert, the keyless append and the per-file reload (<c>load.reloadColumn</c>),
/// stamp a new row's audit columns exactly as the keyed upsert's insert does: the merge time in <c>InsertedDate_DW</c>
/// and <c>UpdatedDate_DW</c>, and <c>'I'</c> in <c>RowStatus_DW</c>. Neither path ever updates a row, so an audit
/// column it left out stayed NULL on every row the table held, and a watermark or a <c>UpdatedDate_DW &gt;= @since</c>
/// predicate built on it never saw a row arrive. Runs against the real sink.
/// </summary>
[Trait("Category", "Integration")]
public sealed class InsertOnlyAuditStampIntegrationTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static readonly SystemColumnsPolicy AllAuditColumns = new() { InsertedDate = true, UpdatedDate = true, RowStatus = true };

    [SkippableFact]
    public async Task AKeylessAppend_StampsEveryAuditColumnTheFlowTurnsOn()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 8861;
        const string src = "_SfAuditKeyless_Src";
        const string trg = "_SfAuditKeyless_Trg";
        await DropAsync(cs, flowId, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Code] nvarchar(20) NULL, [Amount] int NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] ([Code],[Amount]) VALUES (N'a', 1), (N'b', 2), (N'c', 3);");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = Obj(src) },
                Target = new IngestionTarget { Server = "sink", Table = Obj(trg) },
                SystemColumns = AllAuditColumns,
            });

            Assert.True(result.Success, result.Error);
            await AssertEveryRowStampedAsync(cs, trg, expectedRows: 3);
        }
        finally
        {
            await DropAsync(cs, flowId, src, trg);
        }
    }

    [SkippableFact]
    public async Task APerFileReload_StampsUpdatedDateOnTheRowsItInserts()
    {
        var cs = IntegrationDb.Require();
        const int flowId = 8862;
        const string src = "_SfAuditReload_Src";
        const string trg = "_SfAuditReload_Trg";
        await DropAsync(cs, flowId, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([FileName_DW] nvarchar(400) NULL, [Id] int NOT NULL, [Val] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([FileName_DW],[Id],[Val]) VALUES (N'/land/a.csv', 1, 'a1'), (N'/land/a.csv', 2, 'a2');");

        try
        {
            var flow = new IngestionFlow
            {
                FlowId = flowId,
                Source = new IngestionSource { Server = "sink", Table = Obj(src) },
                Target = new IngestionTarget { Server = "sink", Table = Obj(trg) },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"], ReloadColumn = "FileName_DW" },
                SystemColumns = AllAuditColumns,
            };

            var first = await RelationalIngestionHarness.BuildRunner().RunAsync(flow);
            Assert.True(first.Success, first.Error);
            await AssertEveryRowStampedAsync(cs, trg, expectedRows: 2);

            // The resend replaces the file's rows; the rows it inserts are stamped again.
            await IntegrationDb.ExecuteAsync(cs, $"DELETE FROM [dbo].[{src}];");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] ([FileName_DW],[Id],[Val]) VALUES (N'/land/a.csv', 1, 'a1-v2');");
            var second = await RelationalIngestionHarness.BuildRunner().RunAsync(flow);
            Assert.True(second.Success, second.Error);
            await AssertEveryRowStampedAsync(cs, trg, expectedRows: 1);
        }
        finally
        {
            await DropAsync(cs, flowId, src, trg);
        }
    }

    private static async Task AssertEveryRowStampedAsync(string cs, string trg, long expectedRows)
    {
        Assert.Equal(expectedRows, await IntegrationDb.RowCountAsync(cs, trg));
        Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs,
            $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [InsertedDate_DW] IS NULL OR [UpdatedDate_DW] IS NULL " +
            "OR [RowStatus_DW] IS NULL OR [RowStatus_DW] <> 'I'"));
    }

    private static async Task DropAsync(string cs, int flowId, string src, string trg)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
    }
}
