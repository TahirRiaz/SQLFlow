using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Tests.Integration;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A file run that selects no files is a clean no-op for every kind of run, not only an incremental one: a full
/// load, an explicit backfill window and a flow with no incremental spec all end as a success that loaded nothing,
/// with a warning that says why. The selection is decided before the target is touched, so these runs use the real
/// runner against a connection that is never opened.
/// </summary>
public sealed class EmptySourceRunTests : IDisposable
{
    private const string UnopenedConnection = "Server=never-opened;Database=never-opened";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_empty_" + Guid.NewGuid().ToString("N"));

    public EmptySourceRunTests() => Directory.CreateDirectory(_dir);

    private sealed class RecordingSink : IFlowEventSink
    {
        public List<FlowEvent> Published { get; } = [];

        public void Publish(FlowEvent flowEvent) => Published.Add(flowEvent);
    }

    private FlowDefinition Flow(Dictionary<string, string?> options, IncrementalSpec? incremental)
        => new()
        {
            Name = "empty_source",
            Source = new SourceSpec { Type = "csv", Location = _dir, Options = options },
            Target = new TargetSpec { Connection = UnopenedConnection, Schema = "dbo", Table = "EmptySource" },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
            Incremental = incremental,
        };

    private static async Task<(FlowResult Result, RecordingSink Events)> RunAsync(FlowDefinition flow)
    {
        var events = new RecordingSink();
        var result = await IntegrationDb.RealRunner().RunAsync(
            flow, runId: null, statementSink: null, runHistoryDirectory: null, watermarkTable: null, events: events);
        return (result, events);
    }

    private static void AssertCleanNoOp(FlowResult result, RecordingSink events)
    {
        Assert.Equal(FlowStatus.Success, result.Status);
        Assert.Equal(0, result.RowsLoaded);
        Assert.Null(result.Error);

        // The run did what it was asked and the answer was "nothing": no stage failed and no error was published.
        Assert.DoesNotContain(result.Trace, t => !t.Succeeded);
        Assert.DoesNotContain(events.Published, e => e.Level == FlowEventLevel.Error);
    }

    [Fact]
    public async Task BackfillWindow_ThatSelectsNoFile_IsASuccessWithAWarningNamingTheFileDate()
    {
        var path = Path.Combine(_dir, "seed.csv");
        File.WriteAllText(path, "OrderId\n1\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 2, 18, 42, 47, DateTimeKind.Utc));

        // What a backfill window does to a file flow: the init file-date window is set and the watermark is
        // suppressed (a forced full load), so the incremental no-op fallback does not apply to this run.
        var flow = Flow(
            new()
            {
                ["srcFile"] = "*.csv",
                ["initFromFileDate"] = "2025-10-01 00:00:00",
                ["initToFileDate"] = "2025-10-24 23:59:00",
            },
            new IncrementalSpec { DateColumn = "FileDate_DW", FullLoad = true });

        var (result, events) = await RunAsync(flow);

        AssertCleanNoOp(result, events);
        var warning = Assert.Single(events.Published, e => e.Level == FlowEventLevel.Warning);
        Assert.Contains("date window [2025-10-01 00:00:00 .. 2025-10-24 23:59:00]", warning.Message, StringComparison.Ordinal);
        Assert.Contains("examined 1 file(s)", warning.Message, StringComparison.Ordinal);

        // The window is only explicable next to the date it was compared with, and where that date was read.
        Assert.Contains("dated 2026-10-02 18:42:47 UTC (the file's modified time)", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FullLoad_OverAnEmptyLocation_IsASuccessWithAWarning()
    {
        var flow = Flow(new() { ["srcFile"] = "*.csv" }, new IncrementalSpec { DateColumn = "FileDate_DW", FullLoad = true });

        var (result, events) = await RunAsync(flow);

        AssertCleanNoOp(result, events);
        var warning = Assert.Single(events.Published, e => e.Level == FlowEventLevel.Warning);
        Assert.Contains("match pattern '*.csv'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FlowWithNoIncrementalSpec_OverAnEmptyLocation_IsASuccessWithAWarning()
    {
        var flow = Flow(new() { ["srcFile"] = "*.csv" }, incremental: null);

        var (result, events) = await RunAsync(flow);

        AssertCleanNoOp(result, events);
        var warning = Assert.Single(events.Published, e => e.Level == FlowEventLevel.Warning);
        Assert.Contains("match pattern '*.csv'", warning.Message, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
