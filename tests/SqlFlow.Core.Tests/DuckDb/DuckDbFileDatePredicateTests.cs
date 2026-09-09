using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.DuckDb;
using Xunit;

namespace SqlFlow.Tests.DuckDb;

/// <summary>
/// The fileDate window pushed down onto Hive partition columns: a partition-date predicate at the scan level (so
/// DuckDB prunes folders), the granularity widening from day to month to year, and the cases that do not push down
/// (name/modified date, non-partition-aware formats).
/// </summary>
public sealed class DuckDbFileDatePredicateTests
{
    private static SourceSpec Source(string? location, params (string Key, string? Value)[] options)
        => new()
        {
            Type = "duckdb",
            Location = location,
            Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        };

    [Fact]
    public void HivePath_DayPartitions_ComposesADayPredicate()
    {
        var predicate = DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"),
            ("initFromFileDate", "2025-01-01"), ("initToFileDate", "2025-12-31")));

        Assert.Equal(
            "make_date(CAST(\"year\" AS INTEGER), CAST(\"month\" AS INTEGER), CAST(\"day\" AS INTEGER)) >= DATE '2025-01-01' "
            + "AND make_date(CAST(\"year\" AS INTEGER), CAST(\"month\" AS INTEGER), CAST(\"day\" AS INTEGER)) <= DATE '2025-12-31'",
            predicate);
    }

    [Fact]
    public void MonthPartitions_UseLastDayForTheUpperBound()
    {
        var predicate = DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"),
            ("fileDate.partitions", "year,month"),
            ("initFromFileDate", "2025-02-01")));

        Assert.Equal(
            "last_day(make_date(CAST(\"year\" AS INTEGER), CAST(\"month\" AS INTEGER), 1)) >= DATE '2025-02-01'",
            predicate);
    }

    [Fact]
    public void YearPartitions_SpanTheWholeYear()
    {
        var predicate = DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"),
            ("fileDate.partitions", "year"),
            ("initToFileDate", "2025-12-31")));

        Assert.Equal("make_date(CAST(\"year\" AS INTEGER), 1, 1) <= DATE '2025-12-31'", predicate);
    }

    [Fact]
    public void IncrementalWatermark_TakesTheMoreRestrictiveLowerBound()
    {
        var predicate = DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"),
            ("fileDate.partitions", "year"),
            ("initFromFileDate", "2025-01-01"), ("incrementalAfterDate", "2025-06-01")));

        Assert.Equal("make_date(CAST(\"year\" AS INTEGER), 12, 31) >= DATE '2025-06-01'", predicate);
    }

    [Fact]
    public void NoWindow_IsNoPredicate()
        => Assert.Null(DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"))));

    [Fact]
    public void NameDate_DoesNotPushDown()
        => Assert.Null(DuckDbQuery.FileDatePredicate(Source("/lake/*.parquet",
            ("fileDate.from", "name"), ("fileDate.pattern", @"(?<year>\d{4})"), ("initFromFileDate", "2025-01-01"))));

    [Fact]
    public void CsvFormat_DoesNotPushDown()
        => Assert.Null(DuckDbQuery.FileDatePredicate(Source("/lake/**/*.csv",
            ("fileDate.from", "path"), ("fileDate.hive", "true"), ("initFromFileDate", "2025-01-01"))));

    [Fact]
    public void HiveFileDate_ImplicitlyEnablesHivePartitioningOnTheScan()
        => Assert.Contains("hive_partitioning = true",
            DuckDbQuery.BuildRelation(Source("/lake/**/*.parquet", ("fileDate.from", "path"), ("fileDate.hive", "true"))),
            StringComparison.Ordinal);

    [Fact]
    public void DayWithoutMonth_Throws()
        => Assert.Throws<SqlFlowException>(() => DuckDbQuery.FileDatePredicate(Source("/lake/**/*.parquet",
            ("fileDate.from", "path"), ("fileDate.hive", "true"),
            ("fileDate.partitions", "year,day"), ("initFromFileDate", "2025-01-01"))));
}
