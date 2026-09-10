using System.Xml.Linq;
using SqlFlow.Core;
using SqlFlow.Sources.Xml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Engine-level tests for the path-based XML flattener: nested elements, attributes, repeating elements
/// (XML's arrays) under each repeat-handling mode, explode, exclude / xml-string / include rules, and
/// schema-evolution aliases. Runs without a database.
/// </summary>
public sealed class XmlFlattenerTests
{
    private static Dictionary<string, string?> Flatten(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).Flatten(XElement.Parse(xml))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static List<Dictionary<string, string?>> FlattenRows(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).FlattenRows(XElement.Parse(xml))
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public void Flatten_NestedElements_JoinKeysWithSeparator()
    {
        var row = Flatten(
            "<order><id>1</id><customer><name>Ann</name><city>Oslo</city></customer></order>",
            new XmlFlattenConfig());

        Assert.Equal("1", row["id"]);
        Assert.Equal("Ann", row["customer_name"]);
        Assert.Equal("Oslo", row["customer_city"]);
    }

    [Fact]
    public void Flatten_Attributes_BecomeColumns()
    {
        var row = Flatten("""<order id="7" status="open"><total>9.5</total></order>""", new XmlFlattenConfig());

        Assert.Equal("7", row["id"]);       // /@id with default "@" prefix sanitizes to id
        Assert.Equal("open", row["status"]);
        Assert.Equal("9.5", row["total"]);
    }

    [Fact]
    public void Flatten_AttributePrefix_KeepsAttributeDistinctFromSameNamedElement()
    {
        var row = Flatten("""<book id="1"><id>internal</id></book>""", new XmlFlattenConfig { AttributePrefix = "attr_" });

        Assert.Equal("1", row["attr_id"]);
        Assert.Equal("internal", row["id"]);
    }

    [Fact]
    public void Flatten_NestedAttribute_PathIncludesParent()
    {
        var row = Flatten("""<order><price currency="USD">9.99</price></order>""", new XmlFlattenConfig());

        Assert.Equal("9.99", row["price"]);            // element text
        Assert.Equal("USD", row["price_currency"]);    // /price/@currency
    }

    [Fact]
    public void Flatten_RepeatingElements_DefaultKeepsXmlFragment()
    {
        var row = Flatten("<order><item>A</item><item>B</item></order>", new XmlFlattenConfig());
        Assert.Equal("<item>A</item><item>B</item>", row["item"]);
    }

