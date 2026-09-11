using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The stopping node's drain (<see cref="RunWorker.DrainInFlightAsync"/>). A stop signal is routine in an autoscaled
/// fleet - the scaler reclaims a replica, a revision swaps, an operator restarts a node - and it must mean "stop
/// claiming and finish what you hold", never "sever it". A severed run records no outcome at all, so it is recovered
/// only by the orphan reaper's requeue, which consumes one of its
/// <see cref="SqlFlow.Catalog.RunQueueStore.MaxExecutionAttempts"/> executions and repeats all of its work; three
/// such stops fail the run outright and blame it for dying though nothing was ever wrong with it. These tests pin
/// both halves of the contract: a stop lets in-flight work finish, and the drain is nonetheless bounded so a node
/// never outstays the termination grace period it was given.
/// </summary>
public sealed class RunWorkerDrainTests
{
    // The drain touches neither the catalog nor the engine: it waits on tasks the caller hands it, and its cancel
    // poll returns immediately while this worker holds no registered runs. So a bare provider is enough, and these
    // tests need no database (unlike the queue-store suite).
    private static RunWorker NewWorker()
    {
        var provider = new ServiceCollection().BuildServiceProvider();
        return new RunWorker(
            provider, new IdleTransport(), new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance);
    }

    /// <summary>A dispatcher with nothing to say: the drain's heartbeat polls it and gets an empty answer.</summary>
    private sealed class IdleTransport : INodeTransport
    {
        public Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
            => Task.FromResult(NodePollResponse.Empty(90));

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(RunOutcomeStatus.Recorded);

        public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(true);
    }

    [Fact]
    public async Task Drain_LetsInFlightWorkFinish_WithoutCancellingIt()
    {
        var worker = NewWorker();
        using var abort = new CancellationTokenSource();

        // Stands in for a run mid-statement when the stop arrives: it observes the abort token, so had the stop
        // been wired to sever in-flight work (the pre-fix behavior, where the run token was linked to the stopping
        // token), this would come back cancelled instead of completed.
        var severed = false;
        var inFlight = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(300), abort.Token);
            }
            catch (OperationCanceledException)
            {
                severed = true;
            }
        });

        await worker.DrainInFlightAsync([inFlight], TimeSpan.FromSeconds(30), abort);

        Assert.True(inFlight.IsCompleted);
        Assert.False(severed);
        Assert.False(abort.IsCancellationRequested);
    }

    [Fact]
    public async Task Drain_SeversWorkThatOutlastsTheWindow()
    {
        var worker = NewWorker();
        using var abort = new CancellationTokenSource();

        // Work that would outlast any grace period: the drain must give up on it rather than hold the process open
        // until the platform's kill lands, which would sever it anyway with nothing logged to say why.
        var severed = false;
        var inFlight = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, abort.Token);
            }
            catch (OperationCanceledException)
            {
                severed = true;
            }
        });

        await worker.DrainInFlightAsync([inFlight], TimeSpan.FromMilliseconds(250), abort);

        Assert.True(abort.IsCancellationRequested);
        Assert.True(severed);
    }

    [Fact]
    public async Task Drain_WithNoWindow_SeversImmediately()
    {
        var worker = NewWorker();
        using var abort = new CancellationTokenSource();

        var inFlight = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, abort.Token);
            }
            catch (OperationCanceledException)
            {
                // The caller asked for no drain at all; severing at once is the contract.
            }
        });

        await worker.DrainInFlightAsync([inFlight], TimeSpan.Zero, abort);

        Assert.True(abort.IsCancellationRequested);
        Assert.True(inFlight.IsCompleted);
    }
}
