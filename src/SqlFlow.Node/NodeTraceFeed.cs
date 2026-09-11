using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Node;

/// <summary>
/// The node-side live trace feed: as the engine generates each SQL statement and publishes each canonical run
/// event (file progress, resolved watermarks, engine decisions, stage summaries, warnings), this streams them to
/// the control plane in batches over the node protocol, so the GUI's Statements and Events views update while the
/// run is still executing and the trace is durable even if this process dies before its <c>run.json</c> is
/// written. One feed serves both streams of one run; it is the run's <see cref="IRunStatementSink"/> and its
/// <see cref="IFlowEventSink"/> at once.
/// </summary>
/// <remarks>
/// Non-blocking by design: <see cref="Report"/> and <see cref="Publish"/> only enqueue, so a slow or unreachable
/// control plane never stalls the run; a single background drain packs what has accumulated into a batch every
/// <see cref="NodeProtocol.MaxTraceBatchItems"/> items, <see cref="NodeProtocol.MaxTraceBatchChars"/> characters or
/// flush interval, whichever comes first, and posts it under the hand-out's fence. A retryable failure (the
/// control plane unreachable, a passive replica, a timeout) is retried with backoff inside a bounded budget; a
/// refused batch (the run no longer carries this node's lease) or a final failure breaks the feed, which then
/// keeps draining and discarding so the writer never blocks and disposal always returns. The completion projection
/// fills whatever gap a broken feed left from the authoritative artifact, keyed on the highest ordinal the feed
/// wrote, so event ordinals are assigned here in publication order exactly as the artifact assigns them. Disposal
/// flushes everything queued before returning, so the caller's outcome report always follows the last batch.
/// </remarks>
internal sealed partial class NodeTraceFeed : IRunStatementSink, IFlowEventSink, IAsyncDisposable
{
    /// <summary>How long the drain waits for more items before posting what it has.</summary>
    internal static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>How long one batch keeps being retried against a control plane that answers retryably (a passive
    /// replica during an ownership hand-over, a restart) before the feed gives up on the run's live trace.</summary>
    internal static readonly TimeSpan RetryBudget = TimeSpan.FromSeconds(60);

    /// <summary>The first wait before a retry; each further retry doubles it up to <see cref="MaxRetryDelay"/>.</summary>
    internal static readonly TimeSpan DefaultRetryBaseDelay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(10);

    private readonly INodeTransport _transport;
    private readonly Guid _runId;
    private readonly string _node;
    private readonly int _attempt;
    private readonly ILogger _logger;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _retryBaseDelay;
    private readonly CancellationToken _abort;
    private readonly Channel<TraceItem> _channel =
        Channel.CreateUnbounded<TraceItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _drain;

    // Assigned in publication order, counting every published event even past a broken feed, so the completion
    // re-projection and a partially-written live feed agree on each event's position.
    private int _eventOrdinal;

    // Once a batch is refused or finally fails, the feed stops posting but keeps draining, so the writers never
    // block and DisposeAsync always returns. The completion projection is authoritative from that point.
    private bool _broken;

    /// <param name="transport">The node's transport to the dispatcher.</param>
    /// <param name="runId">The run whose trace this feeds.</param>
    /// <param name="node">This node's name, the first half of the fence.</param>
    /// <param name="attempt">The attempt the hand-out carried, the second half of the fence.</param>
    /// <param name="logger">Where a broken feed is reported.</param>
    /// <param name="abort">Trips when the run is being abandoned (a stopping node's drain window expired): pending
    /// batches are then dropped rather than retried, so disposal returns at once.</param>
    /// <param name="flushInterval">How long the drain waits for more items before posting; the default in production,
    /// shorter in tests.</param>
    /// <param name="retryBaseDelay">The first wait before a retried post; the default in production, shorter in tests.</param>
    public NodeTraceFeed(
        INodeTransport transport, Guid runId, string node, int attempt, ILogger logger, CancellationToken abort,
        TimeSpan? flushInterval = null, TimeSpan? retryBaseDelay = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(logger);
        _transport = transport;
        _runId = runId;
        _node = node;
        _attempt = attempt;
        _logger = logger;
        _abort = abort;
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _retryBaseDelay = retryBaseDelay ?? DefaultRetryBaseDelay;
        _drain = DrainAsync();
    }

    /// <summary>Whether a batch was refused or finally failed, after which nothing more is posted.</summary>
    public bool IsBroken => _broken;

    public void Report(SqlTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // TryWrite on an unbounded channel only fails once the writer is completed (post-dispose); dropping then is
        // correct, and it never throws, honouring the "must not break the run" sink contract.
        _channel.Writer.TryWrite(TraceItem.Of(
            new TraceStatement(entry.Sequence, entry.TimestampUtc, entry.Step, entry.Sql, entry.Error)));
    }

    public void ReportFailure(int sequence, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(errorMessage);
        _channel.Writer.TryWrite(TraceItem.Of(new TraceStatementFailure(sequence, errorMessage)));
    }

    public void Publish(FlowEvent flowEvent)
    {
        ArgumentNullException.ThrowIfNull(flowEvent);
        var ordinal = Interlocked.Increment(ref _eventOrdinal);
        _channel.Writer.TryWrite(TraceItem.Of(new TraceEvent(
            ordinal, flowEvent.Timestamp.UtcDateTime, RunEventLevels.Name(flowEvent.Level), flowEvent.Stage,
            flowEvent.Message, flowEvent.Rows, flowEvent.ElapsedMs)));
    }

    public async ValueTask DisposeAsync()
    {
        // Stop accepting items and wait for every queued batch to be posted before returning: the caller disposes
        // the feed before the outcome report, whose projection keeps these live rows and appends only the tail they
        // missed, so the feed must be fully settled when the control plane reads the highest live ordinal.
        _channel.Writer.TryComplete();
        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEnded(_runId, SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        var batch = new List<TraceItem>(NodeProtocol.MaxTraceBatchItems);
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            var chars = 0L;
            var deadline = Stopwatch.GetTimestamp() + (long)(_flushInterval.TotalSeconds * Stopwatch.Frequency);
            while (batch.Count < NodeProtocol.MaxTraceBatchItems)
            {
                // Peek before taking: an item that would push the batch past its character bound waits for the next
                // batch (this drain is the channel's only reader, so the peeked item is still there to read). A
                // single item larger than the bound travels alone.
                if (reader.TryPeek(out var next))
                {
                    if (batch.Count > 0 && chars + next.Chars > NodeProtocol.MaxTraceBatchChars)
                    {
                        break;
                    }

                    reader.TryRead(out var item);
                    batch.Add(item!);
                    chars += item!.Chars;
                    continue;
                }

                // Nothing more is queued right now: wait for the next item or the flush interval, whichever first,
                // so a chatty run posts full batches and a quiet one still lands each entry within the interval.
                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    break;
                }

                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency));
                try
                {
                    if (!await reader.WaitToReadAsync(timeout.Token).ConfigureAwait(false))
                    {
                        break; // the writer completed: post what we have and finish
                    }
                }
                catch (OperationCanceledException)
                {
                    break; // the interval elapsed
                }
            }

