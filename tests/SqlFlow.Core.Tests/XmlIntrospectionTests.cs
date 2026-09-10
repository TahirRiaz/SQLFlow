using System.Xml.Linq;
using SqlFlow.Sources.Xml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Tests the XML inventory + flatten formula (which drive the paths/flatten/discover commands) and the
/// flattener fixes from the adversarial review: an element that is both a container and carries text
/// (attributes-on-a-leaf, mixed content, heterogeneous repeats, scalar records) must produce the text
/// column in BOTH the formula and the runtime flatten, and an include rule with a subscript must not drop
/// its data.
/// </summary>
public sealed class XmlIntrospectionTests
{
    private static List<string> FormulaColumns(XmlFlattenConfig config, params string[] xmlRecords)
    {
        // The formula is the union of the flattener's own resolved schema columns, so it is exactly what the
        // load produces - the test mirrors how IntrospectAsync builds it.
        var flattener = new XmlPathFlattener(config);
        var perRecord = xmlRecords.Select(x => flattener.SchemaColumns(XElement.Parse(x)));
        return XmlFlattenFormulaBuilder.FromRecords(perRecord).Columns.Select(c => c.Name).ToList();
    }

    private static Dictionary<string, string?> Flatten(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).Flatten(XElement.Parse(xml))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static List<Dictionary<string, string?>> FlattenRows(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).FlattenRows(XElement.Parse(xml))
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase))
            .ToList();

    [Fact]
    public void Formula_AttributeBearingLeaf_KeepsBothTextAndAttributeColumns()
    {
        // Regression: the inventory used to classify <price currency=...>9.99</price> as a container and drop
        // the price text column, which the load actually creates.
        var columns = FormulaColumns(new XmlFlattenConfig(), """<order><price currency="USD">9.99</price></order>""");

        Assert.Contains("price", columns);
        Assert.Contains("price_currency", columns);
    }

    [Fact]
    public void Formula_ScalarRecord_HasColumn()
    {
        // A leaf record element (no children, only text) must surface its column, matching the load.
        var columns = FormulaColumns(new XmlFlattenConfig(), "<color>red</color>");
        Assert.Contains("color", columns);
    }

    [Fact]
    public void Formula_HeterogeneousExplodedRepeat_HasBothScalarAndFieldColumns()
    {
        // First sibling is a container, second is a scalar leaf: both item_sku and item must appear.
        var columns = FormulaColumns(
            new XmlFlattenConfig { ExplodePaths = ["/item"] },
            "<r><item><sku>X</sku></item><item>plain</item></r>");

        Assert.Contains("item_sku", columns);
        Assert.Contains("item", columns);
    }

    [Fact]
    public void Formula_MatchesLoadForAttributeLeaf()
    {
        // The formula's column set must equal what the flattener produces for the same record.
        const string xml = """<order id="1"><price currency="USD">9.99</price></order>""";
        var loaded = Flatten(xml, new XmlFlattenConfig()).Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
        var formula = FormulaColumns(new XmlFlattenConfig(), xml).OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(loaded, formula);
    }

    [Fact]
    public void Flatten_MixedContent_KeepsTheElementsOwnText()
    {
        // Regression (data loss): text interleaved with child elements was dropped.
        var row = Flatten("<o><note>start<b>x</b>end</note></o>", new XmlFlattenConfig());

        Assert.Equal("startend", row["note"]);   // the element's own direct text
        Assert.Equal("x", row["note_b"]);
    }

    [Fact]
    public void Flatten_IncludePathWithSubscript_DoesNotDropData()
    {
        // Regression (data loss): an include like /line[*]/sku made /line unreachable, dropping sku entirely.
        var rows = FlattenRows(
            "<order><line><sku>A</sku></line><line><sku>B</sku></line></order>",
            new XmlFlattenConfig { IncludePaths = ["/line[*]/sku"], ExplodePaths = ["/line"] });

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => (string?)r.GetValueOrDefault("line_sku")).ToArray());
    }

    // The formula is the union of the flattener's own schema columns, so it must equal the flatten's column set
    // for every config. These are the configs the re-review showed diverging under the old inventory-based formula.
    private static void AssertFormulaMatchesFlatten(XmlFlattenConfig config, string xml)
    {
        var flattenCols = Flatten(xml, config).Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        var formulaCols = FormulaColumns(config, xml).OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(flattenCols, formulaCols);
    }

    [Fact]
    public void Flatten_AttributeVsSameNamedElement_KeepsBothValuesLossless()
    {
        // Finding 1 (critical): the default '@' prefix sanitizes away, so /@id and /id both want 'id'. The
        // flatten must de-collide and keep BOTH values, not overwrite one.
        var row = Flatten("""<rec id="A"><id>B</id></rec>""", new XmlFlattenConfig());

        Assert.Equal("A", row["id"]);     // attribute, written first
        Assert.Equal("B", row["id_2"]);   // same-named child element, de-collided
        AssertFormulaMatchesFlatten(new XmlFlattenConfig(), """<rec id="A"><id>B</id></rec>""");
    }

    [Fact]
    public void Flatten_CaseOnlyCollision_KeepsBothValues()
    {
        // Finding 8: <Tag> and <tag> fold to one case-insensitive column; the flatten must keep both values.
        var row = Flatten("<row><Tag>UPPER</Tag><tag>lower</tag></row>", new XmlFlattenConfig());

        Assert.Equal("UPPER", row["Tag"]);
        Assert.Equal("lower", row["tag_2"]);
    }

    [Fact]
    public void FormulaMatchesFlatten_AcrossConfigsThatPreviouslyDiverged()
    {
        AssertFormulaMatchesFlatten(new XmlFlattenConfig(), """<rec id="A"><id>B</id></rec>""");               // finding 1
        AssertFormulaMatchesFlatten(new XmlFlattenConfig { MaxDepth = 1 }, "<o><a><b>text</b></a></o>");        // findings 2/4
        AssertFormulaMatchesFlatten(                                                                            // finding 3
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.FirstElement },
            "<r><line>plain</line><line><sku>X</sku></line></r>");
        AssertFormulaMatchesFlatten(new XmlFlattenConfig { ExcludePaths = ["/a*c"] },                           // finding 5
            "<rec><abc><token>secret</token></abc><keep>1</keep></rec>");
        AssertFormulaMatchesFlatten(new XmlFlattenConfig { IncludePaths = ["/other"] }, "<rec>scalar</rec>");   // findings 10/12
    }

    [Fact]
    public void Formula_MaxDepthBoundary_DeclaresTheXmlStringColumn()
    {
        // Findings 2/4: at MaxDepth=1 the flatten serializes /a/b as one large-text column a_b; the formula
        // must declare exactly that, not the deep leaf columns nor nothing.
        var columns = FormulaColumns(new XmlFlattenConfig { MaxDepth = 1 }, "<o><a><b>text</b></a></o>");
        Assert.Equal(new[] { "a_b" }, columns);
    }

    [Fact]
    public void Formula_FirstElement_RealizesOnlyTheChosenSiblingsFields()
    {
        // Finding 3: under first_element the formula must mirror the flatten (which flattens only elements[0]),
        // so a heterogeneous repeat yields only the first sibling's column.
        var columns = FormulaColumns(
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.FirstElement },
            "<r><line>plain</line><line><sku>X</sku></line></r>");
        Assert.Equal(new[] { "line" }, columns);
    }

    [Fact]
    public void Formula_WildcardExclude_DropsTheContainerSubtree()
    {
        // Finding 5: an exclude glob matching a container drops its whole subtree at load; the formula must too.
        var columns = FormulaColumns(new XmlFlattenConfig { ExcludePaths = ["/a*c"] },
            "<rec><abc><token>secret</token></abc><keep>1</keep></rec>");
        Assert.Equal(new[] { "keep" }, columns);
    }

    [Fact]
    public void Flatten_ColumnMappingOnRepeatingPath_AppliesAtLoad()
    {
        // Finding 6: a mapping written against the wildcard form the discover output prints must apply to the
        // concrete [N] indices the runtime produces.
        var config = new XmlFlattenConfig
        {
            ExplodePaths = ["/line"],
            ColumnMappings = new Dictionary<string, string> { ["/line[*]/sku"] = "mysku" },
        };

        var rows = FlattenRows("<order><line><sku>A</sku></line><line><sku>B</sku></line></order>", config);

        Assert.Equal(2, rows.Count);
        Assert.Equal("A", rows[0]["mysku"]);
        Assert.Equal("B", rows[1]["mysku"]);
        Assert.DoesNotContain("line_sku", rows[0].Keys);
        Assert.Equal(new[] { "mysku" }, FormulaColumns(config, "<order><line><sku>A</sku></line><line><sku>B</sku></line></order>"));
    }

    [Fact]
    public void Flatten_NonAliasedFieldSharingAliasTarget_NeverClobbersTheAliasedValue()
    {
        // Finding 7: /qty aliases onto 'amount'; a sibling empty <amount> must not overwrite the aliased value
        // with null. The coalesce is order-independent.
        var config = new XmlFlattenConfig { PathAliasColumns = XmlFlattenConfig.ParsePathAliases("amount=/qty") };

        var aliasFirst = Flatten("<row><id>1</id><qty>5</qty><amount></amount></row>", config);
        var realFirst = Flatten("<row><id>1</id><amount></amount><qty>5</qty></row>", config);

        Assert.Equal("5", aliasFirst["amount"]);
        Assert.Equal("5", realFirst["amount"]);
    }

    [Fact]
    public void Flatten_RecordRootText_IsEmittedRegardlessOfIncludeWhitelist()
    {
        // Findings 10/12: the record root's own text is always emitted (a scalar record is never empty), so the
        // formula must declare it even when an IncludePaths whitelist omits the root path.
        var row = Flatten("<rec>scalar</rec>", new XmlFlattenConfig { IncludePaths = ["/other"] });
        Assert.Equal("scalar", row["rec"]);
        Assert.Equal(new[] { "rec" }, FormulaColumns(new XmlFlattenConfig { IncludePaths = ["/other"] }, "<rec>scalar</rec>"));
    }

    [Fact]
    public void Explode_NameCollision_SchemaAndDataViewAgree_NoColumnSwap()
    {
        // Regression R1/R2 (critical): under explode the de-collision must be the SAME in the schema view (which
        // binds the load's columns) and the data view (the streamed rows). The element /line[*]/tag and the
        // attribute /line[*]/@tag must each stay in their own column for EVERY exploded row, even when the first
        // sibling omits the attribute the later siblings supply.
        var config = new XmlFlattenConfig { ExplodePaths = ["/line"] };
        const string xml = """<order><line><tag>E1</tag></line><line tag="AT2"><tag>E2</tag></line><line tag="AT3"><tag>E3</tag></line></order>""";

        var rows = FlattenRows(xml, config);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "E1", "E2", "E3" }, rows.Select(r => (string?)r.GetValueOrDefault("line_tag")).ToArray());
        Assert.Equal(new string?[] { null, "AT2", "AT3" }, rows.Select(r => (string?)r.GetValueOrDefault("line_tag_2")).ToArray());

        // The formula (schema view) is exactly the data view's column union - no phantom, no swap.
        var formula = FormulaColumns(config, xml).OrderBy(c => c, StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "line_tag", "line_tag_2" }, formula);
    }

    [Fact]
    public void Explode_AttributeThenElementCollision_DoesNotMergeOrPhantom()
    {
        // Regression R2 (high): schema-evolution shape where the legacy line carries price as an attribute and
        // the newer line as a child element. Both values must survive in stable, distinct columns.
        var config = new XmlFlattenConfig { ExplodePaths = ["/line"] };
        const string xml = """<order><line price="9.99"><sku>A</sku></line><line><sku>B</sku><price>12.50</price></line></order>""";

        var rows = FlattenRows(xml, config);

        Assert.Equal(2, rows.Count);
        // Attribute /line[*]/@price owns line_price (seen first); element /line[*]/price owns line_price_2.
        Assert.Equal("9.99", rows[0]["line_price"]);
        Assert.Null(rows[0].GetValueOrDefault("line_price_2"));
        Assert.Equal("12.50", rows[1]["line_price_2"]);
        Assert.Null(rows[1].GetValueOrDefault("line_price"));
    }

    [Fact]
    public void Aliasing_DeclaringTargetName_DoesNotCollapseDistinctRealFields()
    {
        // Regression R3 (high): declaring an alias whose TARGET name coincides with an ordinary column name must
        // not merge two distinct real fields. /tag and /Tag stay in two distinct columns with both values.
        var config = new XmlFlattenConfig { PathAliasColumns = XmlFlattenConfig.ParsePathAliases("tag=/label") };
        var row = Flatten("<r><tag>X</tag><Tag>Y</Tag></r>", config);

        Assert.Equal(2, row.Count);
        Assert.Equal(new[] { "X", "Y" }, row.Values.OrderBy(v => v, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void Aliasing_AliasValuePreserved_RegardlessOfDocumentOrder()
    {
        // Regression V2 (high): when a natural field shares the alias-target name, the alias-supplied value must
        // survive whether the natural field is visited before or after the alias source. Alias source owns the
        // union column; the natural field de-collides.
        var config = new XmlFlattenConfig { PathAliasColumns = XmlFlattenConfig.ParsePathAliases("email=/v2_email") };

        var naturalFirst = Flatten("<row><email>natural</email><v2_email>alias</v2_email></row>", config);
        var aliasFirst = Flatten("<row><v2_email>alias</v2_email><email>natural</email></row>", config);

        foreach (var row in new[] { naturalFirst, aliasFirst })
        {
            Assert.Equal("alias", row["email"]);
            Assert.Equal("natural", row["email_2"]);
        }
    }

    [Fact]
    public void Explode_SingleOccurrenceElement_StillExplodesIntoSameColumn()
    {
        // Regression V1 (high): a field whose element occurs once in some records and many times in others must
        // land in ONE column. A single occurrence of an explode-configured path still explodes (with a [0]
        // subscript), so it does not drift into a separate de-collided column or an un-exploded XML fragment.
        var config = new XmlFlattenConfig { ExplodePaths = ["/line", "/line[*]/tag"] };

        var twoLines = FlattenRows("<order><line><tag>a</tag><tag>b</tag></line><line><tag>c</tag></line></order>", config);
        Assert.Equal(new[] { "a", "b", "c" }, twoLines.Select(r => (string?)r.GetValueOrDefault("line_tag")).ToArray());
        Assert.All(twoLines, r => Assert.DoesNotContain("line_tag_2", r.Keys));

        // Single outer line, two inner tags: the configured inner explode still fires (2 rows, same column).
        var oneLine = FlattenRows("<order><line><tag>one</tag><tag>two</tag></line></order>", config);
        Assert.Equal(new[] { "one", "two" }, oneLine.Select(r => (string?)r.GetValueOrDefault("line_tag")).ToArray());
    }
}
