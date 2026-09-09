using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Export;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Exhaustive, database-free coverage of the export segmentation engine (<see cref="ExportSegmentPlanner"/>): the
/// automatic chunking of an export into per-file SELECTs and file names across every mode - full (F), day (D),
/// month (M), integer key (K) - with the ExportSize grouping, the half-open date / inclusive key predicates, the
/// trailing NULL-rows segment, the timestamp and subfolder composition, the static filter, and the validation
/// errors. The planner is pure and deterministic, so this pins the segmentation behavior precisely without a sink.
/// </summary>
public sealed class ExportSegmentPlannerTests
{
    private static readonly string[] Columns = ["Id", "Amount", "OrderDate"];
    private static readonly DateTime RunTimestamp = new(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ExportFlow Flow(
        string by, int size = 1, string? dateColumn = null, string? keyColumn = null,
        DateOnly? from = null, DateOnly? to = null, bool timestamp = false, string? filter = null,
        string? subfolder = null, string fileName = "out", string? withHint = null, int threads = 0)
        => new()
        {
            FlowId = 1,
            SysAlias = "exp",
            SrcServer = "sink",
            Source = new RelationalObject { Database = "DB", Schema = "dbo", Name = "Orders" },
            ExportBy = by,
            ExportSize = size,
            DateColumn = dateColumn,
            IncrementalColumn = keyColumn,
            FromDate = from,
            ToDate = to,
            AddTimeStampToFileName = timestamp,
            SrcFilter = filter,
            Subfolderpattern = subfolder,
            TrgFileName = fileName,
            SrcWithHint = withHint,
            NoOfThreads = threads,
        };

    [Fact]
    public void Full_ProducesOneFullTableSegment()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F"), Columns, keyMax: 0, RunTimestamp));
        Assert.StartsWith("SELECT [Id], [Amount], [OrderDate] FROM ", segment.Sql);
        Assert.EndsWith("WHERE 1=1", segment.Sql);
        Assert.Contains("Orders", segment.Sql, StringComparison.Ordinal);
        Assert.Equal("out", segment.FileName);
        Assert.Equal(string.Empty, segment.SubFolder);
    }

    [Fact]
    public void Month_ProducesOneSegmentPerMonth_PlusNullRows_HalfOpen()
    {
        var segments = ExportSegmentPlanner.Plan(
            Flow("M", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 3, 31)),
            Columns, keyMax: 0, RunTimestamp);

        Assert.Equal(4, segments.Count); // Jan, Feb, Mar, + NullRows
        Assert.EndsWith("_NullRows", segments[^1].FileName);
        Assert.Contains("[OrderDate] IS NULL", segments[^1].Sql, StringComparison.Ordinal);
        // Half-open date window: [start, nextMonth).
        Assert.Contains("[OrderDate] >= '2024-01-01' AND [OrderDate] < '2024-02-01'", segments[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Day_WithExportSizeTwo_GroupsTwoDaysPerChunk()
    {
        var segments = ExportSegmentPlanner.Plan(
            Flow("D", size: 2, dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 4)),
            Columns, keyMax: 0, RunTimestamp);

        Assert.Equal(3, segments.Count); // (01-01..01-02), (01-03..01-04), NullRows
        Assert.Contains("[OrderDate] >= '2024-01-01' AND [OrderDate] < '2024-01-03'", segments[0].Sql, StringComparison.Ordinal);
        Assert.Equal("out_2024-01-01-2024-01-02", segments[0].FileName);
        Assert.EndsWith("_NullRows", segments[^1].FileName);
    }

    [Fact]
    public void Day_SizeOne_SingleDayFileNameHasNoRange()
    {
        var segments = ExportSegmentPlanner.Plan(
            Flow("D", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 1)),
            Columns, keyMax: 0, RunTimestamp);

        Assert.Equal(2, segments.Count); // one day + NullRows
        Assert.Equal("out_2024-01-01", segments[0].FileName);
    }

    [Fact]
    public void Key_ProducesInclusiveZeroPaddedRanges_PlusNullRows()
    {
        var segments = ExportSegmentPlanner.Plan(Flow("K", size: 100, keyColumn: "Id"), Columns, keyMax: 250, RunTimestamp);

        Assert.EndsWith("_NullRows", segments[^1].FileName);
        Assert.Contains("[Id] IS NULL", segments[^1].Sql, StringComparison.Ordinal);
        Assert.Contains("[Id] >= 0 AND [Id] <= ", segments[0].Sql, StringComparison.Ordinal); // inclusive key window
        Assert.Matches(@"^out_\d{3}-\d{3}$", segments[0].FileName);                            // padded to 250's width
    }

