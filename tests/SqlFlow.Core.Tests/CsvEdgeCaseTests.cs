using System.Globalization;
using System.Text;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for the CSV reader (GenericParsing-based). These probe
/// boundary behaviors not already pinned by <see cref="CsvSourceReaderTests"/>: alternate option aliases,
/// escape characters, ragged rows, blank versus whitespace-only lines, header recovery, duplicate and blank
/// header names, multiple encodings, expected-column-count enforcement, and large or wide fields. Every test
/// is pure in-memory (no database, no network) and deterministic (fixed inputs only).
/// </summary>
public sealed class CsvEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_csv_edge_" + Guid.NewGuid().ToString("N"));
    private readonly CsvSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public CsvEdgeCaseTests() => Directory.CreateDirectory(_dir);

    // Writes a file with the default UTF-8 encoding and returns a CSV source over it.
    private SourceSpec CsvFile(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    // Writes a file with an explicit encoding (for BOM/encoding cases) and returns a CSV source over it.
    private SourceSpec CsvFileEncoded(string fileName, string content, Encoding encoding, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content, encoding);
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    // Drains the reader the way the bulk loader would, returning the resolved column names and the
    // materialized rows so a test can assert on streamed values.
    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadAllAsync(SourceSpec source)
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

    // Returns just the value cells of the single data row (system/provenance columns excluded), so a test
    // can assert on the source values without indexing past the data columns.
    private async Task<object?[]> SingleDataRowAsync(SourceSpec source, int sourceColumnCount)
    {
        var (_, rows) = await ReadAllAsync(source);
        var row = Assert.Single(rows);
        return row.Take(sourceColumnCount).ToArray();
    }

    [Theory]
    [InlineData("\\t", "\t", "x,y")]   // tab-delimited (the "\t" escape token): a comma inside the field is data
    [InlineData("|", "|", "x,y")]      // pipe-delimited: the comma is data, not a separator
    [InlineData(";", ";", "x,y")]      // semicolon-delimited: same
    public async Task Delimiter_OtherSeparatorInsideField_IsLiteralData(string delimiterOption, string actualDelimiter, string secondValue)
    {
        // The tab delimiter is given as the escaped token "\t" (the convention the reader maps to a real tab);
        // a literal tab option value would be treated as blank and ignored. The point of the case is that the
        // OTHER common separator (a comma) sitting inside a field is ordinary data under each delimiter.
        var source = CsvFile("delim.csv", $"A{actualDelimiter}B\n1{actualDelimiter}{secondValue}\n", new() { ["delimiter"] = delimiterOption });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("1", cells[0]);
        Assert.Equal(secondValue, cells[1]);
    }

    [Fact]
    public async Task Delimiter_ColumnDelimiterAlias_IsHonored()
    {
        // The reader accepts the legacy 'columnDelimiter' option name as an alias for 'delimiter'.
        var source = CsvFile("alias.csv", "A;B\n1;Acme\n", new() { ["columnDelimiter"] = ";" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("1", cells[0]);
        Assert.Equal("Acme", cells[1]);
    }

    [Fact]
    public async Task TextQualifier_CustomSingleQuote_AllowsDelimiterInsideField()
    {
        // A single quote as the qualifier lets a comma live inside the quoted value.
        var source = CsvFile("sq.csv", "A,B\n1,'Acme, Inc.'\n", new() { ["textQualifier"] = "'" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("Acme, Inc.", cells[1]);
    }

    [Fact]
    public async Task TextQualifier_QualifierAlias_IsHonored()
    {
        // The reader accepts 'qualifier' as an alias for 'textQualifier'.
        var source = CsvFile("qalias.csv", "A,B\n1,'a,b'\n", new() { ["qualifier"] = "'" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("a,b", cells[1]);
    }

    [Fact]
    public async Task EscapeCharacter_Backslash_EscapesDelimiterInsideUnquotedField()
    {
        // With an escape character set, a backslash-escaped comma is taken as a literal comma in the value.
        var source = CsvFile("esc.csv", "A,B\n1,a\\,b\n", new() { ["escapeCharacter"] = "\\" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("a,b", cells[1]);
    }

    [Fact]
    public async Task EscapeCharacter_ZeroSentinel_DisablesEscaping()
    {
        // The legacy sentinel "0" means "no escape character"; a backslash is then just an ordinary character.
        var source = CsvFile("esc0.csv", "A,B\n1,a\\b\n", new() { ["escapeCharacter"] = "0" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("a\\b", cells[1]);
    }

    [Fact]
    public async Task QuotedEmptyString_BecomesDbNull()
    {
        // An explicitly quoted empty string is still an empty value, which the pipeline maps to NULL.
        var source = CsvFile("qe.csv", "A,B\n1,\"\"\n");

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("1", cells[0]);
        Assert.Null(cells[1]);
    }

    [Fact]
    public async Task WhitespaceOnlyLine_IsAReadableRow_NotSkipped()
    {
        // A line of only spaces is NOT an empty line: it parses as a single-cell row, so it is kept and the
        // trailing column NULL-fills. Row numbering counts it, so the row after it is the third data row.
        var source = CsvFile("ws.csv", "A,B\n1,x\n   \n2,y\n");

        var (columns, rows) = await ReadAllAsync(source);
        var rowNumIdx = columns.IndexOf("RowNumber_DW");

        Assert.Equal(3, rows.Count);
        Assert.Equal("   ", rows[1][0]);   // the spaces survive as the first cell (trim is off by default)
        Assert.Null(rows[1][1]);            // there is no second cell, so column B is NULL
        Assert.Equal(new[] { 1L, 2L, 3L }, rows.Select(r => (long)r[rowNumIdx]!).ToArray());
    }

    [Fact]
    public async Task BlankLineBetweenRows_IsSkipped_AndRowNumbersStayContiguous()
    {
        // A truly empty line (no characters) is skipped by the parser, so only the two real rows load and the
        // injected RowNumber_DW values remain contiguous (1, 2) with no gap for the blank line.
        var source = CsvFile("blank.csv", "A,B\n1,x\n\n2,y\n");

        var (columns, rows) = await ReadAllAsync(source);
        var rowNumIdx = columns.IndexOf("RowNumber_DW");

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { 1L, 2L }, rows.Select(r => (long)r[rowNumIdx]!).ToArray());
    }

    [Fact]
    public async Task HeaderOnlyFile_MissingTrailingNewline_YieldsNoRows()
    {
        // A header-only file is an empty dataset with a known schema, exactly like the header-only file WITH a
        // trailing newline (covered by CsvSourceReaderTests). The absence of a final line terminator must not
        // change that: it must still yield zero data rows.
        var source = CsvFile("nohdrnl.csv", "OrderId,Customer,Amount");

        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = 0;
        while (await data.ReadAsync())
        {
            rows++;
        }

        Assert.Contains(columns, c => c.Name == "OrderId");
        Assert.Equal(0, rows);
        Assert.Equal(0, Assert.Single(read.ProcessedFiles).Rows);
    }

    [Fact]
    public async Task HeaderOnlyFile_CrlfTerminated_YieldsNoRows()
    {
        // The same header-only contract with Windows line endings (distinct from the LF header-only case).
        var source = CsvFile("hdrcrlf.csv", "A,B,C\r\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("A", columns);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task EmptyFile_HasOnlySystemColumns_AndNoRows()
    {
        // A zero-byte file has no source columns at all (only the provenance columns survive) and produces no
        // rows, without throwing.
        var source = CsvFile("empty.csv", string.Empty);

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Empty(rows);
        Assert.DoesNotContain("OrderId", columns);
        Assert.Contains("FileName_DW", columns);
    }

    [Fact]
    public async Task RaggedShortRow_NullFillsMissingTrailingColumns()
    {
        // The header declares three columns; a later row carries only two, so the third column NULL-fills.
        var source = CsvFile("short.csv", "A,B,C\n1,2,3\n4,5\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "A", "B", "C" }, columns.Take(3).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal(new object?[] { "4", "5", null }, rows[1].Take(3).ToArray());
    }

    [Fact]
    public async Task RaggedLongFirstRow_GeneratesNameForExtraColumn_ThenNullFills()
    {
        // The header is A,B but the first DATA row has a third cell, so the schema gains a generated Column3.
        // A subsequent narrower row NULL-fills that generated column.
        var source = CsvFile("long.csv", "A,B\n1,2,3\n4,5\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "A", "B", "Column3" }, columns.Take(3).ToArray());
        Assert.Equal(new object?[] { "1", "2", "3" }, rows[0].Take(3).ToArray());
        Assert.Equal(new object?[] { "4", "5", null }, rows[1].Take(3).ToArray());
    }

    [Fact]
    public async Task TrailingDelimiter_AddsEmptyTrailingColumn_ThatCoercesToNull()
    {
        // A trailing comma yields an extra empty trailing field; the empty value coerces to NULL.
        var source = CsvFile("trail.csv", "A,B\n1,2,\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal("Column3", columns[2]);
        Assert.Equal(new object?[] { "1", "2", null }, rows.Single().Take(3).ToArray());
    }

    [Fact]
    public async Task ExpectedColumnCount_TooShortRow_ThrowsNamingFileAndRow()
    {
        var source = CsvFile("ecshort.csv", "A,B,C\n1,2,3\n4,5\n", new() { ["expectedColumnCount"] = "3" });
        var columns = await _reader.GetColumnsAsync(source);

        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("ecshort.csv", error.Message, StringComparison.Ordinal);
        Assert.Contains("line", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExpectedColumnCount_TooLongRow_ThrowsNamingFile()
    {
        var source = CsvFile("eclong.csv", "A,B,C\n1,2,3\n4,5,6,7\n", new() { ["expectedColumnCount"] = "3" });
        var columns = await _reader.GetColumnsAsync(source);

        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("eclong.csv", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FirstRowSetsExpectedColumnCount_RaggedLaterRow_Throws()
    {
        // The first data row fixes the expected width at 3; a later 2-cell row then violates it.
        var source = CsvFile("frsec.csv", "A,B,C\n1,2,3\n4,5\n", new() { ["firstRowSetsExpectedColumnCount"] = "true" });
        var columns = await _reader.GetColumnsAsync(source);

        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("frsec.csv", error.Message, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> EncodingCases()
    {
        yield return new object[] { "UTF8", new UTF8Encoding(false), "Ærø" };
        yield return new object[] { "UTF16", new UnicodeEncoding(false, false), "Ærø" };
        yield return new object[] { "UTF32", new UTF32Encoding(false, false), "Ærø" };
        yield return new object[] { "ASCII", Encoding.ASCII, "Plain" };
    }

    [Theory]
    [MemberData(nameof(EncodingCases))]
    public async Task Encoding_RoundTripsValuesForDeclaredEncoding(string encodingOption, Encoding encoding, string value)
    {
        var source = CsvFileEncoded("enc.csv", $"OrderId,Customer\n1,{value}\n", encoding, new() { ["encoding"] = encodingOption });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal("1", cells[0]);
        Assert.Equal(value, cells[1]);
    }

    [Fact]
    public async Task Utf32Bom_StripsBomFromFirstHeader()
    {
        // A UTF-32 byte-order mark must not leak into the first column name.
        var source = CsvFileEncoded("bom32.csv", "OrderId,Customer\n1,Acme\n",
            new UTF32Encoding(bigEndian: false, byteOrderMark: true), new() { ["encoding"] = "UTF32" });

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal("OrderId", columns[0]);
    }

    [Fact]
    public async Task DuplicateHeaderNames_AreDeduplicatedWithOrdinalSuffix()
    {
        // Two columns named "A" cannot both be "A" in the target; the legacy cleanup appends the duplicate's
        // zero-based ordinal index, producing A and A1.
        var source = CsvFile("dup.csv", "A,A,B\n1,2,3\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "A", "A1", "B" }, columns.Take(3).ToArray());
        Assert.Equal(new object?[] { "1", "2", "3" }, rows.Single().Take(3).ToArray());
    }

    [Fact]
    public async Task BlankHeaderName_BecomesGeneratedColumnName()
    {
        // A header with an empty middle name gets a positional generated name (Column2), keeping the row aligned.
        var source = CsvFile("blankhdr.csv", "A,,C\n1,2,3\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "A", "Column2", "C" }, columns.Take(3).ToArray());
        Assert.Equal(new object?[] { "1", "2", "3" }, rows.Single().Take(3).ToArray());
    }

    [Fact]
    public async Task AllBlankHeaderRow_ProducesAllGeneratedNames()
    {
        // A header row of only delimiters produces only generated names, one per column.
        var source = CsvFile("allblank.csv", ",,\nx,y,z\n");

        var (columns, _) = await ReadAllAsync(source);

        Assert.Equal(new[] { "Column1", "Column2", "Column3" }, columns.Take(3).ToArray());
    }

    [Fact]
    public async Task HeaderWithInvalidCharacters_IsCleanedToUnderscores()
    {
        // Spaces and punctuation are not valid identifier characters, so each is replaced with an underscore.
        var source = CsvFile("dirty.csv", "Order Id,Amount($)\n1,9.5\n");

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal("Order_Id", columns[0]);
        Assert.Equal("Amount___", columns[1]);  // each of '(', '$', ')' becomes one underscore
    }

    [Fact]
    public async Task HeaderWhitespace_WithoutTrim_IsCleanedToUnderscores()
    {
        // With trim off, leading/trailing spaces in a header survive the parse and are then turned into
        // underscores by the always-on file column-name cleanup.
        var source = CsvFile("hsp.csv", " OrderId , Customer \n1,Acme\n");

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal("_OrderId_", columns[0]);
        Assert.Equal("_Customer_", columns[1]);
    }

    [Fact]
    public async Task VeryLongField_BeyondDefaultBuffer_ThrowsClearError()
    {
        // The default read buffer is small (1 KB). A field larger than the buffer cannot be parsed, and the
        // failure must name the offending file so the cause is obvious.
        var huge = new string('x', 100_000);
        var source = CsvFile("hugefail.csv", $"A,B\n1,{huge}\n");

        // The buffer overflow surfaces as the first row is read (the header read already touches it), so the
        // failure can be raised from either the column read or the streaming read. Drive both so the test is
        // robust to which one trips first, and assert the file is named.
        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            var columns = await _reader.GetColumnsAsync(source);
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("hugefail.csv", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VeryLongField_WithRaisedBuffer_RoundTripsIntact()
    {
        // With the read buffer raised past the field size, a very large field survives intact (no truncation,
        // no split).
        var huge = new string('x', 100_000);
        var source = CsvFile("hugeok.csv", $"A,B\n1,{huge}\n", new() { ["maxBufferSize"] = "2000000" });

        var cells = await SingleDataRowAsync(source, 2);

        Assert.Equal(huge, cells[1]);
        Assert.Equal(100_000, ((string)cells[1]!).Length);
    }

    [Fact]
    public async Task ManyColumns_AreAllResolvedAndAligned()
    {
        // A wide row (60 columns) must resolve every header and align every value.
        const int width = 60;
        var header = string.Join(",", Enumerable.Range(0, width).Select(i => "c" + i.ToString(CultureInfo.InvariantCulture)));
        var dataRow = string.Join(",", Enumerable.Range(0, width).Select(i => "v" + i.ToString(CultureInfo.InvariantCulture)));
        var source = CsvFile("wide.csv", header + "\n" + dataRow + "\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal("c0", columns[0]);
        Assert.Equal("c59", columns[width - 1]);
        Assert.Equal("v59", rows.Single()[width - 1]);
    }

    [Fact]
    public async Task CrOnlyLineEndings_ParseCleanly()
    {
        // Old Mac style: a bare carriage return separates rows (distinct from the CRLF case).
        var source = CsvFile("cr.csv", "OrderId,Customer\r1,Acme\r2,Globex\r");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Acme", rows[0][1]);
        Assert.Equal("Globex", rows[1][1]);
    }

    [Fact]
    public async Task LastRowWithoutTrailingNewline_IsRead()
    {
        // A final data row with no trailing newline must still be read (no dropped last row).
        var source = CsvFile("notrail.csv", "OrderId,Customer\n1,Acme\n2,Globex");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Globex", rows[1][1]);
    }

    [Fact]
    public async Task DefaultCommentCharacter_SkipsHashLines_WithoutOption()
    {
        // The underlying parser treats '#' as a comment by default, so a leading-'#' line is skipped even when
        // no commentCharacter option is supplied.
        var source = CsvFile("hash.csv", "OrderId,Customer\n#1,Acme\n2,Globex\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("2", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task ExplicitCommentCharacter_SkipsMatchingLines()
    {
        // A custom comment character (semicolon) skips lines that start with it.
        var source = CsvFile("semic.csv", "OrderId,Customer\n;a note line\n1,Acme\n", new() { ["commentCharacter"] = ";" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("1", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task MaxRows_CapsLoadedRows_EvenWithSkipEndingDataRows()
    {
        // maxRows bounds the number of rows actually loaded. Combined with skipEndingDataRows, the cap is still
        // honored: no more than maxRows rows are ever yielded.
        var source = CsvFile("cap.csv", "Id\n1\n2\n3\n4\n5\n", new() { ["maxRows"] = "3", ["skipEndingDataRows"] = "1" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(3, rows.Count);
        Assert.True(rows.Count <= 3, "maxRows must cap the number of loaded rows");
    }

    [Fact]
    public async Task IncludeFileLineNumber_AddsTypedLineColumn_ThatStrictlyIncreases()
    {
        // The optional FileLineNumber column carries the file's line position as a long. With a header on line 1
        // the two data rows are lines 2 and 3, so the values are positive and strictly increasing.
        var source = CsvFile("fln.csv", "A,B\n1,x\n2,y\n", new() { ["includeFileLineNumber"] = "true" });

        var resolved = await _reader.GetColumnsAsync(source);
        var lineIdx = resolved.ToList().FindIndex(c => c.Name == "FileLineNumber");
        Assert.True(lineIdx >= 0);
        Assert.Equal(typeof(long), resolved[lineIdx].Type);

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(2L, (long)rows[0][lineIdx]!);
        Assert.Equal(3L, (long)rows[1][lineIdx]!);
    }

    [Fact]
    public async Task RowNumber_RestartsPerFileAcrossFolder()
    {
        // RowNumber_DW is a per-file data-row index, so two files each begin their own numbering at 1.
        File.WriteAllText(Path.Combine(_dir, "p1.csv"), "Id\n10\n11\n");
        File.WriteAllText(Path.Combine(_dir, "p2.csv"), "Id\n20\n21\n22\n");
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" },
        };

        var (columns, rows) = await ReadAllAsync(source);
        var rowNumIdx = columns.IndexOf("RowNumber_DW");

        var perFileMax = rows.Max(r => (long)r[rowNumIdx]!);
        var startCount = rows.Count(r => (long)r[rowNumIdx]! == 1L);

        Assert.Equal(5, rows.Count);
        Assert.Equal(3L, perFileMax);     // the larger file reaches row 3
        Assert.Equal(2, startCount);       // each of the two files contributes a row numbered 1
    }

    [Fact]
    public async Task ShowPathWithFileName_DefaultsToBareFileName()
    {
        // By default the FileName_DW value is the bare name, with no directory component (the complement of the
        // showPathWithFileName=true case covered elsewhere).
        var source = CsvFile("bare.csv", "OrderId\n1\n");
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var nameIdx = columns.FindIndex(c => c.Name == "FileName_DW");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        Assert.True(await data.ReadAsync());

        var name = (string)data.GetValue(nameIdx);
        Assert.Equal("bare.csv", name);
        Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeaderlessNoTrailingNewline_TreatsSingleLineAsData()
    {
        // With headers disabled and no trailing newline, the only line is a data row (not a header).
        var source = CsvFile("hl.csv", "1,Acme,9.5", new() { ["header"] = "false" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "Column1", "Column2", "Column3" }, columns.Take(3).ToArray());
        Assert.Equal(new object?[] { "1", "Acme", "9.5" }, Assert.Single(rows).Take(3).ToArray());
    }

    [Fact]
    public async Task QuotedFieldWithEmbeddedCrlf_PreservesNewline()
    {
        // A quoted value that spans physical lines with CRLF inside keeps the embedded newline (one logical row).
        var source = CsvFile("qcrlf.csv", "A,B\r\n1,\"x\r\ny\"\r\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Single(rows);
        Assert.Equal("x\r\ny", rows[0][1]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
