using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;
using SqlFlow.Dispatch;
using SqlFlow.Dispatch.Protocol;

namespace SqlFlow.Catalog;

/// <summary>What to enqueue: references only (never a secret). The flow is addressed by repo + name; an optional
/// pool routes it to eligible nodes, and an optional commit SHA pins it to an exact git version the node
/// materializes. A null <see cref="CommitSha"/> is not "unpinned" but "default": enqueueing pins the run to the
/// repo's last synced commit when one is known (see <see cref="RunQueueStore.EnqueueAsync"/>), so any node in the
/// fleet can execute it. Enqueueing also snapshots the pipeline's exact YAML into the content-addressed
/// <see cref="CatalogFlowVersion"/> store and stamps the run with its hash, so the executing node loads the
/// document from the catalog instead of cloning the repo; the commit pin remains the audit trail and the
/// fallback. <see cref="Parameters"/> carries the per-run substitution parameters (the built-in backfill: full
/// load, window, file pattern), validated at the trust boundary and recorded on the run row so every backfill is
/// auditable. Bundled into one request so the optional references can never be passed in the wrong order.</summary>
/// <para><c>TriggerSource</c> records WHAT asked for the run (see <see cref="RunTriggerSources"/>), defaulting
/// to <see cref="RunTriggerSources.Manual"/> because every caller that does not say is a person or an API
/// client asking; the scheduler passes <see cref="RunTriggerSources.Schedule"/> and its schedule id. It is
/// recorded rather than inferred because nothing else on the row distinguishes the two: a schedule fire and a
/// GUI Run button take this same path and produce otherwise identical rows.</para>
public sealed record RunEnqueueRequest(
    Guid RepoId, string FlowName, string FlowKind, string? TargetPool = null, string? CommitSha = null,
    RunParameters? Parameters = null, string TriggerSource = RunTriggerSources.Manual,
    Guid? TriggerScheduleId = null);

/// <summary>What to enqueue as one multi-flow run group (a Node or Batch execution): the resolved, ordered member
/// flows (with their waves) plus the shared routing. Every member is enqueued under one <see cref="RunGroupModes"/>
/// header and gated by wave, so a dependency never runs before what it depends on.
/// <para>A node backfill carries per-member parameters through <paramref name="MemberParameters"/> (keyed by flow
/// name): the caller decides, per member, whether it takes the backfill window (the anchor and every window-honoring
/// descendant, so each layer re-reads the same historical slice) or <see cref="RunParameters.ReprocessFromSourceMin"/>
/// (a relational descendant, so the back-dated rows an upstream flow re-lands are re-pulled instead of stopping below
/// the target's high-water mark). A member absent from the map runs with default parameters, so an ordinary group (or
/// a schedule fire) passes no map and every member runs as defined.</para>
/// <para><paramref name="MaxConcurrency"/> bounds how many members may execute at once (null = unbounded, the
/// historical behavior). It is stamped onto every member run and applied by the dispatcher's group gate; because
/// waves are gated, it is effectively the width of the running wave.</para></summary>
public sealed record RunGroupEnqueueRequest(
    Guid RepoId, string Mode, string Anchor, IReadOnlyList<RunScopeMember> Members,
    string? TargetPool = null, string? CommitSha = null,
    IReadOnlyDictionary<string, RunParameters>? MemberParameters = null,
    int? MaxConcurrency = null, string TriggerSource = RunTriggerSources.Manual,
    Guid? TriggerScheduleId = null);

/// <summary>The outcome of enqueuing a single run: its new id and its placement row, which the caller hands to the
/// dispatcher so memory learns of the run the ledger already holds.</summary>
public sealed record RunEnqueueResult(Guid RunId, DispatchRun Placement);

/// <summary>The outcome of enqueuing a group: the new group id, the ids of every member run in wave order, and the
/// members' placement rows for the dispatcher.</summary>
public sealed record RunGroupEnqueueResult(Guid GroupId, IReadOnlyList<Guid> RunIds, IReadOnlyList<DispatchRun> Placements);

/// <summary>The outcome of cancelling a run group: whether the group existed, how many queued members were cancelled
/// outright, and how many running members had a cancel request stamped.</summary>
public sealed record GroupCancelResult(bool Found, int CancelledQueued, int RequestedRunning);

/// <summary>The outcome of a terminal write (fail, cancel-running): whether it applied, and which still-queued group
/// members were skipped as a consequence, so the dispatcher can drop them from memory too.</summary>
public sealed record RunTerminalResult(bool Applied, IReadOnlyList<Guid> SkippedRunIds);

/// <summary>The result of a cancel request, so the API can answer 200 / 202 / 404 / 409 precisely.</summary>
public enum CancelOutcome
{
    /// <summary>The run was queued and is now cancelled outright (it never started).</summary>
    Cancelled,

    /// <summary>The run was already running, so a cancel was requested: the owning node will abort the in-flight
    /// statement and record the run cancelled. The cancel is durable but asynchronous, hence a distinct outcome.</summary>
    CancelRequested,

    /// <summary>No run with that id exists.</summary>
    NotFound,

    /// <summary>The run exists but is already finished (terminal), so there is nothing to cancel.</summary>
    NotCancellable,
}

/// <summary>
/// The run journal: the <see cref="CatalogRun"/> table records every run from <c>queued</c> through terminal, so
/// runs survive a restart and are visible to the read API from the moment they are queued. Placement (who executes
/// what, in which order, under which gates) is decided by the control plane's in-memory dispatcher; this store only
/// journals those decisions with plain conditional updates, each fenced on the (node, attempt) pair the hand-out
/// recorded, so a write from a superseded holder affects nothing. Nothing here depends on a database feature beyond
/// conditional UPDATE, INSERT and SELECT. This is distinct from <see cref="CatalogSync"/>, which maps
/// already-finished on-disk runs into the catalog; both share the run.json projection, so there is one mapping
/// path. Stateless (pure operations over the supplied context and clock), like <see cref="CatalogProjection"/>.
/// </summary>
public static class RunQueueStore
{
    /// <summary>The largest run artifact a completion accepts; larger ones are recorded as unreadable so a runaway
    /// trace can never stall the control plane. The same bound the artifact sync and the node protocol apply.</summary>
    public const long MaxArtifactBytes = Dispatch.Protocol.NodeProtocol.MaxArtifactBytes;

