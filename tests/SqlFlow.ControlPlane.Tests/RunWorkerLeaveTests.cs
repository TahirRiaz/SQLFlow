using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The stopping node's goodbye (<see cref="RunWorker.RunAsync"/>). Every worker pod polls under a fresh name, so a node
/// that stopped without a word stays on the fleet view as offline until the dispatcher's retention prunes it; with a
/// revision swap or a scale-in every few hours, that is a page of dead rows. A node that stops cleanly therefore says
/// so on its last poll, after the drain, and a node no dispatcher ever answered (nothing lists it) says nothing. The
/// goodbye is best-effort: a dispatcher that cannot be told never turns a clean stop into a failure.
/// </summary>
public sealed class RunWorkerLeaveTests
{
    private static RunWorker NewWorker(INodeTransport transport)
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new RunWorker(
            provider, transport, new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance,
            NodeIdentity.Resolve("leave-test-node"));
    }

    private static RunWorkerOptions Options() => new() { PollWait = TimeSpan.FromSeconds(1), DrainTimeout = TimeSpan.FromSeconds(5) };

    [Fact]
    public async Task AfterACleanStop_TheLastPollSaysTheNodeIsLeaving_HoldingNothingAndTakingNothing()
    {
        using var stop = new CancellationTokenSource();
        var transport = new ScriptedTransport(stop);
        using var worker = NewWorker(transport);

        await worker.RunAsync(Options(), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        var polls = transport.Polls.ToArray();
        Assert.Equal(2, polls.Length);
        Assert.False(polls[0].Leaving);
        var leave = polls[1];
        Assert.True(leave.Leaving);
        Assert.Equal(("leave-test-node", polls[0].StartedUtc), (leave.Node, leave.StartedUtc));
        Assert.Equal((0, 0, 0), (leave.FreeRunSlots, leave.FreeTaskSlots, leave.WaitSeconds));
        Assert.Empty(leave.HoldingRuns);
        Assert.Empty(leave.HoldingTasks);
    }

    [Fact]
    public async Task ANodeNoDispatcherEverAnswered_SaysNothingWhenItStops()
    {
        using var stop = new CancellationTokenSource();
        var transport = new ScriptedTransport(stop) { Refuse = true };
        using var worker = NewWorker(transport);

        await worker.RunAsync(Options(), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.DoesNotContain(transport.Polls, p => p.Leaving);
    }

    [Fact]
    public async Task AGoodbyeTheDispatcherRefuses_StillEndsInACleanStop()
    {
        using var stop = new CancellationTokenSource();
        var transport = new ScriptedTransport(stop) { RefuseLeave = true };
        using var worker = NewWorker(transport);

        await worker.RunAsync(Options(), stop.Token).WaitAsync(TimeSpan.FromSeconds(30));

        // A refusal that is not worth repeating is tried once, logged, and the node exits as it would have anyway.
        Assert.Single(transport.Polls, p => p.Leaving);
    }

    /// <summary>A dispatcher that answers the first poll empty and, as it does, stands in for the stop signal arriving;
    /// or one that refuses every poll as a passive replica would (<see cref="Refuse"/>); or one that answers the work
    /// polls but turns the goodbye away as a malformed request (<see cref="RefuseLeave"/>).</summary>
    private sealed class ScriptedTransport(CancellationTokenSource stop) : INodeTransport
    {
        public ConcurrentQueue<NodePollRequest> Polls { get; } = new();

        public bool Refuse { get; init; }

        public bool RefuseLeave { get; init; }

        public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
        {
            Polls.Enqueue(request);
            if (!request.Leaving)
            {
                await stop.CancelAsync();
            }

            if (Refuse)
            {
                throw new NodeTransportException("Dispatch is not active on this replica", 503, retryable: true);
            }

            if (request.Leaving && RefuseLeave)
            {
                throw new NodeTransportException("Bad request", 400, retryable: false);
            }

            return NodePollResponse.Empty(90);
        }

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => Task.FromResult(new RunContextResponse(true, null, null));

        public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
            => Task.FromResult(true);

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
