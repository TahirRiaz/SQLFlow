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
/// A repo source the control plane's configuration names, as the control plane writes it at start: exactly one of
/// <see cref="RemoteUrl"/> and <see cref="LocalPath"/>, the branch, the sync interval, a git credential as a reference
/// (never a token), and the flow files to leave out. <see cref="Setting"/> is the configuration key that names it, which
/// the API names when it refuses to change the source.
/// </summary>
public sealed record ConfiguredRepository(
    string Name, string? RemoteUrl, string? LocalPath, string Branch, int SyncIntervalSeconds, string? CredentialReference,
    string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths, string Setting);

/// <summary>What writing the configured sources changed: the sources added, the ones updated, the ones released.</summary>
public sealed record ConfiguredRepositoriesApplied(IReadOnlyList<string> Registered, IReadOnlyList<string> Updated, IReadOnlyList<string> Released)
{
    /// <summary>A one-line account of the change, as a bootstrap step reports it.</summary>
    public string Describe()
        => Registered.Count == 0 && Updated.Count == 0 && Released.Count == 0
            ? "unchanged"
            : string.Join("; ", new[]
            {
                Registered.Count == 0 ? null : "registered " + string.Join(", ", Registered),
                Updated.Count == 0 ? null : "updated " + string.Join(", ", Updated),
                Released.Count == 0 ? null : "released " + string.Join(", ", Released),
            }.Where(p => p is not null));
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
    /// Exactly one of <paramref name="remoteUrl"/> (a git remote the managed sync clones and pulls) and
    /// <paramref name="localPath"/> (a directory the control-plane host reads live, with no git step, for a
    /// working copy that has never been committed or pushed) must be given. <paramref name="credentialReference"/>
    /// is a secret reference (never a secret value) for the git token, and <paramref name="credentialUsername"/>
    /// the paired username; both are optional, apply only to a git source, and null clears them.</summary>
    public static Task<Guid> UpsertAsync(
        CatalogDbContext catalog, string name, string? remoteUrl, string branch, bool enabled, int syncIntervalSeconds,
        DateTime nowUtc, string? localPath = null, string? credentialReference = null, string? credentialUsername = null,
        IReadOnlyList<string>? excludedFlowPaths = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var hasRemote = !string.IsNullOrWhiteSpace(remoteUrl);
        var hasLocal = !string.IsNullOrWhiteSpace(localPath);
        if (hasRemote == hasLocal)
        {
            throw new ArgumentException(
                "A repo source needs exactly one of remoteUrl (a git remote) and localPath (a directory read live, with no git step).");
        }

        var id = FlowIdentity.FromName($"reposource/{name}");
        var interval = Math.Max(1, syncIntervalSeconds);
        var effectiveBranch = string.IsNullOrWhiteSpace(branch) ? "main" : branch.Trim();
        var effectiveRemoteUrl = hasRemote ? remoteUrl!.Trim() : null;
        var effectiveLocalPath = hasLocal ? Path.GetFullPath(localPath!.Trim()) : null;
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
                    RemoteUrl = effectiveRemoteUrl,
                    LocalPath = effectiveLocalPath,
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

            existing.RemoteUrl = effectiveRemoteUrl;
            existing.LocalPath = effectiveLocalPath;
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

    /// <summary>
    /// Writes the sources the control plane's configuration names, in one transaction: registers each one the catalog does
    /// not hold (due to sync at once), brings each one it holds in line with the configuration and marks it as set by it
    /// (an existing source keeps its sync schedule and history), and releases each source marked as configured that the
    /// configuration no longer names, keeping it as an ordinary source. Writing the same configuration again changes
    /// nothing. A configured source is always enabled: a deployment that names a repository wants it synced.
    /// </summary>
    public static Task<ConfiguredRepositoriesApplied> ApplyConfiguredAsync(
        CatalogDbContext catalog, IReadOnlyList<ConfiguredRepository> configured, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configured);
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var registered = new List<string>();
            var updated = new List<string>();
            var released = new List<string>();
            var named = new HashSet<Guid>();
            foreach (var repository in configured)
            {
                var id = FlowIdentity.FromName($"reposource/{repository.Name}");
                named.Add(id);
                var remote = string.IsNullOrWhiteSpace(repository.RemoteUrl) ? null : repository.RemoteUrl.Trim();
                var local = string.IsNullOrWhiteSpace(repository.LocalPath) ? null : Path.GetFullPath(repository.LocalPath.Trim());
                var branch = string.IsNullOrWhiteSpace(repository.Branch) ? "main" : repository.Branch.Trim();
                var interval = Math.Max(1, repository.SyncIntervalSeconds);
                var reference = string.IsNullOrWhiteSpace(repository.CredentialReference) ? null : repository.CredentialReference.Trim();
                var username = string.IsNullOrWhiteSpace(repository.CredentialUsername) ? null : repository.CredentialUsername.Trim();
                var excluded = SerializeExcludedPaths(repository.ExcludedFlowPaths);
                var existing = await catalog.RepoSources.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    catalog.RepoSources.Add(new CatalogRepoSource
                    {
                        Id = id,
                        Name = repository.Name,
                        RemoteUrl = remote,
                        LocalPath = local,
                        Branch = branch,
                        Enabled = true,
                        SyncIntervalSeconds = interval,
                        CredentialReference = reference,
                        CredentialUsername = username,
                        ExcludedFlowPaths = excluded,
                        ConfiguredBy = repository.Setting,
                        NextSyncUtc = nowUtc,
                        CreatedUtc = nowUtc,
                        UpdatedUtc = nowUtc,
                    });
                    registered.Add(repository.Name);
                    continue;
                }

                var changed = existing.RemoteUrl != remote || existing.LocalPath != local || existing.Branch != branch
                    || !existing.Enabled || existing.SyncIntervalSeconds != interval || existing.CredentialReference != reference
                    || existing.CredentialUsername != username || existing.ExcludedFlowPaths != excluded
                    || existing.ConfiguredBy != repository.Setting || existing.Name != repository.Name;
                if (!changed)
                {
                    continue;
                }

                existing.Name = repository.Name;
                existing.RemoteUrl = remote;
                existing.LocalPath = local;
                existing.Branch = branch;
                existing.SyncIntervalSeconds = interval;
                existing.CredentialReference = reference;
                existing.CredentialUsername = username;
                existing.ExcludedFlowPaths = excluded;
                existing.ConfiguredBy = repository.Setting;
                existing.UpdatedUtc = nowUtc;
                if (!existing.Enabled || existing.NextSyncUtc is null)
                {
                    existing.Enabled = true;
                    existing.NextSyncUtc = nowUtc;
                }

                updated.Add(repository.Name);
            }

