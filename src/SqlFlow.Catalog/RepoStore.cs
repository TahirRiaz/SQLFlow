using Microsoft.EntityFrameworkCore;

namespace SqlFlow.Catalog;

/// <summary>How many catalog rows a repo deletion removed, so the API can report the impact of an irreversible
/// purge. <see cref="SourceRemoved"/> is whether a matching managed git source was also dropped (so the deleted
/// repo cannot resurrect on the next background sync).</summary>
public sealed record RepoDeletionResult(
    int Pipelines, int Runs, int RunGroups, int Schedules, int LineageEdges, bool SourceRemoved);

/// <summary>
/// Persistence for the catalog's repos: today the one mutation is deleting a repo and everything attributed to it.
/// A repo owns no rows through a foreign key (the catalog uses soft links across the board, so run history can
/// outlive a pipeline leaving git), so a delete is an explicit, ordered purge of every repo-scoped table rather
/// than a cascade the database performs. Global rows a repo only contributes to (<see cref="CatalogObject"/> and
/// its columns, content-addressed <see cref="CatalogFlowVersion"/> rows shared across repos) are deliberately left
/// in place: the same object can be referenced by flows in other repos, and the sync itself never garbage-collects
/// them either.
/// </summary>
public static class RepoStore
{
    /// <summary>
    /// Deletes a repo and every catalog row attributed to it: its pipelines, runs (and each run's file / assertion /
    /// statement / event / surrogate-key / health-check children), run groups, lineage edges, object relationships,
    /// flow dependencies, pipeline columns, schedules and schedule members, and its transform-column rows. A managed
    /// git source registered under the same name is dropped too, together with its sync activity trace, so the
    /// background sync cannot recreate the repo on its next tick. The whole purge runs in one serializable
    /// transaction, so a repo is either fully gone or untouched. Returns null when no repo has the given id.
    /// </summary>
    public static Task<RepoDeletionResult?> DeleteAsync(
        CatalogDbContext catalog, Guid repoId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return CatalogTransaction.InSerializableAsync<RepoDeletionResult?>(catalog, async () =>
        {
            var repo = await catalog.Repos.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == repoId, ct).ConfigureAwait(false);
            if (repo is null)
            {
                return null;
            }

            // The run children carry a nullable RepoId that may be unstamped on older rows, so they are removed by
            // their parent run id (which is always present and indexed) rather than by RepoId. This must run before
            // the runs themselves are deleted, while the subquery can still resolve the repo's run ids.
            var runIds = catalog.Runs.Where(r => r.RepoId == repoId).Select(r => r.RunId);
            await catalog.RunFiles.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunAssertions.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunStatements.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunEvents.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunSurrogateKeys.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunHealthCheckMetrics.Where(x => runIds.Contains(x.RunId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            // The schema-change feed is stamped with its own RepoId (unlike the older run children), so it is
            // removed by that directly and does not depend on the run subquery still resolving.
            await catalog.SchemaChanges.Where(x => x.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            var runs = await catalog.Runs.Where(r => r.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var runGroups = await catalog.RunGroups.Where(g => g.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var pipelines = await catalog.Pipelines.Where(p => p.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.PipelineColumns.Where(c => c.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var lineageEdges = await catalog.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.SubscriberQueries.Where(q => q.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.Subscribers.Where(s => s.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.ScheduleMembers.Where(m => m.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var schedules = await catalog.Schedules.Where(s => s.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            // A repo and its managed git source are joined by name in every surface (the source drives the sync, the
            // repo is what a sync produced). Dropping the source under the same name is what makes the delete stick:
            // otherwise the background RepoSyncService would re-clone the remote and recreate the repo on its next
            // tick. Its sync activity trace is keyed by the source id, so it is cleared in the same transaction.
            var source = await catalog.RepoSources.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Name == repo.Name, ct).ConfigureAwait(false);
            var sourceRemoved = false;
            if (source is not null)
            {
                await catalog.ActivityEvents
                    .Where(e => e.Kind == ActivityKinds.RepoSync && e.SubjectKey == source.Id.ToString())
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await catalog.RepoSources.Where(s => s.Id == source.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                sourceRemoved = true;
            }

            await catalog.Repos.Where(r => r.Id == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);

            return new RepoDeletionResult(pipelines, runs, runGroups, schedules, lineageEdges, sourceRemoved);
        }, ct);
    }
}
