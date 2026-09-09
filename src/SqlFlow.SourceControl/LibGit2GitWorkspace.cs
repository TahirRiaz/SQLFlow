using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using SqlFlow.Core;

namespace SqlFlow.SourceControl;

/// <summary>
/// The LibGit2Sharp implementation of <see cref="IGitWorkspace"/>: a fully managed, cross-platform git client
/// (no external git binary), the engine the pilot's BitBucket and a GitHub remote both speak over HTTPS. It
/// clones a configured remote (or initializes a local repository), lands on the target branch, and on commit
/// stages only this flow's database subtree, commits when there is a real change, and pushes fast-forward.
/// Authentication is username + secret (BitBucket app password or GitHub token), supplied per call and never
/// persisted.
/// </summary>
public sealed class LibGit2GitWorkspace : IGitWorkspace
{
    private readonly GitWorkspaceConfig _config;

    public LibGit2GitWorkspace(GitWorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.WorkingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.PathScope);
        ArgumentException.ThrowIfNullOrWhiteSpace(config.Branch);
        _config = config;
    }

    public void EnsureReady(CancellationToken ct = default)
    {
        var workdir = Path.GetFullPath(_config.WorkingDirectory);
        Directory.CreateDirectory(workdir);

        try
        {
            if (!Repository.IsValid(workdir))
            {
                if (_config.Remote is { } remote)
                {
                    var options = new CloneOptions();
                    options.FetchOptions.CredentialsProvider = CredentialsProvider();
                    Repository.Clone(remote, workdir, options);
                }
                else
                {
                    Repository.Init(workdir);
                }
            }

            using var repo = new Repository(workdir);
            EnsureRemote(repo);
            EnsureBranch(repo);
            SyncWithRemote(repo);
        }
        catch (LibGit2SharpException ex)
        {
            throw new SqlFlowException(GitMessage("prepare the repository", ex), ex);
        }
    }

    public GitCommitResult Commit(string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        var workdir = Path.GetFullPath(_config.WorkingDirectory);

        try
        {
            using var repo = new Repository(workdir);

            // Stage exactly this flow's database subtree: adds, modifications, and deletions, nothing else, so a
            // shared repository's other database folders are never swept into this commit.
            var prefix = _config.PathScope.Replace('\\', '/').TrimEnd('/') + "/";
            var scoped = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true })
                .Where(e => e.State is not (FileStatus.Ignored or FileStatus.Unaltered))
                .Where(e => e.FilePath.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Select(e => e.FilePath)
                .ToList();

            if (scoped.Count == 0)
            {
                return new GitCommitResult { Committed = false, Pushed = false, FilesChanged = 0 };
            }

            Commands.Stage(repo, scoped);

            var signature = new Signature(_config.AuthorName, _config.AuthorEmail, DateTimeOffset.Now);
            var commit = repo.Commit(message, signature, signature, new CommitOptions());

            var pushed = false;
            if (_config.Remote is not null && _config.Push)
            {
                Push(repo);
                pushed = true;
            }

            return new GitCommitResult
            {
                Committed = true,
                CommitSha = commit.Sha,
                Pushed = pushed,
                FilesChanged = scoped.Count,
            };
        }
        catch (LibGit2SharpException ex)
        {
            throw new SqlFlowException(GitMessage("commit or push the snapshot", ex), ex);
        }
    }

    private void EnsureRemote(Repository repo)
    {
        if (_config.Remote is not { } remote)
        {
            return;
        }

        var origin = repo.Network.Remotes["origin"];
        if (origin is null)
        {
            repo.Network.Remotes.Add("origin", remote);
        }
    }

    /// <summary>Lands the working tree on the target branch before any file is written: checks out an existing
    /// branch, creates one from HEAD when there are commits, or points the unborn HEAD at it so the first commit
    /// creates it (the empty-repository case).</summary>
    private void EnsureBranch(Repository repo)
    {
        var branch = _config.Branch;
        var existing = repo.Branches[branch];
        if (existing is not null)
        {
            if (!string.Equals(repo.Head.FriendlyName, branch, StringComparison.Ordinal))
            {
                Commands.Checkout(repo, existing);
            }

            return;
        }

        if (repo.Head.Tip is null)
        {
            // Unborn HEAD (a fresh init or an empty remote): aim HEAD at the branch so the first commit creates it.
            repo.Refs.UpdateTarget("HEAD", "refs/heads/" + branch);
            return;
        }

        var created = repo.CreateBranch(branch);
        Commands.Checkout(repo, created);
    }

    /// <summary>
    /// Brings an EXISTING working tree back in line with the remote before anything is scripted into it: fetch,
    /// then hard-reset the branch onto the remote tip. A fresh clone is already there, so this only matters for a
    /// reused directory, and that case is the whole point. The snapshot is regenerated from the database on every
    /// run, so the working tree carries nothing worth preserving; without the reset a directory that fell behind
    /// (a sibling flow, or another worker replica, pushed in the meantime) could never fast-forward again and
    /// every later push would be rejected until someone deleted the folder by hand.
    /// </summary>
    private void SyncWithRemote(Repository repo)
    {
        if (_config.Remote is null)
        {
            return;
        }

        var origin = repo.Network.Remotes["origin"];
        if (origin is null)
        {
            return;
        }

        var options = new FetchOptions { CredentialsProvider = CredentialsProvider() };
        Commands.Fetch(repo, origin.Name, origin.FetchRefSpecs.Select(r => r.Specification), options, logMessage: null);

        // No remote-tracking branch yet means the remote does not carry this branch (an empty repository, or a
        // branch this flow is the first to publish): there is nothing to reset onto, and the first push creates it.
        var remoteBranch = repo.Branches[$"{origin.Name}/{_config.Branch}"];
        if (remoteBranch?.Tip is null)
        {
            return;
        }

        repo.Reset(ResetMode.Hard, remoteBranch.Tip);
    }

    private void Push(Repository repo)
    {
        var origin = repo.Network.Remotes["origin"]
            ?? throw new SqlFlowException("A remote was configured but 'origin' is not set on the repository.");

        var options = new PushOptions { CredentialsProvider = CredentialsProvider() };

        // An explicit source:destination refspec creates or fast-forwards the remote branch; no leading '+', so
        // a non-fast-forward push is rejected by the server rather than silently rewriting history.
        var refSpec = $"refs/heads/{_config.Branch}:refs/heads/{_config.Branch}";
        repo.Network.Push(origin, refSpec, options);
    }

    private CredentialsHandler? CredentialsProvider()
    {
        if (_config.Credentials is not { } credentials)
        {
            return null;
        }

        // GitHub accepts any non-empty username beside a token; BitBucket needs the account username, which the
        // operator supplies. The placeholder only ever applies to the token-as-password case.
        var username = string.IsNullOrEmpty(credentials.Username) ? "x-access-token" : credentials.Username;
        return (_, _, _) => new UsernamePasswordCredentials { Username = username, Password = credentials.Secret };
    }

    private string GitMessage(string action, LibGit2SharpException ex)
        => $"Could not {action} at '{Path.GetFullPath(_config.WorkingDirectory)}': {ex.Message}";
}
