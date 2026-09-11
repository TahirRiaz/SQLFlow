using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Lineage;
using SqlFlow.Core.Secrets;
using SqlFlow.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Extraction;
using SqlFlow.Yaml;

namespace SqlFlow.Catalog;

/// <summary>The tally of one sync pass.</summary>
public sealed record CatalogSyncResult
{
    public int PipelinesAdded { get; init; }
    public int PipelinesUpdated { get; init; }
    public int PipelinesUnchanged { get; init; }
    public int PipelinesDeactivated { get; init; }

    public int PipelinesDeleted { get; init; }
    public int RunsAdded { get; init; }
    public int RunsSkipped { get; init; }
    public int RunsFailed { get; init; }
    public int ObjectsUpserted { get; init; }

    /// <summary>Database-less twin rows deleted because their object now syncs under a database-qualified
    /// key and no repo's edges reference the weak key anymore (identity healing).</summary>
    public int ObjectsSuperseded { get; init; }

    public int ObjectColumns { get; init; }
    public int LineageEdges { get; init; }

    /// <summary>Previously-derived edges this pass kept because it could not re-derive them (an offline
    /// recompute, or a connected pass whose derive failed for their server): degraded passes preserve
    /// knowledge, never wipe it.</summary>
    public int LineageEdgesPreserved { get; init; }

    public int FlowDependencies { get; init; }
    public int Waves { get; init; }
    public int RunFilesAdded { get; init; }
    public int RunAssertionsAdded { get; init; }
    public int RunStatementsAdded { get; init; }
    public int RunEventsAdded { get; init; }
    public int RunSurrogateKeysAdded { get; init; }
    public int RunHealthCheckMetricsAdded { get; init; }

