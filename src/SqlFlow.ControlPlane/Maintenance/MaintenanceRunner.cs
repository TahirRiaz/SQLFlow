using System.Collections.Concurrent;
using SqlFlow.Catalog;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Maintenance;

/// <summary>An area found among the contributors, with the contributor that describes and cleans it.</summary>
public sealed record FoundStorageArea(IStorageContributor Contributor, StorageArea Area);

/// <summary>Finds storage areas among the registered contributors.</summary>
public static class StorageAreas
{
    /// <summary>The area <paramref name="areaId"/> and its contributor, or null when no contributor describes it.</summary>
    public static FoundStorageArea? Find(IEnumerable<IStorageContributor> contributors, string areaId)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        foreach (var contributor in contributors)
        {
            foreach (var area in contributor.Areas)
            {
                if (string.Equals(area.Id, areaId, StringComparison.Ordinal))
                {
                    return new FoundStorageArea(contributor, area);
                }
            }
        }

        return null;
    }
}

/// <summary>Why a cleanup was not started, as the endpoint answers it.</summary>
public enum CleanupRefusal
{
    /// <summary>No contributor describes the area.</summary>
    UnknownArea,

    /// <summary>The area takes no retention, so nothing cleans it from the Maintenance page.</summary>
    NotCleanable,

    /// <summary>The days lie outside what the area takes.</summary>
    DaysOutOfRange,

    /// <summary>A cleanup of the area is already running on this control plane.</summary>
    AlreadyRunning,
}

/// <summary>What a request for a cleanup came to: the entry of the cleanup it started, or why it started none.</summary>
public sealed record CleanupStart(CatalogMaintenanceCleanup? Entry, CleanupRefusal? Refusal, string? Message);

/// <summary>
/// Runs the cleanups of storage areas: an admin's "clean up now" from the Maintenance page and the automatic pass that applies
/// the retention an admin set. Both come through here, so they run the same contributor code, write the same log and never run
/// two cleanups of one area at once on this control plane. A cleanup asked for runs in the background, on a scope of its own,
/// and its log entry is open until it ends, so the page can follow it however long a large backlog takes.
/// </summary>
public sealed partial class MaintenanceRunner
{
    /// <summary>How long a cleanup entry may stay open before any control plane takes it for one whose host went away.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(12);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<MaintenanceRunner> _logger;
    private readonly CancellationToken _stopping;
    private readonly ConcurrentDictionary<string, RunningCleanup> _running = new(StringComparer.Ordinal);

