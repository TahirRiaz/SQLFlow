using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Runs;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Execution;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A run's trace opens with where the executed flow came from: which commit, snapshot or synced copy, and the folder
/// its sibling files are read from. A file flow that reads data out of its repository reads the copy in that folder,
/// so this line is what tells an operator whether a file they just edited is the one the run saw. The worker is driven
/// here through a transport that hands out one run and records the trace the node posts, so the assertion is on what
/// the control plane actually receives.
/// </summary>
public sealed class RunWorkerOriginTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_origin_" + Guid.NewGuid().ToString("N"));

    public RunWorkerOriginTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "flows"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, "data", "rows.csv"), "Id\n1\n");

        // The target connection is an unset environment variable, so the run fails once it reaches the target; the
        // origin line is published before the run does anything, so the failure does not matter to the assertion.
        File.WriteAllText(Path.Combine(_root, "flows", "f.yaml"), """
            name: origin-flow
            source:
              type: csv
              location: ../data
            target:
              connection: ${env:SQLFLOW_ORIGIN_TEST_UNSET_CONNECTION}
              schema: dbo
              table: Origin
            """);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task AnUnpinnedRun_OpensItsTraceWithTheFolderItReads()
    {
        var transport = new OneRunTransport(new RunSpec(
            RepoId: Guid.NewGuid(), PipelineId: Guid.NewGuid(), FlowName: "origin-flow", RepoName: "estate",
            RepoRemoteUrl: null, RepoRootPath: _root, PipelineRelativePath: "flows/f.yaml", CommitSha: null,
            FlowVersionHash: null, Parameters: RunParameters.None, CredentialReference: null, CredentialUsername: null));

        var first = await RunOnceAsync(transport);

        Assert.Equal(1, first.Ordinal);
        Assert.Equal("run.origin", first.Step);
        Assert.Equal($"executing repository 'estate' as it stands in {_root} (no commit pinned)", first.Message);
    }

    /// <summary>Runs the worker until the handed-out run reports its outcome, then returns the first trace event the
    /// node posted for it.</summary>
    private static async Task<TraceEvent> RunOnceAsync(OneRunTransport transport)
    {
        var provider = new ServiceCollection().AddLogging().AddSqlFlowEngine().BuildServiceProvider();
        using var worker = new RunWorker(
            provider, transport, new DocumentExecutor(provider), TimeProvider.System, NullLogger<RunWorker>.Instance);
        using var stop = new CancellationTokenSource();
        var loop = worker.RunAsync(new RunWorkerOptions { PollWait = TimeSpan.FromSeconds(1) }, stop.Token);

        var reported = await Task.WhenAny(transport.Outcome, Task.Delay(TimeSpan.FromMinutes(2)));
        await stop.CancelAsync();
        await loop;

        Assert.True(reported == transport.Outcome, "The handed-out run never reported an outcome.");
        return transport.Events.OrderBy(e => e.Ordinal).First();
    }

    /// <summary>Hands out one run on the first poll, then nothing; records every trace event posted and completes
    /// <see cref="Outcome"/> when the run reports.</summary>
    private sealed class OneRunTransport(RunSpec spec) : INodeTransport
    {
        private readonly TaskCompletionSource<RunOutcomeRequest> _outcome = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentQueue<TraceEvent> _events = new();
        private int _handedOut;

        public Task<RunOutcomeRequest> Outcome => _outcome.Task;

        public IReadOnlyList<TraceEvent> Events => [.. _events];

        public async Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _handedOut, 1) == 0)
            {
                return new NodePollResponse([new RunHandout(Guid.NewGuid(), 1, spec)], [], [], [], [], [], false, 90);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            return NodePollResponse.Empty(90);
        }

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => Task.FromResult(new RunContextResponse(true, null, null));

        public Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
        {
            foreach (var traceEvent in batch.Events)
            {
                _events.Enqueue(traceEvent);
            }

            return Task.FromResult(true);
        }

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
        {
            _outcome.TrySetResult(request);
            return Task.FromResult(RunOutcomeStatus.Recorded);
        }

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
