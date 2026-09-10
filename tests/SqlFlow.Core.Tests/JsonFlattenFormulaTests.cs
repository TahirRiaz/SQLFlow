using System.Text.Json;
using SqlFlow.Sources.Json;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Tests the flatten-formula builder: which paths become columns under a config, and how column-name
/// collisions are resolved into explicit mappings so the flatten stays lossless.
/// </summary>
public sealed class JsonFlattenFormulaTests
{
    private static JsonPathInventory Inventory(params string[] jsonRecords)
    {
        var perRecord = jsonRecords.Select(json =>
        {
            using var doc = JsonDocument.Parse(json);
            return JsonPathInventoryBuilder.ExtractTypedPaths(doc.RootElement.Clone(), maxDepth: 20);
        });

        return JsonPathInventoryBuilder.Build(perRecord);
    }

    [Fact]
    public void Build_ValueAndArrayPathsBecomeColumns_ObjectsAndElementFieldsDoNot()
    {
        var inventory = Inventory("""{ "id": 1, "vendor": { "name": "x" }, "tags": ["a"], "items": [ { "sku": "s" } ] }""");
        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig());

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("id", names);
        Assert.Contains("vendor_name", names);   // value under an object
        Assert.Contains("tags", names);          // array -> one column (to_json)
        Assert.Contains("items", names);         // array -> one column
        Assert.DoesNotContain("vendor", names);  // object container is not a column
        Assert.DoesNotContain("items_items_sku", names); // [*] element field is not a column without explode
    }

    [Fact]
    public void Build_ResolvesCollisionsLosslessly()
    {
        // Both $.vendor_id and $.vendor.id fold onto "vendor_id".
        var inventory = Inventory("""{ "vendor_id": 1, "vendor": { "id": 2 } }""");
        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig());

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("vendor_id", names);
        Assert.Contains("vendor_id_2", names); // the second path is remapped, not dropped
        Assert.Equal(formula.Columns.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The remapped path is the deeper one, captured as an explicit mapping.
        Assert.Equal("vendor_id_2", formula.CollisionMappings["$.vendor.id"]);
    }

    [Fact]
    public void Build_ExcludedSubtreeProducesNoColumns()
    {
        var inventory = Inventory("""{ "id": 1, "debug": { "trace": "x" } }""");
        var config = new JsonFlattenConfig { ExcludePaths = ["$.debug"] };
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("id", names);
        Assert.DoesNotContain("debug_trace", names);
    }

    [Fact]
    public void Build_JsonPathBecomesOneColumnAndChildrenAreFolded()
    {
        var inventory = Inventory("""{ "id": 1, "payload": { "a": 1, "b": 2 } }""");
        var config = new JsonFlattenConfig { JsonPaths = ["$.payload"] };
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("payload", names);
        Assert.DoesNotContain("payload_a", names);
        Assert.DoesNotContain("payload_b", names);
    }

    [Fact]
    public void Build_ExplodedArray_RealizesElementFieldsAndDropsTheArrayColumn()
    {
        var inventory = Inventory("""{ "id": 1, "items": [ { "sku": "A", "qty": 2 } ] }""");
        var config = new JsonFlattenConfig { ExplodePaths = ["$.items"] };
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("id", names);
        Assert.Contains("items_sku", names);   // element fields become columns under explode
        Assert.Contains("items_qty", names);
        Assert.DoesNotContain("items", names);  // the exploded array is not a column
    }

    [Fact]
    public void Build_ExplodedScalarArray_YieldsTheArrayColumn()
    {
        // Regression: the formula used to omit the column for an exploded array of scalars.
        var inventory = Inventory("""{ "id": 1, "tags": ["x", "y"] }""");
        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig { ExplodePaths = ["$.tags"] });

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("id", names);
        Assert.Single(names, n => n == "tags");   // exactly one realized column for the exploded scalar
    }

    [Fact]
    public void Build_HeterogeneousExplodedArray_IncludesFieldsFromAllElements()
    {
        // Regression: only the first element's fields used to be inventoried.
        var inventory = Inventory("""{ "items": [ { "sku": "A" }, { "qty": 2 } ] }""");
        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig { ExplodePaths = ["$.items"] });

        var names = formula.Columns.Select(c => c.Name).ToList();
        Assert.Contains("items_sku", names);
        Assert.Contains("items_qty", names);   // field present only in the second element
    }

    [Fact]
    public void Build_FirstElement_RealizesElementFields()
    {
        // Regression: a first_element config used to produce an empty formula.
        var inventory = Inventory("""{ "a": [ { "x": 1 } ] }""");
        var formula = JsonFlattenFormulaBuilder.Build(inventory, new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.FirstElement });

        Assert.Contains("a_x", formula.Columns.Select(c => c.Name));
    }

    [Fact]
    public void Build_FlagsArrayAndJsonPathColumnsAsJsonText()
    {
        var inventory = Inventory("""{ "id": 1, "tags": ["a"], "payload": { "x": 1 } }""");
        var config = new JsonFlattenConfig { JsonPaths = ["$.payload"] };
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        var byName = formula.Columns.ToDictionary(c => c.Name, c => c.IsJsonText, StringComparer.Ordinal);
        Assert.False(byName["id"]);       // a scalar stays a scalar column
        Assert.True(byName["tags"]);      // an array kept whole is JSON text
        Assert.True(byName["payload"]);   // a json-path subtree is JSON text
    }

    [Fact]
    public void Build_SkipArrayHandling_DropsArrayColumns()
    {
        var inventory = Inventory("""{ "id": 1, "tags": ["a", "b"] }""");
        var config = new JsonFlattenConfig { ArrayHandling = JsonArrayHandling.Skip };
        var formula = JsonFlattenFormulaBuilder.Build(inventory, config);

        Assert.Contains("id", formula.Columns.Select(c => c.Name));
        Assert.DoesNotContain("tags", formula.Columns.Select(c => c.Name));
    }
}
