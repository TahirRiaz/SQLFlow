using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.Acquire.Tests;

/// <summary>
/// The multi-item api flow: one flow declaring several endpoints under <c>items:</c> over a shared <c>source</c>
/// connection envelope (mirroring a <c>cpy</c> flow's steps). Proves the shared transport/baseUrl/auth is applied to
/// every item, each item carries its own request/pagination/iterate/landing, the single-endpoint form still works, and
/// the mutually-exclusive-form rules fail loudly rather than landing to the wrong place.
/// </summary>
public sealed class MultiItemLoaderTests
{
    private static AcquireFlow Parse(string yaml) => new YamlAcquireFlowLoader().Parse(yaml, "<test>");

    private const string TwoItems = """
        flowType: api
        name: citybike_00_api
        batch: Citybike
        source:
          baseUrl: https://api.kolumbus.citybike.cloud
          auth:
            type: bearer
            secretRef: ${keyvault:sqlflow-v3-secrets/citybike-token}
          reliability:
            rateLimitRps: 8
            urlAllowlist: ["*.citybike.cloud"]
        items:
          - name: bikes
            request: { method: GET, path: /api/Bikes }
            landing:
              target: abfss://datalakev2@acct.dfs.core.windows.net/raw/citybike/api/bikes
              pathTemplate: "history/{yyyy}/citybike_bikes_{yyyyMMdd}"
              format: json
          - name: alert
            request: { method: GET, path: /api/alert }
            pagination: { strategy: page }
            iterate:
              - kind: date_window
                granularity: day
                from: now-3d
                to: now
            landing:
              target: abfss://datalakev2@acct.dfs.core.windows.net/raw/citybike/api/alert
              pathTemplate: "history/{yyyy}/citybike_alerts_{yyyyMMdd}"
              format: json
        """;

    [Fact]
    public void Multi_item_flow_parses_each_endpoint_over_the_shared_envelope()
    {
        var flow = Parse(TwoItems);

        Assert.Equal("citybike_00_api", flow.Name);
        Assert.Equal("Citybike", flow.Batch);
        Assert.Equal(2, flow.Items.Count);

        // The shared connection envelope is applied to every item's source: same transport, base URL, auth, reliability.
        foreach (var item in flow.Items)
        {
            Assert.Equal(AcquireTransport.Http, item.Source.Transport);
            Assert.Equal("https://api.kolumbus.citybike.cloud", item.Source.BaseUrl);
            Assert.Equal(AcquireAuthType.Bearer, item.Source.Auth.Type);
            Assert.Equal("${keyvault:sqlflow-v3-secrets/citybike-token}", item.Source.Auth.SecretRef);
            Assert.Equal(8, item.Source.Reliability.RateLimitRps);
            Assert.Contains("*.citybike.cloud", item.Source.Reliability.UrlAllowlist);
        }

        // Each item carries its own request, pagination, fan-out, and landing.
        var bikes = flow.Items[0];
        Assert.Equal("bikes", bikes.Name);
        Assert.Equal("/api/Bikes", bikes.Source.Request!.Path);
        Assert.Empty(bikes.Source.Iterations);
        Assert.Equal("abfss://datalakev2@acct.dfs.core.windows.net/raw/citybike/api/bikes", bikes.Landing.Target);

        var alert = flow.Items[1];
        Assert.Equal("alert", alert.Name);
        Assert.Equal("/api/alert", alert.Source.Request!.Path);
        Assert.Equal(AcquirePaginationStrategy.Page, alert.Source.Pagination.Strategy);
        Assert.Single(alert.Source.Iterations);
        Assert.Equal(AcquireIterationKind.DateWindow, alert.Source.Iterations[0].Kind);

        // The shared-envelope accessor points at the first item's source.
        Assert.Same(flow.Items[0].Source, flow.Source);
    }

    [Fact]
    public void Single_endpoint_form_still_parses_as_one_item()
    {
        var flow = Parse("""
            flowType: api
            name: github_issues
            source:
              baseUrl: https://api.github.com
              request: { path: /repos/microsoft/vscode/issues }
            landing:
              target: abfss://datalakev2@acct.dfs.core.windows.net/raw/github/issues
              pathTemplate: "history/{yyyy}/issues_{yyyyMMdd}"
              format: json
            """);

        var item = Assert.Single(flow.Items);
        Assert.Null(item.Name);
        Assert.Equal("/repos/microsoft/vscode/issues", item.Source.Request!.Path);
        Assert.Equal("abfss://datalakev2@acct.dfs.core.windows.net/raw/github/issues", item.Landing.Target);
    }

    [Fact]
    public void Items_with_top_level_landing_is_rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            flowType: api
            name: bad
            source:
              baseUrl: https://api.test
            landing:
              target: abfss://datalakev2@acct.dfs.core.windows.net/raw/x
              pathTemplate: "t"
            items:
              - request: { path: /a }
                landing: { target: abfss://datalakev2@acct.dfs.core.windows.net/raw/a, pathTemplate: "a" }
            """));

        Assert.Contains("remove the top-level 'landing'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Items_with_a_request_on_the_shared_source_is_rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            flowType: api
            name: bad
            source:
              baseUrl: https://api.test
              request: { path: /shared }
            items:
              - request: { path: /a }
                landing: { target: abfss://datalakev2@acct.dfs.core.windows.net/raw/a, pathTemplate: "a" }
            """));

        Assert.Contains("belong under each 'items[]' entry", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Items_with_incremental_is_rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            flowType: api
            name: bad
            source:
              baseUrl: https://api.test
            incremental:
              source: response
              column: updated_at
            items:
              - request: { path: /a }
                landing: { target: abfss://datalakev2@acct.dfs.core.windows.net/raw/a, pathTemplate: "a" }
            """));

        Assert.Contains("only valid on a single-endpoint api flow", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Item_without_a_landing_is_rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            flowType: api
            name: bad
            source:
              baseUrl: https://api.test
            items:
              - request: { path: /a }
            """));

        Assert.Contains("items[0].landing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Http_item_without_a_request_is_rejected()
    {
        var ex = Assert.Throws<FlowValidationException>(() => Parse("""
            flowType: api
            name: bad
            source:
              baseUrl: https://api.test
            items:
              - landing: { target: abfss://datalakev2@acct.dfs.core.windows.net/raw/a, pathTemplate: "a" }
            """));

        Assert.Contains("items[0].request", ex.Message, StringComparison.Ordinal);
    }
}