    public MaintenanceRunner(
        IServiceScopeFactory scopes, TimeProvider clock, IHostApplicationLifetime lifetime, ILogger<MaintenanceRunner> logger)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
        _stopping = lifetime.ApplicationStopping;
    }

    /// <summary>The host this control plane records on the cleanups it runs.</summary>
    public string Host { get; } = Environment.MachineName;

    /// <summary>Whether a cleanup of <paramref name="areaId"/> is running on this control plane.</summary>
    public bool IsRunning(string areaId) => _running.ContainsKey(areaId);

    /// <summary>The cleanup of <paramref name="areaId"/> running on this control plane, for a caller that has to wait for it
    /// to end (a test); null when none is.</summary>
    internal Task? Running(string areaId) => _running.TryGetValue(areaId, out var running) ? running.Task : null;

    /// <summary>
    /// Whether the open cleanup entry <paramref name="entry"/> is still being worked on: by this control plane, or by another
    /// host that may still be running it (one that has not been open for <see cref="StaleAfter"/>). An open entry of this host
    /// that this control plane is not running was left by a stop, and the next pass closes it.
    /// </summary>
    public bool IsLive(CatalogMaintenanceCleanup entry, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.CompletedUtc is not null)
        {
            return false;
        }

        return string.Equals(entry.Host, Host, StringComparison.Ordinal)
            ? _running.TryGetValue(entry.AreaId, out var running) && running.EntryId == entry.Id
            : nowUtc - entry.StartedUtc < StaleAfter;
    }

    /// <summary>
    /// Starts an admin's cleanup of <paramref name="areaId"/> keeping <paramref name="days"/> days, as
    /// <paramref name="actor"/>, and answers its open log entry at once; the cleanup itself runs in the background and closes
    /// the entry when it ends. Refused, with a message, for an area nobody describes, one that takes no retention, days
    /// outside what it takes, or an area already being cleaned here.
    /// </summary>
    public async Task<CleanupStart> StartAsync(string areaId, int days, string? actor, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(areaId);
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var found = StorageAreas.Find(scope.ServiceProvider.GetServices<IStorageContributor>(), areaId);
            var refusal = Check(found, areaId, days);
            if (refusal is not null)
            {
                return refusal;
            }
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_running.TryAdd(areaId, new RunningCleanup(started.Task, null)))
        {
            return new CleanupStart(null, CleanupRefusal.AlreadyRunning, $"A cleanup of '{areaId}' is already running; it ends on its own, and its result shows in the area's history.");
        }

        CatalogMaintenanceCleanup entry;
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            var now = _clock.GetUtcNow().UtcDateTime;
            await CloseInterruptedAsync(catalog, now, ct).ConfigureAwait(false);
            entry = await MaintenanceStore.StartCleanupAsync(
                catalog, areaId, CleanupTriggers.Manual, actor, Host, days, now.AddDays(-days), now, ct).ConfigureAwait(false);
        }
        catch
        {
            _running.TryRemove(areaId, out _);
            started.TrySetResult();
            throw;
        }

        // The cleanup waits for its own registration, so however quickly it ends, its end removes it from the running set
        // after it was put there, never before.
        var run = Task.Run(() => RunManualAsync(entry, started.Task), CancellationToken.None);
        _running[areaId] = new RunningCleanup(run, entry.Id);
        started.TrySetResult();
        return new CleanupStart(entry, null, null);
    }

    /// <summary>
    /// The automatic pass: closes the cleanups a stopped control plane left open, cleans every area an admin set a retention
    /// for (skipping one a cleanup asked for is already cleaning), records each that removed something or failed, and trims
    /// the cleanup log. One area failing is logged and recorded, and the pass goes on to the next. Answers how many rows went.
    /// </summary>
    public async Task<long> RunAutomaticAsync(CancellationToken ct)
    {
        IReadOnlyDictionary<string, CatalogRetentionSetting> retentions;
        List<StorageArea> areas;
        await using (var scope = _scopes.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await CloseInterruptedAsync(catalog, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            retentions = await MaintenanceStore.GetRetentionsAsync(catalog, ct).ConfigureAwait(false);
            areas = scope.ServiceProvider.GetServices<IStorageContributor>()
                .SelectMany(c => c.Areas)
                .Where(a => a.Retention is not null && retentions.TryGetValue(a.Id, out var setting) && setting.Days is not null)
                .ToList();
        }

        long total = 0;
        foreach (var area in areas)
        {
            ct.ThrowIfCancellationRequested();
            var setting = retentions[area.Id];
            total += await RunAutomaticAsync(area.Id, setting.Days!.Value, setting.UpdatedBy, ct).ConfigureAwait(false);
        }

        await using (var scope = _scopes.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await MaintenanceStore.TrimCleanupLogAsync(catalog, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>
    /// Closes the cleanup entries nobody is working on any more: this host's open entries of the areas this control plane is
    /// not cleaning (a stop left them), and any entry open longer than <see cref="StaleAfter"/> (its host went away). An area
    /// this control plane holds is left alone whatever its entries, since one may have been written a moment before the
    /// cleanup that wrote it was registered.
    /// </summary>
    private async Task CloseInterruptedAsync(CatalogDbContext catalog, DateTime nowUtc, CancellationToken ct)
    {
        var busy = _running.Keys.ToList();
        var closed = await MaintenanceStore.CloseInterruptedAsync(catalog, Host, busy, StaleAfter, nowUtc, ct).ConfigureAwait(false);
        if (closed > 0)
        {
            LogClosedInterrupted(closed);
        }
    }

    private static CleanupStart? Check(FoundStorageArea? found, string areaId, int days)
    {
        if (found is null)
        {
            return new CleanupStart(null, CleanupRefusal.UnknownArea, $"No storage area '{areaId}'.");
        }

        if (found.Area.Retention is not { } retention)
        {
            return new CleanupStart(null, CleanupRefusal.NotCleanable, $"'{found.Area.Title}' is kept or cleans itself; nothing removes it from the Maintenance page.");
        }

        if (days < retention.MinDays || days > retention.MaxDays)
        {
            return new CleanupStart(null, CleanupRefusal.DaysOutOfRange, $"'{found.Area.Title}' keeps between {retention.MinDays} and {retention.MaxDays} days.");
        }

        return null;
    }

    private async Task RunManualAsync(CatalogMaintenanceCleanup entry, Task registered)
    {
        await registered.ConfigureAwait(false);
        long removed = 0;
        string? error = null;
        try
        {
            removed = await CleanAsync(entry.AreaId, entry.OlderThanUtc, _stopping).ConfigureAwait(false);
            LogCleaned(entry.AreaId, removed, entry.Days, CleanupTriggers.Manual, entry.Actor ?? "unknown");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            error = "The control plane stopped before this cleanup finished. What it removed stays removed; clean up again to finish.";
        }
        catch (Exception ex)
        {
            error = SecretHygiene.RedactedMessage(ex);
            LogFailed(entry.AreaId, CleanupTriggers.Manual, error);
        }
        finally
        {
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                await MaintenanceStore.CompleteCleanupAsync(catalog, entry.Id, removed, error, _clock.GetUtcNow().UtcDateTime, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The entry stays open; the next start of this host, or the pass once it is stale, closes it as interrupted.
                LogCloseFailed(entry.Id, SecretHygiene.RedactedMessage(ex));
            }
            finally
            {
                _running.TryRemove(entry.AreaId, out _);
            }
        }
    }

    private async Task<long> RunAutomaticAsync(string areaId, int days, string? actor, CancellationToken ct)
    {
        if (!_running.TryAdd(areaId, new RunningCleanup(Task.CompletedTask, null)))
        {
            return 0;
        }

        try
        {
            var started = _clock.GetUtcNow().UtcDateTime;
            var olderThan = started.AddDays(-days);
            long removed = 0;
            string? error = null;
            try
            {
                removed = await CleanAsync(areaId, olderThan, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                error = SecretHygiene.RedactedMessage(ex);
                LogFailed(areaId, CleanupTriggers.Automatic, error);
            }

            if (removed > 0 || error is not null)
            {
                if (error is null)
                {
                    LogCleaned(areaId, removed, days, CleanupTriggers.Automatic, actor ?? "unknown");
                }

                await using var scope = _scopes.CreateAsyncScope();
                var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                await MaintenanceStore.RecordAutomaticAsync(
                    catalog, areaId, actor, Host, days, olderThan, started, _clock.GetUtcNow().UtcDateTime, removed, error, ct).ConfigureAwait(false);
            }

            return removed;
        }
        finally
        {
            _running.TryRemove(areaId, out _);
        }
    }

    /// <summary>Cleans one area on a scope of its own, so one area's failure leaves nothing behind for the next.</summary>
    private async Task<long> CleanAsync(string areaId, DateTime olderThanUtc, CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var found = StorageAreas.Find(scope.ServiceProvider.GetServices<IStorageContributor>(), areaId)
            ?? throw new InvalidOperationException($"No storage area '{areaId}' is described any more.");
        return await found.Contributor.CleanAsync(found.Area, olderThanUtc, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Maintenance removed {Removed} row(s) of storage area '{AreaId}' older than {Days} day(s) ({Trigger}, for {Actor}).")]
    private partial void LogCleaned(string areaId, long removed, int days, string trigger, string actor);

    [LoggerMessage(Level = LogLevel.Error, Message = "Maintenance cleanup of storage area '{AreaId}' ({Trigger}) failed: {Error}")]
    private partial void LogFailed(string areaId, string trigger, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Maintenance could not record the end of cleanup {CleanupId}; it is closed as interrupted later: {Error}")]
    private partial void LogCloseFailed(long cleanupId, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Maintenance closed {Count} cleanup(s) a stopped control plane left open; what they removed stays removed.")]
    private partial void LogClosedInterrupted(int count);

    /// <summary>A cleanup running on this control plane: its task, and the log entry of one an admin asked for.</summary>
    private sealed record RunningCleanup(Task Task, long? EntryId);
}
