using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The cpy/sftp-to-file lineage link, verified through the full graph build (declared tier). A cpy flow that
/// lands files into the lake, or an sftp download that writes them, meets the downstream file ingestion on one
/// shared file node even when the two sides name the same Azure container path in different URI shapes: the copy
/// target written as <c>abfss://c@acct.dfs.core.windows.net/p</c> and the ingestion reading it back as
/// <c>https://acct.dfs.core.windows.net/c/p/</c> (trailing slash and all) canonicalize to one node, so a real
/// flow dependency forms and the execution waves order the copy ahead of the load. A different container path
/// never binds.
/// </summary>
public sealed class LineageCopySftpFileLinkTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 7, 15, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-cpylink-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageCopySftpFileLinkTests() => Directory.CreateDirectory(_root);

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

    private static string Copy(string name, string source, string target)
        => string.Join('\n',
            "flowType: cpy",
            $"name: {name}",
            "operation: copy",
            "source:",
            $"  location: {source}",
            "  pattern: \"*.json\"",
            "target:",
            $"  location: {target}") + '\n';

    /// <summary>A single cpy pipeline listing several source-to-target copies (the multi-item form).</summary>
    private static string CopyItems(string name, params (string Source, string Target)[] items)
    {
        var lines = new List<string> { "flowType: cpy", $"name: {name}", "operation: copy", "items:" };
        foreach (var (src, trg) in items)
        {
            lines.Add($"  - source: {{ location: {src}, pattern: \"*.json\" }}");
            lines.Add($"    target: {{ location: {trg} }}");
        }

        return string.Join('\n', lines) + '\n';
    }

    private static string SftpDownload(string name, string local, string remotePath = "/outbound")
        => string.Join('\n',
            "flowType: sftp",
            $"name: {name}",
            "direction: download",
            "server:",
            "  host: sftp.vendor.com",
            "  username: svc",
            "  passwordRef: ${env:SFTP_PW}",
            $"remotePath: {remotePath}",
            $"local: {local}",
            "pattern: \"*.json\"") + '\n';

    /// <summary>A cpy/sftp flow header (via <paramref name="head"/>) plus an explicit <c>outputs:</c> block, one
    /// entry per (location, srcFile, srcPathMask).</summary>
    private static string WithOutputs(string head, params (string Location, string? SrcFile, string? Mask)[] outputs)
    {
        var lines = new List<string> { head.TrimEnd('\n'), "outputs:" };
        foreach (var (location, srcFile, mask) in outputs)
        {
            var extra = "";
            if (srcFile is not null)
            {
                extra += $", srcFile: \"{srcFile}\"";
            }

            if (mask is not null)
            {
                extra += $", srcPathMask: \"{mask}\"";
            }

            lines.Add($"  - {{ location: {location}{extra} }}");
        }

        return string.Join('\n', lines) + '\n';
    }

    private static string FileIngestion(string name, string type, string location, string? srcFile = null, string table = "Landing")
    {
        var lines = new List<string>
        {
            $"name: {name}",
            "source:",
            $"  type: {type}",
            $"  location: {location}",
        };
        if (srcFile is not null)
        {
            lines.Add($"  options: {{ srcFile: \"{srcFile}\", searchSubDirectories: \"true\" }}");
        }

        lines.Add("target:");
        lines.Add("  connection: ${env:SQLFLOW_CONN_DWH}");
        lines.Add("  schema: raw");
        lines.Add($"  table: {table}");
        return string.Join('\n', lines) + '\n';
    }

    private static bool DependsOn(LineageReport report, string fromFlow, string toFlow)
        => report.FlowDependencies.Any(d => d.FromFlow == fromFlow && d.ToFlow == toFlow);

    private static int WaveOf(LineageReport report, string flow)
        => report.ExecutionPlan.Waves.Single(w => w.Flows.Contains(flow)).Wave;

    // The vendor drop the copy reads and the lake target it writes, and the https form the ingestion reads it back in.
    private const string Vendor = "abfss://boatbooking@dwstorageboatbookingprod.dfs.core.windows.net/DETAIL";
    private const string LakeAbfss = "abfss://datalakev2@acct.dfs.core.windows.net/raw/boatbooking/history/detail";
    private const string LakeHttps = "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/detail/";

    [Fact]
    public void Copy_BindsToIngestion_AcrossUriShapes()
    {
        Write("00_cpy.flow.yaml", Copy("bb-cpy-detail", Vendor, LakeAbfss));
        Write("01_jsn.flow.yaml", FileIngestion("bb-load-detail", "json", LakeHttps, srcFile: "*.json"));

        var report = Build();

        Assert.True(DependsOn(report, "bb-cpy-detail", "bb-load-detail"));
        // Both sides meet on exactly one canonical lake node (the two URI shapes collapsed).
        var node = Assert.Single(report.Objects, o =>
            o.Kind == LineageNodeKind.File && o.Key.Contains("history/detail"));
        Assert.Equal("az://acct/datalakev2/raw/boatbooking/history/detail", node.Name);
        Assert.Contains(report.Edges, e => e.Flow == "bb-cpy-detail" && e.Relation == LineageRelation.Writes && e.ObjectKey == node.Key);
        Assert.Contains(report.Edges, e => e.Flow == "bb-load-detail" && e.Relation == LineageRelation.Reads && e.ObjectKey == node.Key);
    }

    [Fact]
    public void Copy_ExecutionWaves_OrderCopyAheadOfLoad()
    {
        Write("00_cpy.flow.yaml", Copy("bb-cpy-detail", Vendor, LakeAbfss));
        Write("01_jsn.flow.yaml", FileIngestion("bb-load-detail", "json", LakeHttps, srcFile: "*.json"));

        var report = Build();

        Assert.True(WaveOf(report, "bb-cpy-detail") < WaveOf(report, "bb-load-detail"));
    }

    [Fact]
    public void Sftp_Download_BindsToIngestion_AcrossUriShapes()
    {
        Write("00_sftp.flow.yaml", SftpDownload("vendor-download", LakeAbfss));
        Write("01_jsn.flow.yaml", FileIngestion("vendor-load", "json", LakeHttps, srcFile: "*.json"));

        var report = Build();

        Assert.True(DependsOn(report, "vendor-download", "vendor-load"));
        Assert.True(WaveOf(report, "vendor-download") < WaveOf(report, "vendor-load"));
    }

    [Fact]
    public void Copy_NoBind_DifferentContainerPath()
    {
        Write("00_cpy.flow.yaml", Copy("bb-cpy-detail", Vendor,
            "abfss://datalakev2@acct.dfs.core.windows.net/raw/boatbooking/history/sess"));
        Write("01_jsn.flow.yaml", FileIngestion("bb-load-detail", "json", LakeHttps, srcFile: "*.json"));

        var report = Build();

        Assert.False(DependsOn(report, "bb-cpy-detail", "bb-load-detail"));
        // The copy still records its own distinct target node.
        Assert.Contains(report.Edges, e => e.Flow == "bb-cpy-detail" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Name == "az://acct/datalakev2/raw/boatbooking/history/sess"));
    }

    private const string Lake = "abfss://datalakev2@acct.dfs.core.windows.net/raw/boatbooking/history";

    [Fact]
    public void Copy_MultiItemPipeline_EachStepBindsToItsLoad()
    {
        // One copy pipeline lists two object copies (the Boatbooking shape); lineage is computed from the items, so
        // each landed folder binds to its own load and every load runs after the single copy.
        Write("00_cpy.flow.yaml", CopyItems("bb-cpy",
            ("abfss://boatbooking@vendor.dfs.core.windows.net/DETAIL", $"{Lake}/detail"),
            ("abfss://boatbooking@vendor.dfs.core.windows.net/SESS", $"{Lake}/sess")));
        Write("01_detail.flow.yaml", FileIngestion("load-detail", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/detail/", srcFile: "*.json", table: "Detail"));
        Write("01_sess.flow.yaml", FileIngestion("load-sess", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/sess/", srcFile: "*.json", table: "Sess"));

        var report = Build();

        Assert.True(DependsOn(report, "bb-cpy", "load-detail"));
        Assert.True(DependsOn(report, "bb-cpy", "load-sess"));
        Assert.True(WaveOf(report, "bb-cpy") < WaveOf(report, "load-detail"));
        Assert.True(WaveOf(report, "bb-cpy") < WaveOf(report, "load-sess"));
    }

    [Fact]
    public void Copy_SubfolderDrops_BindToParentFolderLoad()
    {
        // The converted-source shape (ticketapp): one copy lands each dataset into its own subfolder under history/,
        // while a single load reads the parent folder recursively (searchSubDirectories). No drop folder equals the
        // watched folder - each is beneath it - so this binds only when folder containment is honored across the
        // abfss/https URI shapes. The load must run after the copy.
        Write("00_cpy.flow.yaml", CopyItems("src-cpy",
            ("abfss://export@vendor.dfs.core.windows.net/appinstances", $"{Lake}/appinstances"),
            ("abfss://export@vendor.dfs.core.windows.net/orders", $"{Lake}/orders")));
        Write("01_csv.flow.yaml", FileIngestion("src-load", "csv",
            "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/", srcFile: "src*.csv"));

        var report = Build();

        Assert.True(DependsOn(report, "src-cpy", "src-load"));
        Assert.True(WaveOf(report, "src-cpy") < WaveOf(report, "src-load"));
        // Both meet on the single parent-folder node the load watches; the per-dataset drops collapse onto it.
        Assert.Contains(report.Edges, e => e.Flow == "src-cpy" && e.Relation == LineageRelation.Writes
            && report.Objects.Any(o => o.Key == e.ObjectKey && o.Name == "az://acct/datalakev2/raw/boatbooking/history"));
    }

    [Fact]
    public void Copy_DeclaredOutputs_FanOutToEachConsumer_ByFolder()
    {
        // One copy declares two distinct output folders; each binds to the ingestion that reads it, and not the other.
        Write("00_cpy.flow.yaml", WithOutputs(
            Copy("bb-cpy", Vendor, Lake),
            ($"{Lake}/detail", "*.json", null),
            ($"{Lake}/sess", "*.json", null)));
        Write("01_detail.flow.yaml", FileIngestion("load-detail", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/detail/", srcFile: "*.json", table: "Detail"));
        Write("01_sess.flow.yaml", FileIngestion("load-sess", "json",
            "https://acct.dfs.core.windows.net/datalakev2/raw/boatbooking/history/sess/", srcFile: "*.json", table: "Sess"));

        var report = Build();

        Assert.True(DependsOn(report, "bb-cpy", "load-detail"));
        Assert.True(DependsOn(report, "bb-cpy", "load-sess"));
        // Each load's wave is after the copy's.
        Assert.True(WaveOf(report, "bb-cpy") < WaveOf(report, "load-detail"));
    }

    [Fact]
    public void Sftp_Download_MultipleFiles_AllConsumersGetAnEdge()
    {
        // The user's scenario: one sftp download drops several file sets; every consumer of those files has an edge
        // from this sftp. Distinguished here by file-name glob within one shared landing folder.
        Write("00_sftp.flow.yaml", WithOutputs(
            SftpDownload("vendor-download", $"{Lake}/inbound"),
            ($"{Lake}/inbound", "orders_*.json", null),
            ($"{Lake}/inbound", "invoices_*.json", null)));
        Write("01_orders.flow.yaml", FileIngestion("load-orders", "json",
            $"{Lake}/inbound", srcFile: "orders_*.json", table: "Orders"));
        Write("01_invoices.flow.yaml", FileIngestion("load-invoices", "json",
            $"{Lake}/inbound", srcFile: "invoices_*.json", table: "Invoices"));

        var report = Build();

        Assert.True(DependsOn(report, "vendor-download", "load-orders"));
        Assert.True(DependsOn(report, "vendor-download", "load-invoices"));
    }

    [Fact]
    public void Sftp_Download_DeclaredOutput_RegexMaskSelectsConsumer()
    {
        // A producer-side path regex (srcPathMask on the output) selects which consumer the download feeds.
        Write("00_sftp.flow.yaml", WithOutputs(
            SftpDownload("vendor-download", $"{Lake}"),
            ($"{Lake}", "*.json", ".*/history/trans(/.*)?$")));
        Write("01_trans.flow.yaml", FileIngestion("load-trans", "json", $"{Lake}/trans", srcFile: "*.json", table: "Trans"));
        Write("01_detail.flow.yaml", FileIngestion("load-detail", "json", $"{Lake}/detail", srcFile: "*.json", table: "Detail"));

        var report = Build();

        Assert.True(DependsOn(report, "vendor-download", "load-trans"));
        // The mask excludes the detail folder, so no spurious edge forms.
        Assert.False(DependsOn(report, "vendor-download", "load-detail"));
    }

    [Fact]
    public void Copy_DeclaredOutput_ConcreteFileGlob_NoBindOnTypeMismatch()
    {
        // The output is a concrete file (its own node, not the folder), so a consumer whose glob the file name cannot
        // satisfy does not bind - the file-name step genuinely filters when the output is a specific file.
        Write("00_cpy.flow.yaml", WithOutputs(
            Copy("bb-cpy", Vendor, $"{Lake}/archive"),
            ($"{Lake}/archive/bundle.parquet", null, null)));
        Write("01_csv.flow.yaml", FileIngestion("load-csv", "csv", $"{Lake}/archive", srcFile: "*.csv", table: "Csv"));

        var report = Build();

        Assert.False(DependsOn(report, "bb-cpy", "load-csv"));
    }
}
