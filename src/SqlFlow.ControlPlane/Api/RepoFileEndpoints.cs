using System.Text;
using LibGit2Sharp;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SqlFlow.Catalog;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;

namespace SqlFlow.ControlPlane.Api;

/// <summary>
/// One file of a synced repository as text, for the repo page's code window: what the repository holds at a path its
/// content listing (<see cref="RepoTreeEndpoints"/>) names, read where the listing reads it (the git source's branch tip
/// through the shared read-only clone, or the folder a local-path repo was synced from).
///
/// The read is bounded and shows only what a repository is for: the first <see cref="MaxTextBytes"/> of a text file, a
/// binary file reported without its bytes, and nothing outside the repository (a path climbing out of it, a file reached
/// through a link, git's own files). A file holding credentials rather than code is not shown either: one named as a
/// credential store (an environment file, a private key, a login file), and, in a local-path repo, one git ignores, which
/// never reaches the repository and is where credentials, data drops and build output sit. The route is closed to an
/// assistant run's delegated token, which reads metadata: the host maps it outside the read surface the assistant opens.
/// </summary>
public static class RepoFileEndpoints
{
    /// <summary>The most of a file returned as text. A flow, a mapping or a script is far smaller; a data file past it
    /// is shown in part and reported clipped, rather than shipping megabytes to an editor.</summary>
    internal const int MaxTextBytes = 1_048_576;

    /// <summary>How much of a file is looked at to tell binary from text: git's own rule, a NUL byte in the first 8,000
    /// bytes.</summary>
    private const int BinarySniffBytes = 8_000;

    /// <summary>The longest path a request may name.</summary>
    private const int MaxPathLength = 1_024;

