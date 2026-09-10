using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Reader-level tests for <see cref="JsonSourceReader"/> over real temp files (no database): schema
/// discovery, row streaming with the flatten projection, schema evolution across files, NDJSON, root path,
/// option-driven flatten rules, and the discover scan.
/// </summary>
public sealed class JsonSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_json_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public JsonSourceReaderTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Json(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        var type = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return new SourceSpec { Type = type, Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

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

    private static object? Cell(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    [Fact]
    public async Task GetColumns_ReturnsFlattenedSourceColumnsThenProvenance()
    {
        var source = Json("a.json", """[ { "id": 1, "user": { "name": "Ann" } } ]""");

        var names = (await _reader.GetColumnsAsync(source)).Select(c => c.Name).ToList();

        Assert.Equal(new[] { "id", "user_name" }, names.Take(2).ToArray());
        Assert.Contains("FileName_DW", names);
        Assert.Contains("RowNumber_DW", names);
    }

    [Fact]
    public async Task Open_StreamsOneRowPerArrayElementWithFlattenedValues()
    {
        var source = Json("orders.json",
            """[ { "id": 1, "addr": { "city": "Oslo" } }, { "id": 2, "addr": { "city": "Bergen" } } ]""");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", Cell(columns, rows[0], "id"));
        Assert.Equal("Oslo", Cell(columns, rows[0], "addr_city"));
        Assert.Equal("Bergen", Cell(columns, rows[1], "addr_city"));
        Assert.Equal(1L, Cell(columns, rows[0], "RowNumber_DW"));
        Assert.Equal(2L, Cell(columns, rows[1], "RowNumber_DW"));
    }

    [Fact]
    public async Task Folder_UnionsFlattenedColumnsAcrossFilesWithNullFill()
    {
        Json("jan.json", """{ "id": 1, "amount": 9.5 }""");
        Json("feb.json", """{ "id": 2, "country": "Norway" }""");
        var source = new SourceSpec
        {
            Type = "json",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.json" },
        };

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("amount", columns);
        Assert.Contains("country", columns);
        Assert.Equal(2, rows.Count);
        // The file lacking a column null-fills it.
        var janRow = rows.Single(r => Equals(Cell(columns, r, "id"), "1"));
        Assert.Null(Cell(columns, janRow, "country"));
    }

    [Fact]
    public async Task Ndjson_ReadsOneRowPerLine()
    {
        var source = Json("events.ndjson", "{ \"id\": 1 }\n{ \"id\": 2 }\n{ \"id\": 3 }\n");

        var (_, rows) = await ReadAllAsync(source);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task RootPath_SelectsNestedRecordArray()
    {
        var source = Json("env.json",
            """{ "data": { "records": [ { "id": 1 }, { "id": 2 } ] } }""",
            new Dictionary<string, string?> { ["rootPath"] = "$.data.records" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", Cell(columns, rows[0], "id"));
    }

    [Fact]
    public async Task JsonPathsOption_KeepsSubtreeAsOneColumn()
    {
        var source = Json("p.json",
            """[ { "id": 1, "payload": { "a": 1, "b": 2 } } ]""",
            new Dictionary<string, string?> { ["jsonPaths"] = "$.payload" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("payload", columns);
        Assert.DoesNotContain("payload_a", columns);
        Assert.Equal("{\"a\":1,\"b\":2}", Cell(columns, rows[0], "payload"));
    }

    [Fact]
    public async Task ExcludePathsOption_DropsSubtree()
    {
        var source = Json("e.json",
            """[ { "id": 1, "debug": { "trace": "x" } } ]""",
            new Dictionary<string, string?> { ["excludePaths"] = "$.debug" });

        var (columns, _) = await ReadAllAsync(source);

        Assert.Contains("id", columns);
        Assert.DoesNotContain("debug", columns);
        Assert.DoesNotContain("debug_trace", columns);
    }

    [Fact]
    public async Task Discover_ReportsStructureAcrossFiles()
    {
        Json("one.json", """{ "id": 1, "name": "a" }""");
        Json("two.json", """{ "id": 2, "tags": ["x"] }""");
        var source = new SourceSpec
        {
            Type = "json",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.json" },
        };

        var result = await _reader.DiscoverAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal(2, result.FilesScanned);
        Assert.Equal(2, result.RecordsScanned);
        Assert.Contains("$.id", result.AllPaths);
        Assert.Contains("$.tags", result.AllPaths); // array of scalars -> the array path (no [*])
        Assert.Equal(2, result.Structures.Count);
    }

    [Fact]
    public async Task CaseOnlyDifferingKeys_FoldIntoOneColumnDeterministically()
    {
        // SQL Server columns are case-insensitive, so Id and id cannot both exist; they fold to one column
        // (last value wins) instead of being silently misassigned across the case-sensitive/insensitive seam.
        var source = Json("c.json", """[ { "Id": 1, "id": 2, "name": "x" } ]""");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Single(columns, c => string.Equals(c, "Id", StringComparison.OrdinalIgnoreCase));
        var row = Assert.Single(rows);
        Assert.Equal("2", Cell(columns, row, "Id"));   // document-order last write wins
        Assert.Equal("x", Cell(columns, row, "name"));
    }

    [Fact]
    public async Task BuildFlattenConfig_InvalidArrayHandling_FailsClearly()
    {
        var source = Json("x.json", """{ "a": 1 }""",
            new Dictionary<string, string?> { ["arrayHandling"] = "nonsense" });

        await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task Explode_ProducesOneRowPerElementWithStableColumns()
    {
        var source = Json("orders.json",
            """[ { "id": 1, "items": [ { "sku": "A" }, { "sku": "B" } ] } ]""",
            new Dictionary<string, string?> { ["explodePaths"] = "$.items" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Contains("items_sku", columns);
        Assert.DoesNotContain("items", columns); // the exploded array is consumed, not kept as a column
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => (string?)Cell(columns, r, "items_sku")).ToArray());
        // The parent field repeats on every exploded row, and RowNumber_DW is per output row.
        Assert.All(rows, r => Assert.Equal("1", Cell(columns, r, "id")));
        Assert.Equal(new object?[] { 1L, 2L }, rows.Select(r => Cell(columns, r, "RowNumber_DW")).ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
