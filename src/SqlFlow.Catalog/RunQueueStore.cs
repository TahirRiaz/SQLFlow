using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

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
/// historical behavior). It is stamped onto every member run and applied by the queue's claim gate; because waves are
/// gated, it is effectively the width of the running wave.</para></summary>
public sealed record RunGroupEnqueueRequest(
    Guid RepoId, string Mode, string Anchor, IReadOnlyList<RunScopeMember> Members,
    string? TargetPool = null, string? CommitSha = null,
    IReadOnlyDictionary<string, RunParameters>? MemberParameters = null,
    int? MaxConcurrency = null, string TriggerSource = RunTriggerSources.Manual,
    Guid? TriggerScheduleId = null);

/// <summary>The outcome of enqueuing a group: the new group id and the ids of every member run, in wave order.</summary>
public sealed record RunGroupEnqueueResult(Guid GroupId, IReadOnlyList<Guid> RunIds);

/// <summary>The outcome of cancelling a run group: whether the group existed, how many queued members were cancelled
/// outright, and how many running members had a cancel request stamped (the latter drives a worker nudge).</summary>
public sealed record GroupCancelResult(bool Found, int CancelledQueued, int RequestedRunning);

/// <summary>A successful claim: the run to execute and the claim's <see cref="CatalogRun.Attempt"/> value, which is
/// the fencing token the claiming node presents on every outcome write (complete / fail / cancel). A write whose
/// token no longer matches the row is a stale write from a superseded execution and is dropped.</summary>
public readonly record struct ClaimedRun(Guid RunId, int Attempt);

/// <summary>How a completion write-back ended, so the worker can log the difference between a recorded outcome, a
/// run driven <c>failed</c> because its artifact could not be read, and a write dropped by the claim fence.</summary>
public enum RunCompletionOutcome
{
    /// <summary>The outcome was recorded from a valid artifact.</summary>
    Recorded,

    /// <summary>The artifact was missing or corrupt; the run was driven to <c>failed</c> so it never lingers.</summary>
    ArtifactUnreadable,

    /// <summary>The row no longer carries the caller's claim (it was requeued by crash recovery, re-claimed by
    /// another execution, or driven terminal by someone else), so nothing was written: this caller is a superseded
    /// execution and the row's current owner is authoritative.</summary>
    StaleClaim,
}

