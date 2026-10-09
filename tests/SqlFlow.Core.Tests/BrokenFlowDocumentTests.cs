using SqlFlow.Catalog;
using SqlFlow.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A YAML file that is recognisably a flow but does not load is reported by the estate scan (and so by lineage and the
/// discovery preview), naming the file and the loader's error, while a YAML file that is not a flow at all stays quietly
/// out. Before, both were dropped without a word: a flow whose inline schedule carried an operation its built-in kind
/// refuses simply vanished from the scan, and the next sync deleted its pipeline.
/// </summary>
public sealed class BrokenFlowDocumentTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sqlflow-broken-flow-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string GoodFlow = """
        name: orders
        source:
          type: csv
          location: ./orders.csv
        target:
          connection: ${env:SQLFLOW_CONN_DWH}
          schema: dbo
          table: Orders
        """;

    // A built-in kind takes no operation, so this document fails to load although it is plainly a flow.
    private const string BrokenIngestion = """
        flowType: ing
        name: customers
        schedule:
          cron: "0 6 * * *"
          operation: load
        connections:
          src: ${env:SQLFLOW_SRC}
          dwh: ${env:SQLFLOW_DW}
        source:
          server: src
          object: Shop.dbo.Customers
        target:
          server: dwh
          object: DW.raw.Customers
        load:
          keyColumns: [CustomerId]
        """;

    private const string ConfigFile = """
        version: 3
        services:
          web:
            image: nginx
        """;

    private const string CompanionDocument = """
        documentType: glossary
        name: terms
        source: a word that is not a flow's
        target: another
        """;

    [Theory]
    [InlineData("flows/orders.yaml", "flowType: ing\nname: x\n", true)]
    [InlineData("flows/orders.yaml", "name: x\nsource: {}\ntarget: {}\n", true)]
    [InlineData("flows/orders.flow.yaml", "name: x\n", true)]
    [InlineData("flows/orders.flow.yaml", "", true)]
    [InlineData("flows/broken.yaml", "flowType: ing\nname: [unclosed\n", true)]
    [InlineData("flows/broken.yaml", "name: x\nsource:\n  a: [unclosed\ntarget: y\n", true)]
    [InlineData("flows/glossary.flow.yaml", "documentType: glossary\nname: x\n", false)]
    [InlineData("flows/glossary.yaml", "documentType: glossary\nsource: a\ntarget: b\n", false)]
    [InlineData("docker-compose.yaml", "version: 3\nservices:\n  web: {}\n", false)]
    [InlineData("config/settings.yaml", "", false)]
    [InlineData("config/broken.yaml", "key: [unclosed\n", false)]
    [InlineData("config/list.yaml", "- a\n- b\n", false)]
    public void Recognition_TellsAFlowFromAFileThatIsNotOne(string path, string yaml, bool expected)
        => Assert.Equal(expected, FlowDocumentRecognition.IsRecognisableFlow(path, yaml));

    [Fact]
    public void TheScan_ReportsAFlowThatDoesNotLoad_AndLeavesTheFilesThatAreNotFlowsQuiet()
    {
        WriteEstate();

        var result = new FlowSetCollector().Collect(_dir);

        Assert.Equal(["orders"], result.Flows.Select(f => f.Node.Name));
        var broken = Assert.Single(result.BrokenFlows);
        Assert.Equal("flows/customers.yaml", broken.File);
        Assert.Contains("operation, values and payload apply to flows of a registered kind; 'ing' flows take none", broken.Error, StringComparison.Ordinal);

        var warning = Assert.Single(result.Warnings, w => w.Contains("customers.yaml", StringComparison.Ordinal));
        Assert.StartsWith("flows/customers.yaml: is a flow document that does not load", warning, StringComparison.Ordinal);
        Assert.Contains("'ing' flows take none", warning, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("docker-compose", StringComparison.Ordinal)
                                                     || w.Contains("glossary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Lineage_CarriesTheWarning()
    {
        WriteEstate();

        var report = await LineageService.ComputeAsync(new LineageOptions { FlowDirectory = _dir, IncludeObserved = false });

        Assert.Contains(report.Warnings, w => w.StartsWith("flows/customers.yaml: is a flow document that does not load", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("docker-compose", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDiscoveryPreview_ListsTheBrokenFlowWithItsError()
    {
        WriteEstate();

        var discovered = FlowDiscovery.Discover(_dir);

        Assert.True(Assert.Single(discovered, d => d.RelativePath == "flows/orders.yaml").ParseOk);
        var broken = Assert.Single(discovered, d => d.RelativePath == "flows/customers.yaml");
        Assert.False(broken.ParseOk);
        Assert.Null(broken.FlowName);
        Assert.Contains("'ing' flows take none", broken.ParseError, StringComparison.Ordinal);
        Assert.DoesNotContain(discovered, d => d.RelativePath.Contains("docker-compose", StringComparison.Ordinal)
                                               || d.RelativePath.Contains("glossary", StringComparison.Ordinal));
    }

    private void WriteEstate()
    {
        Write("flows/orders.yaml", GoodFlow);
        Write("flows/customers.yaml", BrokenIngestion);
        Write("docker-compose.yaml", ConfigFile);
        Write("docs/glossary.yaml", CompanionDocument);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
