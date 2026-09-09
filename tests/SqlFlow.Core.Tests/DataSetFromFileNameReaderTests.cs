using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// End-to-end (no database) verification that the file readers populate DataSet_DW from a date detected in the
/// file NAME while FileDate_DW stays the file's last-modified timestamp, that a name with no date falls back to
/// last-modified, and that <c>dataSetFromFileName: false</c> restores the legacy-identical behavior where
/// DataSet_DW equals FileDate_DW. Exercises the shared pipeline through the CSV reader.
/// </summary>
public sealed class DataSetFromFileNameReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_dataset_" + Guid.NewGuid().ToString("N"));
    private readonly CsvSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public DataSetFromFileNameReaderTests() => Directory.CreateDirectory(_dir);

    // Writes a CSV whose last-modified time is pinned distinct from any date in its name, so the two provenance
    // columns are distinguishable.
    private SourceSpec Csv(string fileName, DateTime modifiedUtc, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "OrderId,Amount\n1,9.5\n2,3.0\n");
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new SourceSpec { Type = "csv", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private void WriteCsv(string fileName, DateTime modifiedUtc)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, "OrderId,Amount\n1,9.5\n");
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> ReadAllAsync(SourceSpec source)
    {
        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();
        var read = await _reader.OpenAsync(source, await _reader.GetColumnsAsync(source));
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

        return (columns, rows);
    }

    private async Task<(int FileDate, int DataSet, List<object?[]> Rows)> ReadAsync(SourceSpec source)
    {
        var columns = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();
        var read = await _reader.OpenAsync(source, await _reader.GetColumnsAsync(source));
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

        return (columns.IndexOf("FileDate_DW"), columns.IndexOf("DataSet_DW"), rows);
    }

    [Fact]
    public async Task DataSet_TakesFilenameDate_WhileFileDate_StaysLastModified()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("sess_20240101.csv", modified));

        Assert.NotEmpty(rows);
        Assert.All(rows, r =>
        {
            Assert.Equal("20240615090000", r[fileDate]);   // last-modified
            Assert.Equal("20240101000000", r[dataSet]);    // date parsed from the name
        });
    }

    [Fact]
    public async Task DataSet_FallsBackToLastModified_WhenNameHasNoDate()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("orders_batch.csv", modified));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal(r[fileDate], r[dataSet]));   // both = last-modified
        Assert.All(rows, r => Assert.Equal("20240615090000", r[dataSet]));
    }

    [Fact]
    public async Task DataSet_EqualsLastModified_WhenOptedOut()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var options = new Dictionary<string, string?> { ["dataSetFromFileName"] = "false" };
        var (fileDate, dataSet, rows) = await ReadAsync(Csv("sess_20240101.csv", modified, options));

        Assert.NotEmpty(rows);
        // Opted out: the name date is ignored, DataSet_DW == FileDate_DW (the pre-port behavior).
        Assert.All(rows, r => Assert.Equal("20240615090000", r[dataSet]));
        Assert.All(rows, r => Assert.Equal(r[fileDate], r[dataSet]));
    }

    [Fact]
    public async Task DataSet_UsesCustomFormat_FromOptions()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        var options = new Dictionary<string, string?> { ["dataSetFormats"] = "yyMMdd" };
        var (_, dataSet, rows) = await ReadAsync(Csv("bill_240301.csv", modified, options));

        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Equal("20240301000000", r[dataSet]));   // yyMMdd -> 2024-03-01
    }

    [Fact]
    public async Task DataSet_InfersMonthFirst_FromSiblingFile_AcrossTheSet()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        // 03-15-2024 can only be MM-dd (month 15 impossible the other way); it teaches the whole set month-first,
        // which then decides the otherwise-ambiguous 01-02-2024 in the SAME run.
        WriteCsv("03-15-2024.csv", modified);
        WriteCsv("01-02-2024.csv", modified);
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" },
        };

        var (columns, rows) = await ReadAllAsync(source);
        var nameIdx = columns.IndexOf("FileName_DW");
        var dataSetIdx = columns.IndexOf("DataSet_DW");

        var ambiguous = rows.Where(r => ((string)r[nameIdx]!).Contains("01-02-2024")).ToList();
        var forced = rows.Where(r => ((string)r[nameIdx]!).Contains("03-15-2024")).ToList();
        Assert.NotEmpty(ambiguous);
        Assert.All(ambiguous, r => Assert.Equal("20240102000000", r[dataSetIdx]));   // Jan 2, month-first inferred
        Assert.All(forced, r => Assert.Equal("20240315000000", r[dataSetIdx]));       // March 15, resolved per file
    }

    [Fact]
    public async Task ReadResult_CarriesDetectedConvention_ForTheRunLog()
    {
        var modified = new DateTime(2024, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        WriteCsv("03-15-2024.csv", modified);   // forces month-first for the set
        WriteCsv("01-02-2024.csv", modified);
        var source = new SourceSpec
        {
            Type = "csv",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.csv" },
        };

        var read = await _reader.OpenAsync(source, await _reader.GetColumnsAsync(source));
        await read.Reader.DisposeAsync();

        // The convention rides out on the read result so the FlowRunner can put it in run.json / the catalog.
        Assert.Equal("filename dates; month-first (inferred from file set)", read.DataSetConvention);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
