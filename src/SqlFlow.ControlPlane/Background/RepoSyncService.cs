using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Configuration;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Background;

/// <summary>
/// Keeps the shadow catalog synced from git: it scans the registered repo sources, and for each one due it pulls
/// the branch tip and runs the same catalog sync the CLI's <c>db sync</c> runs (pipelines, lineage, schedules, run
/// history), recording the pulled commit. Git stays the source of truth; this just keeps the queryable shadow
/// current without anyone running a sync by hand. A source's next sync is claimed atomically, so when several
/// control-plane nodes run this service the same source is never synced twice in one interval.
/// </summary>
/// <remarks>
/// Robustness: one source's failure (an unreachable remote, a bad branch) is recorded on that source and never
/// stops the others or the loop; a scan error is logged and retried next tick. Git credentials are resolved from
/// the control plane's own environment, never stored in the catalog. All diagnostics are secret-redacted.
/// <para>
/// The scan runs every <see cref="ManagedSyncOptions.PollSeconds"/> for the periodic syncs, and at once when a
/// sync-now or a new source wakes it (<see cref="RepoSyncSignal"/>), so an operator never waits out the scan interval.
/// </para>
/// </remarks>
public sealed partial class RepoSyncService : BackgroundService
{
    private const int MaxPerTick = 50;

    private readonly IServiceProvider _services;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _pollInterval;
    private readonly bool _connectLineage;
    private readonly bool _enabled;
    private readonly RepoSyncSignal _wake;
    private readonly GitMaterializer _materializer = new();
    private readonly ILogger<RepoSyncService> _logger;