    [Fact]
    public void Full_WithIntegerKeyRange_SplitsIntoContiguousInclusiveRanges_PlusNullRows()
    {
        var range = new FullExportKeyRange { Column = "Id", Kind = FullExportKeyKind.Whole, Min = 0L, Max = 100L };
        var segments = ExportSegmentPlanner.Plan(Flow("F", threads: 4), Columns, keyMax: 0, RunTimestamp, range);

        Assert.Equal(5, segments.Count); // four thread ranges + NullRows
        Assert.Contains("[Id] >= 0 AND [Id] <= 24", segments[0].Sql, StringComparison.Ordinal);
        Assert.Contains("[Id] >= 25 AND [Id] <= 49", segments[1].Sql, StringComparison.Ordinal);
        Assert.Contains("[Id] >= 50 AND [Id] <= 74", segments[2].Sql, StringComparison.Ordinal);
        Assert.Contains("[Id] >= 75 AND [Id] <= 100", segments[3].Sql, StringComparison.Ordinal);
        Assert.Equal("out_000-024", segments[0].FileName); // padded to 100's width, K-style
        Assert.EndsWith("_NullRows", segments[^1].FileName);
        Assert.Contains("[Id] IS NULL", segments[^1].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Full_WithDateTimeKeyRange_UsesHalfOpenRanges_LastClosedAtMax()
    {
        var range = new FullExportKeyRange
        {
            Column = "OrderDate",
            Kind = FullExportKeyKind.DateTime,
            Min = new DateTime(2024, 1, 1),
            Max = new DateTime(2024, 1, 5),
        };
        var segments = ExportSegmentPlanner.Plan(Flow("F", threads: 2), Columns, keyMax: 0, RunTimestamp, range);

        Assert.Equal(3, segments.Count); // two thread ranges + NullRows
        Assert.Contains(
            "[OrderDate] >= '2024-01-01T00:00:00.0000000' AND [OrderDate] < '2024-01-03T00:00:00.0000000'",
            segments[0].Sql, StringComparison.Ordinal);
        Assert.Contains(
            "[OrderDate] >= '2024-01-03T00:00:00.0000000' AND [OrderDate] <= '2024-01-05T00:00:00.0000000'",
            segments[1].Sql, StringComparison.Ordinal);
        Assert.Equal("out_20240101000000-20240103000000", segments[0].FileName);
        Assert.EndsWith("_NullRows", segments[^1].FileName);
    }

    [Fact]
    public void Full_WithKeyRange_ButSingleThread_StaysOneSegment()
    {
        var range = new FullExportKeyRange { Column = "Id", Kind = FullExportKeyKind.Whole, Min = 0L, Max = 100L };
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", threads: 1), Columns, keyMax: 0, RunTimestamp, range));
        Assert.EndsWith("WHERE 1=1", segment.Sql);
        Assert.Equal("out", segment.FileName);
    }

    [Fact]
    public void Full_WithCollapsedKeyRange_EmitsOneRange_PlusNullRows()
    {
        var range = new FullExportKeyRange { Column = "Id", Kind = FullExportKeyKind.Whole, Min = 7L, Max = 7L };
        var segments = ExportSegmentPlanner.Plan(Flow("F", threads: 4), Columns, keyMax: 0, RunTimestamp, range);

        Assert.Equal(2, segments.Count); // the whole (single-value) range + NullRows
        Assert.Contains("[Id] >= 7 AND [Id] <= 7", segments[0].Sql, StringComparison.Ordinal);
        Assert.EndsWith("_NullRows", segments[^1].FileName);
    }

    [Fact]
    public void Timestamp_IsAppendedToFileName_WhenEnabled()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", timestamp: true), Columns, keyMax: 0, RunTimestamp));
        Assert.Equal("out_20240115120000", segment.FileName);
    }

    [Fact]
    public void SrcFilter_IsAppendedToEverySegmentWhere()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", filter: "AND [Region] = 'EU'"), Columns, keyMax: 0, RunTimestamp));
        Assert.Contains("WHERE 1=1 AND [Region] = 'EU'", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Subfolder_PatternBecomesADatePath()
    {
        var segments = ExportSegmentPlanner.Plan(
            Flow("M", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 31), subfolder: "YYYY/MM"),
            Columns, keyMax: 0, RunTimestamp);

        Assert.Equal("2024/01/", segments[0].SubFolder);
    }

    [Fact]
    public void FileName_DefaultsToSourceName_WhenTargetNameBlank()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", fileName: ""), Columns, keyMax: 0, RunTimestamp));
        Assert.Equal("Orders", segment.FileName);
    }

    [Fact]
    public void ColumnIdentifiers_AreBracketEscaped()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F"), ["Wei]rd"], keyMax: 0, RunTimestamp));
        Assert.Contains("[Wei]]rd]", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Month_WithoutDateColumn_Throws()
        => Assert.Throws<SqlFlowException>(() => ExportSegmentPlanner.Plan(Flow("M"), Columns, keyMax: 0, RunTimestamp));

    [Fact]
    public void Key_WithoutIncrementalColumn_Throws()
        => Assert.Throws<SqlFlowException>(() => ExportSegmentPlanner.Plan(Flow("K"), Columns, keyMax: 100, RunTimestamp));

    [Fact]
    public void SrcWithHint_EmitsWithClauseAfterTable()
    {
        var segment = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", withHint: "NOLOCK"), Columns, keyMax: 0, RunTimestamp));
        Assert.Contains("].[Orders] WITH (NOLOCK) WHERE 1=1", segment.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("NOLOCK")]
    [InlineData("(NOLOCK)")]
    [InlineData("WITH (NOLOCK)")]
    [InlineData("  with  ( nolock )  ")]
    public void SrcWithHint_NormalizesCommonSpellingsToOneWithClause(string spelling)
    {
        var sql = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", withHint: spelling), Columns, keyMax: 0, RunTimestamp)).Sql;
        // Exactly one WITH clause, no doubled parentheses or nested WITH.
        Assert.Equal(1, CountOccurrences(sql, "WITH ("));
        Assert.DoesNotContain("WITH (WITH", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("((", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SrcWithHint_PreservesMultipleHints()
    {
        var sql = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", withHint: "NOLOCK, INDEX(IX_Orders)"), Columns, keyMax: 0, RunTimestamp)).Sql;
        Assert.Contains("WITH (NOLOCK, INDEX(IX_Orders))", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SrcWithHint_BlankEmitsNoHint(string? hint)
    {
        var sql = Assert.Single(ExportSegmentPlanner.Plan(Flow("F", withHint: hint), Columns, keyMax: 0, RunTimestamp)).Sql;
        Assert.DoesNotContain("WITH (", sql, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
