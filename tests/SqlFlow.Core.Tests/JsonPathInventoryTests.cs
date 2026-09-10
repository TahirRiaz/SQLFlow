using System.Text.Json;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Tests for the typed path inventory behind the <c>paths</c> command: it surfaces object and array
/// container paths (not just the leaves), reports the kind of each path, and aggregates presence across
/// records. Includes a reader-level scan over temp files.
/// </summary>
public sealed class JsonPathInventoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_jsonpaths_" + Guid.NewGuid().ToString("N"));

    public JsonPathInventoryTests() => Directory.CreateDirectory(_dir);

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static Dictionary<string, JsonNodeKind> Inventory(string json)
    {
        var nodes = JsonPathInventoryBuilder.ExtractTypedPaths(Parse(json), maxDepth: 20);
        return nodes.ToDictionary(n => n.Path, n => n.Kind, StringComparer.Ordinal);
    }

    [Fact]
    public void Extract_SurfacesObjectAndArrayContainersNotJustLeaves()
    {
        var inv = Inventory("""{ "id": 1, "vendor": { "id": 9, "name": "AC/DC" } }""");

        Assert.Equal(JsonNodeKind.Value, inv["$.id"]);
        Assert.Equal(JsonNodeKind.Object, inv["$.vendor"]); // the container path discover would not list
        Assert.Equal(JsonNodeKind.Value, inv["$.vendor.id"]);
        Assert.Equal(JsonNodeKind.Value, inv["$.vendor.name"]);
    }

    [Fact]
    public void Extract_ArrayOfObjects_YieldsArrayElementAndFieldPaths()
    {
        var inv = Inventory("""{ "details": [ { "track_id": 14, "name": "x" } ] }""");

        Assert.Equal(JsonNodeKind.Array, inv["$.details"]);
        Assert.Equal(JsonNodeKind.Object, inv["$.details[*]"]);
        Assert.Equal(JsonNodeKind.Value, inv["$.details[*].track_id"]);
        Assert.Equal(JsonNodeKind.Value, inv["$.details[*].name"]);
    }

    [Fact]
    public void Extract_ArrayOfScalars_YieldsArrayAndScalarElementPath()
    {
        var inv = Inventory("""{ "tags": ["a", "b", "c"] }""");

        Assert.Equal(JsonNodeKind.Array, inv["$.tags"]);
        // The scalar element path is surfaced so an exploded scalar array still yields its column.
        Assert.Equal(JsonNodeKind.Value, inv["$.tags[*]"]);
    }

    [Fact]
    public void Extract_RespectsMaxDepth()
    {
        var nodes = JsonPathInventoryBuilder.ExtractTypedPaths(Parse("""{ "a": { "b": { "c": 1 } } }"""), maxDepth: 1);
        var paths = nodes.Select(n => n.Path).ToHashSet();

        Assert.Contains("$.a", paths);
        Assert.Contains("$.a.b", paths);          // depth 1 child of $.a, allowed
        Assert.DoesNotContain("$.a.b.c", paths);  // depth 2 is beyond maxDepth 1
    }

    [Fact]
    public void Build_AggregatesPresenceAndPrefersStructuredKindAcrossRecords()
    {
        var records = new[]
        {
            JsonPathInventoryBuilder.ExtractTypedPaths(Parse("""{ "id": 1, "extra": "scalar" }"""), 20),
            JsonPathInventoryBuilder.ExtractTypedPaths(Parse("""{ "id": 2, "extra": { "deep": true } }"""), 20),
            JsonPathInventoryBuilder.ExtractTypedPaths(Parse("""{ "id": 3 }"""), 20),
        };

        var inventory = JsonPathInventoryBuilder.Build(records);
        var byPath = inventory.Paths.ToDictionary(p => p.Path, StringComparer.Ordinal);

        Assert.Equal(3, inventory.RecordsScanned);
        Assert.Equal(3, byPath["$.id"].RecordCount);      // present in all
        Assert.Equal(2, byPath["$.extra"].RecordCount);   // present in 2 of 3
        // $.extra is a value in one record, an object in another -> the structured kind wins.
        Assert.Equal(JsonNodeKind.Object, byPath["$.extra"].Kind);
    }

    [Fact]
    public async Task Reader_InventoryAsync_ScansFolderAndCountsFiles()
    {
        File.WriteAllText(Path.Combine(_dir, "a.json"), """{ "id": 1, "vendor": { "name": "x" } }""");
        File.WriteAllText(Path.Combine(_dir, "b.json"), """{ "id": 2, "tags": ["t"] }""");

        var reader = new JsonSourceReader(new LocalFileLifecycle(), [new LocalFileStore()]);
        var source = new SourceSpec
        {
            Type = "json",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.json" },
        };

        var inventory = await reader.InventoryAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 20);

        Assert.Equal(2, inventory.FilesScanned);
        Assert.Equal(2, inventory.RecordsScanned);
        var paths = inventory.Paths.ToDictionary(p => p.Path, p => p.Kind, StringComparer.Ordinal);
        Assert.Equal(JsonNodeKind.Object, paths["$.vendor"]);
        Assert.Equal(JsonNodeKind.Array, paths["$.tags"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
