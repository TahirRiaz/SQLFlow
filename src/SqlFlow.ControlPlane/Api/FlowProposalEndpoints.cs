using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Identity;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;
using SqlFlow.SourceControl.Proposals;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One file a proposal writes: a repo-relative path (<c>flows/orders.01_pre.flow.yaml</c>) and its full
/// content. An existing path is revised; a new path is added.</summary>
public sealed record ProposeFlowsFile(string Path, string Content);

/// <summary>The body to propose pipelines to a tracked repo source as a pull request. <see cref="Files"/> are the
/// generated flow files (typically a discover's pre + ods pair). <see cref="BaseBranch"/> defaults to the source's
/// tracked branch; <see cref="HeadBranch"/> defaults to a content-derived <c>sqlflow/proposal-*</c> branch. No secret
/// is accepted here: the control plane pushes and opens the pull request with the source's own stored credential.</summary>
public sealed record ProposeFlowsRequest(
    string Title, string? Body, string? BaseBranch, string? HeadBranch, IReadOnlyList<ProposeFlowsFile> Files);

/// <summary>An opened proposal: the pull-request URL a human reviews it at and its number, plus the pushed head
/// branch, the commit SHA (feed it to a commit-pinned run to test the proposal before it merges), the file
/// count, and the preflight warnings that were stamped into the pull-request body (empty when the preflight was
/// clean).</summary>
public sealed record FlowProposalCreated(
    string PullRequestUrl, int PullRequestNumber, string HeadBranch, string CommitSha, int FilesChanged,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The authoring surface: propose pipelines to a tracked repo source as a pull request
/// (<c>POST /api/v1/repos/sources/{id}/proposals</c>). It never writes the catalog directly; the catalog is a
/// projection of the tracked branch, so the only durable way to add pipelines is a branch a human reviews and merges,
/// after which the existing managed sync imports them. Mapped under the "author" scope: pushing to a source repo is a
/// higher trust boundary than triggering a run, so it is separate from "operate". The pull request is a proposal, not
/// a merge, so a person always stays in the loop, and the commit is authored under the requesting user for audit.
/// Every proposal is preflighted first (<see cref="FlowProposalPreflight"/>): a flow file the sync could not import
/// is rejected before any git work, and softer findings ride into the response and the pull-request body.
/// </summary>
public static class FlowProposalEndpoints
{
    // The flow files a proposal is allowed to write. Restricting the extension keeps this an authoring surface (flows
    // and their companion SQL/JSON), not an arbitrary write-any-file-to-the-repo primitive.
    private static readonly string[] AllowedExtensions = [".yaml", ".yml", ".sql", ".json", ".md"];

    // A git-safe branch name: starts alphanumeric, then word/dot/dash/slash; ".." is rejected separately.
    private static readonly Regex BranchNamePattern =
        new(@"^[A-Za-z0-9][A-Za-z0-9._/-]{0,199}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const int MaxFiles = 100;
    private const int MaxFileChars = 500_000;
    private const int MaxTotalChars = 2_000_000;

    public static RouteGroupBuilder MapFlowProposalEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        group.MapPost("/repos/sources/{id:guid}/proposals", ProposeAsync)
            .WithTags("RepoSources").WithName("ProposePipelines");
        return group;
    }

    private static async Task<Results<Created<FlowProposalCreated>, ProblemHttpResult>> ProposeAsync(
        Guid id,
        ProposeFlowsRequest request,
        ClaimsPrincipal user,
        CatalogDbContext db,
        ISecretResolver resolver,
        IGitProposalPublisher gitPublisher,
        IEnumerable<IPullRequestPublisher> prPublishers,
        CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Title))
        {
            return Problem("A proposal requires a non-blank title.", StatusCodes.Status400BadRequest);
        }

        var filesOrError = ValidateFiles(request.Files);
        if (filesOrError.Error is { } fileError)
        {
            return Problem(fileError, StatusCodes.Status400BadRequest);
        }

        var headBranch = request.HeadBranch?.Trim();
        if (!string.IsNullOrEmpty(headBranch) && !IsValidBranchName(headBranch))
        {
            return Problem(
                "headBranch must be a git-safe branch name (letters, digits, '.', '_', '-', '/'; no '..').",
                StatusCodes.Status400BadRequest);
        }

        var baseBranch = request.BaseBranch?.Trim();
        if (!string.IsNullOrEmpty(baseBranch) && !IsValidBranchName(baseBranch))
        {
            return Problem("baseBranch must be a git-safe branch name.", StatusCodes.Status400BadRequest);
        }

        var source = await db.RepoSources.AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Name, s.RemoteUrl, s.Branch, s.CredentialReference, s.CredentialUsername })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (source is null)
        {
            return Problem($"No repo source '{id}'.", StatusCodes.Status404NotFound, "Not found");
        }

        // Preflight with the engine's own loaders BEFORE anything touches git: a flow that would not import is
        // rejected here with the loader's message (merging it would land nothing), and softer findings (an
        // endpoint change on a revised flow, a duplicate flow name) ride into the response and the pull-request
        // body so the human reviewer decides with them in view. The synced repo's id is derived from the
        // source's name, exactly as the managed sync derives it.
        var repoId = FlowIdentity.FromName(source.Name);
        var existingPipelines = await db.Pipelines.AsNoTracking()
            .Where(p => p.RepoId == repoId && p.Active)
            .Select(p => new FlowProposalPreflight.ExistingPipeline(p.Name, p.RelativePath, p.Yaml))
            .ToListAsync(ct).ConfigureAwait(false);
        var preflight = FlowProposalPreflight.Run(filesOrError.Files!, existingPipelines);
        if (preflight.Errors.Count > 0)
        {
            return Problem(
                "The proposal was rejected by preflight; nothing was pushed.\n"
                + string.Join("\n", preflight.Errors.Concat(preflight.Warnings).Select(f => $"- {f}")),
                StatusCodes.Status422UnprocessableEntity, "Proposal failed preflight");
        }

        var coordinates = RemoteUrlParser.Parse(source.RemoteUrl);
        if (coordinates is null)
        {
            return Problem(
                "Automated pull requests are supported only for github.com and bitbucket.org HTTPS remotes; "
                + "this source's remote is not one of them.",
                StatusCodes.Status400BadRequest);
        }

        var publisher = prPublishers.FirstOrDefault(p => p.Provider == coordinates.Provider);
        if (publisher is null)
        {
            return Problem(
                $"No pull-request publisher is registered for {coordinates.Provider}.",
                StatusCodes.Status400BadRequest);
        }

        GitMaterializerCredentials? credentials;
        try
        {
            credentials = await GitMaterializer
                .ResolveCredentialsAsync(resolver, source.CredentialReference, source.CredentialUsername, ct)
                .ConfigureAwait(false);
        }
        catch (SqlFlowNodeException ex)
        {
            return Problem(SecretHygiene.RedactedMessage(ex), StatusCodes.Status400BadRequest);
        }

        if (credentials is null)
        {
            return Problem(
                "This repo source has no git credential configured; set its credentialReference so the control plane "
                + "can push a proposal branch and open a pull request.",
                StatusCodes.Status400BadRequest);
        }

        var files = filesOrError.Files!;
        var effectiveBase = string.IsNullOrEmpty(baseBranch) ? source.Branch : baseBranch;
        var effectiveHead = string.IsNullOrEmpty(headBranch) ? GenerateBranchName(request.Title, files) : headBranch;
        var (authorName, authorEmail) = AuthorIdentity(user);
        var commitMessage = ComposeCommitMessage(request.Title, request.Body);
        var pullRequestBody = StampPreflightWarnings(request.Body, preflight.Warnings);

        ProposalPublishResult publish;
        try
        {
            publish = await gitPublisher.PublishAsync(
                new ProposalPublishRequest(
                    source.RemoteUrl, effectiveBase, effectiveHead, commitMessage,
                    authorName, authorEmail, credentials.Username, credentials.Secret, files),
                ct).ConfigureAwait(false);
        }
        catch (SqlFlowException ex)
        {
            return Problem(
                $"The proposal branch could not be published: {SecretHygiene.RedactedMessage(ex)}",
                StatusCodes.Status400BadRequest, "Proposal failed");
        }

        try
        {
            var result = await publisher.CreateAsync(
                coordinates,
                new PullRequestRequest(
                    effectiveBase, publish.HeadBranch, request.Title.Trim(), pullRequestBody,
                    credentials.Username, credentials.Secret),
                ct).ConfigureAwait(false);

            return TypedResults.Created(
                result.Url,
                new FlowProposalCreated(
                    result.Url, result.Number, publish.HeadBranch, publish.CommitSha, publish.FilesChanged,
                    preflight.Warnings.Select(f => f.ToString()).ToList()));
        }
        catch (SqlFlowException ex)
        {
            // The branch is already on the remote but the pull request did not open. Roll the branch back so a retry
            // starts clean, then surface the real cause.
            await gitPublisher.TryDeleteRemoteBranchAsync(
                new RemoteBranchRef(source.RemoteUrl, publish.HeadBranch, credentials.Username, credentials.Secret), ct)
                .ConfigureAwait(false);
            return Problem(
                $"The proposal branch was pushed but the pull request could not be opened (the branch was rolled back): "
                + SecretHygiene.RedactedMessage(ex),
                StatusCodes.Status502BadGateway, "Pull request failed");
        }
    }

    private static (List<ProposalFile>? Files, string? Error) ValidateFiles(IReadOnlyList<ProposeFlowsFile>? files)
    {
        if (files is null || files.Count == 0)
        {
            return (null, "A proposal requires at least one file.");
        }

        if (files.Count > MaxFiles)
        {
            return (null, $"A proposal may include at most {MaxFiles} files.");
        }

        var result = new List<ProposalFile>(files.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || string.IsNullOrWhiteSpace(file.Path))
            {
                return (null, "Every proposal file requires a non-blank path.");
            }

            // Reject an absolute/rooted path (a leading slash, a drive, or a UNC prefix) before any normalization, so
            // it is never silently reinterpreted as repo-relative.
            var cleaned = file.Path.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(cleaned) || cleaned.Contains(':', StringComparison.Ordinal))
            {
                return (null, $"The path '{file.Path}' must be repo-relative, not absolute.");
            }

            var normalized = cleaned.TrimStart('/');

            if (normalized.Split('/').Any(segment => segment is ".." or "."))
            {
                return (null, $"The path '{file.Path}' must not contain '.' or '..' segments.");
            }

            if (!AllowedExtensions.Any(ext => normalized.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            {
                return (null,
                    $"The path '{file.Path}' must end with one of: {string.Join(", ", AllowedExtensions)} "
                    + "(a proposal writes flow files and their companions, not arbitrary files).");
            }

            if (!seen.Add(normalized))
            {
                return (null, $"The path '{file.Path}' is listed more than once.");
            }

            if (string.IsNullOrWhiteSpace(file.Content))
            {
                return (null, $"The file '{file.Path}' has no content.");
            }

            if (file.Content.Length > MaxFileChars)
            {
                return (null, $"The file '{file.Path}' exceeds the {MaxFileChars}-character limit.");
            }

            total += file.Content.Length;
            if (total > MaxTotalChars)
            {
                return (null, $"The proposal's total content exceeds the {MaxTotalChars}-character limit.");
            }

            result.Add(new ProposalFile(normalized, file.Content));
        }

        return (result, null);
    }

    private static bool IsValidBranchName(string branch)
        => BranchNamePattern.IsMatch(branch)
            && !branch.Contains("..", StringComparison.Ordinal)
            && !branch.EndsWith('/')
            && !branch.EndsWith(".lock", StringComparison.OrdinalIgnoreCase);

    // A deterministic branch per proposal content, so an identical re-proposal targets the same branch name (and the
    // push's up-to-date/rejection makes a duplicate visible) rather than littering the remote with random branches.
    private static string GenerateBranchName(string title, IReadOnlyList<ProposalFile> files)
    {
        var builder = new StringBuilder(title);
        foreach (var file in files)
        {
            builder.Append('\n').Append(file.Path).Append('\n').Append(file.Content);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return "sqlflow/proposal-" + Convert.ToHexStringLower(hash)[..12];
    }

    // Attribute the commit to the requesting user (the "sub" claim) for audit; the email is a stable noreply address
    // derived from it, since the token carries no verified email.
    private static (string Name, string Email) AuthorIdentity(ClaimsPrincipal user)
    {
        var subject = user.FindFirst("sub")?.Value;
        var name = string.IsNullOrWhiteSpace(subject) ? "sqlflow" : subject.Trim();
        var local = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray());
        if (string.IsNullOrWhiteSpace(local))
        {
            local = "sqlflow";
        }

        return (name, $"{local}@users.noreply.sqlflow");
    }

    private static string ComposeCommitMessage(string title, string? body)
    {
        var summary = title.Trim();
        return string.IsNullOrWhiteSpace(body) ? summary : $"{summary}\n\n{body.Trim()}";
    }

    /// <summary>The pull-request body with the preflight warnings appended as their own section, so the reviewer
    /// decides with them in view (an endpoint change on a revised flow is exactly what a review must catch). The
    /// requested body passes through unchanged when the preflight was clean.</summary>
    private static string? StampPreflightWarnings(string? body, IReadOnlyList<ProposalFinding> warnings)
    {
        if (warnings.Count == 0)
        {
            return body;
        }

        var section = "## SQLFlow preflight\n\n"
            + string.Join("\n", warnings.Select(f => $"- :warning: `{f.Path}`: {f.Message}"));
        return string.IsNullOrWhiteSpace(body) ? section : $"{body.Trim()}\n\n{section}";
    }

    private static ProblemHttpResult Problem(string detail, int statusCode, string title = "Invalid request")
        => TypedResults.Problem(detail: detail, statusCode: statusCode, title: title);
}
