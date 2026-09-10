using ClosedXML.Excel;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

public sealed class XlsSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xls_" + Guid.NewGuid().ToString("N"));
    private readonly XlsSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public XlsSourceReaderTests() => Directory.CreateDirectory(_dir);

    private string Xlsx(string name, Action<XLWorkbook> build)
    {
        var path = Path.Combine(_dir, name);
        using var wb = new XLWorkbook();
        build(wb);
        wb.SaveAs(path);
        return path;
    }

    private static SourceSpec Source(string path, Dictionary<string, string?>? options = null)
        => new() { Type = "xlsx", Location = path, Options = options ?? new Dictionary<string, string?>() };

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
    public void CanHandle_XlsAndXlsx()
    {
        Assert.True(_reader.CanHandle("xls"));
        Assert.True(_reader.CanHandle("XLSX"));
        Assert.False(_reader.CanHandle("csv"));
    }

    [Fact]
    public async Task Open_HeaderAndRows_WithProvenanceColumns()
    {
        var path = Xlsx("orders.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Customer";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "Acme";
            ws.Cell(3, 1).Value = 2;
            ws.Cell(3, 2).Value = "Globex";
        });

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Equal(new[] { "OrderId", "Customer" }, columns.Take(2).ToArray());
        Assert.Contains("FileName_DW", columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0][0]);     // numeric cell rendered as a raw string
        Assert.Equal("Acme", rows[0][1]);
        Assert.Equal("Globex", rows[1][1]);
    }

    [Fact]
    public async Task Open_Headerless_GeneratesColumnNames()
    {
        var path = Xlsx("nh.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = 1;
            ws.Cell(1, 2).Value = "Acme";
            ws.Cell(2, 1).Value = 2;
            ws.Cell(2, 2).Value = "Globex";
        });

        var (columns, rows) = await ReadAllAsync(Source(path, new() { ["header"] = "false" }));

        Assert.Equal(new[] { "Column1", "Column2" }, columns.Take(2).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0][0]);
    }

    [Fact]
    public async Task Open_SelectsSheetByName()
    {
        var path = Xlsx("multi.xlsx", wb =>
        {
            var first = wb.AddWorksheet("First");
            first.Cell(1, 1).Value = "A";
            first.Cell(2, 1).Value = "ignored";
            var second = wb.AddWorksheet("Second");
            second.Cell(1, 1).Value = "OrderId";
            second.Cell(2, 1).Value = 42;
        });

        var (columns, rows) = await ReadAllAsync(Source(path, new() { ["sheetName"] = "Second" }));

        Assert.Equal("OrderId", columns[0]);
        Assert.Equal("42", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_SelectsSheetByIndex()
    {
        var path = Xlsx("idx.xlsx", wb =>
        {
            var first = wb.AddWorksheet("First");
            first.Cell(1, 1).Value = "A";
            first.Cell(2, 1).Value = "x";
            var second = wb.AddWorksheet("Second");
            second.Cell(1, 1).Value = "B";
            second.Cell(2, 1).Value = "y";
        });

        var (columns, rows) = await ReadAllAsync(Source(path, new() { ["sheetName"] = "1", ["useSheetIndex"] = "true" }));

        Assert.Equal("B", columns[0]);
        Assert.Equal("y", Assert.Single(rows)[0]);
    }

    [Fact]
    public async Task Open_SheetRange_BoundsTheCells()
    {
        var path = Xlsx("range.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "junk-preamble";   // A1, outside the range
            ws.Cell(2, 2).Value = "Code";            // B2 header
            ws.Cell(2, 3).Value = "Name";            // C2 header
            ws.Cell(3, 2).Value = 10;
            ws.Cell(3, 3).Value = "Alpha";
            ws.Cell(4, 2).Value = 20;
            ws.Cell(4, 3).Value = "Beta";
            ws.Cell(5, 2).Value = 99;                // outside the range, excluded
        });

        var (columns, rows) = await ReadAllAsync(Source(path, new() { ["sheetRange"] = "B2:C4" }));

        Assert.Equal(new[] { "Code", "Name" }, columns.Take(2).ToArray());
        Assert.Equal(2, rows.Count);
        Assert.Equal("10", rows[0][0]);
        Assert.Equal("Beta", rows[1][1]);
    }

    [Fact]
    public async Task Open_EmptyCell_BecomesNull()
    {
        var path = Xlsx("nulls.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Id";
            ws.Cell(1, 2).Value = "Note";
            ws.Cell(2, 1).Value = 1;
            // B2 deliberately left empty
        });

        var (_, rows) = await ReadAllAsync(Source(path));

        var row = Assert.Single(rows);
        Assert.Equal("1", row[0]);
        Assert.Null(row[1]);
    }

    [Fact]
    public async Task Folder_UnionsColumnsAcrossWorkbooks()
    {
        Xlsx("a.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Amount";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = 9.5;
        });
        Xlsx("b.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Country";
            ws.Cell(2, 1).Value = 2;
            ws.Cell(2, 2).Value = "Norway";
        });

        var source = Source(_dir, new() { ["srcFile"] = "*.xlsx" });
        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("Amount", columns);
        Assert.Contains("Country", columns);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Open_HashKey_IsInjectedForXls()
    {
        var path = Xlsx("hk.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(2, 1).Value = 1;
        });

        var columns = (await _reader.GetColumnsAsync(Source(path, new() { ["includeHashKey"] = "true" }))).ToList();
        var hash = columns.SingleOrDefault(c => c.Name == "HashKey_DW");

        Assert.NotNull(hash);
        Assert.Equal("varbinary(64)", hash!.SqlType);
    }

    [Fact]
    public async Task Open_RendersTypedCells_AsRawStrings()
    {
        var path = Xlsx("types.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "Num";
            ws.Cell(1, 2).Value = "Dec";
            ws.Cell(1, 3).Value = "When";
            ws.Cell(1, 4).Value = "Flag";
            ws.Cell(2, 1).Value = 7;
            ws.Cell(2, 2).Value = 9.5;
            ws.Cell(2, 3).Value = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Unspecified);
            ws.Cell(2, 4).Value = true;
        });

        var (_, rows) = await ReadAllAsync(Source(path));
        var row = Assert.Single(rows);

        Assert.Equal("7", row[0]);                       // integer (Excel double) -> "7"
        Assert.Equal("9.5", row[1]);                     // decimal
        Assert.Equal("2024-01-15 00:00:00", row[2]);     // date -> ISO-ish invariant string
        Assert.Equal("True", row[3]);                    // boolean
    }

    [Fact]
    public async Task Open_HeaderOnlySheet_YieldsNoRowsButKeepsColumns()
    {
        var path = Xlsx("headeronly.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Customer";
        });

        var (columns, rows) = await ReadAllAsync(Source(path));

        Assert.Contains("OrderId", columns);
        Assert.Contains("Customer", columns);
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Open_RaggedRow_NullFillsMissingTrailingCells()
    {
        var path = Xlsx("ragged.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(1, 2).Value = "B";
            ws.Cell(1, 3).Value = "C";
            ws.Cell(2, 1).Value = "x";
            ws.Cell(2, 2).Value = "y";
            // C2 left empty -> ragged
        });

        var columns = (await _reader.GetColumnsAsync(Source(path))).ToList();
        var cIdx = columns.FindIndex(c => c.Name == "C");
        var (_, rows) = await ReadAllAsync(Source(path));

        Assert.Null(Assert.Single(rows)[cIdx]);
    }

    [Fact]
    public async Task Open_SheetRange_SingleCellStart_ReadsToSheetEnd()
    {
        var path = Xlsx("openrange.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(2, 2).Value = "Code";
            ws.Cell(3, 2).Value = 1;
            ws.Cell(4, 2).Value = 2;
        });

        var (columns, rows) = await ReadAllAsync(Source(path, new() { ["sheetRange"] = "B2" }));

        Assert.Equal("Code", columns[0]);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Open_SheetNotFound_Throws()
    {
        var path = Xlsx("one.xlsx", wb => wb.AddWorksheet("Only").Cell(1, 1).Value = "A");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(Source(path, new() { ["sheetName"] = "Missing" })));
    }

    [Fact]
    public async Task Open_SheetIndexOutOfRange_Throws()
    {
        var path = Xlsx("oneidx.xlsx", wb => wb.AddWorksheet("Only").Cell(1, 1).Value = "A");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(Source(path, new() { ["sheetName"] = "5", ["useSheetIndex"] = "true" })));
    }

    [Fact]
    public async Task Open_InvalidSheetRange_Throws()
    {
        var path = Xlsx("badrange.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "A";
            ws.Cell(2, 1).Value = "x";
        });

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _reader.GetColumnsAsync(Source(path, new() { ["sheetRange"] = "not-a-range" })));
    }

    [Fact]
    public async Task Open_ProvenanceColumns_HaveCorrectTypes()
    {
        var path = Xlsx("prov.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(2, 1).Value = 1;
        });

        var columns = await _reader.GetColumnsAsync(Source(path, new() { ["includeFileLineNumber"] = "true" }));

        // File provenance lands as strings (the raw layer is untyped; the transformation view applies
        // the real types), while the row/line counters stay real longs from the reader.
        Assert.Equal(typeof(string), columns.Single(c => c.Name == "FileSize_DW").Type);
        Assert.Equal(typeof(string), columns.Single(c => c.Name == "FileDate_DW").Type);
        Assert.Equal(typeof(long), columns.Single(c => c.Name == "RowNumber_DW").Type);
        Assert.Equal(typeof(long), columns.Single(c => c.Name == "FileLineNumber").Type);
    }

    [Fact]
    public async Task Open_NoSystemColumns_LeavesOnlySourceColumns()
    {
        var path = Xlsx("nosys.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(2, 1).Value = 1;
        });

        var names = (await _reader.GetColumnsAsync(Source(path, new()
        {
            ["includeFileName"] = "false",
            ["includeFileDate"] = "false",
            ["includeFileRowDate"] = "false",
            ["includeFileSize"] = "false",
            ["includeDataSet"] = "false",
            ["includeRowNumber"] = "false",
        }))).Select(c => c.Name).ToList();

        Assert.Equal(["OrderId"], names);
    }

    [Fact]
    public async Task Open_ConcatKey_BuildsForXls()
    {
        var path = Xlsx("ck.xlsx", wb =>
        {
            var ws = wb.AddWorksheet("Data");
            ws.Cell(1, 1).Value = "OrderId";
            ws.Cell(1, 2).Value = "Customer";
            ws.Cell(2, 1).Value = 1;
            ws.Cell(2, 2).Value = "Acme";
        });

        var columns = (await _reader.GetColumnsAsync(Source(path,
            new() { ["includeConcatKey"] = "true", ["concatKeyColumns"] = "OrderId,Customer", ["concatKeySeparator"] = "-" }))).ToList();
        var idx = columns.FindIndex(c => c.Name == "ConcatKey_DW");

        await using var data = (await _reader.OpenAsync(Source(path,
            new() { ["includeConcatKey"] = "true", ["concatKeyColumns"] = "OrderId,Customer", ["concatKeySeparator"] = "-" }), columns)).Reader;
        Assert.True(await data.ReadAsync());

        Assert.Equal("1-Acme", data.GetString(idx));
    }

    [Fact]
    public async Task Open_IncrementalAfterDate_ExcludesOlderWorkbooks()
    {
        void Dated(string name, DateTime utc)
        {
            var p = Xlsx(name, wb =>
            {
                var ws = wb.AddWorksheet("Data");
                ws.Cell(1, 1).Value = "OrderId";
                ws.Cell(2, 1).Value = 1;
            });
            File.SetLastWriteTimeUtc(p, utc);
        }

        Dated("old.xlsx", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Dated("new.xlsx", new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        var source = Source(_dir, new()
        {
            ["srcFile"] = "*.xlsx",
            ["incrementalAfterDate"] = "2024-02-01T00:00:00.0000000Z",
        });

        var (_, rows) = await ReadAllAsync(source);

        // Only new.xlsx is strictly newer than the watermark; old.xlsx is excluded.
        Assert.Single(rows);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
