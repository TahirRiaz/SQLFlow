using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Events;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The canonical run event stream: the run-log bridge (one Log call feeds run.log AND the event
/// stream), the collector (published events become run.json <c>events</c> records), and the composite fan-out
/// the runner uses to attach a per-run sink beside the host-wide one.</summary>
public sealed class RunEventStreamTests
{
    private sealed class RecordingSink : IFlowEventSink
    {
        public List<FlowEvent> Published { get; } = [];

        public void Publish(FlowEvent flowEvent) => Published.Add(flowEvent);
    }

    [Fact]
    public void Bridge_ForwardsEveryEntryToTheRunLog_AndHonoursItsLevel()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        var events = new RecordingSink();
        var bridge = new RunLogEventBridge(logger, events);

        bridge.Log(RunLogLevel.Info, "incremental", "watermark resolved");
        bridge.Log(RunLogLevel.Debug, "mapping", "42 columns mapped");
        bridge.Log(RunLogLevel.Trace, "upsert.insert", "INSERT INTO t;");

        // The wrapped logger still applies its own level (Info): only the info entry lands in run.log.
        var kept = Assert.Single(logger.Entries);
        Assert.Equal("incremental", kept.Step);
    }

    [Fact]
    public void Bridge_PublishesInfoAndDebugAsEvents_AndKeepsSqlMirrorsOut()
    {
        var logger = new RunLogger(RunLogLevel.Trace);
        var events = new RecordingSink();
        var runId = Guid.NewGuid();
        var bridge = new RunLogEventBridge(logger, events, runId, "my_flow");

        bridge.Log(RunLogLevel.Info, "incremental", "watermark resolved");
        bridge.Log(RunLogLevel.Debug, "mapping", "42 columns mapped");
        bridge.Log(RunLogLevel.Trace, "upsert.insert", "INSERT INTO t;");

        // Trace entries are exactly the generated-SQL mirrors, which stream through the statement sink; the
        // event stream carries everything else, whatever level run.log was asked for.
        Assert.Equal(2, events.Published.Count);
        Assert.Equal(FlowEventLevel.Info, events.Published[0].Level);
        Assert.Equal("incremental", events.Published[0].Stage);
        Assert.Equal("watermark resolved", events.Published[0].Message);
        Assert.Equal(runId, events.Published[0].RunId);
        Assert.Equal("my_flow", events.Published[0].FlowName);
        Assert.Equal(FlowEventLevel.Debug, events.Published[1].Level);
        // The SQL mirror still reached the run log (its level is Trace).
        Assert.Equal(3, logger.Entries.Count);
    }

    [Fact]
    public void Bridge_ReportsTraceLevel_SoNoRunnerDropsAnEntryAtTheSource()
    {
        var bridge = new RunLogEventBridge(new RunLogger(RunLogLevel.Info), new RecordingSink());
        Assert.Equal(RunLogLevel.Trace, bridge.Level);
    }

    [Fact]
    public void Collector_TurnsPublishedEventsIntoRecords_InOrder_AndForwardsLive()
    {
        var live = new RecordingSink();
        var collector = new RunEventCollector(live);
        var at = new DateTimeOffset(2026, 7, 8, 12, 0, 0, TimeSpan.Zero);

        collector.Publish(new FlowEvent
        {
            Timestamp = at, Level = FlowEventLevel.Info, Stage = "source.open",
            Message = "read 'a.csv' (31 row(s))", Rows = 31,
        });
        collector.Publish(new FlowEvent
        {
            Timestamp = at.AddSeconds(1), Level = FlowEventLevel.Warning, Message = "index skipped", ElapsedMs = 12.5,
        });

        Assert.Equal(2, collector.Records.Count);
        var first = collector.Records[0];
        Assert.Equal(at.UtcDateTime, first.TimestampUtc);
        Assert.Equal(RunEventLevels.Info, first.Level);
        Assert.Equal("source.open", first.Step);
        Assert.Equal("read 'a.csv' (31 row(s))", first.Message);
        Assert.Equal(31, first.Rows);
        Assert.Null(first.ElapsedMs);
        var second = collector.Records[1];
        Assert.Equal(RunEventLevels.Warning, second.Level);
        Assert.Null(second.Step);
        Assert.Equal(12.5, second.ElapsedMs);
        // Every event was also forwarded to the live sink (the node's catalog writer), as published.
        Assert.Equal(2, live.Published.Count);
    }

    [Fact]
    public void Collector_WithoutLiveSink_JustCollects()
    {
        var collector = new RunEventCollector();
        collector.Publish(new FlowEvent { Message = "hello" });
        Assert.Equal("hello", Assert.Single(collector.Records).Message);
    }

    [Fact]
    public void Collector_IsThreadSafe()
    {
        var collector = new RunEventCollector();
        Parallel.For(0, 500, i => collector.Publish(new FlowEvent { Message = $"m{i}" }));
        Assert.Equal(500, collector.Records.Count);
    }

    [Fact]
    public void Composite_FansOutToEverySink_InOrder()
    {
        var a = new RecordingSink();
        var b = new RecordingSink();
        var composite = new CompositeFlowEventSink(a, b);

        composite.Publish(new FlowEvent { Message = "one" });

        Assert.Equal("one", Assert.Single(a.Published).Message);
        Assert.Equal("one", Assert.Single(b.Published).Message);
    }

    [Theory]
    [InlineData(FlowEventLevel.Trace, "trace")]
    [InlineData(FlowEventLevel.Debug, "debug")]
    [InlineData(FlowEventLevel.Info, "info")]
    [InlineData(FlowEventLevel.Warning, "warning")]
    [InlineData(FlowEventLevel.Error, "error")]
    public void Levels_HaveStableLowercaseNames(FlowEventLevel level, string expected)
        => Assert.Equal(expected, RunEventLevels.Name(level));
}
