using System.Data;
using System.Globalization;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Export;
using SqlFlow.Core.Export.Legacy;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Export;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for the export feature area: the segment planner
/// (<see cref="ExportSegmentPlanner"/>), the legacy-row to <see cref="ExportFlow"/> mapper
/// (<see cref="ExportFlowMapper"/>), and the file writers (<see cref="CsvExportFileWriter"/>,
/// <see cref="ParquetExportFileWriter"/>, <see cref="ExportFileWriterFactory"/>). These cases probe calendar
/// and key boundaries (year roll, leap February, inverted windows, padding widths), token and identifier
/// escaping corners, filter trimming, and writer formatting that the existing Export tests do not exercise.
/// Everything is pure and in-memory, so the suite is deterministic and always runs without a sink.
/// </summary>
public sealed class ExportEdgeCaseTests
{
    private static readonly string[] EdgeColumns = ["Id", "Amount", "OrderDate"];
    private static readonly DateTime EdgeRunTimestamp = new(2024, 1, 15, 12, 0, 0, DateTimeKind.Utc);

    private static ExportFlow EdgeFlow(
        string by, int size = 1, string? dateColumn = null, string? keyColumn = null,
        DateOnly? from = null, DateOnly? to = null, bool timestamp = false, string? filter = null,
        string? subfolder = null, string fileName = "out")
        => new()
        {
            FlowId = 7,
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
        };

    private static IReadOnlyList<ExportSegment> EdgePlan(ExportFlow flow, int keyMax = 0, IReadOnlyList<string>? columns = null)
        => ExportSegmentPlanner.Plan(flow, columns ?? EdgeColumns, keyMax, EdgeRunTimestamp);

    private static LegacyExportRow EdgeRow() => new()
    {
        FlowID = 9,
        SysAlias = "sys",
        srcServer = "src",
        srcDBSchTbl = "[DB].[dbo].[Orders]",
    };

