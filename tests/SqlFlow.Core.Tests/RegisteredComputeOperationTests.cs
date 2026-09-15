using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Compute;
using SqlFlow.Execution;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Compute operations a host module registers beside the built-in datasource operations: which names a registration may
/// carry, how a payload of a registered operation is validated (a transport contract of a target reference and bounded
/// arguments) and round-trips through the queue, and how the executor built by the engine's own registration dispatches
/// to it, refuses a bad registration, and bounds its result.
/// </summary>
public sealed class RegisteredComputeOperationTests
{
    private static readonly string[] Registered = ["echoArgs"];

    [Theory]
    [InlineData("echoArgs")]
    [InlineData("probe-read2")]
    [InlineData("x")]
    public void AValidRegisteredName_IsAccepted(string name) => Assert.True(ComputeOperations.IsValidRegisteredName(name));

    [Theory]
    [InlineData("EchoArgs")]
    [InlineData("1probe")]
    [InlineData("probe_read")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("testConnection")]
    [InlineData("TESTCONNECTION")]
    [InlineData("an-operation-name-that-is-far-too-long-to-register")]
    public void AnInvalidOrBuiltInName_IsRefused(string? name) => Assert.False(ComputeOperations.IsValidRegisteredName(name));

    [Fact]
    public void ARegisteredOperationsPayload_ValidatesAgainstTheRegisteredNames_AndNotWithout()
    {
        var payload = new ComputeTaskPayload
        {
            Operation = "echoArgs",
            SourceRef = "flow:wells",
            Arguments = new Dictionary<string, string> { ["key"] = "value" },
        };

        payload.Validate(Registered);

        var ex = Assert.Throws<SqlFlowException>(() => payload.Validate());
        Assert.Contains("Unknown compute operation 'echoArgs'", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownOperation_NamesTheRegisteredOperationsBesideTheBuiltInOnes()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => new ComputeTaskPayload { Operation = "nope", SourceRef = "x" }.Validate(Registered));
        Assert.Contains("testConnection", ex.Message, StringComparison.Ordinal);
        Assert.Contains("echoArgs", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ABuiltInOperation_RefusesArguments()
    {
        var ex = Assert.Throws<SqlFlowException>(() => new ComputeTaskPayload
        {
            Operation = ComputeOperations.TestConnection,
            SourceRef = "${env:SQLFLOW_SOURCE}",
            Arguments = new Dictionary<string, string> { ["a"] = "1" },
        }.Validate(Registered));
        Assert.Contains("takes its typed fields, not arguments", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredOperation_NeedsATargetReference()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => new ComputeTaskPayload { Operation = "echoArgs", SourceRef = " " }.Validate(Registered));
        Assert.Contains("requires a target reference", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARegisteredOperation_BoundsItsArguments()
    {
        var tooMany = Enumerable.Range(0, ComputeTaskPayload.MaxArguments + 1).ToDictionary(i => $"a{i}", _ => "x");
        Assert.Throws<SqlFlowException>(
            () => new ComputeTaskPayload { Operation = "echoArgs", SourceRef = "x", Arguments = tooMany }.Validate(Registered));

        var tooLong = new Dictionary<string, string> { ["a"] = new string('x', ComputeTaskPayload.MaxArgumentLength + 1) };
        Assert.Throws<SqlFlowException>(
            () => new ComputeTaskPayload { Operation = "echoArgs", SourceRef = "x", Arguments = tooLong }.Validate(Registered));

        var badName = new Dictionary<string, string> { ["bad\nname"] = "x" };
        Assert.Throws<SqlFlowException>(
            () => new ComputeTaskPayload { Operation = "echoArgs", SourceRef = "x", Arguments = badName }.Validate(Registered));
    }

    [Fact]
    public void ARegisteredOperationsPayload_RoundTripsThroughTheQueueRow()
    {
        var json = new ComputeTaskPayload
        {
            Operation = "echoArgs",
            SourceRef = "flow:wells",
            Arguments = new Dictionary<string, string> { ["key"] = "value" },
        }.ToJson();

        var back = ComputeTaskPayload.FromJson(json, Registered);
        Assert.Equal("value", back.RequireArgument("key"));
        Assert.Null(back.Argument("missing"));
        Assert.Throws<SqlFlowException>(() => ComputeTaskPayload.FromJson(json));
    }

    [Fact]
    public async Task TheEngineBuiltExecutor_DispatchesToARegisteredOperation()
    {
        using var provider = Provider(new EchoArgsOperation());
        var executor = provider.GetRequiredService<ComputeTaskExecutor>();

        Assert.Contains("echoArgs", executor.RegisteredOperations);
        var result = await executor.ExecuteAsync(new ComputeTaskPayload
        {
            Operation = "echoArgs",
            SourceRef = "flow:wells",
            Arguments = new Dictionary<string, string> { ["key"] = "value" },
        }, CancellationToken.None);

        using var document = JsonDocument.Parse(result);
        Assert.Equal("flow:wells", document.RootElement.GetProperty("target").GetString());
        Assert.Equal("value", document.RootElement.GetProperty("key").GetString());
    }

    [Fact]
    public async Task ARegisteredOperationsOwnRefusal_Surfaces()
    {
        using var provider = Provider(new EchoArgsOperation());
        var ex = await Assert.ThrowsAsync<SqlFlowException>(() => provider.GetRequiredService<ComputeTaskExecutor>().ExecuteAsync(
            new ComputeTaskPayload { Operation = "echoArgs", SourceRef = "flow:wells" }, CancellationToken.None));
        Assert.Contains("requires the 'key' argument", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOversizedOrMissingResult_FailsTheTask()
    {
        using var huge = Provider(new FixedResultOperation("hugeResult", new string('x', 8_000_001)));
        var tooLarge = await Assert.ThrowsAsync<SqlFlowException>(() => huge.GetRequiredService<ComputeTaskExecutor>().ExecuteAsync(
            new ComputeTaskPayload { Operation = "hugeResult", SourceRef = "x" }, CancellationToken.None));
        Assert.Contains("over the 8000000-character limit", tooLarge.Message, StringComparison.Ordinal);

        using var none = Provider(new FixedResultOperation("noResult", null));
        var missing = await Assert.ThrowsAsync<SqlFlowException>(() => none.GetRequiredService<ComputeTaskExecutor>().ExecuteAsync(
            new ComputeTaskPayload { Operation = "noResult", SourceRef = "x" }, CancellationToken.None));
        Assert.Contains("returned no result", missing.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("testConnection")]
    [InlineData("Invalid")]
    public void ARegistrationWithAnInvalidOrBuiltInName_IsRefused(string name)
    {
        using var provider = Provider(new FixedResultOperation(name, "{}"));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ComputeTaskExecutor>());
        Assert.Contains("not a valid registered operation name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoRegistrationsOfOneName_AreRefused()
    {
        using var provider = Provider(new FixedResultOperation("same", "{}"), new FixedResultOperation("same", "{}"));
        var ex = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ComputeTaskExecutor>());
        Assert.Contains("registered by both", ex.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider Provider(params IComputeOperation[] operations)
    {
        var services = new ServiceCollection().AddLogging().AddSqlFlowEngine();
        foreach (var operation in operations)
        {
            services.AddSingleton(operation);
        }

        return services.BuildServiceProvider();
    }

    private sealed class EchoArgsOperation : IComputeOperation
    {
        public string Name => "echoArgs";

        public Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct)
            => Task.FromResult(JsonSerializer.Serialize(new { target = payload.SourceRef, key = payload.RequireArgument("key") }));
    }

    private sealed class FixedResultOperation(string name, string? result) : IComputeOperation
    {
        public string Name => name;

        public Task<string> ExecuteAsync(ComputeTaskPayload payload, CancellationToken ct) => Task.FromResult(result!);
    }
}