    /// <summary>How many times a run may be handed out before an interrupted attempt is failed instead of requeued.
    /// Interruption here means the executing node went silent without recording an outcome (a reclaimed pod, a
    /// crash, an eviction); a run that FAILS records its failure normally and is never retried by this machinery.
    /// The cap is what stops a poison run (one that reliably kills its node, e.g. by exhausting memory) from
    /// crash-looping the fleet forever: three executions distinguishes "unlucky twice" from "the run is the cause".</summary>
    public const int MaxExecutionAttempts = 3;

    /// <summary>Enqueues a run: inserts a <c>queued</c> <see cref="CatalogRun"/> row and returns its newly minted
    /// (time-ordered) id with the placement row the dispatcher needs. The caller hands the id back to the trigger's
    /// caller, and the run is recorded under it, so <c>GET /runs/{id}</c> reflects the run from the moment it is queued.
    /// <para>
    /// Commit pinning: an explicit <see cref="RunEnqueueRequest.CommitSha"/> is honored verbatim. When it is
    /// omitted, the run is pinned to the repo's last successfully synced commit (the managed-sync source's
    /// <see cref="CatalogRepoSource.LastSyncedSha"/>), so the version that executes is exactly the version the
    /// catalog reflects, and ANY node in the fleet can materialize and run it, whether or not it holds a local
    /// synced copy. The version decision is therefore made centrally, at enqueue time, while the content itself
    /// still travels through git (workers materialize the commit once and cache it). Only when no synced commit is
    /// resolvable (the repo was synced from a local path with no remote, or has no managed source) does the run
    /// stay unpinned and fall back to the executing node's locally synced copy.
    /// </para>
    /// <para>
    /// Content snapshot: alongside the pin, the pipeline's current YAML is snapshotted into the content-addressed
    /// <see cref="CatalogFlowVersion"/> store and the run is stamped with its hash, so the executing node reads
    /// the document from the catalog and needs no git access at all for the common case. A missing pipeline row,
    /// empty stored YAML, or a flow that embeds a literal credential (whose stored YAML is the redacted form, not
    /// the committed bytes) is not snapshotted: the run keeps the git materialization path above.
    /// </para></summary>
    public static async Task<RunEnqueueResult> EnqueueAsync(
        CatalogDbContext catalog, RunEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FlowName);

        var parameters = request.Parameters ?? RunParameters.None;
        parameters.Validate();

