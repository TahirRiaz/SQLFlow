using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The acquisition-to-file lineage link, verified through the full graph build (declared tier). An api flow lands
/// raw payloads at target/pathTemplate + extension; the drop must meet the downstream file ingestion on the
/// ingestion's watched-folder node even when the two sides name the same Azure container path in different URI
/// shapes and the template carries render-time tokens ({window.from:yyyy}, ...), so a real flow dependency forms
/// and the execution waves order the acquisition ahead of the load. A landing elsewhere never binds.
/// </summary>
public sealed class LineageAcquireFileLinkTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 7, 22, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-acqlink-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageAcquireFileLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
        => File.WriteAllText(Path.Combine(_root, relative), content);

    private LineageReport Build()
    {
        var collected = new FlowSetCollector().Collect(_root);
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    private static string Acquire(string name, string target, string pathTemplate, string? format = "json")
    {
        var lines = new List<string>
        {
            "flowType: api",
            $"name: {name}",
            "source:",
            "  baseUrl: https://api.vendor.test",
            "  request: { path: /export }",
            "landing:",
            $"  target: {target}",
            $"  pathTemplate: \"{pathTemplate}\"",
        };
        if (format is not null)
        {
            lines.Add($"  format: {format}");
        }

        return string.Join('\n', lines) + '\n';
    }

    private static string FileIngestion(string name, string type, string location, string srcFile, string table = "Landing")
        => string.Join('\n',
            $"name: {name}",
            "source:",
            $"  type: {type}",
            $"  location: {location}",
            $"  options: {{ srcFile: \"{srcFile}\", searchSubDirectories: \"true\" }}",
            "target:",
            "  connection: ${env:SQLFLOW_CONN_DWH}",
            "  schema: pre",
            $"  table: {table}") + '\n';

    private static bool DependsOn(LineageReport report, string fromFlow, string toFlow)
        => report.FlowDependencies.Any(d => d.FromFlow == fromFlow && d.ToFlow == toFlow);

    private static int WaveOf(LineageReport report, string flow)
        => report.ExecutionPlan.Waves.Single(w => w.Flows.Contains(flow)).Wave;

    // The ticketapp shape: the acquisition lands dated JSON under history/{yyyy}/ and the ingestion reads the
    // history/ parent back in https form, recursively, by file-name glob.
    private const string LakeAbfss = "abfss://datalakev2@acct.dfs.core.windows.net/raw/ticketapp";
    private const string HistoryHttps = "https://acct.dfs.core.windows.net/datalakev2/raw/ticketapp/history/";
    private const string Template = "history/{window.from:yyyy}/ticketapp_{window.from:yyyy-MM-dd}";

    [Fact]
    public void Acquire_BindsToIngestion_AcrossUriShapes_TokensAsWildcards()
    {
        Write("00_api.yaml", Acquire("ticketapp_00_api", LakeAbfss, Template));
        Write("01_jsn.yaml", FileIngestion("ticketapp_trans_01_jsn", "json", HistoryHttps, srcFile: "ticketapp*.json"));

        var report = Build();

        Assert.True(DependsOn(report, "ticketapp_00_api", "ticketapp_trans_01_jsn"));
        // Both sides meet on exactly one canonical DROP node: the folder the ingestion watches. The
        // acquisition's other file node is its external SOURCE endpoint, which it reads.
        var node = Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File && o.Name.StartsWith("az://", StringComparison.Ordinal));
        Assert.Equal("az://acct/datalakev2/raw/ticketapp/history", node.Name);
        Assert.Contains(report.Edges, e => e.Flow == "ticketapp_00_api" && e.Relation == LineageRelation.Writes && e.ObjectKey == node.Key);
        Assert.Contains(report.Edges, e => e.Flow == "ticketapp_trans_01_jsn" && e.Relation == LineageRelation.Reads && e.ObjectKey == node.Key);
        var endpoint = Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File && o.Name.StartsWith("https://api.vendor.test", StringComparison.Ordinal));
        Assert.Contains(report.Edges, e => e.Flow == "ticketapp_00_api" && e.Relation == LineageRelation.Reads && e.ObjectKey == endpoint.Key);
    }

    [Fact]
    public void Acquire_ExecutionWaves_OrderAcquisitionAheadOfLoad()
    {
        Write("00_api.yaml", Acquire("ticketapp_00_api", LakeAbfss, Template));
        Write("01_jsn.yaml", FileIngestion("ticketapp_trans_01_jsn", "json", HistoryHttps, srcFile: "ticketapp*.json"));

        var report = Build();

        Assert.True(WaveOf(report, "ticketapp_00_api") < WaveOf(report, "ticketapp_trans_01_jsn"));
    }

    [Fact]
    public void Acquire_AutoFormat_BindsAnyExtensionConsumer()
    {
        // 'auto' derives the extension from the response at run time, so the declared drop must match a consumer of
        // any extension; format omitted, the loader's default.
        Write("00_api.yaml", Acquire("vendor_00_api", LakeAbfss, Template, format: null));
        Write("01_csv.yaml", FileIngestion("vendor_01_csv", "csv", HistoryHttps, srcFile: "ticketapp*.csv"));

        var report = Build();

        Assert.True(DependsOn(report, "vendor_00_api", "vendor_01_csv"));
    }

    [Fact]
    public void Acquire_NoBind_DifferentLandingPath_StillRecordsOwnDrop()
    {
        Write("00_api.yaml", Acquire("vendor_00_api",
            "abfss://datalakev2@acct.dfs.core.windows.net/raw/othersource", Template));
        Write("01_jsn.yaml", FileIngestion("ticketapp_trans_01_jsn", "json", HistoryHttps, srcFile: "ticketapp*.json"));

        var report = Build();

        Assert.False(DependsOn(report, "vendor_00_api", "ticketapp_trans_01_jsn"));
        // The unconsumed drop still records its own node: the landing folder's static prefix, tokens excluded.
        Assert.Contains(report.Edges, e => e.Flow == "vendor_00_api" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Name == "az://acct/datalakev2/raw/othersource/history"));
    }

    [Fact]
    public void Acquire_BindsToIngestion_WatchingParentOfDropFolder()
    {
        // The ingestion watches the landing target root recursively while the drop lands under history/{yyyy}/:
        // no folder equality anywhere, so only the producer-consumer reconciliation (folder containment plus
        // file-glob overlap) can attribute the acquisition a write of the watched node.
        Write("00_api.yaml", Acquire("ticketapp_00_api", LakeAbfss, Template));
        Write("01_jsn.yaml", FileIngestion("ticketapp_trans_01_jsn", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/ticketapp/", srcFile: "ticketapp*.json"));

        var report = Build();

        Assert.True(DependsOn(report, "ticketapp_00_api", "ticketapp_trans_01_jsn"));
        Assert.Contains(report.Edges, e => e.Flow == "ticketapp_00_api" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Name == "az://acct/datalakev2/raw/ticketapp"));
    }

    [Fact]
    public void Acquire_NoBind_ConsumerGlobExcludesFileName()
    {
        // Same containment as above (drop beneath the watched root), but the template's file stem cannot yield a
        // name the consumer's glob accepts, so the file-name step genuinely filters and no dependency forms.
        Write("00_api.yaml", Acquire("vendor_00_api", LakeAbfss, Template));
        Write("01_jsn.yaml", FileIngestion("other_01_jsn", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/ticketapp/", srcFile: "othersource*.json"));

        var report = Build();

        Assert.False(DependsOn(report, "vendor_00_api", "other_01_jsn"));
    }

    // A multi-item api flow (the cyclehire shape): ONE pipeline that lands two endpoints to two different lake paths.
    // Each item's drop must reconcile independently to the pre flow watching its own path, so the single acquisition
    // flow feeds both downstream loads and both file nodes exist. This is the multi-item lineage guarantee.
    private static string AcquireMultiItem(string name)
        => string.Join('\n',
            "flowType: api",
            $"name: {name}",
            "source:",
            "  baseUrl: https://api.cyclehire.example",
            "items:",
            "  - name: bikes",
            "    request: { path: /api/Bikes }",
            "    landing:",
            "      target: abfss://datalakev2@acct.dfs.core.windows.net/raw/cyclehire/api/bikes",
            "      pathTemplate: \"history/{yyyy}/{MM}/cyclehire_bikes_{yyyyMMdd}\"",
            "      format: json",
            "  - name: alert",
            "    request: { path: /api/alert }",
            "    landing:",
            "      target: abfss://datalakev2@acct.dfs.core.windows.net/raw/cyclehire/api/alert",
            "      pathTemplate: \"history/{yyyy}/{MM}/cyclehire_alerts_{yyyyMMdd}\"",
            "      format: json") + '\n';

    [Fact]
    public void Acquire_MultiItem_EachItemBindsToItsOwnIngestion()
    {
        Write("00_api.yaml", AcquireMultiItem("cyclehire_00_api"));
        Write("bikes_01_jsn.yaml", FileIngestion("cyclehire_bikes_01_jsn", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/cyclehire/api/bikes/history/", srcFile: "cyclehire_bikes*.json", table: "Citybikes_Bikes"));
        Write("alert_01_jsn.yaml", FileIngestion("cyclehire_alert_01_jsn", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/cyclehire/api/alert/history/", srcFile: "cyclehire_alerts*.json", table: "Citybikes_Alert"));

        var report = Build();

        // The one flow feeds both downstream loads.
        Assert.True(DependsOn(report, "cyclehire_00_api", "cyclehire_bikes_01_jsn"));
        Assert.True(DependsOn(report, "cyclehire_00_api", "cyclehire_alert_01_jsn"));

        // Two distinct landing nodes, each written by the single acquisition flow and read by the matching pre flow.
        var bikes = Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File && o.Name == "az://acct/datalakev2/raw/cyclehire/api/bikes/history");
        var alert = Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File && o.Name == "az://acct/datalakev2/raw/cyclehire/api/alert/history");
        Assert.Contains(report.Edges, e => e.Flow == "cyclehire_00_api" && e.Relation == LineageRelation.Writes && e.ObjectKey == bikes.Key);
        Assert.Contains(report.Edges, e => e.Flow == "cyclehire_00_api" && e.Relation == LineageRelation.Writes && e.ObjectKey == alert.Key);
        Assert.Contains(report.Edges, e => e.Flow == "cyclehire_bikes_01_jsn" && e.Relation == LineageRelation.Reads && e.ObjectKey == bikes.Key);
        Assert.Contains(report.Edges, e => e.Flow == "cyclehire_alert_01_jsn" && e.Relation == LineageRelation.Reads && e.ObjectKey == alert.Key);
    }
}
