using LibGit2Sharp;
using SqlFlow.SourceControl;
using Xunit;

namespace SqlFlow.Tests.SourceControl;

/// <summary>
/// The LibGit2Sharp workspace, exercised fully offline against temp working trees and a local bare "remote":
/// it initializes or clones, lands on the requested branch, commits only the scoped subtree, is a no-op when
/// nothing changed, and pushes to a remote so the snapshot round-trips. This is the engine the pilot's
/// BitBucket remote speaks to, validated without any network.
/// </summary>
public sealed class LibGit2GitWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_scm_git_" + Guid.NewGuid().ToString("N"));

    public LibGit2GitWorkspaceTests() => Directory.CreateDirectory(_root);

    private string Path2(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void WriteFile(string workdir, string relative, string content)
    {
        var full = Path.Combine(workdir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static GitWorkspaceConfig Config(string workdir, string? remote = null, bool push = true) => new()
    {
        WorkingDirectory = workdir,
        Remote = remote,
        Branch = "main",
        AuthorName = "Test Bot",
        AuthorEmail = "bot@test.local",
        Push = push,
        PathScope = "Warehouse",
    };

    [Fact]
    public void LocalInit_CommitsTheScopedSubtree()
    {
        var workdir = Path2("local");
        var workspace = new LibGit2GitWorkspace(Config(workdir));
        workspace.EnsureReady();

        WriteFile(workdir, "Warehouse/Table/dbo.Customer.sql", "CREATE TABLE x;\n");
        var result = workspace.Commit("first snapshot");

        Assert.True(result.Committed);
        Assert.NotNull(result.CommitSha);
        Assert.False(result.Pushed);
        Assert.Equal(1, result.FilesChanged);

        using var repo = new Repository(workdir);
        Assert.Equal("main", repo.Head.FriendlyName);
        Assert.Equal("first snapshot", repo.Head.Tip.Message.Trim());
    }

    [Fact]
    public void Commit_IsNoOp_WhenNothingChanged()
    {
        var workdir = Path2("noop");
        var workspace = new LibGit2GitWorkspace(Config(workdir));
        workspace.EnsureReady();
        WriteFile(workdir, "Warehouse/Table/dbo.Customer.sql", "CREATE TABLE x;\n");
        workspace.Commit("first");

        var second = workspace.Commit("second");

        Assert.False(second.Committed);
        Assert.Null(second.CommitSha);
        Assert.Equal(0, second.FilesChanged);
    }

    [Fact]
    public void Commit_StagesOnlyItsOwnDatabaseFolder()
    {
        var workdir = Path2("scoped");
        var workspace = new LibGit2GitWorkspace(Config(workdir));
        workspace.EnsureReady();

        WriteFile(workdir, "Warehouse/Table/dbo.Customer.sql", "a\n");
        WriteFile(workdir, "OtherDb/Table/dbo.Foreign.sql", "b\n");
        var result = workspace.Commit("scoped snapshot");

        Assert.Equal(1, result.FilesChanged);
        using var repo = new Repository(workdir);
        var status = repo.RetrieveStatus(new StatusOptions { IncludeUntracked = true, RecurseUntrackedDirs = true });
        // The foreign database folder is still untracked: it was never staged into this flow's commit.
        Assert.Contains(status, e => e.FilePath.Replace('\\', '/') == "OtherDb/Table/dbo.Foreign.sql"
                                     && e.State.HasFlag(FileStatus.NewInWorkdir));
    }

    [Fact]
    public void ClonePush_RoundTripsToARemote()
    {
        var bare = Path2("remote.git");
        Repository.Init(bare, isBare: true);

        var workdir = Path2("clone");
        var workspace = new LibGit2GitWorkspace(Config(workdir, remote: bare));
        workspace.EnsureReady();

        WriteFile(workdir, "Warehouse/StoredProcedure/dbo.usp.sql", "CREATE PROC dbo.usp AS SELECT 1;\n");
        var result = workspace.Commit("push snapshot");

        Assert.True(result.Committed);
        Assert.True(result.Pushed);

        // The remote now carries the commit on main; assert the pushed branch's tree contains the file
        // (the bare repo's own HEAD still points at its init default branch, so checking the tree is exact).
        using var remote = new Repository(bare);
        var main = remote.Branches["main"];
        Assert.NotNull(main);
        Assert.NotNull(main!.Tip["Warehouse/StoredProcedure/dbo.usp.sql"]);
    }

    [Fact]
    public void NoPush_CommitsLocallyButLeavesTheRemoteEmpty()
    {
        var bare = Path2("remote2.git");
        Repository.Init(bare, isBare: true);

        var workdir = Path2("clone2");
        var workspace = new LibGit2GitWorkspace(Config(workdir, remote: bare, push: false));
        workspace.EnsureReady();
        WriteFile(workdir, "Warehouse/Table/dbo.Customer.sql", "x\n");
        var result = workspace.Commit("local only");

        Assert.True(result.Committed);
        Assert.False(result.Pushed);

        using var remote = new Repository(bare);
        Assert.Empty(remote.Branches); // nothing was pushed
    }

    [Fact]
    public void StaleWorkingTree_CatchesUpToTheRemote_AndStillPushes()
    {
        // The multi-writer case the deployed estate actually has: two workspaces (two worker replicas, or two scm
        // flows sharing a remote) each hold their own clone. One pushes; the other is now behind. Without a fetch
        // and reset its next push would be rejected as non-fast-forward, forever, so it must catch up on its own.
        var bare = Path2("shared.git");
        Repository.Init(bare, isBare: true);

        var firstDir = Path2("writer-a");
        var first = new LibGit2GitWorkspace(Config(firstDir, remote: bare));
        first.EnsureReady();
        WriteFile(firstDir, "Warehouse/Table/dbo.A.sql", "CREATE TABLE dbo.A;\n");
        Assert.True(first.Commit("snapshot A").Pushed);

        var secondDir = Path2("writer-b");
        var second = new LibGit2GitWorkspace(Config(secondDir, remote: bare));
        second.EnsureReady();
        WriteFile(secondDir, "Warehouse/Table/dbo.B.sql", "CREATE TABLE dbo.B;\n");
        Assert.True(second.Commit("snapshot B").Pushed);

        // Writer A is now stale. It re-prepares (the start of its next run), which must fast-forward it onto
        // B's commit, and its own next snapshot must land on top rather than be rejected.
        first.EnsureReady();
        WriteFile(firstDir, "Warehouse/Table/dbo.A.sql", "CREATE TABLE dbo.A (Id int);\n");
        var third = first.Commit("snapshot A again");

        Assert.True(third.Committed);
        Assert.True(third.Pushed);

        using var remote = new Repository(bare);
        var main = remote.Branches["main"]!;
        Assert.NotNull(main.Tip["Warehouse/Table/dbo.A.sql"]);
        Assert.NotNull(main.Tip["Warehouse/Table/dbo.B.sql"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            // git packs can be read-only on Windows; clear the attribute before delete.
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }
}
