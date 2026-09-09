using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the run log in BOTH modes against the real sink: with-database mode persists one live flw.SysLog
/// row per flow (archiving the prior run to flw.SysStats) with the correct fetched/inserted/updated counts and
/// timing; without-database mode (the default no-op log) writes nothing; a failed run is logged with
/// Success = 0 and the error, and the run still returns rather than throwing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionRunLogIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg)
        => new()
        {
            FlowId = flowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
        };

    [SkippableFact]
    public async Task WithDatabase_WritesLiveRow_ArchivesPriorRun_AndCountsAreCorrect()
    {
        const int flowId = 8;
        var cs = IntegrationDb.Require();
        const string src = "_SfRunLog_Src";
        const string trg = "_SfRunLog_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunnerWithRunLog(cs);
            var flow = Flow(flowId, src, trg);

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, first.RowsInserted);
            Assert.Equal(0, first.RowsUpdated);

            Assert.Equal(2L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Fetched] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(2L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Inserted] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(0L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Updated] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int, [Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CASE WHEN [StartTime] IS NOT NULL AND [EndTime] IS NOT NULL AND [EndTime] >= [StartTime] THEN 1 ELSE 0 END FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            var process = await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Process] FROM [flw].[SysLog] WHERE FlowID = {flowId}");
            Assert.Contains(src, process ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains(trg, process ?? string.Empty, StringComparison.Ordinal);

            // Mutate the source and re-run: one row changes, one is new.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Val] = 'z' WHERE [Id] = 1;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (3,'c');");

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(3L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Fetched] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Inserted] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1L, await IntegrationDb.ScalarAsync<long?>(cs, $"SELECT [Updated] FROM [flw].[SysLog] WHERE FlowID = {flowId}"));

            // One live SysLog row, one archived SysStats row (the prior run).
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [flw].[SysStats] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task WithoutDatabase_WritesNoRunLog()
    {
        const int flowId = 9;
        var cs = IntegrationDb.Require();
        const string src = "_SfRunLogOff_Src";
        const string trg = "_SfRunLogOff_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            // Default runner = without-database mode (NullIngestionRunLog).
            var runner = RelationalIngestionHarness.BuildRunner();
            var result = await runner.RunAsync(Flow(flowId, src, trg));
            Assert.True(result.Success, result.Error);

            // No run-log row for this flow (the table may not even exist; the guard returns 0 either way).
            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT CASE WHEN OBJECT_ID('flw.SysLog','U') IS NULL THEN 0 ELSE (SELECT COUNT(*) FROM [flw].[SysLog] WHERE FlowID = {flowId}) END"));
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FailedRun_IsLoggedWithSuccessZero_AndReturns()
    {
        const int flowId = 10;
        var cs = IntegrationDb.Require();
        const string trg = "_SfRunLogErr_Trg";
        await CleanRunLog(cs, flowId);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        try
        {
            var runner = RelationalIngestionHarness.BuildRunnerWithRunLog(cs);
            // The source object does not exist, so the run fails (and must not throw).
            var flow = Flow(flowId, "_SfRunLog_NoSuchSource", trg);

            var result = await runner.RunAsync(flow);
            Assert.False(result.Success);
            Assert.NotNull(result.Error);

            Assert.Equal(0, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CONVERT(int, [Success]) FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT CASE WHEN [ErrorRuntime] IS NOT NULL THEN 1 ELSE 0 END FROM [flw].[SysLog] WHERE FlowID = {flowId}"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
            await CleanRunLog(cs, flowId);
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await CleanRunLog(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
    }

    private static async Task Cleanup(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await CleanRunLog(cs, flowId);
    }

    private static Task CleanRunLog(string cs, int flowId)
        => IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE FlowID = {flowId}; " +
            $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE FlowID = {flowId};");
}
