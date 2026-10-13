using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The name a node polls under. A launcher that starts its own workers names each one and recognises it by that name
/// when it first polls, so a configured name must reach every call the node makes, and a name the registry, the header
/// or a launcher could read differently must be refused before the node takes any work.
/// </summary>
public sealed class NodeIdentityTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredName_IsTheMachineName(string? configured)
        => Assert.Equal(Environment.MachineName, NodeIdentity.Resolve(configured).Name);

    [Theory]
    [InlineData("worker-heavy-0a1b2c3d4e")]
    [InlineData("w")]
    [InlineData("Node_7.eu-north")]
    public void AValidName_IsKept(string configured)
        => Assert.Equal(configured, NodeIdentity.Resolve(configured).Name);

    [Fact]
    public void AName_IsTrimmed()
        => Assert.Equal("worker-1", NodeIdentity.Resolve("  worker-1 ").Name);

    [Theory]
    [InlineData("-worker", "starts with a letter or digit")]
    [InlineData(".worker", "starts with a letter or digit")]
    [InlineData("worker 1", "holds ' '")]
    [InlineData("worker/1", "holds '/'")]
    [InlineData("wörker", "holds 'ö'")]
    [InlineData("worker:1", "holds ':'")]
    public void AnInvalidName_IsRefusedWithTheReason(string configured, string reason)
    {
        var ex = Assert.Throws<ArgumentException>(() => NodeIdentity.Resolve(configured));
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameLongerThanTheLimit_IsRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => NodeIdentity.Resolve(new string('a', NodeIdentity.MaxLength + 1)));
        Assert.Contains($"at most {NodeIdentity.MaxLength}", ex.Message, StringComparison.Ordinal);
        Assert.Equal(NodeIdentity.MaxLength, NodeIdentity.Resolve(new string('a', NodeIdentity.MaxLength)).Name.Length);
    }

    [Fact]
    public async Task AWorkerGivenAName_PollsUnderIt()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        var transport = new RecordingTransport();
        using var worker = new RunWorker(
            provider, transport, new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance,
            NodeIdentity.Resolve("worker-default-0123456789"));
        Assert.Equal("worker-default-0123456789", worker.NodeName);

        using var stop = new CancellationTokenSource();
        transport.OnPoll = () => stop.Cancel();
        await worker.RunAsync(new RunWorkerOptions { PollWait = TimeSpan.FromSeconds(1), DrainTimeout = TimeSpan.Zero }, stop.Token);

        var poll = Assert.Single(transport.Polls);
        Assert.Equal("worker-default-0123456789", poll.Node);
    }

    [Fact]
    public void AWorkerWithoutAName_IsTheMachine()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new RunWorker(
            provider, new RecordingTransport(), new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance);
        Assert.Equal(Environment.MachineName, worker.NodeName);
    }

    [Fact]
    public void TheContainer_GivesTheWorkerItsRegisteredName_AndTheMachineNameWithoutOne()
    {
        static RunWorker Resolve(NodeIdentity? identity)
        {
            var services = new ServiceCollection();
            services.AddSingleton(TimeProvider.System);
            services.AddLogging();
            services.AddSingleton<INodeTransport, RecordingTransport>();
            services.AddSingleton(sp => new DocumentExecutor(sp));
            if (identity is not null)
            {
                services.AddSingleton(identity);
            }

            services.AddSingleton<RunWorker>();
            return services.BuildServiceProvider().GetRequiredService<RunWorker>();
        }

        Assert.Equal("worker-x-1", Resolve(NodeIdentity.Resolve("worker-x-1")).NodeName);
        Assert.Equal(Environment.MachineName, Resolve(null).NodeName);
    }

    /// <summary>A dispatcher that records each poll and hands out nothing.</summary>
    private sealed class RecordingTransport : INodeTransport
    {
        public List<NodePollRequest> Polls { get; } = [];

        public Action? OnPoll { get; set; }

        public Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
        {
            lock (Polls)
            {
                Polls.Add(request);
            }

            OnPoll?.Invoke();
            return Task.FromResult(NodePollResponse.Empty(90));
        }

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct) => Task.FromResult<string?>(null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => Task.FromResult(new RunContextResponse(true, null, null));

        public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct) => Task.FromResult(true);

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(RunOutcomeStatus.Recorded);

        public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(true);

        public Task<FanOutResponse> EnqueueFanOutAsync(Guid rootRunId, FanOutRequest request, CancellationToken ct)
            => Task.FromResult(FanOutResponse.NotHeld);

        public Task<FanOutStateResponse> GetFanOutStateAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => Task.FromResult(FanOutStateResponse.NotHeld);

        public Task<FanOutCancelResponse> CancelFanOutAsync(Guid rootRunId, Guid groupId, FanOutFence fence, CancellationToken ct)
            => Task.FromResult(FanOutCancelResponse.NotHeld);
    }
}
