using Microsoft.Extensions.Options;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch;

namespace SqlFlow.ControlPlane.Dispatch;

/// <summary>
/// Hosts the <see cref="Dispatcher"/>: arbitrates ownership through the ledger's lease so exactly one control-plane
/// replica dispatches at a time, activates the dispatcher (rebuilding memory from the journal) when this replica
/// wins, deactivates it when the lease is lost, and drives the housekeeping tick about once a second while active.
/// On a graceful stop the lease is released at once, so a revision swap hands over within one renew interval
/// rather than waiting out the TTL; only a crash leaves the successor waiting.
/// </summary>
public sealed partial class DispatchService : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly IDispatchLedger _ledger;
    private readonly DispatchOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<DispatchService> _logger;
    private readonly string _owner;

    public DispatchService(
        Dispatcher dispatcher, IDispatchLedger ledger, IOptions<ControlPlaneOptions> options, TimeProvider clock,
        ILogger<DispatchService> logger)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _dispatcher = dispatcher;
        _ledger = ledger;
        _options = options.Value.Dispatch;
        _clock = clock;
        _logger = logger;
        // Unique per process incarnation: a restarted replica under the same host name must not be mistaken for
        // the previous owner, and a same-name renewal must never resurrect a dead incarnation's hold. Bounded to
        // the lease row's column width whatever the host name is.
        var host = Environment.MachineName;
        var owner = $"{host}/{Environment.ProcessId}/{Guid.NewGuid():N}";
        _owner = owner.Length <= 256 ? owner : owner[..256];
    }

    /// <summary>This replica's ownership identity, as it appears on the lease row and in the dispatch snapshot.</summary>
    public string Owner => _owner;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextOwnershipAttempt = _clock.GetUtcNow();
        DateTime? lastRenewedUtc = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var now = _clock.GetUtcNow();
                if (now >= nextOwnershipAttempt)
                {
                    nextOwnershipAttempt = now + _options.OwnershipRenew;
                    bool held;
                    try
                    {
                        held = await _ledger.TryAcquireOwnershipAsync(_owner, now.UtcDateTime, _options.OwnershipTtl, stoppingToken)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        // The ledger is unreachable: keep serving only while the last successful renewal still
                        // covers this moment; past the TTL this replica can no longer be sure it is the owner.
                        LogOwnershipCheckFailed(SecretHygiene.RedactedMessage(ex));
                        held = _dispatcher.IsActive && lastRenewedUtc is { } renewed
                            && now.UtcDateTime - renewed < _options.OwnershipTtl;
                        if (!held && _dispatcher.IsActive)
                        {
                            LogOwnershipLost(_owner, "the lease could not be renewed before it expired");
                            _dispatcher.Deactivate();
                        }

                        held = false;
                    }

                    if (held)
                    {
                        lastRenewedUtc = now.UtcDateTime;
                        if (!_dispatcher.IsActive)
                        {
                            await _dispatcher.ActivateAsync(_owner, stoppingToken).ConfigureAwait(false);
                        }
                    }
                    else if (_dispatcher.IsActive && lastRenewedUtc is { } last && now.UtcDateTime - last >= _options.OwnershipTtl)
                    {
                        LogOwnershipLost(_owner, "another replica holds the lease");
                        _dispatcher.Deactivate();
                    }
                }

                if (_dispatcher.IsActive)
                {
                    await _dispatcher.TickAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown disposed the container under this tick; there is nothing left to do.
                break;
            }
            catch (Exception ex)
            {
                LogTickError(SecretHygiene.RedactedMessage(ex));
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        if (!_dispatcher.IsActive)
        {
            return;
        }

        // A graceful stop: stop serving, then free the lease so the successor takes over at once. The release runs
        // on its own short deadline because the stopping token is already tripped.
        _dispatcher.Deactivate();
        using var release = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _ledger.ReleaseOwnershipAsync(_owner, release.Token).ConfigureAwait(false);
            LogOwnershipReleased(_owner);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOwnershipReleaseFailed(SecretHygiene.RedactedMessage(ex), _options.OwnershipTtlSeconds);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dispatch ownership check failed: {Error}")]
    private partial void LogOwnershipCheckFailed(string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dispatch ownership lost by '{Owner}': {Reason}. This replica no longer serves nodes.")]
    private partial void LogOwnershipLost(string owner, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Dispatch ownership released by '{Owner}' on shutdown.")]
    private partial void LogOwnershipReleased(string owner);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dispatch ownership could not be released on shutdown ({Error}); the successor acquires it after the {TtlSeconds}s TTL.")]
    private partial void LogOwnershipReleaseFailed(string error, int ttlSeconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Dispatch service tick error: {Error}")]
    private partial void LogTickError(string error);
}
