using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Prunes the per-run SQL trace (<c>RunStatement</c> and <c>RunEvent</c>) on a cadence, so the two tables that
/// otherwise grow without bound stay bounded: it keeps each pipeline's latest run, every failed run, and anything
/// still inside the retention window, and deletes the rest (see <see cref="RunTraceStore"/>). The delete runs here,
/// on this service's own loop and its own catalog connection (a fresh DI scope), never on the request thread and
/// never inside a pipeline run: the live trace sinks only ever append, so nothing a running pipeline does pays the
/// cost of a delete. The GUI's "clean up now" runs the same prune synchronously through the maintenance endpoint
/// (so it can report the outcome and any error); this service is the hands-off automatic path.
/// </summary>
/// <remarks>
/// Hosted only when the automatic sweep is enabled (<c>ControlPlane:RunTrace:Enabled</c>), on every control-plane
/// replica like <c>OrphanRunReaper</c>; the batched, join-gated delete is idempotent and safe under concurrency, so
/// multiple replicas sweeping at once never conflict. The retention is read fresh each sweep (it is operator-tunable
/// from the GUI), and a null retention means keep forever, so the sweep does nothing. A sweep error (a transient
/// database outage) is logged, secret-redacted, and retried on the next tick; it never stops the loop.
/// </remarks>
public sealed partial class RunTraceReaper : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<RunTraceReaper> _logger;

    public RunTraceReaper(
        IServiceProvider services,
        TimeProvider clock,
        IOptions<ControlPlaneOptions> options,
        ILogger<RunTraceReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.RunTrace.PollSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the DI container out from under this tick; leave quietly.
                break;
            }
            catch (Exception ex)
            {
                LogSweepError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        // The retention is operator-tunable from the GUI, read fresh each sweep so a change takes effect without a
        // restart. Null means keep forever: the sweep does nothing.
        var days = await MaintenanceStore.GetRunTraceRetentionDaysAsync(catalog, ct).ConfigureAwait(false);
        if (days is not { } retentionDays)
        {
            return;
        }

        var supersededBefore = _clock.GetUtcNow().UtcDateTime - TimeSpan.FromDays(retentionDays);
        var deleted = await RunTraceStore.PruneStatementsAsync(catalog, supersededBefore, ct).ConfigureAwait(false);
        if (deleted > 0)
        {
            LogPruned(deleted, retentionDays);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Run-trace prune reclaimed {Statements} SQL statement row(s), keeping each pipeline's latest run, every failed run, and anything newer than {RetentionDays}d (run events are never pruned).")]
    private partial void LogPruned(int statements, int retentionDays);

    [LoggerMessage(Level = LogLevel.Error, Message = "Run-trace prune sweep error: {Error}")]
    private partial void LogSweepError(string error);
}
