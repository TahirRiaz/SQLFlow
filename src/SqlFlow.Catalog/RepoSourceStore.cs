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
/// the API names when it refuses to change the source. <see cref="AutoSync"/> is whether it syncs on its own, when the
/// configuration says (<c>&lt;Setting&gt;:AutoSync</c>); null leaves the choice to an administrator.
/// </summary>
public sealed record ConfiguredRepository(
    string Name, string? RemoteUrl, string? LocalPath, string Branch, int SyncIntervalSeconds, string? CredentialReference,
    string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths, string Setting, bool? AutoSync = null)
{
    /// <summary>The configuration key of <see cref="AutoSync"/>.</summary>
    public string AutoSyncSetting => Setting + ":AutoSync";
}

/// <summary>
/// What a requested sync reads when its source syncs only on request (<see cref="CatalogRepoSource.AutoSync"/> off). A
/// source that syncs on its own reads the tip of its branch whatever the request says.
/// </summary>
public enum RepoSyncMove
{
    /// <summary>The commit the source holds, again: a change that needs the catalog recomputed, never a newer commit.</summary>
    Hold,

    /// <summary>The tip of the source's branch (a sync-now, or a trigger request that names no commit).</summary>
    BranchTip,

    /// <summary>The commit the request names, which must be on the branch; a source already holding it stays.</summary>
    Commit,
}

/// <summary>How turning a source's automatic sync on or off went.</summary>
public enum AutoSyncChange
{
    Applied,

    /// <summary>The source already syncs that way; nothing was written.</summary>
    Unchanged,

    NotFound,

    /// <summary>The control plane's configuration sets it (<see cref="AutoSyncChangeResult.Setting"/>).</summary>
    SetByConfiguration,
}

