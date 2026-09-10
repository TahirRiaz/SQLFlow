using System.Text.Json;
using LibGit2Sharp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// The change-history read surface, over the two things this estate versions and the two questions people
/// actually ask of them:
///
/// <list type="bullet">
/// <item><b>Flow definitions</b> (<c>/flow-history</c>): how a pipeline's YAML has been edited over time, from the
/// git history of the repo the estate syncs. "Who changed this flow's merge keys, and when."</item>
/// <item><b>Managed database schemas</b> (<c>/schema-changes</c>, mapped in CatalogEndpoints, plus the DDL diff
/// and the window comparison here): which database objects a source-control snapshot found added, changed, or
/// dropped, what one snapshot's patch was, and what an object's whole script looked like before and after a
/// window. "When did this column appear on this table, and how does it read now against then."</item>
/// </list>
///
/// They are deliberately separate surfaces because they answer different questions about different artifacts: one
/// is about the ETL code, the other about the databases it runs against. A caller that conflates them gets a
/// confidently wrong answer, so neither endpoint accepts the other's identifiers.
///
/// Credentials never leave the control plane. A caller names a repo or a flow; the credential reference stored
/// against it is resolved here through the same <see cref="GitMaterializer.ResolveCredentialsAsync"/> path the
/// managed sync uses, so no client (the GUI, the MCP server, a script) ever holds a git token. Only repositories
/// the estate already manages are reachable: a registered repo source, or the repository an scm flow declares.
/// An arbitrary remote URL is not an addressable target.
/// </summary>
public static class GitHistoryEndpoints
{
    /// <summary>How stale the read-only history clone may be before a request refreshes it. A minute keeps a
    /// burst of related queries (a log, then a diff of one of its commits) on one fetch while still reflecting a
    /// push made moments ago.</summary>
    private static readonly TimeSpan HistoryFreshness = TimeSpan.FromMinutes(1);

    /// <summary>The most commits one query returns. A history read is a browsing aid, not a bulk export.</summary>
    private const int MaxCommits = 200;

    /// <summary>How many commits one query walks before giving up on finding more matches. Bounds the cost of a
    /// filter that matches nothing recent (a folder untouched for years) without truncating a normal answer.</summary>
    private const int MaxScannedCommits = 5_000;

    /// <summary>The largest patch returned inline. A diff past this is reported truncated rather than streamed:
    /// a generated snapshot of a large table can run to megabytes and nothing useful reads it as one blob.</summary>
    private const int MaxPatchChars = 200_000;

    /// <summary>The largest object script returned inline, per side of a comparison. A scripted table or module is
    /// small, but a data snapshot of a reference table is not, and nothing useful reads megabytes of INSERTs in a
    /// diff pane; an oversized side is clipped and reported clipped rather than silently shortened.</summary>
    private const int MaxObjectChars = 200_000;

    public static RouteGroupBuilder MapGitHistoryEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var flows = group.MapGroup("/flow-history").WithTags("FlowHistory");
        flows.MapGet("/commits", ListFlowCommitsAsync).WithName("ListFlowDefinitionCommits");
        flows.MapGet("/flows/{pipelineId:guid}", GetFlowFileHistoryAsync).WithName("GetFlowDefinitionHistory");
        flows.MapGet("/diff", GetFlowDiffAsync).WithName("GetFlowDefinitionDiff");

        var schema = group.MapGroup("/schema-changes").WithTags("SchemaChanges");
        schema.MapGet("/ddl", GetSchemaObjectDdlAsync).WithName("GetSchemaObjectDdl");
        schema.MapGet("/{id:long}/compare", CompareSchemaObjectAsync).WithName("CompareSchemaObject");

