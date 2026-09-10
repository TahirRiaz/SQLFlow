using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The run's SQL trace against the real sink: every generated statement is captured in order on a successful
/// run, the trace-so-far survives a FAILED run (the case that matters for debugging), the batched
/// lock-escalation-avoiding apply produces correct counts, IgnoreColumnsInHash suppresses false updates, and
/// with-database mode persists the rendered trace to flw.SysLog.TraceLog.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SqlTraceIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg) => new()
    {
        FlowId = flowId,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
    };

    [SkippableFact]
    public async Task SuccessfulRun_CapturesOrderedTrace()
    {
        const int flowId = 9105;
        var cs = IntegrationDb.Require();
        const string src = "_SfTrace_Src";
        const string trg = "_SfTrace_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var result = await runner.RunAsync(Flow(flowId, src, trg));

            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(result.SqlTrace);

            // Sequences are 1..n in order.
            Assert.Equal(Enumerable.Range(1, result.SqlTrace.Count), result.SqlTrace.Select(e => e.Sequence));

            // The load-bearing steps are all present, in execution order.
            var steps = result.SqlTrace.Select(e => e.Step).ToList();
            var expectedOrder = new[] { "staging.create", "source.select", "target.evolve", "upsert.update", "upsert.insert", "staging.drop" };
            var positions = expectedOrder.Select(s => steps.IndexOf(s)).ToList();
            Assert.DoesNotContain(-1, positions);
            Assert.Equal(positions.OrderBy(p => p), positions);

            // The trace carries the real SQL (the upsert insert references the flow's canonical staging table).
            var insertEntry = result.SqlTrace.Single(e => e.Step == "upsert.insert");
            Assert.Contains($"[raw].[dbo_{trg}_{flowId}]", insertEntry.Sql, StringComparison.Ordinal);
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FailedRun_KeepsTraceUpToFailurePoint()
    {
        const int flowId = 9106;
        var cs = IntegrationDb.Require();
        const string src = "_SfTraceF_Src";
        const string trg = "_SfTraceF_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg) with
            {
                Process = new ProcessPolicy { PostProcessOnTarget = "RAISERROR('trace-test boom', 16, 1);" },
            };

            var result = await runner.RunAsync(flow);

            Assert.False(result.Success);
            Assert.Contains("boom", result.Error ?? string.Empty, StringComparison.Ordinal);

            // The failure case is where the trace matters: everything generated up to and including the
            // failing hook is captured.
            Assert.NotEmpty(result.SqlTrace);
            Assert.Contains(result.SqlTrace, e => e.Step == "upsert.insert");
            Assert.Contains(result.SqlTrace, e => e.Step == "target.postprocess" && e.Sql.Contains("boom", StringComparison.Ordinal));
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task BatchedApply_UpsertsCorrectly_WithWindowedScripts()
    {
        const int flowId = 32;
        var cs = IntegrationDb.Require();
        const string src = "_SfTraceB_Src";
        const string trg = "_SfTraceB_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] SELECT TOP (10) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'v' FROM sys.objects;");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg) with
            {
                Load = new IngestionLoadPolicy
                {
                    KeyColumns = ["Id"],
                    BatchUpsertToAvoidLockEscalation = true,
                    BatchUpsertRowCount = 3,    // 10 rows -> 4 windows, proving the loop accumulates counts
                },
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);
            Assert.Equal(10, first.RowsInserted);
            Assert.Equal(10, await IntegrationDb.RowCountAsync(cs, trg));

            // Change 4 rows and add 2: the windowed update and insert must count exactly.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Val] = 'changed' WHERE [Id] <= 4;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (11,'n'),(12,'n');");

            var second = await runner.RunAsync(flow);
            Assert.True(second.Success, second.Error);
            Assert.Equal(4, second.RowsUpdated);
            Assert.Equal(2, second.RowsInserted);
            Assert.Equal(12, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Contains(second.SqlTrace, e => e.Step == "upsert.update" && e.Sql.Contains("#UpsertKeysU", StringComparison.Ordinal));
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task IgnoreColumnsInHash_SuppressesFalseUpdates()
    {
        const int flowId = 33;
        var cs = IntegrationDb.Require();
        const string src = "_SfTraceH_Src";
        const string trg = "_SfTraceH_Trg";
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL, [Note] nvarchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a','n1');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg) with
            {
                Change = new ChangePolicy { IgnoreColumnsInHash = ["Note"] },
            };

            var first = await runner.RunAsync(flow);
            Assert.True(first.Success, first.Error);

            // Only the ignored column changes: the checksum sees no difference, so nothing updates.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Note] = 'changed';");
            var second = await runner.RunAsync(flow);

            Assert.True(second.Success, second.Error);
            Assert.Equal(0, second.RowsUpdated);
            Assert.Equal(0, second.RowsInserted);
            Assert.Equal("n1", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Note] FROM [dbo].[{trg}] WHERE [Id] = 1"));

            // A checksummed column changing IS detected, and the excluded column is still copied with it.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Val] = 'b';");
            var third = await runner.RunAsync(flow);

            Assert.True(third.Success, third.Error);
            Assert.Equal(1, third.RowsUpdated);
            Assert.Equal("changed", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Note] FROM [dbo].[{trg}] WHERE [Id] = 1"));
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FullMode_PersistsTraceLog_ToSysLog()
    {
        const int flowId = 34;
        var cs = IntegrationDb.Require();
        const string src = "_SfTraceL_Src";
        const string trg = "_SfTraceL_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");
        await IntegrationDb.ExecuteAsync(cs,
            $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId};");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunnerWithRunLog(cs);
            var result = await runner.RunAsync(Flow(flowId, src, trg));
            Assert.True(result.Success, result.Error);

            var traceLog = await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [TraceLog] FROM [flw].[SysLog] WHERE [FlowID] = {flowId}");
            Assert.False(string.IsNullOrWhiteSpace(traceLog));
            Assert.Contains("upsert.insert", traceLog, StringComparison.Ordinal);
            var createCmd = await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [CreateCmd] FROM [flw].[SysLog] WHERE [FlowID] = {flowId}");
            Assert.Contains("CREATE TABLE", createCmd ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
            await IntegrationDb.ExecuteAsync(cs,
                $"IF OBJECT_ID('flw.SysLog','U') IS NOT NULL DELETE FROM [flw].[SysLog] WHERE [FlowID] = {flowId}; " +
                $"IF OBJECT_ID('flw.SysStats','U') IS NOT NULL DELETE FROM [flw].[SysStats] WHERE [FlowID] = {flowId};");
        }
    }

    private static async Task Reset(string cs, string src, string trg, int flowId)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Val] nvarchar(20) NULL);");
    }
}
