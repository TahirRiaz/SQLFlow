using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Lets a sync-now answer when its sync has happened rather than when it was queued. A run is pinned to the repo's last
/// synced commit, and the sync loop records the commit only after the sync a sync-now wakes it for (it clones,
/// reconciles the catalog and recomputes lineage first), so a sync-now that answered at once let an operator start a run
/// on the commit before the one they had just synced for. The sync-now action waits here, for at most
/// <see cref="ManagedSyncOptions.SyncNowWaitSeconds"/>, until an attempt that started after its request has recorded
/// an outcome (<see cref="RepoSourceStore.IsSyncAnswered"/>); its answer then carries the commit that attempt pulled,
/// or the error it failed with.
/// </summary>
public sealed class RepoSyncWaiter
{
    /// <summary>How often the wait reads the source again.</summary>
    internal static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly TimeProvider _clock;
    private readonly TimeSpan _wait;
    private readonly TimeSpan _abandonedAfter;
    private readonly TimeSpan _pollInterval;

    public RepoSyncWaiter(IOptions<ControlPlaneOptions> options, TimeProvider clock)
        : this(options, clock, DefaultPollInterval)
    {
    }

    internal RepoSyncWaiter(IOptions<ControlPlaneOptions> options, TimeProvider clock, TimeSpan pollInterval)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        var sync = options.Value.ManagedSync;
        _clock = clock;
        _wait = TimeSpan.FromSeconds(sync.SyncNowWaitSeconds);
        _abandonedAfter = TimeSpan.FromMinutes(sync.SyncAbandonedAfterMinutes);
        _pollInterval = pollInterval;
    }

    /// <summary>Whether a sync of <paramref name="source"/> is in progress for an operator (a sync-now not yet answered,
    /// or an attempt running), so a client can show it until it ends.</summary>
    public bool IsPending(CatalogRepoSource source)
        => RepoSourceStore.IsSyncPending(source, _clock.GetUtcNow().UtcDateTime, _abandonedAfter);

    /// <summary>
    /// Waits until the sync-now made at <paramref name="requestedUtc"/> has been answered, the wait runs out, or the
    /// source is disabled or leaves the catalog, and returns the source as it then stands (null once it has left) with
    /// whether the request was answered. A source that answers at once, or a wait of zero, reads the source once.
    /// </summary>
    public async Task<(CatalogRepoSource? Source, bool Answered)> WaitForAnswerAsync(
        CatalogDbContext catalog, Guid sourceId, DateTime requestedUtc, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var deadline = _clock.GetUtcNow() + _wait;
        while (true)
        {
            var source = await catalog.RepoSources.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Id == sourceId, ct).ConfigureAwait(false);
            if (source is null)
            {
                return (null, false);
            }

            if (RepoSourceStore.IsSyncAnswered(source, requestedUtc))
            {
                return (source, true);
            }

            if (!source.Enabled || _clock.GetUtcNow() >= deadline)
            {
                return (source, false);
            }

            await Task.Delay(_pollInterval, _clock, ct).ConfigureAwait(false);
        }
    }
}
