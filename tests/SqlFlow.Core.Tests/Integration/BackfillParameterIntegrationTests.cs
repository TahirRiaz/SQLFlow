using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The built-in backfill (per-run substitution parameters) against the real sink for ingestion flows: a full-load
/// parameter reads the whole source regardless of the target watermark, and a backfill window bounds the source
/// read to the incremental date column, replacing the probed watermark entirely. Asserts the generated WHERE and
/// the staged row counts, so the override behavior is verified end to end, not just that the flag threaded through.
/// Gated on a reachable sink, like the sibling incremental suite.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BackfillParameterIntegrationTests
{
    private static IngestionFlow Flow(int flowId, string src, string trg, IncrementalPolicy incremental)
        => new()
        {
            FlowId = flowId,
            Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
            Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
            Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            Incremental = incremental,
        };

    [SkippableFact]
    public async Task FullLoadParameter_ReadsWholeSource_EvenPastTheWatermark()
    {
        const int flowId = 51;
        var cs = IntegrationDb.Require();
        const string src = "_SfBf1_Src";
        const string trg = "_SfBf1_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a'),(2,'b'),(3,'c');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });

            // Establish a watermark (Id=3) with a normal run.
            Assert.True((await runner.RunAsync(flow)).Success);
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (4,'d');");

            // The full-load PARAMETER overrides the watermark: the whole source is re-read, not just Id>3.
            var full = await runner.RunAsync(flow, new IngestionRunOptions
            {
                Parameters = new RunParameters { FullLoad = true },
            });
            Assert.True(full.Success, full.Error);
            Assert.Equal(string.Empty, full.SourceWhere);
            Assert.Equal(4, full.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task BackfillWindow_BoundsTheDateColumn_ReplacingTheWatermark()
    {
        const int flowId = 52;
        var cs = IntegrationDb.Require();
        const string src = "_SfBf2_Src";
        const string trg = "_SfBf2_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [OrderDate] datetime2(3) NULL");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'2023-01-05'),(2,'2023-01-20'),(3,'2023-02-10'),(4,'2023-03-01');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { DateColumn = "OrderDate", OverlapDays = 0 });

            // A closed window [2023-01-01, 2023-02-01): only January rows (Id 1, 2), regardless of any watermark.
            var windowed = await runner.RunAsync(flow, new IngestionRunOptions
            {
                Parameters = new RunParameters
                {
                    BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    BackfillTo = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                },
            });
            Assert.True(windowed.Success, windowed.Error);
            Assert.Contains(">= '2023-01-01", windowed.SourceWhere, StringComparison.Ordinal);
            Assert.Contains("< '2023-02-01", windowed.SourceWhere, StringComparison.Ordinal);
            Assert.Equal(2, windowed.RowsStaged);

            // An open-ended window [2023-02-01, ..): the February and March rows (Id 3, 4).
            var openEnded = await runner.RunAsync(flow, new IngestionRunOptions
            {
                Parameters = new RunParameters { BackfillFrom = new DateTime(2023, 2, 1, 0, 0, 0, DateTimeKind.Utc) },
            });
            Assert.True(openEnded.Success, openEnded.Error);
            Assert.Contains(">= '2023-02-01", openEnded.SourceWhere, StringComparison.Ordinal);
            Assert.DoesNotContain("<", openEnded.SourceWhere, StringComparison.Ordinal);
            Assert.Equal(2, openEnded.RowsStaged);
        }
        finally
        {
            await Cleanup(cs, src, trg, flowId);
        }
    }

    [SkippableFact]
    public async Task BackfillWindow_WithoutADateColumn_FailsWithAClearError()
    {
        const int flowId = 53;
        var cs = IntegrationDb.Require();
        const string src = "_SfBf3_Src";
        const string trg = "_SfBf3_Trg";
        await Reset(cs, src, trg, flowId, "[Id] int NOT NULL, [Val] nvarchar(20) NULL");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'a');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            // No incremental.dateColumn: a window has nothing to bound, so the run fails with a clear message
            // (the log-only runner returns a failed result rather than throwing).
            var flow = Flow(flowId, src, trg, new IncrementalPolicy { Columns = ["Id"] });
            var result = await runner.RunAsync(flow, new IngestionRunOptions
            {
                Parameters = new RunParameters { BackfillFrom = new DateTime(2023, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            });
            Assert.False(result.Success);
            Assert.Contains("dateColumn", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
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
