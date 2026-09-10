using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Node;

/// <summary>
/// The node-side live event sink: as the engine publishes each canonical run event (file progress, resolved
/// watermarks, engine decisions, stage summaries, warnings), this persists a <see cref="CatalogRunEvent"/> row to
/// the catalog, so the GUI's Events view streams in while the run is still executing and the timeline is durable
/// even if this process dies before its <c>run.json</c> is written.
/// </summary>
/// <remarks>
/// The exact twin of <see cref="CatalogRunStatementSink"/>, for the event stream. Non-blocking by design: the
/// engine's <see cref="Publish"/> only enqueues, so a slow catalog never stalls the run; a single background
/// writer drains the queue in order on the sink's own catalog context (a fresh DI scope, separate from the
/// completion context, with its own connection). These rows are the durable, append-only event log: completion
/// keeps them under their stable ids and only appends any tail the feed missed, so the trace stream delivers
/// each event exactly once. Best-effort by design: if a live write fails the feed stops and is logged (never
/// surfaced to the run), and completion fills the gap from the run.json <c>events</c> array. Ordinals are
/// assigned in publication order by the single reader, matching the artifact projection's ordinals so the tail
/// append aligns.
/// </remarks>
internal sealed class CatalogRunEventSink : IFlowEventSink, IAsyncDisposable
{
    private readonly Guid _runId;
    private readonly Guid _repoId;
    private readonly ILogger _logger;
    private readonly AsyncServiceScope _scope;
    private readonly CatalogDbContext _catalog;
    private readonly Channel<FlowEvent> _channel =
        Channel.CreateUnbounded<FlowEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _drain;

    // Once a live write throws (a transient catalog blip), the feed stops writing but keeps draining to completion,
    // so Publish never blocks and DisposeAsync always returns. The completion projection fills the gap.
    private bool _broken;

    public CatalogRunEventSink(IServiceProvider services, Guid runId, Guid repoId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(logger);
        _runId = runId;
        _repoId = repoId;
        _logger = logger;
        _scope = services.CreateAsyncScope();
        _catalog = _scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        _drain = DrainAsync();
    }

    public void Publish(FlowEvent flowEvent)
    {
        ArgumentNullException.ThrowIfNull(flowEvent);
        // TryWrite on an unbounded channel only fails once the writer is completed (post-dispose); dropping then is
        // correct, and it never throws, honouring the "must not break the run" sink contract.
        _channel.Writer.TryWrite(flowEvent);
    }

    public async ValueTask DisposeAsync()
    {
        // Stop accepting events and wait for every queued write to land before returning: the caller disposes the
        // sink before the completion write-back, so the live rows are fully settled when it reconciles them against
        // the artifact (no row can arrive after completion reads the highest live ordinal).
        _channel.Writer.TryComplete();
        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Live event feed for run {RunId} ended with an error: {Error}",
                _runId, SecretHygiene.RedactedMessage(ex));
        }

        await _scope.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        var ordinal = 0;
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var ev))
            {
                // The ordinal counts every published event, even past a broken feed, so the completion
                // re-projection and a partially-written live feed agree on each event's position.
                ordinal++;
                if (_broken)
                {
                    continue; // keep draining so the writer never blocks; the completion projection is authoritative
                }

                try
                {
                    _catalog.RunEvents.Add(new CatalogRunEvent
                    {
                        RunId = _runId,
                        RepoId = _repoId,
                        Ordinal = ordinal,
                        TimestampUtc = ev.Timestamp.UtcDateTime,
                        Level = RunEventLevels.Name(ev.Level),
                        // Step is capped at 128 (see CatalogDbContext); match the projection's bound.
                        Step = ev.Stage is { } stage ? (stage.Length > 128 ? stage[..128] : stage) : null,
                        Message = ev.Message,
                        Rows = ev.Rows,
                        ElapsedMs = ev.ElapsedMs,
                    });
                    await _catalog.SaveChangesAsync().ConfigureAwait(false);
                    // The context is long-lived for the run; detach the saved row so it does not accumulate.
                    _catalog.ChangeTracker.Clear();
                }
                catch (Exception ex)
                {
                    _broken = true;
                    _logger.LogWarning("Live event write for run {RunId} failed; the timeline will still be "
                        + "recorded from the run artifact at completion: {Error}",
                        _runId, SecretHygiene.RedactedMessage(ex));
                }
            }
        }
    }
}