    // ----------------------------------------------------------------------------------------------------
    // Planner: date-window boundaries (year roll, leap year, non-leap February, multi-month grouping).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Month_WindowCrossesYearBoundary_ProducesAMonthPerCalendarMonth()
    {
        // Dec 2023, Jan 2024, Feb 2024, then the trailing NULL-rows segment.
        var segments = EdgePlan(EdgeFlow("M", dateColumn: "OrderDate", from: new DateOnly(2023, 12, 1), to: new DateOnly(2024, 2, 29)));

        Assert.Equal(4, segments.Count);
        // The first month's half-open upper bound rolls the year forward.
        Assert.Contains("[OrderDate] >= '2023-12-01' AND [OrderDate] < '2024-01-01'", segments[0].Sql, StringComparison.Ordinal);
        Assert.Equal("out_2023-12-01-2023-12-31", segments[0].FileName);
        // The leap-February month ends on the 29th and its upper bound rolls to March.
        Assert.Equal("out_2024-02-01-2024-02-29", segments[2].FileName);
        Assert.Contains("[OrderDate] < '2024-03-01'", segments[2].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Day_LeapDay_SingleFileWithHalfOpenUpperBoundInMarch()
    {
        var segments = EdgePlan(EdgeFlow("D", dateColumn: "OrderDate", from: new DateOnly(2024, 2, 29), to: new DateOnly(2024, 2, 29)));

        Assert.Equal(2, segments.Count); // the leap day + NullRows
        Assert.Equal("out_2024-02-29", segments[0].FileName);
        Assert.Contains("[OrderDate] >= '2024-02-29' AND [OrderDate] < '2024-03-01'", segments[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Month_NonLeapFebruary_EndsOnThe28th()
    {
        var segments = EdgePlan(EdgeFlow("M", dateColumn: "OrderDate", from: new DateOnly(2023, 2, 1), to: new DateOnly(2023, 2, 28)));

        Assert.Equal("out_2023-02-01-2023-02-28", segments[0].FileName);
        Assert.Contains("[OrderDate] < '2023-03-01'", segments[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Month_GroupingTwoMonths_SpansIntoLeapFebruaryInOneChunk()
    {
        // ExportSize 2 anchors the chunk end on the second month (Feb 2024 -> the 29th).
        var segments = EdgePlan(EdgeFlow("M", size: 2, dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 4, 30)));

        Assert.Equal(3, segments.Count); // (Jan-Feb), (Mar-Apr), NullRows
        Assert.Equal("out_2024-01-01-2024-02-29", segments[0].FileName);
        Assert.Contains("[OrderDate] < '2024-03-01'", segments[0].Sql, StringComparison.Ordinal);
        Assert.Equal("out_2024-03-01-2024-04-30", segments[1].FileName);
    }

    [Fact]
    public void Day_GroupingWiderThanWindow_ClampsToASingleRangeChunk()
    {
        // A 10-day chunk over a 3-day window collapses to one clamped range plus NullRows.
        var segments = EdgePlan(EdgeFlow("D", size: 10, dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 3)));

        Assert.Equal(2, segments.Count);
        Assert.Equal("out_2024-01-01-2024-01-03", segments[0].FileName);
        Assert.Contains("[OrderDate] >= '2024-01-01' AND [OrderDate] < '2024-01-04'", segments[0].Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("D")]
    [InlineData("M")]
    public void InvertedDateWindow_YieldsOnlyTheNullRowsSegment(string by)
    {
        // FromDate after ToDate: no calendar chunks, but the IS NULL segment is still emitted.
        var segment = Assert.Single(EdgePlan(EdgeFlow(by, dateColumn: "OrderDate", from: new DateOnly(2024, 3, 1), to: new DateOnly(2024, 1, 31))));

        Assert.Equal("out_NullRows", segment.FileName);
        Assert.Contains("[OrderDate] IS NULL", segment.Sql, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------
    // Planner: integer-key boundaries (zero, negative, empty bucketing, inclusive upper, padding width).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Key_KeyMaxZero_ProducesTheZeroBucketPlusNullRows()
    {
        var segments = EdgePlan(EdgeFlow("K", size: 100, keyColumn: "Id"), keyMax: 0);

        Assert.Equal(2, segments.Count); // bucket (0,0) + NullRows
        Assert.Equal("out_0-0", segments[0].FileName); // width clamps to 1 when keyMax is 0
        Assert.Contains("[Id] >= 0 AND [Id] <= 0", segments[0].Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Key_NonPositiveExportSize_YieldsOnlyTheNullRowsSegment(int size)
    {
        // A non-positive bucket size produces no key buckets; only the IS NULL segment remains.
        var segment = Assert.Single(EdgePlan(EdgeFlow("K", size: size, keyColumn: "Id"), keyMax: 250));

        Assert.Equal("out_NullRows", segment.FileName);
        Assert.Contains("[Id] IS NULL", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Key_NegativeKeyMax_YieldsOnlyTheNullRowsSegment()
    {
        // An inverted [0, keyMax] range (keyMax below the start) produces no buckets.
        var segment = Assert.Single(EdgePlan(EdgeFlow("K", size: 10, keyColumn: "Id"), keyMax: -3));

        Assert.Equal("out_NullRows", segment.FileName);
    }

    [Fact]
    public void Key_LastBucketUpperBound_IsTheInclusiveKeyMax()
    {
        var segments = EdgePlan(EdgeFlow("K", size: 100, keyColumn: "Id"), keyMax: 250);

        // The final non-null bucket closes exactly on keyMax, inclusively.
        var lastBucket = segments[^2];
        Assert.Contains("[Id] >= 200 AND [Id] <= 250", lastBucket.Sql, StringComparison.Ordinal);
        Assert.Equal("out_200-250", lastBucket.FileName);
    }

    [Theory]
    [InlineData(9, "out_0-9")]      // single digit -> width 1
    [InlineData(10, "out_00-09")]   // ten -> width 2, zero padded
    [InlineData(99, "out_00-09")]   // still width 2
    [InlineData(100, "out_000-009")] // hundred -> width 3
    [InlineData(1000, "out_0000-0009")] // thousand -> width 4
    public void Key_FirstBucketName_IsPaddedToTheWidthOfKeyMax(int keyMax, string expectedFirstFileName)
    {
        // ExportSize 10 makes the first bucket [0, 9]; its name is zero-padded to keyMax's digit count.
        var segments = EdgePlan(EdgeFlow("K", size: 10, keyColumn: "Id"), keyMax: keyMax);

        Assert.Equal(expectedFirstFileName, segments[0].FileName);
    }

    // ----------------------------------------------------------------------------------------------------
    // Planner: subfolder pattern token combinations and the always-flat NullRows folder.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("YYYY", "2024/")]
    [InlineData("MM", "01/")]
    [InlineData("DD", "15/")]
    [InlineData("YYYYMM", "2024/01/")]
    [InlineData("YYYY-MM-DD", "2024/01/15/")]
    [InlineData("yyyy/mm/dd", "2024/01/15/")] // pattern is upper-cased before token detection
    public void Subfolder_TokenCombination_BecomesTheMatchingDatePath(string pattern, string expected)
    {
        // A single-day window pins the chunk-start date to 2024-01-15 for a stable subfolder.
        var day = new DateOnly(2024, 1, 15);
        var segments = EdgePlan(EdgeFlow("D", dateColumn: "OrderDate", from: day, to: day, subfolder: pattern));

        Assert.Equal(expected, segments[0].SubFolder);
    }

    [Theory]
    [InlineData("static")] // no date tokens
    [InlineData("   ")]     // whitespace only
    public void Subfolder_PatternWithoutDateTokens_ProducesNoSubfolder(string pattern)
    {
        var day = new DateOnly(2024, 1, 15);
        var segments = EdgePlan(EdgeFlow("D", dateColumn: "OrderDate", from: day, to: day, subfolder: pattern));

        Assert.Equal(string.Empty, segments[0].SubFolder);
    }

    [Fact]
    public void NullRowsSegment_HasNoSubfolder_EvenWhenAPatternIsConfigured()
    {
        var segments = EdgePlan(EdgeFlow("M", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 31), subfolder: "YYYY/MM"));

        Assert.Equal("out_NullRows", segments[^1].FileName);
        Assert.Equal(string.Empty, segments[^1].SubFolder);
    }

    // ----------------------------------------------------------------------------------------------------
    // Planner: ExportBy normalization, the unknown-unit fallback, and timestamp placement.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("f")]
    [InlineData("  F  ")]
    public void ExportBy_FullVariants_NormalizeToASingleFullTableSegment(string by)
    {
        var segment = Assert.Single(EdgePlan(EdgeFlow(by)));

        Assert.EndsWith("WHERE 1=1", segment.Sql, StringComparison.Ordinal);
        Assert.Equal("out", segment.FileName);
    }

    [Fact]
    public void ExportBy_LowercaseDay_NormalizesAndChunksByDay()
    {
        var segments = EdgePlan(EdgeFlow("d", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 1)));

        Assert.Equal(2, segments.Count);
        Assert.Equal("out_2024-01-01", segments[0].FileName);
    }

    [Theory]
    [InlineData("X")]
    [InlineData("Z")]
    [InlineData("FULL")]
    public void ExportBy_UnknownUnit_FallsBackToASingleFullTableSegmentWithNoNullRows(string by)
    {
        var segment = Assert.Single(EdgePlan(EdgeFlow(by)));

        Assert.DoesNotContain("IS NULL", segment.Sql, StringComparison.Ordinal);
        Assert.Equal("out", segment.FileName);
    }

    [Fact]
    public void Timestamp_SitsBetweenPrefixAndChunkPostfix_ForDayChunks()
    {
        var segments = EdgePlan(EdgeFlow("D", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 1), timestamp: true));

        Assert.Equal("out_20240115120000_2024-01-01", segments[0].FileName);
        Assert.Equal("out_20240115120000_NullRows", segments[^1].FileName);
    }

    [Fact]
    public void Timestamp_UsesTheSuppliedRunTimestamp_NotMachineClock()
    {
        var runAt = new DateTime(2030, 12, 31, 23, 59, 58, DateTimeKind.Utc);
        var segment = Assert.Single(ExportSegmentPlanner.Plan(EdgeFlow("F", timestamp: true), EdgeColumns, keyMax: 0, runAt));

        Assert.Equal("out_20301231235958", segment.FileName);
    }

    // ----------------------------------------------------------------------------------------------------
    // Planner: SrcFilter trimming and propagation, and identifier / object-name escaping.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void SrcFilter_IsTrimmed_AndPrefixedWithExactlyOneSpace()
    {
        var segment = Assert.Single(EdgePlan(EdgeFlow("F", filter: "   AND [Region] = 'EU'   ")));

        Assert.Contains("WHERE 1=1 AND [Region] = 'EU'", segment.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("EU'   ", segment.Sql, StringComparison.Ordinal); // trailing whitespace gone
    }

    [Fact]
    public void SrcFilter_IsAppliedToTheNullRowsSegmentToo()
    {
        var segments = EdgePlan(EdgeFlow("M", dateColumn: "OrderDate", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 31), filter: "AND [Region] = 'EU'"));

        var nullRows = segments[^1];
        Assert.Contains("WHERE 1=1 AND [Region] = 'EU' AND ([OrderDate] IS NULL)", nullRows.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void SrcFilter_BlankOrWhitespace_AddsNothingToTheWhereClause(string filter)
    {
        var segment = Assert.Single(EdgePlan(EdgeFlow("F", filter: filter)));

        Assert.EndsWith("WHERE 1=1", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_ThreePartName_IsFullyBracketedInTheFromClause()
    {
        var segment = Assert.Single(EdgePlan(EdgeFlow("F")));

        Assert.Contains("FROM [DB].[dbo].[Orders] WHERE", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_NameContainingABracket_IsEscapedInTheFromClause()
    {
        var flow = EdgeFlow("F") with { Source = new RelationalObject { Database = "DB", Schema = "dbo", Name = "Or]ders" } };
        var segment = Assert.Single(EdgePlan(flow));

        Assert.Contains("[Or]]ders]", segment.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DateColumn_ContainingABracket_IsEscapedInTheChunkPredicate()
    {
        var segments = EdgePlan(EdgeFlow("D", dateColumn: "Ord]er", from: new DateOnly(2024, 1, 1), to: new DateOnly(2024, 1, 1)));

        Assert.Contains("[Ord]]er]", segments[0].Sql, StringComparison.Ordinal);
        Assert.Contains("[Ord]]er] IS NULL", segments[^1].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void IncrementalColumn_ContainingABracket_IsEscapedInTheKeyPredicate()
    {
        var segments = EdgePlan(EdgeFlow("K", size: 100, keyColumn: "I]d"), keyMax: 50);

        Assert.Contains("[I]]d] >= 0 AND [I]]d] <= 50", segments[0].Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyColumnList_ProducesASelectWithNoProjection()
    {
        // A reader with zero columns yields "SELECT  FROM ..." (the projection list is empty).
        var segment = Assert.Single(EdgePlan(EdgeFlow("F"), columns: []));

        Assert.StartsWith("SELECT  FROM [DB].[dbo].[Orders] WHERE 1=1", segment.Sql, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------------------------------------------
    // Mapper: defaulting, trimming, and three-part parsing corners not covered elsewhere.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(" m ", "M")]
    [InlineData("k", "K")]
    [InlineData("F", "F")]
    public void Mapper_ExportBy_IsTrimmedAndUpperCased(string raw, string expected)
    {
        var row = EdgeRow();
        row.ExportBy = raw;
        Assert.Equal(expected, ExportFlowMapper.FromLegacy(row).ExportBy);
    }

    [Fact]
    public void Mapper_NullExportBy_DefaultsToDay()
    {
        var row = EdgeRow();
        row.ExportBy = null;
        Assert.Equal("D", ExportFlowMapper.FromLegacy(row).ExportBy);
    }

    [Theory]
    [InlineData(null, "csv")]
    [InlineData("   ", "csv")]
    [InlineData(" Parquet ", "Parquet")] // non-blank values are trimmed but not lower-cased by the mapper
    public void Mapper_TrgFiletype_DefaultsWhenBlankAndTrimsOtherwise(string? raw, string expected)
    {
        var row = EdgeRow();
        row.trgFiletype = raw;
        Assert.Equal(expected, ExportFlowMapper.FromLegacy(row).TrgFiletype);
    }

    [Fact]
    public void Mapper_ServicePrincipalAlias_IsTrimmedIntoAnAtReference()
    {
        var row = EdgeRow();
        row.ServicePrincipalAlias = "  adls1  ";
        Assert.Equal("@adls1", ExportFlowMapper.FromLegacy(row).TargetReference);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mapper_BlankServicePrincipalAlias_LeavesTargetReferenceNull(string? alias)
    {
        var row = EdgeRow();
        row.ServicePrincipalAlias = alias;
        Assert.Null(ExportFlowMapper.FromLegacy(row).TargetReference);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void Mapper_BlankSrcFilterAndSubfolder_BecomeNull(string blank)
    {
        var row = EdgeRow();
        row.srcFilter = blank;
        row.Subfolderpattern = blank;
        var flow = ExportFlowMapper.FromLegacy(row);
        Assert.Null(flow.SrcFilter);
        Assert.Null(flow.Subfolderpattern);
    }

    [Fact]
    public void Mapper_FourPartSourceName_DropsTheLeadingServerPart()
    {
        var row = EdgeRow();
        row.srcDBSchTbl = "[srv].[DB].[dbo].[Orders]";
        var source = ExportFlowMapper.FromLegacy(row).Source;
        Assert.Equal("DB", source.Database);
        Assert.Equal("dbo", source.Schema);
        Assert.Equal("Orders", source.Name);
    }

    [Fact]
    public void Mapper_TwoPartSourceName_Throws()
    {
        var row = EdgeRow();
        row.srcDBSchTbl = "[dbo].[Orders]";
        Assert.Throws<SqlFlowException>(() => ExportFlowMapper.FromLegacy(row));
    }

    [Fact]
    public void Mapper_NegativeExportSize_IsCarriedThroughVerbatim()
    {
        // The mapper does not clamp ExportSize; the planner is responsible for treating it as empty bucketing.
        var row = EdgeRow();
        row.ExportSize = -5;
        Assert.Equal(-5, ExportFlowMapper.FromLegacy(row).ExportSize);
    }

    // ----------------------------------------------------------------------------------------------------
    // Writer factory: type selection and qualifier defaulting.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("parquet")]
    [InlineData("prq")]
    [InlineData("  PARQUET  ")]
    public void Factory_ParquetFiletypes_ReturnAParquetWriter(string fileType)
    {
        var writer = ExportFileWriterFactory.Create(EdgeFlow("F") with { TrgFiletype = fileType });
        Assert.IsType<ParquetExportFileWriter>(writer);
    }

    [Theory]
    [InlineData("csv")]
    [InlineData("")]
    [InlineData("txt")] // any non-Parquet value falls through to the CSV writer
    public void Factory_NonParquetFiletypes_ReturnACsvWriter(string fileType)
    {
        var writer = ExportFileWriterFactory.Create(EdgeFlow("F") with { TrgFiletype = fileType });
        Assert.IsType<CsvExportFileWriter>(writer);
    }

    [Fact]
    public async Task Factory_EmptyTextQualifier_DefaultsToDoubleQuote()
    {
        // An empty qualifier must not crash the CSV writer; it falls back to the double-quote character.
        var writer = ExportFileWriterFactory.Create(EdgeFlow("F") with { TrgFiletype = "csv", TextQualifier = string.Empty });

        using var table = EdgeStringTable("a;b");
        using var reader = table.CreateDataReader();
        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);

        Assert.Equal(1, rows);
        var line = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[1];
        Assert.Equal("\"a;b\"", line); // string column with embedded default delimiter, wrapped in the default qualifier
    }

    // ----------------------------------------------------------------------------------------------------
    // CSV writer: empty input, header toggling, encoding, and value formatting corners.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Csv_EmptyReader_WritesHeaderOnly_AndReturnsZeroRows()
    {
        using var table = EdgeStringTable();
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);

        Assert.Equal(0, rows);
        var text = EdgeReadAllText(stream, new UTF8Encoding(false));
        Assert.Equal("Value\r\n", text);
    }

    [Fact]
    public async Task Csv_HeaderDisabled_OmitsTheHeaderRow()
    {
        using var table = EdgeStringTable("x");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions() with { WriteHeader = false });

        using var stream = new MemoryStream();
        var rows = await writer.WriteAsync(reader, stream);

        Assert.Equal(1, rows);
        var text = EdgeReadAllText(stream, new UTF8Encoding(false));
        Assert.Equal("\"x\"\r\n", text); // first line is data, not "Value"
    }

    [Fact]
    public async Task Csv_HonorsUnicodeEncoding_RoundTrippingNonAsciiText()
    {
        using var table = EdgeStringTable("smörgås");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions() with { Encoding = Encoding.Unicode });

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var text = EdgeReadAllText(stream, Encoding.Unicode);
        Assert.Equal("Value\r\n\"smörgås\"\r\n", text);
    }

    [Fact]
    public async Task Csv_NonStringColumnValue_IsNotQuotedUnlessItNeedsTo()
    {
        // A bool column formats to "True"/"False"; it is not a string column, so it stays bare.
        using var table = new DataTable();
        table.Columns.Add("Flag", typeof(bool));
        table.Rows.Add(true);
        table.Rows.Add(false);
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var lines = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("True", lines[1]);
        Assert.Equal("False", lines[2]);
    }

    [Fact]
    public async Task Csv_StringValueEqualToTheQualifier_IsDoubledAndWrapped()
    {
        using var table = EdgeStringTable("\"");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var line = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[1];
        Assert.Equal("\"\"\"\"", line); // a lone quote becomes a doubled quote inside a quoted field
    }

    [Fact]
    public async Task Csv_StringValueWithEmbeddedNewline_IsQuoted()
    {
        using var table = EdgeStringTable("line1\nline2");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var text = EdgeReadAllText(stream, new UTF8Encoding(false));
        Assert.Equal("Value\r\n\"line1\nline2\"\r\n", text);
    }

    [Fact]
    public async Task Csv_NonStringColumnValueContainingTheDelimiter_IsQuoted()
    {
        // A custom delimiter inside a numeric-looking string column forces quoting even with QuoteStringColumnsOnly.
        using var table = new DataTable();
        table.Columns.Add("Raw", typeof(object));
        table.Rows.Add("a|b");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions() with { Delimiter = "|", QuoteStringColumnsOnly = false });

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var line = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[1];
        Assert.Equal("\"a|b\"", line);
    }

    [Fact]
    public async Task Csv_DateTimeValue_UsesInvariantSevenFractionFormat()
    {
        using var table = new DataTable();
        table.Columns.Add("Ts", typeof(DateTime));
        table.Rows.Add(new DateTime(2024, 1, 15, 13, 45, 30, DateTimeKind.Unspecified).AddTicks(1234567));
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var line = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[1];
        Assert.Equal("2024-01-15 13:45:30.1234567", line); // not quoted: DateTime column, no special characters
    }

    [Fact]
    public async Task Csv_DecimalAndGuidValues_UseInvariantFormatting()
    {
        using var table = new DataTable();
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Key", typeof(Guid));
        var guid = new Guid("01234567-89ab-cdef-0123-456789abcdef");
        table.Rows.Add(1234.5m, guid);
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var fields = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[1].Split(';');
        Assert.Equal("1234.5", fields[0]);
        Assert.Equal(guid.ToString("D", CultureInfo.InvariantCulture), fields[1]);
    }

    [Fact]
    public async Task Csv_HeaderName_IsNeverQuotedAsAStringColumn_ButIsQuotedWhenItContainsTheDelimiter()
    {
        // The header is emitted with isStringColumn=false, so a plain name stays bare while a delimiter-bearing
        // name is quoted on its own merits.
        using var table = new DataTable();
        table.Columns.Add("a;b", typeof(string));
        table.Rows.Add("v");
        using var reader = table.CreateDataReader();
        var writer = new CsvExportFileWriter(EdgeCsvOptions());

        using var stream = new MemoryStream();
        await writer.WriteAsync(reader, stream);

        var header = EdgeReadAllText(stream, new UTF8Encoding(false)).Split("\r\n")[0];
        Assert.Equal("\"a;b\"", header);
    }

    // ----------------------------------------------------------------------------------------------------
    // Private, uniquely named helpers (no collision with other files in the namespace).
    // ----------------------------------------------------------------------------------------------------

    private static CsvWriteOptions EdgeCsvOptions() => new()
    {
        Delimiter = ";",
        Qualifier = '"',
        Encoding = new UTF8Encoding(false),
    };

    private static DataTable EdgeStringTable(params string[] values)
    {
        var table = new DataTable();
        table.Columns.Add("Value", typeof(string));
        foreach (var value in values)
        {
            table.Rows.Add(value);
        }

        return table;
    }

    private static string EdgeReadAllText(MemoryStream stream, Encoding encoding)
    {
        stream.Position = 0;
        using var streamReader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return streamReader.ReadToEnd();
    }
}
