using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Core.Tests;

/// <summary>
/// Reading a file's BUSINESS timestamp out of its name or path, to second granularity.
///
/// This exists because an object store's last-modified time is a property of the storage, not of the data: a
/// server-side copy between accounts rewrites it and cannot set it back. A pipeline that watermarks on it replays
/// its entire history the first time the lake is moved, and cannot express a backfill window at all. A stamp read
/// from the file's own name is stable across copies, tier moves and re-uploads, so the watermark, the stored
/// FileDate_DW provenance and a --from/--to backfill all agree on one clock.
/// </summary>
public sealed class FileDateTimestampTests
{
    private static FileDateSpec Spec(string pattern, string from = "name")
        => FileDateSpec.FromOptions(new Dictionary<string, string?>
        {
            ["fileDate.from"] = from,
            ["fileDate.pattern"] = pattern,
        })!;

    [Fact]
    public void DateOnlyName_YieldsMidnight()
    {
        var spec = Spec(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})");
        Assert.Equal(
            new DateTime(2021, 4, 30, 0, 0, 0, DateTimeKind.Utc),
            spec.ExtractTimestamp("PassengerInOut_20210430.csv"));
    }

    [Fact]
    public void FullTimestampName_YieldsTheSecond()
    {
        var spec = Spec(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_(?<hour>\d{2})(?<minute>\d{2})(?<second>\d{2})");
        Assert.Equal(
            new DateTime(2026, 8, 2, 7, 5, 30, DateTimeKind.Utc),
            spec.ExtractTimestamp("export_20260802_070530.csv"));
    }

    [Fact]
    public void FullTimestamp_IsAOneSecondInterval_SoAWindowBoundedAtThatSecondStillOverlaps()
    {
        var spec = Spec(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_(?<hour>\d{2})(?<minute>\d{2})(?<second>\d{2})");
        var interval = spec.Extract("export_20260802_070530.csv");

        Assert.NotNull(interval);
        Assert.Equal(new DateTime(2026, 8, 2, 7, 5, 30, DateTimeKind.Utc), interval!.Value.Lo);
        Assert.True(interval.Value.Hi > interval.Value.Lo);
        Assert.True(interval.Value.Hi < new DateTime(2026, 8, 2, 7, 5, 31, DateTimeKind.Utc));
    }

    [Theory]
    [InlineData(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_(?<hour>\d{2})(?<minute>\d{2})", "x_20260802_0705.csv", 7, 5, 0)]
    [InlineData(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_(?<hour>\d{2})", "x_20260802_07.csv", 7, 0, 0)]
    public void PartialTimeComponents_FillDownwardsWithZero(string pattern, string name, int h, int mi, int s)
    {
        var spec = Spec(pattern);
        Assert.Equal(new DateTime(2026, 8, 2, h, mi, s, DateTimeKind.Utc), spec.ExtractTimestamp(name));
    }

    [Fact]
    public void PositionalGroups_ReadYearMonthDayHourMinuteSecondInOrder()
    {
        var spec = Spec(@"(\d{4})(\d{2})(\d{2})_(\d{2})(\d{2})(\d{2})");
        Assert.Equal(
            new DateTime(2026, 8, 2, 7, 5, 30, DateTimeKind.Utc),
            spec.ExtractTimestamp("export_20260802_070530.csv"));
    }

    [Fact]
    public void HiveTokens_CarryMinuteAndSecondToo()
    {
        var spec = FileDateSpec.FromOptions(new Dictionary<string, string?>
        {
            ["fileDate.from"] = "path",
            ["fileDate.hive"] = "true",
        })!;

        Assert.Equal(
            new DateTime(2026, 8, 2, 7, 5, 30, DateTimeKind.Utc),
            spec.ExtractTimestamp("raw/year=2026/month=08/day=02/hour=07/minute=05/second=30/part.csv"));
    }

    [Fact]
    public void AnUndatedName_YieldsNull_SoTheCallerCanFallBackToModified()
    {
        var spec = Spec(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})");
        Assert.Null(spec.ExtractTimestamp("no_date_here.csv"));
    }

    [Theory]
    [InlineData("x_20260802_250000.csv")] // hour 25
    [InlineData("x_20260802_076000.csv")] // minute 60
    [InlineData("x_20260802_070560.csv")] // second 60
    public void OutOfRangeTimeComponents_YieldNullRatherThanAWrongInstant(string name)
    {
        var spec = Spec(@"(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})_(?<hour>\d{2})(?<minute>\d{2})(?<second>\d{2})");
        Assert.Null(spec.ExtractTimestamp(name));
    }

    [Fact]
    public void CoarsePartitions_StillWiden_SoPruningRemainsCorrect()
    {
        var spec = FileDateSpec.FromOptions(new Dictionary<string, string?>
        {
            ["fileDate.from"] = "path",
            ["fileDate.hive"] = "true",
        })!;

        var year = spec.Extract("raw/year=2025/part.csv");
        Assert.NotNull(year);
        Assert.Equal(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), year!.Value.Lo);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(-1), year.Value.Hi);
    }
}
