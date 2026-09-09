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
/// What a synced repository actually HOLDS, as opposed to what the catalog imported from it. The catalog knows only
/// the flow files a sync selected, so a folder of SQL scripts, of documentation, or of flows excluded from the
/// import is invisible to every catalog-derived listing. This endpoint reads the repository itself, so the repo view
/// can show its real folder outline rather than only the folders that happen to hold a registered pipeline.
///
/// The read is bounded and content-free: paths, folder-or-file, and blob sizes. No file body is served here (a flow's
/// YAML is read through the pipeline detail, an scm snapshot through the schema-change diff), and no client ever
/// holds a git credential: the repo's registered source supplies the remote and its credential REFERENCE, resolved
/// through the same <see cref="GitMaterializer.ResolveCredentialsAsync"/> path the managed sync uses.
/// </summary>
public static class RepoTreeEndpoints
{
    /// <summary>How stale the read-only clone may be before a listing refreshes it. Matches the history surface, so
    /// a folder pushed moments ago shows up while a burst of browsing costs one fetch at most.</summary>
    private static readonly TimeSpan TreeFreshness = TimeSpan.FromMinutes(1);

    /// <summary>The most entries one listing returns. A repo of flows runs to a few thousand files; the cap keeps a
    /// repository that also carries a data dump from turning a folder outline into a megabyte response, and the
    /// clipping is reported rather than silent.</summary>
    private const int MaxEntries = 20_000;

    public static RouteGroupBuilder MapRepoTreeEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapGet("/{id:guid}/tree", GetRepoTreeAsync).WithName("GetRepositoryTree");

        return group;
    }

    /// <summary>
    /// Every folder and file in one repository, repo-relative and forward-slashed, sorted by path. A repo tracked by
    /// a registered git source is read from its branch tip through the shared read-only clone; a repo synced by the
    /// CLI from a local path is read from that path when the control-plane host can see it. Neither is reachable for
    /// a repo with no source and no visible root path, which is reported as a 400 rather than as an empty tree that
    /// would read as "this repository holds nothing".
    /// </summary>
    private static async Task<Results<Ok<RepoTreeDto>, ProblemHttpResult>> GetRepoTreeAsync(
        Guid id, CatalogDbContext db, ISecretResolver secrets, CancellationToken ct)
    {
        var repo = await db.Repos.AsNoTracking().Where(r => r.Id == id)
            .Select(r => new { r.Name, r.RootPath })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (repo is null)
        {
            return NotFound("repository", id);
        }

        var source = await db.RepoSources.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Name == repo.Name, ct).ConfigureAwait(false);
        if (source is not null)
        {
            return await ReadFromGitAsync(secrets, source, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(repo.RootPath))
        {
            return BadRequest(
                "Contents unavailable",
                $"Repo '{repo.Name}' has no registered git source and no recorded root path, so its files cannot be listed.");
        }

        if (!Directory.Exists(repo.RootPath))
        {
            return BadRequest(
                "Contents unavailable",
                $"The control-plane host cannot see the repo's root path '{repo.RootPath}', so its files cannot be listed. A repo synced from a local path is browsable only where the flow files live.");
        }

        var rootPath = repo.RootPath;
        var entries = await Task.Run(() => ReadFromDisk(rootPath, ct), ct).ConfigureAwait(false);
        return TypedResults.Ok(Listing("disk", entries));
    }

    /// <summary>The branch tip's tree of a repo tracked by a git source, read from the shared read-only clone.</summary>
    private static async Task<Results<Ok<RepoTreeDto>, ProblemHttpResult>> ReadFromGitAsync(
        ISecretResolver secrets, CatalogRepoSource source, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, source.CredentialReference, source.CredentialUsername, ct)
                .ConfigureAwait(false);

            // git and file IO are blocking, and a cold clone is slow; keep both off the request thread.
            var entries = await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(source.RemoteUrl, source.Branch, credentials, TreeFreshness, ct);
                    using var repository = new Repository(workingDir);
                    var found = new List<RepoTreeEntryDto>();
                    if (repository.Head.Tip is { } tip)
                    {
                        Walk(tip.Tree, prefix: string.Empty, found, ct);
                    }

                    return found;
                },
                ct).ConfigureAwait(false);

            return TypedResults.Ok(Listing("git", entries));
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return BadRequest("Contents unavailable", SecretHygiene.RedactedMessage(ex));
        }
    }

    /// <summary>Depth-first walk of a commit's tree, emitting a folder before its contents so the outline can be
    /// rebuilt from the paths alone. Stops at <see cref="MaxEntries"/>; submodules (git links) are not entered, since
    /// their contents belong to another repository.</summary>
    private static void Walk(Tree tree, string prefix, List<RepoTreeEntryDto> into, CancellationToken ct)
    {
        foreach (var entry in tree)
        {
            if (into.Count >= MaxEntries)
            {
                return;
            }

            ct.ThrowIfCancellationRequested();
            var path = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            switch (entry.TargetType)
            {
                case TreeEntryTargetType.Tree:
                    into.Add(new RepoTreeEntryDto(path, IsFolder: true, SizeBytes: 0));
                    Walk((Tree)entry.Target, path, into, ct);
                    break;
                case TreeEntryTargetType.Blob:
                    into.Add(new RepoTreeEntryDto(path, IsFolder: false, ((Blob)entry.Target).Size));
                    break;
                default:
                    break;
            }
        }
    }

    /// <summary>The same listing for a repo synced from a local path: the working tree on disk, minus git's own
    /// metadata folder, which is machinery rather than repository content.</summary>
    private static List<RepoTreeEntryDto> ReadFromDisk(string rootPath, CancellationToken ct)
    {
        var found = new List<RepoTreeEntryDto>();
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0 && found.Count < MaxEntries)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                // A folder the host may not read (or one that vanished mid-walk) is skipped rather than failing the
                // whole listing: the rest of the repository is still an honest answer.
                continue;
            }

            foreach (var child in children)
            {
                if (found.Count >= MaxEntries)
                {
                    break;
                }

                var isFolder = Directory.Exists(child);
                if (isFolder && string.Equals(Path.GetFileName(child), ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(rootPath, child).Replace('\\', '/');
                if (isFolder)
                {
                    found.Add(new RepoTreeEntryDto(relative, IsFolder: true, SizeBytes: 0));
                    pending.Push(child);
                }
                else
                {
                    found.Add(new RepoTreeEntryDto(relative, IsFolder: false, FileSize(child)));
                }
            }
        }

        return found;
    }

    /// <summary>A file's size on disk, or 0 when the host cannot stat it. A size is a browsing aid, so an unreadable
    /// one drops the number rather than the file.</summary>
    private static long FileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Sorts a walk into one path-ordered listing and reports whether the cap clipped it.</summary>
    private static RepoTreeDto Listing(string readFrom, List<RepoTreeEntryDto> entries)
    {
        var truncated = entries.Count >= MaxEntries;
        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return new RepoTreeDto(readFrom, entries, truncated);
    }

    private static ProblemHttpResult NotFound(string resource, Guid id)
        => TypedResults.Problem(
            detail: $"No {resource} with id '{id}'.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Not found");

    private static ProblemHttpResult BadRequest(string title, string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);
}