    public RepoSyncService(
        IServiceProvider services, TimeProvider clock, IOptions<ControlPlaneOptions> options, RepoSyncSignal wake,
        ILogger<RepoSyncService> logger)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(wake);
        ArgumentNullException.ThrowIfNull(logger);
        _services = services;
        _clock = clock;
        _wake = wake;
        _pollInterval = TimeSpan.FromSeconds(Math.Max(1, options.Value.ManagedSync.PollSeconds));
        _connectLineage = options.Value.ManagedSync.ConnectLineage;
        // NOTE: the sync CANNOT be routed to the estate's compute workers - those containers run the CLI's
        // 'sqlflow worker' drain loop (no sync service). The control-plane app owns the managed sync, so its
        // environment must carry the data-plane connection references the lineage step resolves (the same
        // ${env:...} names the flows use); a missing one degrades that server's derive to a preserved-knowledge
        // warning. ManagedSync.Enabled=false opts an instance out entirely (a local dev control plane sharing
        // the production catalog must never steal claims).
        _enabled = options.Value.ManagedSync.Enabled;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            LogSyncDisabled();
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                // Host teardown has disposed the DI container and the loggers out from under this tick. No work
                // remains and logging would itself throw (the disposed Windows EventLog provider), so leave the loop
                // quietly instead of escalating shutdown noise into a "BackgroundService failed".
                break;
            }
            catch (Exception ex)
            {
                LogScanError(SecretHygiene.RedactedMessage(ex));
            }

            // Sleep until the next periodic scan, or until a sync-now or a new source wakes the loop, whichever is first.
            try
            {
                await _wake.WaitAsync(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        List<CatalogRepoSource> due;
        await using (var scope = _services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            due = (await RepoSourceStore.ListDueAsync(catalog, now, MaxPerTick, ct).ConfigureAwait(false)).ToList();
        }

        foreach (var source in due)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await SyncOneAsync(source, ct).ConfigureAwait(false);
        }
    }

    private async Task SyncOneAsync(CatalogRepoSource source, CancellationToken ct)
    {
        if (source.NextSyncUtc is not { } observed)
        {
            return;
        }

        await using var scope = _services.CreateAsyncScope();
        var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var next = now.AddSeconds(source.SyncIntervalSeconds);

        // Claim the sync by advancing the next-sync from the value we observed; only the winner pulls + syncs.
        if (!await RepoSourceStore.TryClaimSyncAsync(catalog, source.Id, observed, next, now, ct).ConfigureAwait(false))
        {
            return;
        }

        // Trace this attempt into the activity log the GUI's bottom panel tails, so an operator watches the sync
        // happen (clone, checkout, reconcile, result, warnings) instead of seeing only a terminal badge. The trace
        // shares this scope's catalog context and detaches its own rows, so it never disturbs the sync's writes;
        // its subject is the source id, keyed by the well-known repo-sync kind.
        var trace = await ActivityTrace.BeginAsync(
            catalog, ActivityKinds.RepoSync, source.Id.ToString(), _clock, ct).ConfigureAwait(false);

        try
        {
            await trace.InfoAsync("start", $"Sync started for '{source.Name}' (branch {source.Branch}).", ct).ConfigureAwait(false);

            // The sync resolves its references (the repository's git credential, and every server the connected lineage
            // tier opens) with the values the control plane supplies with this repository's runs, so lineage reaches
            // exactly the databases the runs do.
            var repoId = SqlFlow.Core.Identity.FlowIdentity.FromName(source.Name);
            var references = await scope.ServiceProvider.GetRequiredService<RunReferences>().ForAsync(repoId, ct).ConfigureAwait(false);
            using var supplied = SuppliedReferences.Supply(references);

            var resolver = scope.ServiceProvider.GetRequiredService<ISecretResolver>();
            var (workingDir, sha) = source.LocalPath is { Length: > 0 } localPath
                ? await ReadLocalPathAsync(catalog, trace, source.Name, localPath, ct).ConfigureAwait(false)
                : await CloneAsync(trace, resolver, source, ct).ConfigureAwait(false);

            // The preview-first selection: only the flows the source includes are projected as pipelines (an
            // excluded flow never becomes a catalog pipeline, so the scheduler never picks it up).
            var excludedFlowPaths = RepoSourceStore.ParseExcludedPaths(source.ExcludedFlowPaths);

            // The exact same catalog sync the CLI's `db sync` runs - one sync path, as a TWO-STEP trace: step
            // "sync" mirrors git (pipelines, schedules, runs); step "lineage" is the computation, streamed
            // under-the-hood into this trace as it runs (tier begins, each server's connect / harvest tally /
            // failure), so the panel shows exactly what the graph was built from - and what it could not reach.
            // When ConnectLineage is on (the default), the connected/derived tier runs too: it opens the
            // referenced SQL Servers with the source's resolved secrets and expands module bodies through the
            // T-SQL parser, so a stored-procedure flow gains the reads/writes of the procedure it executes. A
            // connect failure is non-fatal: the affected server's previously-derived lineage is preserved, the
            // failure is a first-class line here, and the offline tiers still land. A manual "sync now" carries
            // a force-lineage request on the source, so this sync recomputes the whole graph (and the offline
            // object-body/column enrichment) even when the commit is unchanged.
            await trace.InfoAsync("sync", "Step 1/2: reconciling catalog from the estate (pipelines, schedules, runs).", ct).ConfigureAwait(false);
            await trace.InfoAsync("lineage", "Step 2/2: computing lineage (progress below as it runs).", ct).ConfigureAwait(false);

            // The derived tier collects servers in parallel and reports from those tasks; the trace writes to
            // one DbContext, so progress lines serialize through this gate.
            var traceGate = new SemaphoreSlim(1, 1);
            async Task ReportProgressAsync(string message, CancellationToken token)
            {
                await traceGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await trace.InfoAsync("lineage", message, token).ConfigureAwait(false);
                }
                finally
                {
                    traceGate.Release();
                }
            }

            // The pre-sync snapshot of this repo's report visuals' content hashes, taken before SyncAsync deletes and
            // reinserts every subscriber report row: it is how the post-sync question step below tells an unchanged
            // visual from a new, changed, or moved one, since the sync itself preserves no row identity across passes.
            var before = await RepoSyncFollowUp.BeforeAsync(catalog, repoId, ct).ConfigureAwait(false);

            var result = await new CatalogSync(
                    scope.ServiceProvider.GetRequiredService<SqlFlow.Yaml.YamlDocumentLoader>(),
                    scope.ServiceProvider.GetServices<ICatalogSyncExtension>())
                .SyncAsync(catalog, workingDir, source.Name, source.RemoteUrl, _clock.GetUtcNow().UtcDateTime,
                    includeDerived: _connectLineage, secrets: resolver,
                    excludedFlowPaths: excludedFlowPaths, forceLineage: source.ForceLineageOnNextSync,
                    lineageProgress: ReportProgressAsync, ct: ct)
                .ConfigureAwait(false);

            await EmitResultAsync(trace, result, ct).ConfigureAwait(false);

            // Report questions and AI contexts: control-plane-only, after the sync's own transaction has committed, so a
            // slow model call or a context that does not resolve never blocks or rolls back the structural sync above.
            var followUp = await RepoSyncFollowUp
                .AfterAsync(catalog, scope.ServiceProvider, repoId, workingDir, before, _clock.GetUtcNow().UtcDateTime, ct)
                .ConfigureAwait(false);
            foreach (var warning in followUp.Questions)
            {
                await trace.InfoAsync("questions", warning, ct).ConfigureAwait(false);
            }

            foreach (var warning in followUp.AiContexts)
            {
                await trace.InfoAsync("ai-context", warning, ct).ConfigureAwait(false);
            }

            await RepoSourceStore.RecordSuccessAsync(catalog, source.Id, sha, _clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
            await trace.CompleteAsync(ActivityStatuses.Succeeded, $"Sync complete at {sha}.", ct).ConfigureAwait(false);
            LogSynced(source.Name, sha);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The host is stopping mid-attempt: no outcome is recorded, but the attempt is ended, so it does not show as
            // running (and keep a sync-now waiting) when nobody is running it. The request it would have answered stays
            // for the next attempt.
            await RecordAbandonedAsync(catalog, source.Id).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            var redacted = SecretHygiene.RedactedMessage(ex);
            LogSyncError(source.Name, redacted);
            await RecordFailureAsync(catalog, source.Id, redacted).ConfigureAwait(false);
            await CompleteTraceFailureAsync(trace, redacted).ConfigureAwait(false);
        }
    }

    /// <summary>Materializes a git source's branch tip into its commit folder in the node cache (reused when the tip
    /// has not moved; see <see cref="GitMaterializer.MaterializeBranch"/>), which the catalog sync then records as
    /// the repo's root. The credential is resolved from the source's stored ${...} reference, or when it has none from
    /// the host (its GitHub App, then its SQLFLOW_GIT_TOKEN); the secret is never stored.</summary>
    private async Task<(string WorkingDir, string Sha)> CloneAsync(
        ActivityTrace trace, ISecretResolver resolver, CatalogRepoSource source, CancellationToken ct)
    {
        var remoteUrl = source.RemoteUrl
            ?? throw new InvalidOperationException($"Repo source '{source.Name}' has neither a remote URL nor a local path.");
        await trace.InfoAsync("credentials", "Resolving git credentials.", ct).ConfigureAwait(false);
        var credential = await GitMaterializer
            .ResolveCredentialAsync(resolver, source.CredentialReference, source.CredentialUsername, remoteUrl, GitAccess.Read, ct)
            .ConfigureAwait(false);

        // The trace says which credential the sync read with (never the secret), so a refused clone is diagnosed from it.
        await trace.InfoAsync("credentials", $"Reading with {credential.Summary}.", ct).ConfigureAwait(false);
        await trace.InfoAsync("clone", $"Reading the tip of {remoteUrl} (branch {source.Branch}).", ct).ConfigureAwait(false);
        var checkout = _materializer.MaterializeBranch(remoteUrl, source.Branch, credential.Credentials, ct);
        await trace.InfoAsync("clone", checkout.Reused
            ? $"{checkout.CommitSha} is already checked out at {checkout.WorkingDirectory}; nothing to clone."
            : $"Checked out {checkout.CommitSha} at {checkout.WorkingDirectory}.", ct).ConfigureAwait(false);
        return (checkout.WorkingDirectory, checkout.CommitSha);
    }

    /// <summary>
    /// Reads a local-path source live, with no git step, so uncommitted and untracked files sync exactly as they sit
    /// on disk. The repo's remote is cleared first: a run is pinned to a commit only when its repo has a remote, so
    /// runs of this repo read the directory itself rather than a commit that does not hold the working tree. The
    /// recorded version is <c>worktree</c>, with the checkout's HEAD beside it when the directory is a git working copy.
    /// </summary>
    private static async Task<(string WorkingDir, string Sha)> ReadLocalPathAsync(
        CatalogDbContext catalog, ActivityTrace trace, string repoName, string localPath, CancellationToken ct)
    {
        await trace.InfoAsync("read", $"Reading {localPath} live (no git step).", ct).ConfigureAwait(false);
        if (!Directory.Exists(localPath))
        {
            throw new DirectoryNotFoundException(
                $"The control-plane host cannot see local path '{localPath}'. A local-path source only syncs where the control plane runs on the machine that holds the directory.");
        }

        await catalog.Repos
            .Where(r => r.Name == repoName && r.RemoteUrl != null)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.RemoteUrl, (string?)null), ct)
            .ConfigureAwait(false);

        var head = WorkingCopyHead(localPath);
        var version = head is null ? "worktree" : $"worktree@{head}";
        await trace.InfoAsync("read", $"Read the working tree ({version}).", ct).ConfigureAwait(false);
        return (localPath, version);
    }

    /// <summary>The HEAD commit of the git working copy at <paramref name="path"/>, or null when it is none or has no commit yet.</summary>
    private static string? WorkingCopyHead(string path)
    {
        var discovered = LibGit2Sharp.Repository.Discover(path);
        if (discovered is null)
        {
            return null;
        }

        using var repository = new LibGit2Sharp.Repository(discovered);
        return repository.Head?.Tip?.Sha;
    }

    /// <summary>Expands the sync result into a concise activity line plus one warning line per warning, so the panel
    /// shows what the reconcile did and surfaces every warning the sync collected.</summary>
    private static async Task EmitResultAsync(ActivityTrace trace, CatalogSyncResult result, CancellationToken ct)
    {
        await trace.InfoAsync(
            "result",
            $"Pipelines: {result.PipelinesAdded} added, {result.PipelinesUpdated} updated, "
                + $"{result.PipelinesUnchanged} unchanged, {result.PipelinesDeactivated} deactivated, "
                + $"{result.PipelinesDeleted} removed.",
            ct).ConfigureAwait(false);

        if (result.RunsAdded > 0)
        {
            await trace.InfoAsync("result", $"Run history: {result.RunsAdded} run(s) recorded.", ct).ConfigureAwait(false);
        }

        await trace.InfoAsync(
            "result",
            $"Lineage: {result.ObjectsUpserted} objects, {result.LineageEdges} edges, {result.Waves} waves"
                + (result.LineageEdgesPreserved > 0
                    ? $" ({result.LineageEdgesPreserved} previously-derived edge(s) preserved from unreachable servers)"
                    : string.Empty)
                + (result.LineageConnected ? " (connected)." : "."),
            ct).ConfigureAwait(false);

        // A derived-tier failure means the graph is knowingly incomplete (module bodies unread, so sp flows and
        // view readers keep unknown reads/writes). That must be a first-class result line, not one warning among
        // dozens: the operator watching the panel sees immediately that the connected pass did not cover the
        // estate and which count of servers it missed.
        var unreachable = result.Warnings.Count(w => w.Contains("derived lineage unavailable", StringComparison.OrdinalIgnoreCase));
        if (unreachable > 0)
        {
            await trace.WarnAsync(
                "result",
                $"Derived lineage MISSING for {unreachable} server(s): module bodies were not harvested, so stored-procedure and view lineage is incomplete. See the per-server warnings below.",
                ct).ConfigureAwait(false);
        }

        foreach (var warning in result.Warnings)
        {
            await trace.WarnAsync("warning", warning, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Best-effort terminal "failed" trace line, on its own short deadline so a cancelled or unhealthy
    /// request context cannot leave the panel hanging without a terminal event (mirrors <see cref="RecordFailureAsync"/>).</summary>
    private async Task CompleteTraceFailureAsync(ActivityTrace trace, string error)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await trace.CompleteAsync(ActivityStatuses.Failed, $"Sync failed: {error}", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogScanError(SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>Best-effort end of an attempt whose host is stopping, on its own short deadline because the sync's
    /// token has already been cancelled (mirrors <see cref="RecordFailureAsync"/>).</summary>
    private async Task RecordAbandonedAsync(CatalogDbContext catalog, Guid id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RepoSourceStore.RecordAbandonedAsync(catalog, id, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogScanError(SecretHygiene.RedactedMessage(ex));
        }
    }

    private async Task RecordFailureAsync(CatalogDbContext catalog, Guid id, string error)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await RepoSourceStore.RecordFailureAsync(catalog, id, error, _clock.GetUtcNow().UtcDateTime, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogScanError(SecretHygiene.RedactedMessage(ex));
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Synced repo source '{Source}' at {CommitSha}.")]
    private partial void LogSynced(string source, string commitSha);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Repo source '{Source}' sync failed: {Error}")]
    private partial void LogSyncError(string source, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Managed-sync scan error: {Error}")]
    private partial void LogScanError(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Managed sync is disabled on this instance (ControlPlane:ManagedSync:Enabled=false); it will not claim repo syncs.")]
    private partial void LogSyncDisabled();
}
