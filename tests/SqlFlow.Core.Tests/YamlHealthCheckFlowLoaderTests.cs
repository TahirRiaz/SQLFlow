using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The health-check YAML surface (flowType: hc): the smallest document maps with the documented
/// defaults, the baseValue shorthand and the metrics list are mutually exclusive, the ml block validates its
/// ranges, holidays parse and de-duplicate, and a foreign-provider target fails at parse time.</summary>
public sealed class YamlHealthCheckFlowLoaderTests
{
    private static readonly YamlHealthCheckFlowLoader Loader = new();

    private const string Minimal = """
        flowType: hc
        name: orders-rowcount
        connections:
          dwh: ${env:DWH}
        target:
          server: dwh
          object: DW.dbo.Orders
        dateColumn: OrderDate
        baseValue: COUNT(*)
        """;

    [Fact]
    public void Minimal_MapsWithDefaults()
    {
        var doc = Loader.Parse(Minimal);
        var flow = doc.Flow;

        Assert.Equal("orders-rowcount", flow.SysAlias);
        Assert.True(flow.FlowId > 0);
        Assert.Equal("dwh", flow.Server);
        Assert.Equal("@dwh", flow.ConnectionReference);
        Assert.Equal("[DW].[dbo].[Orders]", flow.Target.QualifiedName);
        Assert.Equal("OrderDate", flow.DateColumn);

        var metric = Assert.Single(flow.Metrics);
        Assert.Equal("rowCount", metric.Name);          // the COUNT(*) naming convention
        Assert.Equal("COUNT(*)", metric.Expression);

        Assert.Null(flow.FilterCriteria);
        Assert.Equal(120, flow.MaxExperimentSeconds);
        Assert.Equal(2.0, flow.AnomalyThreshold);
        Assert.Equal(0.025, flow.EsdAlpha);
        Assert.Equal(0.10, flow.MaxAnomalyFraction);
        Assert.Equal(1, flow.MaturityDays);
        Assert.Equal(new DateOnly(1990, 1, 1), flow.SentinelDateFloor);
        Assert.Equal(ExecutionMode.Auto, flow.Mode);
        Assert.Equal(HealthCheckTraining.Auto, flow.Training);
        Assert.Null(flow.RetrainAfterDays);
        Assert.Empty(flow.Holidays);
        Assert.Null(flow.Batch);
        Assert.Null(flow.Description);
        Assert.Equal("hc", flow.FlowType);

        var connection = Assert.Single(doc.Connections);
        Assert.Equal("dwh", connection.Alias);
        Assert.Equal(DataSourceKind.MSSQL, connection.Kind);
        Assert.Equal(CredentialMode.InlineConnectionString, connection.Credential.Mode);
    }

    [Fact]
    public void Mode_ParsesManual_CaseInsensitively()
    {
        var doc = Loader.Parse(Minimal + "\nmode: Manual");
        Assert.Equal(ExecutionMode.Manual, doc.Flow.Mode);
    }

