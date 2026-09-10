using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The read API over the shadow catalog: repositories and pipelines, paginated and projected to DTOs (the raw EF
/// entities never leave the host). Every query is read-only (<see cref="EntityFrameworkQueryableExtensions.AsNoTracking"/>)
/// and bounded by page size. Lineage, runs, and search are mapped by their own modules; this is the registry view.
/// </summary>
public static class CatalogEndpoints
{
    public static RouteGroupBuilder MapCatalogEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapGet("/", ListReposAsync).WithName("ListRepositories");
        repos.MapGet("/{id:guid}", GetRepoAsync).WithName("GetRepository");

        var pipelines = group.MapGroup("/pipelines").WithTags("Pipelines");
        pipelines.MapGet("/", ListPipelinesAsync).WithName("ListPipelines");
        pipelines.MapGet("/batches", ListPipelineBatchesAsync).WithName("ListPipelineBatches");
        pipelines.MapGet("/projects", ListPipelineProjectsAsync).WithName("ListPipelineProjects");
        pipelines.MapGet("/{id:guid}", GetPipelineAsync).WithName("GetPipeline");
        pipelines.MapGet("/{id:guid}/definition", GetPipelineDefinitionAsync).WithName("GetPipelineDefinition");
        pipelines.MapGet("/{id:guid}/parameters", GetPipelineParametersAsync).WithName("GetPipelineParameters");
        pipelines.MapGet("/{id:guid}/columns", GetPipelineColumnsAsync).WithName("GetPipelineColumns");
        pipelines.MapGet("/{id:guid}/files", GetPipelineFilesAsync).WithName("GetPipelineFiles");
        pipelines.MapGet("/{id:guid}/files/stats", GetPipelineFileStatsAsync).WithName("GetPipelineFileStats");

        var schema = group.MapGroup("/schema-changes").WithTags("SchemaChanges");
        schema.MapGet("/", ListSchemaChangesAsync).WithName("ListSchemaChanges");
        schema.MapGet("/databases", ListSchemaChangeDatabasesAsync).WithName("ListSchemaChangeDatabases");

        return group;
    }

    /// <summary>
    /// The catalog mutations, mapped under the "operate" scope: a manual sync of a local-path repo. A repo tracked by
    /// a git source is kept current by that source's managed sync, so this serves the repos synced by the CLI from a
    /// filesystem path (which the background <see cref="Background.RepoSyncService"/> never polls), giving the GUI a
    /// "Sync now" for them without dropping to the CLI.
    /// </summary>
    public static RouteGroupBuilder MapCatalogWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapPost("/{id:guid}/sync", SyncRepoAsync).WithName("SyncRepository");
        repos.MapDelete("/{id:guid}", DeleteRepoAsync).WithName("DeleteRepository");

        return group;
    }

    /// <summary>
    /// Deletes a repo and everything attributed to it: its pipelines, run history (and the runs' drill-down detail),
    /// run groups, lineage (edges, object relationships, flow dependencies), schedules, and transform columns, plus
    /// any managed git source registered under the same name (dropped so the background sync cannot recreate the
    /// repo). Irreversible, so it is an explicit operator action rather than a side effect of a sync. Global objects
    /// the repo only contributed to are left in place (they can be shared by other repos). Returns the counts removed,
    /// or 404 when no repo has the given id.
    /// </summary>
    private static async Task<Results<Ok<RepoDeletionResult>, ProblemHttpResult>> DeleteRepoAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var result = await RepoStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return result is null ? NotFound("repository", id) : TypedResults.Ok(result);
    }

    /// <summary>
    /// Re-sync a local-path repo from its recorded root path, running the same connected catalog sync the CLI's
    /// <c>db sync --connect</c> runs: the flow estate is mirrored and the derived lineage tier is read from the live
    /// database, so the lineage graph (including the object-level view) refreshes. The sync runs inline; a small repo
    /// completes in one request. Refused for a repo managed by a git source (its root path is a transient clone
    /// cache, so a re-sync from it would reflect a stale checkout: trigger the source's own sync instead) and for a
    /// root path the control-plane host cannot see (a local-path sync only works where the flows live on disk).
    /// </summary>
    private static async Task<Results<Ok<RepoSyncResultDto>, ProblemHttpResult>> SyncRepoAsync(
        Guid id, CatalogDbContext db, ISecretResolver secrets, TimeProvider clock, CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.Name, r.RemoteUrl, r.RootPath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            return NotFound("repository", id);
        }

        // A repo tracked by a git source is kept current by the managed sync (RepoSyncService), whose root path is a
        // transient clone cache; a local-path sync from it would reflect a stale checkout. Direct the caller to the
        // source's own sync so each repo has a single, unambiguous sync path.
        var hasSource = await db.RepoSources.AsNoTracking()
            .AnyAsync(s => s.Name == repo.Name, ct).ConfigureAwait(false);
        if (hasSource)
        {
            return BadRequest(
                "Managed by a git source",
                $"Repo '{repo.Name}' is synced from a registered git source; trigger its sync from the repo source instead of a local-path sync.");
        }

        if (string.IsNullOrWhiteSpace(repo.RootPath))
        {
            return BadRequest("No local path", $"Repo '{repo.Name}' has no recorded root path to sync from.");
        }

        if (!Directory.Exists(repo.RootPath))
        {
            return BadRequest(
                "Path not reachable",
                $"The control-plane host cannot see the repo's root path '{repo.RootPath}'. A local-path sync only works when the control plane runs on the machine that holds the flow files.");
        }

        try
        {
            // The same connected sync the CLI's `db sync --connect` runs: one sync path, with the derived tier (live
            // catalog + sys.sql_modules reads) enabled so the object-level lineage populates. A derived-tier failure
            // (an unreachable database, a timeout) is downgraded to a warning inside SyncAsync and never faults the
            // request; the flow registry and flow-level lineage still land.
            var result = await new CatalogSync().SyncAsync(
                db, repo.RootPath, repo.Name, repo.RemoteUrl, clock.GetUtcNow().UtcDateTime,
                includeDerived: true, secrets: secrets, ct: ct).ConfigureAwait(false);

            return TypedResults.Ok(new RepoSyncResultDto(
                result.PipelinesAdded, result.PipelinesUpdated, result.PipelinesUnchanged, result.PipelinesDeactivated, result.PipelinesDeleted,
                result.ObjectsUpserted, result.ObjectColumns, result.LineageEdges, result.Waves, result.FlowDependencies,
                result.LineageConnected, result.Warnings.Take(20).ToArray()));
        }
        catch (SqlFlowException ex)
        {
            // A domain-level failure of the sync itself (e.g. a flow document embedding a credential): report it as a
            // clean, secret-redacted 400 rather than a 500. Infrastructure faults (catalog writes) bubble to the
            // correlation-id error handler.
            return BadRequest("Sync failed", SecretHygiene.RedactedMessage(ex));
        }
    }

    private static async Task<Ok<PagedResult<RepoDto>>> ListReposAsync(
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.Repos.AsNoTracking().OrderBy(r => r.Name).ThenBy(r => r.Id);
        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .Skip((p - 1) * size).Take(size)
            .Select(r => new RepoDto(r.Id, r.Name, r.RemoteUrl, r.RootPath, r.FirstSeenUtc, r.LastSyncUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<RepoDto>(items, p, size, total));
    }

    private static async Task<Results<Ok<RepoDto>, ProblemHttpResult>> GetRepoAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new RepoDto(r.Id, r.Name, r.RemoteUrl, r.RootPath, r.FirstSeenUtc, r.LastSyncUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("repository", id) : TypedResults.Ok(dto);
    }

    private static async Task<Ok<PagedResult<PipelineSummaryDto>>> ListPipelinesAsync(
        CatalogDbContext db, Guid? repoId, string? kind, bool? active, string? name, string? project,
        string? batch, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);

        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(x => x.Kind == kind);
        }

        if (active is { } a)
        {
            query = query.Where(x => x.Active == a);
        }

        // The default batch is a presentation label for flows that declare none, so filtering by it must match
        // both the literal label and the null rows it stands in for (mirrors the coalescing in the batches list).
        if (!string.IsNullOrWhiteSpace(batch))
        {
            query = batch == CatalogPipeline.DefaultBatch
                ? query.Where(x => x.Batch == null || x.Batch == batch)
                : query.Where(x => x.Batch == batch);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            query = query.Where(x => x.Name.Contains(name));
        }

        // The project is a flow's root folder within its repo (the first path segment, or "(root)" for a flow at
        // the repo root): narrow the list to one source's folder. Root flows have no slash; a named project matches
        // its folder prefix so nested folders under it stay included.
        if (!string.IsNullOrWhiteSpace(project))
        {
            // Contains("/") stays a string overload: this predicate is translated to SQL by EF Core, and the
            // char overload CA1847 suggests is not guaranteed to translate to the same CHARINDEX.
#pragma warning disable CA1847
            query = project == ProjectPath.Root
                ? query.Where(x => !x.RelativePath.Contains("/"))
                : query.Where(x => x.RelativePath.StartsWith(project + "/"));
#pragma warning restore CA1847
        }

        var ordered = query.OrderBy(x => x.Name).ThenBy(x => x.Id);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var items = await ordered
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineSummaryDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active, x.ExecutionMode, x.Lifecycle,
                x.SourceServer, x.TargetServer, x.RelativePath, x.FirstSeenUtc, x.LastSeenUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineSummaryDto>(items, p, size, total));
    }

    /// <summary>
    /// The distinct batches (source-system groupings) of the catalog's flows, optionally scoped to one repo, each
    /// with its flow counts, in one response (no paging): the distinct batches are bounded by the estate's source
    /// count, not its flow count. A flow that declares no batch is coalesced into
    /// <see cref="CatalogPipeline.DefaultBatch"/>, matching how the pipelines list's batch filter resolves it.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<PipelineBatchDto>>> ListPipelineBatchesAsync(
        CatalogDbContext db, Guid? repoId, bool? active, CancellationToken ct)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        if (active is { } a)
        {
            query = query.Where(x => x.Active == a);
        }

        var groups = await query
            .GroupBy(x => new { x.RepoId, Batch = x.Batch ?? CatalogPipeline.DefaultBatch })
            .Select(g => new PipelineBatchDto(
                g.Key.RepoId, g.Key.Batch, g.Count(), g.Count(x => x.Active)))
            .ToListAsync(ct).ConfigureAwait(false);

        var ordered = groups
            .OrderBy(b => b.RepoId)
            .ThenBy(b => b.Batch, StringComparer.Ordinal)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<PipelineBatchDto>>(ordered);
    }

    /// <summary>
    /// The distinct projects (repo-root folders) the pipelines list can be filtered by, optionally scoped to one
    /// repo, sorted for a stable dropdown. Derived from each flow's repo-relative path (see <see cref="ProjectPath"/>):
    /// the segment before the first slash, or the root project for a flow at the repo root. Distinct paths are pulled
    /// once and reduced in memory (the segment split is not worth pushing into SQL for the catalog's cardinality).
    /// </summary>
    private static async Task<Ok<IReadOnlyList<string>>> ListPipelineProjectsAsync(
        CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        var query = db.Pipelines.AsNoTracking().AsQueryable();
        if (repoId is { } r)
        {
            query = query.Where(x => x.RepoId == r);
        }

        var paths = await query.Select(x => x.RelativePath).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var projects = paths
            .Select(ProjectPath.Of)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return TypedResults.Ok<IReadOnlyList<string>>(projects);
    }

    private static async Task<Results<Ok<PipelineDetailDto>, ProblemHttpResult>> GetPipelineAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var dto = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new PipelineDetailDto(
                x.Id, x.RepoId, x.Name, x.Kind, x.Batch, x.Wave, x.Active, x.ExecutionMode, x.Lifecycle,
                x.SourceServer, x.TargetServer, x.RelativePath, x.ContentHash,
                x.Yaml, x.DefinitionJson, x.FirstSeenUtc, x.LastSeenUtc))
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return dto is null ? NotFound("pipeline", id) : TypedResults.Ok(dto);
    }

    private static async Task<Results<ContentHttpResult, ProblemHttpResult>> GetPipelineDefinitionAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var json = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.DefinitionJson)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return json is null
            ? NotFound("pipeline", id)
            : TypedResults.Text(json, "application/json");
    }

    /// <summary>
    /// The run parameters that apply to this pipeline, so the GUI can build a trigger form driven by the flow's own
    /// definition rather than showing every control for every kind. The applicable set comes from the shared
    /// <see cref="RunParameterApplicability"/> (the same per-kind rules the engine honors); for a relational
    /// ingestion the backfill window is offered only when the definition declares an incremental date column a window
    /// can bound.
    /// </summary>
    private static async Task<Results<Ok<FlowParametersDto>, ProblemHttpResult>> GetPipelineParametersAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var row = await db.Pipelines.AsNoTracking().Where(x => x.Id == id)
            .Select(x => new { x.Kind, x.DefinitionJson })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return NotFound("pipeline", id);
        }

        var descriptors = RunParameterApplicability.For(row.Kind, HasIncrementalDateColumn(row.DefinitionJson));
        return TypedResults.Ok(new FlowParametersDto(row.Kind, descriptors));
    }

    /// <summary>Whether the flow definition declares a non-empty incremental date column anywhere (a tolerant probe
    /// over the stored definition JSON, so it holds across the definition's exact nesting/casing). A malformed or
    /// absent definition reports false, which correctly withholds the window from a flow that cannot bound one.</summary>
    private static bool HasIncrementalDateColumn(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(definitionJson);
            return FindNonEmptyDateColumn(doc.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool FindNonEmptyDateColumn(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (string.Equals(prop.Name, "dateColumn", StringComparison.OrdinalIgnoreCase)
                        && prop.Value.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(prop.Value.GetString()))
                    {
                        return true;
                    }

                    if (FindNonEmptyDateColumn(prop.Value))
                    {
                        return true;
                    }
                }

                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (FindNonEmptyDateColumn(item))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>
    /// The pipeline's pre-ingestion transform columns: <c>declared</c> rows projected from the flow YAML on sync
    /// (the source of truth) and <c>detected</c> rows from the latest run's generated transformation view. An
    /// optional <c>kind</c> filters to one provenance. Ordered declared-first, then by view position. Returned
    /// whole (not paged): a view's column count is bounded by the table it projects.
    /// </summary>
    private static async Task<Ok<IReadOnlyList<PipelineColumnDto>>> GetPipelineColumnsAsync(
        Guid id, CatalogDbContext db, string? kind, CancellationToken ct)
    {
        var query = db.PipelineColumns.AsNoTracking().Where(c => c.PipelineId == id);
        if (!string.IsNullOrWhiteSpace(kind))
        {
            query = query.Where(c => c.Kind == kind);
        }

        IReadOnlyList<PipelineColumnDto> items = await query
            .OrderBy(c => c.Kind == PipelineColumnKinds.Declared ? 0 : 1).ThenBy(c => c.Ordinal)
            .Select(c => new PipelineColumnDto(
                c.Kind, c.Ordinal, c.ColumnName, c.SourceColumn, c.Expression, c.DataType,
                c.SortOrder, c.IsVirtual, c.ExcludeFromView, c.Converted))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(items);
    }

    /// <summary>
    /// The distinct files a pipeline has processed across its whole run history, newest-modified first: the
    /// "files this pipeline has seen" browser. Deduplicated by name+path (a file re-pulled by several runs appears
    /// once, carrying its newest processing's metadata), searchable by name or path, and each row flagged
    /// <c>lastRun</c> when it was processed by the pipeline's most recent file-bearing run so a caller can separate
    /// "what the last run found" from everything seen. Paged; an unknown pipeline is a 404 (distinct from a real
    /// pipeline that has processed no files, which is an empty page).
    /// </summary>
    private static async Task<Results<Ok<PagedResult<PipelineFileDto>>, ProblemHttpResult>> GetPipelineFilesAsync(
        Guid id, CatalogDbContext db, string? search, int? page, int? pageSize, CancellationToken ct)
    {
        if (!await db.Pipelines.AsNoTracking().AnyAsync(p => p.Id == id, ct).ConfigureAwait(false))
        {
            return NotFound("pipeline", id);
        }

        var (p, size) = PageRequest.Normalize(page, pageSize);

        // The pipeline's most recent run that actually processed files: the files it touched carry the lastRun
        // flag. Ordering by StartUtc (nulls last) so the newest run wins; null when the pipeline has no files yet.
        var lastRunId = await (
                from f in db.RunFiles.AsNoTracking()
                join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
                where r.PipelineId == id
                orderby r.StartUtc descending
                select (Guid?)r.RunId)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);

        var files =
            from f in db.RunFiles.AsNoTracking()
            join r in db.Runs.AsNoTracking() on f.RunId equals r.RunId
            where r.PipelineId == id
            select new { f.Name, f.Path, f.Modified, f.Rows, f.SizeBytes, r.RunId, r.StartUtc };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            files = files.Where(x => x.Name.Contains(term) || (x.Path != null && x.Path.Contains(term)));
        }

        // Deduplicate to one row per file (name + path), keeping the newest processing's metadata and whether any
        // of its processings was the last run. File-constant attributes (rows/size/modified) are stable across
        // re-pulls, so the group aggregates are the file's own values.
        var distinct = files
            .GroupBy(x => new { x.Name, x.Path })
            .Select(g => new
            {
                g.Key.Name,
                g.Key.Path,
                Modified = g.Max(x => x.Modified),
                Rows = g.Max(x => x.Rows),
                SizeBytes = g.Max(x => x.SizeBytes),
                LastProcessedUtc = g.Max(x => x.StartUtc),
                InLastRun = g.Max(x => x.RunId == lastRunId ? 1 : 0),
            });

        var total = await distinct.LongCountAsync(ct).ConfigureAwait(false);
        var items = await distinct
            .OrderByDescending(x => x.Modified).ThenBy(x => x.Name)
            .Skip((p - 1) * size).Take(size)
            .Select(x => new PipelineFileDto(
                x.Name, x.Path, x.Modified, x.Rows, x.SizeBytes, x.InLastRun == 1, x.LastProcessedUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<PipelineFileDto>(items, p, size, total));
    }

    /// <summary>
    /// The size profile of the pipeline's file deliveries: average, median, spread, extremes and totals over every
    /// distinct file in its run history, plus a recent-files window. Computed on demand from the same universe the
    /// files view browses, so it needs no stored aggregate and never goes stale. A pipeline that has processed no
    /// files reports zeros; an unknown pipeline is a 404.
    /// </summary>
    private static async Task<Results<Ok<PipelineFileStatsDto>, ProblemHttpResult>> GetPipelineFileStatsAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        if (!await db.Pipelines.AsNoTracking().AnyAsync(p => p.Id == id, ct).ConfigureAwait(false))
        {
            return NotFound("pipeline", id);
        }

        return TypedResults.Ok(await PipelineFileStats.ComputeAsync(db, id, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The estate's schema history, newest first: every object a source-control snapshot found added, changed, or
    /// dropped. Filters narrow it to one database, one kind of change, or a time window, and a free-text term
    /// matches the object's schema, name, or category so "where did Bysykkel_Trips change" is one query. This is
    /// a read of what the snapshots already recorded; it touches no database being tracked.
    /// </summary>
    private static async Task<Ok<PagedResult<SchemaChangeDto>>> ListSchemaChangesAsync(
        CatalogDbContext db, Guid? repoId, string? database, string? changeType, DateTime? since, string? search,
        int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var query = db.SchemaChanges.AsNoTracking();

        if (repoId is { } repo)
        {
            query = query.Where(c => c.RepoId == repo);
        }

        if (!string.IsNullOrWhiteSpace(database))
        {
            query = query.Where(c => c.Database == database);
        }

        if (!string.IsNullOrWhiteSpace(changeType))
        {
            query = query.Where(c => c.ChangeType == changeType);
        }

        if (since is { } from)
        {
            query = query.Where(c => c.OccurredUtc >= from);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(c => c.Name.Contains(term) || c.Schema!.Contains(term) || c.Category.Contains(term));
        }

        var total = await query.LongCountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(c => c.OccurredUtc).ThenBy(c => c.Database).ThenBy(c => c.Id)
            .Skip((p - 1) * size).Take(size)
            .Select(c => new SchemaChangeDto(
                c.Id, c.RepoId, c.RunId, c.PipelineId, c.Database, c.Category, c.Schema, c.Name,
                c.ChangeType, c.CommitSha, c.OccurredUtc))
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(new PagedResult<SchemaChangeDto>(items, p, size, total));
    }

    /// <summary>
    /// One row per tracked database: how many changes it has recorded, split by kind, and when it was last seen to
    /// change. This is the summary strip above the feed, and it doubles as the answer to "is this database still
    /// being snapshotted at all".
    /// </summary>
    private static async Task<Ok<IReadOnlyList<SchemaChangeDatabaseDto>>> ListSchemaChangeDatabasesAsync(
        CatalogDbContext db, Guid? repoId, DateTime? since, CancellationToken ct)
    {
        var query = db.SchemaChanges.AsNoTracking();
        if (repoId is { } repo)
        {
            query = query.Where(c => c.RepoId == repo);
        }

        if (since is { } from)
        {
            query = query.Where(c => c.OccurredUtc >= from);
        }

        var rows = await query
            .GroupBy(c => c.Database)
            .Select(g => new SchemaChangeDatabaseDto(
                g.Key,
                g.Count(),
                g.Count(c => c.ChangeType == SchemaChangeKinds.Added),
                g.Count(c => c.ChangeType == SchemaChangeKinds.Changed),
                g.Count(c => c.ChangeType == SchemaChangeKinds.Deleted),
                g.Max(c => c.OccurredUtc)))
            .OrderBy(d => d.Database)
            .ToListAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok((IReadOnlyList<SchemaChangeDatabaseDto>)rows);
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