    [Fact]
    public void Flatten_RepeatingElements_Count()
    {
        var row = Flatten("<order><item>A</item><item>B</item><item>C</item></order>",
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Count });
        Assert.Equal("3", row["item"]);
    }

    [Fact]
    public void Flatten_RepeatingElements_Join()
    {
        var row = Flatten("<o><tag>x</tag><tag>y</tag></o>",
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Join, JoinSeparator = ";" });
        Assert.Equal("x;y", row["tag"]);
    }

    [Fact]
    public void Flatten_XmlPaths_KeepSubtreeAsOneColumn()
    {
        var row = Flatten("<o><id>1</id><meta><a>1</a><b>2</b></meta></o>",
            new XmlFlattenConfig { XmlPaths = ["/meta"] });

        Assert.Equal("1", row["id"]);
        Assert.Equal("<meta><a>1</a><b>2</b></meta>", row["meta"]);
        Assert.False(row.ContainsKey("meta_a"));
    }

    [Fact]
    public void Flatten_ExcludePaths_DropsSubtree()
    {
        var row = Flatten("<o><id>1</id><debug><trace>x</trace></debug></o>",
            new XmlFlattenConfig { ExcludePaths = ["/debug"] });

        Assert.True(row.ContainsKey("id"));
        Assert.False(row.ContainsKey("debug_trace"));
    }

    [Fact]
    public void Flatten_IncludePaths_Whitelists()
    {
        var row = Flatten("<o><keep><a>1</a></keep><drop><b>2</b></drop></o>",
            new XmlFlattenConfig { IncludePaths = ["/keep"] });

        Assert.True(row.ContainsKey("keep_a"));
        Assert.False(row.ContainsKey("drop_b"));
    }

    [Fact]
    public void Flatten_MaxDepth_CollapsesDeeperNodesToXml()
    {
        var row = Flatten("<o><a><b><c>1</c></b></a></o>", new XmlFlattenConfig { MaxDepth = 1 });
        // /a is depth 1 (recurses); /a/b is depth 2 (> maxDepth) -> kept as XML.
        Assert.Equal("<b><c>1</c></b>", row["a_b"]);
        Assert.False(row.ContainsKey("a_b_c"));
    }

    [Fact]
    public void Flatten_ColumnMappings_RenameColumn()
    {
        var row = Flatten("<o><customer><id>42</id></customer></o>",
            new XmlFlattenConfig { ColumnMappings = new Dictionary<string, string> { ["/customer/id"] = "customer_key" } });

        Assert.Equal("42", row["customer_key"]);
        Assert.False(row.ContainsKey("customer_id"));
    }

    [Fact]
    public void Explode_RepeatingElement_OneRowPerElement()
    {
        var rows = FlattenRows(
            "<order><id>1</id><line><sku>A</sku></line><line><sku>B</sku></line></order>",
            new XmlFlattenConfig { ExplodePaths = ["/line"] });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("1", r["id"]));
        Assert.Equal("A", rows[0]["line_sku"]);
        Assert.Equal("B", rows[1]["line_sku"]);
        Assert.DoesNotContain("line", rows[0].Keys);   // the repeating element is consumed
    }

    [Fact]
    public void Explode_SiblingRepeats_CrossProduct()
    {
        var rows = FlattenRows(
            "<o><id>1</id><a>1</a><a>2</a><b>x</b><b>y</b></o>",
            new XmlFlattenConfig { ExplodePaths = ["/a", "/b"] });

        Assert.Equal(4, rows.Count);
        var combos = rows.Select(r => $"{r["a"]}-{r["b"]}").OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "1-x", "1-y", "2-x", "2-y" }, combos);
    }

    /// <summary>
    /// The cross-product bound is per FLOW, not a hardcoded constant. Two independent repeats multiply, so a
    /// document far smaller than the default cap can still be bounded deliberately (a wide row costs far more
    /// memory than a narrow one, and the bound is counted in rows).
    /// </summary>
    [Fact]
    public void Explode_BeyondConfiguredRowBound_FailsLoudlyNamingThePathAndTheOption()
    {
        var xml = "<r><a>1</a><a>2</a><a>3</a><b>x</b><b>y</b><b>z</b></r>";

        // 3 x 3 = 9 rows, which the default cap would allow.
        Assert.Equal(9, FlattenRows(xml, new XmlFlattenConfig { ExplodePaths = ["/a", "/b"] }).Count);

        var ex = Assert.Throws<SqlFlowException>(() =>
            FlattenRows(xml, new XmlFlattenConfig { ExplodePaths = ["/a", "/b"], MaxRowsPerRecord = 4 }));

        Assert.Contains("more than 4 rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains("maxRowsPerRecord", ex.Message, StringComparison.Ordinal);
        Assert.Contains("/b", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The rows of one record share a single column plan and carry only their values, so a row must still
    /// report every column the record produces, including the ones it has no value for.
    /// </summary>
    [Fact]
    public void Explode_RowsShareTheRecordColumnPlan_SoAbsentColumnsSurfaceAsNull()
    {
        var rows = FlattenRows(
            "<r><id>1</id><a><x>1</x></a><a><y>2</y></a></r>",
            new XmlFlattenConfig { ExplodePaths = ["/a"] });

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("1", r["id"]));

        // Both rows expose both exploded columns; the one that did not supply a value reports null, not a
        // missing key. The positional row representation depends on this being true.
        Assert.Equal("1", rows[0]["a_x"]);
        Assert.Null(rows[0]["a_y"]);
        Assert.Null(rows[1]["a_x"]);
        Assert.Equal("2", rows[1]["a_y"]);
    }

    [Fact]
    public void Explode_EmptyRepeat_KeepsParentRow()
    {
        // No <line> elements: the parent row survives (left-join), line columns absent.
        var rows = FlattenRows("<order><id>1</id></order>",
            new XmlFlattenConfig { ExplodePaths = ["/line"] });

        var row = Assert.Single(rows);
        Assert.Equal("1", row["id"]);
    }

    [Fact]
    public void PathAliases_RenamedElementAcrossVersions_OneColumnNeverNull()
    {
        var config = new XmlFlattenConfig
        {
            PathAliasColumns = XmlFlattenConfig.ParsePathAliases("person_name=/name|/fullName"),
        };

        var v1 = FlattenRows("<p><id>1</id><name>Ann</name></p>", config);
        var v2 = FlattenRows("<p><id>2</id><fullName>Bob</fullName></p>", config);

        Assert.Equal("Ann", v1[0]["person_name"]);
        Assert.Equal("Bob", v2[0]["person_name"]);
        Assert.False(v1[0].ContainsKey("full_name"));
    }

    [Fact]
    public void ParseRepeatHandling_RejectsUnknownWithGuidance()
    {
        var ex = Assert.Throws<SqlFlowException>(() => XmlFlattenConfig.ParseRepeatHandling("bogus"));
        Assert.Contains("repeatHandling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IsExcluded_BracketedWildcard_DoesNotMatchNonNumericSubscript()
    {
        var config = new XmlFlattenConfig { ExcludePaths = ["/a/b[*]/c"] };
        Assert.True(config.IsExcluded("/a/b[0]/c"));
        Assert.False(config.IsExcluded("/a/b[x]/c"));
    }

    [Fact]
    public void IsIncluded_DoesNotLeakPrefixSiblings()
    {
        var config = new XmlFlattenConfig { IncludePaths = ["/item"] };
        Assert.True(config.IsIncluded("/item"));
        Assert.False(config.IsIncluded("/items"));
        Assert.False(config.IsIncluded("/items/sku"));
    }

}
