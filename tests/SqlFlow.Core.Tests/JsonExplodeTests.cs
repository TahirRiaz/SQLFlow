using System.Text.Json;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Edge-case coverage for array explosion in the flattener: arrays of objects and scalars, cross-product
/// of sibling arrays, nested explosion, empty arrays (left-join), mixing explode with other array handling,
/// global explode, and the no-explode single-row case. Column names strip the array index, so an exploded
/// field is one stable column with one row per element (matching the delta-forge runtime).
/// </summary>
public sealed class JsonExplodeTests
{
    private static List<Dictionary<string, string?>> FlattenRows(string json, JsonFlattenConfig config)
    {
        using var doc = JsonDocument.Parse(json);
        return new JsonPathFlattener(config)
            .FlattenRows(doc.RootElement.Clone())
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal))
            .ToList();
    }

    [Fact]
    public void Explode_ArrayOfObjects_OneRowPerElement_ParentRepeats()
    {
        var rows = FlattenRows(
            """{ "id": 1, "items": [ { "sku": "A", "qty": 2 }, { "sku": "B", "qty": 5 } ] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items"] });

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", rows[0]["id"]);
        Assert.Equal("1", rows[1]["id"]);
        Assert.Equal("A", rows[0]["items_sku"]);
        Assert.Equal("2", rows[0]["items_qty"]);
        Assert.Equal("B", rows[1]["items_sku"]);
        Assert.Equal("5", rows[1]["items_qty"]);
        Assert.DoesNotContain("items", rows[0].Keys);          // the array is consumed, not kept
        Assert.DoesNotContain("items_0_sku", rows[0].Keys);    // the index is not baked into the column name
    }

    [Fact]
    public void Explode_ArrayOfScalars_ColumnIsTheArrayName()
    {
        var rows = FlattenRows(
            """{ "id": 1, "tags": ["x", "y", "z"] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.tags"] });

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "x", "y", "z" }, rows.Select(r => r["tags"]).ToArray());
        Assert.Equal("1", rows[2]["id"]);
    }

    [Fact]
    public void Explode_SiblingArrays_CrossProduct()
    {
        var rows = FlattenRows(
            """{ "id": 1, "a": [1, 2], "b": ["x", "y", "z"] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.a", "$.b"] });

        Assert.Equal(6, rows.Count);
        var combos = rows.Select(r => $"{r["a"]}-{r["b"]}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "1-x", "1-y", "1-z", "2-x", "2-y", "2-z" }, combos);
        Assert.All(rows, r => Assert.Equal("1", r["id"]));
    }

    [Fact]
    public void Explode_Nested_ExpandsBothLevels()
    {
        var rows = FlattenRows(
            """
            { "id": 1, "groups": [
              { "g": "G1", "members": [ { "m": "a" }, { "m": "b" } ] },
              { "g": "G2", "members": [ { "m": "c" } ] }
            ] }
            """,
            new JsonFlattenConfig { ExplodePaths = ["$.groups", "$.groups[*].members"] });

        Assert.Equal(3, rows.Count);
        var combos = rows.Select(r => $"{r["groups_g"]}:{r["groups_members_m"]}")
            .OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "G1:a", "G1:b", "G2:c" }, combos);
    }

    [Fact]
    public void Explode_EmptyArray_KeepsParentRowWithNoElementColumns()
    {
        var rows = FlattenRows(
            """{ "id": 1, "items": [] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items"] });

        var row = Assert.Single(rows);
        Assert.Equal("1", row["id"]);
        Assert.DoesNotContain("items", row.Keys);
    }

    [Fact]
    public void Explode_EmptyArray_StillCrossProductsWithASiblingArray()
    {
        var rows = FlattenRows(
            """{ "id": 1, "items": [], "tags": ["x", "y"] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items", "$.tags"] });

        Assert.Equal(2, rows.Count); // empty items behaves as identity (left-join), tags drives the 2 rows
        Assert.Equal(new[] { "x", "y" }, rows.Select(r => r["tags"]).ToArray());
    }

    [Fact]
    public void Explode_OnlyConfiguredArrays_OthersFollowArrayHandling()
    {
        var rows = FlattenRows(
            """{ "id": 1, "items": [ { "sku": "A" } ], "tags": ["x", "y"] }""",
            new JsonFlattenConfig { ExplodePaths = ["$.items"] }); // tags keeps default to_json

        var row = Assert.Single(rows);
        Assert.Equal("A", row["items_sku"]);
        Assert.Equal("[\"x\",\"y\"]", row["tags"]);
    }

    [Fact]
    public void GlobalExplodeArrayHandling_ExplodesEveryArray()
    {
        var rows = FlattenRows(
            """{ "id": 1, "items": [ { "sku": "A" }, { "sku": "B" } ] }""",
            new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Explode });

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => r["items_sku"]).ToArray());
    }

    [Fact]
    public void NoExplode_ReturnsExactlyOneRow()
    {
        var rows = FlattenRows("""{ "id": 1, "tags": ["x", "y"] }""", new JsonFlattenConfig());

        var row = Assert.Single(rows);
        Assert.Equal("[\"x\",\"y\"]", row["tags"]);
    }

    [Fact]
    public void Explode_PathAbsentFromRecord_YieldsOneRow()
    {
        var rows = FlattenRows("""{ "id": 1 }""", new JsonFlattenConfig { ExplodePaths = ["$.items"] });
        Assert.Single(rows);
    }

    [Fact]
    public void SchemaFlatten_VisitsExplodedElementFields_WithoutMultiplyingRows()
    {
        // Flatten (the schema view) returns one row carrying every column, including exploded element
        // fields, so the reader can discover the schema without materializing the cross-product.
        using var doc = JsonDocument.Parse("""{ "id": 1, "items": [ { "sku": "A" }, { "sku": "B" } ] }""");
        var pairs = new JsonPathFlattener(new JsonFlattenConfig { ExplodePaths = ["$.items"] })
            .Flatten(doc.RootElement.Clone());

        var names = pairs.Select(p => p.Key).ToList();
        Assert.Contains("id", names);
        Assert.Contains("items_sku", names);
    }
}
