using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.ManagedWorkers;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// Ticks the managed fleet (<see cref="ManagedFleet"/>) on its cadence. Hosted on every replica only when
/// <c>ControlPlane:ManagedWorkers:Enabled</c> is set; the fleet itself does nothing on a replica that does not own
/// dispatch, so exactly one replica starts and stops workers at a time. Constructing it resolves the fleet, which
/// resolves the configured launcher, so a host whose modules registered no launcher of that name refuses to start.
/// </summary>
public sealed partial class ManagedWorkersService : BackgroundService
{
    private readonly ManagedFleet _fleet;
    private readonly TimeProvider _clock;
    private readonly ILogger<ManagedWorkersService> _logger;

    public ManagedWorkersService(ManagedFleet fleet, TimeProvider clock, ILogger<ManagedWorkersService> logger)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _fleet = fleet;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        LogStarted(_fleet.LauncherName, (int)_fleet.Settings.Tick.TotalSeconds);
        using var timer = new PeriodicTimer(_fleet.Settings.Tick, _clock);
        do
        {
            try
            {
                await _fleet.TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // The fleet contains launcher and catalog failures itself; anything that still escapes is a defect worth
                // an error line, never a reason to stop managing workers.
                LogTickFailed(SecretHygiene.RedactedMessage(ex));
            }
        }
        while (await WaitAsync(timer, stoppingToken).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Registers the managed fleet and its loop when the feature is on. The fleet takes the launcher a module
    /// registered under the configured name and refuses, naming what is registered, when there is none.</summary>
    internal static void Register(IServiceCollection services, ManagedWorkersOptions options, DispatchOptions dispatch)
    {
        if (!options.Enabled)
        {
            return;
        }

        services.AddSingleton(sp =>
        {
            var name = options.Launcher!.Trim();
            var launchers = sp.GetServices<IWorkerLauncher>().ToList();
            var launcher = launchers.FirstOrDefault(l => string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"{ManagedWorkersOptions.Section}:Launcher is '{name}', but this host registers "
                    + (launchers.Count == 0
                        ? "no launcher at all"
                        : "only " + string.Join(", ", launchers.Select(l => $"'{l.Name}'")))
                    + "; managed workers cannot start.");

            // The build workers are named with: the deployment's, when the launcher's platform names one, else this
            // control plane's assemblies. A healthy worker polls at least once per long poll; a margin covers the round
            // trip and a busy control plane.
            var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ManagedWorkersService>();
            var (tag, source, known) = ControlPlaneBuild.Resolve(launcher.DeploymentBuild, AppContext.BaseDirectory);
            if (known)
            {
                LogBuild(log, tag, source);
            }
            else if (options.ReplaceWorkersOnStart)
            {
                LogBuildUnknown(log, source);
            }

            var settings = options.ToSettings(RunWorker.DefaultMaxConcurrentRuns, tag, dispatch.LongPoll + TimeSpan.FromSeconds(15));
            var clock = sp.GetRequiredService<TimeProvider>();
            var floors = new PoolFloorReader(sp.GetRequiredService<IServiceScopeFactory>(), clock);
            return new ManagedFleet(
                sp.GetRequiredService<Dispatcher>(), launcher, settings, floors.ReadAsync, clock,
                sp.GetRequiredService<ILogger<ManagedFleet>>());
        });
        services.AddHostedService<ManagedWorkersService>();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers are on: the '{Launcher}' launcher starts and stops this estate's workers, checked every {TickSeconds} s by the replica that owns dispatch.")]
    private partial void LogStarted(string launcher, int tickSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed workers: a tick failed unexpectedly ({Error}); the next tick tries again.")]
    private partial void LogTickFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed workers: workers started here carry build '{Tag}', from {Source}; a control plane of another build replaces them.")]
    private static partial void LogBuild(ILogger logger, string tag, string source);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Managed workers: the build of this control plane could not be read from {Source}, so a redeploy cannot be told from a restart and workers are not replaced on start (ReplaceWorkersOnStart has no effect); they are still replaced at their lifetime.")]
    private static partial void LogBuildUnknown(ILogger logger, string source);

    /// <summary>Reads each pool's floor as the fleet controls set it (the always-on floor, raised by an open manual
    /// override: <see cref="WorkerPoolStore.ResolveTarget"/> with no demand), at most once every few seconds.</summary>
    private sealed class PoolFloorReader(IServiceScopeFactory scopes, TimeProvider clock)
    {
        private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
        private IReadOnlyDictionary<string, int> _cached = new Dictionary<string, int>(StringComparer.Ordinal);
        private DateTime _readUtc = DateTime.MinValue;

        public async Task<IReadOnlyDictionary<string, int>> ReadAsync(CancellationToken ct)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (now - _readUtc < CacheFor)
            {
                return _cached;
            }

            await using var scope = scopes.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var rows = await WorkerPoolStore.ListDesiredAsync(catalog, ct).ConfigureAwait(false);
            _cached = rows.ToDictionary(r => r.Pool, r => WorkerPoolStore.ResolveTarget(0, r, now), StringComparer.Ordinal);
            _readUtc = now;
            return _cached;
        }
    }
}
