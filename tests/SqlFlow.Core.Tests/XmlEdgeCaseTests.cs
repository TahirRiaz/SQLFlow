using System.Text;
using System.Xml.Linq;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Xml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Additional edge-case coverage for the XML feature area (reader, flattener, config naming, introspection
/// formula), focused on cases the existing XmlSourceReaderTests / XmlFlattenerTests / XmlIntrospectionTests
/// suites do not already assert: CDATA and entity decoding, empty / self-closing elements, whitespace
/// trimming, attribute name collisions and Unicode identifiers, encoding declarations read from bytes,
/// namespace-prefix collisions, repeat-handling boundaries (single occurrence, count, skip, first / last),
/// xml-fragment serialization shape, MaxDepth boundaries, explode provenance, and scalar / attribute-only
/// records. All tests are pure in-memory (no database, no network) and deterministic.
/// </summary>
public sealed class XmlEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xmledge_" + Guid.NewGuid().ToString("N"));
    private readonly XmlSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public XmlEdgeCaseTests() => Directory.CreateDirectory(_dir);

    // --- helpers (uniquely named so they cannot collide with other files in the namespace) ---

    private static Dictionary<string, string?> EdgeFlatten(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).Flatten(XElement.Parse(xml))
            .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    private static List<Dictionary<string, string?>> EdgeFlattenRows(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).FlattenRows(XElement.Parse(xml))
            .Select(r => r.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase))
            .ToList();

    private static IReadOnlyList<XmlFlattenColumn> EdgeSchema(string xml, XmlFlattenConfig config)
        => new XmlPathFlattener(config).SchemaColumns(XElement.Parse(xml));

    private SourceSpec EdgeSource(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "xml", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private SourceSpec EdgeSourceBytes(string fileName, byte[] content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, content);
        return new SourceSpec { Type = "xml", Location = path, Options = options ?? new Dictionary<string, string?>() };
    }

    private async Task<(List<string> Columns, List<object?[]> Rows)> EdgeReadAllAsync(SourceSpec source)
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

    private static object? EdgeCell(List<string> columns, object?[] row, string name)
    {
        var idx = columns.IndexOf(name);
        return idx >= 0 ? row[idx] : null;
    }

    // --- CDATA and entity decoding ---

    [Fact]
    public void Cdata_LeafElement_DecodesToLiteralText()
    {
        // A CDATA section is the canonical way to carry markup-looking text; it must surface as the raw literal,
        // not the escaped form, and not be split.
        var row = EdgeFlatten("<o><note><![CDATA[a < b & c > d]]></note></o>", new XmlFlattenConfig());
        Assert.Equal("a < b & c > d", row["note"]);
    }

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&quot;", "\"")]
    [InlineData("&apos;", "'")]
    [InlineData("&#65;", "A")]
    [InlineData("&#x41;", "A")]
    public void Entities_InElementText_AreDecoded(string entity, string expected)
    {
        var row = EdgeFlatten($"<o><v>{entity}</v></o>", new XmlFlattenConfig());
        Assert.Equal(expected, row["v"]);
    }

    [Fact]
    public void Entities_InAttributeValue_AreDecoded()
    {
        var row = EdgeFlatten("""<o tag="a &amp; b"><x>1</x></o>""", new XmlFlattenConfig());
        Assert.Equal("a & b", row["tag"]);
    }

    [Fact]
    public void Cdata_InterleavedWithText_IsConcatenatedAsDirectText()
    {
        // Mixed content where CDATA sits between text runs and a child element: the element's own direct text
        // (plain runs plus the CDATA payload) concatenates; the child keeps its own column.
        var row = EdgeFlatten("<o><note>a<![CDATA[B]]>c<child>d</child>e</note></o>", new XmlFlattenConfig());
        Assert.Equal("aBce", row["note"]);
        Assert.Equal("d", row["note_child"]);
    }

    // --- empty / self-closing / whitespace ---

    [Theory]
    [InlineData("<o><a></a></o>")]   // explicit empty
    [InlineData("<o><a/></o>")]      // self-closing
    [InlineData("<o><a>   </a></o>")] // whitespace-only collapses to empty then null
    public void EmptyOrWhitespaceLeaf_BecomesNull(string xml)
    {
        var row = EdgeFlatten(xml, new XmlFlattenConfig());
        Assert.True(row.ContainsKey("a"));
        Assert.Null(row["a"]);
    }

    [Fact]
    public void LeafText_IsTrimmedOnBothSides()
    {
        var row = EdgeFlatten("<o><a>  hello  </a></o>", new XmlFlattenConfig());
        Assert.Equal("hello", row["a"]);
    }

    [Fact]
    public void SelfClosingElement_WithAttribute_KeepsAttributeAndNullText()
    {
        // A self-closing element that carries an attribute still emits its own (null) text column plus the
        // attribute column, so the attribute is never lost.
        var row = EdgeFlatten("""<o><a k="v"/></o>""", new XmlFlattenConfig());
        Assert.Equal("v", row["a_k"]);
        Assert.Null(row["a"]);
    }

    [Fact]
    public void EmptyRootRecord_EmitsSingleNullSelfNamedColumn()
    {
        // The record root is never empty: a fully empty element falls back to its own name as one null column.
        var row = EdgeFlatten("<o/>", new XmlFlattenConfig());
        var single = Assert.Single(row);
        Assert.Equal("o", single.Key);
        Assert.Null(single.Value);
    }

    [Fact]
    public void AttributeOnlyRecord_EmitsAttributeAndNullSelfText()
    {
        var row = EdgeFlatten("""<o id="7"/>""", new XmlFlattenConfig());
        Assert.Equal("7", row["id"]);
        Assert.True(row.ContainsKey("o"));
        Assert.Null(row["o"]);
    }

    // --- attribute naming, collisions, unicode identifiers ---

    [Theory]
    [InlineData("/@a-b", "a_b")]         // attribute marker stripped, hyphen sanitized to separator
    [InlineData("/a-b", "a_b")]          // hyphen in an element name
    [InlineData("/a/_x", "a_x")]         // interior leading-underscore segment is preserved
    [InlineData("/_x", "x")]             // a leading underscore on the whole name is trimmed away
    [InlineData("/a/b[0]/c", "a_b_c")]   // repetition subscripts are removed
    [InlineData("/résumé", "résumé")]    // Unicode letters are valid identifier characters and survive
    [InlineData("/@@@", "column")]       // a path that sanitizes to nothing falls back to a stable default
    public void ColumnName_EdgeCases_SanitizeAsExpected(string path, string expected)
    {
        Assert.Equal(expected, new XmlFlattenConfig().ColumnName(path));
    }

    [Fact]
    public void AttributeNameCollision_AfterSanitization_KeepsBothValuesDeColliding()
    {
        // Two distinct attributes whose names both sanitize to the same identifier must each keep their value in
        // a stable, distinct column rather than overwriting one another.
        var row = EdgeFlatten("""<o a-b="1" a_b="2"><x>9</x></o>""", new XmlFlattenConfig());
        Assert.Equal("1", row["a_b"]);
        Assert.Equal("2", row["a_b_2"]);
    }

    [Fact]
    public void UnicodeElementName_SurvivesAsColumn()
    {
        var row = EdgeFlatten("<o><naïve>x</naïve></o>", new XmlFlattenConfig());
        Assert.Equal("x", row["naïve"]);
    }

    [Fact]
    public void CustomSeparator_JoinsNestedNames()
    {
        var row = EdgeFlatten("<o><a><b>1</b></a></o>", new XmlFlattenConfig { Separator = "." });
        Assert.Equal("1", row["a.b"]);
    }

    [Fact]
    public void EmptySeparator_FallsBackToUnderscore()
    {
        // An empty separator is not a valid identifier joiner; the config must degrade to the underscore default.
        var row = EdgeFlatten("<o><a><b>1</b></a></o>", new XmlFlattenConfig { Separator = "" });
        Assert.Equal("1", row["a_b"]);
    }

    // --- repeat handling boundaries ---

    [Fact]
    public void SingleOccurrenceElement_WithCountHandling_KeepsValueNotCountOne()
    {
        // XML cannot distinguish a one-element repeat from a non-repeating element, so a lone element is flattened
        // in place (its value), not treated as a repeat and counted as "1".
        var row = EdgeFlatten("<o><item>A</item></o>", new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Count });
        Assert.Equal("A", row["item"]);
    }

    [Fact]
    public void RepeatingElements_Count_EmitsCardinality()
    {
        var row = EdgeFlatten("<o><x>1</x><x>2</x><x>3</x></o>", new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Count });
        Assert.Equal("3", row["x"]);
    }

    [Fact]
    public void RepeatingElements_Skip_DropsTheColumnEntirely()
    {
        var row = EdgeFlatten("<o><id>1</id><x>1</x><x>2</x></o>", new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Skip });
        Assert.True(row.ContainsKey("id"));
        Assert.False(row.ContainsKey("x"));
    }

    [Theory]
    [InlineData(XmlRepeatHandling.FirstElement, "1")]
    [InlineData(XmlRepeatHandling.LastElement, "2")]
    public void RepeatingScalars_FirstOrLast_PicksTheEndElement(XmlRepeatHandling handling, string expected)
    {
        var row = EdgeFlatten("<o><x>1</x><x>2</x></o>", new XmlFlattenConfig { RepeatHandling = handling });
        Assert.Equal(expected, row["x"]);
    }

    [Fact]
    public void RepeatingElements_Join_UsesDescendantText()
    {
        // Join concatenates each element's text (the trimmed descendant text) with the configured separator.
        var row = EdgeFlatten("<o><item><x>1</x></item><item><x>2</x></item></o>",
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Join, JoinSeparator = "|" });
        Assert.Equal("1|2", row["item"]);
    }

    [Fact]
    public void GlobalExplodeMode_ExplodesEveryRepeatWithoutExplicitPaths()
    {
        // RepeatHandling = Explode is the document-wide form of explodePaths: every repeating element multiplies.
        var rows = EdgeFlattenRows("<o><id>1</id><line><s>A</s></line><line><s>B</s></line></o>",
            new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Explode });
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => (string?)r.GetValueOrDefault("line_s")).ToArray());
        Assert.All(rows, r => Assert.Equal("1", r["id"]));
    }

    // --- xml-fragment serialization shape ---

    [Fact]
    public void RepeatingElements_DefaultToXml_KeepsAttributesInFragment()
    {
        var row = EdgeFlatten("""<o><item k="1">A</item><item k="2">B</item></o>""", new XmlFlattenConfig());
        Assert.Equal("""<item k="1">A</item><item k="2">B</item>""", row["item"]);
    }

    [Fact]
    public void XmlPath_KeepsNestedAttributesInFragment()
    {
        var row = EdgeFlatten("""<o><meta v="1"><a x="9">t</a></meta></o>""", new XmlFlattenConfig { XmlPaths = ["/meta"] });
        Assert.Equal("""<meta v="1"><a x="9">t</a></meta>""", row["meta"]);
    }

    [Fact]
    public void XmlString_RepeatColumn_IsFlaggedLargeText()
    {
        // The fragment column for a kept-as-xml repeat is large text; the schema view must declare that so the
        // load picks a large-text SQL type.
        var schema = EdgeSchema("<o><id>1</id><item>A</item><item>B</item></o>", new XmlFlattenConfig());
        var item = Assert.Single(schema, c => c.Name == "item");
        Assert.True(item.IsLargeText);
        var id = Assert.Single(schema, c => c.Name == "id");
        Assert.False(id.IsLargeText);
    }

    // --- MaxDepth boundaries ---

    [Fact]
    public void MaxDepthTwo_CollapsesThirdLevelToXmlFragment()
    {
        var row = EdgeFlatten("<o><a><b><c>1</c></b></a></o>", new XmlFlattenConfig { MaxDepth = 2 });
        Assert.Equal("<c>1</c>", row["a_b_c"]);
        Assert.False(row.ContainsKey("a_b_c_"));
    }

    [Fact]
    public void DefaultMaxDepth_CollapsesAtTheEleventhLevel()
    {
        // Default MaxDepth is 10: the eleventh nesting level is serialized as one XML-fragment column.
        var xml = "<o>" + Repeat("<a>", 12) + "x" + Repeat("</a>", 12) + "</o>";
        var row = EdgeFlatten(xml, new XmlFlattenConfig());
        var column = Assert.Single(row);
        Assert.Equal("a_a_a_a_a_a_a_a_a_a_a", column.Key);   // eleven 'a' segments (the collapse point)
        Assert.Equal("<a><a>x</a></a>", column.Value);       // the collapsed element plus its remaining descendants
    }

    // --- introspection formula parity for previously-unexercised configs ---

    [Fact]
    public void Formula_SkipHandling_OmitsTheRepeatColumn()
    {
        var formula = FormulaColumnNames(new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Skip },
            "<o><id>1</id><x>1</x><x>2</x></o>");
        Assert.Equal(new[] { "id" }, formula);
    }

    [Fact]
    public void Formula_CountHandling_DeclaresTheRepeatAsScalar()
    {
        var formula = FormulaColumnNames(new XmlFlattenConfig { RepeatHandling = XmlRepeatHandling.Count },
            "<o><x>1</x><x>2</x></o>");
        Assert.Equal(new[] { "x" }, formula);
    }

    [Fact]
    public void Formula_EmptyRecordRoot_DeclaresTheSelfNamedColumn()
    {
        Assert.Equal(new[] { "o" }, FormulaColumnNames(new XmlFlattenConfig(), "<o/>"));
    }

    // --- reader-level edge cases over real temp files ---

    [Theory]
    [InlineData("")]                 // truly empty file
    [InlineData("<root></root>")]    // a root with no child records
    [InlineData("<root/>")]          // a self-closing root with no child records
    public async Task NoRecords_YieldsZeroDataRows(string content)
    {
        var (_, rows) = await EdgeReadAllAsync(EdgeSource("none.xml", content));
        Assert.Empty(rows);
    }

    [Fact]
    public async Task Comments_AndInsignificantWhitespace_AreIgnoredByTheReader()
    {
        var source = EdgeSource("doc.xml", "<rows>\n  <!-- a comment -->\n  <row>\n    <id>1</id>\n  </row>\n</rows>");
        var (columns, rows) = await EdgeReadAllAsync(source);
        var row = Assert.Single(rows);
        Assert.Equal("1", EdgeCell(columns, row, "id"));
    }

    [Fact]
    public async Task Utf16EncodingDeclarationWithBom_IsHonoredFromBytes()
    {
        // The reader consumes raw bytes; an explicit UTF-16 declaration plus BOM must decode the accented text
        // correctly rather than mojibake.
        var encoding = Encoding.Unicode;
        const string xml = "<?xml version=\"1.0\" encoding=\"utf-16\"?><rows><row><name>Café</name></row></rows>";
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(xml)).ToArray();

        var (columns, rows) = await EdgeReadAllAsync(EdgeSourceBytes("u16.xml", bytes));
        Assert.Equal("Café", EdgeCell(columns, Assert.Single(rows), "name"));
    }

    [Fact]
    public async Task Utf8AccentedText_WithoutDeclaration_IsDecoded()
    {
        var bytes = Encoding.UTF8.GetBytes("<rows><row><name>naïve</name></row></rows>");
        var (columns, rows) = await EdgeReadAllAsync(EdgeSourceBytes("u8.xml", bytes));
        Assert.Equal("naïve", EdgeCell(columns, Assert.Single(rows), "name"));
    }

    [Fact]
    public async Task NamespacePrefixCollision_KeepsTheFirstAttributeValue()
    {
        // Two attributes share a local name across different namespaces; after namespace stripping only one
        // 'k' column can exist, and the first-declared attribute wins (deterministic, no exception).
        var source = EdgeSource("nscoll.xml",
            """<root xmlns:a="urn:a" xmlns:b="urn:b"><item a:k="first" b:k="second">x</item></root>""");
        var (columns, rows) = await EdgeReadAllAsync(source);
        Assert.Equal("first", EdgeCell(columns, Assert.Single(rows), "k"));
    }

    [Fact]
    public async Task DefaultNamespace_OnNestedElements_IsStripped()
    {
        // A default namespace declared on the root applies to all descendants; stripping must yield clean local
        // names for nested elements too, not prefixed or namespaced ones.
        var source = EdgeSource("defns.xml",
            """<orders xmlns="urn:shop"><order><id>1</id><customer><name>Ann</name></customer></order></orders>""");
        var (columns, rows) = await EdgeReadAllAsync(source);
        var row = Assert.Single(rows);
        Assert.Equal("1", EdgeCell(columns, row, "id"));
        Assert.Equal("Ann", EdgeCell(columns, row, "customer_name"));
    }

    [Fact]
    public async Task Explode_RowNumberProvenance_IncrementsPerOutputRow()
    {
        // Each exploded output row (not each source record) gets the next RowNumber_DW, so downstream keying is
        // per emitted row.
        var source = EdgeSource("exp.xml",
            "<orders><order><id>1</id><line><s>A</s></line><line><s>B</s></line></order></orders>",
            new Dictionary<string, string?> { ["explodePaths"] = "/line" });

        var (columns, rows) = await EdgeReadAllAsync(source);
        Assert.Equal(2, rows.Count);
        Assert.Equal(1L, EdgeCell(columns, rows[0], "RowNumber_DW"));
        Assert.Equal(2L, EdgeCell(columns, rows[1], "RowNumber_DW"));
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => (string?)EdgeCell(columns, r, "line_s")).ToArray());
    }

    [Fact]
    public async Task NamespacedRowXPath_OverStrippedDocument_SelectsByLocalName()
    {
        // rowXPath is evaluated against the namespace-stripped tree, so a plain local-name path selects records
        // even though the source used a default namespace.
        var source = EdgeSource("nsrows.xml",
            """<feed xmlns="urn:x"><records><rec><id>1</id></rec><rec><id>2</id></rec></records></feed>""",
            new Dictionary<string, string?> { ["rowXPath"] = "/feed/records/rec" });

        var (columns, rows) = await EdgeReadAllAsync(source);
        Assert.Equal(2, rows.Count);
        Assert.Equal("2", EdgeCell(columns, rows[1], "id"));
    }

    [Fact]
    public async Task IncludeAttributesFalse_DropsAttributeColumns()
    {
        var source = EdgeSource("noattr.xml", """<rows><row id="9"><name>Ann</name></row></rows>""",
            new Dictionary<string, string?> { ["includeAttributes"] = "false" });

        var (columns, _) = await EdgeReadAllAsync(source);
        Assert.Contains("name", columns);
        Assert.DoesNotContain("id", columns);
    }

    [Fact]
    public async Task InvalidMaxDepth_FailsClearly()
    {
        // maxDepth must be a positive integer; zero (or negative) is rejected before any flatten runs.
        var source = EdgeSource("md.xml", "<rows><row><id>1</id></row></rows>",
            new Dictionary<string, string?> { ["maxDepth"] = "0" });

        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("maxDepth", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("first_elem")]
    public void ParseRepeatHandling_UnknownValue_ThrowsWithGuidance(string value)
    {
        var ex = Assert.Throws<SqlFlowException>(() => XmlFlattenConfig.ParseRepeatHandling(value));
        Assert.Contains("repeatHandling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("to_xml", XmlRepeatHandling.ToXml)]
    [InlineData("TOXML", XmlRepeatHandling.ToXml)]
    [InlineData("first", XmlRepeatHandling.FirstElement)]
    [InlineData("first-element", XmlRepeatHandling.FirstElement)]
    [InlineData("LAST", XmlRepeatHandling.LastElement)]
    [InlineData("join", XmlRepeatHandling.Join)]
    [InlineData("count", XmlRepeatHandling.Count)]
    [InlineData("skip", XmlRepeatHandling.Skip)]
    [InlineData("unnest", XmlRepeatHandling.Explode)]
    [InlineData(null, XmlRepeatHandling.ToXml)]
    public void ParseRepeatHandling_KnownValues_AreCaseAndSeparatorInsensitive(string? value, XmlRepeatHandling expected)
    {
        Assert.Equal(expected, XmlFlattenConfig.ParseRepeatHandling(value));
    }

    [Fact]
    public void ParsePathAliases_MissingPath_Throws()
    {
        var ex = Assert.Throws<SqlFlowException>(() => XmlFlattenConfig.ParsePathAliases("col="));
        Assert.Contains("pathAliases", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/a/b[0]/c", "/a/b[*]/c")]      // concrete index folds to wildcard
    [InlineData("/a/b[*]/c", "/a/b[*]/c")]      // already-wildcard is unchanged
    [InlineData("/a/b[7]/c[2]", "/a/b[*]/c[*]")] // every subscript on the path folds
    [InlineData("/a/b/c", "/a/b/c")]            // no subscripts: unchanged
    [InlineData("/a/b[xy]/c", "/a/b[xy]/c")]    // a non-numeric subscript is left intact
    public void NormalizeIndices_FoldsNumericAndStarSubscriptsToWildcard(string path, string expected)
    {
        Assert.Equal(expected, XmlFlattenConfig.NormalizeIndices(path));
    }

    private static List<string> FormulaColumnNames(XmlFlattenConfig config, string xml)
    {
        var flattener = new XmlPathFlattener(config);
        var perRecord = new[] { flattener.SchemaColumns(XElement.Parse(xml)) };
        return XmlFlattenFormulaBuilder.FromRecords(perRecord).Columns.Select(c => c.Name).ToList();
    }

    private static string Repeat(string token, int times)
    {
        var sb = new StringBuilder(token.Length * times);
        for (var i = 0; i < times; i++)
        {
            sb.Append(token);
        }

        return sb.ToString();
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
