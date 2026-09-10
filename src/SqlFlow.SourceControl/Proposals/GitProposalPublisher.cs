using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;

namespace SqlFlow.SourceControl.Proposals;

/// <summary>One file a proposal writes into the repository, by repo-relative path and full content. The content
/// replaces the file when it already exists on the base branch, so a proposal can add new flows and revise existing
/// ones in the same branch.</summary>
public sealed record ProposalFile(string Path, string Content);

/// <summary>The work of publishing a proposal branch: the remote and base branch to fork from, the feature branch to
/// create, the commit identity and message, the push credential, and the files to write. The secret lives in memory
/// only for the clone/push.</summary>
public sealed record ProposalPublishRequest(
    string RemoteUrl, string BaseBranch, string HeadBranch, string CommitMessage,
    string AuthorName, string AuthorEmail, string? Username, string Secret,
    IReadOnlyList<ProposalFile> Files);

/// <summary>A published proposal branch: the commit that was pushed, the branch it landed on, and how many files it
/// changed. The commit SHA is what a commit-pinned run materializes to test the proposal before it merges.</summary>
public sealed record ProposalPublishResult(string CommitSha, string HeadBranch, int FilesChanged);

/// <summary>Identifies a remote branch for a best-effort cleanup (deleting a pushed proposal branch when opening the
/// pull request afterwards fails), with the credential to authenticate the delete-push.</summary>
public sealed record RemoteBranchRef(string RemoteUrl, string HeadBranch, string? Username, string Secret);

/// <summary>Publishes a proposal as a fresh feature branch on a git remote: clone the base branch, branch off it,
/// write the proposed files, commit under the requesting user's identity, and push the branch (create/fast-forward
/// only, never a force). It never touches the base branch, so a proposal is always a reviewable branch a human opens
/// a pull request from; it is a distinct operation from the snapshot-commit path in <see cref="LibGit2GitWorkspace"/>
/// (which commits a scripted database subtree to the working branch).</summary>
public interface IGitProposalPublisher
{
    /// <summary>Clones, branches, writes, commits, and pushes the proposal. Throws
    /// <see cref="SqlFlowException"/> when nothing changed (an empty proposal) or the git operation fails.</summary>
    Task<ProposalPublishResult> PublishAsync(ProposalPublishRequest request, CancellationToken ct = default);

    /// <summary>Best-effort deletion of a pushed proposal branch, used to roll back when opening the pull request
    /// fails after the branch is already on the remote. Never throws: a branch that cannot be deleted is left for
    /// the operator, which is strictly better than surfacing a cleanup error over the real failure.</summary>
    Task TryDeleteRemoteBranchAsync(RemoteBranchRef branch, CancellationToken ct = default);
}

/// <inheritdoc cref="IGitProposalPublisher"/>
public sealed class GitProposalPublisher : IGitProposalPublisher
{
    private readonly string _workRoot;

    /// <summary>Where proposal clones are staged, shared by the publisher and the process-lifetime sweep. Each
    /// publish uses its own throwaway sub-directory and deletes it when done; the sweep clears the whole root on
    /// process start and stop, so a staged clone never outlives the control-plane session (and a crash leak is
    /// reclaimed on the next start).</summary>
    public static string DefaultWorkRoot { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlflow", "proposals");

    /// <param name="workRoot">Where proposal clones are staged; defaults to <see cref="DefaultWorkRoot"/>. Each
    /// publish uses its own throwaway sub-directory and deletes it when done.</param>
    public GitProposalPublisher(string? workRoot = null)
        => _workRoot = workRoot ?? DefaultWorkRoot;

    /// <summary>Deletes every staged proposal clone under <paramref name="workRoot"/> (default
    /// <see cref="DefaultWorkRoot"/>), handling the read-only objects a git working tree keeps under <c>.git</c>.
    /// Best-effort and safe when the root does not exist. Used to bound the staged clones to the control-plane
    /// process lifetime.</summary>
    public static void ClearWorkRoot(string? workRoot = null)
        => TryDeleteDirectory(workRoot ?? DefaultWorkRoot);

    public Task<ProposalPublishResult> PublishAsync(ProposalPublishRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.RemoteUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BaseBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.HeadBranch);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Secret);
        if (request.Files is null || request.Files.Count == 0)
        {
            throw new SqlFlowException("a proposal must include at least one file to write.");
        }

