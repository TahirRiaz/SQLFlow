using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// File locations in lineage are resolved against the folder of the document that declares them, the rule the engine
/// applies when it runs a file flow, and are identified relative to the estate root. So a flow kept in a subfolder
/// reading <c>../data</c> reads the repository's <c>data</c> folder, the identity is the same in every checkout, and
/// a drop and the flow reading it link whichever folders their documents sit in.
/// </summary>
public sealed class LineageFileAnchorTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow-fileanchor-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageFileAnchorTests() => Directory.CreateDirectory(_root);

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
        Assert.DoesNotContain(collected.Warnings, warning => warning.Contains("skipped", StringComparison.Ordinal));
        return LineageGraphBuilder.Build(collected, _root, [LineageTier.Declared], Utc);
    }

    private static string FileIngestion(string name, string location, string table)
        => string.Join('\n',
            $"name: {name}",
            "source:",
            "  type: csv",
            $"  location: {location}",
            "target:",
            "  connection: ${env:SQLFLOW_CONN_DWH}",
            "  schema: raw",
            $"  table: {table}") + '\n';

    private static string Invoke(string name, string location)
        => string.Join('\n',
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
            "    srcFile: \"*.csv\"") + '\n';

    private static IReadOnlyList<string> FileNames(LineageReport report)
        => report.Objects.Where(o => o.Kind == LineageNodeKind.File).Select(o => o.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    private static bool DependsOn(LineageReport report, string from, string to)
        => report.FlowDependencies.Any(d => d.FromFlow == from && d.ToFlow == to);

    [Fact]
    public void A_flow_in_a_subfolder_reads_the_folder_its_relative_location_names_from_there()
    {
        Write("flows/welllog-pre.yaml", FileIngestion("welllog-pre", "../data/welllog", "WellLog"));

        var report = Build();

        Assert.Equal(["data/welllog"], FileNames(report));
        Assert.Contains(report.Edges, e => e.Flow == "welllog-pre" && e.Relation == LineageRelation.Reads
            && e.ObjectKey == NodeKey.For(ServerIdentity.FileSystem, null, null, "data/welllog"));
    }

    [Fact]
    public void The_same_relative_location_in_two_folders_is_two_folders()
    {
        Write("csv/load.yaml", FileIngestion("csv-load", "./data", "CsvRows"));
        Write("parquet/load.yaml", FileIngestion("parquet-load", "./data", "ParquetRows"));

        var report = Build();

        Assert.Equal(["csv/data", "parquet/data"], FileNames(report));
    }

    [Fact]
    public void Two_spellings_of_one_folder_from_different_documents_are_one_node()
    {
        Write("flows/nested.yaml", FileIngestion("nested-load", "../data/in", "Nested"));
        Write("root.yaml", FileIngestion("root-load", "./data/in", "Root"));

        var report = Build();

        Assert.Equal(["data/in"], FileNames(report));
    }

    [Fact]
    public void A_drop_at_the_root_feeds_a_flow_in_a_subfolder_reading_the_same_folder()
    {
        Write("fetch.yaml", Invoke("fetch", "data/welllog"));
        Write("flows/welllog-pre.yaml", FileIngestion("welllog-pre", "../data/welllog", "WellLog"));

        var report = Build();

        Assert.True(DependsOn(report, "fetch", "welllog-pre"));
        Assert.Equal(["data/welllog"], FileNames(report));
    }

    [Fact]
    public void A_drop_declared_in_a_subfolder_feeds_a_flow_at_the_root()
    {
        Write("jobs/fetch.yaml", Invoke("fetch", "../data/in"));
        Write("load.yaml", FileIngestion("load", "./data/in", "Rows"));

        var report = Build();

        Assert.True(DependsOn(report, "fetch", "load"));
        Assert.Equal(["data/in"], FileNames(report));
    }

    [Fact]
    public void A_dot_prefixed_drop_feeds_a_flow_naming_the_folder_without_it()
    {
        Write("fetch.yaml", Invoke("fetch", "./data/in"));
        Write("load.yaml", FileIngestion("load", "data/in", "Rows"));

        var report = Build();

        Assert.True(DependsOn(report, "fetch", "load"));
    }

    [Fact]
    public void A_trailing_separator_names_the_same_folder()
    {
        Write("fetch.yaml", Invoke("fetch", "data/in/"));
        Write("flows/load.yaml", FileIngestion("load", "../data/in", "Rows"));

        var report = Build();

        Assert.Equal(["data/in"], FileNames(report));
        Assert.True(DependsOn(report, "fetch", "load"));
    }

    [Fact]
    public void A_location_outside_the_estate_keeps_its_absolute_path()
    {
        Write("flows/load.yaml", FileIngestion("load", "../../outside-the-estate", "Rows"));

        var report = Build();

        var expected = Path.GetFullPath(Path.Combine(_root, "..", "outside-the-estate")).Replace('\\', '/');
        Assert.Equal([expected], FileNames(report));
    }

    [Fact]
    public void A_location_that_is_a_reference_is_kept_as_written_and_links_by_that_text()
    {
        Write("flows/fetch.yaml", Invoke("fetch", "${env:LAKE_ROOT}/incoming"));
        Write("load.yaml", FileIngestion("load", "${env:LAKE_ROOT}/incoming", "Rows"));

        var report = Build();

        Assert.Equal(["${env:LAKE_ROOT}/incoming"], FileNames(report));
        Assert.True(DependsOn(report, "fetch", "load"));
    }
}
