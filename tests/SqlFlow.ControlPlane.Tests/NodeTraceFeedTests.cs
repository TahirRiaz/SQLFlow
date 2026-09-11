using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The node's live trace feed (<see cref="NodeTraceFeed"/>) over a recording transport, no database: statements
/// and events are batched in the run's own order with publication ordinals, a failure stamp travels after its
/// statement, disposal posts everything queued before it returns (the outcome report depends on that), large
/// volumes split into bounded batches, a retryable failure is retried and then delivered, a refused batch or a
/// final failure breaks the feed without ever blocking the run, and an abandoned run drops its pending batches
/// at once so a severed node never hangs on its own trace.
/// </summary>
public sealed class NodeTraceFeedTests
{
    private static readonly TimeSpan Fast = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan Never = TimeSpan.FromSeconds(30);

    /// <summary>A dispatcher that records every batch it accepts; <see cref="OnBatch"/> lets a test refuse or fail
    /// a post before it is recorded.</summary>
    private sealed class RecordingTransport : INodeTransport
    {
        private readonly Lock _gate = new();
        private int _calls;

        public List<RunTraceBatch> Batches { get; } = [];

        public Func<RunTraceBatch, Task<bool>>? OnBatch { get; set; }

        public int Calls => Volatile.Read(ref _calls);

        public async Task<bool> ReportTraceAsync(Guid runId, RunTraceBatch batch, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (OnBatch is { } hook && !await hook(batch))
            {
                return false;
            }

            lock (_gate)
            {
                Batches.Add(batch);
            }

            return true;
        }

        public Task<NodePollResponse> PollAsync(NodePollRequest request, CancellationToken ct)
            => Task.FromResult(NodePollResponse.Empty(90));

        public Task<string?> GetFlowVersionAsync(string contentHash, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task<RunContextResponse> ResolveRunContextAsync(Guid runId, RunContextRequest request, CancellationToken ct)
            => Task.FromResult(new RunContextResponse(true, null, null));

        public Task<RunOutcomeStatus> ReportRunOutcomeAsync(Guid runId, RunOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(RunOutcomeStatus.Recorded);

        public Task<bool> ReportTaskOutcomeAsync(Guid taskId, TaskOutcomeRequest request, CancellationToken ct)
            => Task.FromResult(true);
    }

    private static NodeTraceFeed Feed(
        RecordingTransport transport, TimeSpan? flush = null, TimeSpan? retryBase = null, CancellationToken abort = default)
        => new(transport, Guid.NewGuid(), "node-a", 1, NullLogger.Instance, abort, flush ?? Fast, retryBase ?? TimeSpan.FromMilliseconds(30));

    private static SqlTraceEntry Statement(int sequence, string sql = "SELECT 1")
        => new() { Sequence = sequence, Step = "staging.create", Sql = sql };

    private static FlowEvent Event(string message)
        => new() { Message = message, Level = FlowEventLevel.Info, Stage = "source.open", Rows = 3 };

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the condition did not hold within the timeout.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Feed_BatchesStatementsAndEvents_InOrder_WithPublicationOrdinals()
    {
        var transport = new RecordingTransport();
        var feed = Feed(transport);
        feed.Report(Statement(1));
        feed.Publish(Event("one"));
        feed.Report(Statement(2));
        feed.Publish(Event("two"));
        feed.ReportFailure(2, "boom");
        await feed.DisposeAsync();

        var batches = transport.Batches;
        Assert.NotEmpty(batches);
        Assert.All(batches, b => Assert.Equal(("node-a", 1), (b.Node, b.Attempt)));
        Assert.Equal([1, 2], batches.SelectMany(b => b.Statements).Select(s => s.Ordinal));
        Assert.Equal(["one", "two"], batches.SelectMany(b => b.Events).Select(e => e.Message));
        Assert.Equal([1, 2], batches.SelectMany(b => b.Events).Select(e => e.Ordinal));
        Assert.All(batches.SelectMany(b => b.Events), e => Assert.Equal(("info", "source.open", 3L), (e.Level, e.Step, e.Rows)));
        var failure = Assert.Single(batches.SelectMany(b => b.StatementFailures));
        Assert.Equal((2, "boom"), (failure.Ordinal, failure.Error));
        Assert.False(feed.IsBroken);
    }

    [Fact]
    public async Task Feed_FlushesEverythingQueued_BeforeDisposeReturns()
    {
        // With a flush interval no test would wait out, only disposal can have posted these.
        var transport = new RecordingTransport();
        var feed = Feed(transport, flush: Never);
        for (var i = 1; i <= 5; i++)
        {
            feed.Report(Statement(i));
        }

        var stopwatch = Stopwatch.StartNew();
        await feed.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "disposal waited out the flush interval instead of posting at once");
        Assert.Equal(Enumerable.Range(1, 5), transport.Batches.SelectMany(b => b.Statements).Select(s => s.Ordinal));
    }

