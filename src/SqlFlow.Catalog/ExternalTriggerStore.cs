using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>A trigger definition that cannot be stored, with the reason in a sentence an administrator can act on.</summary>
public sealed class ExternalTriggerDefinitionException : Exception
{
    public ExternalTriggerDefinitionException(string message)
        : base(message)
    {
    }

    public ExternalTriggerDefinitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ExternalTriggerDefinitionException()
    {
    }
}

/// <summary>
/// What an administrator or the configuration says an external trigger is: its name and kind, the repository it acts on,
/// the schedules it may fire, and for a GitHub trigger the repository, ref, workflow and environment a token must state.
/// <see cref="Normalized"/> checks it and returns the form the catalog stores, so the API and the configuration accept
/// exactly the same triggers.
/// </summary>
public sealed partial record ExternalTriggerDefinition(
    string Name, string Kind, string RepoName, string? Description, bool AllSchedules, IReadOnlyList<string> Schedules,
    string? GitHubRepository = null, string? GitHubRepositoryId = null, string? GitHubRef = null,
    string? GitHubWorkflow = null, string? GitHubEnvironment = null)
{
    /// <summary>The longest trigger name: short enough for a workflow input and a run's <c>trigger:</c> label.</summary>
    public const int MaxNameLength = 100;

    /// <summary>The most schedules a trigger may list by name.</summary>
    public const int MaxSchedules = 200;

    /// <summary>
    /// The definition checked and trimmed: a name of letters, digits, dots, dashes and underscores; a known kind; a
    /// repository; distinct, non-blank schedule names (none when <see cref="AllSchedules"/> is set; a trigger with none and
    /// without it only syncs); for a GitHub trigger an <c>owner/name</c> repository, a numeric repository id when one is pinned, a ref
    /// pattern, and a workflow given as a repository-relative path; for a key trigger, no GitHub field at all.
    /// </summary>
    /// <exception cref="ExternalTriggerDefinitionException">The definition is not one a trigger may have; the message says why.</exception>
    public ExternalTriggerDefinition Normalized()
    {
        var name = (Name ?? string.Empty).Trim();
        if (!NamePattern().IsMatch(name))
        {
            throw new ExternalTriggerDefinitionException(
                $"A trigger's name is 1 to {MaxNameLength} letters, digits, dots, dashes and underscores, starting with a letter or digit; '{name}' is not.");
        }

        var kind = (Kind ?? string.Empty).Trim().ToLowerInvariant();
        if (!ExternalTriggerKinds.IsKnown(kind))
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}': kind '{Kind}' is not one of {string.Join(", ", ExternalTriggerKinds.All)}.");
        }

        var repo = (RepoName ?? string.Empty).Trim();
        if (repo.Length == 0 || repo.Length > 256 || repo.Any(char.IsControl))
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}' needs the repository it syncs: the name of a repo source, at most 256 characters.");
        }

        var description = string.IsNullOrWhiteSpace(Description) ? null : Description.Trim();
        if (description is { Length: > 1000 })
        {
            throw new ExternalTriggerDefinitionException($"Trigger '{name}': the description is longer than 1000 characters.");
        }

        var schedules = new List<string>();
        foreach (var schedule in Schedules ?? [])
        {
            var trimmed = schedule?.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 256 || trimmed.Any(char.IsControl))
            {
                throw new ExternalTriggerDefinitionException(
                    $"Trigger '{name}': a schedule name is blank, longer than 256 characters, or holds a control character.");
            }

            if (!schedules.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                schedules.Add(trimmed);
            }
        }

        if (AllSchedules && schedules.Count > 0)
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}' may fire every schedule of '{repo}'; leave its schedule list empty, or list the schedules and turn 'all schedules' off.");
        }

        if (schedules.Count > MaxSchedules)
        {
            throw new ExternalTriggerDefinitionException($"Trigger '{name}' lists more than {MaxSchedules} schedules; allow it every schedule instead.");
        }

        if (kind == ExternalTriggerKinds.Key)
        {
            if (new[] { GitHubRepository, GitHubRepositoryId, GitHubRef, GitHubWorkflow, GitHubEnvironment }.Any(v => !string.IsNullOrWhiteSpace(v)))
            {
                throw new ExternalTriggerDefinitionException(
                    $"Trigger '{name}' is a key trigger; the GitHub repository, ref, workflow and environment apply to a GitHub trigger only.");
            }

            return new ExternalTriggerDefinition(name, kind, repo, description, AllSchedules, schedules);
        }

        var repository = (GitHubRepository ?? string.Empty).Trim();
        if (repository.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(repository, UriKind.Absolute, out var address))
        {
            // A pasted repository address: keep its owner/name.
            repository = address.AbsolutePath.Trim('/');
            if (repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                repository = repository[..^4];
            }
        }

        if (!RepositoryPattern().IsMatch(repository))
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}' needs the GitHub repository whose workflows it trusts, written owner/name; '{GitHubRepository}' is not.");
        }

        var repositoryId = string.IsNullOrWhiteSpace(GitHubRepositoryId) ? null : GitHubRepositoryId.Trim();
        if (repositoryId is not null && !RepositoryIdPattern().IsMatch(repositoryId))
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}': the GitHub repository id is the repository's number (the token's repository_id), not '{repositoryId}'.");
        }

        var gitRef = (GitHubRef ?? string.Empty).Trim();
        if (gitRef.Length == 0 || gitRef.Length > 256 || gitRef.Any(char.IsWhiteSpace))
        {
            throw new ExternalTriggerDefinitionException(
                $"Trigger '{name}' needs the git ref a workflow must run on, such as refs/heads/main ('*' matches any run of characters, so '*' alone trusts every ref).");
        }

        if (gitRef != "*" && !gitRef.StartsWith("refs/", StringComparison.Ordinal))
        {
            // A bare branch name is what a person usually means; GitHub's token states the full ref.
            gitRef = "refs/heads/" + gitRef;
        }

        var workflow = string.IsNullOrWhiteSpace(GitHubWorkflow) ? null : GitHubWorkflow.Trim();
        if (workflow is not null)
        {
            if (workflow.StartsWith("./", StringComparison.Ordinal))
            {
                workflow = workflow[2..];
            }

            if (!workflow.Contains('/', StringComparison.Ordinal))
            {
                // A bare file name is the workflow folder's.
                workflow = ".github/workflows/" + workflow;
            }

            if (workflow.Length > 256 || workflow.StartsWith('/') || workflow.Contains("..", StringComparison.Ordinal)
                || workflow.Contains('@', StringComparison.Ordinal) || workflow.Any(char.IsWhiteSpace)
                || !(workflow.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) || workflow.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ExternalTriggerDefinitionException(
                    $"Trigger '{name}': the workflow is the workflow file's path in the repository, such as .github/workflows/deliver.yml; '{GitHubWorkflow}' is not.");
            }
        }

        var environment = string.IsNullOrWhiteSpace(GitHubEnvironment) ? null : GitHubEnvironment.Trim();
        if (environment is { Length: > 256 } || environment?.Any(char.IsControl) == true)
        {
            throw new ExternalTriggerDefinitionException($"Trigger '{name}': the environment name is longer than 256 characters or holds a control character.");
        }

        return new ExternalTriggerDefinition(
            name, kind, repo, description, AllSchedules, schedules, repository, repositoryId, gitRef, workflow, environment);
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryPattern();

    [GeneratedRegex(@"^[0-9]{1,20}$")]
    private static partial Regex RepositoryIdPattern();
}