/// <summary>The orphan reaper's outcome for one sweep: how many interrupted runs went back to the queue for another
/// execution, how many had exhausted their attempts and were failed, and how many were recorded cancelled because
/// an operator's cancel was already pending when the node died.</summary>
public readonly record struct OrphanReapResult(int Requeued, int Failed, int Cancelled)
{
    /// <summary>Whether the sweep changed anything at all.</summary>
    public bool Any => Requeued > 0 || Failed > 0 || Cancelled > 0;
}

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
/// The durable run queue: the <see cref="CatalogRun"/> table IS the queue, so queued and running work survives a
/// host restart, is visible to the read API, and (with the atomic claim below) can be drained by more than one
/// worker without ever double-running a flow. A control-plane trigger or a schedule enqueues a run; a worker claims
/// the oldest queued run, executes it through the shared engine, then records the outcome from its on-disk
/// artifact. This is distinct from <see cref="CatalogSync"/>, which maps already-finished on-disk runs into the
/// catalog; both share the run.json projection and the retriable-transaction helper, so there is one mapping path.
/// Stateless (pure operations over the supplied context and clock), like <see cref="CatalogProjection"/>.
/// </summary>
public static class RunQueueStore
{
    // The reliable single-statement claim: atomically pick the oldest queued run and flip it to running, returning
    // its id. UPDLOCK takes the update lock up front (no lock-upgrade race), READPAST skips rows another worker has
    // already locked (so concurrent workers each get a different run instead of blocking), ROWLOCK keeps the lock
    // granular. Because it is one statement it is atomic on its own - no surrounding transaction is needed - which
    // is exactly what makes it safe for many workers to call at once.
    //
    // The leading SET pins READ COMMITTED for this batch: READPAST is only valid under READ COMMITTED / REPEATABLE
    // READ, and a pooled connection can carry a leftover SERIALIZABLE level from a prior transaction (SQL Server
    // does not reliably reset the session isolation level on connection reuse), which would otherwise make the hint
    // illegal. READ COMMITTED is also the engine default, so this only ever restores it.
    // {POOL_PREDICATE} is replaced with a parameterized pool filter (the pool NAMES are bound as parameters, never
    // interpolated, so the IN list is injection-safe).
    //
    // The group-gating clause enforces wave order within a run group without any external coordinator: a member of a
    // group is claimable only once every same-group member in a LOWER wave is terminal (no sibling with a smaller
    // GroupWave is still queued or running). A standalone run (GroupId IS NULL) short-circuits the clause and is
    // claimable exactly as before. Because a blocked member simply is not selected (rather than locked), READPAST
    // still lets a worker move straight to the next eligible run - a not-yet-ready wave never stalls the queue.
    //
    // The pipeline-gating clause serializes executions of the SAME flow: a run is claimable only while no other run
    // of its pipeline is executing, so a double-trigger (or a schedule firing while the previous run is still going)
    // queues behind the running one instead of racing it. This is load-bearing for ingestion: each flow stages
    // through one canonical work table ([raw].[<schema>_<table>_<flowId>]), which two overlapping executions of the
    // flow would clobber. Like the group gate, a blocked duplicate is simply not selected, so the worker moves on to
    // the next eligible run; node-restart recovery (RecoverStuckRunningAsync) requeues orphaned running rows, so a
    // crashed run cannot wedge its pipeline.
    //
    // That clause alone is only advisory across a fleet: it is a read under READ COMMITTED, holding no lock on the
    // sibling rows, so two nodes claiming two different queued runs of one pipeline can both see no running sibling
    // and both claim (write skew). The unique filtered index UX_Run_RunningPipeline
    // (catalog.Run(PipelineId) WHERE Status = 'running') is what makes the gate atomic: the loser's write fails with
    // a duplicate key, which ClaimNextAsync answers by trying the next-eligible run. The clause stays because it is
    // the cheap, ordering-preserving filter that keeps the conflict rare; the index is the guarantee.
    //
    // The trailing [Status] = @queued on the UPDATE is the same defence for the row itself: the subquery picks a
    // queued run under UPDLOCK, and this keeps the write conditional on that state so no path can flip a run that
    // has meanwhile been claimed, cancelled, or requeued.
    //
    // The group-concurrency clause bounds how WIDE a fire runs: a member carrying a GroupMaxConcurrency (stamped from
    // the firing schedule at enqueue) is claimable only while fewer than that many of its siblings are running. Since
    // the wave gate above already means only one wave is eligible at a time, this is the width of the running wave.
    // A null bound (every standalone run, and any group whose schedule set none) short-circuits to the historical
    // unbounded behavior. Like the other gates it filters rather than locks, so a node that finds the wave saturated
    // moves on to other eligible work instead of blocking.
    //
    // The bound is exact per node, because a node's drain loop claims strictly one run at a time (RunWorker.DrainAsync
    // awaits its concurrency slot, then claims), so its own count is never stale. Across a multi-node fleet two nodes
    // can pass the check on the same free slot and overshoot by at most (claiming nodes - 1): making that exact would
    // need a range lock over the group on every claim, serializing the fleet's hot path to bound a soft resource
    // limit. Treat it as "about this many", which is what protecting an upstream connection budget actually needs.
    // The claim increments [Attempt] and returns it alongside the id: the incremented value is the claiming node's
    // fencing token, which every outcome write is conditional on (see the fenced overloads below), and doubles as
    // the execution counter that bounds crash-recovery requeues.
    /// <summary>How many times one <see cref="ClaimNextAsync"/> call re-runs its claim after losing the
    /// UX_Run_RunningPipeline race. Three covers a realistic fleet (the loser only retries when another node
    /// claimed the very pipeline it picked, and the retry then picks a different run) without letting one poll
    /// hammer a contended queue: exhausting the budget simply reports nothing claimable this tick.</summary>
    private const int ClaimRaceAttempts = 3;

    // SQL Server's two duplicate-key errors: 2601 from a unique index, 2627 from a unique constraint. The claim
    // statement writes one column set on [catalog].[Run] and never touches a key column, so the only uniqueness it
    // can violate is the filtered UX_Run_RunningPipeline index - a lost race for the pipeline, not a fault. Matching
    // on the numbers alone keeps the check independent of the server's message language.
    private const int DuplicateKeyInIndex = 2601;
    private const int DuplicateKeyInConstraint = 2627;

    private const string ClaimSqlTemplate = """
        SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
        UPDATE [catalog].[Run]
        SET [Status] = @running, [ClaimedByNode] = @node, [StartUtc] = @now, [Attempt] = [Attempt] + 1
        OUTPUT inserted.[RunId], inserted.[Attempt]
        WHERE [RunId] = (
            SELECT TOP (1) r.[RunId] FROM [catalog].[Run] AS r WITH (UPDLOCK, READPAST, ROWLOCK)
            WHERE r.[Status] = @queued AND {POOL_PREDICATE}
              AND (r.[GroupId] IS NULL OR NOT EXISTS (
                  SELECT 1 FROM [catalog].[Run] AS s
                  WHERE s.[GroupId] = r.[GroupId] AND s.[GroupWave] < r.[GroupWave]
                    AND s.[Status] IN (@queued, @running)))
              AND (r.[GroupMaxConcurrency] IS NULL OR (
                  SELECT COUNT(*) FROM [catalog].[Run] AS w
                  WHERE w.[GroupId] = r.[GroupId] AND w.[Status] = @running) < r.[GroupMaxConcurrency])
              AND NOT EXISTS (
                  SELECT 1 FROM [catalog].[Run] AS p
                  WHERE p.[PipelineId] = r.[PipelineId] AND p.[Status] = @running)
            ORDER BY r.[EnqueuedUtc], r.[RunId])
          AND [Status] = @queued;
        """;

