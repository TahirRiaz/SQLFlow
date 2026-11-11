using LibGit2Sharp;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What a sync attempt of a git repo source reads: the tip of its branch while it syncs on its own, and, while it syncs
/// only on request, the commit it holds unless the attempt answers a move. A move to a commit is placed on the branch's
/// history, read from a clone of a real (local) remote: it never moves back, never reads a commit off the branch, and
/// refuses a commit that would drop what the held one has.
/// </summary>
public sealed class RepoSyncTargetTests
{
    private const string Held = "1111111111111111111111111111111111111111";

    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private static CatalogRepoSource Source(bool autoSync, string? held = Held) => new()
    {
        Name = "estate",
        RemoteUrl = "https://git.invalid/estate.git",
        Branch = "main",
        AutoSync = autoSync,
        LastSyncedSha = held,
        SyncStartedUtc = T0,
    };

    [Fact]
    public void A_source_that_syncs_on_its_own_reads_the_tip_whatever_a_request_asked()
    {
        var source = Source(autoSync: true);
        source.MoveRequestedUtc = T0.AddSeconds(-1);
        source.MoveToCommit = "2222222";

        var plan = RepoSyncTarget.Plan(source);

        Assert.Equal(RepoSyncRead.BranchTip, plan.Read);
        Assert.Null(plan.Commit);
    }

    [Fact]
    public void A_source_that_syncs_on_request_and_holds_no_commit_reads_the_tip_for_its_first_sync()
    {
        Assert.Equal(RepoSyncRead.BranchTip, RepoSyncTarget.Plan(Source(autoSync: false, held: null)).Read);

        // A source that read a directory before holds no commit either.
        Assert.Equal(RepoSyncRead.BranchTip, RepoSyncTarget.Plan(Source(autoSync: false, held: "worktree@" + Held)).Read);
    }

