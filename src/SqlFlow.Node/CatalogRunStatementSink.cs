using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SqlFlow.Catalog;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Node;

/// <summary>
/// The node-side live statement sink: as the engine generates each SQL statement, this persists a
/// <see cref="CatalogRunStatement"/> row to the catalog, so the GUI's Statements view streams in while the run
/// is still executing and the trace is durable even if this process dies before its <c>run.json</c> is written.
/// </summary>
/// <remarks>
/// Non-blocking by design: the engine's <see cref="Report"/> only enqueues, so a slow catalog never stalls the
/// run; a single background writer drains the queue in order on the sink's own catalog context (a fresh DI scope,
/// separate from the completion context, with its own connection). These rows are the durable, append-only trace:
/// completion keeps them under their stable ids and only appends any tail the feed missed, so the trace stream
/// delivers each statement exactly once. Best-effort by design: if a live write fails the feed stops and is logged
/// (never surfaced to the run), and completion fills the gap from <c>run.json</c>. Ordering is preserved, so the
/// failure update for a statement always lands after that statement's insert.
/// </remarks>
internal sealed class CatalogRunStatementSink : IRunStatementSink, IAsyncDisposable
{
    private readonly record struct Event(
        int Sequence, DateTime TimestampUtc, string Step, string Sql, string? Error, bool IsFailure);

    private readonly Guid _runId;
    private readonly Guid _repoId;
    private readonly ILogger _logger;
    private readonly AsyncServiceScope _scope;
    private readonly CatalogDbContext _catalog;
    private readonly Channel<Event> _channel =
        Channel.CreateUnbounded<Event>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _drain;

    // Once a live write throws (a transient catalog blip), the feed stops writing but keeps draining to completion,
    // so Report/ReportFailure never block and DisposeAsync always returns. The completion projection fills the gap.
    private bool _broken;

    public CatalogRunStatementSink(IServiceProvider services, Guid runId, Guid repoId, ILogger logger)
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

    public void Report(SqlTraceEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        // TryWrite on an unbounded channel only fails once the writer is completed (post-dispose); dropping then is
        // correct, and it never throws, honouring the "must not break the run" contract.
        _channel.Writer.TryWrite(new Event(entry.Sequence, entry.TimestampUtc, entry.Step, entry.Sql, entry.Error, IsFailure: false));
    }

    public void ReportFailure(int sequence, string errorMessage)
    {
        _channel.Writer.TryWrite(new Event(sequence, default, string.Empty, string.Empty, errorMessage, IsFailure: true));
    }

    public async ValueTask DisposeAsync()
    {
        // Stop accepting reports and wait for every queued write to land before returning: the caller disposes the
        // sink before the completion write-back, so the live rows are fully settled when it reconciles them against
        // the artifact (no row can arrive after completion reads the highest live ordinal).
        _channel.Writer.TryComplete();
        try
        {
            await _drain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Live statement feed for run {RunId} ended with an error: {Error}",
                _runId, SecretHygiene.RedactedMessage(ex));
        }

        await _scope.DisposeAsync().ConfigureAwait(false);
    }

    private async Task DrainAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var ev))
            {
                if (_broken)
                {
                    continue; // keep draining so the writer never blocks; the completion projection is authoritative
                }

                try
                {
                    if (ev.IsFailure)
                    {
                        // Stamp the error onto the already-inserted row for this ordinal (ordering guarantees it
                        // exists); a no-op if the insert was the write that broke the feed.
                        await _catalog.RunStatements
                            .Where(s => s.RunId == _runId && s.Ordinal == ev.Sequence)
                            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Error, ev.Error))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _catalog.RunStatements.Add(new CatalogRunStatement
                        {
                            RunId = _runId,
                            RepoId = _repoId,
                            Ordinal = ev.Sequence,
                            TimestampUtc = ev.TimestampUtc,
                            // The Step column is capped at 128 (see CatalogDbContext); match the projection's bound.
                            Step = ev.Step.Length > 128 ? ev.Step[..128] : ev.Step,
                            Sql = ev.Sql,
                            Error = ev.Error,
                        });
                        await _catalog.SaveChangesAsync().ConfigureAwait(false);
                        // The context is long-lived for the run; detach the saved row so it does not accumulate.
                        _catalog.ChangeTracker.Clear();
                    }
                }
                catch (Exception ex)
                {
                    _broken = true;
                    _logger.LogWarning("Live statement write for run {RunId} failed; the trace will still be "
                        + "recorded from the run artifact at completion: {Error}",
                        _runId, SecretHygiene.RedactedMessage(ex));
                }
            }
        }
    }
}