            if (batch.Count > 0)
            {
                await PostAsync(batch).ConfigureAwait(false);
            }
        }
    }

    private async Task PostAsync(List<TraceItem> items)
    {
        if (_broken)
        {
            return; // keep draining so the writers never block; the completion projection is authoritative
        }

        var batch = new RunTraceBatch(
            _node, _attempt,
            items.Where(i => i.Statement is not null).Select(i => i.Statement!).ToList(),
            items.Where(i => i.Failure is not null).Select(i => i.Failure!).ToList(),
            items.Where(i => i.Event is not null).Select(i => i.Event!).ToList());

        var started = Stopwatch.GetTimestamp();
        var retries = 0;
        while (true)
        {
            try
            {
                if (!await _transport.ReportTraceAsync(_runId, batch, _abort).ConfigureAwait(false))
                {
                    _broken = true;
                    LogRefused(_runId, _attempt);
                }

                return;
            }
            catch (OperationCanceledException) when (_abort.IsCancellationRequested)
            {
                // The run is being abandoned: nothing it streamed matters any more, and the lease expiry requeues it.
                _broken = true;
                return;
            }
            catch (Exception ex) when (NodeTransportException.IsRetryable(ex)
                                       && Stopwatch.GetElapsedTime(started) < RetryBudget)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(
                    MaxRetryDelay.TotalMilliseconds,
                    (_retryBaseDelay.TotalMilliseconds * Math.Pow(2, retries)) + Random.Shared.Next(0, 250)));
                retries++;
                try
                {
                    await Task.Delay(delay, _abort).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _broken = true;
                    return;
                }
            }
            catch (Exception ex)
            {
                _broken = true;
                LogBroken(_runId, SecretHygiene.RedactedMessage(ex));
                return;
            }
        }
    }

    /// <summary>One queued entry: exactly one of the three is set. <see cref="Chars"/> is the text it carries, for
    /// the size-based flush.</summary>
    private sealed record TraceItem(TraceStatement? Statement, TraceStatementFailure? Failure, TraceEvent? Event, int Chars)
    {
        public static TraceItem Of(TraceStatement statement)
            => new(statement, null, null, statement.Sql.Length + statement.Step.Length + (statement.Error?.Length ?? 0));

        public static TraceItem Of(TraceStatementFailure failure) => new(null, failure, null, failure.Error.Length);

        public static TraceItem Of(TraceEvent runEvent)
            => new(null, null, runEvent, runEvent.Message.Length + (runEvent.Step?.Length ?? 0));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: live trace feed stopped, the dispatcher no longer honors this node's lease at attempt {Attempt}; the run's trace is left to whichever execution now holds it.")]
    private partial void LogRefused(Guid runId, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: live trace feed stopped after a batch could not be delivered ({Error}); the trace will still be recorded from the run artifact at completion.")]
    private partial void LogBroken(Guid runId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Run {RunId}: live trace feed ended with an error: {Error}")]
    private partial void LogEnded(Guid runId, string error);
}