    /// <summary>Schema differences recorded from source-control runs in this pass.</summary>
    public int SchemaChangesAdded { get; init; }
    public bool LineageConnected { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>The per-pass tally of run artifacts and the drill-down detail rows projected from them.</summary>
internal readonly record struct RunSyncTally(
    int Added, int Skipped, int Failed, int Files, int Assertions, int Statements, int Events, int SurrogateKeys, int Metrics,
    int SchemaChanges);

/// <summary>What a per-run write-back did to the flow's pipeline row.</summary>
public enum PipelineChange
{
    None,
    Added,
    Updated,
    Unchanged,
}

/// <summary>The result of recording one just-completed run into the catalog (the self-maintaining write-back).</summary>
public sealed record RecordRunResult
{
    public PipelineChange PipelineChange { get; init; }
    public bool RunRecorded { get; init; }
    public int RunFilesAdded { get; init; }
    public int RunAssertionsAdded { get; init; }
    public int RunStatementsAdded { get; init; }
    public int RunEventsAdded { get; init; }
    public int RunSurrogateKeysAdded { get; init; }
    public int RunHealthCheckMetricsAdded { get; init; }

    /// <summary>Schema differences recorded from source-control runs in this pass.</summary>
    public int SchemaChangesAdded { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

/// <summary>
/// Synchronizes the shadow catalog FROM the source of truth: the git/YAML estate (the pipeline registry, mapped
/// into rows with the full definition in queryable JSON) and the on-disk <c>run.json</c> history. One direction
/// only - the database mirrors the files, never the reverse - so the catalog can always be rebuilt by re-syncing,
/// and several repos sync into one catalog for cross-repo queries. Pipelines are upserted by their stable
/// identity (ones that have left the estate are deactivated, keeping their run history); runs are inserted by
/// their own id, so re-syncing the same folders, or aggregating many nodes' folders, is idempotent. A pass runs
/// in two phases: the estate is collected and every document parsed exactly once up front (pure computation, no
/// transaction held), then the reconciliation writes run in one serializable transaction so two syncs of the
/// same repo cannot lose each other's updates. Secrets never rest in the catalog: a flow document is detected
/// (and warned) when it embeds a credential, and the stored YAML / definition JSON are passed through the same
/// redactor used for connection-string error text.
/// </summary>
public sealed class CatalogSync
{
    // A flow document is kilobytes; a run.json is small. These caps stop a hostile or corrupt file from
    // exhausting memory or bloating the nvarchar(max) columns. Over-limit files are skipped with a warning.
    private const long MaxYamlBytes = 16L * 1024 * 1024;
    internal const long MaxRunJsonBytes = RunQueueStore.MaxArtifactBytes;

    // SQL Server allows roughly 2100 parameters per command, and a keys.Contains(...) predicate can translate
    // to one parameter per key, so membership queries and deletes over report-sized key sets run in bounded
    // chunks whose results/effects are combined. 500 keys per round trip stays far under the limit.
    private const int KeyChunkSize = 500;

    private static readonly JsonSerializerOptions DefinitionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FlowSetCollector _estate = new();

    private readonly YamlDocumentLoader _documents = new(
        new YamlFlowLoader(), new YamlIngestionFlowLoader(), new YamlExportFlowLoader(),
        new YamlStoredProcedureFlowLoader(), new YamlInvokeFlowLoader(), new YamlHealthCheckFlowLoader(),
        new YamlSourceControlFlowLoader(), new YamlBatchFlowLoader(), new YamlAcquireFlowLoader(),
        new YamlCopyFlowLoader(), new YamlSftpFlowLoader(), new YamlCalendarFlowLoader(),
        new YamlTranslateFlowLoader());

    /// <summary>One estate flow prepared for the reconciliation transaction: its redacted text, content hash,
    /// serialized definition, and the parsed document (null when it failed to parse after the scan) that the
    /// declared-column projection reads. Prepared once per pass; the transaction (and any retry of it) stages
    /// fresh rows from this data.</summary>
    private sealed record PreparedPipeline
    {
        public required CollectedFlow Flow { get; init; }

        public required Guid Id { get; init; }

        public required string Yaml { get; init; }

        public required string ContentHash { get; init; }

        public required string DefinitionJson { get; init; }

        public required FlowDocument? Document { get; init; }
    }

    /// <summary>One validated git-declared schedule, with the flows that joined it and where git declares it, ready
    /// to stage into the schedule and schedule-member tables.</summary>
    private sealed record PreparedSchedule(
        string Name, IReadOnlyList<string> Members, Core.ScheduleSpec Spec, DateTime NextFireUtc,
        ScheduleDefinitionSource Definition);

    /// <summary>One run artifact awaiting insertion: the projected header row (client-keyed, so re-adding it on
    /// a transaction retry is safe) and the parsed document its detail rows project from inside the transaction,
    /// retained so the file is read and parsed exactly once per pass.</summary>
    private sealed record PreparedRun(CatalogRun Run, JsonDocument Document) : IDisposable
    {
        public void Dispose() => Document.Dispose();
    }

    public async Task<CatalogSyncResult> SyncAsync(
        CatalogDbContext context, string estateDirectory, string repoName, string? repoRemoteUrl, DateTime nowUtc,
        bool includeDerived = false, ISecretResolver? secrets = null, IReadOnlySet<string>? excludedFlowPaths = null,
        bool forceLineage = false, Func<string, CancellationToken, Task>? lineageProgress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(estateDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);
        var root = Path.GetFullPath(estateDirectory);
        var repoId = FlowIdentity.FromName(repoName);
        var warnings = new List<string>();

        // ---- Phase one: pure computation and reads, before any transaction. The estate is collected once and
        // each present document read and parsed once; the parsed artifacts (the collected flow set, the loaded
        // documents, the redacted text) flow into every consumer below, including the lineage computation.
        var collected = _estate.Collect(root);
        warnings.AddRange(collected.Warnings);

        // Preview-first selection: flows the source deliberately excludes are not projected as pipelines (and, being
        // absent from the present set, are deactivated below if a previous sync had imported them, keeping their
        // history). The selection is the ONLY gate; everything else stays the same one sync path.
        var flows = excludedFlowPaths is { Count: > 0 }
            ? collected.Flows.Where(f => !excludedFlowPaths.Contains(Normalize(f.Node.File))).ToList()
            : collected.Flows;

        var (pipelines, presentIds, schedules, anyUnreadable) =
            PreparePipelines(root, repoId, flows, collected.Schedules, nowUtc, warnings, ct);

        // The stored state this pass reconciles against, read outside the transaction: the known run ids (so only
        // new artifacts are parsed and retained) and the active pipelines' content hashes (the lineage gate).
        var knownRunIds = (await context.Runs.Where(r => r.RepoId == repoId).Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var storedActiveHashes = await context.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active)
            .Select(p => new { p.Id, p.ContentHash })
            .ToDictionaryAsync(p => p.Id, p => p.ContentHash, ct).ConfigureAwait(false);

        var (runs, runsSkipped, runsFailed) = await PrepareRunsAsync(root, repoId, knownRunIds, warnings, ct).ConfigureAwait(false);
        try
        {
            // The lineage recompute gate: recompute only when an input of the graph could have changed. The
            // declared tier's input is the flow set (content hashes, additions, removals); the observed tier's
            // input is the run history (any newly discovered artifact); the derived tier reads the live
            // catalogs, which can change on their own, so a connected sync always recomputes. An excluded flow
            // still contributes lineage (the graph spans the whole estate) but has no stored hash to compare,
            // so a selection-scoped sync recomputes too. When nothing changed, the stored lineage IS current.
            // A manual "sync now" forces a full recompute (the operator asked for a fresh result, including the
            // offline object-body/column enrichment), so the unchanged-estate shortcut is bypassed.
            var anyExcluded = collected.Flows.Count != flows.Count;
            var lineageNeeded = forceLineage || includeDerived || anyExcluded || runs.Count > 0
                || LineageInputsChanged(pipelines, anyUnreadable, storedActiveHashes);

            LineageReport? report = null;
            string? lineageFailure = null;
            if (lineageNeeded)
            {
                try
                {
                    // Reuses the flow set collected above, so the estate is never scanned or parsed a second time.
                    report = await LineageService.ComputeAsync(
                        new LineageOptions
                        {
                            FlowDirectory = root,
                            IncludeObserved = true,
                            IncludeDerived = includeDerived,
                            Secrets = secrets,
                            Progress = lineageProgress,
                        },
                        collected, ct).ConfigureAwait(false);
                    foreach (var warning in report.Warnings)
                    {
                        warnings.Add($"lineage: {warning}");
                    }
                }
                catch (Exception ex) when (ex is SqlFlow.Core.SqlFlowException or IOException or InvalidOperationException)
                {
                    // Lineage is an enrichment: a failure to compute it (e.g. a connect-tier timeout) must not
                    // fail the whole sync. The pipeline registry and run history still land; the write phase
                    // resets the stale waves so they never read as a real execution order.
                    lineageFailure = SecretHygiene.RedactedMessage(ex);
                    warnings.Add($"lineage was not computed for this sync ({lineageFailure}); objects and edges left unchanged, waves reset to not-computed.");
                }
            }

            // ---- Phase two: one serializable transaction for the reconciliation writes only (so concurrent
            // syncs of the same repo serialize instead of racing), run through the context's execution strategy
            // so it is a single retriable unit. The control plane enables connection resiliency
            // (EnableRetryOnFailure); EF then forbids a user-initiated transaction unless wrapped this way. A
            // retried attempt re-stages its rows from the phase-one artifacts, never from half-tracked state.
            return await CatalogTransaction.InSerializableAsync(context, async () =>
            {
                await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
                var pipelineTally = await ApplyPipelinesAsync(context, repoId, nowUtc, pipelines, presentIds, schedules, excludedFlowPaths, ct).ConfigureAwait(false);
                var runTally = await ApplyRunsAsync(context, repoId, runs, runsSkipped, runsFailed, ct).ConfigureAwait(false);

                (int Objects, int Superseded, int Columns, int Edges, int FlowDeps, int Waves, bool Connected, int PreservedDerived) lineage;
                if (report is not null)
                {
                    lineage = await ApplyLineageAsync(context, repoId, report, includeDerived, nowUtc, ct).ConfigureAwait(false);
                }
                else
                {
                    if (lineageFailure is not null)
                    {
                        // A wave from an earlier successful sync no longer provably reflects this estate, so reset
                        // every active pipeline in this repo to the -1 "not computed" sentinel rather than leaving
                        // a stale wave that reads as a real execution order. The pipelines are already tracked
                        // (loaded in ApplyPipelinesAsync), so mutating them here is persisted by the single
                        // SaveChangesAsync at the end of the pass.
                        foreach (var pipeline in context.Pipelines.Local)
                        {
                            if (pipeline.RepoId == repoId && pipeline.Active)
                            {
                                pipeline.Wave = -1;
                            }
                        }
                    }

                    // else: no lineage input changed since the stored state (same content hashes, no additions or
                    // removals, no new runs, offline): the stored objects, edges, waves, and dependencies are
                    // already current, so the recompute is skipped and nothing lineage-related is written.
                    lineage = (0, 0, 0, 0, 0, 0, false, 0);
                }

                return new CatalogSyncResult
                {
                    PipelinesAdded = pipelineTally.Added,
                    PipelinesUpdated = pipelineTally.Updated,
                    PipelinesUnchanged = pipelineTally.Unchanged,
                    PipelinesDeactivated = pipelineTally.Deactivated,
                    PipelinesDeleted = pipelineTally.Deleted,
                    RunsAdded = runTally.Added,
                    RunsSkipped = runTally.Skipped,
                    RunsFailed = runTally.Failed,
                    RunFilesAdded = runTally.Files,
                    RunAssertionsAdded = runTally.Assertions,
                    RunStatementsAdded = runTally.Statements,
                    RunEventsAdded = runTally.Events,
                    RunSurrogateKeysAdded = runTally.SurrogateKeys,
                    RunHealthCheckMetricsAdded = runTally.Metrics,
                    SchemaChangesAdded = runTally.SchemaChanges,
                    ObjectsUpserted = lineage.Objects,
                    ObjectsSuperseded = lineage.Superseded,
                    ObjectColumns = lineage.Columns,
                    LineageEdges = lineage.Edges,
                    LineageEdgesPreserved = lineage.PreservedDerived,
                    FlowDependencies = lineage.FlowDeps,
                    Waves = lineage.Waves,
                    LineageConnected = lineage.Connected,
                    Warnings = warnings,
                };
            }, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var run in runs)
            {
                run.Dispose();
            }
        }
    }

    // The retriable serializable-transaction wrapper lives in CatalogTransaction so the run-queue lifecycle shares
    // the exact same execution-strategy + change-tracker-reset semantics as the sync/write-back.

    private static async Task UpsertRepoAsync(
        CatalogDbContext context, Guid repoId, string repoName, string? remoteUrl, string root, DateTime nowUtc, CancellationToken ct)
    {
        var repo = await context.Repos.FindAsync([repoId], ct).ConfigureAwait(false);
        if (repo is null)
        {
            context.Repos.Add(new CatalogRepo
            {
                Id = repoId,
                Name = repoName,
                RemoteUrl = NullIfBlank(remoteUrl),
                RootPath = root,
                FirstSeenUtc = nowUtc,
                LastSyncUtc = nowUtc,
            });
        }
        else
        {
            repo.Name = repoName;
            repo.RemoteUrl = NullIfBlank(remoteUrl) ?? repo.RemoteUrl;
            repo.RootPath = root;
            repo.LastSyncUtc = nowUtc;
        }
    }

    /// <summary>
    /// Phase-one preparation of this repo's pipeline projections: reads and parses each present flow document
    /// exactly once, producing everything the reconciliation transaction stages (the redacted YAML, its hash,
    /// the definition JSON, the parsed document for the declared-column projection) plus the validated schedule
    /// mirror entries. File IO and pure computation only; nothing here touches the database.
    /// </summary>
    private (List<PreparedPipeline> Pipelines, HashSet<Guid> PresentIds, List<PreparedSchedule> Schedules, bool AnyUnreadable) PreparePipelines(
        string root, Guid repoId, IReadOnlyList<CollectedFlow> flows, IReadOnlyList<CollectedSchedule> collectedSchedules,
        DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var pipelines = new List<PreparedPipeline>();
        var present = new HashSet<Guid>();
        var anyUnreadable = false;

        foreach (var flow in flows)
        {
            ct.ThrowIfCancellationRequested();
            var id = CatalogIdentity.Pipeline(repoId, flow.Node.Name);
            if (!present.Add(id))
            {
                continue; // a duplicate flow name (already warned by the estate scan); the first wins.
            }

            var fullPath = Path.GetFullPath(Path.Combine(root, flow.Node.File));
            var rawYaml = ReadYaml(fullPath, flow.Node.File, warnings);
            if (rawYaml is null)
            {
                // The file vanished or was unreadable since the estate scan: leave any existing row untouched (it
                // stays in 'present' so it is not deactivated) rather than overwriting it with empty content.
                anyUnreadable = true;
                continue;
            }

            if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
            {
                warnings.Add($"'{flow.Node.Name}' ({flow.Node.File}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
            }

            // Redact before anything is stored or hashed, so no credential ever rests in the catalog and change
            // detection runs on the safe form. Clean reference-only YAML passes through unchanged.
            var yaml = SecretHygiene.RedactedMessage(rawYaml);
            var hash = CatalogProjection.Hash(yaml);

            // ONE parse serves both the queryable definition JSON and the declared-column projection. A document
            // that fails to parse still lands as a pipeline row (the YAML text is the source of truth), just
            // without those enrichments; that is never a reason to fail the sync.
            FlowDocument? document = null;
            var definitionJson = string.Empty;
            try
            {
                document = _documents.Parse(rawYaml, fullPath);
                definitionJson = SerializeDefinition(document, fullPath, warnings);
            }
            catch (SqlFlow.Core.SqlFlowException ex)
            {
                // FlowValidationException derives from SqlFlowException, so a malformed document is caught here too.
                warnings.Add($"'{fullPath}' could not be parsed for the catalog definition ({SecretHygiene.RedactedMessage(ex)}); stored without it.");
            }

            pipelines.Add(new PreparedPipeline
            {
                Flow = flow,
                Id = id,
                Yaml = yaml,
                ContentHash = hash,
                DefinitionJson = definitionJson,
                Document = document,
            });
        }

        // Mirror git-declared schedules. A schedule is defined once by NAME (a schedules.yaml entry, or an inline
        // block on a flow) and flows JOIN it by name; the estate scan has already resolved both shapes, and every
        // reference, into a named schedule with its member set. Validated here (pure); the staging into the schedule
        // and member tables happens inside the sync's transaction.
        var presentNames = flows.Select(f => f.Node.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var schedules = new List<PreparedSchedule>();
        foreach (var schedule in collectedSchedules)
        {
            var spec = schedule.Spec;
            // A CHAINED schedule is driven by its parent's completion, not by the clock, so it carries neither a cron
            // nor an interval. The clock validation demands exactly one of those, so applying it to a chained schedule
            // would reject every one of them and silently drop it from the catalog: registered nowhere, invisible in
            // the GUI, and impossible to run by hand. Only clock-driven schedules are validated here.
            if (!spec.IsChained
                && !ScheduleClock.TryValidate(spec.Cron, spec.IntervalSeconds, spec.Timezone, out var scheduleError))
            {
                warnings.Add($"schedule '{schedule.Name}' ({schedule.Origin}) is invalid: {scheduleError}");
                continue;
            }

            // A member the selection excluded from this sync has no pipeline row to enqueue, so it is not stored as a
            // member. The expansion would skip it anyway (it joins only active pipelines), and leaving it out keeps
            // the stored member set an honest answer to "what does this fire run".
            var members = schedule.Members.Where(presentNames.Contains).ToList();
            var nextFire = ScheduleClock.NextFire(spec.Cron, spec.IntervalSeconds, spec.Timezone, nowUtc) ?? nowUtc;
            schedules.Add(new PreparedSchedule(
                schedule.Name, members, spec, nextFire,
                new ScheduleDefinitionSource(schedule.OriginFile, schedule.OriginFlow, schedule.LibraryYaml)));
        }

        return (pipelines, present, schedules, anyUnreadable);
    }

    /// <summary>Whether the declared tier's lineage inputs differ from what the stored catalog reflects: any
    /// pipeline added, removed, or with changed content since the stored state. Doubt (a file unreadable during
    /// this pass, a stored hash that is blank) counts as changed, so the caller recomputes.</summary>
    private static bool LineageInputsChanged(
        IReadOnlyList<PreparedPipeline> pipelines, bool anyUnreadable, IReadOnlyDictionary<Guid, string> storedActiveHashes)
    {
        if (anyUnreadable || pipelines.Count != storedActiveHashes.Count)
        {
            return true; // uncertain state, or a pipeline was added/removed/deactivated/reactivated.
        }

        foreach (var pipeline in pipelines)
        {
            if (!storedActiveHashes.TryGetValue(pipeline.Id, out var storedHash)
                || string.IsNullOrEmpty(storedHash)
                || !string.Equals(storedHash, pipeline.ContentHash, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reconciles this repo's pipeline rows, git-declared schedules, and declared pipeline-column rows
    /// from the phase-one preparation. Runs inside the sync's transaction and performs only database work.</summary>
    private static async Task<(int Added, int Updated, int Unchanged, int Deactivated, int Deleted)> ApplyPipelinesAsync(
        CatalogDbContext context, Guid repoId, DateTime nowUtc,
        IReadOnlyList<PreparedPipeline> pipelines, IReadOnlySet<Guid> presentIds,
        IReadOnlyList<PreparedSchedule> schedules, IReadOnlySet<string>? excludedFlowPaths, CancellationToken ct)
    {
        // Only this repo's pipelines: another repo's flows in the same catalog must not be touched by this sync.
        // AsTracking so the update/deactivate mutations below persist even when the host's context defaults to
        // NoTracking (the control plane pools its context that way); on a tracking context this is a no-op.
        var existing = await context.Pipelines.Where(p => p.RepoId == repoId).AsTracking().ToDictionaryAsync(p => p.Id, ct).ConfigureAwait(false);
        var added = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var prepared in pipelines)
        {
            ct.ThrowIfCancellationRequested();
            if (existing.TryGetValue(prepared.Id, out var row) && string.Equals(row.ContentHash, prepared.ContentHash, StringComparison.Ordinal))
            {
                // Unchanged content (derived fields cannot have changed either): re-affirm presence only.
                row.Active = true;
                row.LastSeenUtc = nowUtc;
                row.RelativePath = Normalize(prepared.Flow.Node.File);
                unchanged++;
                continue;
            }

            var flow = prepared.Flow;
            var projected = CatalogProjection.Pipeline(
                repoId, flow.Node.Name, flow.Node.Kind, flow.Node.Batch, Normalize(flow.Node.File),
                flow.SourceServerRef, flow.TargetServerRef, prepared.ContentHash, prepared.Yaml, prepared.DefinitionJson, nowUtc,
                flow.Node.Mode, flow.Node.Lifecycle);

            if (row is not null)
            {
                row.Name = projected.Name;
                row.Kind = projected.Kind;
                row.Batch = projected.Batch;
                row.RelativePath = projected.RelativePath;
                row.ExecutionMode = projected.ExecutionMode;
                row.Lifecycle = projected.Lifecycle;
                row.SourceServer = projected.SourceServer;
                row.TargetServer = projected.TargetServer;
                row.ContentHash = projected.ContentHash;
                row.Yaml = projected.Yaml;
                row.DefinitionJson = projected.DefinitionJson;
                row.Active = true;
                row.LastSeenUtc = nowUtc;
                updated++;
            }
            else
            {
                context.Pipelines.Add(projected);
                added++;
            }
        }

        // A flow excluded from a selection-scoped sync is still in the repo, so it is deactivated and reactivates
        // when it is re-included. A flow that has genuinely left the repo is deleted, so it stops appearing in the
        // catalog instead of piling up as an inactive tombstone across renames. Its run history is KEPT (each run
        // carries the flow name, so the traces stand on their own), but its schedule is removed so nothing fires for
        // a flow that is gone. Lineage edges/objects/dependencies are repo-scoped and rebuilt later in this pass.
        var deactivated = 0;
        var removedIds = new List<Guid>();
        foreach (var (id, row) in existing)
        {
            if (presentIds.Contains(id))
            {
                continue;
            }

            if (excludedFlowPaths is { Count: > 0 } && excludedFlowPaths.Contains(row.RelativePath))
            {
                if (row.Active)
                {
                    row.Active = false;
                    deactivated++;
                }
            }
            else
            {
                removedIds.Add(id);
            }
        }

        var deleted = removedIds.Count;
        if (deleted > 0)
        {
            // A pipeline that left the estate takes its memberships with it, so no schedule tries to enqueue a flow
            // that no longer exists. The schedule itself survives: it belongs to a name, not to any one flow.
            await context.ScheduleMembers.Where(m => removedIds.Contains(m.PipelineId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            context.Pipelines.RemoveRange(removedIds.Select(id => existing[id]));
        }

        // Stage the validated yaml schedule mirror, each schedule with the member set git says joined it: an
        // operator's API pause is preserved and API-created schedules are never touched; a schedule that left git is
        // removed with its memberships. Staged on this context so the changes commit inside the sync's own
        // transaction (the store's transaction-free variants).
        var scheduleKeep = new HashSet<Guid>();
        foreach (var schedule in schedules)
        {
            var scheduleId = await ScheduleStore.StageYamlUpsertAsync(
                context, repoId, schedule.Name, schedule.Members, schedule.Spec.Cron, schedule.Spec.IntervalSeconds,
                schedule.Spec.Timezone, schedule.Spec.Enabled, schedule.Spec.Catchup, schedule.Spec.MaxConcurrency,
                schedule.NextFireUtc, nowUtc, schedule.Definition, schedule.Spec.After,
                schedule.Spec.ParentFreshnessHours, ct).ConfigureAwait(false);
            scheduleKeep.Add(scheduleId);
        }

        await ScheduleStore.StageRemoveYamlSchedulesNotInAsync(context, repoId, scheduleKeep, ct).ConfigureAwait(false);

        // Replace this repo's declared pipeline-column rows from the authored YAML transforms of every present
        // flow (the source of truth for "which transforms are set"). Rebuilt wholesale for this repo so a removed
        // or edited transform does not linger; detected rows (from runs) are a different kind and are left
        // untouched. The rows project from the documents parsed in phase one, fresh per attempt.
        await context.PipelineColumns
            .Where(c => c.RepoId == repoId && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var prepared in pipelines)
        {
            foreach (var column in ProjectDeclaredColumns(prepared.Document, prepared.Flow.Node.Kind, repoId, prepared.Id))
            {
                context.PipelineColumns.Add(column);
            }
        }

        return (added, updated, unchanged, deactivated, deleted);
    }

    /// <summary>Projects a flow document's authored transform policy into declared column rows. File and
    /// relational (ing) flows carry the shared transform block; any other kind, or a document that failed to
    /// parse (null), contributes none (the pipeline projection already warned about a parse failure; declared
    /// columns are an enrichment, never a reason to fail the sync). The pipeline's kind gates the projection:
    /// the transform belongs to the load, so a derived hc pipeline sharing the ing document gets no columns.</summary>
    private static IReadOnlyList<CatalogPipelineColumn> ProjectDeclaredColumns(
        FlowDocument? document, string pipelineKind, Guid repoId, Guid pipelineId)
    {
        var policy = document switch
        {
            FileFlowDocument file => file.Flow.Inference,
            IngestionFlowDocument ing when !string.Equals(pipelineKind, "hc", StringComparison.OrdinalIgnoreCase)
                => ing.Document.Flow.Transform,
            _ => null,
        };

        return policy is { Columns.Count: > 0 }
            ? CatalogProjection.PipelineColumnsDeclared(repoId, pipelineId, policy)
            : [];
    }

    /// <summary>Phase-one scan of the estate's run artifacts: parses each <c>run.json</c> once and keeps ONLY the
    /// runs the catalog does not already know, so nothing is read or parsed twice per pass. Known and duplicate
    /// runs count as skipped; unreadable, malformed, or over-limit artifacts as failed. The caller owns disposing
    /// the returned documents once the write phase is done with them.</summary>
    private static async Task<(List<PreparedRun> Runs, int Skipped, int Failed)> PrepareRunsAsync(
        string root, Guid repoId, HashSet<Guid> knownRunIds, List<string> warnings, CancellationToken ct)
    {
        var prepared = new List<PreparedRun>();
        var seen = new HashSet<Guid>();
        var skipped = 0;
        var failed = 0;

        foreach (var file in EnumerateRunArtifacts(root))
        {
            ct.ThrowIfCancellationRequested();
            JsonDocument? document = null;
            try
            {
                var length = new FileInfo(file).Length;
                if (length > MaxRunJsonBytes)
                {
                    warnings.Add($"run artifact '{file}' is {length} bytes, over the {MaxRunJsonBytes}-byte limit; skipped.");
                    failed++;
                    continue;
                }

                document = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var run = CatalogProjection.RunFromJson(document.RootElement, repoId);
                if (run is null)
                {
                    warnings.Add($"run artifact '{file}' is missing required fields; skipped.");
                    failed++;
                    continue;
                }

                if (knownRunIds.Contains(run.RunId) || !seen.Add(run.RunId))
                {
                    skipped++; // immutable and already recorded (or seen earlier this pass).
                    continue;
                }

                prepared.Add(new PreparedRun(run, document));
                document = null; // ownership handed to the prepared list.
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{file}' could not be read ({SecretHygiene.RedactedMessage(ex)}); skipped.");
                failed++;
            }
            finally
            {
                document?.Dispose();
            }
        }

        return (prepared, skipped, failed);
    }

    /// <summary>Inserts the phase-one runs and their drill-down detail, and applies the newest detected
    /// transform-view projection per pipeline. Runs inside the sync's transaction.</summary>
    private static async Task<RunSyncTally> ApplyRunsAsync(
        CatalogDbContext context, Guid repoId, IReadOnlyList<PreparedRun> runs, int skippedInScan, int failedInScan, CancellationToken ct)
    {
        var skipped = skippedInScan;
        var added = 0;
        var files = 0;
        var assertions = 0;
        var statements = 0;
        var events = 0;
        var surrogateKeys = 0;
        var metrics = 0;
        var schemaChanges = 0;

        // The pre-transaction scan already dropped every run the catalog knew then; re-verify the survivors
        // INSIDE the transaction (in bounded chunks) so a run another node recorded in the meantime is skipped,
        // keeping the insert idempotent under concurrency.
        var alreadyKnown = (await SelectByKeysAsync(
                runs.Select(r => r.Run.RunId).ToList(),
                chunk => context.Runs.Where(r => chunk.Contains(r.RunId)).Select(r => r.RunId).ToListAsync(ct))
            .ConfigureAwait(false)).ToHashSet();

        // The newest transform-view projection seen per pipeline this pass: the detected pipeline columns are a
        // "latest run wins" snapshot, so only the most recent run's view columns are applied after the loop.
        var detectedCandidates = new Dictionary<Guid, (DateTime WrittenUtc, IReadOnlyList<CatalogPipelineColumn> Rows)>();

        foreach (var prepared in runs)
        {
            ct.ThrowIfCancellationRequested();
            if (alreadyKnown.Contains(prepared.Run.RunId))
            {
                skipped++; // recorded by a concurrent sync/write-back since the scan; a run is immutable.
                continue;
            }

            // A run is immutable, so its drill-down detail is inserted exactly once, with the run itself.
            context.Runs.Add(prepared.Run);
            added++;
            var detail = AddRunDetail(
                context, prepared.Document.RootElement, prepared.Run.RunId, repoId, pipelineId: prepared.Run.PipelineId);
            files += detail.Files;
            assertions += detail.Assertions;
            statements += detail.Statements;
            events += detail.Events;
            surrogateKeys += detail.SurrogateKeys;
            metrics += detail.Metrics;
            schemaChanges += detail.SchemaChanges;

            var detected = CatalogProjection.PipelineColumnsDetected(prepared.Document.RootElement, repoId, prepared.Run.PipelineId);
            if (detected.Count > 0
                && (!detectedCandidates.TryGetValue(prepared.Run.PipelineId, out var current) || prepared.Run.WrittenUtc > current.WrittenUtc))
            {
                detectedCandidates[prepared.Run.PipelineId] = (prepared.Run.WrittenUtc, detected);
            }
        }

        // Apply the newest detected view projection per pipeline, but never over a run the catalog already knows
        // that is newer than this pass's candidate (an old artifact synced late must not regress the snapshot).
        foreach (var (pipelineId, candidate) in detectedCandidates)
        {
            var newerKnown = await context.Runs
                .AnyAsync(r => r.PipelineId == pipelineId && r.WrittenUtc > candidate.WrittenUtc, ct).ConfigureAwait(false);
            if (newerKnown)
            {
                continue;
            }

            await context.PipelineColumns
                .Where(c => c.PipelineId == pipelineId && c.Kind == PipelineColumnKinds.Detected)
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var row in candidate.Rows)
            {
                context.PipelineColumns.Add(row);
            }
        }

        return new RunSyncTally(
            added, skipped, failedInScan, files, assertions, statements, events, surrogateKeys, metrics, schemaChanges);
    }

    /// <summary>Adds the immutable drill-down detail of one run (files, assertions, generated SQL, canonical
    /// events, surrogate keys, health-check metrics) projected from its run.json root. Shared by the full estate
    /// sync and the per-run write-back, so the detail projection is wired in exactly one place.</summary>
    /// <param name="context">The catalog context the detail rows are added to.</param>
    /// <param name="root">The run.json root element the detail is projected from.</param>
    /// <param name="runId">The run the detail belongs to.</param>
    /// <param name="repoId">The repo the run belongs to.</param>
    /// <param name="existingMaxEventOrdinal">The highest event ordinal already present as a live-streamed row (the
    /// node writes canonical events into the catalog as the run executes). Events at or below it are skipped so
    /// only the missing tail is appended, leaving the live rows - and the stable ids the trace stream already
    /// delivered - untouched. 0 for the CLI and full-sync paths, which have no live rows, so the whole event
    /// timeline is inserted.</param>
    /// <param name="existingMaxStatementOrdinal">The same for the generated-SQL stream: the highest statement
    /// ordinal already present as a live-streamed row, so only the missing tail is appended.</param>
    /// <param name="pipelineId">The run's pipeline, stamped onto the schema-change rows a source-control run
    /// projects so a difference traces back to the snapshot document that found it.</param>
    internal static (int Files, int Assertions, int Statements, int Events, int SurrogateKeys, int Metrics, int SchemaChanges) AddRunDetail(
        CatalogDbContext context, JsonElement root, Guid runId, Guid repoId,
        int existingMaxEventOrdinal = 0, int existingMaxStatementOrdinal = 0, Guid? pipelineId = null)
    {
        var files = 0;
        var assertions = 0;
        var statements = 0;
        var events = 0;
        var surrogateKeys = 0;
        var metrics = 0;
        var schemaChanges = 0;

        foreach (var runFile in CatalogProjection.RunFiles(root, runId, repoId))
        {
            context.RunFiles.Add(runFile);
            files++;
        }

        foreach (var assertion in CatalogProjection.RunAssertions(root, runId, repoId))
        {
            context.RunAssertions.Add(assertion);
            assertions++;
        }

        foreach (var statement in CatalogProjection.RunStatements(root, runId, repoId))
        {
            if (statement.Ordinal <= existingMaxStatementOrdinal)
            {
                continue; // already written live under a stable id; do not re-issue it
            }

            context.RunStatements.Add(statement);
            statements++;
        }

        foreach (var runEvent in CatalogProjection.RunEvents(root, runId, repoId))
        {
            if (runEvent.Ordinal <= existingMaxEventOrdinal)
            {
                continue; // already written live under a stable id; do not re-issue it
            }

            context.RunEvents.Add(runEvent);
            events++;
        }

        foreach (var surrogateKey in CatalogProjection.RunSurrogateKeys(root, runId, repoId))
        {
            context.RunSurrogateKeys.Add(surrogateKey);
            surrogateKeys++;
        }

        foreach (var metric in CatalogProjection.RunHealthCheckMetrics(root, runId, repoId))
        {
            context.RunHealthCheckMetrics.Add(metric);
            metrics++;
        }

        // A source-control run's differences: the schema history of the managed estate, written by the same
        // one-run-one-insert path as every other drill-down so the CLI, the full sync, and the live queue all
        // record it identically. Non-scm runs project nothing here.
        foreach (var change in CatalogProjection.SchemaChanges(root, runId, repoId, pipelineId))
        {
            context.SchemaChanges.Add(change);
            schemaChanges++;
        }

        return (files, assertions, statements, events, surrogateKeys, metrics, schemaChanges);
    }

    /// <summary>
    /// The self-maintaining write-back: records ONE just-completed run (and ensures its pipeline row) into the
    /// catalog, so a configured database stays current without a manual full <see cref="SyncAsync"/>. It upserts
    /// the repo, the single flow that produced the run, and that flow's YAML schedule mirror, then inserts the run
    /// and its detail (idempotent: a run already recorded is left untouched, since a run is immutable). It
    /// deliberately does NOT recompute lineage
    /// or scan sibling flows: the execution plan and the cross-repo graph stay the full sync's responsibility, so
    /// the per-run cost is bounded. Runs in one serializable transaction; the caller treats any failure as
    /// non-fatal to the run itself.
    /// </summary>
    public async Task<RecordRunResult> RecordRunAsync(
        CatalogDbContext context, string flowFilePath, string runJsonPath, string repoName, string? repoRemoteUrl,
        DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(flowFilePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runJsonPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoName);

        var fullFlowPath = Path.GetFullPath(flowFilePath);
        var root = Path.GetDirectoryName(fullFlowPath)
            ?? throw new SqlFlow.Core.SqlFlowException($"'{flowFilePath}' has no parent directory.");
        var repoId = FlowIdentity.FromName(repoName);
        // One serializable transaction, run through the context's execution strategy so it is a single retriable
        // unit. The control plane enables connection resiliency (EnableRetryOnFailure); EF then forbids a
        // user-initiated transaction unless it is wrapped this way. The work rebuilds all its state from the flow
        // document and run.json each attempt, so a retry is safe. See InSerializableTransactionAsync.
        return await CatalogTransaction.InSerializableAsync(context, async () =>
        {
            var warnings = new List<string>();
            await UpsertRepoAsync(context, repoId, repoName, repoRemoteUrl, root, nowUtc, ct).ConfigureAwait(false);
            var pipelineChange = await UpsertSinglePipelineAsync(context, root, fullFlowPath, repoId, nowUtc, warnings, ct).ConfigureAwait(false);

            var runRecorded = false;
            var detail = (Files: 0, Assertions: 0, Statements: 0, Events: 0, SurrogateKeys: 0, Metrics: 0, SchemaChanges: 0);

            // A corrupt, oversized, or unreadable run.json is reported as a warning and the run is simply not
            // recorded; the pipeline upsert above still commits (consistent with how the full sync treats a bad
            // artifact, and with the method's non-fatal contract).
            try
            {
                var length = new FileInfo(runJsonPath).Length;
                if (length > MaxRunJsonBytes)
                {
                    warnings.Add($"run artifact '{runJsonPath}' is {length} bytes, over the {MaxRunJsonBytes}-byte limit; not recorded.");
                }
                else
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath, ct).ConfigureAwait(false));
                    var run = CatalogProjection.RunFromJson(document.RootElement, repoId);
                    if (run is null)
                    {
                        warnings.Add($"run artifact '{runJsonPath}' is missing required fields; not recorded.");
                    }
                    else if (await context.Runs.FindAsync([run.RunId], ct).ConfigureAwait(false) is null)
                    {
                        context.Runs.Add(run);
                        runRecorded = true;
                        detail = AddRunDetail(
                            context, document.RootElement, run.RunId, repoId, pipelineId: run.PipelineId);

                        // Refresh the pipeline's detected view projection from this run ("latest run wins"),
                        // unless the catalog already knows a newer run for the pipeline (a stale artifact
                        // recorded late must not regress the snapshot).
                        var detected = CatalogProjection.PipelineColumnsDetected(document.RootElement, repoId, run.PipelineId);
                        if (detected.Count > 0)
                        {
                            var newerKnown = await context.Runs
                                .AnyAsync(r => r.PipelineId == run.PipelineId && r.WrittenUtc > run.WrittenUtc, ct).ConfigureAwait(false);
                            if (!newerKnown)
                            {
                                await context.PipelineColumns
                                    .Where(c => c.PipelineId == run.PipelineId && c.Kind == PipelineColumnKinds.Detected)
                                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
                                foreach (var row in detected)
                                {
                                    context.PipelineColumns.Add(row);
                                }
                            }
                        }
                    }

                    // else: already recorded (a prior full sync or write-back); a run is immutable, so nothing to do.
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"run artifact '{runJsonPath}' could not be read ({SecretHygiene.RedactedMessage(ex)}); not recorded.");
            }

            return new RecordRunResult
            {
                PipelineChange = pipelineChange,
                RunRecorded = runRecorded,
                RunFilesAdded = detail.Files,
                RunAssertionsAdded = detail.Assertions,
                RunStatementsAdded = detail.Statements,
                RunEventsAdded = detail.Events,
                RunSurrogateKeysAdded = detail.SurrogateKeys,
                RunHealthCheckMetricsAdded = detail.Metrics,
                SchemaChangesAdded = detail.SchemaChanges,
                Warnings = warnings,
            };
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Upserts the single flow that produced a run by loading and parsing JUST that document (a targeted
    /// read, never an estate scan), reusing the same redaction, hashing, and projection as the full pipeline
    /// sync. Lineage-derived fields (Wave) are left to the full sync; an unchanged flow only re-affirms its
    /// presence. A missing, unreadable, or unparseable document is a warning and the run is recorded without a
    /// pipeline row.</summary>
    private async Task<PipelineChange> UpsertSinglePipelineAsync(
        CatalogDbContext context, string root, string fullFlowPath, Guid repoId, DateTime nowUtc, List<string> warnings, CancellationToken ct)
    {
        var relativePath = Path.GetRelativePath(root, fullFlowPath);
        var rawYaml = ReadYaml(fullFlowPath, relativePath, warnings);
        if (rawYaml is null)
        {
            return PipelineChange.None;
        }

        FlowDocument document;
        IReadOnlyList<DocumentFlowHeader> headers;
        try
        {
            document = _documents.Parse(rawYaml, fullFlowPath);
            headers = FlowDocumentHeaders.Project(document);
        }
        catch (SqlFlow.Core.SqlFlowException ex)
        {
            warnings.Add($"'{fullFlowPath}' could not be parsed as a flow ({SecretHygiene.RedactedMessage(ex)}); its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }
        if (headers.Count == 0)
        {
            warnings.Add($"'{fullFlowPath}' is an orchestration document, not a runnable flow; its run is recorded without a pipeline row.");
            return PipelineChange.None;
        }

        if (SecretHygiene.LooksLikeEmbeddedSecret(rawYaml))
        {
            warnings.Add($"'{headers[0].Name}' ({relativePath}) appears to embed a credential; it is redacted in the catalog, but secrets must be ${{env:...}}/${{keyvault:...}} references in the YAML, not literals.");
        }

        var yaml = SecretHygiene.RedactedMessage(rawYaml);
        var hash = CatalogProjection.Hash(yaml);

        // A document can expand into more than one pipeline (an ingestion flow with an embedded healthCheck:
        // block derives a sibling hc flow); every header upserts so both rows stay current, and the primary
        // flow's change is what the write-back reports.
        var primaryChange = PipelineChange.None;
        for (var i = 0; i < headers.Count; i++)
        {
            var change = await UpsertPipelineRowAsync(
                context, document, headers[i], repoId, relativePath, fullFlowPath, yaml, hash, nowUtc, warnings, ct).ConfigureAwait(false);
            if (i == 0)
            {
                primaryChange = change;
            }
        }

        // Schedules are deliberately NOT mirrored here. A schedule belongs to a name and owns a MEMBER SET, and
        // membership is a repo-wide fact: this flow's document cannot say who else joined the name, so writing the
        // schedule from one file would either invent an empty member set or clobber the one the full estate scan
        // established. The full sync owns schedules; a run-only write-back leaves them exactly as it found them.
        return primaryChange;
    }

    /// <summary>Upserts one pipeline row from an already-read document, one header at a time (shared by the
    /// primary flow and any derived sibling). Declared columns refresh alongside, gated by the header's kind so
    /// the load's transform columns are never attributed to a derived hc pipeline.</summary>
    private static async Task<PipelineChange> UpsertPipelineRowAsync(
        CatalogDbContext context, FlowDocument document, DocumentFlowHeader header, Guid repoId,
        string relativePath, string fullFlowPath, string yaml, string hash, DateTime nowUtc,
        List<string> warnings, CancellationToken ct)
    {
        var id = CatalogIdentity.Pipeline(repoId, header.Name);
        var row = await context.Pipelines.FindAsync([id], ct).ConfigureAwait(false);

        // Refresh this one pipeline's declared columns from its YAML on every write-back: declared columns are the
        // source-of-truth projection, cheap to rebuild for a single flow, and this keeps a run-only node's catalog
        // current with authored transforms without waiting for a full sync. Replaced by (pipeline, declared).
        await context.PipelineColumns
            .Where(c => c.PipelineId == id && c.Kind == PipelineColumnKinds.Declared)
            .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var column in ProjectDeclaredColumns(document, header.Kind, repoId, id))
        {
            context.PipelineColumns.Add(column);
        }

        if (row is not null && string.Equals(row.ContentHash, hash, StringComparison.Ordinal))
        {
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            row.RelativePath = Normalize(relativePath);
            return PipelineChange.Unchanged;
        }

        var definitionJson = SerializeDefinition(document, fullFlowPath, warnings);
        var projected = CatalogProjection.Pipeline(
            repoId, header.Name, header.Kind, header.Batch, Normalize(relativePath),
            header.SourceServerRef, header.TargetServerRef, hash, yaml, definitionJson, nowUtc,
            header.Mode, header.Lifecycle);

        if (row is not null)
        {
            row.Name = projected.Name;
            row.Kind = projected.Kind;
            row.Batch = projected.Batch;
            row.RelativePath = projected.RelativePath;
            row.ExecutionMode = projected.ExecutionMode;
            row.Lifecycle = projected.Lifecycle;
            row.SourceServer = projected.SourceServer;
            row.TargetServer = projected.TargetServer;
            row.ContentHash = projected.ContentHash;
            row.Yaml = projected.Yaml;
            row.DefinitionJson = projected.DefinitionJson;
            row.Active = true;
            row.LastSeenUtc = nowUtc;
            return PipelineChange.Updated;
        }

        context.Pipelines.Add(projected);
        return PipelineChange.Added;
    }

    /// <summary>
    /// The offline half of identity healing: rewrites a report so each database-less object identity adopts the
    /// registry's database-qualified row when exactly one candidate exists for the same server reference and
    /// schema/name. A connected sync taught the catalog the object's default database; an offline re-sync must
    /// land on that identity (objects, edges, data-model relationships, and dependency via-keys rewritten
    /// together) instead of re-creating a weak twin row. An ambiguous candidate set (the same schema.name under
    /// two databases of one server) leaves the weak identity untouched: a wrong merge would silently corrupt
    /// lineage, while a split only clutters the explorer until connectivity resolves it.
    /// </summary>
    private static async Task<LineageReport> AdoptResolvedIdentitiesAsync(
        CatalogDbContext context, LineageReport report, CancellationToken ct)
    {
        var weakNodes = report.Objects
            .Where(o => string.IsNullOrWhiteSpace(o.Database) && o.Kind != Core.Lineage.LineageNodeKind.File)
            .ToList();
        if (weakNodes.Count == 0)
        {
            return report;
        }

        // The resolved rows these weak identities could adopt: one bounded read per involved server reference.
        var serverRefs = weakNodes.Select(o => o.ServerRef).Distinct(StringComparer.Ordinal).ToList();
        var candidates = await SelectByKeysAsync(
                serverRefs,
                chunk => context.Objects.AsNoTracking()
                    .Where(o => chunk.Contains(o.ServerRef) && o.Database != null)
                    .Select(o => new { o.Key, o.ServerRef, o.Database, o.Schema, o.Name })
                    .ToListAsync(ct))
            .ConfigureAwait(false);
        if (candidates.Count == 0)
        {
            return report;
        }

        var adopted = new Dictionary<string, (string Key, string Database)>(StringComparer.Ordinal);
        foreach (var node in weakNodes)
        {
            var matches = candidates
                .Where(c => string.Equals(c.ServerRef, node.ServerRef, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Schema, node.Schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(c.Name, node.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count == 1)
            {
                adopted[node.Key] = (matches[0].Key, matches[0].Database!);
            }
        }

        if (adopted.Count == 0)
        {
            return report;
        }

        string MapKey(string key) => adopted.TryGetValue(key, out var target) ? target.Key : key;

        // Non-adopted nodes first, so a weak node whose adopted key collides with a resolved node ALSO in this
        // report simply drops (the stronger node carries the metadata); order is then restored by key.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var objects = new List<LineageObjectNode>(report.Objects.Count);
        foreach (var node in report.Objects.OrderBy(o => adopted.ContainsKey(o.Key) ? 1 : 0))
        {
            var mapped = adopted.TryGetValue(node.Key, out var target)
                ? node with { Key = target.Key, Database = target.Database }
                : node;
            if (seen.Add(mapped.Key))
            {
                objects.Add(mapped);
            }
        }

        objects.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

        return report with
        {
            Objects = objects,
            // Edge records are value-equal, so a weak and a resolved spelling of the same fact collapse.
            Edges = report.Edges.Select(e => e with { ObjectKey = MapKey(e.ObjectKey) }).Distinct().ToList(),
            Relationships = report.Relationships
                .Select(r => r with { FromObjectKey = MapKey(r.FromObjectKey), ToObjectKey = MapKey(r.ToObjectKey) })
                .ToList(),
            FlowDependencies = report.FlowDependencies
                .Select(d => d with { ViaObjects = d.ViaObjects.Select(MapKey).ToList() })
                .ToList(),
        };
    }

    /// <summary>Writes a precomputed lineage report into the catalog: the global object registry, the data
    /// dictionary, this repo's edges and flow dependencies, the execution waves, and the identity healing. Runs
    /// inside the sync's transaction and performs only database work; the report itself was computed before the
    /// transaction opened.</summary>
    private static async Task<(int Objects, int Superseded, int Columns, int Edges, int FlowDeps, int Waves, bool Connected, int PreservedDerived)> ApplyLineageAsync(
        CatalogDbContext context, Guid repoId, LineageReport report, bool includeDerived, DateTime nowUtc, CancellationToken ct)
    {
        // Identity adoption, the offline half of identity healing: a database-less identity in this report
        // adopts the registry's database-qualified row when exactly one exists for the same server reference
        // and schema/name. A connected sync taught the catalog the object's default database; an offline
        // re-sync (or one whose derived tier degraded to a warning) must not split the object back into a
        // weak twin row with its edges pointing at the weak key. Ambiguity (the same schema.name under two
        // databases of one server) keeps the weak identity, mirroring the builder's unification rule.
        report = await AdoptResolvedIdentitiesAsync(context, report, ct).ConfigureAwait(false);

        // Objects are GLOBAL (shared across repos by their canonical key) - upsert, never delete. Load only the
        // keys this report mentions, so the working set scales with the report, not the whole catalog.
        var keys = report.Objects.Select(o => o.Key).Distinct().ToList();
        // AsTracking so the object-metadata updates below persist under a NoTracking host context (see the pipeline
        // query in ApplyPipelinesAsync); harmless on a tracking context.
        var existingObjects = (await SelectByKeysAsync(
                keys, chunk => context.Objects.Where(o => chunk.Contains(o.Key)).AsTracking().ToListAsync(ct))
            .ConfigureAwait(false)).ToDictionary(o => o.Key);
        // Every row this report touches (updated or inserted), so the level stamping below reaches both.
        var touchedObjects = new Dictionary<string, CatalogObject>(StringComparer.Ordinal);
        foreach (var node in report.Objects)
        {
            if (existingObjects.TryGetValue(node.Key, out var row))
            {
                row.ServerRef = node.ServerRef;
                row.Database = NullIfBlank(node.Database);
                row.Schema = NullIfBlank(node.Schema);
                row.Name = node.Name;
                row.Kind = node.Kind.ToString();
                // Only the derived tier reads a module body; an offline sync must not null a stored definition.
                // When present it is redacted (a body can embed a literal credential) and normalized the same way
                // as a freshly-inserted object (blank -> null).
                if (node.Definition is not null)
                {
                    row.Definition = NullIfBlank(SecretHygiene.RedactedMessage(node.Definition));
                }

                // The generating DDL, likewise: only overwrite when this sync captured a script, so an offline
                // sync that saw no run trace never wipes a script an earlier sync stored.
                if (node.Script is not null)
                {
                    row.Script = NullIfBlank(SecretHygiene.RedactedMessage(node.Script));
                    row.ScriptTier = node.ScriptTier?.ToString();
                    row.ScriptUpdatedUtc = nowUtc;
                }

                // The interpreted key, likewise: only overwrite when this sync's codebase named one, so a repo
                // that never loads the object cannot wipe the key its loading repo declared.
                if (node.KeyColumns.Count > 0)
                {
                    row.KeyColumns = string.Join(",", node.KeyColumns);
                    row.KeyOrigin = node.KeyOrigin?.ToString();
                }

                row.LastSeenUtc = nowUtc;
                touchedObjects[node.Key] = row;
            }
            else
            {
                var added = CatalogProjection.MapObject(node, nowUtc);
                context.Objects.Add(added);
                touchedObjects[node.Key] = added;
            }
        }

        // Object columns are the data dictionary. Refresh ONLY the objects a tier supplied columns for this
        // sync (the node carries columns); a failed or offline collection (no columns) never wipes a
        // previously-collected dictionary. Replace-by-key so a re-read reflects schema changes. Precedence:
        // an offline (Observed) set must not overwrite a live (Derived) set an earlier connected sync stored,
        // so a key is refreshed only when this sync's tier is at least as authoritative as what is on record.
        // Like the global Object/Definition (upsert-only, never deleted for cross-repo identity), a dropped
        // object's columns are NOT cleaned up here; the dictionary is additive and tolerates that staleness.
        var columns = 0;
        var withColumns = report.Objects.Where(o => o.Columns.Count > 0).ToList();
        if (withColumns.Count > 0)
        {
            var candidateKeys = withColumns.Select(o => o.Key).Distinct().ToList();
            // Distinct per chunk is a true distinct: the chunks partition the keys, so no pair repeats.
            var existingTiers = await SelectByKeysAsync(
                    candidateKeys,
                    chunk => context.ObjectColumns
                        .Where(c => chunk.Contains(c.ObjectKey))
                        .Select(c => new { c.ObjectKey, c.Tier })
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var existingRankByKey = existingTiers
                .GroupBy(x => x.ObjectKey, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Max(x => TierRank(x.Tier)), StringComparer.Ordinal);

            var nodesToRefresh = withColumns
                .Where(o => TierRank((o.ColumnsTier ?? Core.Lineage.LineageTier.Observed).ToString())
                            >= (existingRankByKey.TryGetValue(o.Key, out var rank) ? rank : -1))
                // One object can be surfaced by more than one flow in a single report: a generated view is
                // DECLARED by its pre flow (transform.generateView) and READ as the source of its ods flow, so
                // report.Objects holds two nodes with the same key, each numbering columns from ordinal 1.
                // Staging both would violate the (ObjectKey, Ordinal) unique index and abort the ENTIRE repo
                // sync over one object. Keep a single column set per key: the most authoritative tier, then the
                // richest declaration.
                .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g
                    .OrderByDescending(o => TierRank((o.ColumnsTier ?? Core.Lineage.LineageTier.Observed).ToString()))
                    .ThenByDescending(o => o.Columns.Count)
                    .First())
                .ToList();
            var refreshedKeys = nodesToRefresh.Select(o => o.Key).Distinct().ToList();
            if (refreshedKeys.Count > 0)
            {
                await ExecuteByKeysAsync(
                        refreshedKeys, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
                foreach (var node in nodesToRefresh)
                {
                    var tier = (node.ColumnsTier ?? Core.Lineage.LineageTier.Observed).ToString();
                    foreach (var column in node.Columns)
                    {
                        context.ObjectColumns.Add(new CatalogObjectColumn
                        {
                            ObjectKey = node.Key,
                            Ordinal = column.Ordinal,
                            Name = column.Name,
                            DataType = column.DataType,
                            Nullable = column.Nullable,
                            Tier = tier,
                        });
                        columns++;
                    }
                }
            }
        }

        // Offline object-body enrichment: fill each resolved object's generating script and interpreted column
        // dictionary from the run-statement trace already persisted in the catalog, so an offline sync (no live
        // catalog to read, no run.json on disk) still shows an object's code and columns, parsed from the exact
        // T-SQL the engine ran. Additive to the tier-supplied columns above; a live (Derived) set is never
        // downgraded.
        columns += await ApplyObservedObjectScriptsAsync(context, repoId, report, touchedObjects, nowUtc, ct).ConfigureAwait(false);

        // Final safety net for the data dictionary: the column set for one object is staged from more than one
        // path (the report's tier-supplied columns above and the observed-script enrichment), and those paths
        // delete-by-key against the DATABASE, not against each other's still-pending inserts. If two of them
        // stage the same (ObjectKey, Ordinal), the unique index throws at SaveChanges and rolls back the WHOLE
        // repo sync - every pipeline, run, and edge - over a single object. Collapse any duplicate staged column
        // to the most authoritative tier so one object can never abort the sync. This runs on the change tracker
        // just before the pass commits, so it catches collisions regardless of which path produced them.
        var stagedDuplicates = context.ChangeTracker.Entries<CatalogObjectColumn>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .GroupBy(c => (Key: c.ObjectKey.ToLowerInvariant(), c.Ordinal))
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var group in stagedDuplicates)
        {
            var keep = group.OrderByDescending(c => TierRank(c.Tier)).First();
            foreach (var drop in group)
            {
                if (!ReferenceEquals(drop, keep))
                {
                    context.Entry(drop).State = EntityState.Detached;
                    columns--;
                }
            }
        }

        // Edges are this repo's view of the graph: replace them wholesale so a removed flow's edges do not
        // linger. EXCEPT the derived knowledge this pass could not re-derive: module-body lineage exists only
        // when the connected tier reaches its server, and a recompute that ran offline (or whose connect failed
        // for a server) must not wipe what an earlier connected pass learned - the same additive principle the
        // object registry applies to definitions and columns. Offline recompute preserves every stored Derived
        // edge; a connected pass preserves the Derived edges of exactly its degraded servers (keys are
        // case-folded, so the server-reference prefix identifies them on either end of the fact).
        var preserve = new List<CatalogLineageEdge>();
        if (!includeDerived)
        {
            preserve = await context.LineageEdges.AsNoTracking()
                .Where(e => e.RepoId == repoId && e.Tier == "Derived")
                .ToListAsync(ct).ConfigureAwait(false);
        }
        else if (report.DegradedDerivedServers.Count > 0)
        {
            var prefixes = report.DegradedDerivedServers
                .Select(s => s.ToLowerInvariant() + "|")
                .ToList();
            var storedDerived = await context.LineageEdges.AsNoTracking()
                .Where(e => e.RepoId == repoId && e.Tier == "Derived")
                .ToListAsync(ct).ConfigureAwait(false);
            preserve = storedDerived
                .Where(e => prefixes.Any(p => e.ObjectKey.StartsWith(p, StringComparison.Ordinal)
                    || (e.ViaModule != null && e.ViaModule.StartsWith(p, StringComparison.Ordinal))))
                .ToList();
        }

        await context.LineageEdges.Where(e => e.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var objectNames = report.Objects.ToDictionary(o => o.Key, o => o.Name, StringComparer.Ordinal);
        var edges = 0;
        var freshIdentities = new HashSet<(string, string, string, string)>();
        foreach (var edge in report.Edges)
        {
            var name = objectNames.TryGetValue(edge.ObjectKey, out var n) ? n : edge.ObjectKey;
            context.LineageEdges.Add(CatalogProjection.Edge(edge, repoId, name));
            freshIdentities.Add((edge.Flow ?? string.Empty, edge.ViaModule ?? string.Empty, edge.Relation.ToString(), edge.ObjectKey));
            edges++;
        }

        var preservedEdges = 0;
        foreach (var edge in preserve)
        {
            if (freshIdentities.Contains((edge.Flow ?? string.Empty, edge.ViaModule ?? string.Empty, edge.Relation, edge.ObjectKey)))
            {
                continue; // this pass re-derived the same fact; the fresh row carries it.
            }

            context.LineageEdges.Add(new CatalogLineageEdge
            {
                RepoId = repoId,
                Flow = edge.Flow,
                PipelineId = edge.PipelineId,
                ViaModule = edge.ViaModule,
                Relation = edge.Relation,
                ObjectKey = edge.ObjectKey,
                ObjectName = edge.ObjectName,
                Tier = edge.Tier,
            });
            preservedEdges++;
            edges++;
        }

        // The interpreted data model is this repo's view too (its code exhibited the joins), so it is
        // replaced per repo the same way; a dossier deduplicates the same relationship across repos at read.
        await context.ObjectRelationships.Where(r => r.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var relationship in report.Relationships)
        {
            context.ObjectRelationships.Add(CatalogProjection.Relationship(relationship, repoId));
        }

        // The consumption side. Repo-scoped and replaced wholesale like the edges above: a subscriber deleted from
        // subscribers.yaml must stop being listed as a consumer, and its stale queries must go with it. FirstSeenUtc
        // survives the replacement, so the catalog can still say how long a report has been reading the warehouse.
        var subscriberFirstSeen = await context.Subscribers.AsNoTracking()
            .Where(s => s.RepoId == repoId)
            .Select(s => new { s.ObjectKey, s.FirstSeenUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        var firstSeenByKey = subscriberFirstSeen.ToDictionary(s => s.ObjectKey, s => s.FirstSeenUtc, StringComparer.Ordinal);

        await context.SubscriberQueries.Where(q => q.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await context.Subscribers.Where(s => s.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        foreach (var subscriber in report.Subscribers)
        {
            context.Subscribers.Add(new CatalogSubscriber
            {
                RepoId = repoId,
                Name = subscriber.Name,
                Type = subscriber.Type,
                ObjectKey = subscriber.ObjectKey,
                File = subscriber.File,
                Owner = subscriber.Owner,
                Description = subscriber.Description,
                Notes = subscriber.Notes,
                Url = subscriber.Url,
                FirstSeenUtc = firstSeenByKey.TryGetValue(subscriber.ObjectKey, out var seen) ? seen : nowUtc,
                LastSeenUtc = nowUtc,
            });

            var ordinal = 0;
            foreach (var query in subscriber.Queries)
            {
                ordinal++;
                context.SubscriberQueries.Add(new CatalogSubscriberQuery
                {
                    RepoId = repoId,
                    SubscriberKey = subscriber.ObjectKey,
                    Ordinal = ordinal,
                    Name = query.Name,
                    ServerRef = query.ServerRef,
                    // A subscriber query is authored SQL and can embed a literal credential exactly as a module
                    // body can, so it is redacted on the same path the object definitions take.
                    Sql = SecretHygiene.RedactedMessage(query.Sql),
                    ObjectKeys = string.Join('\n', query.ObjectKeys),
                });
            }
        }

        // Object levels: each object's depth in the ESTATE-WIDE data-movement graph, so the explorer lists and
        // sorts objects in dependency order (sources first, then everything derived from them, row by row).
        // The graph merges this report's edges (pending in the change tracker; this repo's stored edges were
        // deleted above) with every other repo's stored edges, so a table produced in one repo keeps its depth
        // when this repo only reads it. Report objects are stamped on their tracked/added rows; objects outside
        // the report whose depth shifted through a cross-repo chain are updated in place, grouped by level (the
        // distinct level count is small). An object that leaves the movement graph entirely keeps its last
        // level until its own repo resyncs, matching the catalog's additive, staleness-tolerant object registry.
        var otherRepoFacts = await context.LineageEdges.AsNoTracking()
            .Where(e => e.RepoId != repoId)
            .Select(e => new MovementFact(e.RepoId, e.Flow, e.ViaModule, e.Relation, e.ObjectKey))
            .ToListAsync(ct).ConfigureAwait(false);
        var levels = ComputeObjectLevels(otherRepoFacts.Concat(report.Edges.Select(e =>
            new MovementFact(repoId, NullIfBlank(e.Flow), NullIfBlank(e.ViaModule), e.Relation.ToString(), e.ObjectKey))));
        foreach (var (key, row) in touchedObjects)
        {
            row.Level = levels.TryGetValue(key, out var level) ? level : null;
        }

        foreach (var group in levels.Where(kv => !touchedObjects.ContainsKey(kv.Key)).GroupBy(kv => kv.Value))
        {
            var levelValue = group.Key;
            await ExecuteByKeysAsync(
                    group.Select(kv => kv.Key).ToList(),
                    chunk => context.Objects
                        .Where(o => chunk.Contains(o.Key) && o.Level != levelValue)
                        .ExecuteUpdateAsync(s => s.SetProperty(o => o.Level, levelValue), ct))
                .ConfigureAwait(false);
        }

        // Identity healing: a key that now carries its database (or schema) supersedes the weaker key an
        // earlier sync recorded for the SAME object (a file-flow target keyed '<server>||<schema>|<name>'
        // before default-database resolution, for example). Objects are global and upsert-only for
        // cross-repo identity, but a weaker twin is not another repo's object, it is this object under a
        // partial key; delete it once no repo's edges reference it, so the explorer stops listing a
        // columnless duplicate. This repo's replacement edges above only reference report keys, which are
        // excluded here, so a twin still live in THIS report is never deleted.
        var weakerTwins = report.Objects
            .SelectMany(o => new[]
            {
                string.IsNullOrWhiteSpace(o.Database) ? null : NodeKey.For(o.ServerRef, null, o.Schema, o.Name),
                string.IsNullOrWhiteSpace(o.Schema) ? null : NodeKey.For(o.ServerRef, o.Database, null, o.Name),
            })
            .OfType<string>()
            .Where(twin => !objectNames.ContainsKey(twin))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var superseded = 0;
        if (weakerTwins.Count > 0)
        {
            var stillReferenced = await SelectByKeysAsync(
                    weakerTwins,
                    chunk => context.LineageEdges
                        .Where(e => chunk.Contains(e.ObjectKey))
                        .Select(e => e.ObjectKey)
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var deletable = weakerTwins.Except(stillReferenced, StringComparer.Ordinal).ToList();
            if (deletable.Count > 0)
            {
                superseded = await ExecuteByKeysAsync(
                        deletable, chunk => context.Objects.Where(o => chunk.Contains(o.Key)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
                await ExecuteByKeysAsync(
                        deletable, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
            }
        }

        // The stale-spelling sweep, the second half of the twin cleanup: a weak (database-less) row recorded
        // under a server reference the estate no longer spells (a keyvault ref replaced by an env ref) can
        // never be derived as a twin of THIS report's keys, so the pass above cannot see it. Any non-file
        // database-less row that this report did not touch and that no repo's edges and no data-model
        // relationship reference anymore is residue of an older spelling, not an object: delete it, or the
        // explorer lists it under "(unresolved)" forever. Files stay: a file identity has no database by
        // design. The weak-row set is naturally small (identities pending resolution), so one unchunked read
        // of the keys is bounded.
        var staleWeakKeys = (await context.Objects
                .Where(o => o.Database == null && o.Kind != "File")
                .Select(o => o.Key)
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(key => !objectNames.ContainsKey(key))
            .ToList();
        if (staleWeakKeys.Count > 0)
        {
            var edgeReferenced = await SelectByKeysAsync(
                    staleWeakKeys,
                    chunk => context.LineageEdges
                        .Where(e => chunk.Contains(e.ObjectKey))
                        .Select(e => e.ObjectKey)
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            // Two simple membership queries (per direction) instead of one SelectMany over both columns,
            // which EF cannot translate to SQL.
            var referencedAsFrom = await SelectByKeysAsync(
                    staleWeakKeys,
                    chunk => context.ObjectRelationships
                        .Where(r => chunk.Contains(r.FromObjectKey))
                        .Select(r => r.FromObjectKey)
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var referencedAsTo = await SelectByKeysAsync(
                    staleWeakKeys,
                    chunk => context.ObjectRelationships
                        .Where(r => chunk.Contains(r.ToObjectKey))
                        .Select(r => r.ToObjectKey)
                        .Distinct()
                        .ToListAsync(ct))
                .ConfigureAwait(false);
            var relationshipReferenced = referencedAsFrom.Concat(referencedAsTo).ToList();
            var sweepable = staleWeakKeys
                .Except(edgeReferenced, StringComparer.Ordinal)
                .Except(relationshipReferenced, StringComparer.Ordinal)
                .ToList();
            if (sweepable.Count > 0)
            {
                superseded += await ExecuteByKeysAsync(
                        sweepable, chunk => context.Objects.Where(o => chunk.Contains(o.Key)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
                await ExecuteByKeysAsync(
                        sweepable, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
                    .ConfigureAwait(false);
            }
        }

        // The execution plan - lineage's primary output. Stamp each pipeline with its wave (its batch and order)
        // and replace this repo's flow-level dependency edges, so a GUI/orchestrator reads the runnable order.
        foreach (var wave in report.ExecutionPlan.Waves)
        {
            foreach (var flow in wave.Flows)
            {
                var pipeline = await context.Pipelines.FindAsync([CatalogIdentity.Pipeline(repoId, flow)], ct).ConfigureAwait(false);
                if (pipeline is not null)
                {
                    pipeline.Wave = wave.Wave;
                }
            }
        }

        await context.FlowDependencies.Where(d => d.RepoId == repoId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var dependencies = 0;
        foreach (var dependency in report.FlowDependencies)
        {
            var via = string.Join(", ", dependency.ViaObjects.Select(k => objectNames.TryGetValue(k, out var n) ? n : k));
            context.FlowDependencies.Add(CatalogProjection.FlowDependency(dependency, repoId, via));
            dependencies++;
        }

        return (report.Objects.Count, superseded, columns, edges, dependencies, report.ExecutionPlan.Waves.Count, includeDerived, preservedEdges);
    }

    /// <summary>
    /// The offline object-body enrichment. A warehouse's engine-created objects (a transform view, a pre/arc
    /// table) never appear as authored CREATE DDL in the YAML, and their bodies are not read from a live
    /// catalog offline, so an offline sync would leave them identity-only skeletons: no code, no columns. But
    /// the exact T-SQL the engine ran was captured in each run's statement trace and persists in
    /// <see cref="CatalogRunStatement"/>. This pass re-parses the latest run's trace per active pipeline through
    /// the SAME extractor the lineage tiers use, and stamps each created object's generating script (the CREATE
    /// text) and interpreted column dictionary (parsed from the view's SELECT projection or the table's column
    /// definitions) onto its catalog row. Identity-first: only objects THIS report resolved are enriched, matched
    /// to the two-part names the generated DDL uses by an unambiguous (server reference, schema, name) key.
    /// Tier precedence is honored throughout: an Observed script or column set never overwrites a Derived one an
    /// earlier connected sync stored. Returns the number of column rows written.
    /// </summary>
    /// <summary>
    /// Groups a report's non-file objects by the database-less location key the engine's generated two-part DDL
    /// (`[schema].[name]`) resolves to, mapping each location to every object key that shares it. Several report
    /// objects legitimately share one location: identity resolution leaves both a database-qualified row and a
    /// database-less "unresolved" twin of the SAME physical table (one server reference, one schema, one name),
    /// and both must receive the enrichment. Genuine ambiguity, the same schema.name under two DIFFERENT databases
    /// on one server, cannot be resolved from a two-part name and is dropped (a wrong body is worse than a missing
    /// one); a qualified-plus-null pair is not that and is kept.
    /// </summary>
    internal static IReadOnlyDictionary<string, IReadOnlyList<string>> EnrichableLocations(
        IEnumerable<LineageObjectNode> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var group in objects
                     .Where(o => o.Kind != Core.Lineage.LineageNodeKind.File)
                     .GroupBy(o => NodeKey.For(o.ServerRef, null, o.Schema, o.Name), StringComparer.Ordinal))
        {
            var distinctDatabases = group
                .Select(o => o.Database)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (distinctDatabases > 1)
            {
                continue; // the same schema.name under two databases: not resolvable from a two-part name.
            }

            result[group.Key] = group.Select(o => o.Key).Distinct(StringComparer.Ordinal).ToList();
        }

        return result;
    }

    private static async Task<int> ApplyObservedObjectScriptsAsync(
        CatalogDbContext context, Guid repoId, LineageReport report,
        IReadOnlyDictionary<string, CatalogObject> touchedObjects, DateTime nowUtc, CancellationToken ct)
    {
        // Index this report's non-file objects by the database-less location key the generated two-part DDL
        // (`[schema].[name]`) resolves to, one location to all the object keys that share it (see EnrichableLocations).
        var keysByLocation = EnrichableLocations(report.Objects);
        if (keysByLocation.Count == 0)
        {
            return 0;
        }

        // The objects still awaiting a generating script. Only an object some flow WRITES or CREATES can have a
        // generating statement in the trace, so a read-only source table is not sought (that both bounds the scan
        // and stops a never-created object from forcing a walk of the whole history). The scan below removes a
        // key once its creating statement is found, so it stops as soon as every written object is covered.
        var writtenKeys = report.Edges
            .Where(e => e.Relation is Core.Lineage.LineageRelation.Writes or Core.Lineage.LineageRelation.Creates)
            .Select(e => e.ObjectKey)
            .ToHashSet(StringComparer.Ordinal);
        var remaining = new HashSet<string>(
            keysByLocation.Values.SelectMany(keys => keys).Where(writtenKeys.Contains),
            StringComparer.Ordinal);
        if (remaining.Count == 0)
        {
            return 0;
        }

        // The active pipelines and the server each side of their generated SQL ran against (the target by
        // default; source-step statements ran on the source), so an artifact is attributed to the right server.
        // Read from the change tracker, not a fresh query: ApplyPipelinesAsync tracked this repo's whole
        // pipeline set (existing rows loaded, new rows added) but has not committed it yet, so a database query
        // inside this transaction would miss a pipeline added this pass.
        var pipelines = context.Pipelines.Local
            .Where(p => p.RepoId == repoId && p.Active)
            .GroupBy(p => p.Id)
            .ToDictionary(g => g.Key, g => (g.First().TargetServer, g.First().SourceServer));
        if (pipelines.Count == 0)
        {
            return 0;
        }

        // Every committed run of this repo, newest first: an object's generating statement is taken from the most
        // recent run that emitted it. This matters because the engine re-emits a `CREATE OR ALTER VIEW` on every
        // run but a `CREATE TABLE` only when the table is first created or its schema drifts, so a table's DDL
        // lives in an OLDER run than the latest one. Walking newest-first and stopping once every object is
        // covered captures both without reading more history than needed. Only committed runs are read (a run
        // discovered on disk THIS pass already contributed its artifacts through the lineage graph; this pass
        // serves the runs recorded by earlier write-backs, whose run.json is not on disk when the control plane
        // syncs a git checkout).
        var runs = (await context.Runs.AsNoTracking()
                .Where(r => r.RepoId == repoId)
                .Select(r => new { r.RunId, r.PipelineId, r.WrittenUtc })
                .ToListAsync(ct).ConfigureAwait(false))
            .Where(r => pipelines.ContainsKey(r.PipelineId))
            .OrderByDescending(r => r.WrittenUtc).ThenByDescending(r => r.RunId)
            .ToList();
        if (runs.Count == 0)
        {
            return 0;
        }

        var runIds = runs.Select(r => r.RunId).ToList();
        var statementsByRun = (await SelectByKeysAsync(
                    runIds,
                    chunk => context.RunStatements.AsNoTracking()
                        .Where(s => chunk.Contains(s.RunId))
                        .Select(s => new { s.RunId, s.Ordinal, s.Step, s.Sql })
                        .ToListAsync(ct))
                .ConfigureAwait(false))
            .GroupBy(s => s.RunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Ordinal).ToList());
        if (statementsByRun.Count == 0)
        {
            return 0;
        }

        // The generating script and column set discovered per resolved object key. First observation wins, and
        // the scan is newest-first, so the newest emission of each object's DDL is the one kept.
        var scripts = new Dictionary<string, CollectedObjectArtifact>(StringComparer.Ordinal);
        var columnsByKey = new Dictionary<string, IReadOnlyList<Core.Lineage.LineageColumn>>(StringComparer.Ordinal);

        void Fold(string? serverRef, IReadOnlyList<string> sql, string label)
        {
            if (string.IsNullOrWhiteSpace(serverRef) || sql.Count == 0)
            {
                return;
            }

            // GO-joined into one script so the engine's created-then-read-then-dropped staging dissolves across
            // statements exactly as it executed, mirroring the observed run-trace collector.
            var script = string.Join($"{Environment.NewLine}GO{Environment.NewLine}", sql);
            var deps = TSqlLineageExtractor.Extract(script, label);
            foreach (var artifact in ScriptFactBuilder.ObjectArtifacts(deps, serverRef, Core.Lineage.LineageTier.Observed, minimumParts: 2))
            {
                var location = NodeKey.For(artifact.ServerRef, null, artifact.Schema, artifact.Name);
                if (string.IsNullOrWhiteSpace(artifact.Script) || !keysByLocation.TryGetValue(location, out var objectKeys))
                {
                    continue;
                }

                // Every object at this location is the same physical table (a resolved row and its database-less
                // twin), so the one generating statement enriches all of them. Capture only those still awaiting
                // it, so the newest emission wins and the scan can stop once all are covered.
                foreach (var objectKey in objectKeys)
                {
                    if (!remaining.Remove(objectKey))
                    {
                        continue;
                    }

                    scripts[objectKey] = artifact;
                    if (artifact.Columns.Count > 0)
                    {
                        columnsByKey[objectKey] = artifact.Columns;
                    }
                }
            }
        }

        foreach (var run in runs)
        {
            if (remaining.Count == 0)
            {
                break;
            }

            if (!statementsByRun.TryGetValue(run.RunId, out var runStatements))
            {
                continue;
            }

            var pipeline = pipelines[run.PipelineId];
            var sourceSql = new List<string>();
            var targetSql = new List<string>();
            foreach (var statement in runStatements)
            {
                if (string.IsNullOrWhiteSpace(statement.Sql))
                {
                    continue;
                }

                (statement.Step.StartsWith("source.", StringComparison.OrdinalIgnoreCase) ? sourceSql : targetSql)
                    .Add(statement.Sql);
            }

            Fold(pipeline.TargetServer, targetSql, $"{run.PipelineId}/trace/target");
            Fold(pipeline.SourceServer ?? pipeline.TargetServer, sourceSql, $"{run.PipelineId}/trace/source");
        }

        // Stamp the generating script onto each resolved object, redacted (a generated statement can embed a
        // literal credential) and never over a higher-tier script an earlier connected sync stored.
        foreach (var (objectKey, artifact) in scripts)
        {
            if (!touchedObjects.TryGetValue(objectKey, out var row)
                || TierRank(nameof(Core.Lineage.LineageTier.Observed)) < TierRank(row.ScriptTier))
            {
                continue;
            }

            row.Script = NullIfBlank(SecretHygiene.RedactedMessage(artifact.Script!));
            row.ScriptTier = nameof(Core.Lineage.LineageTier.Observed);
            row.ScriptUpdatedUtc = nowUtc;
        }

        // Refresh the column dictionary for each resolved object, replace-by-key, honoring the same tier
        // precedence as the report's own column refresh: an Observed set never overwrites a Derived one.
        if (columnsByKey.Count == 0)
        {
            return 0;
        }

        var candidateKeys = columnsByKey.Keys.ToList();
        var existingTiers = await SelectByKeysAsync(
                candidateKeys,
                chunk => context.ObjectColumns
                    .Where(c => chunk.Contains(c.ObjectKey))
                    .Select(c => new { c.ObjectKey, c.Tier })
                    .Distinct()
                    .ToListAsync(ct))
            .ConfigureAwait(false);
        var existingRankByKey = existingTiers
            .GroupBy(x => x.ObjectKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Max(x => TierRank(x.Tier)), StringComparer.Ordinal);

        var refreshKeys = candidateKeys
            .Where(k => TierRank(nameof(Core.Lineage.LineageTier.Observed))
                        >= (existingRankByKey.TryGetValue(k, out var rank) ? rank : -1))
            .ToList();
        if (refreshKeys.Count == 0)
        {
            return 0;
        }

        await ExecuteByKeysAsync(
                refreshKeys, chunk => context.ObjectColumns.Where(c => chunk.Contains(c.ObjectKey)).ExecuteDeleteAsync(ct))
            .ConfigureAwait(false);

        var written = 0;
        foreach (var objectKey in refreshKeys)
        {
            foreach (var column in columnsByKey[objectKey])
            {
                context.ObjectColumns.Add(new CatalogObjectColumn
                {
                    ObjectKey = objectKey,
                    Ordinal = column.Ordinal,
                    Name = column.Name,
                    DataType = column.DataType,
                    Nullable = column.Nullable,
                    Tier = nameof(Core.Lineage.LineageTier.Observed),
                });
                written++;
            }
        }

        return written;
    }

    /// <summary>The authority ordering of a column/definition tier: Derived (live) beats Observed (parsed from
    /// a run) beats Declared. An unknown or blank tier ranks below all, so it never blocks a real refresh.</summary>
    private static int TierRank(string? tier) => tier switch
    {
        nameof(Core.Lineage.LineageTier.Derived) => 2,
        nameof(Core.Lineage.LineageTier.Observed) => 1,
        nameof(Core.Lineage.LineageTier.Declared) => 0,
        _ => -1,
    };

    /// <summary>One lineage fact reduced to what the object-level computation needs: which flow (repo-scoped)
    /// or module related to which object, and how. Flows are grouped by (repo, flow) because flow names are
    /// only unique within a repo.</summary>
    private sealed record MovementFact(Guid RepoId, string? Flow, string? ViaModule, string Relation, string ObjectKey);

    /// <summary>
    /// Computes every object's depth in the data-movement graph the facts describe, mirroring how the GUI's
    /// object lineage graph is built: data moves from each object a flow reads to each table it writes, a
    /// module-derived read connects a VIEW to its base table (a view target is fed by its base, never by the
    /// file the flow read), and a <c>Requires</c> fact is a code dependency that moves no data. Levels are a
    /// longest-path layering with cycles broken first (a DFS drops the edges that close a cycle, so a flow that
    /// reads a view derived from its own output still levels cleanly): 0 for a source nothing produces, and one
    /// more than the deepest producer otherwise. Objects that take part in no movement get no entry.
    /// </summary>
    private static Dictionary<string, int> ComputeObjectLevels(IEnumerable<MovementFact> facts)
    {
        var byFlow = new Dictionary<(Guid RepoId, string Flow), (List<string> Reads, List<string> Writes)>();
        var moduleReads = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var writtenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            if (!string.IsNullOrEmpty(fact.Flow))
            {
                if (!byFlow.TryGetValue((fact.RepoId, fact.Flow), out var group))
                {
                    group = (new List<string>(), new List<string>());
                    byFlow[(fact.RepoId, fact.Flow)] = group;
                }

                if (fact.Relation == nameof(Core.Lineage.LineageRelation.Reads))
                {
                    group.Reads.Add(fact.ObjectKey);
                }
                else if (fact.Relation is nameof(Core.Lineage.LineageRelation.Writes)
                         or nameof(Core.Lineage.LineageRelation.Creates))
                {
                    group.Writes.Add(fact.ObjectKey);
                    writtenKeys.Add(fact.ObjectKey);
                }
            }
            else if (fact.ViaModule is { Length: > 0 } module
                     && fact.Relation == nameof(Core.Lineage.LineageRelation.Reads))
            {
                if (!moduleReads.TryGetValue(module, out var bases))
                {
                    bases = new HashSet<string>(StringComparer.Ordinal);
                    moduleReads[module] = bases;
                }

                bases.Add(fact.ObjectKey);
            }
        }

        // A view is a module a flow also writes/creates; a procedure is only required, so it stays out.
        var viewKeys = new HashSet<string>(moduleReads.Keys.Where(writtenKeys.Contains), StringComparer.Ordinal);

        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var inDegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenPairs = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void AddMovement(string source, string target)
        {
            if (source == target)
            {
                return;
            }

            if (!seenPairs.TryGetValue(source, out var targets))
            {
                targets = new HashSet<string>(StringComparer.Ordinal);
                seenPairs[source] = targets;
            }

            if (!targets.Add(target))
            {
                return;
            }

            if (!outgoing.TryGetValue(source, out var list))
            {
                list = new List<string>();
                outgoing[source] = list;
            }

            list.Add(target);
            outgoing.TryAdd(target, new List<string>());
            inDegree[source] = inDegree.TryGetValue(source, out var s) ? s : 0;
            inDegree[target] = inDegree.TryGetValue(target, out var t) ? t + 1 : 1;
        }

        foreach (var group in byFlow.Values)
        {
            foreach (var read in group.Reads)
            {
                foreach (var write in group.Writes)
                {
                    if (!viewKeys.Contains(write))
                    {
                        AddMovement(read, write);
                    }
                }
            }
        }

        foreach (var (view, bases) in moduleReads)
        {
            if (viewKeys.Contains(view))
            {
                foreach (var baseKey in bases)
                {
                    AddMovement(baseKey, view);
                }
            }
        }

        // Break cycles: an iterative DFS marks every edge that closes back onto its own stack; those edges are
        // excluded below, so the remainder is a DAG. Nodes with the fewest inbound edges root the DFS first so
        // cycles break in the natural flow direction, and the ordering is deterministic (ordinal key tiebreak).
        var backTargets = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = on the DFS stack, 2 = finished
        var roots = outgoing.Keys
            .OrderBy(id => inDegree[id])
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToList();
        foreach (var root in roots)
        {
            if (state.ContainsKey(root))
            {
                continue;
            }

            var stack = new List<(string Id, int Next)> { (root, 0) };
            state[root] = 1;
            while (stack.Count > 0)
            {
                var (id, next) = stack[^1];
                var children = outgoing[id];
                if (next < children.Count)
                {
                    stack[^1] = (id, next + 1);
                    var child = children[next];
                    if (state.TryGetValue(child, out var childState))
                    {
                        if (childState == 1)
                        {
                            if (!backTargets.TryGetValue(id, out var targets))
                            {
                                targets = new HashSet<string>(StringComparer.Ordinal);
                                backTargets[id] = targets;
                            }

                            targets.Add(child);
                            inDegree[child]--;
                        }
                    }
                    else
                    {
                        state[child] = 1;
                        stack.Add((child, 0));
                    }
                }
                else
                {
                    state[id] = 2;
                    stack.RemoveAt(stack.Count - 1);
                }
            }
        }

        // Longest-path layering over the acyclic remainder: a node sits one level below its deepest producer.
        var levels = new Dictionary<string, int>(StringComparer.Ordinal);
        var queue = new List<string>();
        foreach (var id in outgoing.Keys)
        {
            if (inDegree[id] == 0)
            {
                levels[id] = 0;
                queue.Add(id);
            }
        }

        for (var head = 0; head < queue.Count; head++)
        {
            var id = queue[head];
            var level = levels[id];
            foreach (var child in outgoing[id])
            {
                if (backTargets.TryGetValue(id, out var backs) && backs.Contains(child))
                {
                    continue;
                }

                levels[child] = Math.Max(levels.TryGetValue(child, out var existing) ? existing : 0, level + 1);
                if (--inDegree[child] == 0)
                {
                    queue.Add(child);
                }
            }
        }

        return levels;
    }

    /// <summary>Runs a keyed membership query per <see cref="KeyChunkSize"/> chunk and unions the rows, so a
    /// large key set never approaches SQL Server's per-command parameter limit. The chunks partition the keys,
    /// so per-chunk results combine without cross-chunk duplicates.</summary>
    private static async Task<List<TRow>> SelectByKeysAsync<TKey, TRow>(
        IReadOnlyList<TKey> keys, Func<TKey[], Task<List<TRow>>> query)
    {
        var rows = new List<TRow>();
        foreach (var chunk in keys.Chunk(KeyChunkSize))
        {
            rows.AddRange(await query(chunk).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>Runs a keyed bulk write per <see cref="KeyChunkSize"/> chunk and sums the affected row counts.</summary>
    private static async Task<int> ExecuteByKeysAsync<TKey>(IReadOnlyList<TKey> keys, Func<TKey[], Task<int>> write)
    {
        var affected = 0;
        foreach (var chunk in keys.Chunk(KeyChunkSize))
        {
            affected += await write(chunk).ConfigureAwait(false);
        }

        return affected;
    }

    /// <summary>Serializes an already-parsed flow document into the queryable, secret-redacted definition JSON.
    /// A serialization failure degrades to an empty definition with a warning (the definition is an enrichment,
    /// never a reason to fail the sync).</summary>
    private static string SerializeDefinition(FlowDocument document, string fullPath, List<string> warnings)
    {
        try
        {
            var json = JsonSerializer.Serialize(document, document.GetType(), DefinitionJsonOptions);
            return SecretHygiene.RedactedMessage(json);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            warnings.Add($"'{fullPath}' could not be serialized for the catalog definition ({SecretHygiene.RedactedMessage(ex)}); stored without it.");
            return string.Empty;
        }
    }

    /// <summary>Reads a flow document's text, or null when it is gone, too large, or unreadable (the caller skips
    /// it without overwriting any existing row). A removed-since-scan file is reported clearly.</summary>
    private static string? ReadYaml(string fullPath, string relativePath, List<string> warnings)
    {
        try
        {
            if (!File.Exists(fullPath))
            {
                warnings.Add($"'{relativePath}' was not found when reading its text (removed during the sync?); left unchanged.");
                return null;
            }

            var length = new FileInfo(fullPath).Length;
            if (length > MaxYamlBytes)
            {
                warnings.Add($"'{relativePath}' is {length} bytes, over the {MaxYamlBytes}-byte limit; skipped.");
                return null;
            }

            return File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"'{relativePath}' could not be read ({SecretHygiene.RedactedMessage(ex)}); left unchanged.");
            return null;
        }
    }

    /// <summary>Every <c>run.json</c> under a <c>.sqlflow/runs</c> tree in the estate.</summary>
    private static IEnumerable<string> EnumerateRunArtifacts(string root)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        return Directory.EnumerateFiles(root, "run.json", SearchOption.AllDirectories)
            .Where(f => f.Replace('\\', '/').Contains("/.sqlflow/runs/", StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