            var stale = await catalog.RepoSources.AsTracking()
                .Where(s => s.ConfiguredBy != null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var source in stale.Where(s => !named.Contains(s.Id)))
            {
                source.ConfiguredBy = null;
                source.UpdatedUtc = nowUtc;
                released.Add(source.Name);
            }

            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ConfiguredRepositoriesApplied(registered, updated, released);
        }, ct);
    }

    /// <summary>The configuration key that sets the source <paramref name="id"/>, or null for a source the API registered (or no source).</summary>
    public static Task<string?> ConfiguredByAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources.AsNoTracking().Where(s => s.Id == id).Select(s => s.ConfiguredBy).FirstOrDefaultAsync(ct);
    }

    /// <summary>The configuration key that sets the source named <paramref name="name"/>, or null for a source the API registered (or no source).</summary>
    public static Task<string?> ConfiguredByNameAsync(CatalogDbContext catalog, string name, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        return catalog.RepoSources.AsNoTracking().Where(s => s.Name == trimmed).Select(s => s.ConfiguredBy).FirstOrDefaultAsync(ct);
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
    /// control-plane node syncs it this interval, and stamps the attempt's start
    /// (<see cref="CatalogRepoSource.SyncStartedUtc"/>). Returns true if this caller won.</summary>
    public static async Task<bool> TryClaimSyncAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextSyncUtc, DateTime newNextSyncUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.NextSyncUtc == observedNextSyncUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, newNextSyncUtc)
                .SetProperty(x => x.SyncStartedUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records a successful sync: the pulled commit and the time, clearing any prior error and the
    /// one-shot force-lineage request a manual sync-now made, when this attempt answered it. A sync-now made while the
    /// attempt was already running is not answered by it: its flag stays, and the source, made due by the request,
    /// syncs again on the next tick with the full recompute the operator asked for.</summary>
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
                .SetProperty(
                    x => x.ForceLineageOnNextSync,
                    x => x.SyncRequestedUtc != null && x.SyncStartedUtc != null && x.SyncRequestedUtc > x.SyncStartedUtc)
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

    /// <summary>Queues the managed sync of the enabled source that produces the repo named
    /// <paramref name="repoName"/>, returning its id; null when the repo is not synced from an enabled source (a
    /// local-path repo applies a change on its next <c>sqlflow db sync</c> instead).</summary>
    public static async Task<Guid?> TriggerForRepoAsync(
        CatalogDbContext catalog, string repoName, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        var sourceId = await catalog.RepoSources.AsNoTracking()
            .Where(s => s.Name == repoName && s.Enabled)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return sourceId is { } id
            && await TriggerNowAsync(catalog, id, nowUtc, ct).ConfigureAwait(false) == RepoSourceMutation.Applied
            ? id
            : null;
    }

    /// <summary>Ends an attempt that recorded no outcome because its host is stopping, so the attempt does not look
    /// as if it were still running. The sync-now it would have answered stays unanswered, and the next attempt
    /// answers it.</summary>
    public static Task RecordAbandonedAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id && s.SyncStartedUtc != null && (s.LastSyncUtc == null || s.SyncStartedUtc > s.LastSyncUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SyncStartedUtc, (DateTime?)null)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Whether a sync attempt of <paramref name="source"/> is running: one started and has not yet
    /// recorded an outcome.</summary>
    public static bool IsSyncRunning(CatalogRepoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.SyncStartedUtc is { } started && (source.LastSyncUtc is not { } finished || started > finished);
    }

    /// <summary>Whether the sync-now made at <paramref name="requestedUtc"/> has been answered: an attempt that started
    /// at or after it has recorded an outcome, success or failure (<see cref="CatalogRepoSource.LastError"/> tells which).
    /// An attempt already running when the request was made does not answer it, because it may have cloned the branch
    /// before the commit the operator synced for.</summary>
    public static bool IsSyncAnswered(CatalogRepoSource source, DateTime requestedUtc)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.SyncStartedUtc is { } started && started >= requestedUtc
            && source.LastSyncUtc is { } finished && finished >= started;
    }

    /// <summary>
    /// Whether a sync of this source is in progress from an operator's point of view: a sync-now not yet answered, or
    /// an attempt running. A request or an attempt older than <paramref name="abandonedAfter"/> does not count: a
    /// request no sync loop ever picked up, or an attempt whose host died without recording an outcome, would
    /// otherwise show as in progress for ever.
    /// </summary>
    public static bool IsSyncPending(CatalogRepoSource source, DateTime nowUtc, TimeSpan abandonedAfter)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.Enabled)
        {
            return false;
        }

        var cutoff = nowUtc - abandonedAfter;
        var requestOpen = source.SyncRequestedUtc is { } requested && requested > cutoff && !IsSyncAnswered(source, requested);
        var attemptRunning = IsSyncRunning(source) && source.SyncStartedUtc > cutoff;
        return requestOpen || attemptRunning;
    }

    /// <summary>Makes a source due immediately (the sync-now action), so the next tick pulls it, and requests a
    /// full lineage recompute on that sync: a manual trigger is a deliberate "refresh everything", so it bypasses
    /// the unchanged-estate shortcut that a periodic sync relies on (which is what recomputes waves and populates
    /// object bodies/columns from the persisted run trace). The flag clears once the sync succeeds. The request's time
    /// is recorded (<see cref="CatalogRepoSource.SyncRequestedUtc"/>), so the caller can tell when an attempt has
    /// answered it (<see cref="IsSyncAnswered"/>).</summary>
    public static async Task<RepoSourceMutation> TriggerNowAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.Enabled)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, nowUtc)
                .SetProperty(x => x.ForceLineageOnNextSync, true)
                .SetProperty(x => x.SyncRequestedUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0 ? RepoSourceMutation.Applied : RepoSourceMutation.NotFound;
    }
}
