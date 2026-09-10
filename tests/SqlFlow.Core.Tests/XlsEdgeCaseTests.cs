using System.Globalization;
using ClosedXML.Excel;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for <see cref="XlsSourceReader"/> and the shared file
/// pipeline behind it. These are pure in-memory tests: every workbook is built with ClosedXML and read
/// straight back through the reader, with fixed inputs so the assertions are deterministic on any machine.
/// They harden the corners the primary suite does not touch: multi-letter and reversed cell ranges, header
/// cleanup (special characters, duplicates, blank header cells), typed-cell rendering (negatives, zero,
/// scientific doubles, sub-second dates, boolean false), leading-zero text, whitespace-only cells, blank
/// middle rows, wider-than-header data rows, sheet selection by case-insensitive name and explicit index,
/// hash/concat keys, and the many invalid-range and invalid-sheet error branches.
/// </summary>
public sealed class XlsEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xlsedge_" + Guid.NewGuid().ToString("N"));
    private readonly XlsSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public XlsEdgeCaseTests() => Directory.CreateDirectory(_dir);

    private string Workbook(string name, Action<XLWorkbook> build)
    {
        var path = Path.Combine(_dir, name);
        using var wb = new XLWorkbook();
        build(wb);
        wb.SaveAs(path);
        return path;
    }

    private static SourceSpec XlsSource(string path, Dictionary<string, string?>? options = null)
        => new() { Type = "xlsx", Location = path, Options = options ?? new Dictionary<string, string?>() };

    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadEverythingAsync(SourceSpec source)
    {
        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = new List<object?[]>();
        while (await data.ReadAsync())
        {
            var row = new object?[data.FieldCount];
            for (var i = 0; i < data.FieldCount; i++)
            {
                row[i] = data.IsDBNull(i) ? null : data.GetValue(i);
            }

            rows.Add(row);
        }

        return (columns.Select(c => c.Name).ToList(), rows);
    }

    private async Task<List<string>> ReadColumnNamesAsync(SourceSpec source)
        => (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

    // ----------------------------------------------------------------------------------------------------
    // Cell-range parsing edge cases (multi-letter, lowercase, reversed, whitespace, single column).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task SheetRange_MultiLetterColumn_MapsBeyondZ()
    {
        // Column AA is the 27th column (index 26). The range AA1:AB2 should pick exactly those two columns.
        var path = Workbook("aa.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 27).Value = "First";   // AA1
            ws.Cell(1, 28).Value = "Second";  // AB1
            ws.Cell(2, 27).Value = "alpha";
            ws.Cell(2, 28).Value = "beta";
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetRange"] = "AA1:AB2" }));

        Assert.Equal(new[] { "First", "Second" }, columns.Take(2).ToArray());
        Assert.Equal("alpha", Assert.Single(rows)[0]);
        Assert.Equal("beta", rows[0][1]);
    }

    [Fact]
    public async Task SheetRange_LowercaseReference_IsAccepted()
    {
        var path = Workbook("lower.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(2, 2).Value = "Code";  // B2
            ws.Cell(2, 3).Value = "Name";  // C2
            ws.Cell(3, 2).Value = 10;
            ws.Cell(3, 3).Value = "Alpha";
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetRange"] = "b2:c3" }));

        Assert.Equal(new[] { "Code", "Name" }, columns.Take(2).ToArray());
        Assert.Equal("10", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetRange_ReversedCorners_AreNormalized()
    {
        // Bottom-right written first, top-left second: the reader sorts the corners so the window is the same.
        var path = Workbook("rev.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(2, 2).Value = "Code";  // B2
            ws.Cell(2, 3).Value = "Name";  // C2
            ws.Cell(3, 2).Value = 1;
            ws.Cell(3, 3).Value = "x";
            ws.Cell(4, 2).Value = 2;
            ws.Cell(4, 3).Value = "y";
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetRange"] = "C4:B2" }));

        Assert.Equal(new[] { "Code", "Name" }, columns.Take(2).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal("y", rows[1][1]);
    }

    [Fact]
    public async Task SheetRange_SurroundingWhitespace_IsTrimmed()
    {
        var path = Workbook("rangews.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(2, 2).Value = "Code";  // B2
            ws.Cell(3, 2).Value = 5;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetRange"] = "  B2 : B3  " }));

        Assert.Equal("Code", columns[0]);
        Assert.Equal("5", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetRange_StartRowBelowFirst_SkipsPreamble()
    {
        // Header on row 3, preamble on rows 1 and 2 must be excluded by the A3 open-ended start.
        var path = Workbook("preamble.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "title-junk";
            ws.Cell(2, 1).Value = "more-junk";
            ws.Cell(3, 1).Value = "Code";
            ws.Cell(4, 1).Value = 100;
            ws.Cell(5, 1).Value = 200;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetRange"] = "A3" }));

        Assert.Equal("Code", columns[0]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("100", rows[0][0]);
    }

    [Theory]
    [InlineData("A0:B2")]   // row 0 is not a valid 1-based row
    [InlineData("5:10")]    // digits only, no column letters
    [InlineData("B")]       // column letter with no row number
    [InlineData("AB")]      // multi-letter column with no row number
    [InlineData("1A:2B")]   // digits before letters
    public async Task SheetRange_InvalidCellReference_Throws(string range)
    {
        var path = Workbook("badcell.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = "x";
        });

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(XlsSource(path, new() { ["sheetRange"] = range })));
    }

    // ----------------------------------------------------------------------------------------------------
    // Header detection and the legacy file-flow column-name cleanup.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Header_SpecialCharacters_BecomeUnderscores()
    {
        var path = Workbook("special.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Order Id";
            ws.Cell(1, 2).Value = "Sub-Category";
            ws.Cell(1, 3).Value = "Unit$Price";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "x";
            ws.Cell(2, 3).Value = 9.5;
        });

        var names = await ReadColumnNamesAsync(XlsSource(path));

        Assert.Equal(new[] { "Order_Id", "Sub_Category", "Unit_Price" }, names.Take(3).ToArray());
    }

    [Fact]
    public async Task Header_NorwegianLetters_ArePreserved()
    {
        // The canonical legacy cleanup regex keeps the six Norwegian letters; they must survive verbatim.
        var path = Workbook("nordic.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Måned";
            ws.Cell(1, 2).Value = "Beløp";
            ws.Cell(2, 1).Value = "Januar";
            ws.Cell(2, 2).Value = 10;
        });

        var names = await ReadColumnNamesAsync(XlsSource(path));

        Assert.Equal(new[] { "Måned", "Beløp" }, names.Take(2).ToArray());
    }

    [Fact]
    public async Task Header_DuplicateNames_AreDisambiguatedByOrdinal()
    {
        var path = Workbook("dups.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Name";
            ws.Cell(1, 2).Value = "Name";
            ws.Cell(1, 3).Value = "Name";
            ws.Cell(2, 1).Value = "a";
            ws.Cell(2, 2).Value = "b";
            ws.Cell(2, 3).Value = "c";
        });

        var names = await ReadColumnNamesAsync(XlsSource(path));

        // First keeps the raw name; later collisions get their zero-based ordinal appended.
        Assert.Equal(new[] { "Name", "Name1", "Name2" }, names.Take(3).ToArray());
    }

    [Fact]
    public async Task Header_CaseInsensitiveDuplicate_IsTreatedAsCollision()
    {
        var path = Workbook("casedup.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Total";
            ws.Cell(1, 2).Value = "TOTAL";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = 2;
        });

        var names = await ReadColumnNamesAsync(XlsSource(path));

        Assert.Equal("Total", names[0]);
        Assert.Equal("TOTAL1", names[1]);
    }

    [Fact]
    public async Task Header_BlankCellAmongHeaders_GetsGeneratedName()
    {
        // B1 is blank while A1 and C1 are named: the blank header keeps its slot as a generated Column2.
        var path = Workbook("blankhdr.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 3).Value = "C";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = 2;
            ws.Cell(2, 3).Value = 3;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal(new[] { "A", "Column2", "C" }, columns.Take(3).ToArray());
        var row = Assert.Single(rows);
        Assert.Equal("2", row[1]); // the middle value still lands under the generated column
    }

    [Fact]
    public async Task Header_AllNumericHeaderCells_AreRenderedThenKept()
    {
        // A header row of numeric cells: each is rendered to its invariant string and kept as the column name.
        var path = Workbook("numhdr.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = 2023;
            ws.Cell(1, 2).Value = 2024;
            ws.Cell(2, 1).Value = "a";
            ws.Cell(2, 2).Value = "b";
        });

        var names = await ReadColumnNamesAsync(XlsSource(path));

        Assert.Equal(new[] { "2023", "2024" }, names.Take(2).ToArray());
    }

    [Fact]
    public async Task Headerless_AlwaysGeneratesColumnNames_RegardlessOfFirstRowContent()
    {
        // With header=false the first row is data even though it looks like a header.
        var path = Workbook("nohdr.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "LooksLikeHeader";
            ws.Cell(1, 2).Value = "AlsoHeader";
            ws.Cell(2, 1).Value = "realdata";
            ws.Cell(2, 2).Value = 7;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["header"] = "false" }));

        Assert.Equal(new[] { "Column1", "Column2" }, columns.Take(2).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal("LooksLikeHeader", rows[0][0]); // first row preserved as data
        Assert.Equal("7", rows[1][1]);
    }

    // ----------------------------------------------------------------------------------------------------
    // Typed-cell rendering: numbers (negative, zero, scientific), sub-second dates, boolean false.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(-3.25, "-3.25")]
    [InlineData(1234567.0, "1234567")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(1e20, "1E+20")]
    [InlineData(-0.5, "-0.5")]
    public async Task NumericCell_RendersWithInvariantCulture(double value, string expected)
    {
        var path = Workbook("num.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "N";
            ws.Cell(2, 1).Value = value;
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal(expected, Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task DateCell_WithTimeComponent_RendersFullTimestamp()
    {
        var path = Workbook("dt.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "When";
            ws.Cell(2, 1).Value = new DateTime(2024, 7, 9, 13, 45, 30, DateTimeKind.Unspecified);
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("2024-07-09 13:45:30", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task DateCell_MidnightDate_RendersWithZeroTime()
    {
        var path = Workbook("midnight.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "When";
            ws.Cell(2, 1).Value = new DateTime(1999, 12, 31, 0, 0, 0, DateTimeKind.Unspecified);
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("1999-12-31 00:00:00", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task BooleanCell_False_RendersAsFalse()
    {
        var path = Workbook("flag.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Flag";
            ws.Cell(2, 1).Value = false;
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("False", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task TextCell_LeadingZeros_ArePreservedNotNumeric()
    {
        // A text-typed cell holding "007" must stay "007"; only numeric cells go through double rendering.
        var path = Workbook("leadingzero.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Code";
            ws.Cell(2, 1).SetValue("007");
            ws.Cell(3, 1).SetValue("00123");
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal(2, rows.Count);
        Assert.Equal("007", rows[0][0]);
        Assert.Equal("00123", rows[1][0]);
    }

    [Fact]
    public async Task TextCell_WhitespaceOnly_IsPreserved()
    {
        // CoerceCell nulls only a zero-length string; a non-empty whitespace string is real content.
        var path = Workbook("spaces.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 2).Value = "B";
            ws.Cell(2, 1).SetValue("   ");
            ws.Cell(2, 2).Value = "keep";
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        var row = Assert.Single(rows);
        Assert.Equal("   ", row[0]);
        Assert.Equal("keep", row[1]);
    }

    [Fact]
    public async Task TextCell_NumberStoredAsText_DoesNotGetDoubleFormatted()
    {
        // "1.50" stored as text stays "1.50"; a numeric 1.50 would render as "1.5".
        var path = Workbook("textnum.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Amount";
            ws.Cell(2, 1).SetValue("1.50");
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("1.50", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task MixedTypesInOneColumn_AllRenderAsStrings()
    {
        // A column with a number, a date and text across rows still resolves to a single string column.
        var path = Workbook("mixed.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Value";
            ws.Cell(2, 1).Value = 42;
            ws.Cell(3, 1).Value = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);
            ws.Cell(4, 1).Value = "text";
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal(typeof(string), (await _reader.GetColumnsAsync(XlsSource(path))).First(c => c.Name == "Value").Type);
        Assert.Equal(3, rows.Count);
        Assert.Equal("42", rows[0][0]);
        Assert.Equal("2024-01-02 00:00:00", rows[1][0]);
        Assert.Equal("text", rows[2][0]);
        Assert.Contains("Value", columns);
    }

    // ----------------------------------------------------------------------------------------------------
    // Sparse layouts: blank middle rows, wider-than-header data rows, interior null cells.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task BlankMiddleRow_IsEmittedAsAllNullRow_AndCountsTowardRowNumber()
    {
        var path = Workbook("blankmid.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 2).Value = "B";
            ws.Cell(2, 1).Value = "r1a";
            ws.Cell(2, 2).Value = "r1b";
            // row 3 left entirely blank
            ws.Cell(4, 1).Value = "r3a";
            ws.Cell(4, 2).Value = "r3b";
        });

        var source = XlsSource(path, new() { ["includeRowNumber"] = "true" });
        var columns = await _reader.GetColumnsAsync(source);
        var rowNumberIndex = columns.ToList().FindIndex(c => c.Name == "RowNumber_DW");
        var (_, rows) = await ReadEverythingAsync(source);

        Assert.Equal(3, rows.Count);
        Assert.Null(rows[1][0]);
        Assert.Null(rows[1][1]);
        Assert.Equal(1L, rows[0][rowNumberIndex]);
        Assert.Equal(2L, rows[1][rowNumberIndex]);
        Assert.Equal(3L, rows[2][rowNumberIndex]);
    }

    [Fact]
    public async Task DataRowWiderThanHeader_AddsGeneratedColumnForTheStrayValue()
    {
        // A value in column D with only A and B headered: the sheet width drives a generated Column3/Column4.
        var path = Workbook("wide.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 2).Value = "B";
            ws.Cell(2, 1).Value = "x";
            ws.Cell(2, 2).Value = "y";
            ws.Cell(2, 4).Value = "stray";
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal(new[] { "A", "B", "Column3", "Column4" }, columns.Take(4).ToArray());
        var row = Assert.Single(rows);
        Assert.Equal("x", row[0]);
        Assert.Equal("y", row[1]);
        Assert.Null(row[2]);              // Column3 has no value in this row
        Assert.Equal("stray", row[3]);   // the stray value lands under Column4
    }

    [Fact]
    public async Task InteriorEmptyCell_BetweenPopulatedCells_BecomesNull()
    {
        // Distinct from a trailing ragged cell: the empty cell is in the middle column, flanked by values.
        var path = Workbook("interior.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 2).Value = "B";
            ws.Cell(1, 3).Value = "C";
            ws.Cell(2, 1).Value = "left";
            // B2 deliberately empty
            ws.Cell(2, 3).Value = "right";
        });

        var (_, rows) = await ReadEverythingAsync(XlsSource(path));

        var row = Assert.Single(rows);
        Assert.Equal("left", row[0]);
        Assert.Null(row[1]);
        Assert.Equal("right", row[2]);
    }

    [Fact]
    public async Task LargeColumnCount_AllColumnsRoundTrip()
    {
        const int wide = 60;
        var path = Workbook("manycols.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            for (var c = 1; c <= wide; c++)
            {
                ws.Cell(1, c).Value = "C" + c.ToString(CultureInfo.InvariantCulture);
                ws.Cell(2, c).Value = c;
            }
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("C1", columns[0]);
        Assert.Equal("C60", columns[wide - 1]);
        var row = Assert.Single(rows);
        Assert.Equal("1", row[0]);
        Assert.Equal("60", row[wide - 1]);
    }

    // ----------------------------------------------------------------------------------------------------
    // Multiple sheets and sheet selection.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoSheetName_DefaultsToFirstSheet_EvenWithLaterSheets()
    {
        var path = Workbook("defaultsheet.xlsx", wb =>
        {
            var first = wb.AddWorksheet("Alpha");
            first.Cell(1, 1).Value = "FromAlpha";
            first.Cell(2, 1).Value = 1;
            var second = wb.AddWorksheet("Beta");
            second.Cell(1, 1).Value = "FromBeta";
            second.Cell(2, 1).Value = 2;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));

        Assert.Equal("FromAlpha", columns[0]);
        Assert.Equal("1", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetByName_IsCaseInsensitive()
    {
        var path = Workbook("casesheet.xlsx", wb =>
        {
            var first = wb.AddWorksheet("Summary");
            first.Cell(1, 1).Value = "Skip";
            first.Cell(2, 1).Value = 0;
            var second = wb.AddWorksheet("Details");
            second.Cell(1, 1).Value = "Picked";
            second.Cell(2, 1).Value = 99;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetName"] = "details" }));

        Assert.Equal("Picked", columns[0]);
        Assert.Equal("99", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetByIndexZero_SelectsFirstSheet()
    {
        var path = Workbook("idx0.xlsx", wb =>
        {
            var first = wb.AddWorksheet("One");
            first.Cell(1, 1).Value = "FirstCol";
            first.Cell(2, 1).Value = 11;
            var second = wb.AddWorksheet("Two");
            second.Cell(1, 1).Value = "SecondCol";
            second.Cell(2, 1).Value = 22;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetName"] = "0", ["useSheetIndex"] = "true" }));

        Assert.Equal("FirstCol", columns[0]);
        Assert.Equal("11", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetByIndex_SelectsMiddleSheetOfThree()
    {
        var path = Workbook("idxmid.xlsx", wb =>
        {
            var s0 = wb.AddWorksheet("S0");
            s0.Cell(1, 1).Value = "Zero";
            s0.Cell(2, 1).Value = 0;
            var s1 = wb.AddWorksheet("S1");
            s1.Cell(1, 1).Value = "MiddleCol";
            s1.Cell(2, 1).Value = 5;
            var s2 = wb.AddWorksheet("S2");
            s2.Cell(1, 1).Value = "Two";
            s2.Cell(2, 1).Value = 2;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["sheetName"] = "1", ["useSheetIndex"] = "true" }));

        Assert.Equal("MiddleCol", columns[0]);
        Assert.Equal("5", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task SheetIndex_Negative_Throws()
    {
        var path = Workbook("negidx.xlsx", wb => wb.AddWorksheet("Only").Cell(1, 1).Value = "A");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(XlsSource(path, new() { ["sheetName"] = "-1", ["useSheetIndex"] = "true" })));
    }

    [Fact]
    public async Task SheetIndex_NonNumeric_Throws()
    {
        var path = Workbook("nanidx.xlsx", wb => wb.AddWorksheet("Only").Cell(1, 1).Value = "A");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(XlsSource(path, new() { ["sheetName"] = "abc", ["useSheetIndex"] = "true" })));
    }

    // ----------------------------------------------------------------------------------------------------
    // Empty sheets.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public async Task EmptySheet_YieldsNoSourceColumnsAndNoRows(string header)
    {
        // A worksheet with no cells at all produces no source columns; only system columns remain, no data rows.
        var path = Workbook("empty.xlsx", wb => wb.AddWorksheet("Blank"));

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path, new() { ["header"] = header }));

        Assert.DoesNotContain("Column1", columns);
        Assert.Contains("FileName_DW", columns); // provenance is still injected
        Assert.Empty(rows);
    }

    // ----------------------------------------------------------------------------------------------------
    // Provenance and synthetic keys.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task FileNameColumn_DefaultsToBareName_NotPath()
    {
        var path = Workbook("provname.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = 1;
        });

        var (columns, rows) = await ReadEverythingAsync(XlsSource(path));
        var idx = columns.IndexOf("FileName_DW");

        Assert.Equal("provname.xlsx", Assert.Single(rows)[idx]);
    }

    [Fact]
    public async Task FileNameColumn_WithShowPath_UsesFullPath()
    {
        var path = Workbook("provpath.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = 1;
        });

        var source = XlsSource(path, new() { ["showPathWithFileName"] = "true" });
        var (columns, rows) = await ReadEverythingAsync(source);
        var idx = columns.IndexOf("FileName_DW");

        Assert.Equal(path, Assert.Single(rows)[idx]);
    }

    [Fact]
    public async Task FileLineNumber_TracksPhysicalRow_DistinctFromDataRowNumber()
    {
        // With a header row, the first data row is physical line 2 but data row 1.
        var path = Workbook("linenum.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = "first";
            ws.Cell(3, 1).Value = "second";
        });

        var source = XlsSource(path, new() { ["includeFileLineNumber"] = "true", ["includeRowNumber"] = "true" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var lineIdx = columns.FindIndex(c => c.Name == "FileLineNumber");
        var rowIdx = columns.FindIndex(c => c.Name == "RowNumber_DW");
        var (_, rows) = await ReadEverythingAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal(2L, rows[0][lineIdx]);
        Assert.Equal(1L, rows[0][rowIdx]);
        Assert.Equal(3L, rows[1][lineIdx]);
        Assert.Equal(2L, rows[1][rowIdx]);
    }

    [Fact]
    public async Task ConcatKey_RendersNumericInputsAsStrings()
    {
        var path = Workbook("concat.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Region";
            ws.Cell(2, 1).Value = 42;
            ws.Cell(2, 2).Value = "North";
        });

        var source = XlsSource(path, new()
        {
            ["includeConcatKey"] = "true",
            ["concatKeyColumns"] = "Id,Region",
            ["concatKeySeparator"] = "::",
        });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var idx = columns.FindIndex(c => c.Name == "ConcatKey_DW");

        await using var data = (await _reader.OpenAsync(source, columns)).Reader;
        Assert.True(await data.ReadAsync());

        Assert.Equal("42::North", data.GetString(idx));
    }

    [Fact]
    public async Task ConcatKey_UnknownColumn_ThrowsWhenRowsAreStreamed()
    {
        // The key-column resolution is part of streaming, so the failure surfaces on the first read, not on
        // the column-discovery call.
        var path = Workbook("concatbad.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(2, 1).Value = 1;
        });

        var source = XlsSource(path, new()
        {
            ["includeConcatKey"] = "true",
            ["concatKeyColumns"] = "DoesNotExist",
        });

        var columns = await _reader.GetColumnsAsync(source);

        await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });
    }

    [Theory]
    [InlineData("MD5", "varbinary(16)")]
    [InlineData("SHA1", "varbinary(20)")]
    [InlineData("SHA2_256", "varbinary(32)")]
    [InlineData("SHA2_512", "varbinary(64)")]
    public async Task HashKey_DigestLength_FollowsHashType(string hashType, string expectedSqlType)
    {
        var path = Workbook("hashtype.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(2, 1).Value = 1;
        });

        var columns = await _reader.GetColumnsAsync(XlsSource(path, new()
        {
            ["includeHashKey"] = "true",
            ["hashKeyType"] = hashType,
        }));
        var hash = columns.Single(c => c.Name == "HashKey_DW");

        Assert.Equal(expectedSqlType, hash.SqlType);
        Assert.Equal(typeof(byte[]), hash.Type);
    }

    [Fact]
    public async Task HashKey_IsDeterministicForEqualKeysAndDiffersForDifferentKeys()
    {
        var path = Workbook("hashdet.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Name";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "Acme";
            ws.Cell(3, 1).Value = 1;
            ws.Cell(3, 2).Value = "Acme";   // identical key to row 1
            ws.Cell(4, 1).Value = 2;
            ws.Cell(4, 2).Value = "Acme";   // different key
        });

        var source = XlsSource(path, new()
        {
            ["includeHashKey"] = "true",
            ["hashKeyColumns"] = "Id,Name",
        });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var idx = columns.FindIndex(c => c.Name == "HashKey_DW");

        var hashes = new List<byte[]>();
        await using (var data = (await _reader.OpenAsync(source, columns)).Reader)
        {
            while (await data.ReadAsync())
            {
                hashes.Add((byte[])data.GetValue(idx));
            }
        }

        Assert.Equal(3, hashes.Count);
        Assert.Equal(hashes[0], hashes[1]);              // identical keys hash identically
        Assert.False(hashes[0].SequenceEqual(hashes[2])); // a different key hashes differently
    }

    [Fact]
    public async Task SourceColumnCollidingWithProvenanceName_Throws()
    {
        // A header literally named FileName_DW would be overwritten by the generated provenance value.
        var path = Workbook("collide.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "FileName_DW";
            ws.Cell(2, 1).Value = "x";
        });

        await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(XlsSource(path)));
    }

    // ----------------------------------------------------------------------------------------------------
    // File selection across a folder of workbooks.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Folder_NoMatchingFiles_ThrowsNoSourceFiles()
    {
        Workbook("present.xlsx", wb => wb.AddWorksheet("Data").Cell(1, 1).Value = "A");

        var source = XlsSource(_dir, new() { ["srcFile"] = "*.no-such-extension" });

        await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task Folder_RowsCarryTheirOwnFileName()
    {
        Workbook("first.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(2, 1).Value = 1;
        });
        Workbook("second.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(2, 1).Value = 2;
        });

        var source = XlsSource(_dir, new() { ["srcFile"] = "*.xlsx" });
        var (columns, rows) = await ReadEverythingAsync(source);
        var nameIdx = columns.IndexOf("FileName_DW");

        Assert.Equal(2, rows.Count);
        var names = rows.Select(r => (string?)r[nameIdx]).OrderBy(n => n, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "first.xlsx", "second.xlsx" }, names);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