    [Fact]
    public void A_source_that_syncs_on_request_reads_the_commit_it_holds_when_no_move_was_asked_for()
    {
        var plan = RepoSyncTarget.Plan(Source(autoSync: false));

        Assert.Equal(RepoSyncRead.HeldCommit, plan.Read);
        Assert.Equal(Held, plan.Commit);
        Assert.Contains("until a trigger request or a sync-now asks", plan.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_asked_for_after_the_attempt_started_is_the_next_attempts()
    {
        var source = Source(autoSync: false);
        source.MoveRequestedUtc = T0.AddSeconds(1);
        source.MoveToCommit = "2222222";

        Assert.Equal(RepoSyncRead.HeldCommit, RepoSyncTarget.Plan(source).Read);
    }

    [Fact]
    public void A_move_the_attempt_answers_reads_the_tip_or_the_commit_asked_for()
    {
        var toTip = Source(autoSync: false);
        toTip.MoveRequestedUtc = T0;
        Assert.Equal(RepoSyncRead.BranchTip, RepoSyncTarget.Plan(toTip).Read);

        var toCommit = Source(autoSync: false);
        toCommit.MoveRequestedUtc = T0.AddSeconds(-5);
        toCommit.MoveToCommit = "2222222";
        var plan = RepoSyncTarget.Plan(toCommit);
        Assert.Equal(RepoSyncRead.AskedCommit, plan.Read);
        Assert.Equal("2222222", plan.Commit);
    }

    [Fact]
    public void A_move_forward_on_the_branch_lands_on_the_commit_asked_for_and_a_short_id_resolves_to_the_full_one()
    {
        using var git = GitHistory.Create();
        using var history = git.Clone();

        var decision = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.First, git.Second[..9]);

        Assert.False(decision.Refused);
        Assert.Equal(git.Second, decision.Commit);
        Assert.StartsWith("Moving from ", decision.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_move_to_a_commit_the_held_one_already_contains_stays_on_the_held_one()
    {
        using var git = GitHistory.Create();
        using var history = git.Clone();

        var back = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.Third, git.First);
        var same = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.Second, git.Second);

        Assert.Equal(git.Third, back.Commit);
        Assert.Contains("never moves back", back.Message, StringComparison.Ordinal);
        Assert.Equal(git.Second, same.Commit);
        Assert.Contains("already holds", same.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_commit_off_the_branch_or_not_in_the_remote_is_refused()
    {
        using var git = GitHistory.Create();
        using var history = git.Clone();

        var side = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.First, git.Side);
        var missing = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.First, "abcdefabcdefabcdefabcdefabcdefabcdefabcd");
        var noBranch = RepoSyncTarget.Resolve(history, git.Remote, "release", git.First, git.Second);

        Assert.True(side.Refused);
        Assert.Contains($"is not on branch '{git.Branch}'", side.Message, StringComparison.Ordinal);
        Assert.True(missing.Refused);
        Assert.Contains("is not in", missing.Message, StringComparison.Ordinal);
        Assert.True(noBranch.Refused);
        Assert.Contains("has no branch 'release'", noBranch.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_commits_on_separate_lines_of_the_branch_are_refused_rather_than_dropping_what_is_held()
    {
        using var git = GitHistory.Create(withMerge: true);
        using var history = git.Clone();

        // The tip, a merge, holds both the branch's own commit and the merged one, but neither contains the other.
        var decision = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.Third, git.Merged!);

        Assert.True(decision.Refused);
        Assert.Contains("separate lines", decision.Message, StringComparison.Ordinal);
        Assert.Equal(git.Tip, RepoSyncTarget.Resolve(history, git.Remote, git.Branch, git.Third, git.Tip).Commit);
    }

    [Fact]
    public void A_held_commit_the_remote_no_longer_has_moves_to_the_commit_asked_for()
    {
        using var git = GitHistory.Create();
        using var history = git.Clone();

        var decision = RepoSyncTarget.Resolve(history, git.Remote, git.Branch, "abcdefabcdefabcdefabcdefabcdefabcdefabcd", git.Second);

        Assert.Equal(git.Second, decision.Commit);
        Assert.Contains("no longer in", decision.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A local remote with three commits on its branch, a commit on a side branch the branch never merges, and optionally a
    /// merge at the tip of a commit made off the second (<see cref="Merged"/>), which sits beside the third; cloned as the
    /// history clone a sync reads.
    /// </summary>
    private sealed class GitHistory : IDisposable
    {
        private readonly string _root;

        private GitHistory(string root) => _root = root;

        public string Remote => Path.Combine(_root, "remote");

        public string Branch { get; private set; } = string.Empty;

        public string First { get; private set; } = string.Empty;

        public string Second { get; private set; } = string.Empty;

        public string Third { get; private set; } = string.Empty;

        public string Side { get; private set; } = string.Empty;

        public string? Merged { get; private set; }

        public string Tip { get; private set; } = string.Empty;

        public static GitHistory Create(bool withMerge = false)
        {
            var git = new GitHistory(Path.Combine(Path.GetTempPath(), "sqlflow_target_" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(git.Remote);
            Repository.Init(git.Remote);
            using var repo = new Repository(git.Remote);
            git.First = Commit(repo, git.Remote, "a.txt", "1");
            git.Branch = repo.Head.FriendlyName;
            git.Second = Commit(repo, git.Remote, "a.txt", "2");
            var fork = repo.CreateBranch("feature");
            git.Third = Commit(repo, git.Remote, "a.txt", "3");

            var side = repo.CreateBranch("side");
            Commands.Checkout(repo, side);
            git.Side = Commit(repo, git.Remote, "side.txt", "s");
            Commands.Checkout(repo, repo.Branches[git.Branch]);

            git.Tip = git.Third;
            if (withMerge)
            {
                Commands.Checkout(repo, fork);
                git.Merged = Commit(repo, git.Remote, "b.txt", "f");
                Commands.Checkout(repo, repo.Branches[git.Branch]);
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
                var merge = repo.Merge(repo.Branches["feature"], signature, new MergeOptions { FastForwardStrategy = FastForwardStrategy.NoFastForward });
                git.Tip = merge.Commit.Sha;
            }

            return git;
        }

        public Repository Clone()
        {
            var path = Path.Combine(_root, "history");
            Repository.Clone(Remote, path);
            return new Repository(path);
        }

        private static string Commit(Repository repo, string dir, string file, string content)
        {
            File.WriteAllText(Path.Combine(dir, file), content);
            Commands.Stage(repo, "*");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            return repo.Commit("commit " + content, signature, signature).Sha;
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // A handle git left open on Windows keeps a file a moment longer; the temp folder is the OS's to clear.
            }
        }
    }
}
