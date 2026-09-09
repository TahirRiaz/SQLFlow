using Microsoft.EntityFrameworkCore;
using SqlFlow.Core;

namespace SqlFlow.Catalog;

/// <summary>The result of a pause/resume or delete request, so the API can answer 200 / 404 precisely.</summary>
public enum ScheduleMutation
{
    Applied,
    NotFound,
}

/// <summary>
/// Where a git-declared schedule's cadence is written, so the catalog can serve the YAML behind a schedule rather
/// than a reconstruction of it: the repo-relative <paramref name="Path"/> of the declaring file, the
/// <paramref name="Flow"/> whose inline block declares it (null for a <c>schedules.yaml</c>), and, only for a library
/// file, that file's <paramref name="Yaml"/> text. A flow document is deliberately not copied here: its redacted text
/// already lives on its pipeline row.
/// </summary>
public sealed record ScheduleDefinitionSource(string Path, string? Flow, string? Yaml);

/// <summary>
/// Persistence for the schedule table: the scheduler's due scan and atomic fire-claim, plus the upserts that keep
/// it in step with git (YAML schedules) and the API (ad-hoc schedules, pause/resume). Stateless like
/// <see cref="RunQueueStore"/>; the next-fire instants are computed by the control plane (which owns the cron
/// library and the time zone database) and passed in, so the catalog has no scheduling-math dependency.
/// </summary>
public static class ScheduleStore
{
    /// <summary>The active schedules whose next fire has arrived, oldest-due first. Read-only; the caller claims
    /// each one with <see cref="TryClaimFireAsync"/> before acting on it.</summary>
    public static async Task<IReadOnlyList<CatalogSchedule>> ListDueAsync(
        CatalogDbContext catalog, DateTime nowUtc, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.Schedules.AsNoTracking()
            .Where(s => s.Enabled && !s.Paused && s.NextFireUtc != null && s.NextFireUtc <= nowUtc)
            .OrderBy(s => s.NextFireUtc)
            .Take(Math.Clamp(max, 1, 1000))
            .ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The chained (shadow) schedules that are ready to fire: active, driven by parents rather than the clock, where
    /// EVERY declared parent has COMPLETED a fire newer than the one this child last reacted to.
    /// <para>
    /// Readiness is evaluated entirely in the database so the scheduler never pulls the run table into memory. A
    /// parent fire counts as complete when the parent has fired at all and no run it enqueued is still queued or
    /// running: the fire is identified by <see cref="CatalogSchedule.LastGroupId"/> for a multi-member fire and
    /// <see cref="CatalogSchedule.LastRunId"/> for a single-member one. Whether those runs succeeded is deliberately
    /// not considered; see <c>ScheduleSpec.After</c> for why a chain must not be parked by one bad link.
    /// </para>
    /// <para>
    /// THE FAN-IN RULE, which is where one parent and several differ. The child is ready when the OLDEST of its
    /// parents' last fires is newer than <see cref="CatalogSchedule.LastParentFireUtc"/>. Testing the oldest is what
    /// makes it wait for all of them: as long as one parent has not fired since the last consumption, that parent's
    /// instant is the minimum and the test fails. Testing "any parent is newer" instead would fire once per parent
    /// per cycle, which is exactly the coincidence-ordering this replaces. The scheduler then stamps the NEWEST of
    /// the parents' fires, so the next tick sees the same completed set as already consumed.
    /// </para>
    /// A parent that is itself chained is handled by the same rule applied to it, so a chain of any length advances
    /// one link per tick. A cycle simply never becomes ready (no link's parents ever complete a fire the child has
    /// not already consumed), so a mis-declared loop stalls quietly instead of firing forever. A parent named but not
    /// defined stalls the child the same quiet way, because it can never contribute a fire instant.
    /// </summary>
    public static async Task<IReadOnlyList<CatalogSchedule>> ListChainedReadyAsync(
        CatalogDbContext catalog, int max, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Per child: how many parents it declares, how many of those are resolvable and complete, and the oldest and
        // newest fire instants among them. The counts are what enforce "every parent", including the unresolvable
        // ones: a named parent with no schedule row contributes to Declared but not to Ready, so the child never
        // clears the bar.
        var readiness =
            from p in catalog.ScheduleParents.AsNoTracking()
            join parent in catalog.Schedules.AsNoTracking()
                on new { p.RepoId, Name = p.ParentName } equals new { parent.RepoId, parent.Name } into resolved
            from parent in resolved.DefaultIfEmpty()
            select new
            {
                p.ScheduleId,
                Complete = parent != null
                           && parent.LastFireUtc != null
                           && !catalog.Runs.Any(r =>
                                  (parent.LastGroupId != null && r.GroupId == parent.LastGroupId
                                   || parent.LastGroupId == null && r.RunId == parent.LastRunId)
                                  && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running)),
                FireUtc = parent != null ? parent.LastFireUtc : null,
            };

        var aggregated =
            from r in readiness
            group r by r.ScheduleId into g
            select new
            {
                ScheduleId = g.Key,
                Declared = g.Count(),
                Complete = g.Count(x => x.Complete),
                OldestFireUtc = g.Min(x => x.FireUtc),
                NewestFireUtc = g.Max(x => x.FireUtc),
            };

        var query =
            from child in catalog.Schedules.AsNoTracking()
            where child.Enabled && !child.Paused
            join a in aggregated on child.Id equals a.ScheduleId
            where a.Declared == a.Complete
               && a.OldestFireUtc != null
               && (child.LastParentFireUtc == null || a.OldestFireUtc > child.LastParentFireUtc)
            orderby a.NewestFireUtc
            select child;

        return await query.Take(Math.Clamp(max, 1, 1000)).ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything a chained schedule's fire turns on, in one read: the NEWEST of its parents' last fires, which is
    /// what the child stamps as consumed; the names of any parent older than <paramref name="freshnessHours"/>, which
    /// the fire records and logs without being held back by; and the full parent list in declaration order for the
    /// log line. Returns nulls and an empty label when the schedule declares no parents.
    /// </summary>
    public static async Task<(DateTime? NewestFireUtc, string? StaleParents, string ParentLabel)> GetParentFireStateAsync(
        CatalogDbContext catalog, Guid scheduleId, int freshnessHours, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var parents = await (
            from p in catalog.ScheduleParents.AsNoTracking()
            where p.ScheduleId == scheduleId
            join parent in catalog.Schedules.AsNoTracking()
                on new { p.RepoId, Name = p.ParentName } equals new { parent.RepoId, parent.Name } into resolved
            from parent in resolved.DefaultIfEmpty()
            orderby p.Ordinal
            select new { p.ParentName, FireUtc = parent != null ? parent.LastFireUtc : null })
            .ToListAsync(ct).ConfigureAwait(false);

        if (parents.Count == 0)
            return (null, null, string.Empty);

        var newest = parents.Max(p => p.FireUtc);
        var label = string.Join(", ", parents.Select(p => p.ParentName));

        // freshnessHours <= 0 opts out. A parent that has never fired is not reported as stale here: it cannot be,
        // because the child is not ready in the first place until every parent has a fire instant.
        if (freshnessHours <= 0)
            return (newest, null, label);

        var cutoff = nowUtc.AddHours(-freshnessHours);
        var stale = parents.Where(p => p.FireUtc != null && p.FireUtc < cutoff).Select(p => p.ParentName).ToList();

        return (newest, stale.Count == 0 ? null : string.Join(", ", stale), label);
    }

    /// <summary>
    /// Atomically claims a chained schedule's fire by stamping the newest parent fire it is reacting to, from the
    /// value the caller observed (normally null, or the previous consumed instant). Returns true only for the winner,
    /// so when several control-plane nodes see the same completed parent set exactly one child fire is enqueued.
    /// <para>
    /// <paramref name="staleParents"/> is recorded on the same update rather than in a second write, so the row never
    /// shows a fire whose staleness has not caught up with it, and a fire that found everything current clears the
    /// previous reading instead of leaving a stale warning to be misread as current.
    /// </para>
    /// </summary>
    public static async Task<bool> TryClaimChainedFireAsync(
        CatalogDbContext catalog, Guid id, DateTime? observedLastParentFireUtc, DateTime parentFireUtc,
        DateTime nowUtc, string? staleParents = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules
            .Where(s => s.Id == id
                        && (observedLastParentFireUtc == null
                                ? s.LastParentFireUtc == null
                                : s.LastParentFireUtc == observedLastParentFireUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastParentFireUtc, parentFireUtc)
                .SetProperty(x => x.LastStaleParents, staleParents)
                .SetProperty(x => x.LastFireUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// Marks this schedule's fire as already consumed by the schedules chained directly behind it, so a run that was
    /// asked to stay put does not drag its chain along.
    /// <para>
    /// Only the DIRECT children need stamping. A grandchild waits on its own parent's fire, and that parent is not
    /// going to fire, so the whole tail is suppressed by stopping the first link. The stamp is the same value the
    /// child would have recorded had it run, which is why this leaves no residue: the next genuine fire of the parent
    /// carries a later instant and the chain resumes normally.
    /// </para>
    /// </summary>
    public static Task<int> SuppressChainAsync(
        CatalogDbContext catalog, Guid repoId, string parentName, DateTime parentFireUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentName);
        // Only children for which this parent is their ONLY parent are suppressed. A fan-in child is left alone on
        // purpose: stamping it here would mark its whole parent set as consumed on the strength of one parent that
        // chose not to run, and the fire it is actually waiting for (all of them) would be skipped rather than
        // deferred. Such a child simply stays unready until this parent fires for real.
        var soleParentChildren = catalog.ScheduleParents
            .Where(p => p.RepoId == repoId && p.ParentName == parentName
                        && !catalog.ScheduleParents.Any(o => o.ScheduleId == p.ScheduleId && o.ParentName != parentName))
            .Select(p => p.ScheduleId);

        return catalog.Schedules
            .Where(s => soleParentChildren.Contains(s.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.LastParentFireUtc, parentFireUtc), ct);
    }

    /// <summary>The parent's last fire instant, used to stamp a chained child when it fires behind it.</summary>
    public static async Task<DateTime?> GetParentLastFireUtcAsync(
        CatalogDbContext catalog, Guid repoId, string parentName, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return await catalog.Schedules.AsNoTracking()
            .Where(s => s.RepoId == repoId && s.Name == parentName)
            .Select(s => s.LastFireUtc)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically claims a schedule's fire by advancing <see cref="CatalogSchedule.NextFireUtc"/> from the value the
    /// caller observed to the next occurrence. Returns true only if this caller won the race: the compare-and-swap on
    /// the observed next-fire means that when several control-plane nodes scan the same due schedule, exactly one
    /// advances it (and so exactly one enqueues the run); the losers see zero rows affected and move on.
    /// </summary>
    public static async Task<bool> TryClaimFireAsync(
        CatalogDbContext catalog, Guid id, DateTime observedNextFireUtc, DateTime? newNextFireUtc, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules
            .Where(s => s.Id == id && s.NextFireUtc == observedNextFireUtc)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.NextFireUtc, newNextFireUtc)
                .SetProperty(x => x.LastFireUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Records the run a fire enqueued, so a scheduled run traces back to its schedule. Clears
    /// <see cref="CatalogSchedule.LastGroupId"/>: a single-member fire is one run, not a set.</summary>
    public static Task SetLastRunAsync(CatalogDbContext catalog, Guid id, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunId, runId)
                .SetProperty(x => x.LastGroupId, (Guid?)null)
                // Stamped on EVERY dispatch, not just a clock-driven one. LastFireUtc means "when this schedule
                // last dispatched its members", so a manual run-now advances a chained child exactly as a cron
                // fire does. Without this, "run this source now" would start the head and silently leave the rest
                // of the chain behind, which is not what an operator asking for a run means.
                .SetProperty(x => x.LastFireUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>Records the wave-gated group a scoped fire enqueued, so the schedule traces to the whole set (and to
    /// its first member, keeping <see cref="CatalogSchedule.LastRunId"/> meaningful for callers that show one run).</summary>
    public static Task SetLastGroupAsync(
        CatalogDbContext catalog, Guid id, Guid groupId, Guid firstRunId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LastRunId, firstRunId)
                .SetProperty(x => x.LastGroupId, groupId)
                // See SetLastRunAsync: every dispatch stamps the fire instant, so a manually started schedule
                // carries its chain with it instead of stopping at the head.
                .SetProperty(x => x.LastFireUtc, nowUtc)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct);
    }

    /// <summary>
    /// Upserts a YAML-declared schedule (deterministic id per NAME) together with its member set. A new schedule is
    /// inserted with the computed next fire. An existing one has its definition refreshed from git, but the
    /// operational state set through the API is preserved: a <see cref="CatalogSchedule.Paused"/> flag survives the
    /// re-sync, and the next-fire cadence is only reset when the cron/interval/time-zone actually changed (an
    /// unchanged sync never disturbs the firing rhythm). Returns the schedule id.
    /// </summary>
    public static Task<Guid> UpsertYamlScheduleAsync(
        CatalogDbContext catalog, Guid repoId, string scheduleName, IReadOnlyCollection<string> members, string? cron,
        int? intervalSeconds, string timezone, bool enabled, bool catchup, int? maxConcurrency,
        DateTime computedNextFireUtc, DateTime nowUtc, ScheduleDefinitionSource? definition = null,
        IReadOnlyList<string>? afterSchedules = null, int parentFreshnessHours = ScheduleDefaults.ParentFreshnessHours,
        CancellationToken ct = default)
        => CatalogTransaction.InSerializableAsync(
            catalog,
            () => StageYamlUpsertAsync(catalog, repoId, scheduleName, members, cron, intervalSeconds, timezone, enabled, catchup, maxConcurrency, computedNextFireUtc, nowUtc, definition, afterSchedules, parentFreshnessHours, ct),
            ct);

    /// <summary>The transaction-free core of the YAML upsert: it stages the insert/update on the context but does
    /// not open a transaction or save, so the catalog sync can call it for every schedule inside its own one
    /// transaction (the public <see cref="UpsertYamlScheduleAsync"/> wraps this for standalone callers).</summary>
    public static async Task<Guid> StageYamlUpsertAsync(
        CatalogDbContext catalog, Guid repoId, string scheduleName, IReadOnlyCollection<string> members, string? cron,
        int? intervalSeconds, string timezone, bool enabled, bool catchup, int? maxConcurrency,
        DateTime computedNextFireUtc, DateTime nowUtc, ScheduleDefinitionSource? definition = null,
        IReadOnlyList<string>? afterSchedules = null, int parentFreshnessHours = ScheduleDefaults.ParentFreshnessHours,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);

        // A chained schedule is driven by its parents, never by the clock: it keeps no cadence and, crucially, a null
        // next fire, which is what keeps it out of the due scan entirely. Duplicates and blanks are dropped here so
        // the stored set is exactly what the readiness count expects; the first spelling wins, preserving order.
        var parents = (afterSchedules ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var chained = parents.Count > 0;
        var effectiveCron = chained ? null : cron;
        var effectiveInterval = chained ? null : intervalSeconds;
        DateTime? effectiveNextFire = chained ? null : computedNextFireUtc;

        var id = CatalogIdentity.YamlSchedule(repoId, scheduleName);
        var existing = await catalog.Schedules.AsTracking()
            .FirstOrDefaultAsync(s => s.Id == id, ct).ConfigureAwait(false);
        if (existing is null)
        {
            catalog.Schedules.Add(new CatalogSchedule
            {
                Id = id,
                RepoId = repoId,
                Name = scheduleName,
                Cron = effectiveCron,
                IntervalSeconds = effectiveInterval,
                ParentFreshnessHours = parentFreshnessHours,
                Timezone = timezone,
                Enabled = enabled,
                Catchup = catchup,
                MaxConcurrency = maxConcurrency,
                Source = "yaml",
                DefinitionPath = definition?.Path,
                DefinitionFlow = definition?.Flow,
                DefinitionYaml = definition?.Yaml,
                NextFireUtc = effectiveNextFire,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            });
        }
        else
        {
            // Only the TIMING definition resets the cadence. Membership changes what a fire runs, not when, so
            // adding or removing a member must not shift the next fire or disturb the rhythm.
            var definitionChanged = existing.Cron != effectiveCron
                || existing.IntervalSeconds != effectiveInterval
                || existing.Timezone != timezone;

            var currentParents = await catalog.ScheduleParents.AsNoTracking()
                .Where(p => p.ScheduleId == id)
                .Select(p => p.ParentName)
                .ToListAsync(ct).ConfigureAwait(false);
            var chainChanged = !currentParents.OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(parents.OrderBy(p => p, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

            existing.RepoId = repoId;
            existing.Name = scheduleName;
            existing.Cron = effectiveCron;
            existing.IntervalSeconds = effectiveInterval;
            existing.ParentFreshnessHours = parentFreshnessHours;
            existing.Timezone = timezone;
            existing.Enabled = enabled;
            existing.Catchup = catchup;
            existing.MaxConcurrency = maxConcurrency;
            existing.Source = "yaml";
            // Git owns where the definition lives: a block moved from a flow into a schedules.yaml (or the reverse)
            // must repoint the provenance, and the old file's text must not linger.
            existing.DefinitionPath = definition?.Path;
            existing.DefinitionFlow = definition?.Flow;
            existing.DefinitionYaml = definition?.Yaml;
            existing.UpdatedUtc = nowUtc;

            // Re-pointing the chain restarts it: the consumed-parent stamp refers to the OLD parent set's fire clock
            // and would be meaningless (and could suppress the first fire) against a different one. The recorded
            // staleness goes with it, since it describes a fire that no longer means anything.
            if (chainChanged)
            {
                existing.LastParentFireUtc = null;
                existing.LastStaleParents = null;
            }

            if (chained)
            {
                // Becoming chained must retire any pending clock occurrence, or the schedule would fire on both.
                existing.NextFireUtc = null;
            }
            else if (definitionChanged || chainChanged || existing.NextFireUtc is null)
            {
                // Only reset the cadence when the timing definition changed; an unchanged re-sync leaves the next fire
                // (and the operator's pause) exactly as they were.
                existing.NextFireUtc = computedNextFireUtc;
            }
        }

        await StageMembersAsync(catalog, repoId, id, members, ct).ConfigureAwait(false);
        await StageParentsAsync(catalog, repoId, id, parents, ct).ConfigureAwait(false);
        return id;
    }

    /// <summary>
    /// Replaces a schedule's parent set with exactly <paramref name="parents"/>, in declaration order. Git is the
    /// authority on what a schedule chains behind, so a parent dropped from the YAML stops holding the fire back and
    /// a new one starts to. Rows already correct are left untouched rather than deleted and reinserted, so an
    /// unchanged sync writes nothing.
    /// </summary>
    private static async Task StageParentsAsync(
        CatalogDbContext catalog, Guid repoId, Guid scheduleId, IReadOnlyList<string> parents, CancellationToken ct)
    {
        var existing = await catalog.ScheduleParents.AsTracking()
            .Where(p => p.ScheduleId == scheduleId)
            .ToListAsync(ct).ConfigureAwait(false);

        var wanted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < parents.Count; i++)
            wanted[parents[i]] = i;

        foreach (var row in existing)
        {
            if (wanted.TryGetValue(row.ParentName, out var ordinal))
            {
                // Keep the row, but let a reordered declaration re-render in the API the way the author wrote it.
                if (row.Ordinal != ordinal)
                    row.Ordinal = ordinal;
                if (row.RepoId != repoId)
                    row.RepoId = repoId;
                wanted.Remove(row.ParentName);
            }
            else
            {
                catalog.ScheduleParents.Remove(row);
            }
        }

        foreach (var (name, ordinal) in wanted)
        {
            catalog.ScheduleParents.Add(new CatalogScheduleParent
            {
                ScheduleId = scheduleId,
                RepoId = repoId,
                ParentName = name,
                Ordinal = ordinal,
            });
        }
    }

    /// <summary>
    /// Replaces a schedule's member set with exactly <paramref name="members"/>: git is the authority on who joined,
    /// so a flow that dropped its <c>schedule:</c> line stops being fired and a new joiner starts. Rows already
    /// correct are left untouched rather than deleted and reinserted, so an unchanged sync writes nothing.
    /// </summary>
    private static async Task StageMembersAsync(
        CatalogDbContext catalog, Guid repoId, Guid scheduleId, IReadOnlyCollection<string> members, CancellationToken ct)
    {
        var wanted = members
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToDictionary(m => CatalogIdentity.Pipeline(repoId, m), m => m);

        var existing = await catalog.ScheduleMembers.AsTracking()
            .Where(m => m.ScheduleId == scheduleId)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in existing)
        {
            if (!wanted.Remove(row.PipelineId))
            {
                catalog.ScheduleMembers.Remove(row);
            }
        }

        foreach (var (pipelineId, flowName) in wanted)
        {
            catalog.ScheduleMembers.Add(new CatalogScheduleMember
            {
                ScheduleId = scheduleId,
                PipelineId = pipelineId,
                RepoId = repoId,
                FlowName = flowName,
            });
        }
    }

    /// <summary>Stages the removal of YAML schedules for a repo whose flows left the estate (so a schedule deleted
    /// from git stops firing), without opening a transaction or saving: it loads the stale rows and marks them for
    /// deletion so the caller's sync transaction removes them. API-created schedules are never touched.</summary>
    public static async Task StageRemoveYamlSchedulesNotInAsync(
        CatalogDbContext catalog, Guid repoId, IReadOnlyCollection<Guid> keepIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var stale = await catalog.Schedules.AsTracking()
            .Where(s => s.RepoId == repoId && s.Source == "yaml" && !keepIds.Contains(s.Id))
            .ToListAsync(ct).ConfigureAwait(false);
        if (stale.Count == 0)
        {
            return;
        }

        // The memberships go with the schedule: nothing else references them, and a member row left behind would
        // survive as an orphan that no fire could ever reach.
        var staleIds = stale.Select(s => s.Id).ToList();
        var orphanedMembers = await catalog.ScheduleMembers.AsTracking()
            .Where(m => staleIds.Contains(m.ScheduleId))
            .ToListAsync(ct).ConfigureAwait(false);
        catalog.ScheduleMembers.RemoveRange(orphanedMembers);
        catalog.Schedules.RemoveRange(stale);
    }

    /// <summary>Creates an ad-hoc API schedule with a fresh id and an explicit member set. A repo can carry its git
    /// schedules plus API ones; the name must not collide with a git schedule's (the caller checks, and the unique
    /// index is the backstop).</summary>
    public static Task<Guid> CreateApiScheduleAsync(
        CatalogDbContext catalog, Guid repoId, string scheduleName, IReadOnlyCollection<string> members, string? cron,
        int? intervalSeconds, string timezone, bool enabled, bool catchup, int? maxConcurrency,
        DateTime computedNextFireUtc, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(members);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheduleName);

        var id = Guid.CreateVersion7();
        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            catalog.Schedules.Add(new CatalogSchedule
            {
                Id = id,
                RepoId = repoId,
                Name = scheduleName,
                Cron = cron,
                IntervalSeconds = intervalSeconds,
                Timezone = timezone,
                Enabled = enabled,
                Catchup = catchup,
                MaxConcurrency = maxConcurrency,
                Source = "api",
                NextFireUtc = computedNextFireUtc,
                CreatedUtc = nowUtc,
                UpdatedUtc = nowUtc,
            });
            await StageMembersAsync(catalog, repoId, id, members, ct).ConfigureAwait(false);
            return id;
        }, ct);
    }

    /// <summary>Pauses or resumes a schedule (the API operational override). Resuming sets a fresh next fire so a
    /// schedule paused across many missed occurrences does not fire a burst on resume.</summary>
    public static async Task<ScheduleMutation> SetPausedAsync(
        CatalogDbContext catalog, Guid id, bool paused, DateTime? nextFireUtcOnResume, DateTime nowUtc,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var affected = await catalog.Schedules
            .Where(s => s.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Paused, paused)
                .SetProperty(x => x.NextFireUtc, x => paused ? x.NextFireUtc : nextFireUtcOnResume)
                .SetProperty(x => x.UpdatedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        return affected > 0 ? ScheduleMutation.Applied : ScheduleMutation.NotFound;
    }

    /// <summary>Deletes a schedule by id, with its memberships (the API delete).</summary>
    public static async Task<ScheduleMutation> DeleteAsync(CatalogDbContext catalog, Guid id, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        await catalog.ScheduleMembers.Where(m => m.ScheduleId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var affected = await catalog.Schedules.Where(s => s.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        return affected > 0 ? ScheduleMutation.Applied : ScheduleMutation.NotFound;
    }
}
