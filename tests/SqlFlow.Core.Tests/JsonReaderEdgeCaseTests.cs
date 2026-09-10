using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge-case hardening for the JSON reader feature area (the <see cref="JsonSourceReader"/>
/// pipeline, the <see cref="JsonPathFlattener"/>, and the <see cref="JsonFlattenConfig"/> option parsing).
/// Everything here runs in memory over temp files or in-memory documents (no database, no network), with
/// fixed inputs so each assertion is deterministic. The themes are the gaps the existing suites leave open:
/// number-token fidelity (big integers, decimals, scientific notation), boolean / null typing, empty vs null
/// strings, lenient parsing (BOM, comments, trailing commas), the json / ndjson / array / single-object shape
/// matrix, malformed and out-of-range navigation, array-handling modes, explode multiplication, column-name
/// sanitization, and option-value validation. Non-ASCII literals are written as \u escapes so the source is
/// pure ASCII and reads identically regardless of the file's stored encoding.
/// </summary>
public sealed class JsonReaderEdgeCaseTests : IDisposable
{
    private readonly string _edgeDir = Path.Combine(Path.GetTempPath(), "sqlflow_jsonedge_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSourceReader _edgeReader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    private static readonly string[] EdgeProvenanceColumns =
    [
        "FileName_DW", "FileDate_DW", "FileRowDate_DW", "FileSize_DW", "DataSet_DW", "RowNumber_DW",
    ];

    public JsonReaderEdgeCaseTests() => Directory.CreateDirectory(_edgeDir);

    // --- Flattener / config unit helpers (pure, no IO) ---

    private static List<Dictionary<string, string?>> EdgeFlattenRows(string json, JsonFlattenConfig config)
    {
        using var doc = JsonDocument.Parse(json);
        return new JsonPathFlattener(config)
            .FlattenRows(doc.RootElement.Clone())
            .Select(row => row.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
            .ToList();
    }

    private static Dictionary<string, string?> EdgeFlattenSingle(string json, JsonFlattenConfig config)
    {
        var rows = EdgeFlattenRows(json, config);
        return Assert.Single(rows);
    }

    // --- Reader helpers (temp files) ---

    private SourceSpec EdgeJson(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_edgeDir, fileName);
        File.WriteAllText(path, content);
        var type = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return new SourceSpec { Type = type, Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private SourceSpec EdgeBytes(string fileName, byte[] bytes, string type)
    {
        var path = Path.Combine(_edgeDir, fileName);
        File.WriteAllBytes(path, bytes);
        return new SourceSpec { Type = type, Location = path, Options = new Dictionary<string, string?>() };
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> EdgeReadAllAsync(SourceSpec source)
    {
        var columns = await _edgeReader.GetColumnsAsync(source);
        var read = await _edgeReader.OpenAsync(source, columns);
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

    private static object? EdgeCell(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    private static List<string> EdgeDataColumns(IEnumerable<string> columns)
        => columns.Where(c => !EdgeProvenanceColumns.Contains(c, StringComparer.OrdinalIgnoreCase)).ToList();

    // --- Number-token fidelity ---

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("2147483648")]                       // Int32.MaxValue + 1
    [InlineData("9223372036854775808")]              // Int64.MaxValue + 1 (no native integer fits this)
    [InlineData("123456789012345678901234567890")]   // far beyond any fixed-width integer
    [InlineData("3.14159265358979")]
    [InlineData("0.30")]                              // trailing zero is significant text, must survive
    [InlineData("1.5e3")]
    [InlineData("1.5E+3")]
    [InlineData("-2.5e-4")]
    [InlineData("100000000000000000000.5")]
    public void NumberTokens_ArePreservedVerbatim(string token)
    {
        // The flattener emits the raw JSON number token so the downstream inference step, not a lossy parse to
        // double, decides the SQL type. The exact characters (sign, exponent, trailing zeros) must round-trip.
        var row = EdgeFlattenSingle($$"""{ "n": {{token}} }""", new JsonFlattenConfig());

        Assert.Equal(token, row["n"]);
    }

    // --- String fidelity at the flatten layer ---

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("\"\"", "")]                          // an empty JSON string stays an empty string here (NOT null)
    [InlineData("\"0123\"", "0123")]                  // a numeric-looking string is not renumbered
    [InlineData("\"true\"", "true")]                  // a boolean-looking string is not reinterpreted
    [InlineData("\"  spaced  \"", "  spaced  ")]      // surrounding whitespace inside the value is preserved
    [InlineData("\"caf\\u00e9 \\u2615\"", "caf\u00e9 \u2615")]  // unicode escapes decode to their characters
    public void StringValues_PreservedAtFlattenLayerIncludingEmpty(string jsonValue, string expected)
    {
        var row = EdgeFlattenSingle($$"""{ "s": {{jsonValue}} }""", new JsonFlattenConfig());

        Assert.Equal(expected, row["s"]);
    }

    // --- Boolean / null typing ---

    [Theory]
    [InlineData("true", "true")]
    [InlineData("false", "false")]
    [InlineData("null", null)]
    public void BooleanAndExplicitNull_MapToExpectedCells(string jsonValue, string? expected)
    {
        var row = EdgeFlattenSingle($$"""{ "v": {{jsonValue}} }""", new JsonFlattenConfig());

        Assert.True(row.ContainsKey("v"));
        Assert.Equal(expected, row["v"]);
    }

    [Fact]
    public void ExplicitNullValue_StillProducesColumn()
    {
        // A field present with an explicit null yields a column carrying a null, distinct from a field that is
        // simply absent (which produces no column for that record at all).
        var row = EdgeFlattenSingle("""{ "id": 1, "deleted_at": null }""", new JsonFlattenConfig());

        Assert.True(row.ContainsKey("deleted_at"));
        Assert.Null(row["deleted_at"]);
        Assert.Equal("1", row["id"]);
    }

    [Fact]
    public void DuplicateKeysSameCase_LastValueWins()
    {
        // JSON permits repeated keys; the ordered accumulator keeps document order and applies last-write-wins,
        // so the second "id" overwrites the first instead of producing two columns.
        var row = EdgeFlattenSingle("""{ "id": 1, "id": 2, "name": "x" }""", new JsonFlattenConfig());

        Assert.Equal("2", row["id"]);
        Assert.Equal("x", row["name"]);
    }

    // --- Column-name derivation and sanitization ---

    [Theory]
    [InlineData("$.id", "id")]
    [InlineData("$.user.name", "user_name")]          // nested keys fold onto the separator
    [InlineData("$.a b", "a_b")]                      // a space is an invalid identifier char, replaced
    [InlineData("$.@@@", "column")]                   // an all-symbol key collapses to the safe fallback name
    [InlineData("$.1st", "_1st")]                     // a leading digit gets an underscore prefix
    [InlineData("$._x_", "x")]                        // leading/trailing separators are trimmed
    [InlineData("$.caf\u00e9", "caf\u00e9")]          // a non-ASCII letter (e-acute) is kept (sanitize keeps letters)
    [InlineData("$.items[0].sku", "items_sku")]       // array subscripts are dropped from the column name
    public void ColumnName_SanitizesPathToIdentifier(string path, string expected)
    {
        var name = new JsonFlattenConfig().ColumnName(path);

        Assert.Equal(expected, name);
    }

    [Fact]
    public void ColumnName_RootScalarPath_BecomesColumnFallback()
    {
        // The document root itself has no key, so a scalar addressed at "$" (e.g. an element of a top-level
        // array of scalars) gets the deterministic fallback name.
        var name = new JsonFlattenConfig().ColumnName("$");

        Assert.Equal("column", name);
    }

    [Fact]
    public void ColumnName_CustomSeparator_IsUsedAndPreserved()
    {
        var name = new JsonFlattenConfig { Separator = "-" }.ColumnName("$.user.name");

        Assert.Equal("user-name", name);
    }

    // --- Array handling modes ---

    [Fact]
    public void ArrayHandling_Count_EmitsElementCount()
    {
        var row = EdgeFlattenSingle(
            """{ "tags": ["a", "b", "c"] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Count });

        Assert.Equal("3", row["tags"]);
    }

    [Fact]
    public void ArrayHandling_Join_DefaultCommaSeparator()
    {
        var row = EdgeFlattenSingle(
            """{ "tags": ["a", "b"] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join });

        Assert.Equal("a,b", row["tags"]);
    }

    [Fact]
    public void ArrayHandling_Join_CustomSeparatorWithNullAndNumber()
    {
        // Join renders each scalar with its raw text: a number keeps its token and a null becomes an empty
        // segment, with the configured separator between every element.
        var row = EdgeFlattenSingle(
            """{ "vals": [1, null, "x"] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join, JoinSeparator = "|" });

        Assert.Equal("1||x", row["vals"]);
    }

    [Fact]
    public void ArrayHandling_FirstElement_FlattensFirstObjectFields()
    {
        // FirstElement flattens element [0] in place under the array's path, so its object fields become columns.
        var row = EdgeFlattenSingle(
            """{ "items": [ { "sku": "A" }, { "sku": "B" } ] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });

        Assert.Equal("A", row["items_sku"]);
        Assert.False(row.ContainsKey("items"));
    }

    [Fact]
    public void ArrayHandling_Skip_ProducesNoColumn()
    {
        var row = EdgeFlattenSingle(
            """{ "id": 1, "tags": ["a", "b"] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Skip });

        Assert.Equal("1", row["id"]);
        Assert.False(row.ContainsKey("tags"));
    }

    // --- Path selection rules ---

    [Fact]
    public void IncludePaths_Whitelist_KeepsOnlyListedSubtree()
    {
        // With an include whitelist, sibling fields outside the list are not descended into and yield no columns;
        // the chain needed to reach the whitelisted leaf is still traversed.
        var row = EdgeFlattenSingle(
            """{ "a": 1, "b": 2, "keep": { "x": 9 } }""",
            new JsonFlattenConfig { IncludePaths = ["$.keep"] });

        Assert.Equal("9", row["keep_x"]);
        Assert.False(row.ContainsKey("a"));
        Assert.False(row.ContainsKey("b"));
    }

    [Fact]
    public void ExcludePaths_Wildcard_DropsMatchingLeaves()
    {
        // A single-'*' glob excludes every leaf whose path matches the prefix, leaving the rest intact.
        var row = EdgeFlattenSingle(
            """{ "raw_a": 1, "raw_b": 2, "id": 3 }""",
            new JsonFlattenConfig { ExcludePaths = ["$.raw_*"] });

        Assert.Equal("3", row["id"]);
        Assert.False(row.ContainsKey("raw_a"));
        Assert.False(row.ContainsKey("raw_b"));
    }

    [Fact]
    public void ColumnMappings_RenameOutputColumn()
    {
        var config = new JsonFlattenConfig
        {
            ColumnMappings = JsonFlattenConfig.ParseColumnMappings("$.id=identifier"),
        };

        var row = EdgeFlattenSingle("""{ "id": 7, "name": "x" }""", config);

        Assert.Equal("7", row["identifier"]);
        Assert.False(row.ContainsKey("id"));
        Assert.Equal("x", row["name"]);
    }

    // --- Explode multiplication ---

    [Fact]
    public void Explode_ScalarArray_OneRowPerElement()
    {
        var rows = EdgeFlattenRows(
            """{ "id": 1, "vals": [10, 20, 30] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.vals"] });

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "10", "20", "30" }, rows.Select(r => r["vals"]).ToArray());
        Assert.All(rows, r => Assert.Equal("1", r["id"]));
    }

    [Fact]
    public void Explode_TwoSiblingArrays_CrossProduct()
    {
        // Two exploded arrays in one record multiply: every pairing of their elements becomes an output row.
        var rows = EdgeFlattenRows(
            """{ "a": [1, 2], "b": [3, 4] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.a", "$.b"] });

        Assert.Equal(4, rows.Count);
        var pairs = rows.Select(r => (r["a"], r["b"])).ToHashSet();
        Assert.Equal(
            new HashSet<(string?, string?)> { ("1", "3"), ("1", "4"), ("2", "3"), ("2", "4") },
            pairs);
    }

    [Fact]
    public void Explode_EmptyArray_KeepsParentRowOnce()
    {
        // An empty exploded array uses left-join semantics: the parent record is preserved as one row rather
        // than being dropped, with no element columns produced.
        var rows = EdgeFlattenRows(
            """{ "id": 1, "items": [] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items"] });

        var row = Assert.Single(rows);
        Assert.Equal("1", row["id"]);
    }

    // --- Depth limit ---

    [Fact]
    public void MaxDepth_ValuesBelowLimitBecomeJsonString()
    {
        // With a shallow depth limit, a subtree deeper than the limit is captured whole as one compact JSON
        // string column instead of being flattened into further child columns.
        var row = EdgeFlattenSingle(
            """{ "a": { "b": { "c": 1 } } }""",
            new JsonFlattenConfig { MaxDepth = 1 });

        Assert.Equal("{\"c\":1}", row["a_b"]);
        Assert.False(row.ContainsKey("a_b_c"));
    }

    // --- Option-value parsing and validation ---

    [Theory]
    [InlineData("to_json", JsonArrayHandling.ToJson)]
    [InlineData("as_json", JsonArrayHandling.ToJson)]
    [InlineData("first", JsonArrayHandling.FirstElement)]
    [InlineData("FIRST-ELEMENT", JsonArrayHandling.FirstElement)]
    [InlineData("join_comma", JsonArrayHandling.Join)]
    [InlineData("COUNT", JsonArrayHandling.Count)]
    [InlineData("unnest", JsonArrayHandling.Explode)]
    public void ParseArrayHandling_AcceptsDocumentedSynonyms(string input, JsonArrayHandling expected)
    {
        Assert.Equal(expected, JsonFlattenConfig.ParseArrayHandling(input));
    }

    [Theory]
    [InlineData("noequalssign")]
    [InlineData("=missingPath")]
    [InlineData("$.path=")]
    public void ParseColumnMappings_InvalidEntry_Throws(string input)
    {
        Assert.Throws<SqlFlowException>(() => JsonFlattenConfig.ParseColumnMappings(input));
    }

    [Theory]
    [InlineData("=$.a|$.b")]
    [InlineData("column=")]
    public void ParsePathAliases_InvalidEntry_Throws(string input)
    {
        Assert.Throws<SqlFlowException>(() => JsonFlattenConfig.ParsePathAliases(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BuildFlattenConfig_NonPositiveMaxDepth_Throws(int maxDepth)
    {
        var meta = new PreIngestionJsn { SysAlias = "edge", SrcPath = "edge", MaxDepth = maxDepth };

        Assert.Throws<SqlFlowException>(() => JsonSourceReader.BuildFlattenConfig(meta));
    }

    // --- Reader: document shapes ---

    [Fact]
    public async Task SingleStandaloneObject_YieldsExactlyOneRow()
    {
        var source = EdgeJson("one.json", """{ "id": 7, "name": "solo" }""");

        var (columns, rows) = await EdgeReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Equal("7", EdgeCell(columns, row, "id"));
        Assert.Equal("solo", EdgeCell(columns, row, "name"));
        Assert.Equal(1L, EdgeCell(columns, row, "RowNumber_DW"));
    }

    [Fact]
    public async Task EmptyFile_YieldsNoRows()
    {
        var source = EdgeJson("empty.json", string.Empty);

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task WhitespaceOnlyFile_YieldsNoRows()
    {
        // A non-empty but content-free file fails the whole-document parse, falls back to line mode, and every
        // (blank) line is skipped, so no records are produced and no parse error is raised.
        var source = EdgeJson("blank.json", "   \n\t\n   ");

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task TopLevelScalarDocument_YieldsNoRows()
    {
        // A bare scalar is valid JSON but is not a record (only objects and arrays expand into records).
        var source = EdgeJson("scalar.json", "42");

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task EmptyObjectRoot_YieldsOneRowWithProvenanceOnly()
    {
        // An empty object is a record, so it produces exactly one output row; it just carries no source columns,
        // only the generated provenance columns.
        var source = EdgeJson("emptyobj.json", "{}");

        var (columns, rows) = await EdgeReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Empty(EdgeDataColumns(columns));
        Assert.Equal(1L, EdgeCell(columns, row, "RowNumber_DW"));
    }

    [Fact]
    public async Task TopLevelScalarArray_ProducesSingleFallbackColumn()
    {
        // A top-level array of scalars fans out to one row per element; each scalar sits at the root path, so it
        // lands in the single deterministic fallback column.
        var source = EdgeJson("scalars.json", "[10, 20, 30]");

        var (columns, rows) = await EdgeReadAllAsync(source);

        var dataColumns = EdgeDataColumns(columns);
        var only = Assert.Single(dataColumns);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new object?[] { "10", "20", "30" }, rows.Select(r => EdgeCell(columns, r, only)).ToArray());
    }

    [Fact]
    public async Task EmptyStringValue_IsCoercedToNull()
    {
        // The shared file pipeline treats an empty source cell as a database null, so an empty JSON string lands
        // as NULL in the target rather than as a zero-length string.
        var source = EdgeJson("emptystr.json", """[ { "id": 1, "note": "" } ]""");

        var (columns, rows) = await EdgeReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Equal("1", EdgeCell(columns, row, "id"));
        Assert.Null(EdgeCell(columns, row, "note"));
    }

    // --- Reader: lenient parsing (BOM, fallback, comments) ---

    [Fact]
    public async Task BomPrefixedJsonArray_IsParsed()
    {
        var bytes = WithUtf8Bom("""[ { "id": 1 }, { "id": 2 } ]""");
        var source = EdgeBytes("bom.json", bytes, "json");

        var (columns, rows) = await EdgeReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", EdgeCell(columns, rows[0], "id"));
        Assert.Equal("2", EdgeCell(columns, rows[1], "id"));
    }

    [Fact]
    public async Task BomPrefixedNdjson_IsParsed()
    {
        var bytes = WithUtf8Bom("{ \"id\": 1 }\n{ \"id\": 2 }\n");
        var source = EdgeBytes("bom.ndjson", bytes, "ndjson");

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task JsonExtensionWithNdjsonBody_FallsBackToLineMode()
    {
        // A ".json" file whose body is actually newline-delimited objects fails the whole-document parse and is
        // recovered line by line, so both records load.
        var source = EdgeJson("looksLikeJson.json", "{ \"id\": 1 }\n{ \"id\": 2 }\n");

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task Ndjson_SkipsBlankAndHashCommentLines()
    {
        var source = EdgeJson(
            "comments.ndjson",
            "{ \"id\": 1 }\n\n# a comment line\n{ \"id\": 2 }\n   \n{ \"id\": 3 }\n");

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public async Task MalformedNdjsonLine_ThrowsWithLineContext()
    {
        // A broken line in NDJSON fails fast with the offending line number, rather than silently dropping data.
        var source = EdgeJson("broken.ndjson", "{ \"id\": 1 }\n{ \"id\": 2 ,, }\n");

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => _edgeReader.GetColumnsAsync(source));
        Assert.Contains("line 2", ex.Message, StringComparison.Ordinal);
    }

    // --- Reader: root-path navigation edge cases ---

    [Fact]
    public async Task RootPathWithArrayIndex_NavigatesToNestedArray()
    {
        var source = EdgeJson(
            "indexed.json",
            """{ "data": [ { "records": [ { "id": 1 }, { "id": 2 } ] } ] }""",
            new Dictionary<string, string?> { ["rootPath"] = "$.data[0].records" });

        var (columns, rows) = await EdgeReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", EdgeCell(columns, rows[0], "id"));
        Assert.Equal("2", EdgeCell(columns, rows[1], "id"));
    }

    [Fact]
    public async Task RootPathIndexOutOfRange_YieldsNoRows()
    {
        // An index past the end of the array resolves to nothing (no record), without raising an error.
        var source = EdgeJson(
            "oob.json",
            """{ "items": [ { "id": 1 } ] }""",
            new Dictionary<string, string?> { ["rootPath"] = "$.items[5]" });

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task RootPathInvalidIndexToken_Throws()
    {
        // A non-numeric subscript in the root path is a configuration error and is surfaced as such.
        var source = EdgeJson(
            "badindex.json",
            """{ "data": [ { "id": 1 } ] }""",
            new Dictionary<string, string?> { ["rootPath"] = "$.data[x]" });

        await Assert.ThrowsAsync<SqlFlowException>(() => _edgeReader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task RootPathToScalarValue_YieldsNoRows()
    {
        // The root path can resolve successfully to a scalar; a scalar is not a record, so the result is empty.
        var source = EdgeJson(
            "rootscalar.json",
            """{ "meta": { "version": 5 } }""",
            new Dictionary<string, string?> { ["rootPath"] = "$.meta.version" });

        var (_, rows) = await EdgeReadAllAsync(source);

        Assert.Empty(rows);
    }

    private static byte[] WithUtf8Bom(string content)
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        var body = Encoding.UTF8.GetBytes(content);
        var result = new byte[bom.Length + body.Length];
        Array.Copy(bom, 0, result, 0, bom.Length);
        Array.Copy(body, 0, result, bom.Length, body.Length);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_edgeDir))
        {
            Directory.Delete(_edgeDir, recursive: true);
        }
    }
}
