using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;

namespace SqlFlow.Catalog;

/// <summary>The result of a sync-now / disable request, so the API can answer 200 / 404 precisely.</summary>
public enum RepoSourceMutation
{
    Applied,
    NotFound,
}

/// <summary>
/// Persistence for the managed-sync source registry: register/refresh a tracked git repo, find the sources due to
/// sync, claim one atomically (so several control-plane nodes never sync the same source at once), and record each
/// sync's outcome. Stateless like the other catalog stores; the git pull + the actual catalog sync are the control
/// plane's job, this only holds the schedule and results.
/// </summary>
public static class RepoSourceStore
{
    /// <summary>Registers or refreshes a tracked repo (keyed by name, so re-registering updates in place). A new
    /// source is due to sync immediately; an existing one keeps its schedule but takes the refreshed settings.
    /// <paramref name="credentialReference"/> is a secret reference (never a secret value) for the git token, and
    /// <paramref name="credentialUsername"/> the paired username; both are optional and null clears them.</summary>
    public static Task<Guid> UpsertAsync(
        CatalogDbContext catalog, string name, string remoteUrl, string branch, bool enabled, int syncIntervalSeconds,
        DateTime nowUtc, string? credentialReference = null, string? credentialUsername = null,
        IReadOnlyList<string>? excludedFlowPaths = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteUrl);

        var id = FlowIdentity.FromName($"reposource/{name}");
        var interval = Math.Max(1, syncIntervalSeconds);
        var effectiveBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var reference = string.IsNullOrWhiteSpace(credentialReference) ? null : credentialReference.Trim();
        var username = string.IsNullOrWhiteSpace(credentialUsername) ? null : credentialUsername.Trim();
        var excludedJson = SerializeExcludedPaths(excludedFlowPaths);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var existing = await catalog.RepoSources.AsTracking()
                .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (existing is null)
            {
                catalog.RepoSources.Add(new CatalogRepoSource
                {
                    Id = id,
                    Name = name,
                    RemoteUrl = remoteUrl,
                    Branch = effectiveBranch,
                    Enabled = enabled,
                    SyncIntervalSeconds = interval,
                    CredentialReference = reference,
                    CredentialUsername = username,
                    ExcludedFlowPaths = excludedJson,
                    NextSyncUtc = nowUtc, // a new source syncs on the next tick
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                });
                return id;
            }

            existing.RemoteUrl = remoteUrl;
            existing.Branch = effectiveBranch;
            existing.Enabled = enabled;
            existing.SyncIntervalSeconds = interval;
            existing.CredentialReference = reference;
            existing.CredentialUsername = username;
            existing.ExcludedFlowPaths = excludedJson;
            existing.UpdatedUtc = nowUtc;
            // Re-enabling a source (or one that never got a next-sync) becomes due now.
            if (enabled && existing.NextSyncUtc is null)
            {
                existing.NextSyncUtc = nowUtc;
            }

            return id;
        }, ct);
    }

    /// <summary>Serializes an excluded-flow selection to the stored JSON array: trimmed, forward-slashed, distinct,
    /// non-blank paths, ordinal-sorted for a stable value. Null / empty selection stores null (import everything).</summary>
    public static string? SerializeExcludedPaths(IReadOnlyList<string>? excludedFlowPaths)
    {
        if (excludedFlowPaths is null)
        {
            return null;
        }

        var paths = excludedFlowPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        return paths.Length == 0 ? null : JsonSerializer.Serialize(paths);
    }

    /// <summary>Parses the stored excluded-flow JSON into a case-insensitive set the sync compares flow paths
    /// against. A null, blank, or malformed value yields an empty set (import everything), never an error.</summary>
    public static IReadOnlySet<string> ParseExcludedPaths(string? excludedJson)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(excludedJson))
        {
            return set;
        }

        try
        {
            var paths = JsonSerializer.Deserialize<string[]>(excludedJson);
            if (paths is not null)
            {
                foreach (var path in paths)
                {
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        set.Add(path.Trim().Replace('\\', '/'));
                    }
                }
            }
        }
        catch (JsonException)
        {
            // A corrupt selection must never break a sync: fall back to importing everything.
        }

        return set;
    }

    /// <summary>Deletes a tracked source and its sync activity trace (keyed by the source id). Used to remove a
    /// source-only row (one registered but not yet synced, so no repo exists to delete through <see cref="RepoStore"/>
    /// yet); once a repo has been produced, deleting the repo removes its source the same way. Returns false when no
    /// source has the given id.</summary>
    public static Task<bool> DeleteAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var exists = await catalog.RepoSources.AsNoTracking()
                .AnyAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (!exists)
            {
                return false;
            }

            await catalog.ActivityEvents
                .Where(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == id.ToString())
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RepoSources.Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return true;
        }, ct);
    }

    /// <summary>The enabled sources whose next sync has arrived, oldest-due first.</summary>
    public static async Task<IReadOnlyList<CatalogRepoSource>> ListDueAsync(
        CatalogDbContext catalog, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.RepoSources.AsNoTracking()
            .Where(s => s.Enabled && s.NextSyncUtc != null && s.NextSyncUtc <= nowUtc)
            .OrderBy(s => s.NextSyncUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Atomically claims a due source by advancing its next-sync from the observed value, so exactly one
    /// control-plane node syncs it this interval. Returns true if this caller won.</summary>
    public static async Task<bool> TryClaimSyncAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextSyncUtc, DateTime newNextSyncUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.NextSyncUtc == observedNextSyncUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, newNextSyncUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records a successful sync: the pulled commit and the time, clearing any prior error and the
    /// one-shot force-lineage request (a manual sync-now has now been honored).</summary>
    public static Task RecordSuccessAsync(
        CatalogDbContext catalog, Guid id, string commitSha, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastSyncedSha, commitSha)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(x => x.ForceLineageOnNextSync, false)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Records a failed sync: keeps the last successful commit and stores the (redacted) error.</summary>
    public static Task RecordFailureAsync(
        CatalogDbContext catalog, Guid id, string error, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastError, error)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Makes a source due immediately (the sync-now action), so the next tick pulls it, and requests a
    /// full lineage recompute on that sync: a manual trigger is a deliberate "refresh everything", so it bypasses
    /// the unchanged-estate shortcut that a periodic sync relies on (which is what recomputes waves and populates
    /// object bodies/columns from the persisted run trace). The flag clears once the sync succeeds.</summary>
    public static async Task<RepoSourceMutation> TriggerNowAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, nowUtc)
                .SetProperty(x => x.ForceLineageOnNextSync, true)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0 ? RepoSourceMutation.Applied : RepoSourceMutation.NotFound;
    }
}
