using System.Text;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Reader-level tests for <see cref="XmlSourceReader"/> over real temp files (no database): row selection
/// (default = the root's child elements), explicit rowXPath, namespace stripping, schema evolution across
/// files, explode with per-output-row provenance, and the provenance columns.
/// </summary>
public sealed class XmlSourceReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_xml_" + Guid.NewGuid().ToString("N"));
    private readonly XmlSourceReader _reader = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public XmlSourceReaderTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Xml(string fileName, string content, Dictionary<string, string?>? options = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        return new SourceSpec { Type = "xml", Location = path, Options = options ?? new Dictionary<string, string?>() };
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
    public async Task DefaultRowSelection_EachRootChildIsARecord()
    {
        var source = Xml("orders.xml",
            "<orders><order><id>1</id><customer>Ann</customer></order><order><id>2</id><customer>Bob</customer></order></orders>");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(new[] { "id", "customer" }, columns.Take(2).ToArray());
        Assert.Contains("FileName_DW", columns);
        Assert.Contains("RowNumber_DW", columns);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Bob", Cell(columns, rows[1], "customer"));
        Assert.Equal(1L, Cell(columns, rows[0], "RowNumber_DW"));
        Assert.Equal(2L, Cell(columns, rows[1], "RowNumber_DW"));
    }

    [Fact]
    public async Task RowXPathDot_TreatsRootAsSingleRecord()
    {
        var source = Xml("one.xml", "<order><id>1</id><total>9.5</total></order>",
            new Dictionary<string, string?> { ["rowXPath"] = "." });

        var (columns, rows) = await ReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Equal("1", Cell(columns, row, "id"));
        Assert.Equal("9.5", Cell(columns, row, "total"));
    }

    [Fact]
    public async Task ExplicitRowXPath_SelectsNestedRows()
    {
        var source = Xml("env.xml",
            "<feed><meta><v>1</v></meta><records><rec><id>1</id></rec><rec><id>2</id></rec></records></feed>",
            new Dictionary<string, string?> { ["rowXPath"] = "/feed/records/rec" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1", Cell(columns, rows[0], "id"));
    }

    [Fact]
    public async Task Namespaces_AreStrippedByDefault()
    {
        var source = Xml("ns.xml",
            """<root xmlns="http://example.com/x" xmlns:a="http://example.com/a"><item a:kind="big"><name>A</name></item></root>""");

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("name", columns);
        Assert.Contains("kind", columns);   // a:kind attribute -> kind
        Assert.Equal("A", Cell(columns, Assert.Single(rows), "name"));
    }

    [Fact]
    public async Task SchemaEvolution_UnionsColumnsAcrossFiles()
    {
        Xml("v1.xml", "<rows><row><id>1</id><name>Ann</name></row></rows>");
        Xml("v2.xml", "<rows><row><id>2</id><email>b@x.io</email></row></rows>");
        var source = new SourceSpec
        {
            Type = "xml",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "*.xml" },
        };

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Contains("name", columns);
        Assert.Contains("email", columns);
        Assert.Equal(2, rows.Count);
        var v1 = rows.Single(r => Equals(Cell(columns, r, "id"), "1"));
        Assert.Null(Cell(columns, v1, "email"));
    }

    [Fact]
    public async Task Explode_OneRowPerRepeatingElement()
    {
        var source = Xml("orders.xml",
            "<orders><order><id>1</id><line><sku>A</sku></line><line><sku>B</sku></line></order></orders>",
            new Dictionary<string, string?> { ["explodePaths"] = "/line" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Contains("line_sku", columns);
        Assert.Equal(new[] { "A", "B" }, rows.Select(r => (string?)Cell(columns, r, "line_sku")).ToArray());
        Assert.All(rows, r => Assert.Equal("1", Cell(columns, r, "id")));
    }

    [Fact]
    public async Task InvalidXml_FailsClearly()
    {
        var source = Xml("bad.xml", "<order><id>1</id>");   // unclosed
        await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
    }

    [Fact]
    public async Task AttributeVsElementCollision_KeepsBothColumnsLossless()
    {
        // Finding 1 end-to-end: an attribute and a same-named element survive as two columns through the load.
        var source = Xml("collide.xml", """<rows><row id="A"><id>B</id></row></rows>""");

        var (columns, rows) = await ReadAllAsync(source);

        var row = Assert.Single(rows);
        Assert.Contains("id", columns);
        Assert.Contains("id_2", columns);
        Assert.Equal("A", Cell(columns, row, "id"));
        Assert.Equal("B", Cell(columns, row, "id_2"));
    }

    [Fact]
    public async Task DataColumnNamedLikeProvenance_FailsClearly()
    {
        // Finding 11: a source field named like an enabled provenance column would be silently overwritten by
        // the generated value; the load must reject the ambiguity instead.
        var source = Xml("prov.xml", "<rows><row><id>1</id><RowNumber_DW>999</RowNumber_DW></row></rows>");

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("RowNumber_DW", ex.Message);
    }

    [Fact]
    public async Task DeeplyNestedXml_IsRejectedClearly_NotStackOverflow()
    {
        // Finding 9 (security): a small but pathologically deep document must surface a clear error, not crash
        // the process with an uncatchable StackOverflowException.
        var sb = new StringBuilder("<rows><row>");
        for (var i = 0; i < 5000; i++)
        {
            sb.Append("<a>");
        }

        sb.Append('x');
        for (var i = 0; i < 5000; i++)
        {
            sb.Append("</a>");
        }

        sb.Append("</row></rows>");
        var source = Xml("deep.xml", sb.ToString());

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("nests", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RowXPath_NonElementResult_FailsClearly()
    {
        // Finding 13: a rowXPath that evaluates to a number/attribute/string (not an element node-set) must
        // surface a clear SqlFlowException, not a raw InvalidOperationException.
        var source = Xml("nonelem.xml", "<rows><row id='1'/><row id='2'/></rows>",
            new Dictionary<string, string?> { ["rowXPath"] = "count(/rows/row)" });

        var ex = await Assert.ThrowsAsync<SqlFlow.Core.SqlFlowException>(() => _reader.GetColumnsAsync(source));
        Assert.Contains("rowXPath", ex.Message);
    }

    [Fact]
    public async Task ExplodeWithNameCollision_SchemaAndDataAgreeEndToEnd()
    {
        // Regression R1/R2 end-to-end: through GetColumnsAsync (schema, binds columns) + streaming (data), an
        // attribute/element collision under explode keeps each value in its own column for every row.
        var source = Xml("exc.xml",
            """<orders><order><line><tag>E1</tag></line><line tag="AT2"><tag>E2</tag></line></order></orders>""",
            new Dictionary<string, string?> { ["explodePaths"] = "/line" });

        var (columns, rows) = await ReadAllAsync(source);

        Assert.Equal(2, rows.Count);
        Assert.Contains("line_tag", columns);
        Assert.Contains("line_tag_2", columns);
        Assert.Equal("E1", Cell(columns, rows[0], "line_tag"));   // element value
        Assert.Equal("E2", Cell(columns, rows[1], "line_tag"));   // element value, not the attribute
        Assert.Null(Cell(columns, rows[0], "line_tag_2"));
        Assert.Equal("AT2", Cell(columns, rows[1], "line_tag_2"));
    }

    [Fact]
    public async Task DisabledProvenanceColumn_SourceFieldPreservedAsString()
    {
        // Regression R4 (high): with the provenance column disabled, a source field that shares its name is a
        // normal string column - its value must not be overwritten by a generated ordinal, nor re-typed to long.
        var source = Xml("dis.xml", "<rows><row><id>1</id><RowNumber_DW>not-a-number</RowNumber_DW></row></rows>",
            new Dictionary<string, string?> { ["includeRowNumber"] = "false" });

        var columns = await _reader.GetColumnsAsync(source);
        Assert.Equal(typeof(string), columns.Single(c => c.Name == "RowNumber_DW").Type);

        var (cols, rows) = await ReadAllAsync(source);
        Assert.Equal("not-a-number", Cell(cols, Assert.Single(rows), "RowNumber_DW"));
    }

    [Fact]
    public async Task ReadAhead_DefaultsToOneFileAtATime_BecauseXmlHoldsAWholeDocument()
    {
        // The XML reader parses a whole document to read it, so every extra open file multiplies the peak.
        // Its default must stay at one file at a time; a flow that knows its files are small can raise it.
        Assert.Equal(FileSourceOptions.WholeFileDefaultReadAhead, await MaxConcurrentOpensAsync(readAhead: null));
        Assert.Equal(3, await MaxConcurrentOpensAsync(readAhead: 3));
    }

    private static async Task<int> MaxConcurrentOpensAsync(int? readAhead)
    {
        var store = new ConcurrencyTrackingFileStore(6, "xml", name => $"<rows><row><id>{name}</id></row></rows>");
        var reader = new XmlSourceReader(new LocalFileLifecycle(), [store]);
        var options = new Dictionary<string, string?> { ["srcFile"] = "*.xml" };
        if (readAhead is { } depth)
        {
            options["readAhead"] = depth.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var source = new SourceSpec { Type = "xml", Location = ConcurrencyTrackingFileStore.Root, Options = options };

        var columns = await reader.GetColumnsAsync(source);
        await using var data = (await reader.OpenAsync(source, columns)).Reader;
        while (await data.ReadAsync())
        {
            // Drain: the peak covers the schema pass and the data pass alike.
        }

        return store.MaxConcurrentOpens;
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
