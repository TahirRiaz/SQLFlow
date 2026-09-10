using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The canonical run log against the real sink: a run at trace level records the step-by-step account WITH the
/// generated SQL woven into the timeline, info level records the account without it, and a failed run logs the
/// failure (the log always survives). The same events seam serves every process; the export runner is covered
/// in its own slice when its YAML mode lands.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RunLogIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg) => new()
    {
        FlowId = flowId,
        SysAlias = "runlog-test",
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
    };

    [SkippableFact]
    public async Task TraceLevel_RecordsTimeline_WithSqlInline()
    {
        const int flowId = 40;
        var cs = IntegrationDb.Require();
        const string src = "_SfRl_Src";
        const string trg = "_SfRl_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var logger = new RunLogger(RunLogLevel.Trace);
            var runner = RelationalIngestionHarness.BuildRunner();
            var result = await runner.RunAsync(Flow(flowId, src, trg), new IngestionRunOptions { Events = logger });

            Assert.True(result.Success, result.Error);
            var steps = logger.Entries.Select(e => e.Step).ToList();

            // The canonical account: start, introspection, staging, window, copy, evolve, apply, drop, end.
            foreach (var expected in new[] { "run.start", "source.introspect", "staging.create", "incremental.window", "stage.copy", "target.evolve", "upsert.apply", "staging.drop", "run.end" })
            {
                Assert.Contains(expected, steps);
            }

            // Trace level weaves the SQL into the timeline.
            Assert.Contains(logger.Entries, e => e.Level == RunLogLevel.Trace && e.Step == "upsert.insert" && e.Sql().Contains("INSERT INTO", StringComparison.Ordinal));

            // The end entry reports success.
            Assert.Contains("SUCCESS", logger.Entries.Last(e => e.Step == "run.end").Message, StringComparison.Ordinal);
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task InfoLevel_OmitsSqlAndDecisions()
    {
        const int flowId = 41;
        var cs = IntegrationDb.Require();
        const string src = "_SfRlI_Src";
        const string trg = "_SfRlI_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var logger = new RunLogger(RunLogLevel.Info);
            var runner = RelationalIngestionHarness.BuildRunner();
            var result = await runner.RunAsync(Flow(flowId, src, trg), new IngestionRunOptions { Events = logger });

            Assert.True(result.Success, result.Error);
            Assert.NotEmpty(logger.Entries);
            Assert.DoesNotContain(logger.Entries, e => e.Level > RunLogLevel.Info);

            // The SQL trace artifact is unaffected by the log level (always captured).
            Assert.NotEmpty(result.SqlTrace);
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task FailedRun_LogsTheFailure()
    {
        const int flowId = 42;
        var cs = IntegrationDb.Require();
        const string src = "_SfRlF_Src";
        const string trg = "_SfRlF_Trg";
        await Reset(cs, src, trg, flowId);
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var logger = new RunLogger(RunLogLevel.Info);
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg) with
            {
                Process = new ProcessPolicy { PostProcessOnTarget = "RAISERROR('runlog boom', 16, 1);" },
            };

            var result = await runner.RunAsync(flow, new IngestionRunOptions { Events = logger });

            Assert.False(result.Success);
            var end = logger.Entries.Last(e => e.Step == "run.end");
            Assert.Contains("FAILED", end.Message, StringComparison.Ordinal);
            Assert.Contains("boom", end.Message, StringComparison.Ordinal);

            // Everything before the failure is in the log too.
            Assert.Contains(logger.Entries, e => e.Step == "upsert.apply");
        }
        finally
        {
            await Reset(cs, src, trg, flowId);
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

internal static class RunLogEntryExtensions
{
    /// <summary>The message of a trace-level entry IS the SQL; named for test readability.</summary>
    public static string Sql(this SqlFlow.Core.Runs.RunLogEntry entry) => entry.Message;
}