    [Fact]
    public async Task Feed_SplitsLargeVolumes_IntoBoundedBatches_ByCountAndBySize()
    {
        var transport = new RecordingTransport();
        var feed = Feed(transport, flush: Never);
        for (var i = 1; i <= 450; i++)
        {
            feed.Report(Statement(i));
        }

        await feed.DisposeAsync();

        Assert.True(transport.Batches.Count >= 3, $"450 statements arrived in {transport.Batches.Count} batch(es)");
        Assert.All(transport.Batches, b => Assert.True(b.Statements.Count <= NodeProtocol.MaxTraceBatchItems));
        Assert.Equal(Enumerable.Range(1, 450), transport.Batches.SelectMany(b => b.Statements).Select(s => s.Ordinal));

        // Two statements that together exceed the character bound never share a batch; each travels alone.
        var big = new RecordingTransport();
        var bigFeed = Feed(big, flush: Never);
        bigFeed.Report(Statement(1, new string('x', (NodeProtocol.MaxTraceBatchChars / 2) + 1)));
        bigFeed.Report(Statement(2, new string('y', (NodeProtocol.MaxTraceBatchChars / 2) + 1)));
        await bigFeed.DisposeAsync();

        Assert.Equal(2, big.Batches.Count);
        Assert.All(big.Batches, b => Assert.Single(b.Statements));
    }

    [Fact]
    public async Task Feed_RetriesARetryableFailure_ThenDelivers()
    {
        var transport = new RecordingTransport();
        var failures = 0;
        transport.OnBatch = _ =>
        {
            var attempt = Interlocked.Increment(ref failures);
            return attempt switch
            {
                1 => throw new NodeTransportException("the control plane answered 503", 503, retryable: true),
                2 => throw new DispatchInactiveException("this replica does not own dispatch"),
                _ => Task.FromResult(true),
            };
        };
        var feed = Feed(transport);
        feed.Report(Statement(1));
        await feed.DisposeAsync();

        Assert.False(feed.IsBroken);
        Assert.Equal(3, transport.Calls);
        Assert.Equal(1, Assert.Single(transport.Batches).Statements[0].Ordinal);
    }

    [Fact]
    public async Task Feed_BreaksOnARefusedBatch_AndDropsWhatFollows()
    {
        var transport = new RecordingTransport();
        transport.OnBatch = _ => Task.FromResult(false);
        var feed = Feed(transport);
        feed.Report(Statement(1));
        await UntilAsync(() => feed.IsBroken, TimeSpan.FromSeconds(10));

        // Everything after the refusal is drained and discarded: the writers never block, nothing more is posted.
        feed.Report(Statement(2));
        feed.Publish(Event("late"));
        await feed.DisposeAsync();

        Assert.True(feed.IsBroken);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(transport.Batches);
    }

    [Fact]
    public async Task Feed_BreaksOnANonRetryableFailure_WithoutRetrying()
    {
        var transport = new RecordingTransport();
        transport.OnBatch = _ => throw new NodeTransportException("the control plane answered 400", 400, retryable: false);
        var feed = Feed(transport);
        feed.Report(Statement(1));
        await feed.DisposeAsync();

        Assert.True(feed.IsBroken);
        Assert.Equal(1, transport.Calls);
        Assert.Empty(transport.Batches);
    }

    [Fact]
    public async Task Feed_OfAnAbandonedRun_DropsPendingBatchesAtOnce()
    {
        // The control plane stays unreachable, so every post is retried; when the run is abandoned (the stopping
        // node's drain window expired) the feed must let go immediately rather than wait out its retry budget.
        using var abort = new CancellationTokenSource();
        var transport = new RecordingTransport();
        transport.OnBatch = _ => throw new NodeTransportException("the control plane could not be reached", null, retryable: true);
        var feed = Feed(transport, abort: abort.Token, retryBase: TimeSpan.FromSeconds(20));
        feed.Report(Statement(1));
        await UntilAsync(() => transport.Calls >= 1, TimeSpan.FromSeconds(10));

        await abort.CancelAsync();
        var stopwatch = Stopwatch.StartNew();
        await feed.DisposeAsync();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), "disposal waited on a retry the abandoned run no longer needs");
        Assert.True(feed.IsBroken);
        Assert.Empty(transport.Batches);
    }
}