        var runId = Guid.CreateVersion7();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, request.FlowName);
        var targetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim();

        // Resolve the commit this run executes and whether the catalog snapshot is a faithful copy of it. The
        // snapshot in CatalogFlowVersion holds the CURRENTLY SYNCED version (what CatalogPipeline.Yaml reflects),
        // so it may only serve a run that executes that same commit: an empty (default) pin, or an explicit pin
        // equal to the synced commit. A pin to any OTHER commit must run those exact bytes, which the catalog does
        // not hold, so it is left unstamped and takes the git materialization path, preserving commit-pin
        // reproducibility.
        var (commitSha, snapshotMatchesCommit) =
            await ResolveCommitForSnapshotAsync(catalog, request.RepoId, request.CommitSha, ct).ConfigureAwait(false);
        var flowVersionHash = snapshotMatchesCommit
            ? await EnsureFlowVersionAsync(catalog, pipelineId, nowUtc, ct).ConfigureAwait(false)
            : null;

        await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            catalog.Runs.Add(new CatalogRun
            {
                RunId = runId,
                PipelineId = pipelineId,
                RepoId = request.RepoId,
                FlowName = request.FlowName,
                FlowKind = string.IsNullOrWhiteSpace(request.FlowKind) ? "unknown" : request.FlowKind,
                TargetPool = targetPool,
                CommitSha = commitSha,
                FlowVersionHash = flowVersionHash,
                FullLoad = parameters.FullLoad,
                BackfillFrom = parameters.BackfillFrom,
                BackfillTo = parameters.BackfillTo,
                FilePattern = string.IsNullOrWhiteSpace(parameters.FilePattern) ? null : parameters.FilePattern.Trim(),
                SourceFilter = string.IsNullOrWhiteSpace(parameters.SourceFilter) ? null : parameters.SourceFilter.Trim(),
                AssertionsOnly = parameters.AssertionsOnly,
                ReprocessFromSourceMin = parameters.ReprocessFromSourceMin,
                TriggerSource = request.TriggerSource,
                TriggerScheduleId = request.TriggerScheduleId,
                Status = RunStatuses.Queued,
                EnqueuedUtc = nowUtc,
                // Until the run finishes there is no artifact; seed WrittenUtc with the enqueue time so the run
                // sorts naturally in the (WrittenUtc-ordered) runs list, then completion overwrites it.
                WrittenUtc = nowUtc,
                Success = false,
            });
            return Task.FromResult(runId);
        }, ct).ConfigureAwait(false);

        return new RunEnqueueResult(
            runId, new DispatchRun(runId, pipelineId, targetPool, null, 0, null, nowUtc, 0, false));
    }

    /// <summary>Enqueues a whole run group (a Node or Batch execution) atomically: inserts one
    /// <see cref="CatalogRunGroup"/> header and one <c>queued</c> <see cref="CatalogRun"/> per member, each stamped
    /// with the shared <see cref="CatalogRun.GroupId"/> and its own <see cref="CatalogRun.GroupWave"/> so the
    /// dispatcher runs them in wave order. Every member is pinned to the same resolved commit (so the whole set
    /// executes one consistent version) and carries default run parameters (backfill is single-flow only). Returns
    /// the group id, the member run ids in wave order, and their placement rows. Members are validated non-empty by
    /// the caller (an empty scope is a request error, not something to enqueue).</summary>
    public static async Task<RunGroupEnqueueResult> EnqueueGroupAsync(
        CatalogDbContext catalog, RunGroupEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mode);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Anchor);
        if (request.Members is not { Count: > 0 })
        {
            throw new ArgumentException("A run group must have at least one member flow.", nameof(request));
        }

        var groupId = Guid.CreateVersion7();
        var targetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim();
        // A non-positive bound would leave every member unclaimable forever, so it collapses to unbounded here as
        // a last line of defence; the YAML loaders already reject one with a warning.
        var maxConcurrency = request.MaxConcurrency is { } max && max >= 1 ? max : (int?)null;

        // Per-member parameters for a node backfill (the caller routed each member to the window or to
        // reprocess-from-source-min); a member absent from the map runs with defaults. Each is validated here, at the
        // enqueue trust boundary, exactly as a single run's parameters are. An ordinary node run or a schedule fire
        // passes no map, so every member resolves to default parameters.
        RunParameters MemberParameters(RunScopeMember member)
            => request.MemberParameters?.GetValueOrDefault(member.FlowName) ?? RunParameters.None;
        if (request.MemberParameters is not null)
        {
            foreach (var member in request.Members)
            {
                MemberParameters(member).Validate();
            }
        }

        // Resolve the shared commit and whether the catalog snapshot faithfully copies it (see the single-run
        // enqueue). Only when it does is each member's YAML staged into the content-addressed store before the run
        // transaction, so the serializable transaction only inserts run rows. Distinct pipelines are staged once;
        // members that share content (a group fanning out one version) reuse the row the first EnsureFlowVersionAsync
        // committed. A pin to a non-synced commit leaves every member unstamped, on the git materialization path.
        var (commitSha, snapshotMatchesCommit) =
            await ResolveCommitForSnapshotAsync(catalog, request.RepoId, request.CommitSha, ct).ConfigureAwait(false);
        var flowVersionByPipeline = new Dictionary<Guid, string?>();
        if (snapshotMatchesCommit)
        {
            foreach (var member in request.Members)
            {
                var pipelineId = CatalogIdentity.Pipeline(request.RepoId, member.FlowName);
                if (!flowVersionByPipeline.ContainsKey(pipelineId))
                {
                    flowVersionByPipeline[pipelineId] =
                        await EnsureFlowVersionAsync(catalog, pipelineId, nowUtc, ct).ConfigureAwait(false);
                }
            }
        }

        var placements = new List<DispatchRun>(request.Members.Count);
        var runIds = await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            placements.Clear();
            catalog.RunGroups.Add(new CatalogRunGroup
            {
                GroupId = groupId,
                RepoId = request.RepoId,
                Mode = request.Mode,
                Anchor = request.Anchor,
                MemberCount = request.Members.Count,
                CommitSha = commitSha,
                EnqueuedUtc = nowUtc,
            });

            var ids = new List<Guid>(request.Members.Count);
            foreach (var member in request.Members)
            {
                var runId = Guid.CreateVersion7();
                ids.Add(runId);
                var pipelineId = CatalogIdentity.Pipeline(request.RepoId, member.FlowName);
                var memberParameters = MemberParameters(member);
                // A negative (uncomputed) wave collapses to 0 so an un-analyzed set runs as one parallel wave.
                var wave = member.Wave < 0 ? 0 : member.Wave;
                catalog.Runs.Add(new CatalogRun
                {
                    RunId = runId,
                    PipelineId = pipelineId,
                    RepoId = request.RepoId,
                    FlowName = member.FlowName,
                    FlowKind = string.IsNullOrWhiteSpace(member.FlowKind) ? "unknown" : member.FlowKind,
                    TargetPool = targetPool,
                    CommitSha = commitSha,
                    FlowVersionHash = flowVersionByPipeline.GetValueOrDefault(pipelineId),
                    GroupId = groupId,
                    GroupWave = wave,
                    GroupMaxConcurrency = maxConcurrency,
                    FullLoad = memberParameters.FullLoad,
                    BackfillFrom = memberParameters.BackfillFrom,
                    BackfillTo = memberParameters.BackfillTo,
                    FilePattern = string.IsNullOrWhiteSpace(memberParameters.FilePattern) ? null : memberParameters.FilePattern.Trim(),
                    SourceFilter = string.IsNullOrWhiteSpace(memberParameters.SourceFilter) ? null : memberParameters.SourceFilter.Trim(),
                    AssertionsOnly = memberParameters.AssertionsOnly,
                    ReprocessFromSourceMin = memberParameters.ReprocessFromSourceMin,
                    TriggerSource = request.TriggerSource,
                    TriggerScheduleId = request.TriggerScheduleId,
                    Status = RunStatuses.Queued,
                    EnqueuedUtc = nowUtc,
                    WrittenUtc = nowUtc,
                    Success = false,
                });
                placements.Add(new DispatchRun(runId, pipelineId, targetPool, groupId, wave, maxConcurrency, nowUtc, 0, false));
            }

            return Task.FromResult(ids);
        }, ct).ConfigureAwait(false);

        return new RunGroupEnqueueResult(groupId, runIds, placements);
    }

    /// <summary>
    /// The commit an unpinned enqueue defaults to: the repo's managed-sync source's last successfully synced SHA.
    /// The repo row and its source row are joined by name, which is the managed-sync invariant (the sync records
    /// the repo under the source's name; see RepoSyncService). The pin is only usable when the repo has a remote
    /// URL for workers to materialize from, so a repo synced from a bare local path never produces a pin a worker
    /// could not honor.
    /// </summary>
    private static async Task<string?> ResolveSyncedShaAsync(
        CatalogDbContext catalog, Guid repoId, CancellationToken ct)
    {
        var sha = await (
                from repo in catalog.Repos.AsNoTracking()
                join source in catalog.RepoSources.AsNoTracking() on repo.Name equals source.Name
                where repo.Id == repoId
                      && repo.RemoteUrl != null && repo.RemoteUrl != ""
                      && source.LastSyncedSha != null && source.LastSyncedSha != ""
                select source.LastSyncedSha)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(sha) ? null : sha.Trim();
    }

    /// <summary>
    /// Resolves the commit a run executes from its request, and whether the catalog's YAML snapshot faithfully
    /// copies that commit. An explicit <paramref name="requestedSha"/> is honored verbatim; an empty one defaults to
    /// the repo's last synced commit (see <see cref="ResolveSyncedShaAsync"/>). The snapshot in
    /// <see cref="CatalogFlowVersion"/> only ever holds the currently synced version, so it matches the run when the
    /// run executes that synced commit: an empty pin, or an explicit pin equal to the synced SHA. A pin to any other
    /// commit does not match, and its run must materialize those exact bytes from git rather than run the snapshot.
    /// </summary>
    private static async Task<(string? CommitSha, bool SnapshotMatchesCommit)> ResolveCommitForSnapshotAsync(
        CatalogDbContext catalog, Guid repoId, string? requestedSha, CancellationToken ct)
    {
        var syncedSha = await ResolveSyncedShaAsync(catalog, repoId, ct).ConfigureAwait(false);
        var explicitSha = string.IsNullOrWhiteSpace(requestedSha) ? null : requestedSha.Trim();
        var commitSha = explicitSha ?? syncedSha;
        var snapshotMatchesCommit = explicitSha is null
            || string.Equals(explicitSha, syncedSha, StringComparison.OrdinalIgnoreCase);
        return (commitSha, snapshotMatchesCommit);
    }

    /// <summary>
    /// Snapshots the pipeline's current YAML into the content-addressed <see cref="CatalogFlowVersion"/> store and
    /// returns its hash, staging the version row when this content has not been seen before (same hash = same bytes,
    /// so a hundred runs at one commit share one row). Returns null, leaving the run on the git materialization
    /// path, when no trustworthy snapshot exists: the pipeline row is missing (the flow left the catalog between
    /// resolution and enqueue), its stored YAML is empty, or the YAML carries an embedded literal credential. In
    /// that last case the stored text is the redacted form, not the committed bytes, so executing it would silently
    /// run a different document; such a flow (already loudly warned by the sync) keeps cloning git until it is fixed
    /// to use ${...} references.
    /// <para>
    /// Deliberately staged in its own short save, OUTSIDE the caller's serializable run transaction: the version
    /// store is append-only immutable content, so committing it independently is correct, and it keeps the
    /// content-addressed table off the serializable transaction's lock footprint (two concurrent enqueues of the
    /// same brand-new version would otherwise take conflicting range locks and deadlock). A concurrent staging of
    /// the identical version races benignly: the loser catches the duplicate-key and treats the existing row, whose
    /// bytes are identical, as the staged one. An orphaned version row (its run transaction later fails) is
    /// harmless and reused by the next enqueue of that content.
    /// </para>
    /// </summary>
    private static async Task<string?> EnsureFlowVersionAsync(
        CatalogDbContext catalog, Guid pipelineId, DateTime nowUtc, CancellationToken ct)
    {
        var pipeline = await catalog.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.ContentHash, p.Yaml })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null
            || string.IsNullOrWhiteSpace(pipeline.ContentHash)
            || string.IsNullOrWhiteSpace(pipeline.Yaml)
            || SecretHygiene.LooksLikeEmbeddedSecret(pipeline.Yaml))
        {
            return null;
        }

        var exists = await catalog.FlowVersions.AsNoTracking()
            .AnyAsync(v => v.ContentHash == pipeline.ContentHash, ct).ConfigureAwait(false);
        if (!exists)
        {
            catalog.FlowVersions.Add(new CatalogFlowVersion
            {
                ContentHash = pipeline.ContentHash,
                Yaml = pipeline.Yaml,
                FirstSeenUtc = nowUtc,
            });
            try
            {
                await catalog.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // A concurrent enqueue staged this identical (content-addressed) version first; the row exists with
                // the same bytes, which is exactly the goal. Detach the failed add so the context stays clean for
                // the run transaction that follows.
                var entry = catalog.Entry(catalog.FlowVersions.Local.First(v => v.ContentHash == pipeline.ContentHash));
                entry.State = EntityState.Detached;
            }
        }

        return pipeline.ContentHash;
    }

    // ------------------------------------------------------------------------------------- dispatcher journal ----

    /// <summary>Every queued run's placement row and every running run with its recorded holder, for rebuilding the
    /// dispatcher's memory at activation.</summary>
    public static async Task<(IReadOnlyList<DispatchRun> Queued, IReadOnlyList<RunningRunRecord> Running)> LoadDispatchStateAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var queued = await PlacementQuery(catalog.Runs.AsNoTracking().Where(r => r.Status == RunStatuses.Queued))
            .ToListAsync(ct).ConfigureAwait(false);
        var running = await RunningQuery(catalog.Runs.AsNoTracking().Where(r => r.Status == RunStatuses.Running))
            .ToListAsync(ct).ConfigureAwait(false);
        return (queued.Select(ToDispatchRun).ToList(), running.Select(ToRunningRecord).OfType<RunningRunRecord>().ToList());
    }

    /// <summary>The ids of every queued run and the holder of every running run, the cheap read reconcile diffs
    /// against the dispatcher's memory.</summary>
    public static async Task<(IReadOnlyList<Guid> QueuedIds, IReadOnlyList<ActiveRunRef> Running)> ListActiveAsync(
        CatalogDbContext catalog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var queued = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Queued)
            .Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false);
        var running = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running)
            .Select(r => new { r.RunId, r.ClaimedByNode, r.Attempt, Cancel = r.CancelRequestedUtc != null })
            .ToListAsync(ct).ConfigureAwait(false);
        return (queued, running.Select(r => new ActiveRunRef(r.RunId, r.ClaimedByNode, r.Attempt, r.Cancel)).ToList());
    }

    /// <summary>The placement rows of the given runs that are still queued.</summary>
    public static async Task<IReadOnlyList<DispatchRun>> LoadQueuedAsync(
        CatalogDbContext catalog, IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count == 0)
        {
            return [];
        }

        var ids = runIds.ToList();
        var rows = await PlacementQuery(catalog.Runs.AsNoTracking()
                .Where(r => r.Status == RunStatuses.Queued && ids.Contains(r.RunId)))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToDispatchRun).ToList();
    }

    /// <summary>The placement rows and holders of the given runs that are still running under a recorded node.</summary>
    public static async Task<IReadOnlyList<RunningRunRecord>> LoadRunningAsync(
        CatalogDbContext catalog, IReadOnlyCollection<Guid> runIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(runIds);
        if (runIds.Count == 0)
        {
            return [];
        }

        var ids = runIds.ToList();
        var rows = await RunningQuery(catalog.Runs.AsNoTracking()
                .Where(r => r.Status == RunStatuses.Running && ids.Contains(r.RunId)))
            .ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(ToRunningRecord).OfType<RunningRunRecord>().ToList();
    }

    /// <summary>Journals a hand-out: the run goes from <c>queued</c> to <c>running</c> under <paramref name="node"/>,
    /// with <see cref="CatalogRun.Attempt"/> advanced from <paramref name="expectedAttempt"/> to one more, and returns
    /// the execution spec the node needs (see <see cref="LoadSpecAsync"/>). The spec is read first, so a read that
    /// fails leaves the run queued with no attempt consumed; the write is then conditional on the row still being
    /// queued at exactly that attempt, so a run cancelled or requeued directly in the meantime is never handed out on
    /// stale knowledge. Returns null when the row is gone or the write did not apply.</summary>
    public static async Task<RunSpec?> MarkHandedOutAsync(
        CatalogDbContext catalog, Guid runId, int expectedAttempt, string node, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        var spec = await LoadSpecAsync(catalog, runId, ct).ConfigureAwait(false);
        if (spec is null)
        {
            return null;
        }

        var attempt = expectedAttempt + 1;
        var written = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Queued && r.Attempt == expectedAttempt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Running)
                .SetProperty(r => r.ClaimedByNode, node)
                .SetProperty(r => r.StartUtc, nowUtc)
                .SetProperty(r => r.Attempt, attempt), ct)
            .ConfigureAwait(false);
        return written > 0 ? spec : null;
    }

    /// <summary>Everything a node needs to execute the run, in one joined projection: the run row, its repo and
    /// pipeline, and the repo's managed source (whose credential REFERENCE a SHA-pinned run resolves on the node).
    /// Left joins keep the missing cases distinguishable, so the node's failure message stays precise when a repo or
    /// pipeline has left the catalog since the enqueue. Null when the run row itself is gone.</summary>
    public static async Task<RunSpec?> LoadSpecAsync(CatalogDbContext catalog, Guid runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var row = await (
                from r in catalog.Runs.AsNoTracking()
                where r.RunId == runId
                join repoRow in catalog.Repos.AsNoTracking() on r.RepoId equals (Guid?)repoRow.Id into repoRows
                from repo in repoRows.DefaultIfEmpty()
                join pipelineRow in catalog.Pipelines.AsNoTracking() on r.PipelineId equals pipelineRow.Id into pipelineRows
                from pipeline in pipelineRows.DefaultIfEmpty()
                join sourceRow in catalog.RepoSources.AsNoTracking() on repo.Name equals sourceRow.Name into sourceRows
                from source in sourceRows.DefaultIfEmpty()
                select new
                {
                    r.RepoId,
                    r.PipelineId,
                    r.FlowName,
                    r.CommitSha,
                    r.FlowVersionHash,
                    r.FullLoad,
                    r.BackfillFrom,
                    r.BackfillTo,
                    r.FilePattern,
                    r.SourceFilter,
                    r.AssertionsOnly,
                    r.ReprocessFromSourceMin,
                    RepoName = repo != null ? repo.Name : null,
                    RepoRemoteUrl = repo != null ? repo.RemoteUrl : null,
                    RepoRootPath = repo != null ? repo.RootPath : null,
                    PipelineRelativePath = pipeline != null ? pipeline.RelativePath : null,
                    CredentialReference = source != null ? source.CredentialReference : null,
                    CredentialUsername = source != null ? source.CredentialUsername : null,
                })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        return new RunSpec(
            row.RepoId, row.PipelineId, row.FlowName, row.RepoName, row.RepoRemoteUrl, row.RepoRootPath,
            row.PipelineRelativePath, row.CommitSha, row.FlowVersionHash,
            new RunParameters
            {
                FullLoad = row.FullLoad,
                BackfillFrom = row.BackfillFrom,
                BackfillTo = row.BackfillTo,
                FilePattern = row.FilePattern,
                SourceFilter = row.SourceFilter,
                AssertionsOnly = row.AssertionsOnly,
                ReprocessFromSourceMin = row.ReprocessFromSourceMin,
            },
            row.CredentialReference, row.CredentialUsername);
    }

    /// <summary>The YAML text of the snapshotted flow version with the given content hash, served to a node staging
    /// a handed-out run; null when no such version is staged.</summary>
    public static Task<string?> LoadFlowVersionAsync(CatalogDbContext catalog, string contentHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        return catalog.FlowVersions.AsNoTracking()
            .Where(v => v.ContentHash == contentHash)
            .Select(v => v.Yaml)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Records a run's outcome as its node reported it, under the fence: completion from the artifact
    /// (which decides success or failure), a failure with a reason, or an honored operator cancel.</summary>
    public static async Task<RunOutcomeRecord> RecordOutcomeAsync(
        CatalogDbContext catalog, Guid runId, string node, int attempt, RunOutcomeKind outcome, string? failure,
        string? artifactJson, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        switch (outcome)
        {
            case RunOutcomeKind.Completed:
                return await CompleteFromArtifactAsync(catalog, runId, artifactJson, nowUtc, node, attempt, ct).ConfigureAwait(false);
            case RunOutcomeKind.Failed:
                {
                    var reason = string.IsNullOrWhiteSpace(failure) ? "the run failed without a recorded reason." : failure;
                    var result = await FailAsync(catalog, runId, reason, nowUtc, node, attempt, ct).ConfigureAwait(false);
                    return new RunOutcomeRecord(
                        result.Applied ? RunOutcomeStatus.Recorded : RunOutcomeStatus.StaleClaim, result.SkippedRunIds);
                }

            default:
                {
                    var result = await CancelRunningAsync(catalog, runId, nowUtc, node, attempt, ct).ConfigureAwait(false);
                    return new RunOutcomeRecord(
                        result.Applied ? RunOutcomeStatus.Recorded : RunOutcomeStatus.StaleClaim, result.SkippedRunIds);
                }
        }
    }

    /// <summary>Records a claimed run's outcome from its <c>run.json</c> artifact text: copies the result fields onto
    /// the existing row, flips it to the terminal <c>succeeded</c>/<c>failed</c> state, and inserts the drill-down
    /// detail. If the artifact is missing, oversized or corrupt the run is still moved to <c>failed</c> (with the
    /// reason) so it never lingers in <c>running</c>.
    /// <para>The fence: a node passes the <paramref name="claimedByNode"/> / <paramref name="claimAttempt"/> its
    /// hand-out carried, and the write applies only while the row still carries exactly that claim (still
    /// <c>running</c>, same node, same attempt). A row that was requeued after its lease lapsed (and possibly handed
    /// out again for a later attempt) no longer matches, so a node presumed dead that finishes late writes nothing:
    /// the current execution is authoritative, and this one's result is dropped as
    /// <see cref="RunOutcomeStatus.StaleClaim"/>. Passing no fence (the artifact-sync path, which records finished
    /// CLI runs that were never handed out) applies unconditionally.</para></summary>
    public static Task<RunOutcomeRecord> CompleteFromArtifactAsync(
        CatalogDbContext catalog, Guid runId, string? artifactJson, DateTime nowUtc,
        string? claimedByNode = null, int? claimAttempt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            // Load WITH tracking explicitly: this is a read-modify-write, and the control plane's context defaults
            // to NoTracking (it is mostly a read model), under which a loaded entity's mutations would be silently
            // dropped by SaveChanges. AsTracking() overrides that default for this update.
            var existing = await catalog.Runs.AsTracking()
                .FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);

            // The fence check runs inside the same serializable transaction as the write, so "still mine" and the
            // completion commit atomically: a requeue between them would deadlock/retry, never interleave.
            if (claimedByNode is not null && (existing is null
                || existing.Status != RunStatuses.Running
                || existing.ClaimedByNode != claimedByNode
                || existing.Attempt != claimAttempt))
            {
                return new RunOutcomeRecord(RunOutcomeStatus.StaleClaim, []);
            }

            if (existing is null)
            {
                // Nothing to record against and nothing to fail: an unfenced completion for an unknown run is only
                // possible from a caller that did not enqueue, which the artifact sync handles on its own path.
                return new RunOutcomeRecord(RunOutcomeStatus.ArtifactUnreadable, []);
            }

            var repoId = existing.RepoId ?? Guid.Empty;
            string? readError = null;
            if (string.IsNullOrWhiteSpace(artifactJson))
            {
                readError = "no run artifact was reported";
            }
            else if (artifactJson.Length > MaxArtifactBytes)
            {
                readError = $"the run artifact is {artifactJson.Length} characters, over the {MaxArtifactBytes}-byte limit";
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(artifactJson);
                    var projected = CatalogProjection.RunFromJson(document.RootElement, repoId);
                    if (projected is null)
                    {
                        readError = "the run artifact is missing required fields";
                    }
                    else
                    {
                        ApplyCompletion(existing, projected, nowUtc);

                        // The node streamed this run's statements and canonical events into the catalog live as it
                        // executed: each is an immutable, append-only row the trace stream already delivered under a
                        // stable id. Do NOT delete and re-project them - that would re-issue every row under a fresh
                        // id, and the live tail (which forwards rows past the client's id cursor) would re-stream the
                        // whole trace. Instead append only the tail the live feed did not write: nothing in the
                        // normal case (the feed captured everything), or the gap after the point a best-effort feed
                        // broke, taken from the authoritative run.json. Atomic with the rest of the completion in
                        // this serializable transaction.
                        var maxEventOrdinal = await catalog.RunEvents.Where(e => e.RunId == runId)
                            .Select(e => (int?)e.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        var maxStatementOrdinal = await catalog.RunStatements.Where(s => s.RunId == runId)
                            .Select(s => (int?)s.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        CatalogSync.AddRunDetail(
                            catalog, document.RootElement, runId, repoId, maxEventOrdinal, maxStatementOrdinal,
                            existing.PipelineId);
                        // A failed group member strands its dependents: skip them in the same transaction so the
                        // completion and its consequences commit together (a no-op for a standalone or succeeded run).
                        var skipped = projected.Success
                            ? []
                            : await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
                        return new RunOutcomeRecord(RunOutcomeStatus.Recorded, skipped);
                    }
                }
                catch (JsonException ex)
                {
                    readError = SecretHygiene.RedactedMessage(ex);
                }
            }

            // The artifact could not be read: still drive the run to a terminal state so it is never stuck. Under a
            // fence the row is proven above to still be this caller's running claim, so the write is safe here too.
            existing.Status = RunStatuses.Failed;
            existing.Success = false;
            existing.EndUtc = nowUtc;
            existing.WrittenUtc = nowUtc;
            existing.Error = $"the run executed but its result could not be recorded: {readError}.";
            // An unrecordable run is still a failed group member: strand its dependents like any other failure.
            var stranded = await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
            return new RunOutcomeRecord(RunOutcomeStatus.ArtifactUnreadable, stranded);
        }, ct);
    }

    /// <summary>Drives a run to <c>failed</c> with a reason when execution could not even produce an artifact (the
    /// flow file was missing, failed to load, or the worker threw): a no-op if the run is already terminal, so a
    /// late failure never overwrites a recorded success. A node failing its OWN run passes the
    /// <paramref name="claimedByNode"/> / <paramref name="claimAttempt"/> fence its hand-out carried; the write then
    /// applies only while the row still carries exactly that claim, so a late failure from a superseded holder can
    /// never clobber a run another execution now owns. Unfenced callers apply on the lifecycle guard alone.</summary>
    public static async Task<RunTerminalResult> FailAsync(
        CatalogDbContext catalog, Guid runId, string error, DateTime nowUtc,
        string? claimedByNode = null, int? claimAttempt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var failed = await catalog.Runs
            .Where(r => r.RunId == runId && (r.Status == RunStatuses.Queued || r.Status == RunStatuses.Running))
            .Where(r => claimedByNode == null
                || (r.Status == RunStatuses.Running && r.ClaimedByNode == claimedByNode && r.Attempt == claimAttempt))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Failed)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, error)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (failed == 0)
        {
            return new RunTerminalResult(false, []);
        }

        return new RunTerminalResult(true, await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false));
    }

    /// <summary>Cancels a run, honoring its lifecycle. A still-queued run is cancelled outright (it never ran). A
    /// run already <c>running</c> cannot be cancelled out from under its node here; instead a durable cancel
    /// request is stamped (<see cref="CatalogRun.CancelRequestedUtc"/>) for the dispatcher to relay to the owning
    /// node, which aborts the in-flight statement and records the run cancelled (see
    /// <see cref="CancelRunningAsync"/>). Each step is a single atomic conditional update, so a run that is handed
    /// out between the queued check and the running check is caught by the second step rather than lost. Requesting
    /// a cancel on a run that already has one pending is idempotent (still <see cref="CancelOutcome.CancelRequested"/>).</summary>
    public static async Task<CancelOutcome> CancelAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (cancelled > 0)
        {
            // Cancelling a queued group member is a non-success terminal too: its dependents in the group can no
            // longer run, so skip them (a no-op for a standalone run).
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
            return CancelOutcome.Cancelled;
        }

        // Still-running: record the request for the owning node. Stamp CancelRequestedUtc only when it is not yet
        // set, so the request reflects when the operator first asked (a repeated click does not keep moving it).
        var requested = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running && r.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (requested > 0)
        {
            return CancelOutcome.CancelRequested;
        }

        // No queued or freshly-running row updated: either it is running with a request already pending (idempotent
        // success), it is already terminal (nothing to cancel), or it does not exist.
        var state = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => (string?)r.Status)
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return state switch
        {
            null => CancelOutcome.NotFound,
            RunStatuses.Running => CancelOutcome.CancelRequested,
            _ => CancelOutcome.NotCancellable,
        };
    }

    /// <summary>Cancels a whole run group as a unit: every still-<c>queued</c> member is cancelled outright and every
    /// member already <c>running</c> gets a durable cancel request stamped for its owning node to honor, exactly as
    /// the single-run <see cref="CancelAsync"/> does. No per-member dependent-skipping is needed here: the entire
    /// group is being cancelled, so there is nothing left to strand. Returns whether the group existed and how many
    /// members were affected, so the endpoint can answer 404 for an unknown group.</summary>
    public static async Task<GroupCancelResult> CancelGroupAsync(
        CatalogDbContext catalog, Guid groupId, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var found = await catalog.RunGroups.AsNoTracking()
            .AnyAsync(g => g.GroupId == groupId, ct).ConfigureAwait(false);
        if (!found)
        {
            return new GroupCancelResult(false, 0, 0);
        }

        var cancelledQueued = await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        var requestedRunning = await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Running && r.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CancelRequestedUtc, nowUtc), ct)
            .ConfigureAwait(false);

        return new GroupCancelResult(true, cancelledQueued, requestedRunning);
    }

    /// <summary>Records a running run as <c>cancelled</c> after its owning node has aborted the in-flight statement.
    /// Conditional on the run still being <c>running</c>, so a run that finished on its own (succeeded/failed) in the
    /// same instant is never overwritten by a late cancel; with the optional <paramref name="claimedByNode"/> /
    /// <paramref name="claimAttempt"/> fence, also conditional on the row still carrying the caller's claim, so a
    /// superseded holder's late cancel never lands on a requeued or re-handed-out execution.</summary>
    public static async Task<RunTerminalResult> CancelRunningAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc,
        string? claimedByNode = null, int? claimAttempt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var cancelled = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running)
            .Where(r => claimedByNode == null || (r.ClaimedByNode == claimedByNode && r.Attempt == claimAttempt))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, "The run was cancelled by an operator while executing.")
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);

        if (cancelled == 0)
        {
            return new RunTerminalResult(false, []);
        }

        return new RunTerminalResult(true, await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false));
    }

    private static string InterruptedTerminalError(string node, int attempt) =>
        $"Run interrupted: its node '{node}' stopped without recording an outcome, and this was execution "
        + $"attempt {attempt} of {MaxExecutionAttempts}, so it is not requeued again (a run that repeatedly dies "
        + "mid-flight is treated as the cause). Re-trigger the flow to run it once more.";

    /// <summary>Puts an interrupted run (its node's lease lapsed without an outcome) back to <c>queued</c> for another
    /// node to execute, with the holder cleared and the consumed attempt kept, so the next hand-out advances the
    /// attempt and fences off this attempt's late writes. Losing a node is recoverable, not terminal: flows are
    /// idempotent (keyed merges, content-addressed landing), so a half-finished attempt re-runs clean. Fenced on the
    /// expired lease's node and attempt, and on the attempt budget, so no interleaving can requeue a run past its
    /// budget or a run its node completed in the same instant.</summary>
    public static Task<InterruptedRunRecord> RequeueInterruptedAsync(
        CatalogDbContext catalog, Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            var requeued = await catalog.Runs
                .Where(r => r.RunId == runId && r.Status == RunStatuses.Running
                    && r.ClaimedByNode == node && r.Attempt == attempt
                    && r.Attempt < MaxExecutionAttempts && r.CancelRequestedUtc == null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatuses.Queued)
                    .SetProperty(r => r.ClaimedByNode, (string?)null)
                    .SetProperty(r => r.StartUtc, (DateTime?)null)
                    .SetProperty(r => r.WrittenUtc, nowUtc), ct)
                .ConfigureAwait(false);
            if (requeued == 0)
            {
                return new InterruptedRunRecord(false, []);
            }

            // The interrupted attempt's live trace is discarded with it: the next execution starts from scratch
            // and streams its own trace from ordinal 1, and the run's trace must be that execution's, not the two
            // interleaved (the completion's tail append keys on the highest ordinal present, so leftover rows from a
            // dead attempt would also hide the successor's rows beneath that ordinal). Atomic with the requeue, so
            // no window exists in which the row is queued but still carries a stale trace.
            await catalog.RunStatements.Where(s => s.RunId == runId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await catalog.RunEvents.Where(e => e.RunId == runId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return new InterruptedRunRecord(true, []);
        }, ct);
    }

    /// <summary>Fails an interrupted run that has consumed its whole attempt budget: a run that repeatedly dies with
    /// its node is the cause, not the victim (the poison-run bound). Its dependents are skipped. Fenced.</summary>
    public static async Task<InterruptedRunRecord> FailInterruptedAsync(
        CatalogDbContext catalog, Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        var failed = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running
                && r.ClaimedByNode == node && r.Attempt == attempt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Failed)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, InterruptedTerminalError(node, attempt))
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (failed == 0)
        {
            return new InterruptedRunRecord(false, []);
        }

        return new InterruptedRunRecord(true, await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false));
    }

    /// <summary>Records an interrupted run cancelled: the operator already asked for its death before its node went
    /// silent, and the cancel intent is authoritative (a requeue would resurrect work the operator explicitly
    /// killed). Its dependents are skipped. Fenced.</summary>
    public static async Task<InterruptedRunRecord> CancelInterruptedAsync(
        CatalogDbContext catalog, Guid runId, string node, int attempt, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        var cancelled = await catalog.Runs
            .Where(r => r.RunId == runId && r.Status == RunStatuses.Running
                && r.ClaimedByNode == node && r.Attempt == attempt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Cancelled)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, "The run was cancelled by an operator; its node stopped before recording the cancellation.")
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (cancelled == 0)
        {
            return new InterruptedRunRecord(false, []);
        }

        return new InterruptedRunRecord(true, await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false));
    }

    /// <summary>When a group member reaches a non-success terminal state (failed / cancelled), marks every member
    /// that transitively depends on it and is still <c>queued</c> as <c>skipped</c>: a broken upstream is never fed
    /// downstream, while independent branches of the group keep running. A no-op for a standalone run (no group), a
    /// succeeded run, or a run whose dependents have all already started. Idempotent (guarded on <c>queued</c>), so
    /// two failures in the same group each skip their own cone without fighting. The reachable set is computed over
    /// the group's own members only, so a dependent outside this run group is never touched. Returns the ids of the
    /// members skipped, so the dispatcher drops them from its memory.</summary>
    private static async Task<IReadOnlyList<Guid>> SkipGroupDescendantsAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct)
    {
        var run = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => new { r.GroupId, r.PipelineId, r.RepoId, r.FlowName, r.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null || run.GroupId is not { } groupId || run.RepoId is not { } repoId
            || run.Status == RunStatuses.Succeeded)
        {
            return [];
        }

        var members = await catalog.Runs.AsNoTracking()
            .Where(r => r.GroupId == groupId)
            .Select(r => new { r.RunId, r.PipelineId, r.Status })
            .ToListAsync(ct).ConfigureAwait(false);
        var memberIds = members.Select(m => m.PipelineId).ToHashSet();

        var edges = await catalog.FlowDependencies.AsNoTracking()
            .Where(d => d.RepoId == repoId)
            .Select(d => new { d.FromPipelineId, d.ToPipelineId })
            .ToListAsync(ct).ConfigureAwait(false);

        var outgoing = new Dictionary<Guid, List<Guid>>();
        foreach (var edge in edges)
        {
            // Only edges wholly inside this group matter: a dependency on a flow that is not part of the run cannot
            // be satisfied or skipped by it.
            if (!memberIds.Contains(edge.FromPipelineId) || !memberIds.Contains(edge.ToPipelineId))
            {
                continue;
            }

            if (!outgoing.TryGetValue(edge.FromPipelineId, out var to))
            {
                to = [];
                outgoing[edge.FromPipelineId] = to;
            }

            to.Add(edge.ToPipelineId);
        }

        var dependents = new HashSet<Guid>();
        var queue = new Queue<Guid>();
        queue.Enqueue(run.PipelineId);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!outgoing.TryGetValue(current, out var neighbors))
            {
                continue;
            }

            foreach (var next in neighbors)
            {
                if (dependents.Add(next))
                {
                    queue.Enqueue(next);
                }
            }
        }

        if (dependents.Count == 0)
        {
            return [];
        }

        // The rows to skip are decided here, then written by id, so the caller learns exactly which members left the
        // queue (the guard on 'queued' stays in the write, so a member that started meanwhile is untouched).
        var candidateIds = members
            .Where(m => m.Status == RunStatuses.Queued && dependents.Contains(m.PipelineId))
            .Select(m => m.RunId)
            .ToList();
        if (candidateIds.Count == 0)
        {
            return [];
        }

        var reason = $"skipped: an upstream dependency ('{run.FlowName}') did not succeed.";
        var skipped = await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Queued && candidateIds.Contains(r.RunId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Skipped)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
        if (skipped == candidateIds.Count)
        {
            return candidateIds;
        }

        // Some candidates started between the read and the write: report only those actually skipped.
        return await catalog.Runs.AsNoTracking()
            .Where(r => candidateIds.Contains(r.RunId) && r.Status == RunStatuses.Skipped)
            .Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false);
    }

    private static void ApplyCompletion(CatalogRun target, CatalogRun projected, DateTime nowUtc)
    {
        // Identity fields are the same whether the row was enqueued or is being inserted fresh (RunFromJson derives
        // PipelineId from repo + flow name, exactly as enqueue did); the queue-only fields (EnqueuedUtc,
        // ClaimedByNode, the run parameters FullLoad/BackfillFrom/BackfillTo/FilePattern) and the hand-out's
        // StartUtc are preserved by simply not assigning them here.
        target.PipelineId = projected.PipelineId;
        target.RepoId = projected.RepoId;
        target.FlowName = projected.FlowName;
        target.FlowKind = projected.FlowKind;
        target.Success = projected.Success;
        target.Status = projected.Success ? RunStatuses.Succeeded : RunStatuses.Failed;
        target.SchemaVersion = projected.SchemaVersion;
        target.WrittenUtc = projected.WrittenUtc == default ? nowUtc : projected.WrittenUtc;
        target.StartUtc = projected.StartUtc ?? target.StartUtc;
        target.EndUtc = projected.EndUtc ?? nowUtc;
        target.DurationSeconds = projected.DurationSeconds;
        target.RowsLoaded = projected.RowsLoaded;
        target.RowsInserted = projected.RowsInserted;
        target.RowsUpdated = projected.RowsUpdated;
        target.RowsDeleted = projected.RowsDeleted;
        target.Error = projected.Error;
        target.Host = projected.Host ?? target.Host;
        target.IncrementalMode = projected.IncrementalMode;
        target.IncrementalFilter = projected.IncrementalFilter;
        target.IncrementalWatermark = projected.IncrementalWatermark;
        target.IncrementalWatermarkSource = projected.IncrementalWatermarkSource;
        target.DataSetConvention = projected.DataSetConvention;
        // Fill only, never overwrite: an enqueued run already carries what asked for it (a schedule, a person),
        // and the projection's view of an artifact is always "cli". This assigns solely on the path where the
        // completion inserts a row that was never enqueued, which IS a node-local execution.
        target.TriggerSource ??= projected.TriggerSource;
    }

    private sealed record PlacementRow(
        Guid RunId, Guid PipelineId, string? TargetPool, Guid? GroupId, int GroupWave, int? GroupMaxConcurrency,
        DateTime? EnqueuedUtc, DateTime WrittenUtc, int Attempt, DateTime? CancelRequestedUtc, string? ClaimedByNode);

    private static IQueryable<PlacementRow> PlacementQuery(IQueryable<CatalogRun> runs)
        => runs.Select(r => new PlacementRow(
            r.RunId, r.PipelineId, r.TargetPool, r.GroupId, r.GroupWave, r.GroupMaxConcurrency,
            r.EnqueuedUtc, r.WrittenUtc, r.Attempt, r.CancelRequestedUtc, r.ClaimedByNode));

    private static IQueryable<PlacementRow> RunningQuery(IQueryable<CatalogRun> runs) => PlacementQuery(runs);

    private static DispatchRun ToDispatchRun(PlacementRow row) => new(
        row.RunId, row.PipelineId, row.TargetPool, row.GroupId, row.GroupWave, row.GroupMaxConcurrency,
        row.EnqueuedUtc ?? row.WrittenUtc, row.Attempt, row.CancelRequestedUtc != null);

    private static RunningRunRecord? ToRunningRecord(PlacementRow row)
        => string.IsNullOrWhiteSpace(row.ClaimedByNode) ? null : new RunningRunRecord(ToDispatchRun(row), row.ClaimedByNode);
}