    /// <summary>Enqueues a run: inserts a <c>queued</c> <see cref="CatalogRun"/> row and returns its newly minted
    /// (time-ordered) id. The caller hands that id back to the trigger's caller, and the run is recorded under it,
    /// so <c>GET /runs/{id}</c> reflects the run from the moment it is queued.
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
    public static async Task<Guid> EnqueueAsync(
        CatalogDbContext catalog, RunEnqueueRequest request, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FlowName);

        var parameters = request.Parameters ?? RunParameters.None;
        parameters.Validate();

        var runId = Guid.CreateVersion7();
        var pipelineId = CatalogIdentity.Pipeline(request.RepoId, request.FlowName);

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

        return await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
            catalog.Runs.Add(new CatalogRun
            {
                RunId = runId,
                PipelineId = pipelineId,
                RepoId = request.RepoId,
                FlowName = request.FlowName,
                FlowKind = string.IsNullOrWhiteSpace(request.FlowKind) ? "unknown" : request.FlowKind,
                TargetPool = string.IsNullOrWhiteSpace(request.TargetPool) ? null : request.TargetPool.Trim(),
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
        }, ct);
    }

    /// <summary>Enqueues a whole run group (a Node or Batch execution) atomically: inserts one
    /// <see cref="CatalogRunGroup"/> header and one <c>queued</c> <see cref="CatalogRun"/> per member, each stamped
    /// with the shared <see cref="CatalogRun.GroupId"/> and its own <see cref="CatalogRun.GroupWave"/> so the claim
    /// runs them in wave order. Every member is pinned to the same resolved commit (so the whole set executes one
    /// consistent version) and carries default run parameters (backfill is single-flow only). Returns the group id
    /// and the member run ids in wave order. Members are validated non-empty by the caller (an empty scope is a
    /// request error, not something to enqueue).</summary>
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

        return await CatalogTransaction.InSerializableAsync(catalog, () =>
        {
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

            var runIds = new List<Guid>(request.Members.Count);
            foreach (var member in request.Members)
            {
                var runId = Guid.CreateVersion7();
                runIds.Add(runId);
                var pipelineId = CatalogIdentity.Pipeline(request.RepoId, member.FlowName);
                var memberParameters = MemberParameters(member);
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
                    // A negative (uncomputed) wave collapses to 0 so an un-analyzed set runs as one parallel wave.
                    GroupWave = member.Wave < 0 ? 0 : member.Wave,
                    // A non-positive bound would leave every member unclaimable forever, so it collapses to
                    // unbounded here as a last line of defence; the YAML loaders already reject one with a warning.
                    GroupMaxConcurrency = request.MaxConcurrency is { } max && max >= 1 ? max : null,
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
            }

            return Task.FromResult(new RunGroupEnqueueResult(groupId, runIds));
        }, ct);
    }

    /// <summary>
    /// The commit an unpinned enqueue defaults to: the repo's managed-sync source's last successfully synced SHA.
    /// The repo row and its source row are joined by name, which is the managed-sync invariant (the sync records
    /// the repo under the source's name; see RepoSyncService). The pin is only usable when the repo has a remote
    /// URL for workers to materialize from, so a repo synced from a bare local path never produces a pin a worker
    /// could not honor. Runs inside the enqueue transaction, so the pin and the queued row are one consistent
    /// snapshot.
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

    /// <summary>Atomically claims the oldest queued run this node is eligible for, flipping it to <c>running</c> and
    /// returning its id and claim attempt (the fencing token the node must present on every outcome write), or null
    /// when there is none. Eligibility: an untargeted run (no pool) is claimable by any node; a pooled run only by a
    /// node that serves that pool (<paramref name="pools"/>); a run whose pipeline already has a running execution
    /// waits its turn (same-flow runs never overlap, protecting the flow's canonical staging table). Safe to call
    /// concurrently from many workers: each claim takes a different run (or none). The one-execution-per-pipeline
    /// rule is guaranteed by the database (the filtered unique index UX_Run_RunningPipeline), not merely checked
    /// here, so a claim that loses that race to another node retries against the next-eligible run instead of
    /// producing a second concurrent execution of one flow.</summary>
    public static async Task<ClaimedRun?> ClaimNextAsync(
        CatalogDbContext catalog, string node, IReadOnlyList<string> pools, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);
        ArgumentNullException.ThrowIfNull(pools);

        // A worker with no pools claims only untargeted runs; a pooled worker also claims runs routed to one of its
        // pools. The pool names are bound as parameters (only the @poolN placeholders are interpolated), so the IN
        // list cannot be an injection vector.
        var poolPredicate = "r.[TargetPool] IS NULL";
        if (pools.Count > 0)
        {
            var placeholders = string.Join(", ", pools.Select((_, i) => $"@pool{i}"));
            poolPredicate = $"(r.[TargetPool] IS NULL OR r.[TargetPool] IN ({placeholders}))";
        }

        var sql = ClaimSqlTemplate.Replace("{POOL_PREDICATE}", poolPredicate, StringComparison.Ordinal);

        // Losing the race for a pipeline is an ordinary outcome, not an error: the run stays queued and becomes
        // claimable the moment the winner finishes. Each retry re-runs the whole claim, so it evaluates the gates
        // afresh and takes the next-eligible run (usually a different pipeline). The attempt budget bounds the work
        // a heavily contended queue can cause in one poll; exhausting it answers "nothing claimable right now", and
        // the drain loop's next nudge or poll picks the work up.
        for (var attempt = 1; attempt <= ClaimRaceAttempts; attempt++)
        {
            try
            {
                return await ClaimOnceAsync(catalog, sql, node, pools, nowUtc, ct).ConfigureAwait(false);
            }
            catch (SqlException ex) when (IsRunningPipelineConflict(ex))
            {
                // Another node claimed this pipeline between this claim's gate check and its write.
            }
        }

        return null;
    }

    /// <summary>The claim's single database round trip: runs <see cref="ClaimSqlTemplate"/> (already pool-expanded)
    /// and materializes the claimed run, or null when nothing was eligible. Separated from
    /// <see cref="ClaimNextAsync"/> so the lost-race retry re-enters through a fresh execution strategy, which is
    /// what the strategy contract requires of a retried operation.</summary>
    private static async Task<ClaimedRun?> ClaimOnceAsync(
        CatalogDbContext catalog, string sql, string node, IReadOnlyList<string> pools, DateTime nowUtc,
        CancellationToken ct)
    {
        var strategy = catalog.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            var connection = catalog.Database.GetDbConnection();
            await catalog.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                // Enlist in the context's current transaction if one is open (there is none on the standalone claim
                // path, but this keeps the command correct if a caller ever wraps it).
                if (catalog.Database.CurrentTransaction is { } tx)
                {
                    command.Transaction = tx.GetDbTransaction();
                }

                AddParameter(command, "@running", RunStatuses.Running);
                AddParameter(command, "@queued", RunStatuses.Queued);
                AddParameter(command, "@node", node);
                AddParameter(command, "@now", nowUtc);
                for (var i = 0; i < pools.Count; i++)
                {
                    AddParameter(command, $"@pool{i}", pools[i]);
                }

                await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    return (ClaimedRun?)null;
                }

                return new ClaimedRun(reader.GetGuid(0), reader.GetInt32(1));
            }
            finally
            {
                await catalog.Database.CloseConnectionAsync().ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    }

    /// <summary>True when a failed claim is the database refusing a second running run for one pipeline (see
    /// <see cref="DuplicateKeyInIndex"/>). Every other SqlException is a real failure and propagates to the worker's
    /// poll-error handling. A batch can carry several errors, so the whole collection is inspected.</summary>
    private static bool IsRunningPipelineConflict(SqlException ex)
    {
        foreach (SqlError error in ex.Errors)
        {
            if (error.Number is DuplicateKeyInIndex or DuplicateKeyInConstraint)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Records a claimed run's outcome from its on-disk <c>run.json</c>: copies the result fields onto the
    /// existing row, flips it to the terminal <c>succeeded</c>/<c>failed</c> state, and inserts the drill-down
    /// detail. If the artifact is missing or corrupt the run is still moved to <c>failed</c> (with the reason) so it
    /// never lingers in <c>running</c>.
    /// <para>The claim fence: a worker passes the <paramref name="claimedByNode"/> / <paramref name="claimAttempt"/>
    /// its claim returned, and the write applies only while the row still carries exactly that claim (still
    /// <c>running</c>, same node, same attempt). A row that was requeued by crash recovery (and possibly re-claimed
    /// for a later attempt) no longer matches, so a zombie worker (presumed dead, actually alive) that finishes
    /// late writes nothing: the current execution is authoritative, and this one's result is dropped as
    /// <see cref="RunCompletionOutcome.StaleClaim"/>. Passing no fence (the artifact-sync path, which records
    /// finished CLI runs that were never claimed) applies unconditionally as before.</para></summary>
    public static Task<RunCompletionOutcome> CompleteFromArtifactAsync(
        CatalogDbContext catalog, Guid runId, Guid repoId, string runJsonPath, DateTime nowUtc,
        string? claimedByNode = null, int? claimAttempt = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(runJsonPath);

        return CatalogTransaction.InSerializableAsync(catalog, async () =>
        {
            // Load WITH tracking explicitly: this is a read-modify-write, and the control plane's context defaults
            // to NoTracking (it is mostly a read model), under which a loaded entity's mutations would be silently
            // dropped by SaveChanges. AsTracking() overrides that default for this update.
            var existing = await catalog.Runs.AsTracking()
                .FirstOrDefaultAsync(r => r.RunId == runId, ct).ConfigureAwait(false);

            // The fence check runs inside the same serializable transaction as the write, so "still mine" and the
            // completion commit atomically: a reaper requeue between them would deadlock/retry, never interleave.
            if (claimedByNode is not null && (existing is null
                || existing.Status != RunStatuses.Running
                || existing.ClaimedByNode != claimedByNode
                || existing.Attempt != claimAttempt))
            {
                return RunCompletionOutcome.StaleClaim;
            }

            string? readError = null;
            try
            {
                var length = new FileInfo(runJsonPath).Length;
                if (length > CatalogSync.MaxRunJsonBytes)
                {
                    readError = $"the run artifact is {length} bytes, over the {CatalogSync.MaxRunJsonBytes}-byte limit";
                }
                else
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(runJsonPath, ct).ConfigureAwait(false));
                    var projected = CatalogProjection.RunFromJson(document.RootElement, repoId);
                    if (projected is null)
                    {
                        readError = "the run artifact is missing required fields";
                    }
                    else
                    {
                        var target = existing ?? new CatalogRun { RunId = runId };
                        ApplyCompletion(target, projected, nowUtc);
                        if (existing is null)
                        {
                            catalog.Runs.Add(target);
                        }

                        // The node streamed this run's statements and canonical events into the catalog live as it
                        // executed: each is an immutable, append-only row the trace stream already delivered under a
                        // stable id. Do NOT delete and re-project them - that would re-issue every row under a fresh
                        // id, and the live tail (which forwards rows past the client's id cursor) would re-stream the
                        // whole trace. Instead append only the tail the live feed did not write: nothing in the
                        // normal case (the feed captured everything), or the gap after the point a best-effort feed
                        // broke, taken from the authoritative run.json. CLI and full-sync runs have no live rows, so
                        // the whole detail is inserted. Atomic with the rest of the completion in this serializable
                        // transaction.
                        var maxEventOrdinal = await catalog.RunEvents.Where(e => e.RunId == runId)
                            .Select(e => (int?)e.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        var maxStatementOrdinal = await catalog.RunStatements.Where(s => s.RunId == runId)
                            .Select(s => (int?)s.Ordinal).MaxAsync(ct).ConfigureAwait(false) ?? 0;
                        CatalogSync.AddRunDetail(
                            catalog, document.RootElement, runId, repoId, maxEventOrdinal, maxStatementOrdinal,
                            target.PipelineId);
                        // A failed group member strands its dependents: skip them in the same transaction so the
                        // completion and its consequences commit together (a no-op for a standalone or succeeded run).
                        if (!projected.Success)
                        {
                            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
                        }

                        return RunCompletionOutcome.Recorded;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                readError = SecretHygiene.RedactedMessage(ex);
            }

            // The artifact could not be read: still drive the run to a terminal state so it is never stuck. Under a
            // fence the row is proven above to still be this caller's running claim, so the write is safe here too.
            if (existing is not null)
            {
                existing.Status = RunStatuses.Failed;
                existing.Success = false;
                existing.EndUtc = nowUtc;
                existing.WrittenUtc = nowUtc;
                existing.Error = $"the run executed but its result could not be recorded: {readError}.";
                // An unrecordable run is still a failed group member: strand its dependents like any other failure.
                await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
            }

            return RunCompletionOutcome.ArtifactUnreadable;
        }, ct);
    }

    /// <summary>Drives a run to <c>failed</c> with a reason when execution could not even produce an artifact (the
    /// flow file was missing, failed to load, or the worker threw): a no-op if the run is already terminal, so a
    /// late failure never overwrites a recorded success. A worker failing its OWN claimed run passes the
    /// <paramref name="claimedByNode"/> / <paramref name="claimAttempt"/> fence its claim returned; the write then
    /// applies only while the row still carries exactly that claim, so a zombie's late failure can never clobber a
    /// run that crash recovery has requeued (or another execution now owns). Unfenced callers (the control plane
    /// failing a queued run) apply on the lifecycle guard alone, as before.</summary>
    public static async Task FailAsync(
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

        if (failed > 0)
        {
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Cancels a run, honoring its lifecycle. A still-queued run is cancelled outright (it never ran). A
    /// run already <c>running</c> cannot be cancelled out from under its worker here; instead a durable cancel
    /// request is stamped (<see cref="CatalogRun.CancelRequestedUtc"/>) for the owning node to observe, abort the
    /// in-flight statement, and record the run cancelled (see <see cref="ListCancelRequestedAsync"/> /
    /// <see cref="CancelRunningAsync"/>). Each step is a single atomic conditional update, so a run that is claimed
    /// between the queued check and the running check is caught by the second step rather than lost. Requesting a
    /// cancel on a run that already has one pending is idempotent (still <see cref="CancelOutcome.CancelRequested"/>).</summary>
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
    /// members were affected, so the endpoint can answer 404 for an unknown group and the dispatcher can nudge the
    /// worker when a running member must observe its request.</summary>
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

    /// <summary>The ids of runs this node is executing that an operator has asked to cancel: <c>running</c>, claimed
    /// by <paramref name="node"/>, with a pending <see cref="CatalogRun.CancelRequestedUtc"/>. The worker polls this
    /// to trip the matching run's cancellation token. Scoped to the node so a worker only ever cancels its own
    /// in-flight work.</summary>
    public static Task<List<Guid>> ListCancelRequestedAsync(
        CatalogDbContext catalog, string node, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        return catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node && r.CancelRequestedUtc != null)
            .Select(r => r.RunId)
            .ToListAsync(ct);
    }

    /// <summary>Records a running run as <c>cancelled</c> after its owning node has aborted the in-flight statement.
    /// Conditional on the run still being <c>running</c>, so a run that finished on its own (succeeded/failed) in the
    /// same instant is never overwritten by a late cancel; with the optional <paramref name="claimedByNode"/> /
    /// <paramref name="claimAttempt"/> fence, also conditional on the row still carrying the caller's claim, so a
    /// zombie's late cancel never lands on a requeued or re-claimed execution.</summary>
    public static async Task<int> CancelRunningAsync(
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

        if (cancelled > 0)
        {
            await SkipGroupDescendantsAsync(catalog, runId, nowUtc, ct).ConfigureAwait(false);
        }

        return cancelled;
    }

    /// <summary>How many times a run may be claimed for execution before an interrupted attempt is failed instead of
    /// requeued. Interruption here means the executing process died without recording an outcome (a reclaimed pod, a
    /// crash, an eviction); a run that FAILS records its failure normally and is never retried by this machinery.
    /// The cap is what stops a poison run (one that reliably kills its node, e.g. by exhausting memory) from
    /// crash-looping the fleet forever: three executions distinguishes "unlucky twice" from "the run is the cause".</summary>
    public const int MaxExecutionAttempts = 3;

    private static string InterruptedTerminalError(string node, int attempt) =>
        $"Run interrupted: its claiming node '{node}' stopped without recording an outcome, and this was execution "
        + $"attempt {attempt} of {MaxExecutionAttempts}, so it is not requeued again (a run that repeatedly dies "
        + "mid-flight is treated as the cause). Re-trigger the flow to run it once more.";

    /// <summary>Recovers runs left <c>running</c> by this node: on worker startup they are orphans from a previous
    /// incarnation that stopped mid-run. Each goes back to <c>queued</c> to be executed again, unless it has already
    /// consumed <see cref="MaxExecutionAttempts"/> claims, in which case it is failed (with its dependents skipped)
    /// exactly as the liveness reaper would: a run that keeps dying with its node is the cause, not the victim.
    /// Returns the number requeued.</summary>
    public static async Task<int> RecoverStuckRunningAsync(
        CatalogDbContext catalog, string node, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentException.ThrowIfNullOrWhiteSpace(node);

        // An interrupted run the operator had already asked to cancel is recorded cancelled, never requeued: the
        // cancel intent is authoritative, and a requeue would resurrect work the operator explicitly killed.
        var pendingCancels = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node && r.CancelRequestedUtc != null)
            .Select(r => r.RunId)
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var runId in pendingCancels)
        {
            await CancelRunningAsync(catalog, runId, nowUtc, ct: ct).ConfigureAwait(false);
        }

        // Fail the attempt-exhausted ones next (each needs its group descendants skipped, so per-run), then bulk
        // requeue the rest. Both writes are guarded on the row still being this node's running claim, so a
        // concurrent liveness reaper doing the same recovery is idempotent, not doubled. The bulk requeue repeats
        // the under-cap predicate rather than trusting the loops above to have consumed every excluded row, so no
        // interleaving can ever requeue a run past its attempt budget or against a pending cancel.
        var exhausted = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node && r.Attempt >= MaxExecutionAttempts)
            .Select(r => new { r.RunId, r.Attempt })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var run in exhausted)
        {
            var failed = await catalog.Runs
                .Where(r => r.RunId == run.RunId && r.Status == RunStatuses.Running && r.ClaimedByNode == node)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatuses.Failed)
                    .SetProperty(r => r.Success, false)
                    .SetProperty(r => r.Error, InterruptedTerminalError(node, run.Attempt))
                    .SetProperty(r => r.EndUtc, nowUtc)
                    .SetProperty(r => r.WrittenUtc, nowUtc), ct)
                .ConfigureAwait(false);
            if (failed > 0)
            {
                await SkipGroupDescendantsAsync(catalog, run.RunId, nowUtc, ct).ConfigureAwait(false);
            }
        }

        return await catalog.Runs
            .Where(r => r.Status == RunStatuses.Running && r.ClaimedByNode == node
                && r.Attempt < MaxExecutionAttempts && r.CancelRequestedUtc == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Queued)
                .SetProperty(r => r.ClaimedByNode, (string?)null)
                .SetProperty(r => r.StartUtc, (DateTime?)null), ct)
            .ConfigureAwait(false);
    }

    /// <summary>Recovers runs left <c>running</c> by a node that is no longer alive. A run is an orphan when its
    /// <c>ClaimedByNode</c> has no fleet heartbeat at or after <paramref name="staleBefore"/> (its registry row is
    /// absent or its last-seen is older than the cutoff), or when no claimant is recorded at all: in every case the
    /// process that was executing it is gone and no outcome will ever be recorded, so the run would otherwise sit
    /// <c>running</c> forever and block every future run of its pipeline (the claim's pipeline gate). Unlike
    /// <see cref="RecoverStuckRunningAsync"/>, which recovers a node's OWN restart orphans by name, this reclaims any
    /// node's orphans by liveness, so a crashed pod that never returns under the same name is still cleared.
    /// <para>Losing a worker is recoverable, not terminal, so the default disposition is REQUEUE: the run goes back
    /// to <c>queued</c> (claim cleared, attempt count already consumed by the claim) and the next eligible worker
    /// executes it again; flows are idempotent (keyed merges, content-addressed landing), so a half-finished attempt
    /// re-runs clean. Two cases do not requeue: a run whose operator cancel was already pending is recorded
    /// <c>cancelled</c> (the cancel intent is authoritative), and a run that has consumed
    /// <see cref="MaxExecutionAttempts"/> claims is failed, because a run that repeatedly dies with its node is the
    /// cause rather than the victim (the poison-run bound).</para>
    /// <para>Every write is a conditional update guarded on the row still carrying the exact orphaned claim (still
    /// <c>running</c>, same node, same attempt), so a run its real node completes in the same instant is never
    /// overwritten and concurrent reapers on multiple control-plane replicas are idempotent; a failed or cancelled
    /// group member skips its still-queued dependents, exactly as an operator cancel does. The liveness signal is
    /// only safe to act on because a node heartbeats on a cadence independent of its draining
    /// (<c>RunWorker.HeartbeatLoopAsync</c>), so a busy node is never mistaken for a dead one; the caller sets
    /// <paramref name="staleBefore"/> comfortably older than that cadence.</para></summary>
    public static async Task<OrphanReapResult> ReapOrphanedRunningAsync(
        CatalogDbContext catalog, DateTime staleBefore, DateTime nowUtc, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        // Candidate orphans: running rows with no live node backing them, resolved server-side as one indexed
        // anti-join against the fleet registry. A healthy fleet returns nothing, so the steady-state cost is a
        // single cheap read.
        var orphans = await catalog.Runs.AsNoTracking()
            .Where(r => r.Status == RunStatuses.Running)
            .Where(r => r.ClaimedByNode == null
                || !catalog.Nodes.Any(n => n.Name == r.ClaimedByNode && n.LastSeenUtc >= staleBefore))
            .Select(r => new { r.RunId, r.ClaimedByNode, r.Attempt, r.CancelRequestedUtc })
            .ToListAsync(ct).ConfigureAwait(false);
        if (orphans.Count == 0)
        {
            return default;
        }

        var result = default(OrphanReapResult);
        foreach (var orphan in orphans)
        {
            var node = orphan.ClaimedByNode ?? "(unclaimed)";

            // The operator already asked for this run's death before its node died: record the cancel, never a
            // resurrection. The claim fence (node + attempt) keeps this from touching a row the real node is
            // completing, or that another reaper replica has already moved on.
            if (orphan.CancelRequestedUtc is not null)
            {
                var cancelled = await catalog.Runs
                    .Where(r => r.RunId == orphan.RunId && r.Status == RunStatuses.Running
                        && r.ClaimedByNode == orphan.ClaimedByNode && r.Attempt == orphan.Attempt)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatuses.Cancelled)
                        .SetProperty(r => r.Success, false)
                        .SetProperty(r => r.Error,
                            "The run was cancelled by an operator; its node died before recording the cancellation.")
                        .SetProperty(r => r.EndUtc, nowUtc)
                        .SetProperty(r => r.WrittenUtc, nowUtc), ct)
                    .ConfigureAwait(false);
                if (cancelled > 0)
                {
                    await SkipGroupDescendantsAsync(catalog, orphan.RunId, nowUtc, ct).ConfigureAwait(false);
                    result = result with { Cancelled = result.Cancelled + 1 };
                }

                continue;
            }

            if (orphan.Attempt < MaxExecutionAttempts)
            {
                // Requeue: back to the queue with the claim cleared, for any eligible worker to claim (which
                // increments Attempt again, fencing off this attempt's zombie writes). The under-cap predicate is
                // repeated in the WHERE so no interleaving can requeue a run past its budget.
                var requeued = await catalog.Runs
                    .Where(r => r.RunId == orphan.RunId && r.Status == RunStatuses.Running
                        && r.ClaimedByNode == orphan.ClaimedByNode && r.Attempt == orphan.Attempt
                        && r.Attempt < MaxExecutionAttempts)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(r => r.Status, RunStatuses.Queued)
                        .SetProperty(r => r.ClaimedByNode, (string?)null)
                        .SetProperty(r => r.StartUtc, (DateTime?)null), ct)
                    .ConfigureAwait(false);
                if (requeued > 0)
                {
                    result = result with { Requeued = result.Requeued + 1 };
                }

                continue;
            }

            var failed = await catalog.Runs
                .Where(r => r.RunId == orphan.RunId && r.Status == RunStatuses.Running
                    && r.ClaimedByNode == orphan.ClaimedByNode && r.Attempt == orphan.Attempt)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, RunStatuses.Failed)
                    .SetProperty(r => r.Success, false)
                    .SetProperty(r => r.Error, InterruptedTerminalError(node, orphan.Attempt))
                    .SetProperty(r => r.EndUtc, nowUtc)
                    .SetProperty(r => r.WrittenUtc, nowUtc), ct)
                .ConfigureAwait(false);
            if (failed > 0)
            {
                await SkipGroupDescendantsAsync(catalog, orphan.RunId, nowUtc, ct).ConfigureAwait(false);
                result = result with { Failed = result.Failed + 1 };
            }
        }

        return result;
    }

    /// <summary>When a group member reaches a non-success terminal state (failed / cancelled), marks every member
    /// that transitively depends on it and is still <c>queued</c> as <c>skipped</c>: a broken upstream is never fed
    /// downstream, while independent branches of the group keep running. A no-op for a standalone run (no group), a
    /// succeeded run, or a run whose dependents have all already started. Idempotent (guarded on <c>queued</c>), so
    /// two failures in the same group each skip their own cone without fighting. The reachable set is computed over
    /// the group's own members only, so a dependent outside this run group is never touched.</summary>
    private static async Task SkipGroupDescendantsAsync(
        CatalogDbContext catalog, Guid runId, DateTime nowUtc, CancellationToken ct)
    {
        var run = await catalog.Runs.AsNoTracking()
            .Where(r => r.RunId == runId)
            .Select(r => new { r.GroupId, r.PipelineId, r.RepoId, r.FlowName, r.Status })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (run is null || run.GroupId is not { } groupId || run.RepoId is not { } repoId
            || run.Status == RunStatuses.Succeeded)
        {
            return;
        }

        var memberIds = (await catalog.Runs.AsNoTracking()
                .Where(r => r.GroupId == groupId)
                .Select(r => r.PipelineId)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet();

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
                to = new List<Guid>();
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
            return;
        }

        var dependentIds = dependents.ToList();
        var reason = $"skipped: an upstream dependency ('{run.FlowName}') did not succeed.";
        await catalog.Runs
            .Where(r => r.GroupId == groupId && r.Status == RunStatuses.Queued && dependentIds.Contains(r.PipelineId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, RunStatuses.Skipped)
                .SetProperty(r => r.Success, false)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.EndUtc, nowUtc)
                .SetProperty(r => r.WrittenUtc, nowUtc), ct)
            .ConfigureAwait(false);
    }

    private static void ApplyCompletion(CatalogRun target, CatalogRun projected, DateTime nowUtc)
    {
        // Identity fields are the same whether the row was enqueued or is being inserted fresh (RunFromJson derives
        // PipelineId from repo + flow name, exactly as enqueue did); the queue-only fields (EnqueuedUtc,
        // ClaimedByNode, the run parameters FullLoad/BackfillFrom/BackfillTo/FilePattern) and the claim's
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

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