    [Fact]
    public void UnknownMode_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal + "\nmode: scheduled"));
        Assert.Contains("'mode'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("auto, manual", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FullDocument_MapsEveryField()
    {
        var doc = Loader.Parse("""
            flowType: hc
            name: orders-watch
            description: Watches orders volume and revenue.
            batch: nightly
            connections:
              dwh: ${env:DWH}
            target:
              server: dwh
              object: DW.dbo.Orders
            dateColumn: OrderDate
            metrics:
              - name: orders
                baseValue: COUNT(*)
              - name: revenue
                baseValue: SUM(Amount)
              - baseValue: COUNT(DISTINCT CustomerID)
            filter: OrderStatus <> 'Cancelled'
            maturityDays: 2
            sentinelDateFloor: 2000-01-01
            ml:
              maxExperimentSeconds: 30
              anomalyThreshold: 1.5
              esdAlpha: 0.01
              maxAnomalyFraction: 0.2
              training: auto
              retrainAfterDays: 14
            holidays:
              - 2026-01-01
              - 2026-12-25
              - 2026-01-01
            """);
        var flow = doc.Flow;

        Assert.Equal("orders-watch", flow.SysAlias);
        Assert.Equal("Watches orders volume and revenue.", flow.Description);
        Assert.Equal("nightly", flow.Batch);

        Assert.Equal(3, flow.Metrics.Count);
        Assert.Equal(("orders", "COUNT(*)"), (flow.Metrics[0].Name, flow.Metrics[0].Expression));
        Assert.Equal(("revenue", "SUM(Amount)"), (flow.Metrics[1].Name, flow.Metrics[1].Expression));
        Assert.Equal(("value", "COUNT(DISTINCT CustomerID)"), (flow.Metrics[2].Name, flow.Metrics[2].Expression));

        Assert.Equal("OrderStatus <> 'Cancelled'", flow.FilterCriteria);
        Assert.Equal(2, flow.MaturityDays);
        Assert.Equal(new DateOnly(2000, 1, 1), flow.SentinelDateFloor);
        Assert.Equal(30, flow.MaxExperimentSeconds);
        Assert.Equal(1.5, flow.AnomalyThreshold);
        Assert.Equal(0.01, flow.EsdAlpha);
        Assert.Equal(0.2, flow.MaxAnomalyFraction);
        Assert.Equal(HealthCheckTraining.Auto, flow.Training);
        Assert.Equal(14, flow.RetrainAfterDays);

        // The duplicated holiday collapses to one; order is preserved.
        Assert.Equal([new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 25)], flow.Holidays);
    }

    [Fact]
    public void InlineConnection_SynthesizesTheTargetName()
    {
        var doc = Loader.Parse("""
            flowType: hc
            name: inline-check
            target:
              connection: ${env:DWH}
              object: DW.dbo.Orders
            dateColumn: OrderDate
            baseValue: SUM(Amount)
            """);

        Assert.Equal("target", doc.Flow.Server);
        Assert.Equal("value", Assert.Single(doc.Flow.Metrics).Name);  // non-COUNT(*) shorthand naming
        var connection = Assert.Single(doc.Connections);
        Assert.Equal("target", connection.Alias);
    }

    [Theory]
    [InlineData("name: orders-rowcount", "name: ''", "'name' is required for a health-check flow.")]
    [InlineData("object: DW.dbo.Orders", "object: ''", "'target.object' is required (a three-part name like Database.Schema.Table).")]
    [InlineData("object: DW.dbo.Orders", "object: dbo.Orders", "'target.object'")]
    [InlineData("dateColumn: OrderDate", "dateColumn: ''", "'dateColumn' is required")]
    [InlineData("baseValue: COUNT(*)", "baseValue: ''", "a health check needs a metric")]
    public void MissingRequirements_FailWithThePath(string find, string replace, string expectedError)
    {
        var yaml = Minimal.Replace(find, replace, StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingTargetBlock_Fails()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("flowType: hc\nname: x\n"));
        Assert.Contains("'target' is required.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BaseValueAndMetrics_AreMutuallyExclusive()
    {
        var yaml = Minimal + "\nmetrics:\n  - name: extra\n    baseValue: SUM(X)\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("either 'baseValue' (one metric) or 'metrics' (a list), not both", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("- name: a\n    baseValue: ''", "'metrics[0].baseValue' is required")]
    [InlineData("- name: 'bad name'\n    baseValue: COUNT(*)", "is invalid. Use letters, digits, '_', or '-'.")]
    public void MetricEntries_Validate(string entry, string expectedError)
    {
        var yaml = Minimal.Replace("baseValue: COUNT(*)", $"metrics:\n  {entry}", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateMetricNames_AreRejected()
    {
        var yaml = Minimal.Replace("baseValue: COUNT(*)",
            "metrics:\n  - name: m\n    baseValue: COUNT(*)\n  - name: M\n    baseValue: SUM(X)", StringComparison.Ordinal);

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("metric name 'M' is declared more than once", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("maxExperimentSeconds: 0", "'ml.maxExperimentSeconds' must be between 1 and 86400")]
    [InlineData("maxExperimentSeconds: 86401", "'ml.maxExperimentSeconds' must be between 1 and 86400")]
    [InlineData("anomalyThreshold: 0", "'ml.anomalyThreshold' must be a positive number")]
    [InlineData("anomalyThreshold: -2", "'ml.anomalyThreshold' must be a positive number")]
    [InlineData("anomalyThreshold: 101", "'ml.anomalyThreshold' must be a positive number")]
    [InlineData("esdAlpha: 0", "'ml.esdAlpha' must be a significance level strictly between 0 and 0.5")]
    [InlineData("esdAlpha: 0.5", "'ml.esdAlpha' must be a significance level strictly between 0 and 0.5")]
    [InlineData("maxAnomalyFraction: 0", "'ml.maxAnomalyFraction' must be in (0, 0.49]")]
    [InlineData("maxAnomalyFraction: 0.6", "'ml.maxAnomalyFraction' must be in (0, 0.49]")]
    [InlineData("training: sometimes", "'ml.training' has unknown value 'sometimes'. Allowed: auto, always, never.")]
    [InlineData("retrainAfterDays: 0", "'ml.retrainAfterDays' must be at least 1")]
    public void MlBlock_ValidatesItsRanges(string mlLine, string expectedError)
    {
        var yaml = Minimal + $"\nml:\n  {mlLine}\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains(expectedError, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("maturityDays: -1")]
    [InlineData("maturityDays: 31")]
    public void MaturityDays_ValidatesItsRange(string line)
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(Minimal + "\n" + line + "\n"));
        Assert.Contains("'maturityDays' must be between 0 and 30", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("always")]
    [InlineData("never")]
    public void RetrainAfterDays_RequiresAutoTraining(string training)
    {
        var yaml = Minimal + $"\nml:\n  training: {training}\n  retrainAfterDays: 30\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'ml.retrainAfterDays' only applies with 'ml.training: auto'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BadHolidayDate_FailsWithItsIndex()
    {
        var yaml = Minimal + "\nholidays:\n  - 2026-01-01\n  - not-a-date\n";

        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse(yaml));
        Assert.Contains("'holidays[1]' must be a date like 2024-01-31, got 'not-a-date'.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForeignProviderTarget_IsRejectedAtParseTime()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Loader.Parse("""
            flowType: hc
            name: bad-check
            connections:
              shop:
                provider: mysql
                connection: ${env:SHOP}
            target:
              server: shop
              object: shop.sales.orders
            dateColumn: order_date
            baseValue: COUNT(*)
            """));

        Assert.Contains("the target connection 'shop' is 'MySQL'; a health-check flow's target must be SQL Server (mssql or azdb)",
            ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentLoader_DispatchesHc_AndNamesItInTheUnknownKindError()
    {
        var documents = new YamlDocumentLoader(
            new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
            new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(), new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(), new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(), new YamlTranslateFlowLoader());

        var doc = Assert.IsType<HealthCheckFlowDocument>(documents.Parse(Minimal));
        Assert.Equal("orders-rowcount", doc.Document.Flow.SysAlias);

        var ex = Assert.Throws<FlowValidationException>(() => documents.Parse("flowType: bogus"));
        Assert.Contains("'hc' for an ML health check", ex.Message, StringComparison.Ordinal);
    }
}
