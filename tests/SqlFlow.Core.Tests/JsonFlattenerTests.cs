using System.Text;
using System.Text.Json;
using SqlFlow.Core;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Engine-level tests for the path-based JSON flattener, the record reader (object / array / NDJSON
/// detection and root-path navigation), and structure discovery. These run without a database.
/// </summary>
public sealed class JsonFlattenerTests
{
    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, string?> Flatten(string json, JsonFlattenConfig config)
    {
        var pairs = new JsonPathFlattener(config).Flatten(Parse(json));
        return pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
    }

    [Fact]
    public void Flatten_NestedObject_JoinsKeysWithSeparatorAndTypesValuesAsStrings()
    {
        var row = Flatten(
            """{ "id": 7, "active": true, "ratio": 1.50, "missing": null, "address": { "city": "Oslo" } }""",
            new JsonFlattenConfig());

        Assert.Equal("7", row["id"]);
        Assert.Equal("true", row["active"]);
        Assert.Equal("1.50", row["ratio"]); // raw token preserved (no 1.5 reformat)
        Assert.Null(row["missing"]);
        Assert.Equal("Oslo", row["address_city"]);
    }

    [Fact]
    public void Flatten_PreservesFirstSeenColumnOrder()
    {
        var pairs = new JsonPathFlattener(new JsonFlattenConfig())
            .Flatten(Parse("""{ "b": 1, "a": { "z": 2, "y": 3 } }"""));

        Assert.Equal(new[] { "b", "a_z", "a_y" }, pairs.Select(p => p.Key).ToArray());
    }

    [Fact]
    public void Flatten_ArrayToJson_IsDefaultAndKeepsCompactJson()
    {
        var row = Flatten("""{ "tags": ["x", "y"] }""", new JsonFlattenConfig());
        Assert.Equal("[\"x\",\"y\"]", row["tags"]);
    }

    [Fact]
    public void Flatten_ArrayFirstElement_FlattensFirstObjectInPlace()
    {
        var row = Flatten(
            """{ "items": [ { "sku": "A1", "qty": 2 }, { "sku": "B2", "qty": 9 } ] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });

        Assert.Equal("A1", row["items_sku"]);
        Assert.Equal("2", row["items_qty"]);
    }

    [Fact]
    public void Flatten_ArrayJoin_UsesJoinSeparator()
    {
        var row = Flatten(
            """{ "tags": ["x", "y", "z"] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Join, JoinSeparator = ";" });

        Assert.Equal("x;y;z", row["tags"]);
    }

    [Fact]
    public void Flatten_ArrayCount_EmitsElementCount()
    {
        var row = Flatten("""{ "tags": ["x", "y", "z"] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Count });
        Assert.Equal("3", row["tags"]);
    }

    [Fact]
    public void Flatten_ArraySkip_DropsTheColumn()
    {
        var row = Flatten("""{ "keep": 1, "tags": ["x"] }""", new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Skip });
        Assert.True(row.ContainsKey("keep"));
        Assert.False(row.ContainsKey("tags"));
    }

    [Fact]
    public void Flatten_ExcludePath_DropsTheSubtreeEntirely()
    {
        var row = Flatten(
            """{ "id": 1, "secret": { "token": "abc" } }""",
            new JsonFlattenConfig { ExcludePaths = ["$.secret"] });

        Assert.True(row.ContainsKey("id"));
        Assert.False(row.ContainsKey("secret"));
        Assert.False(row.ContainsKey("secret_token"));
    }

    [Fact]
    public void Flatten_JsonPath_KeepsTheSubtreeAsOneJsonStringColumn()
    {
        var row = Flatten(
            """{ "id": 1, "payload": { "a": 1, "b": [2, 3] } }""",
            new JsonFlattenConfig { JsonPaths = ["$.payload"] });

        Assert.Equal("1", row["id"]);
        Assert.Equal("{\"a\":1,\"b\":[2,3]}", row["payload"]);
        Assert.False(row.ContainsKey("payload_a"));
    }

    [Fact]
    public void Flatten_IncludePaths_WhitelistsAndStillTraversesAncestors()
    {
        var row = Flatten(
            """{ "keep": { "deep": 5 }, "drop": 9, "other": { "x": 1 } }""",
            new JsonFlattenConfig { IncludePaths = ["$.keep"] });

        Assert.Equal("5", row["keep_deep"]);
        Assert.False(row.ContainsKey("drop"));
        Assert.False(row.ContainsKey("other_x"));
    }

    [Fact]
    public void Flatten_MaxDepth_CollapsesDeeperNodesToJsonString()
    {
        var row = Flatten(
            """{ "a": { "b": { "c": 1 } } }""",
            new JsonFlattenConfig { MaxDepth = 1 });

        // $.a is depth 1 (allowed, recurses); $.a.b is depth 2 (> maxDepth) -> JSON string.
        Assert.Equal("{\"c\":1}", row["a_b"]);
        Assert.False(row.ContainsKey("a_b_c"));
    }

    [Fact]
    public void Flatten_ColumnMappings_RenameTheColumn()
    {
        var row = Flatten(
            """{ "user": { "id": 42 } }""",
            new JsonFlattenConfig { ColumnMappings = new Dictionary<string, string> { ["$.user.id"] = "user_key" } });

        Assert.Equal("42", row["user_key"]);
        Assert.False(row.ContainsKey("user_id"));
    }

    [Theory]
    [InlineData("$.address.city", "address_city")]
    [InlineData("$.user.firstName", "user_firstName")]
    [InlineData("$.2nd.value", "_2nd_value")]
    [InlineData("$.weird key!", "weird_key")]
    public void ColumnName_SanitizesPathIntoValidIdentifier(string path, string expected)
    {
        Assert.Equal(expected, new JsonFlattenConfig().ColumnName(path));
    }

    [Fact]
    public void IsIncluded_DoesNotLeakPrefixSiblings()
    {
        var config = new JsonFlattenConfig { IncludePaths = ["$.item"] };

        Assert.True(config.IsIncluded("$.item"));
        Assert.False(config.IsIncluded("$.items"));      // a sibling sharing the prefix is not whitelisted
        Assert.False(config.IsIncluded("$.items.sku"));
        Assert.False(config.IsIncluded("$.itemize"));
    }

    [Fact]
    public void IsExcluded_BracketedWildcard_DoesNotMatchNonNumericSubscript()
    {
        var config = new JsonFlattenConfig { ExcludePaths = ["$.a[*].b"] };

        Assert.True(config.IsExcluded("$.a[0].b"));    // a numeric index normalizes to the wildcard
        Assert.False(config.IsExcluded("$.a[x].b"));   // a non-numeric subscript must not match
    }

    [Fact]
    public void ParseArrayHandling_AcceptsExplode()
    {
        Assert.Equal(JsonArrayHandling.Explode, JsonFlattenConfig.ParseArrayHandling("explode"));
        Assert.Equal(JsonArrayHandling.Explode, JsonFlattenConfig.ParseArrayHandling("EXPLODE"));
    }

    [Fact]
    public void ParseArrayHandling_RejectsUnknownValueWithAGuidingMessage()
    {
        var ex = Assert.Throws<SqlFlowException>(() => JsonFlattenConfig.ParseArrayHandling("bogus"));
        Assert.Contains("arrayHandling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RecordReader_SingleObject_YieldsOneRecord()
    {
        // Records are valid only during iteration (the backing document is short-lived), so the kind is
        // captured inside the loop rather than after materializing the sequence.
        var kinds = await RecordKinds("""{ "id": 1 }""");

        Assert.Single(kinds);
        Assert.Equal(JsonValueKind.Object, kinds[0]);
    }

    [Fact]
    public async Task RecordReader_TopLevelArray_FansOutToOneRecordPerElement()
    {
        var records = await RecordKinds("""[ { "id": 1 }, { "id": 2 }, { "id": 3 } ]""");
        Assert.Equal(3, records.Count);
    }

    [Fact]
    public async Task RecordReader_Ndjson_ParsesOneRecordPerLineAndSkipsBlanksAndComments()
    {
        var ndjson = "{ \"id\": 1 }\n\n# a comment\n{ \"id\": 2 }\n";
        var records = await RecordKinds(ndjson, fileName: "x.ndjson");
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task RecordReader_PlainJsonThatIsActuallyNdjson_ReadsEachLineAsARecord()
    {
        // Two objects on two lines is not one valid JSON document; the reader treats a stream of top-level
        // values as exactly that, so NDJSON needs no separate mode.
        var records = await RecordKinds("{ \"id\": 1 }\n{ \"id\": 2 }\n");
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task RecordReader_RootPath_NavigatesToANestedRecordArray()
    {
        var json = """{ "meta": { "v": 1 }, "data": { "records": [ { "id": 1 }, { "id": 2 } ] } }""";
        var records = await RecordKinds(json, "$.data.records");
        Assert.Equal(2, records.Count);
    }

    [Fact]
    public async Task RecordReader_StripsUtf8Bom()
    {
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Bytes("""{ "id": 1 }""")).ToArray();
        using var stream = new MemoryStream(withBom);
        var records = new List<JsonValueKind>();
        await foreach (var record in JsonRecordReader.ReadRecordsAsync(stream, "$", "x.json"))
        {
            records.Add(record.ValueKind);
        }

        Assert.Single(records);
    }

    [Fact]
    public async Task RecordReader_MalformedNdjsonLine_ThrowsWithLineNumber()
    {
        var ex = await Assert.ThrowsAsync<SqlFlowException>(
            () => RecordKinds("{ \"id\": 1 }\n{ oops\n", fileName: "bad.ndjson"));
        Assert.Contains("line 2", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads every record's value kind, which both counts the records and keeps to the reader's contract that
    /// an element is valid only until the next one is requested.
    /// </summary>
    private static async Task<List<JsonValueKind>> RecordKinds(string content, string rootPath = "$", string fileName = "x.json")
    {
        using var stream = new MemoryStream(Bytes(content));
        var kinds = new List<JsonValueKind>();
        await foreach (var record in JsonRecordReader.ReadRecordsAsync(stream, rootPath, fileName))
        {
            kinds.Add(record.ValueKind);
        }

        return kinds;
    }

    [Fact]
    public void Discovery_ReportsPathsAndDistinctShapes()
    {
        var records = new[]
        {
            Parse("""{ "id": 1, "name": "a" }"""),
            Parse("""{ "id": 2, "name": "b" }"""),
            Parse("""{ "id": 3, "extra": true }"""),
        };

        var result = JsonStructureDiscovery.Discover(records, maxDepth: 10);

        Assert.Equal(3, result.RecordsScanned);
        Assert.Contains("$.id", result.AllPaths);
        Assert.Contains("$.name", result.AllPaths);
        Assert.Contains("$.extra", result.AllPaths);
        Assert.Equal(2, result.Structures.Count); // {id,name} and {id,extra}
    }

    [Fact]
    public void Discovery_MarksArrayBearingPathsWithWildcard()
    {
        var result = JsonStructureDiscovery.Discover([Parse("""{ "items": [ { "sku": "A" } ] }""")], maxDepth: 10);
        Assert.Contains("$.items[*].sku", result.AllPaths);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
