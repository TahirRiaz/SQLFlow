using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The invoke-to-file lineage link, verified through the full graph build (declared tier): an invoke that declares
/// the file(s) its external compute lands is connected to the file ingestion that reads them, so a real flow
/// dependency forms (invoke before ingestion), both flows meet on one shared file node, and the execution waves
/// order the fetch ahead of the load. The match is engine-parity (path first, then file name), so it links across
/// wildcards and subfolders but never across a different folder or file type.
/// </summary>
public sealed class LineageInvokeFileLinkTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 7, 14, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-invlink-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageInvokeFileLinkTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private LineageReport Build()
    {
        var collected = new FlowSetCollector().Collect(_root);
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    /// <summary>An invoke flow whose output lands at <paramref name="location"/> (optionally with a file glob and a
    /// path mask).</summary>
    private static string Invoke(string name, string location, string? srcFile = null, string? srcPathMask = null)
    {
        var lines = new List<string>
        {
            "flowType: inv",
            $"name: {name}",
            "servicePrincipals:",
            "  deploy: { subscriptionId: s, resourceGroup: rg, dataFactoryName: adf }",
            "invoke:",
            "  type: adf",
            "  pipeline: pl_fetch",
            "  servicePrincipal: deploy",
            "  output:",
            $"    location: {location}",
        };
        if (srcFile is not null)
        {
            lines.Add($"    srcFile: \"{srcFile}\"");   // quote: a glob beginning with '*' is a YAML alias otherwise
        }

        if (srcPathMask is not null)
        {
            lines.Add($"    srcPathMask: \"{srcPathMask}\"");
        }

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>An invoke that lands several file drops (e.g. an SFTP download of many file sets), one <c>outputs:</c>
    /// entry per (folder, glob).</summary>
    private static string InvokeOutputs(string name, params (string Location, string? SrcFile)[] outputs)
    {
        var lines = new List<string>
        {
            "flowType: inv",
            $"name: {name}",
            "servicePrincipals:",
            "  deploy: { subscriptionId: s, resourceGroup: rg, dataFactoryName: adf }",
            "invoke:",
            "  type: adf",
            "  pipeline: pl_fetch",
            "  servicePrincipal: deploy",
            "  outputs:",
        };
        foreach (var (location, srcFile) in outputs)
        {
            var glob = srcFile is null ? "" : $", srcFile: \"{srcFile}\"";
            lines.Add($"    - {{ location: {location}{glob} }}");
        }

        return string.Join('\n', lines) + '\n';
    }

    /// <summary>A file ingestion reading <paramref name="type"/> files from <paramref name="location"/> (optionally
    /// constrained by a file glob).</summary>
    private static string FileIngestion(
        string name, string type, string location, string? srcFile = null, string? srcPathMask = null, string table = "Orders")
    {
        var lines = new List<string>
        {
            $"name: {name}",
            "source:",
            $"  type: {type}",
            $"  location: {location}",
        };
        var options = new List<string>();
        if (srcFile is not null)
        {
            options.Add($"srcFile: \"{srcFile}\"");
        }

        if (srcPathMask is not null)
        {
            options.Add($"srcPathMask: \"{srcPathMask}\"");
        }

        if (options.Count > 0)
        {
            lines.Add($"  options: {{ {string.Join(", ", options)} }}");
        }

        lines.Add("target:");
        lines.Add("  connection: ${env:SQLFLOW_CONN_DWH}");
        lines.Add("  schema: raw");
        lines.Add($"  table: {table}");
        return string.Join('\n', lines) + '\n';
    }

    private static LineageObjectNode FileNode(LineageReport report)
        => Assert.Single(report.Objects, o => o.Kind == LineageNodeKind.File);

    private static bool DependsOn(LineageReport report, string fromFlow, string toFlow)
        => report.FlowDependencies.Any(d => d.FromFlow == fromFlow && d.ToFlow == toFlow);

    private static int WaveOf(LineageReport report, string flow)
        => report.ExecutionPlan.Waves.Single(w => w.Flows.Contains(flow)).Wave;

    [Fact]
    public void Link_FormsFlowDependency_InvokeBeforeIngestion()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming", srcFile: "orders_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming", srcFile: "orders_*.csv"));

        var report = Build();

        var dep = Assert.Single(report.FlowDependencies, d => d.FromFlow == "fetch-orders" && d.ToFlow == "load-orders");
        Assert.Contains(FileNode(report).Key, dep.ViaObjects);
    }

    [Fact]
    public void Link_MeetsOnOneSharedFileNode()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming", srcFile: "orders_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming", srcFile: "orders_*.csv"));

        var report = Build();

        var file = FileNode(report);
        Assert.Equal("data/incoming", file.Name);
        Assert.Contains(report.Edges, e => e.Flow == "fetch-orders" && e.Relation == LineageRelation.Writes && e.ObjectKey == file.Key);
        Assert.Contains(report.Edges, e => e.Flow == "load-orders" && e.Relation == LineageRelation.Reads && e.ObjectKey == file.Key);
    }

    [Fact]
    public void Link_ExecutionWaves_OrderFetchAheadOfLoad()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming", srcFile: "orders_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming", srcFile: "orders_*.csv"));

        var report = Build();

        Assert.True(WaveOf(report, "fetch-orders") < WaveOf(report, "load-orders"));
    }

    [Fact]
    public void Link_WildcardOnBothSides()   // invoke drops orders_*.csv; ingestion reads default *.csv
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming", srcFile: "orders_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming"));   // default *.csv

        Assert.True(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void Link_ConcreteOutputFile_ToConcreteSource()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming/orders.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming/orders.csv"));

        Assert.True(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void Link_ProducerInSubfolder_OfWatchedRoot()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming/orders/orders_1.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming"));   // watches the root

        Assert.True(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void Link_PathMaskOnConsumer_Confirms()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming/orders", srcFile: "*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data", srcPathMask: ".*/orders/.*"));

        // The mask is authored on the source options; assert the link forms via the mask path.
        Assert.True(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void Link_CloudUrl_DatedDrop()
    {
        const string root = "abfss://raw@datalake.dfs.core.windows.net/orders";
        Write("fetch.flow.yaml", Invoke("fetch-orders", $"{root}/orders_20260714.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", root, srcFile: "orders_*.csv"));

        Assert.True(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void Link_FansOutToEveryMatchingIngestion()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming", srcFile: "*.csv"));
        Write("load-a.flow.yaml", FileIngestion("load-a", "csv", "./data/incoming", table: "OrdersA"));
        Write("load-b.flow.yaml", FileIngestion("load-b", "csv", "./data/incoming", table: "OrdersB"));

        var report = Build();
        Assert.True(DependsOn(report, "fetch-orders", "load-a"));
        Assert.True(DependsOn(report, "fetch-orders", "load-b"));
    }

    [Fact]
    public void MultiOutput_SftpDrop_BindsEachDropToItsOwnIngestion()
    {
        Write("fetch.flow.yaml", InvokeOutputs("sftp-fetch",
            ("./data/orders", "orders_*.csv"),
            ("./data/invoices", "inv_*.csv")));
        Write("load-orders.flow.yaml", FileIngestion("load-orders", "csv", "./data/orders", srcFile: "orders_*.csv", table: "Orders"));
        Write("load-invoices.flow.yaml", FileIngestion("load-invoices", "csv", "./data/invoices", srcFile: "inv_*.csv", table: "Invoices"));

        var report = Build();
        Assert.True(DependsOn(report, "sftp-fetch", "load-orders"));
        Assert.True(DependsOn(report, "sftp-fetch", "load-invoices"));
        // Two distinct file nodes, one per drop.
        Assert.Equal(2, report.Objects.Count(o => o.Kind == LineageNodeKind.File));
    }

    [Fact]
    public void MultiOutput_UnconsumedDrop_RecordsOwnNode_WhileOthersLink()
    {
        Write("fetch.flow.yaml", InvokeOutputs("sftp-fetch",
            ("./data/orders", "orders_*.csv"),
            ("./data/archive", "*.csv")));   // nothing reads the archive folder
        Write("load-orders.flow.yaml", FileIngestion("load-orders", "csv", "./data/orders", srcFile: "orders_*.csv"));

        var report = Build();
        Assert.True(DependsOn(report, "sftp-fetch", "load-orders"));
        // The unconsumed drop is still a visible file node the invoke writes.
        Assert.Contains(report.Edges, e => e.Flow == "sftp-fetch" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Kind == LineageNodeKind.File && o.Name == "data/archive"));
    }

    [Fact]
    public void MultiOutput_ManyFilesOneFolder_LinkViaGlob()   // SFTP dropping many files into one folder
    {
        Write("fetch.flow.yaml", Invoke("sftp-fetch", "./data/incoming", srcFile: "export_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-incoming", "csv", "./data/incoming", srcFile: "export_*.csv"));

        Assert.True(DependsOn(Build(), "sftp-fetch", "load-incoming"));
    }

    [Fact]
    public void NoLink_DifferentFolder()
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/invoices", srcFile: "orders_*.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming", srcFile: "orders_*.csv"));

        var report = Build();
        Assert.False(DependsOn(report, "fetch-orders", "load-orders"));
        // The invoke still records its own declared output node; it just is not the ingestion's.
        Assert.Contains(report.Edges, e => e.Flow == "fetch-orders" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Kind == LineageNodeKind.File && o.Name == "data/invoices"));
    }

    [Fact]
    public void NoLink_SiblingFolderPrefix()   // data/incoming2 must not match data/incoming
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming2/orders.csv"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming"));

        Assert.False(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void NoLink_FileTypeMismatch()   // parquet drop into a folder a csv ingestion watches
    {
        Write("fetch.flow.yaml", Invoke("fetch-orders", "./data/incoming/data.parquet"));
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming"));

        Assert.False(DependsOn(Build(), "fetch-orders", "load-orders"));
    }

    [Fact]
    public void NoOutput_InvokeStaysADatalessNode()
    {
        Write("fetch.flow.yaml", """
            flowType: inv
            name: fetch-orders
            servicePrincipals:
              deploy: { subscriptionId: s, resourceGroup: rg, dataFactoryName: adf }
            invoke:
              type: adf
              pipeline: pl_fetch
              servicePrincipal: deploy
            """);
        Write("load.flow.yaml", FileIngestion("load-orders", "csv", "./data/incoming"));

        var report = Build();
        Assert.Contains(report.Flows, f => f.Name == "fetch-orders" && f.Kind == "inv");
        Assert.DoesNotContain(report.Edges, e => e.Flow == "fetch-orders");
        Assert.False(DependsOn(report, "fetch-orders", "load-orders"));
    }
}
