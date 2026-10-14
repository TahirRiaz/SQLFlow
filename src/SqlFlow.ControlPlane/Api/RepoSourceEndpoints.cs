using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A tracked repo source the control plane auto-syncs into the catalog, with its schedule and last
/// result: a git remote (<see cref="RemoteUrl"/> set) it clones and pulls, or a directory (<see cref="LocalPath"/>
/// set) it reads live with no git step, for a working copy that has never been committed or pushed. Exactly one of
/// the two is set. <see cref="CredentialReference"/> is a secret reference (never a secret value), safe to return
/// to clients, and applies only to a git source. <see cref="ExcludedFlowPaths"/> is the preview-first selection of
/// flow files this source does NOT import. <see cref="SyncRequestedUtc"/> is the latest sync-now,
/// <see cref="SyncStartedUtc"/> the start of the latest attempt, and <see cref="SyncPending"/> whether a sync is in
/// progress for an operator (a sync-now not yet answered, or an attempt running; see <see cref="RepoSyncWaiter"/>), so
/// a client shows a sync as running until the commit it pulls is the one runs are pinned to.</summary>
public sealed record RepoSourceDto(
    Guid Id, string Name, string? RemoteUrl, string? LocalPath, string Branch, bool Enabled, int SyncIntervalSeconds,
    DateTime? NextSyncUtc, DateTime? LastSyncUtc, string? LastSyncedSha, string? LastError,
    string? CredentialReference, string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths,
    DateTime CreatedUtc, DateTime UpdatedUtc,
    DateTime? SyncRequestedUtc = null, DateTime? SyncStartedUtc = null, bool SyncPending = false, string? ConfiguredBy = null);

/// <summary>The body to register (or update) a tracked repo source: exactly one of <see cref="RemoteUrl"/> (a git
/// remote the managed sync clones and pulls) and <see cref="LocalPath"/> (a directory the control-plane host reads
/// live, with no git step) must be given. <see cref="CredentialReference"/> is a secret reference
/// (<c>${keyvault:vault/secret}</c> / <c>${env:NAME}</c>) for a private remote's token, never a raw token: the
/// secret is created and maintained in the vault, SQLFlow only references it; it is ignored for a local-path
/// source. <see cref="ExcludedFlowPaths"/> are the repo-relative flow files to leave out of the import (from a
/// discover preview); null or empty imports every <c>*.flow.yaml</c>.</summary>
public sealed record RegisterRepoSourceRequest(
    string Name, string? RemoteUrl, string? Branch, int? SyncIntervalSeconds, bool? Enabled,
    string? CredentialReference = null, string? CredentialUsername = null, string[]? ExcludedFlowPaths = null,
    string? LocalPath = null);

/// <summary>The body to preview a repo's flows before importing anything: a git remote plus optional branch and a
/// credential reference. Read-only; the clone lives in a cache and nothing is written to the catalog.</summary>
public sealed record DiscoverRepoRequest(
    string RemoteUrl, string? Branch, string? CredentialReference, string? CredentialUsername);

/// <summary>One <c>*.flow.yaml</c> a discover found, for the selection wizard: its repo-relative path, parsed name
/// and kind (or the parse error when it does not parse), size, and secret-redacted content for preview.</summary>
public sealed record DiscoveredFlowDto(
    string RelativePath, string? FlowName, string? Kind, long SizeBytes, bool ParseOk, string? ParseError, string? Content);

/// <summary>The registered-source acknowledgement.</summary>
public sealed record RepoSourceRegistered(Guid Id);

/// <summary>
/// The managed-sync surface: register a git repo the control plane keeps the catalog synced from, list the tracked
/// sources (with their last commit and any error), and force a sync now. Reads are mapped under the "read" scope;
/// the mutations under "operate". A credential to pull a private remote is never accepted here - the control plane
/// resolves it from its own environment.
/// </summary>
public static class RepoSourceEndpoints
{
    // A single ${scheme:locator} reference (the whole value), so a raw pasted token is rejected: SQLFlow references
    // secrets, it never stores them. Matches ${env:NAME} and ${keyvault:vault/secret}.
    private static readonly Regex SecretReferencePattern =
        new(@"^\$\{[a-zA-Z]+:[^}]+\}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static RouteGroupBuilder MapRepoSourceReadEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapGet("/repos/sources", ListSourcesAsync).WithTags("RepoSources").WithName("ListRepoSources");
        return group;
    }