    /// <summary>The names of files that hold credentials rather than code: package manager, network and database
    /// logins, git's stored credentials, and SSH keys.</summary>
    private static readonly HashSet<string> CredentialStoreNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".npmrc", ".pypirc", ".netrc", "_netrc", ".pgpass", ".git-credentials", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519",
    };

    /// <summary>The extensions of key and certificate stores and of password databases.</summary>
    private static readonly HashSet<string> CredentialStoreExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".kdbx",
    };

    public static RouteGroupBuilder MapRepoFileEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var repos = group.MapGroup("/repos").WithTags("Repositories");
        repos.MapGet("/{id:guid}/file", GetRepoFileAsync).WithName("GetRepositoryFile");

        return group;
    }

    /// <summary>
    /// The file at <paramref name="path"/> in one repository: its text (the first <see cref="MaxTextBytes"/> of it, said
    /// to be clipped past that), or, for a binary file, its size alone. A path that is not a repo-relative file path is a
    /// 400, a file holding credentials a 403 saying why, an unknown repo or a file the repository does not hold a 404.
    /// </summary>
    private static async Task<Results<Ok<RepoFileDto>, ProblemHttpResult>> GetRepoFileAsync(
        Guid id, string? path, CatalogDbContext db, ISecretResolver secrets, CancellationToken ct)
    {
        if (PathProblem(path) is { } wrong)
        {
            return RepoTreeEndpoints.BadRequest("Not a repository file", wrong);
        }

        var file = path!;
        if (CredentialStore(file) is { } store)
        {
            return NotShown($"'{file}' is {store}, which holds credentials rather than code, so its content is not shown.");
        }

        var (location, problem) = await RepoTreeEndpoints.LocateAsync(id, db, "read", ct).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var (read, refused) = location!.GitSource is { } source
            ? await ReadFromGitAsync(secrets, source, file, ct).ConfigureAwait(false)
            : await Task.Run(() => ReadFromDisk(location.RootPath!, file), ct).ConfigureAwait(false);
        if (refused is not null)
        {
            return refused;
        }

        return TypedResults.Ok(read!);
    }

    /// <summary>Why <paramref name="path"/> names no file of a repository, or null when it does: it must be repo-relative
    /// and forward-slashed as the content listing gives it, name no folder by <c>.</c>, <c>..</c> or an empty name, and
    /// name none of git's own files.</summary>
    internal static string? PathProblem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "Name the file with ?path=, repo-relative and forward-slashed (sales/orders.flow.yaml).";
        }

        if (path.Length > MaxPathLength)
        {
            return $"The path is {path.Length} characters long; a repository file's path is at most {MaxPathLength}.";
        }

        if (path.Any(char.IsControl))
        {
            return "The path holds a control character; name the file by its path in the repository.";
        }

        if (path.StartsWith('/') || path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal))
        {
            return $"'{path}' is not a repo-relative, forward-slashed path (sales/orders.flow.yaml).";
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment == "." || segment == "..")
            {
                return $"'{path}' names a folder by '.', '..' or an empty name; name the file by its path in the repository.";
            }

            if (RepoTreeEndpoints.GitMachinery.Contains(segment))
            {
                return $"'{path}' is git's own file ({segment}), not repository content.";
            }
        }

        return null;
    }

    /// <summary>What kind of credential store <paramref name="path"/> names, or null when its name is not one: an
    /// environment file (<c>.env</c>, <c>.env.local</c>), a login or key file, or a key or certificate store.</summary>
    internal static string? CredentialStore(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = path[(path.LastIndexOf('/') + 1)..];
        if (name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return "an environment file";
        }

        if (CredentialStoreNames.Contains(name))
        {
            return "a login or key file";
        }

        var extension = Path.GetExtension(name);
        return extension.Length > 0 && CredentialStoreExtensions.Contains(extension) ? "a key or certificate store" : null;
    }

    /// <summary>The file at the branch tip of a repo tracked by a git source, read from the shared read-only clone.</summary>
    private static async Task<(RepoFileDto? File, ProblemHttpResult? Problem)> ReadFromGitAsync(
        ISecretResolver secrets, CatalogRepoSource source, string path, CancellationToken ct)
    {
        try
        {
            var credentials = await GitMaterializer
                .ResolveCredentialsAsync(secrets, source.CredentialReference, source.CredentialUsername, source.RemoteUrl, GitAccess.Read, ct)
                .ConfigureAwait(false);

            // git and file IO are blocking, and a cold clone is slow; keep both off the request thread.
            return await Task.Run(
                () =>
                {
                    var workingDir = new GitMaterializer()
                        .EnsureHistoryClone(source.RemoteUrl!, source.Branch, credentials, RepoTreeEndpoints.TreeFreshness, ct);
                    using var repository = new Repository(workingDir);
                    return ReadFromRepository(repository, path, source.Branch);
                },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SqlFlowNodeException or SqlFlowException or LibGit2SharpException)
        {
            return (null, RepoTreeEndpoints.BadRequest("Contents unavailable", SecretHygiene.RedactedMessage(ex)));
        }
    }

    /// <summary>The file at the tip of <paramref name="repository"/>'s checked-out branch: its text, a 404 when the tip
    /// holds no file there (or the branch has no commit yet), and a 400 for a folder or a submodule.</summary>
    internal static (RepoFileDto? File, ProblemHttpResult? Problem) ReadFromRepository(Repository repository, string path, string branch)
    {
        ArgumentNullException.ThrowIfNull(repository);

        var tip = repository.Head.Tip;
        var entry = tip?[path];
        if (tip is null || entry is null)
        {
            return (null, NotFound($"Branch '{branch}' holds no file '{path}'."));
        }

        if (entry.TargetType != TreeEntryTargetType.Blob)
        {
            return (null, RepoTreeEndpoints.BadRequest(
                "Not a file",
                entry.TargetType == TreeEntryTargetType.Tree
                    ? $"'{path}' is a folder; name a file in it."
                    : $"'{path}' is a submodule, whose files belong to another repository."));
        }

        var blob = (Blob)entry.Target;
        using var stream = blob.GetContentStream();
        return (Text(path, "git", branch, tip.Sha, stream, blob.Size), null);
    }

    /// <summary>
    /// The file under a local-path repo's root on the control-plane host: its text, a 404 when the repository holds no
    /// file there, a 400 for a folder, for a path leaving the root, or for a file reached through a link (a link may lead
    /// out of the repository, so neither the file nor any folder on the way to it may be one), and a 403 for a file git
    /// ignores when the root is a git working tree.
    /// </summary>
    internal static (RepoFileDto? File, ProblemHttpResult? Problem) ReadFromDisk(string rootPath, string path)
    {
        ArgumentNullException.ThrowIfNull(rootPath);
        ArgumentNullException.ThrowIfNull(path);

        var root = Path.GetFullPath(rootPath);
        var full = Path.GetFullPath(Path.Join(root, path));
        var relative = Path.GetRelativePath(root, full);
        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return (null, RepoTreeEndpoints.BadRequest("Not a repository file", $"'{path}' is not inside the repository."));
        }

        var current = root;
        foreach (var segment in path.Split('/'))
        {
            current = Path.Join(current, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return (null, NotFound($"The repository holds no file '{path}'."));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                return (null, Unreadable(path, ex));
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                return (null, RepoTreeEndpoints.BadRequest(
                    "Not a repository file",
                    $"'{path}' is reached through a link, which may lead out of the repository, so its content is not shown."));
            }
        }

        if (Directory.Exists(full))
        {
            return (null, RepoTreeEndpoints.BadRequest("Not a file", $"'{path}' is a folder; name a file in it."));
        }

        if (IgnoredByGit(full))
        {
            return (null, NotShown(
                $"Git ignores '{path}', so it never reaches the repository; its content is not shown, since an ignored file is where credentials, data drops and build output sit."));
        }

        try
        {
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return (Text(path, "disk", null, null, stream, stream.Length), null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return (null, Unreadable(path, ex));
        }
    }

    /// <summary>
    /// Whether the git working tree holding <paramref name="fullPath"/>, if any, ignores it: an untracked file its ignore
    /// rules match. A tracked file is the repository's even where a pattern matches it. A folder in no working tree, or
    /// one whose git metadata cannot be opened, has no ignore rules to ask, and the name rules alone apply.
    /// </summary>
    private static bool IgnoredByGit(string fullPath)
    {
        var gitDir = Repository.Discover(Path.GetDirectoryName(fullPath) ?? fullPath);
        if (gitDir is null)
        {
            return false;
        }

        try
        {
            using var repository = new Repository(gitDir);
            if (repository.Info.WorkingDirectory is not { } workingDirectory)
            {
                return false;
            }

            var relative = Path.GetRelativePath(workingDirectory, fullPath).Replace('\\', '/');
            return !relative.StartsWith("..", StringComparison.Ordinal)
                && (repository.RetrieveStatus(relative) & FileStatus.Ignored) != 0;
        }
        catch (LibGit2SharpException)
        {
            return false;
        }
    }

    /// <summary>
    /// The first <see cref="MaxTextBytes"/> of a file as text, decoded as UTF-8 unless a byte order mark says otherwise,
    /// or, for a binary file (a NUL byte among its first 8,000 bytes, outside a UTF-16 text), its size alone. A text clipped
    /// at the cap drops a character the cut split rather than ending in a replacement mark.
    /// </summary>
    internal static RepoFileDto Text(string path, string readFrom, string? branch, string? commitSha, Stream stream, long size)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var buffer = new byte[(int)Math.Min(Math.Max(size, 0), MaxTextBytes)];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        if (LooksBinary(buffer.AsSpan(0, read)))
        {
            return new RepoFileDto(path, readFrom, branch, commitSha, size, Binary: true, Truncated: false, Text: null);
        }

        var truncated = size > read;
        using var memory = new MemoryStream(buffer, 0, read, writable: false);
        using var reader = new StreamReader(memory, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return new RepoFileDto(
            path, readFrom, branch, commitSha, size, Binary: false, truncated, truncated ? text.TrimEnd('�') : text);
    }

    /// <summary>Whether the start of a file reads as binary: a NUL byte among its first 8,000 bytes, unless it opens with
    /// a UTF-16 byte order mark, whose text holds a NUL in every ASCII character.</summary>
    internal static bool LooksBinary(ReadOnlySpan<byte> head)
    {
        ReadOnlySpan<byte> littleEndian = [0xFF, 0xFE];
        ReadOnlySpan<byte> bigEndian = [0xFE, 0xFF];
        if (head.StartsWith(littleEndian) || head.StartsWith(bigEndian))
        {
            return false;
        }

        return head[..Math.Min(head.Length, BinarySniffBytes)].Contains((byte)0);
    }

    private static ProblemHttpResult Unreadable(string path, Exception ex)
        => RepoTreeEndpoints.BadRequest(
            "Contents unavailable", $"The control-plane host cannot read '{path}': {SecretHygiene.RedactedMessage(ex)}");

    private static ProblemHttpResult NotShown(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status403Forbidden, title: "Content not shown");

    private static ProblemHttpResult NotFound(string detail)
        => TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status404NotFound, title: "Not found");
}
