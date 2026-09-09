using SqlFlow.Core;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The fileDate parser and its path/name date extraction: Hive partition tokens and regex patterns, the interval
/// widening that keeps coarse partitions correct, and the validation that rejects contradictory or empty config.
/// </summary>
public sealed class FileDateSpecTests
{
    private static IReadOnlyDictionary<string, string?> Options(params (string Key, string? Value)[] pairs)
        => pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0, int s = 0)
        => new(y, mo, d, h, mi, s, DateTimeKind.Utc);

    [Fact]
    public void NoFileDate_IsNull()
        => Assert.Null(FileDateSpec.FromOptions(Options()));

    [Fact]
    public void ModifiedSource_IsNull()
        => Assert.Null(FileDateSpec.FromOptions(Options(("fileDate.from", "modified"))));

    [Fact]
    public void HivePath_FullDate_IsASingleDay()
    {
        var spec = FileDateSpec.FromOptions(Options(("fileDate.from", "path"), ("fileDate.hive", "true")))!;

        var interval = spec.Extract("/lake/orders/year=2025/month=03/day=05/part-0.parquet");

        Assert.NotNull(interval);
        Assert.Equal(Utc(2025, 3, 5), interval!.Value.Lo);
        Assert.Equal(Utc(2025, 3, 6).AddTicks(-1), interval.Value.Hi);
    }

    [Fact]
    public void HivePath_YearAndMonth_SpansTheWholeMonth()
    {
        var spec = FileDateSpec.FromOptions(Options(("fileDate.from", "path"), ("fileDate.hive", "true")))!;

        var interval = spec.Extract("/lake/year=2025/month=02/")!.Value;

        Assert.Equal(Utc(2025, 2, 1), interval.Lo);
        Assert.Equal(Utc(2025, 3, 1).AddTicks(-1), interval.Hi);
    }

    [Fact]
    public void HivePath_YearOnly_SpansTheWholeYear()
    {
        var spec = FileDateSpec.FromOptions(Options(("fileDate.from", "path"), ("fileDate.hive", "true")))!;

        var interval = spec.Extract("/lake/year=2024/")!.Value;

        Assert.Equal(Utc(2024, 1, 1), interval.Lo);
        Assert.Equal(Utc(2025, 1, 1).AddTicks(-1), interval.Hi);
    }

    [Fact]
    public void HivePath_InvalidMonth_IsNoDate()
    {
        var spec = FileDateSpec.FromOptions(Options(("fileDate.from", "path"), ("fileDate.hive", "true")))!;
        Assert.Null(spec.Extract("/lake/year=2025/month=13/"));
    }

    [Fact]
    public void HivePath_NoTokens_IsNoDate()
    {
        var spec = FileDateSpec.FromOptions(Options(("fileDate.from", "path"), ("fileDate.hive", "true")))!;
        Assert.Null(spec.Extract("/lake/orders/plain/part-0.parquet"));
    }

    [Fact]
    public void NamePattern_NamedGroups_ParseTheFileName()
    {
        var spec = FileDateSpec.FromOptions(Options(
            ("fileDate.from", "name"),
            ("fileDate.pattern", @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})")))!;

        var interval = spec.Extract("orders_2025-03-01.csv")!.Value;

        Assert.Equal(Utc(2025, 3, 1), interval.Lo);
        Assert.Equal(Utc(2025, 3, 2).AddTicks(-1), interval.Hi);
    }

    [Fact]
    public void PathPattern_PositionalGroups_ReadAsYearThenMonth()
    {
        var spec = FileDateSpec.FromOptions(Options(
            ("fileDate.from", "path"),
            ("fileDate.pattern", @"year=(\d{4})/month=(\d{2})")))!;

        var interval = spec.Extract("/lake/year=2025/month=07/part.parquet")!.Value;

        Assert.Equal(Utc(2025, 7, 1), interval.Lo);
        Assert.Equal(Utc(2025, 8, 1).AddTicks(-1), interval.Hi);
    }

    [Fact]
    public void Pattern_NoMatch_IsNoDate()
    {
        var spec = FileDateSpec.FromOptions(Options(
            ("fileDate.from", "name"),
            ("fileDate.pattern", @"(?<year>\d{4})-(?<month>\d{2})-(?<day>\d{2})")))!;

        Assert.Null(spec.Extract("orders_no_date.csv"));
    }

    [Fact]
    public void HiveAndPattern_Together_Throws()
        => Assert.Throws<SqlFlowException>(() => FileDateSpec.FromOptions(Options(
            ("fileDate.from", "path"), ("fileDate.hive", "true"), ("fileDate.pattern", @"(\d{4})"))));

    [Fact]
    public void PathWithoutHiveOrPattern_Throws()
        => Assert.Throws<SqlFlowException>(() => FileDateSpec.FromOptions(Options(("fileDate.from", "path"))));

    [Fact]
    public void HiveWithNameSource_Throws()
        => Assert.Throws<SqlFlowException>(() => FileDateSpec.FromOptions(Options(
            ("fileDate.from", "name"), ("fileDate.hive", "true"))));

    [Fact]
    public void UnknownFromValue_Throws()
        => Assert.Throws<SqlFlowException>(() => FileDateSpec.FromOptions(Options(("fileDate.from", "sideways"))));

    [Fact]
    public void InvalidPatternRegex_Throws()
        => Assert.Throws<SqlFlowException>(() => FileDateSpec.FromOptions(Options(
            ("fileDate.from", "name"), ("fileDate.pattern", "[unterminated"))));
}