    public static RouteGroupBuilder MapRepoSourceWriteEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/repos/sources", RegisterSourceAsync).WithTags("RepoSources").WithName("RegisterRepoSource");
        group.MapPost("/repos/sources/{id:guid}/sync", SyncNowAsync).WithTags("RepoSources").WithName("SyncRepoSourceNow");
        group.MapDelete("/repos/sources/{id:guid}", DeleteSourceAsync).WithTags("RepoSources").WithName("DeleteRepoSource");
        // Preview-first scan: clone a remote and list its flows without importing (nothing reaches the catalog until
        // a sync). Under "operate" because it clones a remote using the deployment's git credential.
        group.MapPost("/repos/discover", DiscoverRepoAsync).WithTags("RepoSources").WithName("DiscoverRepoFlows");
        return group;
    }

    private static async Task<Ok<PagedResult<RepoSourceDto>>> ListSourcesAsync(
        CatalogDbContext db, RepoSyncWaiter syncWaiter, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RepoSources.AsNoTracking().OrderBy(s => s.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(s => ToDto(s, syncWaiter)).ToList();
        return TypedResults.Ok(new PagedResult<RepoSourceDto>(items, p, size, total));
    }

    private static async Task<Results<Created<RepoSourceRegistered>, ProblemHttpResult>> RegisterSourceAsync(
        RegisterRepoSourceRequest request, CatalogDbContext db, TimeProvider clock, RepoSyncSignal syncSignal, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return TypedResults.Problem(
                detail: "A repo source requires a non-blank name.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var hasRemote = !string.IsNullOrWhiteSpace(request.RemoteUrl);
        var hasLocal = !string.IsNullOrWhiteSpace(request.LocalPath);
        if (hasRemote == hasLocal)
        {
            return TypedResults.Problem(
                detail: "A repo source requires exactly one of remoteUrl (a git remote the managed sync clones and pulls) and localPath (a directory the control-plane host reads live, with no git step).",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (hasLocal && !Directory.Exists(request.LocalPath))
        {
            return TypedResults.Problem(
                detail: $"The control-plane host cannot see localPath '{request.LocalPath}'. A local-path source only works when the control plane runs on the machine that holds the directory.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        if (await RepoSourceStore.ConfiguredByNameAsync(db, request.Name, ct).ConfigureAwait(false) is { } setting)
        {
            return SetByConfiguration(request.Name.Trim(), setting);
        }

        var credentialReference = string.IsNullOrWhiteSpace(request.CredentialReference)
            ? null
            : request.CredentialReference.Trim();
        if (credentialReference is not null && !SecretReferencePattern.IsMatch(credentialReference))
        {
            return TypedResults.Problem(
                detail: "credentialReference must be a secret reference like ${keyvault:my-vault/github-pat} or ${env:GIT_TOKEN}, not a raw token. Create and maintain the secret in your vault; SQLFlow only references it.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var id = await RepoSourceStore.UpsertAsync(
            db, request.Name.Trim(), hasRemote ? request.RemoteUrl!.Trim() : null, request.Branch ?? "main",
            request.Enabled ?? true, request.SyncIntervalSeconds ?? 300, clock.GetUtcNow().UtcDateTime,
            hasLocal ? request.LocalPath!.Trim() : null,
            credentialReference, request.CredentialUsername, request.ExcludedFlowPaths, ct).ConfigureAwait(false);

        // A new or re-enabled source is due now: start its first sync at once rather than on the next scan.
        syncSignal.Wake();
        return TypedResults.Created($"/api/v1/repos/sources/{id}", new RepoSourceRegistered(id));
    }

    /// <summary>
    /// Syncs a source now and answers once the sync has happened: the source is made due and this host's sync loop is
    /// woken to start it at once (<see cref="RepoSyncSignal"/>), and the response waits (up to
    /// <c>ManagedSync:SyncNowWaitSeconds</c>) for the attempt that answers this request, so it carries the commit that
    /// attempt pulled (<c>lastSyncedSha</c>), which is the commit a run started afterwards is pinned to, or the error it
    /// failed with (<c>lastError</c>). 200 when the attempt answered, success or failure; 202 with the source still
    /// <c>syncPending</c> when it outlasted the wait; 404 for an unknown or disabled source.
    /// </summary>
    private static async Task<Results<Ok<RepoSourceDto>, Accepted<RepoSourceDto>, ProblemHttpResult>> SyncNowAsync(
        Guid id, CatalogDbContext db, TimeProvider clock, RepoSyncWaiter syncWaiter, RepoSyncSignal syncSignal,
        CancellationToken ct)
    {
        var requestedUtc = clock.GetUtcNow().UtcDateTime;
        if (!await QueueSyncAsync(db, id, "Sync requested", requestedUtc, clock, syncSignal, ct).ConfigureAwait(false))
        {
            return TypedResults.Problem(
                detail: $"No enabled repo source '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var (row, answered) = await syncWaiter.WaitForAnswerAsync(db, id, requestedUtc, ct).ConfigureAwait(false);
        if (row is null)
        {
            return TypedResults.Problem(
                detail: $"Repo source '{id}' was removed while its sync was pending.",
                statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        var dto = ToDto(row, syncWaiter);
        return answered ? TypedResults.Ok(dto) : TypedResults.Accepted("/api/v1/repos/sources", dto);
    }

    /// <summary>
    /// Asks the managed sync to run the repo synced from git as <paramref name="repoName"/> now, with a full lineage
    /// recompute. False when the repo is not synced from an enabled git source (a local-path repo applies such a change
    /// on its next <c>sqlflow db sync</c> instead).
    /// </summary>
    internal static async Task<bool> QueueSyncForRepoAsync(
        CatalogDbContext db, string repoName, string reason, TimeProvider clock, RepoSyncSignal syncSignal,
        CancellationToken ct)
    {
        var sourceId = await RepoSourceStore.TriggerForRepoAsync(db, repoName, clock.GetUtcNow().UtcDateTime, ct)
            .ConfigureAwait(false);
        if (sourceId is not { } id)
        {
            return false;
        }

        await TraceQueuedAsync(db, id, reason, clock, syncSignal, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Queues one source's sync, requested at <paramref name="requestedUtc"/> (the single path behind "sync
    /// now" and every change that needs the graph recomputed; a sync-now waits for the attempt that answers that
    /// instant). False when no enabled source has the id.</summary>
    private static async Task<bool> QueueSyncAsync(
        CatalogDbContext db, Guid id, string reason, DateTime requestedUtc, TimeProvider clock, RepoSyncSignal syncSignal,
        CancellationToken ct)
    {
        var outcome = await RepoSourceStore.TriggerNowAsync(db, id, requestedUtc, ct).ConfigureAwait(false);
        if (outcome == RepoSourceMutation.NotFound)
        {
            return false;
        }

        await TraceQueuedAsync(db, id, reason, clock, syncSignal, ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Opens the queued sync's activity trace, then starts the sync now instead of on the loop's next scan.</summary>
    private static async Task TraceQueuedAsync(
        CatalogDbContext db, Guid id, string reason, TimeProvider clock, RepoSyncSignal syncSignal, CancellationToken ct)
    {
        // Post a non-terminal "queued" line to the activity trace so the panel a client opens on this click latches
        // onto a live trace right away: the background sync worker then appends the real clone/reconcile/result trace,
        // and the stream stays open until that attempt is terminal. The line is written before the loop is woken, so it
        // always comes first in the trace.
        var trace = await ActivityTrace.BeginAsync(db, ActivityKinds.RepoSync, id.ToString(), clock, ct).ConfigureAwait(false);
        await trace.InfoAsync("queued", $"{reason}; waiting for a worker to pick it up.", ct).ConfigureAwait(false);

        // A sync already running finishes first, and the woken scan follows it at once.
        syncSignal.Wake();
    }

    /// <summary>
    /// Deletes a tracked git source. This is the removal for a source-only row (one registered but not yet synced, so
    /// no repo has been produced): once a sync has created the repo, deleting the repo (<c>DELETE /repos/{id}</c>) is
    /// what removes the source, so the two facets are never left half-deleted. Returns 404 when no source has the id.
    /// </summary>
    private static async Task<Results<Ok, ProblemHttpResult>> DeleteSourceAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        if (await RepoSourceStore.ConfiguredByAsync(db, id, ct).ConfigureAwait(false) is { } setting)
        {
            return SetByConfiguration(id.ToString("D"), setting);
        }

        var removed = await RepoSourceStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return removed
            ? TypedResults.Ok()
            : TypedResults.Problem(
                detail: $"No repo source '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }

    /// <summary>
    /// The refusal of a change to a source the control plane's configuration sets: the next start would set it back, so the
    /// answer names the setting to change instead.
    /// </summary>
    internal static ProblemHttpResult SetByConfiguration(string source, string setting)
        => TypedResults.Problem(
            detail: $"Repo source '{source}' is set by the control plane's configuration ({setting}); change it there, and the control plane writes it when it next starts.",
            statusCode: StatusCodes.Status409Conflict, title: "Set by configuration");

    private static async Task<Results<Ok<List<DiscoveredFlowDto>>, ProblemHttpResult>> DiscoverRepoAsync(
        DiscoverRepoRequest request, ISecretResolver resolver, SqlFlow.Yaml.YamlDocumentLoader documents, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            return TypedResults.Problem(
                detail: "A discover requires a non-blank remoteUrl.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        var credentialReference = string.IsNullOrWhiteSpace(request.CredentialReference)
            ? null
            : request.CredentialReference.Trim();
        if (credentialReference is not null && !SecretReferencePattern.IsMatch(credentialReference))
        {
            return TypedResults.Problem(
                detail: "credentialReference must be a secret reference like ${keyvault:my-vault/github-pat} or ${env:GIT_TOKEN}, not a raw token.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
        }

        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(resolver, credentialReference, request.CredentialUsername, ct).ConfigureAwait(false);
            var branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim();
            var remoteUrl = request.RemoteUrl.Trim();

            // Clone + parse off the request thread (git + file IO are blocking); read-only, nothing touches the
            // catalog. The clone lands in the node cache and is reused by a later sync of the same remote.
            var flows = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer().MaterializeBranch(remoteUrl, branch, credentials, ct).WorkingDirectory;
                    return FlowDiscovery.Discover(workingDir, documents, ct);
                },
                ct).ConfigureAwait(false);

            var dtos = flows
                .Select(f => new DiscoveredFlowDto(f.RelativePath, f.FlowName, f.Kind, f.SizeBytes, f.ParseOk, f.ParseError, f.Content))
                .ToList();
            return TypedResults.Ok(dtos);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException)
        {
            return TypedResults.Problem(
                detail: SecretHygiene.RedactedMessage(ex),
                statusCode: StatusCodes.Status400BadRequest, title: "Discover failed");
        }
    }

    // A method (not an EF expression) so the stored ExcludedFlowPaths JSON can be deserialized; the repo-source
    // table is tiny, so materializing the row before mapping is free.
    private static RepoSourceDto ToDto(CatalogRepoSource s, RepoSyncWaiter syncWaiter) => new(
        s.Id, s.Name, s.RemoteUrl, s.LocalPath, s.Branch, s.Enabled, s.SyncIntervalSeconds,
        s.NextSyncUtc, s.LastSyncUtc, s.LastSyncedSha, s.LastError,
        s.CredentialReference, s.CredentialUsername,
        RepoSourceStore.ParseExcludedPaths(s.ExcludedFlowPaths).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        s.CreatedUtc, s.UpdatedUtc,
        s.SyncRequestedUtc, s.SyncStartedUtc, syncWaiter.IsPending(s), s.ConfiguredBy);
}
