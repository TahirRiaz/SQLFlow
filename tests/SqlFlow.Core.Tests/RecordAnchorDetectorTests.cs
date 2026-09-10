using System.Text.Json;
using System.Xml.Linq;
using SqlFlow.Sources.Json;
using SqlFlow.Sources.Xml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Unit tests for the statistics-driven record-anchor detectors that give <c>discover</c> its correct row
/// grain: the JSON detector finds the envelope's dominant array of records, the XML detector finds the
/// outermost repeating element. Both decline (return null) when the document genuinely is one row, an explicit
/// grain would win instead. Ported from the delta-forge record_anchor suite so the two implementations agree.
/// </summary>
public sealed class RecordAnchorDetectorTests
{
    // --- JSON ---------------------------------------------------------------

    private static string? JsonAnchor(params string[] documents)
    {
        var parsed = documents.Select(d => JsonDocument.Parse(d)).ToList();
        try
        {
            return JsonRecordAnchorDetector.Detect(parsed.Select(d => d.RootElement), maxDepth: 10);
        }
        finally
        {
            foreach (var doc in parsed)
            {
                doc.Dispose();
            }
        }
    }

    [Fact]
    public void Json_GithubSearchEnvelope_PicksItems()
    {
        const string doc = """
        {
            "total_count": 867,
            "incomplete_results": false,
            "items": [
                {"id": 1, "full_name": "apache/doris"},
                {"id": 2, "full_name": "delta-io/delta"},
                {"id": 3, "full_name": "apache/iceberg"}
            ]
        }
        """;
        Assert.Equal("$.items", JsonAnchor(doc));
    }

    [Fact]
    public void Json_SingleResultScalarEnvelope_StillPicksItems()
    {
        // One result, but the siblings are pure scalar metadata: the envelope signature justifies descending.
        const string doc = """
        { "total_count": 1, "incomplete_results": false, "items": [ {"id": 1, "full_name": "only/one"} ] }
        """;
        Assert.Equal("$.items", JsonAnchor(doc));
    }

    [Fact]
    public void Json_DeeplyNestedResultsArray_IsFound()
    {
        const string doc = """{ "meta": {"page": 1}, "data": { "results": [ {"id": 1}, {"id": 2}, {"id": 3} ] } }""";
        Assert.Equal("$.data.results", JsonAnchor(doc));
    }

    [Fact]
    public void Json_PlainRecordObject_StaysAtRoot()
    {
        Assert.Null(JsonAnchor("""{"id": 1, "name": "Alice", "address": {"city": "Oslo"}}"""));
    }

    [Fact]
    public void Json_RootArray_ReturnsNullForCallerToHandle()
    {
        Assert.Null(JsonAnchor("""[{"id": 1}, {"id": 2}]"""));
    }

    [Fact]
    public void Json_ArrayOfScalars_IsNeverARowGrain()
    {
        Assert.Null(JsonAnchor("""{"name": "repo", "topics": ["delta", "lakehouse", "rust"]}"""));
    }

    [Fact]
    public void Json_DominantArrayBeatsIncidentalOne()
    {
        const string doc = """{ "audit": [ {"by": "system"} ], "records": [ {"id": 1}, {"id": 2}, {"id": 3} ] }""";
        Assert.Equal("$.records", JsonAnchor(doc));
    }

    [Fact]
    public void Json_SingleElementArrayWithObjectSibling_StaysAtRoot()
    {
        // total == 1 and a sibling object means this is not a clean scalar envelope, so do not descend.
        Assert.Null(JsonAnchor("""{ "config": {"mode": "fast"}, "history": [ {"ts": 1} ] }"""));
    }

    [Fact]
    public void Json_ShallowerArrayWinsOnEqualCounts()
    {
        const string doc = """{ "items": [ {"a": 1}, {"a": 2}, {"a": 3} ], "nested": { "rows": [ {"b": 1}, {"b": 2}, {"b": 3} ] } }""";
        Assert.Equal("$.items", JsonAnchor(doc));
    }

