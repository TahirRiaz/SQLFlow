using SqlFlow.Core;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class InitLoadPlannerTests
{
    private static RelationalObject Source() => new() { Database = "db", Schema = "dbo", Name = "Orders" };

    private static IngestionFlow Flow(InitLoadPolicy initLoad, IncrementalPolicy? incremental = null)
        => new()
        {
            FlowId = 1,
            Source = new IngestionSource { Server = "s", Table = Source() },
            Target = new IngestionTarget { Server = "t", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = "OrdersDw" } },
            InitLoad = initLoad,
            Incremental = incremental ?? new IncrementalPolicy(),
        };

    private static IReadOnlyList<SourceProjection> Cols(params string[] names)
        => names.Select(n => new SourceProjection { SourceName = n, TargetName = n }).ToList();

    [Fact]
    public void Month_ProducesCalendarAlignedHalfOpenSegments_PlusNull()
    {
        var flow = Flow(
            new InitLoadPolicy { Enabled = true, BatchBy = "M", BatchSize = 1, FromDate = new DateOnly(2023, 11, 15), ToDate = new DateOnly(2024, 2, 10) },
            new IncrementalPolicy { DateColumn = "OrderDate" });

        var segments = InitLoadPlanner.Plan(flow, Source(), Cols("Id", "OrderDate", "Amt"));

        Assert.Equal(5, segments.Count);
        Assert.Equal(" AND ([OrderDate] >= '2023-11-15' AND [OrderDate] < '2023-12-01')", segments[0].WhereClause);
        Assert.Equal(" AND ([OrderDate] >= '2023-12-01' AND [OrderDate] < '2024-01-01')", segments[1].WhereClause);
        Assert.Equal(" AND ([OrderDate] >= '2024-01-01' AND [OrderDate] < '2024-02-01')", segments[2].WhereClause);
        Assert.Equal(" AND ([OrderDate] >= '2024-02-01' AND [OrderDate] < '2024-02-11')", segments[3].WhereClause);
        Assert.Equal(" AND ([OrderDate] IS NULL)", segments[4].WhereClause);
        Assert.Equal(
            "SELECT [Id], [OrderDate], [Amt] FROM [dbo].[Orders] WHERE 1=1 AND ([OrderDate] >= '2023-11-15' AND [OrderDate] < '2023-12-01')",
            segments[0].Sql);
    }

    [Fact]
    public void VirtualProjection_IsReadAsItsExpressionAliasedToItsName()
    {
        var flow = Flow(new InitLoadPolicy { Enabled = true, BatchBy = "K", BatchSize = 100, KeyColumn = "OrderId", KeyMaxValue = 50 });
        IReadOnlyList<SourceProjection> projections =
        [
            new SourceProjection { SourceName = "OrderId", TargetName = "OrderId" },
            new SourceProjection { SourceName = "Region_DW", TargetName = "Region_DW", Expression = "UPPER([Region])" },
        ];

        var segments = InitLoadPlanner.Plan(flow, Source(), projections);

        Assert.StartsWith("SELECT [OrderId], UPPER([Region]) AS [Region_DW] FROM [dbo].[Orders] WHERE 1=1", segments[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_ProducesInclusiveBuckets_PlusNull()
    {
        var flow = Flow(new InitLoadPolicy { Enabled = true, BatchBy = "K", BatchSize = 100000, KeyColumn = "OrderId", KeyMaxValue = 250000 });

        var segments = InitLoadPlanner.Plan(flow, Source(), Cols("OrderId", "Amt"));

        Assert.Equal(4, segments.Count);
        Assert.Equal(" AND ([OrderId] >= 0 AND [OrderId] <= 99999)", segments[0].WhereClause);
        Assert.Equal(" AND ([OrderId] >= 100000 AND [OrderId] <= 199999)", segments[1].WhereClause);
        Assert.Equal(" AND ([OrderId] >= 200000 AND [OrderId] <= 250000)", segments[2].WhereClause);
        Assert.Equal(" AND ([OrderId] IS NULL)", segments[3].WhereClause);
    }

    [Fact]
    public void UnknownUnit_ProducesNoSegments()
        => Assert.Empty(InitLoadPlanner.Plan(Flow(new InitLoadPolicy { Enabled = true, BatchBy = "X" }), Source(), Cols("Id")));

    [Fact]
    public void DateModeWithoutDateColumn_Throws()
        => Assert.Throws<SqlFlowException>(() => InitLoadPlanner.Plan(Flow(new InitLoadPolicy { Enabled = true, BatchBy = "M" }), Source(), Cols("Id")));

    [Fact]
    public void KeyModeWithoutKeyColumn_Throws()
        => Assert.Throws<SqlFlowException>(() => InitLoadPlanner.Plan(Flow(new InitLoadPolicy { Enabled = true, BatchBy = "K", KeyMaxValue = 100 }), Source(), Cols("Id")));

    [Fact]
    public void Key_BadBounds_StillAppendsNullSegment()
    {
        // A non-positive bucket size yields no buckets, but the IS NULL key segment is still emitted (legacy).
        var flow = Flow(new InitLoadPolicy { Enabled = true, BatchBy = "K", KeyColumn = "OrderId", KeyMaxValue = 100, BatchSize = 0 });
        var segments = InitLoadPlanner.Plan(flow, Source(), Cols("OrderId"));
        Assert.Equal(" AND ([OrderId] IS NULL)", Assert.Single(segments).WhereClause);
    }
}
