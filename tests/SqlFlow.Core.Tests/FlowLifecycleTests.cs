using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The <c>lifecycle:</c> document contract, shared by every flow kind: absent or blank is production (an existing
/// estate keeps alerting exactly as before), <c>development</c> opts the flow out of notification events, casing
/// is forgiven, and anything else fails the load with the allowed values named. Parsing is asserted per
/// representative kind (the same helper serves them all), and the catalog projection is asserted to carry the
/// value onto the pipeline row, where notification detection reads it.
/// </summary>
public sealed class FlowLifecycleTests
{
    private const string MinimalFileFlow = """
        name: orders
        source:
          type: csv
          location: ./data
        target:
          connection: Server=trg;Database=pre;Integrated Security=True
          schema: pre
          table: Orders
        """;

    private const string MinimalIngestionFlow = """
        flowType: ing
        name: ext-orders
        source:
          connection: Server=src;Database=d;Integrated Security=True
          object: dbo.Orders
        target:
          connection: Server=trg;Database=pre;Integrated Security=True
          object: dbo.Orders
        """;

    [Fact]
    public void FileFlow_DefaultsToProduction()
    {
        var flow = new YamlFlowLoader().Parse(MinimalFileFlow);
        Assert.Equal(FlowLifecycle.Production, flow.Lifecycle);
    }

    [Fact]
    public void FileFlow_ParsesDevelopment()
    {
        var flow = new YamlFlowLoader().Parse(MinimalFileFlow + "\nlifecycle: development\n");
        Assert.Equal(FlowLifecycle.Development, flow.Lifecycle);
    }

    [Fact]
    public void FileFlow_IsCaseInsensitive_AndTrims()
    {
        var flow = new YamlFlowLoader().Parse(MinimalFileFlow + "\nlifecycle: '  Development '\n");
        Assert.Equal(FlowLifecycle.Development, flow.Lifecycle);
    }

    [Fact]
    public void FileFlow_BlankMeansProduction()
    {
        var flow = new YamlFlowLoader().Parse(MinimalFileFlow + "\nlifecycle: ''\n");
        Assert.Equal(FlowLifecycle.Production, flow.Lifecycle);
    }

    [Fact]
    public void FileFlow_RejectsUnknownValue_NamingTheAllowedOnes()
    {
        var ex = Assert.Throws<FlowValidationException>(
            () => new YamlFlowLoader().Parse(MinimalFileFlow + "\nlifecycle: staging\n"));
        Assert.Contains("'lifecycle' has unknown value 'staging'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("production, development", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IngestionFlow_DefaultsToProduction_AndParsesDevelopment()
    {
        var loader = new YamlIngestionFlowLoader();
        Assert.Equal(FlowLifecycle.Production, loader.Parse(MinimalIngestionFlow).Flow.Lifecycle);
        Assert.Equal(
            FlowLifecycle.Development,
            loader.Parse(MinimalIngestionFlow + "\nlifecycle: development\n").Flow.Lifecycle);
    }

    [Fact]
    public void BatchFlow_ParsesDevelopment()
    {
        var doc = new YamlBatchFlowLoader().Parse("""
            flowType: batch
            name: nightly
            lifecycle: development
            members:
              include:
                - "*.flow.yaml"
            """);
        Assert.Equal(FlowLifecycle.Development, doc.Flow.Lifecycle);
    }

    [Fact]
    public void CatalogProjection_CarriesLifecycleOntoThePipelineRow()
    {
        var repoId = Guid.NewGuid();
        var production = CatalogProjection.Pipeline(
            repoId, "orders", "file", null, "orders.flow.yaml", null, "trg", "hash", "yaml", "{}",
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc));
        var development = CatalogProjection.Pipeline(
            repoId, "orders-dev", "file", null, "orders-dev.flow.yaml", null, "trg", "hash", "yaml", "{}",
            new DateTime(2026, 7, 11, 0, 0, 0, DateTimeKind.Utc),
            lifecycle: FlowLifecycle.Development);

        Assert.Equal(PipelineLifecycles.Production, production.Lifecycle);
        Assert.Equal(PipelineLifecycles.Development, development.Lifecycle);
    }
}
