using SqlFlow.Core.Model;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Reader-level tests that statistics-driven record-anchor detection flows through the discover/introspect
/// surface: an envelope JSON file and a nested-repeater XML file are discovered at the record grain (one path
/// set per record, the detected grain reported as an option), while the load path is untouched by detection.
/// </summary>
public sealed class DiscoveryAnchorIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_anchor_" + Guid.NewGuid().ToString("N"));
    private readonly JsonSourceReader _json = new(new LocalFileLifecycle(), [new LocalFileStore()]);
    private readonly XmlSourceReader _xml = new(new LocalFileLifecycle(), [new LocalFileStore()]);

    public DiscoveryAnchorIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Source(string fileName, string content)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        var type = Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
        return new SourceSpec { Type = type, Location = path, Options = new Dictionary<string, string?>() };
    }

    [Fact]
    public async Task Json_EnvelopeArray_IsDiscoveredAtRecordGrain()
    {
        var source = Source("gh.json",
            """{ "total_count": 2, "incomplete_results": false, "items": [ { "id": 1, "name": "a" }, { "id": 2, "name": "b" } ] }""");

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        // The two array elements are the records, not the single envelope object.
        Assert.Equal(2, introspection.Inventory.RecordsScanned);
        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal("$.items", introspection.Options.First(o => o.Key == "rootPath").Value);
        // The record columns are the item fields (id, name), not the envelope scalars.
        Assert.Contains(introspection.Formula.Columns, c => c.Name == "id");
        Assert.Contains(introspection.Formula.Columns, c => c.Name == "name");
        Assert.DoesNotContain(introspection.Formula.Columns, c => c.Name == "total_count");
    }

    [Fact]
    public async Task Json_ExplicitRootPath_WinsOverDetection()
    {
        var path = Path.Combine(_dir, "explicit.json");
        File.WriteAllText(path,
            """{ "audit": [ {"by": "x"} ], "records": [ {"id": 1}, {"id": 2}, {"id": 3} ] }""");
        var source = new SourceSpec
        {
            Type = "json",
            Location = path,
            Options = new Dictionary<string, string?> { ["rootPath"] = "$.audit" },
        };

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);            // caller pinned it; detection stays out
        Assert.Equal("$.audit", introspection.Options.First(o => o.Key == "rootPath").Value);
        Assert.Equal(1, introspection.Inventory.RecordsScanned);  // one element in $.audit
    }

    [Fact]
    public async Task Json_PlainRecords_KeepRootGrain()
    {
        var source = Source("plain.json", """{ "id": 1, "name": "a", "vendor": { "city": "Oslo" } }""");

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);
        Assert.Equal("$", introspection.Options.First(o => o.Key == "rootPath").Value);
        Assert.Equal(1, introspection.Inventory.RecordsScanned);
    }

    [Fact]
    public async Task Json_LoadPath_IsNotAffectedByDetection()
    {
        // The default load reads the document at the root: an envelope is one row whose items array is a column.
        // Detection only shapes discovery, so the runtime stays deterministic on the saved config.
        var source = Source("load.json",
            """{ "total_count": 2, "items": [ { "id": 1 }, { "id": 2 } ] }""");

        var columns = await _json.GetColumnsAsync(source);
        var read = await _json.OpenAsync(source, columns);
        await using var reader = read.Reader;

        var rowCount = 0;
        while (await reader.ReadAsync())
        {
            rowCount++;
        }

        Assert.Equal(1, rowCount);                              // one envelope row, not two records
        Assert.Contains(columns, c => c.Name == "items");        // the array kept as a column at the root grain
    }

    [Fact]
    public async Task Xml_RssEnvelope_IsDiscoveredAtItemGrain()
    {
        var source = Source("feed.xml",
            """
            <rss><channel><title>Feed</title>
                <item><guid>1</guid><title>A</title></item>
                <item><guid>2</guid><title>B</title></item>
            </channel></rss>
            """);

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal(2, introspection.Inventory.RecordsScanned);
        Assert.Equal("/rss/channel/item", introspection.AutoDetectedGrain);
        Assert.Equal("/rss/channel/item", introspection.Options.First(o => o.Key == "rowXPath").Value);
        Assert.Contains(introspection.Formula.Columns, c => c.Name == "guid");
    }

    [Fact]
    public async Task Xml_FlatSingleRecord_KeepsEngineDefault()
    {
        var source = Source("one.xml", "<book><title>A</title><author>x</author></book>");

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);
        Assert.DoesNotContain(introspection.Options, o => o.Key == "rowXPath");
    }

    [Fact]
    public async Task Xml_ExplicitRowXPath_WinsOverDetection()
    {
        var path = Path.Combine(_dir, "explicit.xml");
        File.WriteAllText(path,
            "<rss><channel><item><id>1</id></item><item><id>2</id></item></channel></rss>");
        var source = new SourceSpec
        {
            Type = "xml",
            Location = path,
            Options = new Dictionary<string, string?> { ["rowXPath"] = "/rss/channel" },
        };

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Null(introspection.AutoDetectedGrain);
        Assert.Equal("/rss/channel", introspection.Options.First(o => o.Key == "rowXPath").Value);
        Assert.Equal(1, introspection.Inventory.RecordsScanned);   // one <channel> record, as pinned
    }

    [Fact]
    public async Task Json_DiscoverAsync_EnvelopeReportsRecordGrain()
    {
        var source = Source("d.json",
            """{ "total_count": 3, "items": [ {"id": 1}, {"id": 2}, {"id": 3} ] }""");

        var result = await _json.DiscoverAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal(3, result.RecordsScanned);            // three item records, not one envelope blob
        Assert.Contains("$.id", result.AllPaths);
        Assert.DoesNotContain("$.total_count", result.AllPaths);
    }

    [Fact]
    public async Task Json_InventoryAsync_EnvelopeReportsRecordGrain()
    {
        var source = Source("i.json",
            """{ "total_count": 2, "items": [ {"id": 1, "name": "a"}, {"id": 2, "name": "b"} ] }""");

        var inventory = await _json.InventoryAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal(2, inventory.RecordsScanned);
        var paths = inventory.Paths.Select(p => p.Path).ToHashSet();
        Assert.Contains("$.id", paths);
        Assert.Contains("$.name", paths);
        Assert.DoesNotContain("$.items", paths);
    }

    [Fact]
    public async Task Json_Ndjson_EnvelopePerLine_IsDetected()
    {
        // Each NDJSON line is its own envelope; the detector unions the per-line items arrays.
        var source = Source("stream.ndjson",
            "{ \"page\": 1, \"items\": [ {\"id\": 1}, {\"id\": 2} ] }\n" +
            "{ \"page\": 2, \"items\": [ {\"id\": 3} ] }");

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal(3, introspection.Inventory.RecordsScanned);   // 2 + 1 item records across the two lines
    }

    [Fact]
    public async Task Json_MultiFileEnvelopeCorpus_UnionsAndDetects()
    {
        Source("page1.json", """{ "total_count": 2, "items": [ {"id": 1}, {"id": 2} ] }""");
        Source("page2.json", """{ "total_count": 1, "items": [ {"id": 3} ] }""");
        var source = new SourceSpec
        {
            Type = "json",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "page*.json" },
        };

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.FilesScanned);
        Assert.Equal(3, introspection.Inventory.RecordsScanned);   // 2 + 1 records
    }

    [Fact]
    public async Task Json_MaxRecords_BoundsBothDetectionAndScan()
    {
        var source = Source("big.json",
            """{ "items": [ {"id": 1}, {"id": 2}, {"id": 3}, {"id": 4}, {"id": 5} ] }""");

        var introspection = await _json.IntrospectAsync(source, maxFiles: 100, maxRecords: 2, maxDepth: 10);

        // Detection still finds the envelope, and the scan honors the record cap.
        Assert.Equal("$.items", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.RecordsScanned);
    }

    [Fact]
    public async Task Xml_MultiFileRssCorpus_UnionsAndDetects()
    {
        Source("feed1.xml", "<rss><channel><item><id>1</id></item><item><id>2</id></item></channel></rss>");
        Source("feed2.xml", "<rss><channel><item><id>3</id></item></channel></rss>");
        var source = new SourceSpec
        {
            Type = "xml",
            Location = _dir,
            Options = new Dictionary<string, string?> { ["srcFile"] = "feed*.xml" },
        };

        var introspection = await _xml.IntrospectAsync(source, maxFiles: 100, maxRecords: 0, maxDepth: 10);

        Assert.Equal("/rss/channel/item", introspection.AutoDetectedGrain);
        Assert.Equal(2, introspection.Inventory.FilesScanned);
        Assert.Equal(3, introspection.Inventory.RecordsScanned);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
