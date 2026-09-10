using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

public sealed class RunLoggerTests
{
    [Fact]
    public void Log_FiltersAboveEnabledLevel()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        logger.Log(RunLogLevel.Info, "a", "kept");
        logger.Log(RunLogLevel.Debug, "b", "dropped");
        logger.Log(RunLogLevel.Trace, "c", "dropped");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal("a", entry.Step);
    }

    [Fact]
    public void Log_TraceLevel_KeepsEverything_InOrder()
    {
        var logger = new RunLogger(RunLogLevel.Trace);
        logger.Log(RunLogLevel.Info, "one", "1");
        logger.Log(RunLogLevel.Debug, "two", "2");
        logger.Log(RunLogLevel.Trace, "three", "3");

        Assert.Equal(["one", "two", "three"], logger.Entries.Select(e => e.Step));
    }

    [Fact]
    public void Echo_ReceivesFormattedLines_OnlyForKeptEntries()
    {
        var lines = new List<string>();
        var logger = new RunLogger(RunLogLevel.Info, lines.Add);
        logger.Log(RunLogLevel.Info, "step.name", "hello");
        logger.Log(RunLogLevel.Trace, "step.sql", "SELECT 1");

        var line = Assert.Single(lines);
        Assert.Contains("INFO", line, StringComparison.Ordinal);
        Assert.Contains("step.name", line, StringComparison.Ordinal);
        Assert.Contains("hello", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_IndentsMultilineMessages()
    {
        var entry = new RunLogEntry
        {
            TimestampUtc = new DateTime(2026, 6, 10, 12, 0, 0, DateTimeKind.Utc),
            Level = RunLogLevel.Trace,
            Step = "upsert.insert",
            Message = "INSERT INTO x\nSELECT 1;",
        };

        var formatted = RunLogger.Format(entry);
        var lines = formatted.Split(Environment.NewLine);
        Assert.StartsWith("2026-06-10 12:00:00.000Z TRACE upsert.insert", lines[0], StringComparison.Ordinal);
        Assert.Contains("INSERT INTO x", lines[0], StringComparison.Ordinal);
        Assert.Equal("    | SELECT 1;", lines[1]);
    }

    [Fact]
    public void Render_JoinsAllEntries()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        logger.Log(RunLogLevel.Info, "a", "first");
        logger.Log(RunLogLevel.Info, "b", "second");

        var text = logger.Render();
        Assert.Contains("first", text, StringComparison.Ordinal);
        Assert.Contains("second", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
    }

    [Fact]
    public void Log_IsThreadSafe()
    {
        var logger = new RunLogger(RunLogLevel.Info);
        Parallel.For(0, 500, i => logger.Log(RunLogLevel.Info, "p", $"m{i}"));
        Assert.Equal(500, logger.Entries.Count);
    }
}
