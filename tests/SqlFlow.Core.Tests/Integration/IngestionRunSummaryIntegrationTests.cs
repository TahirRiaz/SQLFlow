using System.Globalization;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// What an ingestion run SAYS about itself when it finishes. The wave board renders the run.end line verbatim, so
/// that one string is how an operator judges a run. It used to carry a rate alone, computed over a duration
/// truncated to whole seconds: every run under a second reported "SUCCESS in 0s (0 rows/s)" whatever it moved, and
/// a run that loaded a million rows was indistinguishable from one that loaded none. The line now states the
/// counts, and the duration keeps millisecond resolution so the rate divides by something real.
/// </summary>
[Trait("Category", "Integration")]
public sealed class IngestionRunSummaryIntegrationTests
{
    private const int FlowId = 91;

    /// <summary>Collects the run-boundary events so the assertions read exactly what the board renders.</summary>
    private sealed class CollectingSink : IRunEventSink
    {
        public List<(string Step, string Message)> Records { get; } = [];

        public RunLogLevel Level => RunLogLevel.Trace;

        public void Log(RunLogLevel level, string stepName, string message) => Records.Add((stepName, message));

        public string LastEnd => Records.Last(r => r.Step == "run.end").Message;
    }

    [SkippableFact]
    public async Task TheRunSummary_StatesTheRowsItMoved_AndSurvivesASubSecondRun()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfSummary_Src";
        const string trg = "_SfSummary_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([Id],[Name]) VALUES (1,'Ann'), (2,'Bob'), (3,'Cy');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var flow = new IngestionFlow
            {
                FlowId = FlowId,
                Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
                Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
                Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
            };

            var events = new CollectingSink();
            var run = await runner.RunAsync(flow, new IngestionRunOptions { Events = events });
            Assert.True(run.Success, run.Error);
            Assert.Equal(3, run.RowsStaged);

            // The counts are in the line, so a reader never has to open the run to learn whether anything moved.
            var summary = events.LastEnd;
            Assert.Contains("3 row(s) staged", summary, StringComparison.Ordinal);
            Assert.Contains("3 inserted", summary, StringComparison.Ordinal);
            Assert.Contains("0 updated", summary, StringComparison.Ordinal);

            // A three-row load finishes well inside a second. That must not collapse the duration to zero, nor the
            // rate with it: "0 rows/s" has to mean no rows, never "too fast to measure".
            Assert.True(run.DurationSeconds > 0, $"duration was {run.DurationSeconds}");
            Assert.DoesNotContain("in 0s", summary, StringComparison.Ordinal);
            Assert.DoesNotContain("(0 rows/s)", summary, StringComparison.Ordinal);

            // Rendered invariantly, so a node under a comma-decimal locale writes the same log as every other node.
            Assert.DoesNotContain(",", summary.AsSpan(0, summary.IndexOf("row(s)", StringComparison.Ordinal)).ToString(), StringComparison.Ordinal);

            // A re-run with an unchanged source stages the same rows and reports zero applied, which is the case
            // that has to stay readable as "nothing to do" rather than "something is wrong".
            var reRunEvents = new CollectingSink();
            var reRun = await runner.RunAsync(flow, new IngestionRunOptions { Events = reRunEvents });
            Assert.True(reRun.Success, reRun.Error);
            Assert.Contains("3 row(s) staged", reRunEvents.LastEnd, StringComparison.Ordinal);
            Assert.Contains("0 inserted", reRunEvents.LastEnd, StringComparison.Ordinal);

            // The duration the caller gets is the same one the line rendered, to milliseconds.
            Assert.Contains(
                run.DurationSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "s",
                summary,
                StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
        }
    }
}
