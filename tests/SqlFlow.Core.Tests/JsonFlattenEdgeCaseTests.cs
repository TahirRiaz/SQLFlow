using System.Globalization;
using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional, non-overlapping edge-case coverage for the path-based JSON flattener, the record reader,
/// and the structure/inventory/formula builders. These harden behavior that the primary suites do not
/// assert: deep nesting at and past the depth limit, empty containers under every array-handling mode,
/// mixed-type and nested explosion, unicode and numeric keys, raw-number token fidelity, JSON-string
/// escaping, duplicate keys, case-insensitive column folding, path collisions, and reader boundaries
/// (BOM, CRLF, whitespace, scalars, bad root paths). All tests are pure in-memory and deterministic.
/// </summary>
public sealed class JsonFlattenEdgeCaseTests
{
    private static JsonElement ParseEdge(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string?> FlattenEdge(string json, JsonFlattenConfig config)
    {
        var pairs = new JsonPathFlattener(config).Flatten(ParseEdge(json));
        return pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    private static List<string> FlattenKeysEdge(string json, JsonFlattenConfig config)
        => new JsonPathFlattener(config).Flatten(ParseEdge(json)).Select(p => p.Key).ToList();

    private static List<Dictionary<string, string?>> FlattenRowsEdge(string json, JsonFlattenConfig config)
        => new JsonPathFlattener(config)
            .FlattenRows(ParseEdge(json))
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            .ToList();

    private static byte[] BytesEdge(string s) => Encoding.UTF8.GetBytes(s);

    // ---------------------------------------------------------------------------------------------
    // Raw-number token fidelity: the flattener emits GetRawText(), so no normalization happens.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1.5e3", "1.5e3")]              // scientific notation kept verbatim
    [InlineData("1E2", "1E2")]                  // uppercase exponent kept
    [InlineData("1.2000", "1.2000")]            // trailing zeros preserved
    [InlineData("-0.0", "-0.0")]                // signed zero preserved
    [InlineData("0.0", "0.0")]
    [InlineData("100000000000000000000", "100000000000000000000")] // beyond Int64 range, kept as text
    [InlineData("123456789012345678901234567890.123456789", "123456789012345678901234567890.123456789")]
    public void Flatten_Number_PreservesRawTokenWithoutReformatting(string token, string expected)
    {
        var row = FlattenEdge($$"""{ "n": {{token}} }""", new JsonFlattenConfig());
        Assert.Equal(expected, row["n"]);
    }

    // ---------------------------------------------------------------------------------------------
    // Empty containers under each array-handling mode and as object values.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_EmptyArray_ToJson_IsEmptyArrayLiteral()
    {
        var row = FlattenEdge("""{ "x": [] }""", new JsonFlattenConfig());
        Assert.Equal("[]", row["x"]);
    }

    [Fact]
    public void Flatten_EmptyArray_Count_IsZero()
    {
        var row = FlattenEdge("""{ "x": [] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Count });
        Assert.Equal("0", row["x"]);
    }

    [Fact]
    public void Flatten_EmptyArray_Join_IsEmptyStringNotNull()
    {
        var row = FlattenEdge("""{ "x": [] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join });
        Assert.True(row.ContainsKey("x"));
        Assert.Equal(string.Empty, row["x"]);
    }

    [Fact]
    public void Flatten_EmptyArray_FirstElement_YieldsNullColumn()
    {
        var row = FlattenEdge("""{ "x": [] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });
        Assert.True(row.ContainsKey("x"));
        Assert.Null(row["x"]);
    }

    [Fact]
    public void Flatten_EmptyObjectValue_ProducesNoColumn()
    {
        var row = FlattenEdge("""{ "id": 1, "meta": {} }""", new JsonFlattenConfig());
        Assert.True(row.ContainsKey("id"));
        Assert.False(row.ContainsKey("meta"));
        Assert.Single(row);
    }

    [Fact]
    public void Flatten_EmptyRootObject_ProducesZeroColumns()
    {
        var row = FlattenEdge("""{}""", new JsonFlattenConfig());
        Assert.Empty(row);
    }

    // ---------------------------------------------------------------------------------------------
    // Depth limits.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_MaxDepthZero_CollapsesFirstChildToJsonString()
    {
        var row = FlattenEdge("""{ "a": { "b": 1 } }""", new JsonFlattenConfig { MaxDepth = 0 });

        // $.a is depth 1 (> maxDepth 0) so it is captured whole instead of recursing.
        Assert.Equal("{\"b\":1}", row["a"]);
        Assert.False(row.ContainsKey("a_b"));
    }

    [Fact]
    public void Flatten_VeryDeepNesting_CollapsesAtTheDepthBoundary()
    {
        // Twelve nested objects with default MaxDepth 10: the path is flattened up to the boundary and the
        // remainder is captured as one JSON-string column, never throwing or running away.
        var sb = new StringBuilder();
        for (var i = 0; i < 12; i++)
        {
            sb.Append("{\"a\":");
        }

        sb.Append('1');
        for (var i = 0; i < 12; i++)
        {
            sb.Append('}');
        }

        var row = FlattenEdge(sb.ToString(), new JsonFlattenConfig());

        var key = Assert.Single(row.Keys);
        Assert.Equal("a_a_a_a_a_a_a_a_a_a_a", key); // eleven segments, then the deeper tail folds to JSON
        Assert.Equal("{\"a\":1}", row[key]);
    }

    // ---------------------------------------------------------------------------------------------
    // Unicode and numeric keys / column-name sanitization at the extremes.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_UnicodeKeys_ArePreservedAsColumnNames()
    {
        var row = FlattenEdge("""{ "naïve": 1, "Ω": 2, "日本": 3 }""", new JsonFlattenConfig());

        Assert.Equal("1", row["naïve"]);
        Assert.Equal("2", row["Ω"]);
        Assert.Equal("3", row["日本"]);
    }

    [Theory]
    [InlineData("$.123", "_123")]            // a leading digit gets an underscore prefix
    [InlineData("$.4x", "_4x")]
    [InlineData("$.a b", "a_b")]             // an interior space folds to the separator
    [InlineData("$.a.b.c", "a_b_c")]         // dots fold to the separator
    [InlineData("$.café_x", "café_x")]       // a unicode letter is kept
    public void ColumnName_HandlesNumericUnicodeAndSpacedKeys(string path, string expected)
    {
        Assert.Equal(expected, new JsonFlattenConfig().ColumnName(path));
    }

    [Theory]
    [InlineData("$")]        // bare root has no name parts
    [InlineData("$.!")]      // a single symbol sanitizes away
    [InlineData("$.---")]    // a run of symbols sanitizes away
    public void ColumnName_UnnameablePath_FallsBackToColumnPlaceholder(string path)
    {
        Assert.Equal("column", new JsonFlattenConfig().ColumnName(path));
    }

    [Fact]
    public void ColumnName_EmojiInPath_FoldsAroundTheNonLetterToTheSeparator()
    {
        // The emoji is a surrogate pair and not a letter or digit, so it folds to the separator and the
        // resulting double separator collapses, leaving the two letters joined.
        Assert.Equal("a_b", new JsonFlattenConfig().ColumnName("$.a\U0001F600b"));
    }

    // ---------------------------------------------------------------------------------------------
    // Duplicate keys and case-insensitive column folding.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_DuplicateJsonKeys_LastValueWinsIntoOneColumn()
    {
        // System.Text.Json keeps the last occurrence of a duplicated key; the flattener surfaces one column.
        var row = FlattenEdge("""{ "a": 1, "a": 2 }""", new JsonFlattenConfig());
        Assert.Single(row);
        Assert.Equal("2", row["a"]);
    }

    [Fact]
    public void Flatten_KeysDifferingOnlyInCase_FoldOntoOneColumnFirstNameWins()
    {
        // Column names are compared case-insensitively (SQL Server collation): "Id" and "id" are one column,
        // keeping the first-seen name and the last-written value.
        var pairs = new JsonPathFlattener(new JsonFlattenConfig()).Flatten(ParseEdge("""{ "Id": 1, "id": 2 }"""));

        var pair = Assert.Single(pairs);
        Assert.Equal("Id", pair.Key);
        Assert.Equal("2", pair.Value);
    }

    // ---------------------------------------------------------------------------------------------
    // JSON-string capture (jsonPaths / past-depth) escaping and precedence.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_JsonPathSubtree_EscapesQuotesAmpersandAndNonAsciiInTheKeptJson()
    {
        var row = FlattenEdge(
            """{ "p": { "name": "AC/DC & Co", "q": "a\"b", "u": "é" } }""",
            new JsonFlattenConfig { JsonPaths = ["$.p"] });

        // The default serializer encoder escapes ", &, and non-ASCII, but leaves '/' untouched.
        Assert.Equal("{\"name\":\"AC/DC \\u0026 Co\",\"q\":\"a\\u0022b\",\"u\":\"\\u00E9\"}", row["p"]);
    }

    [Fact]
    public void Flatten_ArrayToJson_EscapesInnerStringsWithUnicodeEscapes()
    {
        var row = FlattenEdge("""{ "a": ["he said \"hi\"", "café"] }""", new JsonFlattenConfig());
        Assert.Equal("[\"he said \\u0022hi\\u0022\",\"caf\\u00E9\"]", row["a"]);
    }

    [Fact]
    public void Flatten_JsonPathOnArray_TakesPrecedenceOverExplode()
    {
        // A path listed as both a json path and an explode path is captured whole (json check runs first),
        // so it stays a single column and does not multiply rows.
        var rows = FlattenRowsEdge(
            """{ "a": [ { "x": 1 }, { "x": 2 } ] }""",
            new JsonFlattenConfig { JsonPaths = ["$.a"], ExplodePaths = ["$.a"] });

        var row = Assert.Single(rows);
        Assert.Equal("[{\"x\":1},{\"x\":2}]", row["a"]);
        Assert.False(row.ContainsKey("a_x"));
    }

    // ---------------------------------------------------------------------------------------------
    // Array-handling value semantics for mixed and non-scalar elements.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_Join_MixedScalarsRenderBoolsRawNumbersAndEmptyForNull()
    {
        var row = FlattenEdge(
            """{ "a": [1, true, null, "s", 2.5] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join, JoinSeparator = "|" });

        Assert.Equal("1|true||s|2.5", row["a"]);
    }

    [Fact]
    public void Flatten_Join_NonScalarElementsRenderAsCompactJson()
    {
        var row = FlattenEdge(
            """{ "a": [ { "x": 1 }, [1, 2] ] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join, JoinSeparator = "," });

        Assert.Equal("{\"x\":1},[1,2]", row["a"]);
    }

    [Fact]
    public void Flatten_FirstElement_ScalarFirstElementBecomesTheColumnValue()
    {
        var row = FlattenEdge("""{ "a": [42, 99] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });
        Assert.Equal("42", row["a"]);
    }

    [Fact]
    public void Flatten_FirstElement_NullFirstElementYieldsNullColumn()
    {
        var row = FlattenEdge("""{ "a": [null, 2] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });
        Assert.True(row.ContainsKey("a"));
        Assert.Null(row["a"]);
    }

    // ---------------------------------------------------------------------------------------------
    // String value fidelity: control characters and astral-plane code points are preserved.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_StringValue_PreservesControlCharactersVerbatim()
    {
        var row = FlattenEdge("{ \"a\": \"line1\\nline2\\ttab\" }", new JsonFlattenConfig());
        Assert.Equal("line1\nline2\ttab", row["a"]);
    }

    [Fact]
    public void Flatten_StringValue_PreservesAstralCodePoint()
    {
        var row = FlattenEdge("{ \"a\": \"\U0001F600x\" }", new JsonFlattenConfig());
        Assert.Equal("\U0001F600x", row["a"]);
    }

    // ---------------------------------------------------------------------------------------------
    // Explode: cardinality, null/bool scalars, arrays of arrays, nested levels, exclusion, non-arrays.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Explode_ScalarArrayWithNullElement_KeepsTheRowWithANullValue()
    {
        var rows = FlattenRowsEdge("""{ "id": 1, "t": ["a", null, "c"] }""", new JsonFlattenConfig { ExplodePaths = ["$.t"] });

        Assert.Equal(3, rows.Count);
        Assert.Equal("a", rows[0]["t"]);
        Assert.Null(rows[1]["t"]);
        Assert.Equal("c", rows[2]["t"]);
    }

    [Fact]
    public void Explode_BooleanArray_RendersBooleanTokensPerRow()
    {
        var rows = FlattenRowsEdge("""{ "f": [true, false, true] }""", new JsonFlattenConfig { ExplodePaths = ["$.f"] });
        Assert.Equal(new[] { "true", "false", "true" }, rows.Select(r => r["f"]).ToArray());
    }

    [Fact]
    public void Explode_ArrayOfArrays_EachInnerArrayBecomesOneJsonValuedRow()
    {
        var rows = FlattenRowsEdge("""{ "id": 1, "m": [[1, 2], [3]] }""", new JsonFlattenConfig { ExplodePaths = ["$.m"] });

        Assert.Equal(2, rows.Count);
        Assert.Equal("[1,2]", rows[0]["m"]);
        Assert.Equal("[3]", rows[1]["m"]);
        Assert.All(rows, r => Assert.Equal("1", r["id"]));
    }

    [Fact]
    public void Explode_ThreeNestedLevels_ProducesTheFlattenedCrossProduct()
    {
        var rows = FlattenRowsEdge(
            """{ "a": [ { "b": [ { "c": [ { "d": 1 }, { "d": 2 } ] } ] } ] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.a", "$.a[*].b", "$.a[*].b[*].c"] });

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "1", "2" }, rows.Select(r => r["a_b_c_d"]).ToArray());
    }

    [Fact]
    public void Explode_WithExcludedElementField_DropsThatFieldFromEveryRow()
    {
        var rows = FlattenRowsEdge(
            """{ "items": [ { "sku": "A", "secret": "x" }, { "sku": "B", "secret": "y" } ] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items"], ExcludePaths = ["$.items[*].secret"] });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.ContainsKey("items_secret")));
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => r["items_sku"]).ToArray());
    }

    [Fact]
    public void Explode_PathPointingAtAnObject_FallsBackToObjectFlattening()
    {
        // An explode path that resolves to an object (not an array) is not exploded; the object flattens
        // in place and exactly one row results.
        var rows = FlattenRowsEdge("""{ "a": { "x": 1 } }""", new JsonFlattenConfig { ExplodePaths = ["$.a"] });

        var row = Assert.Single(rows);
        Assert.Equal("1", row["a_x"]);
    }

    [Fact]
    public void Explode_EmptyArrayAmongThreeSiblings_BehavesAsIdentityInTheCrossProduct()
    {
        var rows = FlattenRowsEdge(
            """{ "a": [], "b": [1], "c": [2, 3] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.a", "$.b", "$.c"] });

        // a contributes nothing (identity), b is a singleton, c drives the two rows.
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.False(r.ContainsKey("a")));
        Assert.All(rows, r => Assert.Equal("1", r["b"]));
        Assert.Equal(new[] { "2", "3" }, rows.Select(r => r["c"]).ToArray());
    }

    [Fact]
    public void Explode_EmptyArray_SchemaViewStillOmitsElementColumns()
    {
        // The schema view of an empty exploded array has no element fields to visit, so only the siblings remain.
        var keys = FlattenKeysEdge("""{ "id": 1, "items": [] }""", new JsonFlattenConfig { ExplodePaths = ["$.items"] });
        Assert.Equal(new[] { "id" }, keys.ToArray());
    }

    // ---------------------------------------------------------------------------------------------
    // Include / exclude path edge behaviors.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Flatten_ExcludeWildcardPrefix_DropsAllMatchingSiblings()
    {
        var row = FlattenEdge(
            """{ "raw_a": 1, "raw_b": 2, "keep": 3 }""",
            new JsonFlattenConfig { ExcludePaths = ["$.raw_*"] });

        Assert.Equal(new[] { "keep" }, row.Keys.ToArray());
    }

    [Fact]
    public void Flatten_IncludeArrayWildcardLeaf_KeepsOnlyThatExplodedField()
    {
        var row = FlattenEdge(
            """{ "items": [ { "sku": "A", "qty": 2 } ], "other": 9 }""",
            new JsonFlattenConfig { IncludePaths = ["$.items[*].sku"], ExplodePaths = ["$.items"] });

        Assert.True(row.ContainsKey("items_sku"));
        Assert.False(row.ContainsKey("items_qty"));
        Assert.False(row.ContainsKey("other"));
    }

    [Fact]
    public void ColumnMappings_RenameTargetIsSanitized()
    {
        var row = FlattenEdge(
            """{ "u": { "id": 1 } }""",
            new JsonFlattenConfig { ColumnMappings = new Dictionary<string, string> { ["$.u.id"] = "User Id!" } });

        Assert.True(row.ContainsKey("User_Id"));
        Assert.Equal("1", row["User_Id"]);
    }

    [Fact]
    public void Flatten_CustomMultiCharSeparator_JoinsNestedKeys()
    {
        var row = FlattenEdge("""{ "a": { "b": 1 } }""", new JsonFlattenConfig { Separator = "__" });
        Assert.True(row.ContainsKey("a__b"));
        Assert.Equal("1", row["a__b"]);
    }

    // ---------------------------------------------------------------------------------------------
    // Record reader boundaries.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task RecordReader_CrlfNdjson_ParsesOneRecordPerLine()
    {
        var records = await EdgeRecords("{ \"id\": 1 }\r\n{ \"id\": 2 }\r\n", fileName: "x.ndjson");
        Assert.Equal(2, records);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \t ")]
    public async Task RecordReader_EmptyOrWhitespaceOnly_YieldsNoRecords(string content)
    {
        Assert.Equal(0, await EdgeRecords(content));
    }

    [Fact]
    public async Task RecordReader_TopLevelScalar_YieldsNoRecords()
    {
        Assert.Equal(0, await EdgeRecords("42"));
    }

    [Fact]
    public async Task RecordReader_TopLevelMixedArray_FansOutEveryElementIncludingScalars()
    {
        // Every array element is a record; non-object elements are still emitted (and would flatten away).
        Assert.Equal(3, await EdgeRecords("[1, { \"id\": 2 }, \"s\"]"));
    }

    [Fact]
    public async Task RecordReader_RootPathToObject_YieldsSingleRecord()
    {
        Assert.Equal(1, await EdgeRecords("{ \"data\": { \"x\": 1 } }", "$.data"));
    }

    [Fact]
    public async Task RecordReader_RootPathArrayIndex_SelectsTheIndexedElement()
    {
        Assert.Equal(1, await EdgeRecords("{ \"d\": [ { \"a\": 1 }, { \"a\": 2 } ] }", "$.d[1]"));
    }

    [Fact]
    public async Task RecordReader_RootPathNotFound_YieldsNoRecords()
    {
        Assert.Equal(0, await EdgeRecords("{ \"a\": 1 }", "$.missing"));
    }

    [Fact]
    public async Task RecordReader_RootPathNonNumericIndex_Throws()
    {
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => EdgeRecords("{ \"d\": [1] }", "$.d[x]"));
        Assert.Contains("rootPath", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordReader_TrailingCommaAndComments_AreTolerated()
    {
        Assert.Equal(1, await EdgeRecords("{ \"id\": 1, }"));
        Assert.Equal(1, await EdgeRecords("{ \"id\": 1 /* note */ }"));
    }

    [Fact]
    public async Task RecordReader_SingleMalformedDocument_ThrowsWithFirstLineNumber()
    {
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => EdgeRecords("{ oops", fileName: "bad.json"));
        Assert.Contains("line 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordReader_LargeTopLevelArray_ReadsOnlyAWindowAheadOfTheCurrentRecord()
    {
        // The point of the streaming reader: a large array of records is never materialized, so the first
        // record is available after a bounded read and memory does not scale with the file. Measured by how
        // much of the stream has actually been consumed rather than by heap sampling, so it is deterministic.
        var json = new StringBuilder("[");
        for (var i = 0; i < 20_000; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(CultureInfo.InvariantCulture, $$"""{"id":{{i}},"name":"record number {{i}} with some padding text"}""");
        }

        json.Append(']');
        var payload = BytesEdge(json.ToString());
        Assert.True(payload.Length > 1_000_000, "the sample must be far larger than the reader's buffer");

        using var counting = new CountingStream(payload);
        var records = 0;
        long readAtFirstRecord = 0;
        await foreach (var record in JsonRecordReader.ReadRecordsAsync(counting, "$", "big.json"))
        {
            _ = record.ValueKind;
            if (records == 0)
            {
                readAtFirstRecord = counting.BytesRead;
            }

            records++;
        }

        Assert.Equal(20_000, records);
        Assert.True(
            readAtFirstRecord <= 256 * 1024,
            $"the first record should arrive after a bounded read, but {readAtFirstRecord} of {payload.Length} bytes were consumed");
    }

    [Fact]
    public async Task RecordReader_RecordLargerThanTheBuffer_GrowsToFitIt()
    {
        // One value that dwarfs the initial window must still be read: the buffer grows for exactly that case.
        var big = new string('x', 300_000);
        var json = $$"""[ { "id": 1, "blob": "{{big}}" }, { "id": 2, "blob": "small" } ]""";

        using var stream = new MemoryStream(BytesEdge(json));
        var lengths = new List<int>();
        await foreach (var record in JsonRecordReader.ReadRecordsAsync(stream, "$", "big-value.json"))
        {
            lengths.Add(record.GetProperty("blob").GetString()!.Length);
        }

        Assert.Equal([300_000, 5], lengths);
    }

    [Fact]
    public async Task RecordReader_NdjsonWithARootPath_NavigatesEveryLine()
    {
        // Several top-level values, each an envelope: the root path is applied to each one independently.
        var ndjson = "{ \"data\": [ { \"id\": 1 }, { \"id\": 2 } ] }\n{ \"data\": [ { \"id\": 3 } ] }\n";
        Assert.Equal(3, await EdgeRecords(ndjson, "$.data", "x.ndjson"));
    }

    /// <summary>Counts the records the streaming reader yields, consuming each within the iteration step.</summary>
    private static async Task<int> EdgeRecords(string content, string rootPath = "$", string fileName = "x.json")
    {
        using var stream = new MemoryStream(BytesEdge(content));
        var count = 0;
        await foreach (var record in JsonRecordReader.ReadRecordsAsync(stream, rootPath, fileName))
        {
            _ = record.ValueKind;
            count++;
        }

        return count;
    }

    /// <summary>A forward-only stream that reports how many bytes a reader has actually pulled from it.</summary>
    private sealed class CountingStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Structure discovery and typed inventory edge behaviors.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Discovery_EmptyObjectAndEmptyArray_AreListedWithoutElementPaths()
    {
        var paths = JsonStructureDiscovery.ExtractPaths(ParseEdge("""{ "id": 1, "empty": {}, "arr": [] }"""), maxDepth: 10);

        Assert.Contains("$.id", paths);
        Assert.Contains("$.empty", paths);     // an empty object is a leaf for discovery, not an intermediate node
        Assert.Contains("$.arr", paths);
        Assert.DoesNotContain("$.arr[*]", paths); // an empty array contributes no element path
    }

    [Fact]
    public void Inventory_EmptyContainers_ReportTheirOwnKindWithNoChildren()
    {
        var nodes = JsonPathInventoryBuilder.ExtractTypedPaths(ParseEdge("""{ "empty": {}, "arr": [] }"""), maxDepth: 10);
        var byPath = nodes.ToDictionary(n => n.Path, n => n.Kind, StringComparer.Ordinal);

        Assert.Equal(JsonNodeKind.Object, byPath["$.empty"]);
        Assert.Equal(JsonNodeKind.Array, byPath["$.arr"]);
        Assert.DoesNotContain("$.arr[*]", byPath.Keys); // no element node for an empty array
    }

    [Fact]
    public void Inventory_HeterogeneousArrayElement_PrefersTheStructuredKindAcrossRecords()
    {
        // One record has a scalar array, another an object array at the same path; the element node should
        // settle on the more structured kind (object) and surface the nested field.
        var perRecord = new[]
        {
            JsonPathInventoryBuilder.ExtractTypedPaths(ParseEdge("""{ "a": ["x"] }"""), maxDepth: 10),
            JsonPathInventoryBuilder.ExtractTypedPaths(ParseEdge("""{ "a": [ { "k": 1 } ] }"""), maxDepth: 10),
        };

        var inventory = JsonPathInventoryBuilder.Build(perRecord);
        var byPath = inventory.Paths.ToDictionary(p => p.Path, StringComparer.Ordinal);

        Assert.Equal(JsonNodeKind.Array, byPath["$.a"].Kind);
        Assert.Equal(JsonNodeKind.Object, byPath["$.a[*]"].Kind); // object wins over scalar
        Assert.Equal(JsonNodeKind.Value, byPath["$.a[*].k"].Kind);
    }

    [Fact]
    public void Discovery_GroupsIdenticalShapesAndOrdersByFrequency()
    {
        var result = JsonStructureDiscovery.Discover(
            new[] { ParseEdge("""{ "a": 1 }"""), ParseEdge("""{ "a": 2 }"""), ParseEdge("""{ "b": 1 }""") },
            maxDepth: 10);

        Assert.Equal(3, result.RecordsScanned);
        Assert.Equal(2, result.Structures.Count);          // {a} appears twice, {b} once
        Assert.Equal(2, result.Structures[0].RecordCount); // most common shape first
    }

    // ---------------------------------------------------------------------------------------------
    // Flatten-formula collision resolution beyond the two-way case.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void Formula_ThreeWayCollision_RemapsAllButTheFirstLosslessly()
    {
        var inventory = JsonPathInventoryBuilder.Build(new[]
        {
            JsonPathInventoryBuilder.ExtractTypedPaths(
                ParseEdge("""{ "a_b": 1, "a": { "b": 2 }, "a_b_2": 3 }"""), maxDepth: 10),
        });

        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig());
        var names = formula.Columns.Select(c => c.Name).ToList();

        // Every column name is distinct, so no source path is silently overwritten.
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("a_b", names);
        Assert.Equal("a_b_2", formula.CollisionMappings["$.a.b"]);     // the nested path is bumped past a_b_2
        Assert.Equal("a_b_2_2", formula.CollisionMappings["$.a_b_2"]);
    }

    // ---------------------------------------------------------------------------------------------
    // Option parsing edge cases.
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("first", JsonArrayHandling.FirstElement)]
    [InlineData("first_element", JsonArrayHandling.FirstElement)]
    [InlineData("unnest", JsonArrayHandling.Explode)]
    [InlineData("as_json", JsonArrayHandling.ToJson)]
    [InlineData("join_comma", JsonArrayHandling.Join)]
    [InlineData("  Skip  ", JsonArrayHandling.Skip)]
    public void ParseArrayHandling_AcceptsSynonymsAndSeparatorVariants(string value, JsonArrayHandling expected)
    {
        Assert.Equal(expected, JsonFlattenConfig.ParseArrayHandling(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseArrayHandling_BlankDefaultsToToJson(string? value)
    {
        Assert.Equal(JsonArrayHandling.ToJson, JsonFlattenConfig.ParseArrayHandling(value));
    }

    [Fact]
    public void SplitPaths_TrimsEntriesAndDropsEmpties()
    {
        Assert.Equal(new[] { "$.a", "$.b" }, JsonFlattenConfig.SplitPaths(" $.a , $.b ,, ").ToArray());
    }

    [Fact]
    public void ParsePathAliases_NormalizesConcreteArrayIndicesToWildcards()
    {
        var map = JsonFlattenConfig.ParsePathAliases("col=$.a[0].b|$.a[3].b");

        var entry = Assert.Single(map);
        Assert.Equal("$.a[*].b", entry.Key); // both concrete indices collapse to one wildcard key
        Assert.Equal("col", entry.Value);
    }

    [Fact]
    public void ParseColumnMappings_EntryWithoutEquals_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => JsonFlattenConfig.ParseColumnMappings("nopair"));
        Assert.Contains("columnMappings", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
