using SqlFlow.Acquire.Engine;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Acquire.Tests;

public sealed class ParamsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 9, 12, 0, 0, TimeSpan.Zero);

    private static AcquireFlow Flow(string landingDir, IReadOnlyDictionary<string, string?> declaredParams)
        => new()
        {
            Name = "Params_Flow",
            Items = [new AcquireItem
            {
                Source = new AcquireSource
                {
                    BaseUrl = "https://api.test.local",
                    Request = new AcquireRequest
                    {
                        Path = "/report",
                        Query = new Dictionary<string, string> { ["region"] = "{region}" },
                    },
                },
                Landing = new AcquireLanding { Target = landingDir, PathTemplate = "report_{region}" },
            }],
            Params = declaredParams,
        };

    [Fact]
    public async Task Declared_default_binds_and_runtime_value_overrides_it()
    {
        var handler = new StubHttpHandler().Json("/report", req => $"[{{\"r\":\"{req.RequestUri!.Query}\"}}]");
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(Now), out var dir);
        var flow = Flow(dir, new Dictionary<string, string?> { ["region"] = "rogaland" });

        var byDefault = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);
        Assert.True(byDefault.Success);
        Assert.Contains(handler.Requests, r => r.Uri.Query.Contains("region=rogaland", StringComparison.Ordinal));

        var overridden = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None,
            new AcquireRunOverrides { Params = new Dictionary<string, string> { ["region"] = "agder" } });
        Assert.True(overridden.Success);
        Assert.Contains(handler.Requests, r => r.Uri.Query.Contains("region=agder", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Missing_required_param_fails_before_any_request()
    {
        var handler = new StubHttpHandler().Json("/report", _ => "[]");
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(Now), out var dir);
        var flow = Flow(dir, new Dictionary<string, string?> { ["region"] = null });

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("region", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Undeclared_runtime_param_is_rejected()
    {
        var handler = new StubHttpHandler().Json("/report", _ => "[]");
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(Now), out var dir);
        var flow = Flow(dir, new Dictionary<string, string?> { ["region"] = "rogaland" });

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None,
            new AcquireRunOverrides { Params = new Dictionary<string, string> { ["regoin"] = "typo" } });

        Assert.False(result.Success);
        Assert.Contains("regoin", result.Error, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Backfill_window_overrides_date_window_iteration()
    {
        var handler = new StubHttpHandler().Json("/trips", _ => "[{\"t\":1}]");
        var engine = TestEngine.Create(handler, new FakeSecrets(), new FixedClock(Now), out var dir);
        var flow = new AcquireFlow
        {
            Name = "Backfill_Flow",
            Items = [new AcquireItem
            {
                Source = new AcquireSource
                {
                    BaseUrl = "https://api.test.local",
                    Request = new AcquireRequest
                    {
                        Path = "/trips",
                        Query = new Dictionary<string, string> { ["d"] = "{window.from:yyyy-MM-dd}" },
                    },
                    Iterations = [new AcquireIteration { Kind = AcquireIterationKind.DateWindow, From = "now-1d", To = "now" }],
                },
                Landing = new AcquireLanding { Target = dir, PathTemplate = "t_{window.from:yyyyMMdd}" },
            }],
        };

        var result = await engine.RunAsync(flow, Guid.NewGuid(), NullRunEventSink.Instance, null, CancellationToken.None,
            new AcquireRunOverrides
            {
                WindowFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                WindowTo = new DateTimeOffset(2026, 1, 4, 0, 0, 0, TimeSpan.Zero),
            });

        Assert.True(result.Success);
        Assert.Equal(3, result.Iterations); // 3 backfill days, not the flow's own 1-day window
        Assert.Contains(handler.Requests, r => r.Uri.Query.Contains("d=2026-01-01", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, r => r.Uri.Query.Contains("d=2026-01-03", StringComparison.Ordinal));
    }

    [Fact]
    public void Loader_parses_params_block_and_normalizes_blank_default_to_required()
    {
        var loader = new SqlFlow.Yaml.YamlAcquireFlowLoader();
        var flow = loader.Parse("""
            flowType: api
            name: P
            params:
              region: rogaland
              apiVersion: ""
            source:
              baseUrl: https://api.test.local
              request: { path: /x }
            landing:
              target: ./x
              pathTemplate: y
            """);

        Assert.Equal("rogaland", flow.Params["region"]);
        Assert.Null(flow.Params["apiVersion"]);
    }
}