        // Git and file IO are blocking; run the whole publish off the caller's thread.
        return Task.Run(() => PublishCore(request, ct), ct);
    }

    public Task TryDeleteRemoteBranchAsync(RemoteBranchRef branch, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return Task.Run(
            () =>
            {
                var workdir = NewWorkdir();
                try
                {
                    var options = new CloneOptions { Checkout = false };
                    options.FetchOptions.CredentialsProvider = CredentialsProvider(branch.Username, branch.Secret);
                    Repository.Clone(branch.RemoteUrl, workdir, options);

                    using var repo = new Repository(workdir);
                    var origin = repo.Network.Remotes["origin"];
                    if (origin is null)
                    {
                        return;
                    }

                    // An empty source with a destination ref deletes the remote branch.
                    var refSpec = $":refs/heads/{branch.HeadBranch}";
                    repo.Network.Push(origin, refSpec, new PushOptions { CredentialsProvider = CredentialsProvider(branch.Username, branch.Secret) });
                }
                catch (LibGit2SharpException)
                {
                    // Best-effort: a branch we could not delete is left for the operator to clean up.
                }
                finally
                {
                    TryDeleteDirectory(workdir);
                }
            },
            ct);
    }

    private ProposalPublishResult PublishCore(ProposalPublishRequest request, CancellationToken ct)
    {
        var workdir = NewWorkdir();
        try
        {
            var credentials = CredentialsProvider(request.Username, request.Secret);
            var cloneOptions = new CloneOptions { BranchName = request.BaseBranch };
            cloneOptions.FetchOptions.CredentialsProvider = credentials;
            Repository.Clone(request.RemoteUrl, workdir, cloneOptions);

            using var repo = new Repository(workdir);
            if (repo.Head.Tip is null)
            {
                throw new SqlFlowException(
                    $"the base branch '{request.BaseBranch}' has no commits to branch a proposal from.");
            }

            // The feature branch forks from the base tip. A name that already exists locally in the fresh clone
            // (a remote branch of the same name) is reused, so the subsequent push carries whatever we commit.
            var branch = repo.Branches[request.HeadBranch] ?? repo.CreateBranch(request.HeadBranch);
            Commands.Checkout(repo, branch);

            ct.ThrowIfCancellationRequested();
            WriteFiles(workdir, request.Files);

            var changed = repo
                .RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true })
                .Where(e => e.State is not (FileStatus.Ignored or FileStatus.Unaltered))
                .Select(e => e.FilePath)
                .ToList();
            if (changed.Count == 0)
            {
                throw new SqlFlowException(
                    "the proposed files are identical to the base branch; there is nothing to propose.");
            }

            Commands.Stage(repo, changed);
            var signature = new Signature(request.AuthorName, request.AuthorEmail, DateTimeOffset.Now);
            var commit = repo.Commit(request.CommitMessage, signature, signature, new CommitOptions());

            var origin = repo.Network.Remotes["origin"]
                ?? throw new SqlFlowException("the cloned repository has no 'origin' remote to push the proposal to.");

            // Create or fast-forward the remote feature branch; no leading '+', so a branch that already exists and
            // diverges is rejected by the server rather than force-overwritten.
            var refSpec = $"refs/heads/{request.HeadBranch}:refs/heads/{request.HeadBranch}";
            repo.Network.Push(origin, refSpec, new PushOptions { CredentialsProvider = credentials });

            return new ProposalPublishResult(commit.Sha, request.HeadBranch, changed.Count);
        }
        catch (LibGit2SharpException ex)
        {
            throw new SqlFlowException(
                $"could not publish the proposal branch '{request.HeadBranch}': {SecretHygiene.RedactedMessage(ex)}", ex);
        }
        finally
        {
            TryDeleteDirectory(workdir);
        }
    }

    private static void WriteFiles(string workdir, IReadOnlyList<ProposalFile> files)
    {
        var root = System.IO.Path.GetFullPath(workdir);
        foreach (var file in files)
        {
            var relative = NormalizeRelative(file.Path);
            var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));

            // Defense in depth against a traversal that escaped the endpoint's validation: never write outside the
            // clone, and never into the .git directory.
            if (!full.StartsWith(root + System.IO.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                throw new SqlFlowException($"the proposal path '{file.Path}' resolves outside the repository.");
            }

            var gitDir = System.IO.Path.Combine(root, ".git") + System.IO.Path.DirectorySeparatorChar;
            if (full.StartsWith(gitDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"the proposal path '{file.Path}' targets the git metadata directory.");
            }

            var directory = System.IO.Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(full, file.Content ?? string.Empty);
        }
    }

    private static string NormalizeRelative(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = path.Replace('\\', '/').Trim().TrimStart('/');
        if (normalized.Split('/').Any(segment => segment == ".."))
        {
            throw new SqlFlowException($"the proposal path '{path}' must not contain a '..' segment.");
        }

        return normalized.Replace('/', System.IO.Path.DirectorySeparatorChar);
    }

    private static CredentialsHandler CredentialsProvider(string? username, string secret)
    {
        // GitHub accepts any non-empty username beside a token; its conventional placeholder is x-access-token.
        // BitBucket needs the account username, which the repo source supplies.
        var user = string.IsNullOrWhiteSpace(username) ? "x-access-token" : username;
        return (_, _, _) => new UsernamePasswordCredentials { Username = user, Password = secret };
    }

    private string NewWorkdir()
    {
        Directory.CreateDirectory(_workRoot);
        var dir = System.IO.Path.Combine(_workRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            // A git working tree keeps read-only objects under .git; clear the attribute before deleting.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