        return group;
    }

    // ---- Flow definitions (the pipeline YAML) --------------------------------------------------------------

    /// <summary>
    /// Commits that changed pipeline YAML in one synced repository, newest first, filterable by path prefix,
    /// author, date, and message text. This is the ETL code's history, not the databases'.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> ListFlowCommitsAsync(
        CatalogDbContext db, ISecretResolver secrets, Guid? repoId, string? path, string? author, string? message,
        DateTime? since, DateTime? until, int? limit, CancellationToken ct)
    {
        var source = await ResolveSourceAsync(db, repoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest(
                "No repository",
                "No git-synced repository matched. Pass repoId, or register a repo source first; only repositories this estate syncs are readable.");
        }

        return await ReadAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            repo => Log(repo, path, author, message, since, until, limit), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One flow's definition history: every commit that touched THAT flow's YAML file, newest first. The file is
    /// the pipeline's own recorded path, so a caller needs only the pipeline id it already has.
    /// </summary>
    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> GetFlowFileHistoryAsync(
        Guid pipelineId, CatalogDbContext db, ISecretResolver secrets, int? limit, CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.RepoId, p.RelativePath, p.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        var source = await ResolveSourceAsync(db, pipeline.RepoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest(
                "Repository not git-synced",
                $"Flow '{pipeline.Name}' belongs to a repo with no registered git source, so it has no commit history to read.");
        }

        return await ReadAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            repo => Log(repo, pipeline.RelativePath, author: null, message: null, since: null, until: null, limit),
            ct).ConfigureAwait(false);
    }

    /// <summary>The patch one commit applied, optionally narrowed to a single file: what actually changed in the
    /// YAML, not just that it changed.</summary>
    private static async Task<Results<Ok<GitDiffDto>, ProblemHttpResult>> GetFlowDiffAsync(
        CatalogDbContext db, ISecretResolver secrets, Guid? repoId, string sha, string? path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha))
        {
            return BadRequest("Missing commit", "Pass the commit sha to diff (from /flow-history/commits).");
        }

        var source = await ResolveSourceAsync(db, repoId, ct).ConfigureAwait(false);
        if (source is null)
        {
            return BadRequest("No repository", "No git-synced repository matched. Pass repoId.");
        }

        return await ReadDiffAsync(
            secrets, source.RemoteUrl, source.Branch, source.CredentialReference, source.CredentialUsername,
            sha, path, ct).ConfigureAwait(false);
    }

    // ---- Managed database schemas (the snapshot repository) ------------------------------------------------

    /// <summary>
    /// The DDL a source-control snapshot recorded for one database object at one commit: the actual
    /// <c>CREATE TABLE</c> / <c>CREATE VIEW</c> text that changed, as a patch. The commit and path come from a
    /// schema-change row, so this is the drill-down behind "this object changed", never a way to browse a
    /// database.
    /// </summary>
    private static async Task<Results<Ok<GitDiffDto>, ProblemHttpResult>> GetSchemaObjectDdlAsync(
        CatalogDbContext db, ISecretResolver secrets, Guid pipelineId, string sha, string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sha) || string.IsNullOrWhiteSpace(path))
        {
            return BadRequest(
                "Missing commit or path",
                "Pass the snapshot's commitSha and the object's repository path; both come from a /schema-changes row.");
        }

        return await WithSnapshotRepositoryAsync<GitDiffDto>(
            db,
            pipelineId,
            repository => ReadDiffAsync(
                secrets, repository.Remote, repository.Branch, repository.Secret, repository.Username, sha, path, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One database object's whole script at the two ends of a window: what the snapshot repository held when the
    /// window opened, against what it holds now. This is the "what actually changed" behind a schema-change row,
    /// as the two texts rather than a replay of every snapshot between them, so a table four snapshots touched
    /// still reads as one before and one after.
    ///
    /// The row's id carries the object's identity, so the caller names no path: the repository layout is derived
    /// here from the same convention the snapshot writer emits, and a client cannot address a file the schema
    /// history does not know about.
    /// </summary>
    private static async Task<Results<Ok<SchemaObjectCompareDto>, ProblemHttpResult>> CompareSchemaObjectAsync(
        long id, CatalogDbContext db, ISecretResolver secrets, DateTime? since, CancellationToken ct)
    {
        var change = await db.SchemaChanges.AsNoTracking()
            .Where(c => c.Id == id)
            .Select(c => new { c.PipelineId, c.Database, c.Category, c.Schema, c.Name })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (change is null)
        {
            return NotFound("schema change", id);
        }

        if (change.PipelineId is not { } pipelineId)
        {
            return BadRequest(
                "No snapshot flow",
                "This change was recorded before its snapshot flow reached the catalog, so the repository it was committed to cannot be resolved. A later snapshot of the same database records one that can.");
        }

        var path = CatalogProjection.SnapshotPath(change.Database, change.Category, change.Schema, change.Name);
        return await WithSnapshotRepositoryAsync<SchemaObjectCompareDto>(
            db,
            pipelineId,
            repository => ReadCompareAsync(secrets, repository, path, since, ct),
            ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the snapshot repository an scm flow commits to and hands its coordinates to <paramref name="read"/>.
    /// Both schema drill-downs (one snapshot's patch, and the window comparison) come through here, so what makes a
    /// pipeline readable (it exists, it is an scm flow, and it records a remote) is stated once and answered the
    /// same way for both.
    /// </summary>
    /// <typeparam name="T">The DTO the caller's read returns.</typeparam>
    private static async Task<Results<Ok<T>, ProblemHttpResult>> WithSnapshotRepositoryAsync<T>(
        CatalogDbContext db,
        Guid pipelineId,
        Func<(string Remote, string Branch, string? Username, string? Secret), Task<Results<Ok<T>, ProblemHttpResult>>> read,
        CancellationToken ct)
    {
        var pipeline = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == pipelineId)
            .Select(p => new { p.Kind, p.Name, p.DefinitionJson })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (pipeline is null)
        {
            return NotFound("pipeline", pipelineId);
        }

        if (!string.Equals(pipeline.Kind, "scm", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(
                "Not a snapshot flow",
                $"Flow '{pipeline.Name}' is a '{pipeline.Kind}' flow. Object DDL lives in the repository a source-control (scm) flow writes; use /flow-history/diff for a pipeline's own YAML.");
        }

        if (ReadSnapshotRepository(pipeline.DefinitionJson) is not { } repository)
        {
            return BadRequest(
                "No remote recorded",
                $"Flow '{pipeline.Name}' declares no repository remote in the catalog, so its snapshots are local to the node that ran them and cannot be read back here.");
        }

        return await read(repository).ConfigureAwait(false);
    }

    /// <summary>Pulls the repository coordinates out of an scm flow's stored definition. The secret is a
    /// <c>${...}</c> reference (the loader rejects literals), so what is stored and resolved here is a pointer,
    /// never a credential.</summary>
    internal static (string Remote, string Branch, string? Username, string? Secret)? ReadSnapshotRepository(string? definitionJson)
    {
        if (string.IsNullOrWhiteSpace(definitionJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            if (!document.RootElement.TryGetProperty("flow", out var flow)
                || !flow.TryGetProperty("repository", out var repository)
                || repository.TryGetProperty("remote", out var remote) is false
                || remote.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var url = remote.GetString();
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            return (
                url,
                Str(repository, "branch") ?? "main",
                Str(repository, "username"),
                Str(repository, "secret"));
        }
        catch (JsonException)
        {
            return null;
        }

        static string? Str(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    // ---- The shared git read path --------------------------------------------------------------------------

    /// <summary>Resolves which registered git source backs a repo. With no repoId, the estate's single source is
    /// used; with several, the caller must say which.</summary>
    private static async Task<CatalogRepoSource?> ResolveSourceAsync(CatalogDbContext db, Guid? repoId, CancellationToken ct)
    {
        if (repoId is { } id)
        {
            var name = await db.Repos.AsNoTracking().Where(r => r.Id == id).Select(r => r.Name)
                .FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return name is null
                ? null
                : await db.RepoSources.AsNoTracking().FirstOrDefaultAsync(s => s.Name == name, ct).ConfigureAwait(false);
        }

        var sources = await db.RepoSources.AsNoTracking().Take(2).ToListAsync(ct).ConfigureAwait(false);
        return sources.Count == 1 ? sources[0] : null;
    }

    private static async Task<Results<Ok<IReadOnlyList<GitCommitDto>>, ProblemHttpResult>> ReadAsync(
        ISecretResolver secrets, string remoteUrl, string branch, string? credentialReference, string? username,
        Func<Repository, IReadOnlyList<GitCommitDto>> read, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, credentialReference, username, ct).ConfigureAwait(false);

            // git and file IO are blocking, and a cold clone is slow; keep both off the request thread.
            var commits = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(remoteUrl, branch, credentials, HistoryFreshness, ct);
                    using var repo = new Repository(workingDir);
                    return read(repo);
                },
                ct).ConfigureAwait(false);

            return TypedResults.Ok(commits);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("History unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    private static async Task<Results<Ok<GitDiffDto>, ProblemHttpResult>> ReadDiffAsync(
        ISecretResolver secrets, string remoteUrl, string branch, string? credentialReference, string? username,
        string sha, string? path, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, credentialReference, username, ct).ConfigureAwait(false);

            var diff = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(remoteUrl, branch, credentials, HistoryFreshness, ct);
                    using var repo = new Repository(workingDir);
                    return Diff(repo, sha, path);
                },
                ct).ConfigureAwait(false);

            return diff is null
                ? BadRequest("Unknown commit", $"Commit '{sha}' is not in this repository's history.")
                : TypedResults.Ok(diff);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("Diff unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    private static async Task<Results<Ok<SchemaObjectCompareDto>, ProblemHttpResult>> ReadCompareAsync(
        ISecretResolver secrets, (string Remote, string Branch, string? Username, string? Secret) repository,
        string path, DateTime? since, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, repository.Secret, repository.Username, ct).ConfigureAwait(false);

            var comparison = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(repository.Remote, repository.Branch, credentials, HistoryFreshness, ct);
                    using var repo = new Repository(workingDir);
                    return Compare(repo, path, AsUtc(since));
                },
                ct).ConfigureAwait(false);

            return comparison is null
                ? BadRequest(
                    "No snapshot history",
                    $"Branch '{repository.Branch}' of the snapshot repository carries no commits, so there is nothing to compare '{path}' against.")
                : TypedResults.Ok(comparison);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("Comparison unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>
    /// One object's script at the two ends of a window. The "after" side is the branch tip, the state the last
    /// snapshot left; the "before" side is the newest commit at or before <paramref name="since"/>, the state the
    /// window opened on. With no <paramref name="since"/> (an all-time window) there is no earlier side at all and
    /// the whole script reads as added, which is exactly what "since this estate began snapshotting" means.
    ///
    /// The base is found by walking commits newest-first rather than by filtering the whole history by date: the
    /// first commit old enough IS the answer, so the walk stops on it, bounded by <see cref="MaxScannedCommits"/>
    /// so one comparison can never turn into a full history read. Returns null when the branch carries no commits,
    /// which leaves nothing to compare rather than an empty answer that would read as "nothing changed".
    /// </summary>
    internal static SchemaObjectCompareDto? Compare(Repository repo, string path, DateTime? since)
    {
        if (repo.Head.Tip is not { } head)
        {
            return null;
        }

        var normalized = path.Replace('\\', '/').TrimStart('/');
        var baseCommit = since is { } from ? NewestAtOrBefore(repo, from) : null;
        var (beforeText, beforeTruncated) = ReadObject(baseCommit?.Tree, normalized);
        var (afterText, afterTruncated) = ReadObject(head.Tree, normalized);
        var patch = repo.Diff.Compare<Patch>(
            baseCommit?.Tree, head.Tree, [normalized], new CompareOptions { ContextLines = 3 });

        return new SchemaObjectCompareDto(
            normalized,
            baseCommit is null ? null : Revision(baseCommit),
            Revision(head),
            beforeText,
            afterText,
            patch.LinesAdded,
            patch.LinesDeleted,
            beforeTruncated || afterTruncated);
    }

    /// <summary>The newest commit no later than an instant: the repository's state as the window opened. Null when
    /// every commit in reach is newer, which means the history itself begins inside the window.</summary>
    private static Commit? NewestAtOrBefore(Repository repo, DateTime instantUtc)
    {
        var scanned = 0;
        foreach (var commit in repo.Commits)
        {
            if (++scanned > MaxScannedCommits)
            {
                return null;
            }

            if (commit.Author.When.UtcDateTime <= instantUtc)
            {
                return commit;
            }
        }

        return null;
    }

    /// <summary>One object's script at one revision, clipped at the inline limit. A null text (rather than an
    /// empty one) says the file was not in that tree at all, which is how an object added or dropped inside the
    /// window is told apart from one whose script happens to be empty.</summary>
    private static (string? Text, bool Truncated) ReadObject(Tree? tree, string path)
    {
        if (tree?[path]?.Target is not Blob blob)
        {
            return (null, false);
        }

        var text = blob.GetContentText();
        return text.Length > MaxObjectChars ? (text[..MaxObjectChars], true) : (text, false);
    }

    private static GitRevisionDto Revision(Commit commit)
        => new(
            commit.Sha,
            commit.Sha[..Math.Min(8, commit.Sha.Length)],
            commit.Author.Name,
            commit.Author.When.UtcDateTime,
            commit.MessageShort.Trim());

    /// <summary>A query-bound instant as UTC. Minimal-API binding parses a trailing 'Z' into a local-kind
    /// DateTime, so comparing it straight against a commit's UTC timestamp would move the window boundary by the
    /// host's offset; an unspecified kind is read as local time, the usual reading of a bare date.</summary>
    private static DateTime? AsUtc(DateTime? value)
        => value is not { } instant
            ? null
            : instant.Kind == DateTimeKind.Utc ? instant : instant.ToUniversalTime();

    /// <summary>
    /// The commit log, newest first, filtered. Path matching is done against each commit's own changed-file set
    /// rather than through LibGit2Sharp's <c>Commits.QueryBy</c>: that API resolves an exact blob path only (a
    /// folder prefix matches nothing) and throws on some merge topologies, whereas an estate browses by folder
    /// ("what changed under citybike") as often as by file.
    ///
    /// The walk is bounded by <see cref="MaxScannedCommits"/>, so a path that has not been touched in a very long
    /// time stops the search rather than reading an entire repository's history on one request.
    /// </summary>
    internal static IReadOnlyList<GitCommitDto> Log(
        Repository repo, string? path, string? author, string? message, DateTime? since, DateTime? until, int? limit)
    {
        var take = Math.Clamp(limit ?? 50, 1, MaxCommits);
        var prefix = string.IsNullOrWhiteSpace(path) ? null : path.Replace('\\', '/').Trim('/');

        var results = new List<GitCommitDto>(take);
        var scanned = 0;
        foreach (var commit in repo.Commits)
        {
            if (results.Count >= take || ++scanned > MaxScannedCommits)
            {
                break;
            }

            var when = commit.Author.When.UtcDateTime;
            if ((since is { } from && when < from) || (until is { } to && when > to))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(author)
                && !commit.Author.Name.Contains(author, StringComparison.OrdinalIgnoreCase)
                && !commit.Author.Email.Contains(author, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(message)
                && !commit.Message.Contains(message, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var changed = ChangedPaths(repo, commit);
            if (prefix is not null && !changed.Any(p => Touches(p, prefix)))
            {
                continue;
            }

            results.Add(new GitCommitDto(
                commit.Sha,
                commit.Sha[..Math.Min(8, commit.Sha.Length)],
                commit.Author.Name,
                commit.Author.Email,
                when,
                commit.MessageShort.Trim(),
                prefix is null ? changed : changed.Where(p => Touches(p, prefix)).ToList()));
        }

        return results;
    }

    /// <summary>True when a changed path IS the filter or lives under it as a folder. Compared case-insensitively:
    /// the repositories here are authored on Windows and read on Linux, so a case-sensitive match would silently
    /// return nothing for a correctly-spelled folder.</summary>
    private static bool Touches(string changedPath, string prefix)
        => changedPath.Equals(prefix, StringComparison.OrdinalIgnoreCase)
           || changedPath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The paths a commit changed against its first parent; empty for a root commit's whole tree, which
    /// is reported as the tree itself rather than pretending nothing changed.</summary>
    private static IReadOnlyList<string> ChangedPaths(Repository repo, Commit commit)
    {
        var parent = commit.Parents.FirstOrDefault();
        var changes = repo.Diff.Compare<TreeChanges>(parent?.Tree, commit.Tree);
        return changes.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal).Take(MaxCommits).ToList();
    }

    internal static GitDiffDto? Diff(Repository repo, string sha, string? path)
    {
        if (repo.Lookup<Commit>(sha) is not { } commit)
        {
            return null;
        }

        var parent = commit.Parents.FirstOrDefault();
        var compareOptions = new CompareOptions { ContextLines = 3 };
        var patch = string.IsNullOrWhiteSpace(path)
            ? repo.Diff.Compare<Patch>(parent?.Tree, commit.Tree, compareOptions)
            : repo.Diff.Compare<Patch>(
                parent?.Tree, commit.Tree, [path.Replace('\\', '/').TrimStart('/')], compareOptions);

        var text = patch.Content ?? string.Empty;
        var truncated = text.Length > MaxPatchChars;
        return new GitDiffDto(
            commit.Sha,
            commit.Author.Name,
            commit.Author.When.UtcDateTime,
            commit.MessageShort.Trim(),
            path,
            patch.LinesAdded,
            patch.LinesDeleted,
            truncated ? text[..MaxPatchChars] : text,
            truncated);
    }

    private static ProblemHttpResult NotFound(string resource, long id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id.ToString(System.Globalization.CultureInfo.InvariantCulture)}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.", statusCode: StatusCodes.Status404NotFound, title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
