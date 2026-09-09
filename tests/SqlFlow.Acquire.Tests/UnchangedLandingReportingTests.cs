using SqlFlow.Acquire.Engine;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>
/// What an acquisition run REPORTS when it re-fetches data it already holds. A rolling-window feed serves the same
/// days on every call, so the landing pipeline recognizes the byte-identical payload and leaves the blob untouched
/// (no last-modified bump, so the downstream incremental flows are deliberately not re-triggered). The counters and
/// the run summary have to say so: quoting the landed count alone made a run that wrote nothing read as new files
/// arriving, and the flows after it, correctly seeing no new files and loading no rows, then looked broken.
/// </summary>
public sealed class UnchangedLandingReportingTests
{
    private const string BaseUrl = "https://api.example.test";
    private static readonly DateTimeOffset Now = new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Collects the run-boundary events so the assertions read exactly what an operator reads.</summary>
    private sealed class CollectingSink : IRunEventSink
    {
        public List<(RunLogLevel Level, string Step, string Message)> Records { get; } = [];

        public RunLogLevel Level => RunLogLevel.Trace;

        public void Log(RunLogLevel level, string stepName, string message)
            => Records.Add((level, stepName, message));

        public string LastEnd => Records.Last(r => r.Step == "run.end").Message;
    }

    private static AcquireFlow FlowFor(string landingDirectory) => new()
    {
        Name = "Unchanged_Flow",
        Items = [new AcquireItem
        {
            Source = new AcquireSource
            {
                BaseUrl = BaseUrl,
                Auth = new AcquireAuth { Type = AcquireAuthType.Bearer, SecretRef = "${test:token}" },
                Request = new AcquireRequest { Path = "/orders" },
            },
            Landing = new AcquireLanding
            {
                Target = landingDirectory, PathTemplate = "orders", Overwrite = true, SkipUnchanged = true,
            },
        }],
    };

    [Fact]
    public async Task ReFetchingAnIdenticalPayload_IsReportedAsUnchanged_NotAsANewFile()
    {
        const string payload = """[{"id":1,"name":"a"}]""";
        var handler = new StubHttpHandler().Json("/orders", _ => payload);
        var engine = TestEngine.Create(handler, new FakeSecrets(("token", "SECRET123")), new FixedClock(Now), out var dir);
        var runner = new AcquireFlowRunner(engine);
        var flow = FlowFor(dir);
        var anchor = Directory.CreateTempSubdirectory("sqlflow-acq-unchanged").FullName;

        try
        {
            // First run: the payload is genuinely new, so it lands and the summary says so with no caveat.
            var firstEvents = new CollectingSink();
            var first = await runner.RunAsync(flow, anchor, new IngestionRunOptions { Events = firstEvents });

            Assert.True(first.Success, first.Error);
            Assert.Equal(1, first.FilesWritten);
            Assert.Equal(0, first.Unchanged);
            Assert.Contains("1 file(s)", firstEvents.LastEnd, StringComparison.Ordinal);
            Assert.DoesNotContain("unchanged", firstEvents.LastEnd, StringComparison.Ordinal);

            // Second run: the same bytes. The file is counted as landed (it reached its target) but nothing was
            // written, and the summary has to lead with that so it is never read as new data arriving.
            var secondEvents = new CollectingSink();
            var second = await runner.RunAsync(flow, anchor, new IngestionRunOptions { Events = secondEvents });

            Assert.True(second.Success, second.Error);
            Assert.Equal(1, second.FilesWritten);
            Assert.Equal(1, second.Unchanged);
            Assert.Contains("0 new file(s), 1 unchanged", secondEvents.LastEnd, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(anchor, recursive: true);
        }
    }

    [Fact]
    public async Task AReprocess_RelandsThePayload_AndReportsItAsNew()
    {
        // A reprocess deliberately disables the skip so downstream re-reads the file; the summary must then report
        // it as new, because it genuinely was rewritten.
        const string payload = """[{"id":1,"name":"a"}]""";
        var handler = new StubHttpHandler().Json("/orders", _ => payload);
        var engine = TestEngine.Create(handler, new FakeSecrets(("token", "SECRET123")), new FixedClock(Now), out var dir);
        var runner = new AcquireFlowRunner(engine);
        var flow = FlowFor(dir);
        var anchor = Directory.CreateTempSubdirectory("sqlflow-acq-reland").FullName;

        try
        {
            await runner.RunAsync(flow, anchor, new IngestionRunOptions());

            var events = new CollectingSink();
            var reprocess = await runner.RunAsync(
                flow, anchor,
                new IngestionRunOptions { Events = events, Parameters = new RunParameters { FullLoad = true } });

            Assert.True(reprocess.Success, reprocess.Error);
            Assert.Equal(1, reprocess.FilesWritten);
            Assert.Equal(0, reprocess.Unchanged);
            Assert.Contains("1 file(s)", events.LastEnd, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(anchor, recursive: true);
        }
    }
}