/// <summary>A trigger the configuration names, as the control plane writes it at start: its definition, its key's hash for
/// a key trigger (resolved from the configured secret), and the setting that names it.</summary>
public sealed record ConfiguredExternalTrigger(ExternalTriggerDefinition Definition, string? KeyHash, string? KeyPrefix, string Setting);

/// <summary>What writing the configured triggers changed: the triggers added, updated, and released.</summary>
public sealed record ConfiguredExternalTriggersApplied(IReadOnlyList<string> Registered, IReadOnlyList<string> Updated, IReadOnlyList<string> Released)
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

/// <summary>How a write to a trigger went, so the API can answer precisely.</summary>
public enum ExternalTriggerMutation
{
    Applied,
    NotFound,

    /// <summary>Another trigger already has the name.</summary>
    NameTaken,

    /// <summary>The configuration sets the trigger; the API may not change or remove it.</summary>
    Configured,

    /// <summary>The write would change the trigger's kind, which a trigger keeps for life.</summary>
    KindChanged,

    /// <summary>The write applies to a key trigger only (a key rotation on a GitHub trigger).</summary>
    NotAKeyTrigger,
}

/// <summary>
/// Persistence for the external triggers: create, change, enable, remove and list them, rotate a key trigger's key, find
/// the triggers a GitHub token's repository or a presented key's hash names, and write the triggers the configuration
/// names. Stateless like the other catalog stores; proving a caller and matching its claims is the control plane's job.
/// </summary>
public static class ExternalTriggerStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The schedule names a trigger stores, as the JSON array <see cref="CatalogExternalTrigger.Schedules"/> holds.</summary>
    public static string? SerializeSchedules(IReadOnlyList<string> schedules)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        return schedules.Count == 0 ? null : JsonSerializer.Serialize(schedules, Json);
    }

    /// <summary>The schedule names of <see cref="CatalogExternalTrigger.Schedules"/>; none for an empty or unreadable value.</summary>
    public static IReadOnlyList<string> ParseSchedules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json)?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Whether <paramref name="trigger"/> may fire the schedule named <paramref name="schedule"/> (names compare
    /// without regard to case, as schedule names are unique in a repository without regard to case).</summary>
    public static bool MayFire(CatalogExternalTrigger trigger, string schedule)
    {
        ArgumentNullException.ThrowIfNull(trigger);
        ArgumentException.ThrowIfNullOrWhiteSpace(schedule);
        return trigger.AllSchedules
            || ParseSchedules(trigger.Schedules).Contains(schedule.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Every trigger, by name.</summary>
    public static async Task<IReadOnlyList<CatalogExternalTrigger>> ListAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.ExternalTriggers.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The trigger with id <paramref name="id"/>, or null.</summary>
    public static Task<CatalogExternalTrigger?> FindAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.ExternalTriggers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
    }

    /// <summary>
    /// Creates a trigger from <paramref name="definition"/> (already <see cref="ExternalTriggerDefinition.Normalized"/>),
    /// with a key trigger's key hash and prefix, and returns its id; <see cref="ExternalTriggerMutation.NameTaken"/> when
    /// another trigger has the name.
    /// </summary>
    public static Task<(ExternalTriggerMutation Outcome, Guid Id)> CreateAsync(
        CatalogDbContext catalog, ExternalTriggerDefinition definition, string? keyHash, string? keyPrefix, DateTime? keyExpiresUtc,
        string createdBy, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);
        if (definition.Kind == ExternalTriggerKinds.Key && (string.IsNullOrWhiteSpace(keyHash) || string.IsNullOrWhiteSpace(keyPrefix)))
        {
            throw new ArgumentException("A key trigger is created with its key's hash and prefix.", nameof(keyHash));
        }

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            if (await catalog.ExternalTriggers.AnyAsync(t => t.Name == definition.Name, ct).ConfigureAwait(false))
            {
                return (ExternalTriggerMutation.NameTaken, Guid.Empty);
            }

            var trigger = new CatalogExternalTrigger
            {
                Id = Guid.NewGuid(),
                CreatedBy = createdBy.Trim(),
                CreatedUtc = nowUtc,
                Enabled = true,
                KeyHash = definition.Kind == ExternalTriggerKinds.Key ? keyHash : null,
                KeyPrefix = definition.Kind == ExternalTriggerKinds.Key ? keyPrefix : null,
                KeyExpiresUtc = definition.Kind == ExternalTriggerKinds.Key ? keyExpiresUtc : null,
            };
            Apply(trigger, definition, nowUtc);
            catalog.ExternalTriggers.Add(trigger);
            return (ExternalTriggerMutation.Applied, trigger.Id);
        }, ct);
    }

    /// <summary>
    /// Changes trigger <paramref name="id"/> to <paramref name="definition"/> (already normalized). The kind never changes
    /// and a configured trigger is never changed through here; a renamed trigger keeps its id, so its requests stay its.
    /// </summary>
    public static Task<ExternalTriggerMutation> UpdateAsync(
        CatalogDbContext catalog, Guid id, ExternalTriggerDefinition definition, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(definition);
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var trigger = await catalog.ExternalTriggers.AsTracking().FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (trigger is null)
            {
                return ExternalTriggerMutation.NotFound;
            }

            if (trigger.ConfiguredBy is not null)
            {
                return ExternalTriggerMutation.Configured;
            }

            if (!string.Equals(trigger.Kind, definition.Kind, StringComparison.Ordinal))
            {
                return ExternalTriggerMutation.KindChanged;
            }

            if (!string.Equals(trigger.Name, definition.Name, StringComparison.Ordinal)
                && await catalog.ExternalTriggers.AnyAsync(t => t.Name == definition.Name && t.Id != id, ct).ConfigureAwait(false))
            {
                return ExternalTriggerMutation.NameTaken;
            }

            Apply(trigger, definition, nowUtc);
            return ExternalTriggerMutation.Applied;
        }, ct);
    }

    /// <summary>Turns trigger <paramref name="id"/> on or off. A configured trigger is set by its configuration.</summary>
    public static async Task<ExternalTriggerMutation> SetEnabledAsync(
        CatalogDbContext catalog, Guid id, bool enabled, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var trigger = await catalog.ExternalTriggers.AsNoTracking()
            .Where(t => t.Id == id).Select(t => new { t.ConfiguredBy }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return ExternalTriggerMutation.NotFound;
        }

        if (trigger.ConfiguredBy is not null)
        {
            return ExternalTriggerMutation.Configured;
        }

        await catalog.ExternalTriggers.Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.Enabled, enabled).SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return ExternalTriggerMutation.Applied;
    }

    /// <summary>Gives key trigger <paramref name="id"/> a new key (its hash and prefix), so the old key stops working at once.</summary>
    public static Task<ExternalTriggerMutation> RotateKeyAsync(
        CatalogDbContext catalog, Guid id, string keyHash, string keyPrefix, DateTime? keyExpiresUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPrefix);
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var trigger = await catalog.ExternalTriggers.AsTracking().FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
            if (trigger is null)
            {
                return ExternalTriggerMutation.NotFound;
            }

            if (trigger.ConfiguredBy is not null)
            {
                return ExternalTriggerMutation.Configured;
            }

            if (trigger.Kind != ExternalTriggerKinds.Key)
            {
                return ExternalTriggerMutation.NotAKeyTrigger;
            }

            trigger.KeyHash = keyHash;
            trigger.KeyPrefix = keyPrefix;
            trigger.KeyExpiresUtc = keyExpiresUtc;
            trigger.UpdatedUtc = nowUtc;
            return ExternalTriggerMutation.Applied;
        }, ct);
    }

    /// <summary>
    /// Removes trigger <paramref name="id"/>. Its requests stay, naming it by the name it had (they keep the trigger's
    /// name), so the record of what it asked for outlives it. A configured trigger is removed by its configuration.
    /// </summary>
    public static async Task<ExternalTriggerMutation> DeleteAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var trigger = await catalog.ExternalTriggers.AsNoTracking()
            .Where(t => t.Id == id).Select(t => new { t.ConfiguredBy }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (trigger is null)
        {
            return ExternalTriggerMutation.NotFound;
        }

        if (trigger.ConfiguredBy is not null)
        {
            return ExternalTriggerMutation.Configured;
        }

        await catalog.ExternalTriggers.Where(t => t.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return ExternalTriggerMutation.Applied;
    }

    /// <summary>Every enabled GitHub trigger: a handful of rows, matched against a token's claims by the caller.</summary>
    public static async Task<IReadOnlyList<CatalogExternalTrigger>> ListEnabledGitHubAsync(CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.ExternalTriggers.AsNoTracking()
            .Where(t => t.Enabled && t.Kind == ExternalTriggerKinds.GitHub)
            .OrderBy(t => t.Name)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The enabled key trigger whose key hashes to <paramref name="keyHash"/> and has not expired, or null. Unknown,
    /// disabled and expired keys all answer null, so a caller cannot tell which it holds.</summary>
    public static Task<CatalogExternalTrigger?> FindByKeyHashAsync(
        CatalogDbContext catalog, string keyHash, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);
        return catalog.ExternalTriggers.AsNoTracking()
            .Where(t => t.KeyHash == keyHash && t.Enabled && t.Kind == ExternalTriggerKinds.Key
                        && (t.KeyExpiresUtc == null || t.KeyExpiresUtc > nowUtc))
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Records that trigger <paramref name="id"/> was just used, and by whom its proof says.</summary>
    public static Task RecordUsedAsync(CatalogDbContext catalog, Guid id, string detail, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(detail);
        var bounded = detail.Length > 1000 ? detail[..1000] : detail;
        return catalog.ExternalTriggers.Where(t => t.Id == id)
            .ExecuteUpdateAsync(t => t.SetProperty(x => x.LastUsedUtc, nowUtc).SetProperty(x => x.LastUsedDetail, bounded), ct);
    }

    /// <summary>
    /// Writes the triggers the configuration names, in one transaction: registers each the catalog does not hold, brings
    /// each it holds in line (an administrator's trigger of the same name is taken over by the configuration), and
    /// releases each configured trigger the configuration no longer names, disabling it so nothing the configuration
    /// stopped trusting keeps working. Writing the same configuration again changes nothing. A configured trigger is
    /// always enabled.
    /// </summary>
    public static Task<ConfiguredExternalTriggersApplied> ApplyConfiguredAsync(
        CatalogDbContext catalog, IReadOnlyList<ConfiguredExternalTrigger> configured, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(configured);
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var registered = new List<string>();
            var updated = new List<string>();
            var released = new List<string>();
            var named = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in configured)
            {
                var definition = entry.Definition;
                named.Add(definition.Name);
                var existing = await catalog.ExternalTriggers.AsTracking()
                    .FirstOrDefaultAsync(t => t.Name == definition.Name, ct).ConfigureAwait(false);
                if (existing is null)
                {
                    var trigger = new CatalogExternalTrigger
                    {
                        Id = Guid.NewGuid(),
                        CreatedBy = entry.Setting,
                        CreatedUtc = nowUtc,
                    };
                    Apply(trigger, definition, nowUtc);
                    trigger.Enabled = true;
                    trigger.ConfiguredBy = entry.Setting;
                    trigger.KeyHash = entry.KeyHash;
                    trigger.KeyPrefix = entry.KeyPrefix;
                    catalog.ExternalTriggers.Add(trigger);
                    registered.Add(definition.Name);
                    continue;
                }

                var schedules = SerializeSchedules(definition.Schedules);
                var changed = existing.Kind != definition.Kind || existing.RepoName != definition.RepoName
                    || existing.Description != definition.Description || existing.AllSchedules != definition.AllSchedules
                    || existing.Schedules != schedules || existing.GitHubRepository != definition.GitHubRepository
                    || existing.GitHubRepositoryId != definition.GitHubRepositoryId || existing.GitHubRef != definition.GitHubRef
                    || existing.GitHubWorkflow != definition.GitHubWorkflow || existing.GitHubEnvironment != definition.GitHubEnvironment
                    || existing.KeyHash != entry.KeyHash || existing.KeyPrefix != entry.KeyPrefix || existing.KeyExpiresUtc is not null
                    || !existing.Enabled || existing.ConfiguredBy != entry.Setting || existing.Name != definition.Name;
                if (!changed)
                {
                    continue;
                }

                // A kind change is allowed here and nowhere else: the configuration owns the trigger outright.
                existing.Kind = definition.Kind;
                Apply(existing, definition, nowUtc);
                existing.Enabled = true;
                existing.ConfiguredBy = entry.Setting;
                existing.KeyHash = entry.KeyHash;
                existing.KeyPrefix = entry.KeyPrefix;
                existing.KeyExpiresUtc = null;
                updated.Add(definition.Name);
            }

            var stale = await catalog.ExternalTriggers.AsTracking()
                .Where(t => t.ConfiguredBy != null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var trigger in stale.Where(t => !named.Contains(t.Name)))
            {
                trigger.ConfiguredBy = null;
                trigger.Enabled = false;
                trigger.UpdatedUtc = nowUtc;
                released.Add(trigger.Name);
            }

            await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            return new ConfiguredExternalTriggersApplied(registered, updated, released);
        }, ct);
    }

    /// <summary>Copies a normalized definition's fields onto <paramref name="trigger"/> (all but the kind, which the
    /// callers set or check, and the key, which only creation, rotation and the configuration write).</summary>
    private static void Apply(CatalogExternalTrigger trigger, ExternalTriggerDefinition definition, DateTime nowUtc)
    {
        trigger.Name = definition.Name;
        trigger.Kind = definition.Kind;
        trigger.Description = definition.Description;
        trigger.RepoName = definition.RepoName;
        trigger.AllSchedules = definition.AllSchedules;
        trigger.Schedules = SerializeSchedules(definition.Schedules);
        trigger.GitHubRepository = definition.GitHubRepository;
        trigger.GitHubRepositoryId = definition.GitHubRepositoryId;
        trigger.GitHubRef = definition.GitHubRef;
        trigger.GitHubWorkflow = definition.GitHubWorkflow;
        trigger.GitHubEnvironment = definition.GitHubEnvironment;
        trigger.UpdatedUtc = nowUtc;
    }
}
