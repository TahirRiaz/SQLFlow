using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Maintenance;

/// <summary>
/// The Maintenance page's automatic cleanup: every <c>ControlPlane:Maintenance:PollSeconds</c> it has the
/// <see cref="MaintenanceRunner"/> apply the retention an admin set for each storage area. The deletes run here, on this
/// service's loop and its own scopes, never on a request thread or inside a run. An area with no retention set is never
/// touched, so until an admin sets one the pass removes nothing.
/// </summary>
/// <remarks>
/// Hosted when <c>ControlPlane:Maintenance:Enabled</c> is on (the default), on every replica like the notification service:
/// each cleanup deletes in bounded, idempotent statements, so replicas sweeping at once never remove a row twice. A pass that
/// fails (a database outage) is logged, redacted, and tried again on the next tick; it never stops the loop.
/// </remarks>
public sealed partial class MaintenanceReaper : BackgroundService
{
    private readonly MaintenanceRunner _runner;
    private readonly TimeSpan _pollInterval;
    private readonly ILogger<MaintenanceReaper> _logger;

    public MaintenanceReaper(MaintenanceRunner runner, IOptions<ControlPlaneOptions> options, ILogger<MaintenanceReaper> logger)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _runner = runner;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Maintenance.PollSeconds));
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunAutomaticAsync(stoppingToken).ConfigureAwait(false);
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
                LogPassError(SecretHygiene.RedactedMessage(ex));
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

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance pass error: {Error}")]
    private partial void LogPassError(string error);
}

/// <summary>
/// Checks the storage areas every contributor describes once, as the host starts and before it serves a request, so a
/// conflicting description (an id used twice, a table two areas hold, a retention on an area to keep) stops startup with a
/// message naming the contributor and the area, rather than misreporting the database on the Maintenance page. It reads the
/// descriptions only, never the database, which the bootstrap pass may still be migrating.
/// </summary>
internal sealed class StorageAreaStartupCheck : IHostedService
{
    private readonly IServiceScopeFactory _scopes;

    public StorageAreaStartupCheck(IServiceScopeFactory scopes)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        _scopes = scopes;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var problems = StorageAreaRules.Check(scope.ServiceProvider.GetServices<IStorageContributor>());
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The storage areas described for the Maintenance page are inconsistent:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
