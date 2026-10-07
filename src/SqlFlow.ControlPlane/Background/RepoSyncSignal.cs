namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Wakes this host's managed sync loop (<see cref="RepoSyncService"/>) when a repo source has just been made due, so a
/// sync-now, or a newly registered source, starts syncing at once rather than on the loop's next scan, up to
/// <see cref="Configuration.ManagedSyncOptions.PollSeconds"/> later. The catalog stays the record of what is due: the
/// caller makes the source due first and only then wakes the loop, so a host whose loop is disabled, or another
/// control-plane node, still finds the source due on its own scan, and the claim on <c>NextSyncUtc</c> still lets
/// exactly one of them sync it. One pending wake covers any number of requests, because a scan takes every source
/// that is due.
/// </summary>
public sealed class RepoSyncSignal : IDisposable
{
    private readonly SemaphoreSlim _pending = new(0, 1);

    /// <summary>Brings the sync loop's next scan forward to now. Call it once the source is due in the catalog. A
    /// wake that arrives while the loop is syncing starts the next scan as soon as that sync ends.</summary>
    public void Wake()
    {
        try
        {
            _pending.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake is already pending; the scan it starts takes every due source.
        }
    }

    /// <summary>Waits until a wake arrives or <paramref name="timeout"/> passes, whichever comes first, and returns
    /// true when a wake ended the wait.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _pending.WaitAsync(timeout, ct);

    public void Dispose() => _pending.Dispose();
}
