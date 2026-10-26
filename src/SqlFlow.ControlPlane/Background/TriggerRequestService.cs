using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Moves every open trigger request along (<see cref="TriggerRequestProcessor"/>), every
/// <see cref="ExternalTriggerOptions.PollSeconds"/>, and at once when a new request wakes it. Every control-plane replica
/// runs it: a request is leased to one replica at a time (<see cref="TriggerRequestStore.TryLeaseAsync"/>), so the
/// replicas share the open requests and a replica that stops leaves its requests to the others once their leases lapse.
/// </summary>
/// <remarks>
/// One request's failure (an unreadable catalog row, a git outage while checking a commit) is logged and written to that
/// request's trace, and the request is tried again on the next tick; it never stops the others or the loop. A request
/// whose passes fail <see cref="MaxConsecutiveFailures"/> times in a row on this replica is ended as failed with the
/// last error, so a fault that does not go away never keeps a caller waiting for ever.
/// </remarks>
public sealed partial class TriggerRequestService : BackgroundService
{
    private const int MaxPerTick = 50;

    /// <summary>How many passes in a row may fail before a request is ended as failed.</summary>
    internal const int MaxConsecutiveFailures = 20;

    private readonly Dictionary<Guid, int> _failures = [];

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly ExternalTriggerOptions _options;
    private readonly TriggerRequestSignal _wake;
    private readonly ILogger<TriggerRequestService> _logger;
    private readonly string _owner = $"{Environment.MachineName}/{Environment.ProcessId}/{Guid.NewGuid():N}";

    public TriggerRequestService(
        IServiceProvider services, TimeProvider clock, IOptions<ControlPlaneOptions> options, TriggerRequestSignal wake,
        ILogger<TriggerRequestService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _options = options.Value.ExternalTriggers;
        _wake = wake;
        _logger = logger;
    }

    /// <summary>How long a replica holds a request it is working on. Long enough for the slowest step of one pass (reading
    /// a repository's history to check a commit), and renewed right before every fire.</summary>
    internal TimeSpan LeaseFor => TimeSpan.FromSeconds(Math.Max(120, _options.PollSeconds * 10));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled();
            return;
        }

        var interval = TimeSpan.FromSeconds(_options.PollSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the container under the tick; nothing is left to do and logging would throw.
                break;
            }
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await _wake.WaitAsync(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>One pass over the open requests, oldest first. Internal so tests drive the service a tick at a time.</summary>
    internal async Task TickAsync(CancellationToken ct)
    {
        IReadOnlyList<Guid> open;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            open = await TriggerRequestStore.ListOpenAsync(catalog, MaxPerTick, ct).ConfigureAwait(false);
        }

        foreach (var id in open)
        {
            ct.ThrowIfCancellationRequested();
            await AdvanceOneAsync(id, ct).ConfigureAwait(false);
        }
    }

    private async Task AdvanceOneAsync(Guid id, CancellationToken ct)
    {
        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var now = _clock.GetUtcNow().UtcDateTime;
        if (!await TriggerRequestStore.TryLeaseAsync(catalog, id, _owner, now, now + LeaseFor, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var processor = scope.ServiceProvider.GetRequiredService<TriggerRequestProcessor>();
            await processor.AdvanceAsync(id, RenewAsync, ct).ConfigureAwait(false);
            _failures.Remove(id);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = SecretHygiene.RedactedMessage(ex);
            var failures = _failures[id] = _failures.GetValueOrDefault(id) + 1;
            LogRequestError(id, failures, error);
            await TraceErrorAsync(id, error, giveUp: failures >= MaxConsecutiveFailures).ConfigureAwait(false);
            if (failures >= MaxConsecutiveFailures)
            {
                _failures.Remove(id);
            }
        }
        finally
        {
            await ReleaseAsync(id).ConfigureAwait(false);
        }

        async Task<bool> RenewAsync(CancellationToken token)
        {
            await using var renewal = _services.CreateAsyncScope();
            var context = renewal.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var at = _clock.GetUtcNow().UtcDateTime;
            return await TriggerRequestStore.TryLeaseAsync(context, id, _owner, at, at + LeaseFor, token).ConfigureAwait(false);
        }
    }

    /// <summary>Writes a pass's failure into the request's own trace, so its caller sees it in its job log, and with
    /// <paramref name="giveUp"/> ends the request as failed; best effort on a fresh scope and a short deadline, since the
    /// pass's own context may be what failed.</summary>
    private async Task TraceErrorAsync(Guid id, string error, bool giveUp)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            if (await TriggerRequestStore.GetAsync(catalog, id, cts.Token).ConfigureAwait(false) is not { } record)
            {
                return;
            }

            var trace = await ActivityTrace.ResumeAsync(
                catalog, ActivityKinds.TriggerRequest, id.ToString("D"), record.Request.ActivityId, _clock, cts.Token).ConfigureAwait(false);
            if (!giveUp)
            {
                await trace.WarnAsync("retry", $"This step failed and is tried again shortly: {error}", cts.Token).ConfigureAwait(false);
                return;
            }

            var reason = $"Stopped after {MaxConsecutiveFailures} failed attempts in a row: {error}";
            if (await TriggerRequestStore.EndAsFailedAsync(catalog, id, reason, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false))
            {
                await trace.CompleteAsync(ActivityStatuses.Failed, reason, cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            LogTickError(SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task ReleaseAsync(Guid id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var scope = _services.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await TriggerRequestStore.ReleaseLeaseAsync(catalog, id, _owner, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The lease lapses on its own; this only lets another replica take the request a little sooner.
            LogTickError(SecretHygiene.RedactedMessage(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "External triggers are turned off (ControlPlane:ExternalTriggers:Enabled=false); trigger requests are not processed on this instance.")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Error, Message = "Trigger request scan failed: {Error}")]
    private partial void LogTickError(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Trigger request {RequestId} could not be moved along (attempt {Attempt} in a row): {Error}")]
    private partial void LogRequestError(Guid requestId, int attempt, string error);
}

/// <summary>Wakes this host's trigger request loop when a request is made, so its sync is asked for at once rather than
/// on the next tick. One pending wake covers any number of requests.</summary>
public sealed class TriggerRequestSignal : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    public void Wake()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; the tick it starts takes every open request.
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _pending.WaitAsync(timeout, ct);

    public void Dispose() => _pending.Dispose();
}
