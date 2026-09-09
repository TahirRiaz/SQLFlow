using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using SqlFlow.Sources;
using SqlFlow.Sources.Json;
using SqlFlow.Sources.Xml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Degenerate and adversarial edge cases for record-anchor detection and the discovery surface it drives:
/// empty and empty-object arrays, the first-sample gate under multi-sampling, namespaced XML (the case that
/// motivates detection - real RSS/Atom/SOAP feeds), CDATA and mixed content, NDJSON comment/blank lines, BOM,
/// unreadable and malformed input, and the stripNamespaces interaction. These pin the boundaries that the
/// happy-path suite does not.
/// </summary>
public sealed class RecordAnchorEdgeCaseTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_anchoredge_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSourceReader _json = new(new LocalFileLifecycle(), [new LocalFileStore()]);
    private readonly XmlSourceReader _xml = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public RecordAnchorEdgeCaseTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Source(string fileName, string content, Encoding? encoding = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content, encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var type = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return new SourceSpec { Type = type, Location = path, Options = new Dictionary<string, string?>() };
    }

    private static string? JsonAnchor(int maxDepth, params string[] documents)
    {
        var parsed = documents.Select(d => JsonDocument.Parse(d)).ToList();
        try
        {
            return JsonRecordAnchorDetector.Detect(parsed.Select(d => d.RootElement), maxDepth);
        }
        finally
        {
            foreach (var doc in parsed)
            {
                doc.Dispose();
            }
        }
    }

    // --- JSON degenerate shapes --------------------------------------------

    [Fact]
    public void Json_EmptyArray_IsNotARowGrain()
    {
        // No elements means no records: objects == 0, so it never qualifies.
        Assert.Null(JsonAnchor(10, """{ "items": [] }"""));
    }

    [Fact]
    public void Json_ArrayOfEmptyObjects_IsStillARecordGrain()
    {
        // Empty objects are still records (zero-column rows); the array is the grain even with no fields.
        Assert.Equal("$.items", JsonAnchor(10, """{ "meta": 1, "items": [ {}, {}, {} ] }"""));
    }

    [Fact]
    public void Json_ArrayOfNulls_IsNotARowGrain()
    {
        // Null elements are not objects; the array is not a record collection.
        Assert.Null(JsonAnchor(10, """{ "items": [ null, null ] }"""));
    }

    [Fact]
    public void Json_ScalarRoot_ReturnsNull()
    {
        Assert.Null(JsonAnchor(10, "42"));
        Assert.Null(JsonAnchor(10, "\"a string\""));
        Assert.Null(JsonAnchor(10, "true"));
        Assert.Null(JsonAnchor(10, "null"));
    }

    [Fact]
    public void Json_FirstSampleNonObject_GatesDetectionOff()
    {
        // The gate keys on the first sample: a root array first means the caller already handles the grain, even
        // if later samples are envelopes. Detection stays out rather than guessing across mismatched roots.
        Assert.Null(JsonAnchor(10, """[ {"id": 1} ]""", """{ "items": [ {"id": 2}, {"id": 3} ] }"""));
    }

    [Fact]
    public void Json_NestedArraysBeyondDepthIgnored_ShallowOneStillWins()
    {
        // A shallow qualifying array is found; a deeper one beyond maxDepth is invisible and cannot outrank it.
        const string doc = """{ "items": [ {"id": 1}, {"id": 2} ], "deep": { "x": { "y": { "rows": [ {"z": 1}, {"z": 2}, {"z": 3} ] } } } }""";
        Assert.Equal("$.items", JsonAnchor(2, doc));
    }

    // --- JSON reader surface: NDJSON, BOM, malformed -----------------------

    [Fact]
    public async Task Json_Ndjson_SkipsBlankAndCommentLines_DuringDetection()
    {
        var source = Source("s.ndjson",
            "# a header comment\n" +
            "\n" +
            "{ \"items\": [ {\"id\": 1}, {\"id\": 2} ] }\n" +
            "   \n" +
            "{ \"items\": [ {\"id\": 3} ] }\n");

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal(3, introspection.Inventory.RecordsScanned);
    }

    [Fact]
    public async Task Json_Utf8Bom_EnvelopeIsStillDetected()
    {
        var source = Source("bom.json",
            """{ "items": [ {"id": 1}, {"id": 2} ] }""",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.RecordsScanned);
    }

    [Fact]
    public void Json_UnicodeKeys_AreCarriedIntoTheDetectedPath()
    {
        // A non-ASCII envelope key must survive verbatim into the emitted JSONPath.
        Assert.Equal("$.données", JsonAnchor(10, """{ "total": 2, "données": [ {"id": 1}, {"id": 2} ] }"""));
    }

    [Fact]
    public async Task Json_MalformedDocument_SurfacesDuringDiscovery()
    {
        var source = Source("bad.json", "{ this is not json");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10));
    }

    [Fact]
    public async Task Json_EmptyFile_YieldsNoRecordsAndNoGrain()
    {
        var source = Source("empty.json", string.Empty);

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);
        Assert.Equal(0, introspection.Inventory.RecordsScanned);
    }

    // --- XML namespaces, CDATA, mixed content ------------------------------

    [Fact]
    public void Xml_NamespacedAtomFeed_DetectsLocalNameGrain()
    {
        // Namespaces are stripped by default, so detection works on local names: an Atom feed's <entry> repeats.
        const string doc = """
        <feed xmlns="http://www.w3.org/2005/Atom">
            <title>Example</title>
            <entry><id>1</id><title>A</title></entry>
            <entry><id>2</id><title>B</title></entry>
        </feed>
        """;
        var stripped = XmlRecordReader.ReadDocumentRoot(Encoding.UTF8.GetBytes(doc), stripNamespaces: true, "feed.xml");
        Assert.NotNull(stripped);
        Assert.Equal("/feed/entry", XmlRecordAnchorDetector.Detect([stripped!]));
    }

    [Fact]
    public async Task Xml_NamespacedFeed_DiscoversAtEntryGrain_AndSelectsRecordsAtLoad()
    {
        // The full round trip: the detected /feed/entry must actually select the two entries when the reader
        // applies it (against the namespace-stripped working document), yielding two records with an id column.
        var source = Source("atom.xml",
            """
            <feed xmlns="http://www.w3.org/2005/Atom" xmlns:x="urn:x">
                <title>Example</title>
                <entry><id>1</id><x:extra>p</x:extra></entry>
                <entry><id>2</id><x:extra>q</x:extra></entry>
            </feed>
            """);

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("/feed/entry", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.RecordsScanned);
        Assert.Contains(introspection.Formula.Columns, c => c.Name == "id");
    }

    [Fact]
    public void Xml_SoapEnvelope_PicksTheRepeatingBodyRow()
    {
        // A SOAP-style envelope wraps the rows two levels deep; the repeater under the body is the grain.
        const string doc = """
        <Envelope xmlns="http://schemas.xmlsoap.org/soap/envelope/">
            <Body>
                <Row><k>1</k></Row>
                <Row><k>2</k></Row>
                <Row><k>3</k></Row>
            </Body>
        </Envelope>
        """;
        var stripped = XmlRecordReader.ReadDocumentRoot(Encoding.UTF8.GetBytes(doc), stripNamespaces: true, "soap.xml");
        Assert.Equal("/Envelope/Body/Row", XmlRecordAnchorDetector.Detect([stripped!]));
    }

    [Fact]
    public async Task Xml_CdataAndMixedContentRecords_AreDiscoveredAtItemGrain()
    {
        var source = Source("cdata.xml",
            """
            <catalog>
                <book><title><![CDATA[A & B]]></title></book>
                <book><title>plain</title></book>
            </catalog>
            """);

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("/catalog/book", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.RecordsScanned);
    }

    [Fact]
    public async Task Xml_MalformedDocument_SurfacesDuringDiscovery()
    {
        var source = Source("bad.xml", "<open><unclosed></open>");

        await Assert.ThrowsAsync<SqlFlowException>(
            () => _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10));
    }

    [Fact]
    public async Task Xml_EmptyFile_YieldsNoRecordsAndNoGrain()
    {
        var source = Source("empty.xml", string.Empty);

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);
        Assert.Equal(0, introspection.Inventory.RecordsScanned);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