    [Fact]
    public void Json_MultiSampleCountsUnionAcrossPages()
    {
        // Two pages, each one object with a single-element items array. Unioned, items totals 2 and dominates.
        Assert.Equal("$.items", JsonAnchor("""{"page": 1, "items": [ {"id": 1} ]}""", """{"page": 2, "items": [ {"id": 2} ]}"""));
    }

    [Fact]
    public void Json_EmptySamples_ReturnsNull()
    {
        Assert.Null(JsonRecordAnchorDetector.Detect([], maxDepth: 10));
    }

    [Fact]
    public void Json_ObjectRatioAtExactlyHalf_QualifiesAsCandidate()
    {
        // Two elements, one object one scalar: objects*2 (2) >= total (2), so the array still qualifies and,
        // being a 2+-element array, wins outright.
        Assert.Equal("$.mixed", JsonAnchor("""{ "mixed": [ {"id": 1}, "scalar" ] }"""));
    }

    [Fact]
    public void Json_ObjectRatioBelowHalf_DoesNotQualify()
    {
        // One object among three elements: objects*2 (2) < total (3). Not a record collection.
        Assert.Null(JsonAnchor("""{ "mostly_scalars": [ {"id": 1}, "a", "b" ] }"""));
    }

    [Fact]
    public void Json_HomogeneousArrayWins_OnEqualCountAndDepth()
    {
        // Both arrays hold two objects at depth 1. `clean` is homogeneous (one shape); `messy` has two shapes.
        // Fewer distinct shapes wins the tie.
        const string doc = """
        {
            "messy": [ {"a": 1}, {"b": 2} ],
            "clean": [ {"x": 1}, {"x": 2} ]
        }
        """;
        Assert.Equal("$.clean", JsonAnchor(doc));
    }

    [Fact]
    public void Json_RicherRecordWins_OnEqualCountDepthAndHomogeneity()
    {
        // Equal counts, equal depth, both homogeneous: the array whose records carry more fields wins.
        const string doc = """
        {
            "thin": [ {"a": 1}, {"a": 2} ],
            "wide": [ {"a": 1, "b": 2, "c": 3}, {"a": 4, "b": 5, "c": 6} ]
        }
        """;
        Assert.Equal("$.wide", JsonAnchor(doc));
    }

    [Fact]
    public void Json_ArrayBeyondMaxDepth_IsNotDetected()
    {
        // The records sit at depth 3; a maxDepth of 1 cannot see them, so the root grain stands.
        const string doc = """{ "a": { "b": { "items": [ {"id": 1}, {"id": 2} ] } } }""";
        var parsed = JsonDocument.Parse(doc);
        try
        {
            Assert.Null(JsonRecordAnchorDetector.Detect([parsed.RootElement], maxDepth: 1));
            Assert.Equal("$.a.b.items", JsonRecordAnchorDetector.Detect([parsed.RootElement], maxDepth: 10));
        }
        finally
        {
            parsed.Dispose();
        }
    }

    [Fact]
    public void Json_NestedRecordArray_NamedByTransparentArrayConvention_WhenItOutRowsTheOuter()
    {
        // The dominant records live one array-hop deeper, under an element of an outer array. Element children
        // are named at the array's own path (no [idx] segment): $.groups.members. Here the inner array (5) out-
        // rows the outer groups (2), so the deeper, more numerous grain wins despite being one level down.
        const string doc = """
        {
            "groups": [
                { "members": [ {"id": 1}, {"id": 2}, {"id": 3}, {"id": 4}, {"id": 5} ] },
                { "members": [ {"id": 6} ] }
            ]
        }
        """;
        Assert.Equal("$.groups.members", JsonAnchor(doc));
    }

    [Fact]
    public void Json_ShallowerOuterArrayWins_WhenNestedDoesNotOutRowIt()
    {
        // Mirror of the above: when the nested array does not out-row the outer one, the shallower outer array
        // is the grain (one row per group, members kept as a nested value). Deterministic shallowest-wins tie.
        const string doc = """
        {
            "groups": [
                { "members": [ {"id": 1}, {"id": 2} ] },
                { "members": [ {"id": 3}, {"id": 4} ] }
            ]
        }
        """;
        Assert.Equal("$.groups", JsonAnchor(doc));
    }

