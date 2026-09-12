using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

public sealed class CsvSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_csv_" + Guid.NewGuid().ToString("N"));
    private readonly CsvSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public CsvSourceReaderTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Csv(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "csv", Location = path };
    }

    private SourceSpec Csv(string fileName, string content, Dictionary<string, string?> options)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "csv", Location = path, Options = options };
    }

    private SourceSpec Folder(Dictionary<string, string?>? options = null)
    {
        options ??= new Dictionary<string, string?>();
        options["srcFile"] = options.GetValueOrDefault("srcFile") ?? "*.csv";
        return new SourceSpec { Type = "csv", Location = _dir, Options = options };
    }

    // Drains the reader fully into materialized rows, the way the bulk loader would, so a test can
    // assert on streamed values without caring that nothing is buffered up front.
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

    [Fact]
    public async Task GetColumns_ReturnsSourceColumnsThenSystemFields()
    {
        var source = Csv("a.csv", "OrderId,Customer,Amount\n1,Acme,9.5\n2,Globex,3.0\n");

        var names = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "OrderId", "Customer", "Amount" }, names.Take(3).ToArray());
        Assert.Contains("FileName_DW", names);
        Assert.Contains("RowNumber_DW", names);
    }

    [Fact]
    public async Task Open_ReadsAllRowsAndReportsManifest()
    {
        var source = Csv("b.csv", "OrderId,Customer\n1,Acme\n2,Globex\n3,Initech\n");
        var columns = await _reader.GetColumnsAsync(source);

        var read = await _reader.OpenAsync(source, columns);
        using var data = read.Reader;

        var rows = 0;
        while (data.Read())
        {
            rows++;
        }

        Assert.Equal(3, rows);
        Assert.Equal(3, Assert.Single(read.ProcessedFiles).Rows);
    }

    [Fact]
    public async Task Folder_UnionsColumnsAcrossFiles()
    {
        Csv("jan.csv", "OrderId,Amount\n1,9.5\n");
        Csv("feb.csv", "OrderId,Country\n2,Norway\n");
        var source = new SourceSpec { Type = "csv", Location = _dir, Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" } };

        var columns = await _reader.GetColumnsAsync(source);

        Assert.Contains(columns, c => c.Name == "OrderId");
        Assert.Contains(columns, c => c.Name == "Amount");
        Assert.Contains(columns, c => c.Name == "Country");
    }

    [Fact]
    public async Task Open_MalformedRow_ErrorNamesFileAndRow()
    {
        var source = new SourceSpec
        {
            Type = "csv",
            Location = Path.Combine(_dir, "bad.csv"),
            Options = new Dictionary<string, string?> { ["expectedColumnCount"] = "3" },
        };
        File.WriteAllText(source.Location, "A,B,C\n1,2,3\n4,5\n");

        var columns = await _reader.GetColumnsAsync(source);

        // Streaming defers parsing to consumption, so the row-level error surfaces while draining the
        // reader (not at open). It must still name the file and the offending row.
        var read = await _reader.OpenAsync(source, columns);
        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = read.Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("bad.csv", error.Message, StringComparison.Ordinal);
        Assert.Contains("row", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_StreamsVersionedFiles_NullFillsMissingColumns()
    {
        // Two "versions" of the same feed with different column sets. Schema evolution unions the
        // columns; streaming must still align each file's row to the unified shape and NULL-fill
        // whatever that file does not carry.
        Csv("v1.csv", "OrderId,Amount\n1,9.5\n");
        Csv("v2.csv", "OrderId,Country\n2,Norway\n");
        var source = new SourceSpec { Type = "csv", Location = _dir, Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" } };

        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var orderIdx = columns.FindIndex(c => c.Name == "OrderId");
        var amountIdx = columns.FindIndex(c => c.Name == "Amount");
        var countryIdx = columns.FindIndex(c => c.Name == "Country");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = new List<(string Order, object Amount, object Country)>();
        while (await data.ReadAsync())
        {
            rows.Add((data.GetValue(orderIdx).ToString()!, data.GetValue(amountIdx), data.GetValue(countryIdx)));
        }

        Assert.Contains(rows, r => r.Order == "1" && r.Amount is "9.5" && r.Country is DBNull);
        Assert.Contains(rows, r => r.Order == "2" && r.Country is "Norway" && r.Amount is DBNull);
    }

    [Fact]
    public async Task Open_InjectsSystemColumnValuesPerRow()
    {
        var source = Csv("sys.csv", "OrderId,Customer\n1,Acme\n2,Globex\n");
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var nameIdx = columns.FindIndex(c => c.Name == "FileName_DW");
        var sizeIdx = columns.FindIndex(c => c.Name == "FileSize_DW");
        var rowNumIdx = columns.FindIndex(c => c.Name == "RowNumber_DW");

        Assert.True(nameIdx >= 0 && sizeIdx >= 0 && rowNumIdx >= 0);

        // The raw landing layer is untyped: file provenance lands as strings (the transformation view
        // applies the real types downstream), while the row number stays a real long from the parser.
        Assert.Equal(typeof(string), columns[nameIdx].Type);
        Assert.Equal(typeof(string), columns[sizeIdx].Type);
        Assert.Equal(typeof(long), columns[rowNumIdx].Type);

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rowNumbers = new List<long>();
        while (await data.ReadAsync())
        {
            Assert.EndsWith("sys.csv", data.GetString(nameIdx), StringComparison.Ordinal);
            Assert.True(long.Parse(data.GetString(sizeIdx), CultureInfo.InvariantCulture) > 0);
            rowNumbers.Add(data.GetInt64(rowNumIdx));
        }

        // Two data rows, each stamped with its own row number from the parser.
        Assert.Equal(2, rowNumbers.Count);
        Assert.Equal(2, rowNumbers.Distinct().Count());
    }

    [Theory]
    [InlineData(";")]
    [InlineData("|")]
    [InlineData("\\t")]
    public async Task Open_HonorsCustomDelimiter(string delimiter)
    {
        var actual = delimiter == "\\t" ? "\t" : delimiter;
        var source = Csv("d.csv", $"OrderId{actual}Customer\n1{actual}Acme\n", new() { ["delimiter"] = delimiter });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "OrderId", "Customer" }, columns.Take(2).ToArray());
        Assert.Equal("1", rows.Single()[0]);
        Assert.Equal("Acme", rows.Single()[1]);
    }

    [Fact]
    public async Task Open_TextQualifier_AllowsDelimiterInsideQuotedField()
    {
        var source = Csv("q.csv", "OrderId,Customer\n1,\"Acme, Inc.\"\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("Acme, Inc.", rows.Single()[1]);
    }

    [Fact]
    public async Task Open_QuotedField_PreservesEmbeddedNewline()
    {
        var source = Csv("nl.csv", "OrderId,Note\n1,\"line one\nline two\"\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Single(rows);
        Assert.Equal("line one\nline two", rows[0][1]);
    }

    [Fact]
    public async Task Open_EmptyField_BecomesDbNull()
    {
        var source = Csv("e.csv", "OrderId,Customer\n1,\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("1", rows.Single()[0]);
        Assert.Null(rows.Single()[1]);
    }

    [Fact]
    public async Task Open_TrimResults_StripsSurroundingWhitespace()
    {
        var source = Csv("t.csv", "OrderId,Customer\n  1  ,  Acme  \n", new() { ["trim"] = "true" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("1", rows.Single()[0]);
        Assert.Equal("Acme", rows.Single()[1]);
    }

    [Fact]
    public async Task Open_SkipStartingDataRows_SkipsLeadingRows()
    {
        var source = Csv("ss.csv", "OrderId\n1\n2\n3\n", new() { ["skipStartingDataRows"] = "2" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("3", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_SkipEndingDataRows_DropsTrailingRows()
    {
        var source = Csv("se.csv", "OrderId\n1\n2\n3\n4\n5\n", new() { ["skipEndingDataRows"] = "2" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "1", "2", "3" }, rows.Select(r => (string?)r[0]).ToArray());
    }

    [Fact]
    public async Task Open_SkipEndingDataRows_LargerThanRowCount_YieldsNothing()
    {
        var source = Csv("se2.csv", "OrderId\n1\n2\n", new() { ["skipEndingDataRows"] = "10" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Empty(rows);
        // The file is still reported as processed, with zero kept rows.
        var read = await _reader.OpenAsync(source, await _reader.GetColumnsAsync(source));
        await using var data = read.Reader;
        while (await data.ReadAsync()) { }
        Assert.Equal(0, Assert.Single(read.ProcessedFiles).Rows);
    }

    [Fact]
    public async Task Open_MaxRows_TruncatesWithinFile()
    {
        var source = Csv("m.csv", "OrderId\n1\n2\n3\n4\n5\n", new() { ["maxRows"] = "2" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "1", "2" }, rows.Select(r => (string?)r[0]).ToArray());
    }

    [Fact]
    public async Task Open_MaxRows_StopsPartwayThroughFolder()
    {
        Csv("p1.csv", "OrderId\n1\n2\n3\n");
        Csv("p2.csv", "OrderId\n4\n5\n6\n");
        var source = Folder(new() { ["maxRows"] = "4" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(4, rows.Count);
    }

    [Fact]
    public async Task Headerless_GeneratesColumnNamesByCounter()
    {
        // Matches the original SQLFlow behaviour: no header means columns are named Column1, Column2,
        // ... and every line (including the first) is data.
        var source = Csv("nh.csv", "1,Acme,9.5\n2,Globex,3.0\n", new() { ["header"] = "false" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "Column1", "Column2", "Column3" }, columns.Take(3).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0][0]);
        Assert.Equal("Acme", rows[0][1]);
        Assert.Equal("9.5", rows[0][2]);
    }

    [Fact]
    public async Task Headerless_TenPlusColumns_AreNotZeroPadded()
    {
        var line = string.Join(",", Enumerable.Range(0, 11).Select(i => $"v{i}"));
        var source = Csv("wide.csv", line + "\n", new() { ["header"] = "false" });

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal("Column1", columns[0]);
        Assert.Equal("Column10", columns[9]);
        Assert.Equal("Column11", columns[10]);
    }

    [Fact]
    public async Task Headerless_RaggedRows_SizesSchemaFromWidestRow()
    {
        // A headerless file names its columns positionally, so the width is the file's WIDEST row, not its
        // first. Mixed-record-type exports look exactly like this: the settlement file opens with a
        // narrow 28-field header record and follows it with 30-field transaction rows. Sizing from row one
        // would drop the last two fields of every transaction.
        var source = Csv("ragged.csv", "a,b,c\nd,e,f,g,h\ni,j\n", new() { ["header"] = "false" });

        var (columns, rows) = await ReadAllAsync(source);

        // Take(5): the provenance columns follow the data columns in the union.
        Assert.Equal(new[] { "Column1", "Column2", "Column3", "Column4", "Column5" }, columns.Take(5).ToArray());
        Assert.Equal(3, rows.Count);
        Assert.Equal("h", rows[1][4]);      // the wide row keeps its trailing fields
        Assert.Null(rows[0][3]);            // narrower rows leave the extra positions empty
        Assert.Null(rows[2][2]);
    }

    [Fact]
    public async Task SrcEncoding_Latin1_DecodesHighBytesFaithfully()
    {
        // Legacy feeds are delivered in single-byte code pages; the settlement files are Latin1 and
        // carry Norwegian text ("Beløp - netto"). Read as UTF-8, 0xF8 is an invalid start byte and the
        // value is corrupted, so the declared encoding must actually be honoured.
        var path = Path.Combine(_dir, "latin1.csv");
        File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes("Tekst\nBeløp - netto\n"));
        var source = new SourceSpec
        {
            Type = "csv",
            Location = path,
            Options = new Dictionary<string, string?> { ["srcEncoding"] = "Latin1" },
        };

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("Beløp - netto", rows[0][0]);
    }

    [Fact]
    public async Task SrcEncoding_Unknown_FailsInsteadOfSilentlyFallingBackToUtf8()
    {
        var source = Csv("bad-encoding.csv", "Id\n1\n", new() { ["srcEncoding"] = "not-an-encoding" });

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(source));

        Assert.Contains("not-an-encoding", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_HeaderOnlyFile_YieldsNoRowsButReportsManifest()
    {
        var source = Csv("h.csv", "OrderId,Customer\n");

        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;

        var rows = 0;
        while (await data.ReadAsync())
        {
            rows++;
        }

        Assert.Equal(0, rows);
        Assert.Contains(columns, c => c.Name == "OrderId");
        Assert.Equal(0, Assert.Single(read.ProcessedFiles).Rows);
    }

    [Fact]
    public async Task Open_CommentCharacter_SkipsCommentLines()
    {
        var source = Csv("c.csv", "OrderId\n1\n#a comment\n2\n", new() { ["commentCharacter"] = "#" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "1", "2" }, rows.Select(r => (string?)r[0]).ToArray());
    }

    [Fact]
    public async Task Open_FixedWidth_ParsesByColumnWidths()
    {
        // Header and data are both split by the declared widths (3, then 5); each line is exactly
        // 8 characters so it maps cleanly onto the two columns.
        var source = Csv("fw.csv", "ID VALUE\nabc12345\n", new() { ["columnWidths"] = "3,5", ["trim"] = "true" });

        var (_, rows) = await ReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Equal("abc", row[0]);
        Assert.Equal("12345", row[1]);
    }

    [Fact]
    public async Task Open_SearchSubDirectories_FindsNestedFiles()
    {
        var sub = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.csv"), "OrderId\n42\n");
        var source = Folder(new() { ["searchSubDirectories"] = "true" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("42", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_DoesNotRecurse_WhenSearchSubDirectoriesOff()
    {
        var sub = Path.Combine(_dir, "nested");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "deep.csv"), "OrderId\n42\n");
        Csv("top.csv", "OrderId\n1\n");
        var source = Folder();

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("1", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_MultipleFiles_ReportsPerFileRowCounts()
    {
        Csv("f1.csv", "OrderId\n1\n2\n");
        Csv("f2.csv", "OrderId\n3\n4\n5\n");
        var source = Folder();

        var columns = await _reader.GetColumnsAsync(source);
        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        while (await data.ReadAsync()) { }

        Assert.Equal(2, read.ProcessedFiles.Count);
        Assert.Equal(new[] { 2L, 3L }, read.ProcessedFiles.Select(f => f.Rows).OrderBy(r => r).ToArray());
    }

    [Fact]
    public async Task GetColumns_DisabledSystemColumns_AreNotIncluded()
    {
        var source = Csv("toggle.csv", "OrderId\n1\n", new()
        {
            ["includeFileName"] = "false",
            ["includeFileDate"] = "false",
            ["includeRowNumber"] = "false",
        });

        var names = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.DoesNotContain("FileName_DW", names);
        Assert.DoesNotContain("FileDate_DW", names);
        Assert.DoesNotContain("RowNumber_DW", names);
    }

    [Fact]
    public async Task Open_ShowPathWithFileName_UsesFullPath()
    {
        var source = Csv("path.csv", "OrderId\n1\n", new() { ["showPathWithFileName"] = "true" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var nameIdx = columns.FindIndex(c => c.Name == "FileName_DW");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        await data.ReadAsync();

        Assert.Contains(Path.DirectorySeparatorChar.ToString(), (string)data.GetValue(nameIdx), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_NoFilesMatch_Throws()
    {
        Csv("present.csv", "OrderId\n1\n");
        var source = Folder(new() { ["srcFile"] = "*.nomatch" });

        await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task Open_MissingLocation_Throws()
    {
        var source = new SourceSpec { Type = "csv", Location = null };

        await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task SharedReader_HandlesConcurrentSourcesIndependently()
    {
        // The reader is a stateless singleton; reading two different sources at once must not let one
        // stream's columns, row counts, or values bleed into the other.
        var small = Csv("small.csv", "OrderId\n1\n2\n");
        var large = Csv("large.csv", "OrderId,Extra\n" + string.Concat(Enumerable.Range(0, 500).Select(i => $"{i},x\n")));

        var smallTask = ReadAllAsync(small);
        var largeTask = ReadAllAsync(large);
        var (smallCols, smallRows) = await smallTask;
        var (largeCols, largeRows) = await largeTask;

        Assert.Equal(2, smallRows.Count);
        Assert.Equal(500, largeRows.Count);
        Assert.DoesNotContain("Extra", smallCols);
        Assert.Contains("Extra", largeCols);
    }

    [Fact]
    public async Task Open_InitFromToFileDate_FiltersFilesByModifiedDate()
    {
        // Simulate a folder of files with different modified dates by stamping each file's
        // LastWriteTime, then bound ingestion to a window. Only the in-window file should load.
        void Dated(string name, string content, DateTime utc)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, utc);
        }

        Dated("old.csv", "OrderId\n1\n", new DateTime(2024, 1, 10, 0, 0, 0, DateTimeKind.Utc));
        Dated("inwindow.csv", "OrderId\n2\n3\n", new DateTime(2024, 2, 15, 0, 0, 0, DateTimeKind.Utc));
        Dated("future.csv", "OrderId\n4\n", new DateTime(2024, 3, 20, 0, 0, 0, DateTimeKind.Utc));

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["initFromFileDate"] = "2024-02-01",
                ["initToFileDate"] = "2024-03-01",
            },
        };

        var (_, rows) = await ReadAllAsync(source);

        // Only inwindow.csv (2 rows) falls inside the window; old.csv and future.csv are excluded.
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Open_SrcPathMask_RegexFiltersByFileName()
    {
        Csv("orders_20240115.csv", "OrderId\n1\n");
        Csv("orders_20240220.csv", "OrderId\n2\n2b\n");
        Csv("returns_20240115.csv", "OrderId\n9\n");
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv", ["srcPathMask"] = @"orders_\d{8}\.csv$" },
        };

        var (_, rows) = await ReadAllAsync(source);

        // Only the two orders_* files match the regex; returns_* is excluded.
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_SrcPathMask_RegexFiltersBySubfolder()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "east"));
        Directory.CreateDirectory(Path.Combine(_dir, "west"));
        File.WriteAllText(Path.Combine(_dir, "east", "a.csv"), "Id\n1\n");
        File.WriteAllText(Path.Combine(_dir, "west", "b.csv"), "Id\n2\n");
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["searchSubDirectories"] = "true",
                ["srcPathMask"] = @"[\\/]east[\\/]",
            },
        };

        var (_, rows) = await ReadAllAsync(source);

        // Only files under the east folder match the path regex.
        Assert.Equal("1", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_AllFiltersCompose_GlobThenMaskThenDateWindow()
    {
        void Dated(string name, string content, DateTime utc)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, utc);
        }

        // Matches glob + mask, but outside the date window.
        Dated("orders_old.csv", "Id\n1\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        // Matches glob + mask, inside the window: the only row that should load.
        Dated("orders_now.csv", "Id\n2\n", new DateTime(2024, 2, 15, 0, 0, 0, DateTimeKind.Utc));
        // Inside the window but fails the mask (wrong name).
        Dated("returns_now.csv", "Id\n3\n", new DateTime(2024, 2, 16, 0, 0, 0, DateTimeKind.Utc));
        // Right name and window but wrong extension, excluded by the glob.
        Dated("orders_now.txt", "Id\n4\n", new DateTime(2024, 2, 17, 0, 0, 0, DateTimeKind.Utc));

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["srcPathMask"] = @"orders_",
                ["initFromFileDate"] = "2024-02-01",
                ["initToFileDate"] = "2024-03-01",
            },
        };

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("2", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_InvalidSrcPathMask_Throws()
    {
        Csv("a.csv", "Id\n1\n");
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv", ["srcPathMask"] = "[unterminated" },
        };

        var error = await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("srcPathMask", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Open_IncrementalAfterDate_IsExclusiveLowerBound()
    {
        void Dated(string name, string content, DateTime utc)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, content);
            File.SetLastWriteTimeUtc(path, utc);
        }

        // Boundary file (exactly at the watermark) must be excluded; only strictly-newer files load.
        Dated("at.csv", "OrderId\n1\n", new DateTime(2024, 2, 1, 12, 0, 0, DateTimeKind.Utc));
        Dated("after.csv", "OrderId\n2\n3\n", new DateTime(2024, 2, 1, 12, 0, 1, DateTimeKind.Utc));

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["incrementalAfterDate"] = "2024-02-01T12:00:00.0000000Z",
            },
        };

        var (_, rows) = await ReadAllAsync(source);

        // Only after.csv (2 rows) is strictly newer than the watermark; at.csv is excluded.
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Open_InitFromToFileDate_NoFilesInWindow_Throws()
    {
        var path = Path.Combine(_dir, "only.csv");
        File.WriteAllText(path, "OrderId\n1\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv", ["initFromFileDate"] = "2030-01-01" },
        };

        var error = await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("date window", error.Message, StringComparison.OrdinalIgnoreCase);

        // The window, not an absent file, is what emptied the selection: the one candidate was examined.
        Assert.Equal(NoSourceFilesReason.NoneSelected, error.Reason);
        Assert.Contains("examined 1 file(s)", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty location and a location whose files are all older than the watermark are different outcomes, and
    /// the second must never be reported as the first: it is the normal resting state of an incremental flow,
    /// while the first is what a wrong path or pattern looks like.
    /// </summary>
    [Fact]
    public async Task Open_IncrementalAfterDate_FilesExistButNoneNewer_ReportsNoneAfterWatermark()
    {
        var path = Path.Combine(_dir, "old.csv");
        File.WriteAllText(path, "OrderId\n1\n");
        File.SetLastWriteTimeUtc(path, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["incrementalAfterDate"] = "2024-02-01T12:00:00.0000000Z",
            },
        };

        var error = await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));

        Assert.Equal(NoSourceFilesReason.NoneAfterWatermark, error.Reason);
        Assert.Contains("2024-02-01 12:00:00Z", error.Message, StringComparison.Ordinal);
        Assert.Contains("examined 1 file(s)", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_IncrementalAfterDate_LocationEmpty_ReportsNoCandidatesAndBlamesNoWatermark()
    {
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["incrementalAfterDate"] = "2024-02-01T12:00:00.0000000Z",
            },
        };

        var error = await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));

        // Nothing ever reached the watermark test, so the message must not imply the watermark excluded anything.
        Assert.Equal(NoSourceFilesReason.NoCandidates, error.Reason);
        Assert.DoesNotContain("watermark", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("newer than", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("*.csv", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The partitioned-lake shape: the date lives in the path, so whole out-of-window folders are pruned and no
    /// file is ever examined. The tally must still separate this from an empty location, otherwise the cheaper
    /// the pruning gets the more it looks like a misconfigured path.
    /// </summary>
    [Fact]
    public async Task Open_IncrementalAfterDate_PartitionFoldersAllPruned_StillReportsNoneAfterWatermark()
    {
        var partition = Path.Combine(_dir, "year=2024", "month=01", "day=15");
        Directory.CreateDirectory(partition);
        File.WriteAllText(Path.Combine(partition, "orders.csv"), "OrderId\n1\n");

        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?>
            {
                ["srcFile"] = "*.csv",
                ["searchSubDirectories"] = "true",
                ["fileDate.from"] = "path",
                ["fileDate.hive"] = "true",
                ["incrementalAfterDate"] = "2025-06-01T00:00:00.0000000Z",
            },
        };

        var error = await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));

        // The 2024 partition never overlaps a 2025 watermark, so it is skipped whole: nothing was examined, yet
        // this is emphatically not an empty location.
        Assert.Equal(NoSourceFilesReason.NoneAfterWatermark, error.Reason);
        Assert.Contains("pruned 1 out-of-window folder(s)", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("examined", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Open_NoFilesMatchTheGlob_ReportsNoCandidates()
    {
        Csv("present.csv", "OrderId\n1\n");
        var source = Folder(new() { ["srcFile"] = "*.nomatch" });

        var error = await Assert.ThrowsAsync<NoSourceFilesException>(() => _reader.GetColumnsAsync(source));

        Assert.Equal(NoSourceFilesReason.NoCandidates, error.Reason);
    }

    [Fact]
    public async Task HashKey_InjectedAsVarbinary_DeterministicAndExcludesSystemColumns()
    {
        // The same logical row in two differently named files must hash identically, proving the
        // provenance columns (file name, dates, row number) are excluded from the hash by default.
        Csv("fileA.csv", "OrderId,Customer\n1,Acme\n");
        Csv("fileB.csv", "OrderId,Customer\n1,Acme\n");
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv", ["includeHashKey"] = "true" },
        };

        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var hashIdx = columns.FindIndex(c => c.Name == "HashKey_DW");

        Assert.True(hashIdx >= 0);
        Assert.Equal(typeof(byte[]), columns[hashIdx].Type);
        Assert.Equal("varbinary(64)", columns[hashIdx].SqlType);

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var hashes = new List<string>();
        while (await data.ReadAsync())
        {
            hashes.Add(Convert.ToHexString((byte[])data.GetValue(hashIdx)));
        }

        Assert.Equal(2, hashes.Count);
        Assert.Equal(128, hashes[0].Length); // SHA-512 = 64 bytes = 128 hex chars
        Assert.Equal(hashes[0], hashes[1]);  // identical despite different file names
    }

    [Fact]
    public async Task HashKey_DiffersForDifferentRows()
    {
        var source = Csv("h.csv", "OrderId,Customer\n1,Acme\n2,Globex\n", new() { ["includeHashKey"] = "true" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var hashIdx = columns.FindIndex(c => c.Name == "HashKey_DW");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var hashes = new List<string>();
        while (await data.ReadAsync())
        {
            hashes.Add(Convert.ToHexString((byte[])data.GetValue(hashIdx)));
        }

        Assert.Equal(2, hashes.Distinct().Count());
    }

    [Fact]
    public async Task HashKey_RespectsChosenColumns()
    {
        // Two rows differ only in 'Note', which is excluded from the hash, so the hashes must match.
        var source = Csv("hc.csv", "OrderId,Customer,Note\n1,Acme,x\n1,Acme,y\n",
            new() { ["includeHashKey"] = "true", ["hashKeyColumns"] = "OrderId,Customer" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var hashIdx = columns.FindIndex(c => c.Name == "HashKey_DW");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        var hashes = new List<string>();
        while (await data.ReadAsync())
        {
            hashes.Add(Convert.ToHexString((byte[])data.GetValue(hashIdx)));
        }

        Assert.Equal(hashes[0], hashes[1]);
    }

    [Fact]
    public async Task HashKey_Sha256_ProducesShorterDigest()
    {
        var source = Csv("h256.csv", "OrderId\n1\n", new() { ["includeHashKey"] = "true", ["hashKeyType"] = "SHA2_256" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var hashIdx = columns.FindIndex(c => c.Name == "HashKey_DW");

        Assert.Equal("varbinary(32)", columns[hashIdx].SqlType);

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        Assert.True(await data.ReadAsync());
        Assert.Equal(32, ((byte[])data.GetValue(hashIdx)).Length);
    }

    [Fact]
    public async Task HashKey_ExplicitColumnsMatchNothing_Throws()
    {
        var source = Csv("hk0.csv", "OrderId,Customer\n1,Acme\n",
            new() { ["includeHashKey"] = "true", ["hashKeyColumns"] = "DoesNotExist" });
        var columns = await _reader.GetColumnsAsync(source);

        var error = await Assert.ThrowsAsync<SqlFlowException>(async () =>
        {
            await using var data = (await _reader.OpenAsync(source, columns)).Reader;
            while (await data.ReadAsync())
            {
            }
        });

        Assert.Contains("did not match any column", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcatKey_JoinsChosenColumnsWithSeparator()
    {
        var source = Csv("ck.csv", "OrderId,Customer,Amount\n1,Acme,9.5\n",
            new() { ["includeConcatKey"] = "true", ["concatKeyColumns"] = "OrderId,Customer", ["concatKeySeparator"] = "-" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var concatIdx = columns.FindIndex(c => c.Name == "ConcatKey_DW");

        Assert.True(concatIdx >= 0);
        Assert.Equal(typeof(string), columns[concatIdx].Type);

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        Assert.True(await data.ReadAsync());
        Assert.Equal("1-Acme", data.GetString(concatIdx));
    }

    [Fact]
    public async Task ConcatKey_DefaultsToAllSourceColumns_ExcludingSystem()
    {
        var source = Csv("ckd.csv", "A,B\nx,y\n", new() { ["includeConcatKey"] = "true" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var concatIdx = columns.FindIndex(c => c.Name == "ConcatKey_DW");

        var read = await _reader.OpenAsync(source, columns);
        await using var data = read.Reader;
        Assert.True(await data.ReadAsync());

        // Only the source columns A and B feed the key; provenance columns are excluded.
        Assert.Equal("x|y", data.GetString(concatIdx));
    }

    [Fact]
    public async Task Open_Utf8Bom_StripsBomFromFirstHeader()
    {
        var path = Path.Combine(_dir, "bom.csv");
        File.WriteAllText(path, "OrderId,Customer\n1,Acme\n", new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var source = new SourceSpec { Type = "csv", Location = path };

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal("OrderId", columns[0]); // not "﻿OrderId"
    }

    [Fact]
    public async Task Open_CrlfLineEndings_ParseCleanly()
    {
        var source = Csv("crlf.csv", "OrderId,Customer\r\n1,Acme\r\n2,Globex\r\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Acme", rows[0][1]);
        Assert.Equal("Globex", rows[1][1]);
    }

    [Fact]
    public async Task Open_DoubledQualifier_UnescapesToSingleQuote()
    {
        var source = Csv("dq.csv", "Id,Note\n1,\"say \"\"hi\"\"\"\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal("say \"hi\"", rows.Single()[1]);
    }

    [Fact]
    public async Task Folder_AlignsColumnsByName_NotPosition()
    {
        // Two files declare the same columns in opposite order; rows must align by name, not ordinal.
        Csv("u1.csv", "A,B\n1,2\n");
        Csv("u2.csv", "B,A\n20,10\n");
        var source = Folder();

        var (columns, rows) = await ReadAllAsync(source);
        var a = columns.IndexOf("A");
        var b = columns.IndexOf("B");

        Assert.Contains(rows, r => (string?)r[a] == "1" && (string?)r[b] == "2");
        Assert.Contains(rows, r => (string?)r[a] == "10" && (string?)r[b] == "20");
    }

    [Fact]
    public async Task Open_SingleColumnFile_NoDelimiterPresent()
    {
        var source = Csv("one.csv", "OnlyCol\nalpha\nbeta\n");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal("OnlyCol", columns[0]);
        Assert.Equal(2, rows.Count);
        Assert.Equal("alpha", rows[0][0]);
    }

    [Fact]
    public async Task Open_MaxRowsZero_LoadsAll()
    {
        var source = Csv("m0.csv", "Id\n1\n2\n3\n", new() { ["maxRows"] = "0" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task Open_SkipStartingRows_BeyondRowCount_YieldsNothing()
    {
        var source = Csv("ss0.csv", "Id\n1\n2\n", new() { ["skipStartingDataRows"] = "10" });

        var (_, rows) = await ReadAllAsync(source);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task Open_TrimsHeaderWhitespace()
    {
        var source = Csv("th.csv", " OrderId , Customer \n1,Acme\n", new() { ["trim"] = "true" });

        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Contains("OrderId", columns);
        Assert.Contains("Customer", columns);
    }

    [Fact]
    public async Task HashKey_StableAcrossSeparateReaderInstances()
    {
        var source = Csv("stab.csv", "OrderId,Customer\n1,Acme\n", new() { ["includeHashKey"] = "true" });

        async Task<string> HashAsync()
        {
            var reader = new CsvSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);
            var columns = (await reader.GetColumnsAsync(source)).ToList();
            var idx = columns.FindIndex(c => c.Name == "HashKey_DW");
            await using var data = (await reader.OpenAsync(source, columns)).Reader;
            await data.ReadAsync();
            return Convert.ToHexString((byte[])data.GetValue(idx));
        }

        Assert.Equal(await HashAsync(), await HashAsync());
    }

    [Fact]
    public async Task ConcatKey_HandlesEmptyValues()
    {
        var source = Csv("cke.csv", "A,B,C\nx,,z\n",
            new() { ["includeConcatKey"] = "true", ["concatKeyColumns"] = "A,B,C", ["concatKeySeparator"] = "|" });
        var columns = (await _reader.GetColumnsAsync(source)).ToList();
        var idx = columns.FindIndex(c => c.Name == "ConcatKey_DW");

        await using var data = (await _reader.OpenAsync(source, columns)).Reader;
        await data.ReadAsync();

        Assert.Equal("x||z", data.GetString(idx)); // empty middle value preserved between separators
    }

    [Fact]
    public async Task Complete_CopyToPath_CopiesIngestedFile()
    {
        var copyDir = Path.Combine(_dir, "archive");
        var source = Csv("toCopy.csv", "Id\n1\n", new() { ["copyToPath"] = copyDir });

        await _reader.CompleteAsync(source);

        Assert.True(File.Exists(Path.Combine(copyDir, "toCopy.csv")), "the ingested file should be copied to copyToPath");
    }

    // ---------------------------------------------------------------------------------------------
    // readAhead: how many files the run keeps open at once. It must never change WHAT is read or the
    // order it arrives in, only how much of the per-file latency is overlapped.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("4")]
    public async Task ReadAhead_ProducesTheSameColumnUnionAndRowOrderWhateverTheDepth(string readAhead)
    {
        // Three files with drifting schemas: the union order and the row order are both file-ordered, so a
        // deeper readahead (whose reads finish out of order) must still produce exactly the depth-1 answer.
        Csv("ra_1.csv", "Id,Name\n1,a\n2,b\n");
        Csv("ra_2.csv", "Id,Name,Extra\n3,c,x\n");
        Csv("ra_3.csv", "Id,Late\n4,z\n");

        var (columns, rows) = await ReadAllAsync(Folder(new() { ["srcFile"] = "ra_*.csv", ["readAhead"] = readAhead }));

        Assert.Equal(new[] { "Id", "Name", "Extra", "Late" }, columns.Take(4).ToArray());
        Assert.Equal(new[] { "1", "2", "3", "4" }, rows.Select(r => (string?)r[0]).ToArray());
        Assert.Equal(new[] { "a", "b", "c", null }, rows.Select(r => (string?)r[1]).ToArray());
    }

    [Fact]
    public async Task ReadAhead_OpensThatManyFilesAtOnceAndNeverMore()
    {
        // Proven against a store that records concurrent opens: depth 1 is one file at a time, depth 3
        // overlaps three, and neither exceeds what the flow asked for.
        Assert.Equal(1, await MaxConcurrentOpensAsync(readAhead: 1, files: 6));
        Assert.Equal(3, await MaxConcurrentOpensAsync(readAhead: 3, files: 6));
    }

    [Fact]
    public async Task ReadAhead_DefaultsToTheStreamingDepth_ForAStreamingFormat()
    {
        // CSV streams a file line by line, so an open file is cheap and the default overlaps several.
        Assert.Equal(FileSourceOptions.StreamingDefaultReadAhead, await MaxConcurrentOpensAsync(readAhead: null, files: 8));
    }

    [Fact]
    public async Task ReadAhead_FewerFilesThanTheDepth_OpensOnlyWhatExists()
    {
        Assert.Equal(2, await MaxConcurrentOpensAsync(readAhead: 8, files: 2));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("33")]
    public async Task ReadAhead_OutOfRange_IsRejectedWithTheAllowedRange(string readAhead)
    {
        var source = Csv("bad_depth.csv", "Id\n1\n", new() { ["readAhead"] = readAhead });

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(source));

        Assert.Contains("readAhead", ex.Message, StringComparison.Ordinal);
        Assert.Contains("32", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a full read against a store that tracks how many files are open simultaneously, and reports the
    /// peak. A null <paramref name="readAhead"/> leaves the option unset, so the format's default applies.
    /// </summary>
    private static async Task<int> MaxConcurrentOpensAsync(int? readAhead, int files)
    {
        var store = new ConcurrencyTrackingFileStore(files, "csv", name => $"Id,Name\n{name},x\n");
        var reader = new CsvSourceReader(new LocalFileLifecycle(), [store]);
        var options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" };
        if (readAhead is { } depth)
        {
            options["readAhead"] = depth.ToString(CultureInfo.InvariantCulture);
        }

        var source = new SourceSpec { Type = "csv", Location = ConcurrencyTrackingFileStore.Root, Options = options };

        var columns = await reader.GetColumnsAsync(source);
        await using var data = (await reader.OpenAsync(source, columns)).Reader;
        while (await data.ReadAsync())
        {
            // Drain: the peak is measured across the whole run, schema pass and data pass alike.
        }

        return store.MaxConcurrentOpens;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
