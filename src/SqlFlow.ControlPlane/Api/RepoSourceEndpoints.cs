using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Api;

/// <summary>A tracked git repo the control plane auto-syncs into the catalog, with its schedule and last result.
/// <see cref="CredentialReference"/> is a secret reference (never a secret value), safe to return to clients.
/// <see cref="ExcludedFlowPaths"/> is the preview-first selection of flow files this source does NOT import.</summary>
public sealed record RepoSourceDto(
    Guid Id, string Name, string RemoteUrl, string Branch, bool Enabled, int SyncIntervalSeconds,
    DateTime? NextSyncUtc, DateTime? LastSyncUtc, string? LastSyncedSha, string? LastError,
    string? CredentialReference, string? CredentialUsername, IReadOnlyList<string> ExcludedFlowPaths,
    DateTime CreatedUtc, DateTime UpdatedUtc);

/// <summary>The body to register (or update) a tracked repo source. <see cref="CredentialReference"/> is a secret
/// reference (<c>${keyvault:vault/secret}</c> / <c>${env:NAME}</c>) for a private remote's token, never a raw
/// token: the secret is created and maintained in the vault, SQLFlow only references it.
/// <see cref="ExcludedFlowPaths"/> are the repo-relative flow files to leave out of the import (from a discover
/// preview); null or empty imports every <c>*.flow.yaml</c>.</summary>
public sealed record RegisterRepoSourceRequest(
    string Name, string RemoteUrl, string? Branch, int? SyncIntervalSeconds, bool? Enabled,
    string? CredentialReference = null, string? CredentialUsername = null, string[]? ExcludedFlowPaths = null);

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
        CatalogDbContext db, int? page, int? pageSize, CancellationToken ct)
    {
        var (p, size) = PageRequest.Normalize(page, pageSize);
        var ordered = db.RepoSources.AsNoTracking().OrderBy(s => s.Name);
        var total = await ordered.LongCountAsync(ct).ConfigureAwait(false);
        var rows = await ordered.Skip((p - 1) * size).Take(size).ToListAsync(ct).ConfigureAwait(false);
        var items = rows.Select(ToDto).ToList();
        return TypedResults.Ok(new PagedResult<RepoSourceDto>(items, p, size, total));
    }

    private static async Task<Results<Created<RepoSourceRegistered>, ProblemHttpResult>> RegisterSourceAsync(
        RegisterRepoSourceRequest request, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.RemoteUrl))
        {
            return TypedResults.Problem(
                detail: "A repo source requires a non-blank name and remoteUrl.",
                statusCode: StatusCodes.Status400BadRequest, title: "Invalid request");
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
            db, request.Name.Trim(), request.RemoteUrl.Trim(), request.Branch ?? "main",
            request.Enabled ?? true, request.SyncIntervalSeconds ?? 300, clock.GetUtcNow().UtcDateTime,
            credentialReference, request.CredentialUsername, request.ExcludedFlowPaths, ct).ConfigureAwait(false);

        return TypedResults.Created($"/api/v1/repos/sources/{id}", new RepoSourceRegistered(id));
    }

    private static async Task<Results<Ok<RepoSourceDto>, ProblemHttpResult>> SyncNowAsync(
        Guid id, CatalogDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outcome = await RepoSourceStore.TriggerNowAsync(db, id, clock.GetUtcNow().UtcDateTime, ct).ConfigureAwait(false);
        if (outcome == RepoSourceMutation.NotFound)
        {
            return TypedResults.Problem(
                detail: $"No enabled repo source '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
        }

        // Post a non-terminal "queued" line to the activity trace so the panel a client opens on this click latches
        // onto a live trace right away: the background sync worker (which claims the source on its next tick) then
        // appends the real clone/reconcile/result trace, and the stream stays open until that attempt is terminal.
        var trace = await ActivityTrace.BeginAsync(db, ActivityKinds.RepoSync, id.ToString(), clock, ct).ConfigureAwait(false);
        await trace.InfoAsync("queued", "Sync requested; waiting for a worker to pick it up.", ct).ConfigureAwait(false);

        var row = await db.RepoSources.AsNoTracking().Where(s => s.Id == id).FirstAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(ToDto(row));
    }

    /// <summary>
    /// Deletes a tracked git source. This is the removal for a source-only row (one registered but not yet synced, so
    /// no repo has been produced): once a sync has created the repo, deleting the repo (<c>DELETE /repos/{id}</c>) is
    /// what removes the source, so the two facets are never left half-deleted. Returns 404 when no source has the id.
    /// </summary>
    private static async Task<Results<Ok, ProblemHttpResult>> DeleteSourceAsync(
        Guid id, CatalogDbContext db, CancellationToken ct)
    {
        var removed = await RepoSourceStore.DeleteAsync(db, id, ct).ConfigureAwait(false);
        return removed
            ? TypedResults.Ok()
            : TypedResults.Problem(
                detail: $"No repo source '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");
    }

    private static async Task<Results<Ok<List<DiscoveredFlowDto>>, ProblemHttpResult>> DiscoverRepoAsync(
        DiscoverRepoRequest request, ISecretResolver resolver, CancellationToken ct)
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
                    var (workingDir, _) = new GitMaterializer().MaterializeBranch(remoteUrl, branch, credentials, ct);
                    return FlowDiscovery.Discover(workingDir, ct);
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
    private static RepoSourceDto ToDto(CatalogRepoSource s) => new(
        s.Id, s.Name, s.RemoteUrl, s.Branch, s.Enabled, s.SyncIntervalSeconds,
        s.NextSyncUtc, s.LastSyncUtc, s.LastSyncedSha, s.LastError,
        s.CredentialReference, s.CredentialUsername,
        RepoSourceStore.ParseExcludedPaths(s.ExcludedFlowPaths).OrderBy(p => p, StringComparer.Ordinal).ToArray(),
        s.CreatedUtc, s.UpdatedUtc);
}