    // --- XML ----------------------------------------------------------------

    private static string? XmlAnchor(params string[] documents)
        => XmlRecordAnchorDetector.Detect(documents.Select(XElement.Parse));

    [Fact]
    public void Xml_RssEnvelope_PicksNestedItem()
    {
        const string doc = """
        <rss>
            <channel>
                <title>Feed</title>
                <item><guid>1</guid><title>A</title></item>
                <item><guid>2</guid><title>B</title></item>
                <item><guid>3</guid><title>C</title></item>
            </channel>
        </rss>
        """;
        Assert.Equal("/rss/channel/item", XmlAnchor(doc));
    }

    [Fact]
    public void Xml_ShallowCatalog_PicksRepeatingBook()
    {
        Assert.Equal("/catalog/book", XmlAnchor("<catalog><book><title>A</title></book><book><title>B</title></book></catalog>"));
    }

    [Fact]
    public void Xml_NestedRepeaters_PickTheOutermost()
    {
        const string doc = """
        <catalog>
            <book><title>A</title><author>x</author><author>y</author></book>
            <book><title>B</title><author>p</author><author>q</author></book>
        </catalog>
        """;
        Assert.Equal("/catalog/book", XmlAnchor(doc));
    }

    [Fact]
    public void Xml_FlatSingleRecord_ReturnsNull()
    {
        Assert.Null(XmlAnchor("<book><title>A</title><author>x</author></book>"));
    }

    [Fact]
    public void Xml_TwoSiblingRepeaters_PickTheMoreFrequent()
    {
        const string doc = """
        <data>
            <users><user><id>1</id></user><user><id>2</id></user></users>
            <orders><order><id>1</id></order><order><id>2</id></order><order><id>3</id></order></orders>
        </data>
        """;
        Assert.Equal("/data/orders/order", XmlAnchor(doc));
    }

    [Fact]
    public void Xml_MultiSample_UnionsAndDetects()
    {
        Assert.Equal(
            "/rss/channel/item",
            XmlAnchor(
                "<rss><channel><item><id>1</id></item><item><id>2</id></item></channel></rss>",
                "<rss><channel><item><id>3</id></item><item><id>4</id></item></channel></rss>"));
    }

    [Fact]
    public void Xml_SingleItemPerFileCorpus_ReturnsNull()
    {
        // Each file shows <item> exactly once under <channel>: nothing is observed repeating, so it declines.
        Assert.Null(XmlAnchor(
            "<rss><channel><item><id>1</id></item></channel></rss>",
            "<rss><channel><item><id>2</id></item></channel></rss>"));
    }

    [Fact]
    public void Xml_AttributesAreNeverARowGrain()
    {
        // The repeated <row> carries attributes; the anchor is the element, never one of its attributes.
        Assert.Equal("/sheet/row", XmlAnchor("""<sheet><row a="1" b="2"/><row a="3" b="4"/></sheet>"""));
    }

    [Fact]
    public void Xml_MostFrequentRepeaterWins_AtEqualDepth()
    {
        // users (x2) and orders (x3) repeat at the same depth; the more frequent collection wins.
        const string doc = """
        <data>
            <users><user><id>1</id></user><user><id>2</id></user></users>
            <orders><order><id>1</id></order><order><id>2</id></order><order><id>3</id></order></orders>
        </data>
        """;
        Assert.Equal("/data/orders/order", XmlAnchor(doc));
    }

    [Fact]
    public void Xml_SingleDocument_MixedSingleAndRepeatedChildren_PicksTheRepeater()
    {
        // <title> occurs once, <entry> occurs three times under the same <feed>; only the repeater is the grain.
        const string doc = """
        <feed>
            <title>Atom</title>
            <entry><id>1</id></entry>
            <entry><id>2</id></entry>
            <entry><id>3</id></entry>
        </feed>
        """;
        Assert.Equal("/feed/entry", XmlAnchor(doc));
    }
}
