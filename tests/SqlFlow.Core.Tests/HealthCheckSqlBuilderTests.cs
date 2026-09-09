using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The two statements a health check generates: the multi-metric series SELECT (one scan for every
/// metric, NULL-date guard, optional filter, bracket-escaped identifiers) and the data-quality probe.</summary>
public sealed class HealthCheckSqlBuilderTests
{
    private static HealthCheckFlow Flow(
        string dateColumn = "OrderDate", string? filter = null, params (string Name, string Expression)[] metrics) => new()
    {
        FlowId = 1,
        SysAlias = "orders-watch",
        Server = "dwh",
        Target = RelationalObject.Parse("DW.dbo.Orders"),
        DateColumn = dateColumn,
        Metrics = metrics.Length == 0
            ? [new HealthCheckMetric { Name = "rowCount", Expression = "COUNT(*)" }]
            : metrics.Select(m => new HealthCheckMetric { Name = m.Name, Expression = m.Expression }).ToList(),
        FilterCriteria = filter,
    };

    private static readonly DateTime AsOf = new(2026, 6, 12);

    [Fact]
    public void SeriesSelect_SingleMetric_BoundedToTheSaneWindow()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(Flow(), AsOf);

        var expected =
            $"SELECT [OrderDate] AS [Date], CAST(COUNT(*) AS float) AS [rowCount]{Environment.NewLine}" +
            $"FROM [DW].[dbo].[Orders]{Environment.NewLine}" +
            $"WHERE 1 = 1{Environment.NewLine}" +
            $"  AND [OrderDate] >= '1990-01-01'{Environment.NewLine}" +
            $"  AND [OrderDate] <= '2026-06-12'{Environment.NewLine}" +
            $"GROUP BY [OrderDate]{Environment.NewLine}" +
            "ORDER BY [OrderDate];";
        Assert.Equal(expected, sql);
    }

    [Fact]
    public void SeriesSelect_MultiMetric_OneScanManyColumns()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(Flow(metrics:
            [("orders", "COUNT(*)"), ("revenue", "SUM(Amount)"), ("buyers", "COUNT(DISTINCT CustomerID)")]), AsOf);

        Assert.Contains(
            "SELECT [OrderDate] AS [Date], CAST(COUNT(*) AS float) AS [orders], CAST(SUM(Amount) AS float) AS [revenue], CAST(COUNT(DISTINCT CustomerID) AS float) AS [buyers]",
            sql, StringComparison.Ordinal);

        // One scan: exactly one FROM.
        Assert.Equal(1, sql.Split("FROM ").Length - 1);
    }

    [Fact]
    public void SeriesSelect_AndsTheFilterIn_AndHonorsTheSentinelFloor()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(
            Flow(filter: "OrderStatus <> 'Cancelled'") with { SentinelDateFloor = new DateOnly(2000, 5, 1) }, AsOf);

        Assert.Contains($"  AND [OrderDate] >= '2000-05-01'{Environment.NewLine}  AND [OrderDate] <= '2026-06-12'{Environment.NewLine}  AND (OrderStatus <> 'Cancelled'){Environment.NewLine}GROUP BY",
            sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesSelect_EscapesAClosingBracketInIdentifiers()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(Flow(dateColumn: "Order]Date"), AsOf);

        Assert.Contains("[Order]]Date] AS [Date]", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY [Order]]Date]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataQualitySelect_CountsTheThreeDatePathologies()
    {
        var sql = HealthCheckSqlBuilder.DataQualitySelect(
            Flow(filter: "Region = 'EU'") with { SentinelDateFloor = new DateOnly(1995, 6, 1) },
            asOfDate: new DateTime(2026, 6, 12));

        Assert.Contains("COUNT_BIG(CASE WHEN [OrderDate] > '2026-06-12' THEN 1 END) AS [FutureDatedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG(CASE WHEN [OrderDate] < '1995-06-01' THEN 1 END) AS [SentinelDatedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("COUNT_BIG(CASE WHEN [OrderDate] IS NULL THEN 1 END) AS [NullDatedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("AND (Region = 'EU')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT NULL", sql, StringComparison.Ordinal); // NULL dates are the point here
    }
}