/// <summary>The outcome of turning automatic sync on or off, with the setting that refused it when the configuration did.</summary>
public sealed record AutoSyncChangeResult(AutoSyncChange Outcome, string? Setting = null);

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
    /// source is due to sync immediately and syncs on its own (<see cref="CatalogRepoSource.AutoSync"/>); an existing one
    /// keeps its schedule and its automatic sync, which only an administrator turns on or off
    /// (<see cref="SetAutoSyncAsync"/>), but takes the refreshed settings. A change of where it reads from (the remote, the
    /// directory or the branch) makes it due now and asks it to move to the new branch's tip, since the commit it held
    /// belongs to the old one. Exactly one of <paramref name="remoteUrl"/> (a git remote the managed sync clones and pulls) and
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
                    AutoSync = true,
                    CredentialReference = reference,
                    CredentialUsername = username,
                    ExcludedFlowPaths = excludedJson,
                    NextSyncUtc = nowUtc, // a new source syncs on the next tick
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                });
                return id;
            }

            var moved = existing.RemoteUrl != effectiveRemoteUrl || existing.LocalPath != effectiveLocalPath
                || existing.Branch != effectiveBranch;
            existing.RemoteUrl = effectiveRemoteUrl;
            existing.LocalPath = effectiveLocalPath;
            existing.Branch = effectiveBranch;
            existing.Enabled = enabled;
            existing.SyncIntervalSeconds = interval;
            existing.CredentialReference = reference;
            existing.CredentialUsername = username;
            existing.ExcludedFlowPaths = excludedJson;
            existing.UpdatedUtc = nowUtc;
            if (moved)
            {
                RequestMoveToNewLocation(existing, nowUtc);
            }

            // Re-enabling a source (or one that never got a next-sync, as one that syncs only on request has between
            // requests) becomes due now: the sync applies the refreshed settings at the commit it holds.
            if (enabled && existing.NextSyncUtc is null)
            {
                existing.NextSyncUtc = nowUtc;
            }

            return id;
        }, ct);
    }

    /// <summary>A source now reading another remote, directory or branch: the commit it held belongs to the old one, so
    /// its next sync, due now, reads the new branch's tip, whether it syncs on its own or only on request.</summary>
    private static void RequestMoveToNewLocation(CatalogRepoSource source, DateTime nowUtc)
    {
        source.MoveRequestedUtc = nowUtc;
        source.MoveToCommit = null;
        source.NextSyncUtc = nowUtc;
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
    /// nothing. A configured source is always enabled: a deployment that names a repository wants it synced. Its automatic
    /// sync is the configuration's when the configuration says (<see cref="ConfiguredRepository.AutoSync"/>), and an
    /// administrator's otherwise: a value the configuration stops stating stays as it was, for an administrator to change.
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
                var autoSyncBy = repository.AutoSync is null ? null : repository.AutoSyncSetting;
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
                        AutoSync = repository.AutoSync ?? true,
                        AutoSyncConfiguredBy = autoSyncBy,
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

                var autoSync = repository.AutoSync ?? existing.AutoSync;
                var moved = existing.RemoteUrl != remote || existing.LocalPath != local || existing.Branch != branch;
                var changed = moved
                    || !existing.Enabled || existing.SyncIntervalSeconds != interval || existing.CredentialReference != reference
                    || existing.CredentialUsername != username || existing.ExcludedFlowPaths != excluded
                    || existing.ConfiguredBy != repository.Setting || existing.Name != repository.Name
                    || existing.AutoSync != autoSync || existing.AutoSyncConfiguredBy != autoSyncBy;
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
                existing.AutoSyncConfiguredBy = autoSyncBy;
                existing.UpdatedUtc = nowUtc;
                if (!existing.Enabled || existing.NextSyncUtc is null)
                {
                    existing.Enabled = true;
                    existing.NextSyncUtc = nowUtc;
                }

                if (moved)
                {
                    RequestMoveToNewLocation(existing, nowUtc);
                }

                if (existing.AutoSync != autoSync)
                {
                    existing.AutoSync = autoSync;
                    ScheduleAfterAutoSyncChange(existing, nowUtc);
                }

                updated.Add(repository.Name);
            }

            var stale = await catalog.RepoSources.AsTracking()
                .Where(s => s.ConfiguredBy != null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var source in stale.Where(s => !named.Contains(s.Id)))
            {
                // Kept as an ordinary source, syncing as it did; its automatic sync is an administrator's from now on.
                source.ConfiguredBy = null;
                source.AutoSyncConfiguredBy = null;
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

    /// <summary>
    /// Turns a source's automatic sync on or off for <paramref name="actor"/>, recording who and when. On, the source is due
    /// at once, so it catches up with the tip of its branch now rather than an interval later. Off, its next periodic sync is
    /// cancelled and it holds the commit it last synced until a trigger request or a sync-now moves it; a sync already due
    /// (one asked for, or one that never succeeded and is still retrying) still runs, and reads the commit it holds. Refused,
    /// naming the setting, when the control plane's configuration sets the value; unchanged when it already syncs that way.
    /// </summary>
    public static Task<AutoSyncChangeResult> SetAutoSyncAsync(
        CatalogDbContext catalog, Guid id, bool autoSync, string actor, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        var who = actor.Trim();
        who = who.Length > 256 ? who[..256] : who;
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var source = await catalog.RepoSources.AsTracking().FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
            if (source is null)
            {
                return new AutoSyncChangeResult(AutoSyncChange.NotFound);
            }

            if (source.AutoSyncConfiguredBy is { } setting)
            {
                return new AutoSyncChangeResult(AutoSyncChange.SetByConfiguration, setting);
            }

            if (source.AutoSync == autoSync)
            {
                return new AutoSyncChangeResult(AutoSyncChange.Unchanged);
            }

            source.AutoSync = autoSync;
            source.AutoSyncUpdatedBy = who;
            source.AutoSyncUpdatedUtc = nowUtc;
            source.UpdatedUtc = nowUtc;
            ScheduleAfterAutoSyncChange(source, nowUtc);
            return new AutoSyncChangeResult(AutoSyncChange.Applied);
        }, ct);
    }

    /// <summary>
    /// The next sync of a source whose automatic sync was just turned on or off. On, it is due now. Off, a next sync in the
    /// future is the periodic one, which no longer happens; one already due stays, since it was asked for or is overdue, and
    /// the attempt reads the commit the source holds unless a move asked for another. A source that has never synced keeps
    /// its schedule, so its first sync still retries until it succeeds.
    /// </summary>
    private static void ScheduleAfterAutoSyncChange(CatalogRepoSource source, DateTime nowUtc)
    {
        if (source.AutoSync)
        {
            source.NextSyncUtc = nowUtc;
            return;
        }

        if (source.LastSyncedSha is not null && source.NextSyncUtc > nowUtc)
        {
            source.NextSyncUtc = null;
        }
    }

    /// <summary>Whether <paramref name="value"/> is a git commit id, full or abbreviated: 7 to 64 hexadecimal characters.</summary>
    public static bool IsCommitId(string? value)
        => value is { Length: >= 7 and <= 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Whether the attempt of <paramref name="source"/> that started at <see cref="CatalogRepoSource.SyncStartedUtc"/> answers
    /// the move asked for (<see cref="CatalogRepoSource.MoveRequestedUtc"/>): the move was asked for at or before the attempt
    /// started. A move asked for while the attempt runs is the next attempt's.
    /// </summary>
    public static bool AnswersMove(CatalogRepoSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return source.MoveRequestedUtc is { } move && source.SyncStartedUtc is { } started && move <= started;
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
    /// (<see cref="CatalogRepoSource.SyncStartedUtc"/>). Returns true if this caller won. A source that syncs only on request
    /// (<see cref="CatalogRepoSource.AutoSync"/> off) is given no next sync, so it waits for the next request; one that has
    /// never synced successfully keeps <paramref name="newNextSyncUtc"/>, so its first sync retries on its interval.</summary>
    public static async Task<bool> TryClaimSyncAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextSyncUtc, DateTime newNextSyncUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.RepoSources
            .Where(s => s.Id == id && s.NextSyncUtc == observedNextSyncUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextSyncUtc, x => x.AutoSync || x.LastSyncedSha == null ? newNextSyncUtc : (DateTime?)null)
                .SetProperty(x => x.SyncStartedUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records a successful sync: the pulled commit and the time, clearing any prior error and the
    /// one-shot force-lineage request a manual sync-now made, when this attempt answered it. A sync-now made while the
    /// attempt was already running is not answered by it: its flag stays, and the source, made due by the request,
    /// syncs again on the next tick with the full recompute the operator asked for. A move the attempt answered
    /// (<see cref="AnswersMove"/>) is cleared the same way, and one asked for while it ran stays.</summary>
    /// <remarks><paramref name="activityId"/> is the attempt's activity trace, recorded so whatever waited for the attempt
    /// reads exactly its lines.</remarks>
    public static Task RecordSuccessAsync(
        CatalogDbContext catalog, Guid id, string commitSha, DateTime nowUtc, Guid? activityId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastSyncedSha, commitSha)
                .SetProperty(x => x.LastSyncActivityId, activityId)
                .SetProperty(x => x.LastError, (string?)null)
                .SetProperty(
                    x => x.ForceLineageOnNextSync,
                    x => x.SyncRequestedUtc != null && x.SyncStartedUtc != null && x.SyncRequestedUtc > x.SyncStartedUtc)
                .SetProperty(
                    x => x.MoveToCommit,
                    x => x.MoveRequestedUtc != null && x.SyncStartedUtc != null && x.MoveRequestedUtc <= x.SyncStartedUtc
                        ? null
                        : x.MoveToCommit)
                .SetProperty(
                    x => x.MoveRequestedUtc,
                    x => x.MoveRequestedUtc != null && x.SyncStartedUtc != null && x.MoveRequestedUtc <= x.SyncStartedUtc
                        ? null
                        : x.MoveRequestedUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Records a failed sync: keeps the last successful commit and stores the (redacted) error, with the
    /// attempt's activity trace (<paramref name="activityId"/>). A move the attempt answered is answered by the failure
    /// (whoever asked reads the error) and cleared; a source that syncs only on request does not retry it on its own.</summary>
    public static Task RecordFailureAsync(
        CatalogDbContext catalog, Guid id, string error, DateTime nowUtc, Guid? activityId = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.RepoSources
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastSyncUtc, nowUtc)
                .SetProperty(x => x.LastSyncActivityId, activityId)
                .SetProperty(x => x.LastError, error)
                .SetProperty(
                    x => x.MoveToCommit,
                    x => x.MoveRequestedUtc != null && x.SyncStartedUtc != null && x.MoveRequestedUtc <= x.SyncStartedUtc
                        ? null
                        : x.MoveToCommit)
                .SetProperty(
                    x => x.MoveRequestedUtc,
                    x => x.MoveRequestedUtc != null && x.SyncStartedUtc != null && x.MoveRequestedUtc <= x.SyncStartedUtc
                        ? null
                        : x.MoveRequestedUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Queues a sync of the enabled source that produces the repo named <paramref name="repoName"/> to recompute
    /// the catalog (<see cref="ResyncAsync"/>), returning its id; null when the repo is not synced from an enabled source (a
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
            && await ResyncAsync(catalog, id, nowUtc, ct).ConfigureAwait(false) == RepoSourceMutation.Applied
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
    /// answered it (<see cref="IsSyncAnswered"/>). A source that syncs only on request moves to the tip of its branch: a
    /// person asking to sync now is asking for what the branch holds.</summary>
    public static Task<RepoSourceMutation> TriggerNowAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
        => RequestSyncAsync(catalog, id, nowUtc, forceLineage: true, RepoSyncMove.BranchTip, commit: null, ct);

    /// <summary>
    /// Makes a source due immediately to recompute the catalog from it, with a full lineage recompute, as a change that
    /// alters what the sync derives (a registration run, an uploaded report, a host module's own registry) needs. It never
    /// moves a source that syncs only on request: that one syncs the commit it holds again, so nothing merged since reaches
    /// the catalog without a request for it. A source that syncs on its own reads its branch's tip, as every sync of it does.
    /// </summary>
    public static Task<RepoSourceMutation> ResyncAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, CancellationToken ct = default)
        => RequestSyncAsync(catalog, id, nowUtc, forceLineage: true, RepoSyncMove.Hold, commit: null, ct);

    /// <summary>
    /// Makes a source due immediately and records the request's time, as <see cref="TriggerNowAsync"/> does, but asks for
    /// the full lineage recompute only when <paramref name="forceLineage"/> is set. A trigger request after a merge leaves
    /// it off: the merge changed the estate, so the sync recomputes what changed anyway, and an unchanged estate needs
    /// nothing recomputed. A force-lineage request already pending stays pending.
    /// <para>
    /// <paramref name="move"/> is what the sync reads when the source syncs only on request: the commit it holds
    /// (<see cref="RepoSyncMove.Hold"/>, which leaves a move already asked for in place), the tip of its branch, or
    /// <paramref name="commit"/>. Two moves asked for before an attempt takes them become one: the tip wins over a commit,
    /// since it contains every commit on the branch, and of two commits the later request's is kept; the sync never moves
    /// back past what the source holds, and a request whose commit the sync did not reach asks again.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="move"/> is <see cref="RepoSyncMove.Commit"/> and
    /// <paramref name="commit"/> is not a commit id.</exception>
    public static async Task<RepoSourceMutation> RequestSyncAsync(
        CatalogDbContext catalog, Guid id, DateTime nowUtc, bool forceLineage, RepoSyncMove move, string? commit,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (move == RepoSyncMove.Commit && !IsCommitId(commit))
        {
            throw new ArgumentException("A move to a commit names it by its id: 7 to 64 hexadecimal characters.", nameof(commit));
        }

        var enabled = catalog.RepoSources.Where(s => s.Id == id && s.Enabled);
        var affected = move switch
        {
            RepoSyncMove.Hold => await enabled
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.NextSyncUtc, nowUtc)
                    .SetProperty(x => x.ForceLineageOnNextSync, x => forceLineage || x.ForceLineageOnNextSync)
                    .SetProperty(x => x.SyncRequestedUtc, nowUtc)
                    .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
                .ConfigureAwait(false),
            RepoSyncMove.BranchTip => await enabled
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.NextSyncUtc, nowUtc)
                    .SetProperty(x => x.ForceLineageOnNextSync, x => forceLineage || x.ForceLineageOnNextSync)
                    .SetProperty(x => x.SyncRequestedUtc, nowUtc)
                    .SetProperty(x => x.MoveRequestedUtc, nowUtc)
                    .SetProperty(x => x.MoveToCommit, (string?)null)
                    .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
                .ConfigureAwait(false),
            RepoSyncMove.Commit => await enabled
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.NextSyncUtc, nowUtc)
                    .SetProperty(x => x.ForceLineageOnNextSync, x => forceLineage || x.ForceLineageOnNextSync)
                    .SetProperty(x => x.SyncRequestedUtc, nowUtc)
                    // A move to the tip that no attempt has taken yet stays one: the tip holds this commit too.
                    .SetProperty(
                        x => x.MoveToCommit,
                        x => x.MoveRequestedUtc != null && x.MoveToCommit == null
                            && (x.SyncStartedUtc == null || x.MoveRequestedUtc > x.SyncStartedUtc)
                                ? null
                                : commit)
                    .SetProperty(x => x.MoveRequestedUtc, nowUtc)
                    .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
                .ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(move), move, "Not a kind of sync request."),
        };
        return affected > 0 ? RepoSourceMutation.Applied : RepoSourceMutation.NotFound;
    }
}
