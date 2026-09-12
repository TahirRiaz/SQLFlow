using LibGit2Sharp;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The change-history read path, against a real (local) git repository so it needs no network: the log filters,
/// the path-scoped file history, the patch text a diff returns, and the reuse-then-fetch behaviour of the
/// long-lived history clone. Also pins that an scm flow's repository coordinates can be recovered from its stored
/// definition, which is what lets the control plane resolve the credential server-side and keep it away from
/// every client.
/// </summary>
public sealed class GitHistoryTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_hist_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static Commit Commit(Repository repo, string relativePath, string content, string message, string author)
        => Commit(repo, relativePath, content, message, author, DateTimeOffset.UtcNow);

    /// <summary>Commits at an explicit instant, which is what lets a window comparison be pinned: the base side is
    /// chosen by commit date, so the test has to place its commits either side of the boundary.</summary>
    private static Commit Commit(
        Repository repo, string relativePath, string content, string message, string author, DateTimeOffset when)
    {
        var full = Path.Combine(repo.Info.WorkingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        Commands.Stage(repo, "*");
        var signature = new Signature(author, $"{author}@example.com", when);
        return repo.Commit(message, signature, signature);
    }

    /// <summary>Removes a file and commits the deletion, so a dropped object can be compared across the window.</summary>
    private static Commit Drop(Repository repo, string relativePath, string message, DateTimeOffset when)
    {
        var full = Path.Combine(repo.Info.WorkingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(full);
        Commands.Stage(repo, "*");
        var signature = new Signature("scm", "scm@example.com", when);
        return repo.Commit(message, signature, signature);
    }

    [Fact]
    public void Log_ListsCommitsNewestFirst_WithTheirChangedPaths()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, "cyclehire/cyclehire_00_api.yaml", "v1", "add cyclehire acquisition", "ada");
            Commit(repo, "apc/apc_calls_02_ing.yaml", "v1", "add apc load", "grace");

            var log = GitHistoryEndpoints.Log(repo, path: null, author: null, message: null, since: null, until: null, limit: null);

            Assert.Equal(2, log.Count);
            Assert.Equal("add apc load", log[0].Message);
            Assert.Equal("add cyclehire acquisition", log[1].Message);
            Assert.Contains("apc/apc_calls_02_ing.yaml", log[0].ChangedPaths);
            Assert.Equal(8, log[0].ShortSha.Length);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Log_NarrowsToOnePath_SoAFlowsOwnHistoryIsExact()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, "cyclehire/cyclehire_00_api.yaml", "v1", "add cyclehire", "ada");
            Commit(repo, "apc/apc_calls_02_ing.yaml", "v1", "add apc", "grace");
            Commit(repo, "cyclehire/cyclehire_00_api.yaml", "v2", "retune cyclehire watermark", "ada");

            var log = GitHistoryEndpoints.Log(
                repo, "cyclehire/cyclehire_00_api.yaml", author: null, message: null, since: null, until: null, limit: null);

            Assert.Equal(2, log.Count);
            Assert.All(log, c => Assert.Contains("cyclehire", c.Message, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Log_FiltersByAuthorAndMessage()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, "a.yaml", "v1", "add watermark to apc", "ada");
            Commit(repo, "b.yaml", "v1", "unrelated change", "grace");

            Assert.Single(GitHistoryEndpoints.Log(repo, null, "ada", null, null, null, null));
            Assert.Single(GitHistoryEndpoints.Log(repo, null, null, "watermark", null, null, null));
            Assert.Empty(GitHistoryEndpoints.Log(repo, null, "nobody", null, null, null, null));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Diff_ReturnsThePatchText_AndCountsLines()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, "flow.yaml", "keyColumns: [Id]\n", "first", "ada");
            var second = Commit(repo, "flow.yaml", "keyColumns: [Id, Version]\n", "widen the merge key", "ada");

            var diff = GitHistoryEndpoints.Diff(repo, second.Sha, path: null);

            Assert.NotNull(diff);
            Assert.Equal(second.Sha, diff!.Sha);
            Assert.Equal("widen the merge key", diff.Message);
            Assert.Contains("keyColumns: [Id, Version]", diff.Patch, StringComparison.Ordinal);
            Assert.Equal(1, diff.LinesAdded);
            Assert.Equal(1, diff.LinesDeleted);
            Assert.False(diff.Truncated);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Diff_UnknownCommit_IsNull_NotAnEmptyPatch()
    {
        // An empty patch and an unknown commit must not look alike: one means "nothing changed", the other
        // means the caller asked about a commit this repository has never seen.
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, "flow.yaml", "v1", "first", "ada");

            Assert.Null(GitHistoryEndpoints.Diff(repo, "0000000000000000000000000000000000000000", path: null));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void EnsureHistoryClone_ClonesOnce_ThenReusesAndFetchesNewCommits()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            Repository.Init(remote);
            using (var origin = new Repository(remote))
            {
                Commit(origin, "flow.yaml", "v1", "first", "ada");
            }

            var materializer = new GitMaterializer(cache);

            // Never fetch: the clone is created and then reused as-is.
            var first = materializer.EnsureHistoryClone(remote, "master", credentials: null, Timeout.InfiniteTimeSpan);
            var second = materializer.EnsureHistoryClone(remote, "master", credentials: null, Timeout.InfiniteTimeSpan);
            Assert.Equal(first, second);

            using (var origin = new Repository(remote))
            {
                Commit(origin, "flow.yaml", "v2", "second", "ada");
            }

            // Always fetch: the new commit is now reachable from the same working directory, so a history read
            // after a push does not need a fresh clone.
            var third = materializer.EnsureHistoryClone(remote, "master", credentials: null, TimeSpan.Zero);
            Assert.Equal(first, third);

            using var clone = new Repository(third);
            Assert.Contains(clone.Commits.QueryBy(new CommitFilter { IncludeReachableFrom = "origin/master" }),
                c => c.MessageShort.Trim() == "second");
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    // ---- The window comparison (the schema page's before/after drill-down) --------------------------------

    /// <summary>One table's snapshot path, in the layout an scm flow commits.</summary>
    private const string TablePath = "dw-dwh-prod/Table/arc.Cyclehire_Bikes.sql";

    private static readonly DateTimeOffset LongBefore = new(2026, 7, 1, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InsideWindow = new(2026, 8, 20, 3, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Latest = new(2026, 8, 23, 3, 30, 0, TimeSpan.Zero);

    /// <summary>The window boundary the tests compare against: later than <see cref="LongBefore"/>, earlier than
    /// the rest, so exactly one commit sits on the "before" side.</summary>
    private static readonly DateTime WindowStart = new(2026, 8, 16, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Compare_ReadsTheObjectAtBothEndsOfTheWindow_NotOneCommitAtATime()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", "snapshot", "scm", LongBefore);
            Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int, Lat decimal(9,6));\n", "snapshot", "scm", InsideWindow);
            var tip = Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int, Lat decimal(9,6), Lon decimal(9,6));\n", "snapshot", "scm", Latest);

            var comparison = GitHistoryEndpoints.Compare(repo, TablePath, WindowStart);

            Assert.NotNull(comparison);
            // Two snapshots moved the table inside the window; the answer is still ONE before and ONE after.
            Assert.Equal("CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", comparison!.BeforeText);
            Assert.Equal("CREATE TABLE arc.Cyclehire_Bikes (BikeId int, Lat decimal(9,6), Lon decimal(9,6));\n", comparison.AfterText);
            Assert.Equal(tip.Sha, comparison.After.Sha);
            Assert.NotNull(comparison.Before);
            Assert.Equal(LongBefore.UtcDateTime, comparison.Before!.CommittedUtc);
            Assert.Equal(8, comparison.Before.ShortSha.Length);
            Assert.Equal(1, comparison.LinesAdded);
            Assert.Equal(1, comparison.LinesDeleted);
            Assert.False(comparison.Truncated);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Compare_WithNoWindow_ReadsTheWholeScriptAsAdded()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", "snapshot", "scm", LongBefore);

            // An all-time window has no earlier side to compare against, which is what "since this estate began
            // snapshotting" means; the object must not read as unchanged just because nothing precedes it.
            var comparison = GitHistoryEndpoints.Compare(repo, TablePath, since: null);

            Assert.NotNull(comparison);
            Assert.Null(comparison!.Before);
            Assert.Null(comparison.BeforeText);
            Assert.Equal("CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", comparison.AfterText);
            Assert.Equal(1, comparison.LinesAdded);
            Assert.Equal(0, comparison.LinesDeleted);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Compare_ObjectAddedInsideTheWindow_HasABaseCommitButNoBeforeText()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            var baseCommit = Commit(repo, "dw-dwh-prod/Table/arc.Other.sql", "CREATE TABLE arc.Other (Id int);\n", "snapshot", "scm", LongBefore);
            Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", "snapshot", "scm", Latest);

            var comparison = GitHistoryEndpoints.Compare(repo, TablePath, WindowStart);

            Assert.NotNull(comparison);
            // The window HAS a base commit; the object simply was not in it, which is an add, not a missing base.
            Assert.Equal(baseCommit.Sha, comparison!.Before?.Sha);
            Assert.Null(comparison.BeforeText);
            Assert.Equal("CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", comparison.AfterText);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Compare_ObjectDroppedInsideTheWindow_KeepsTheBeforeTextAndHasNoAfterText()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);
            Commit(repo, TablePath, "CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", "snapshot", "scm", LongBefore);
            Commit(repo, "dw-dwh-prod/Table/arc.Other.sql", "CREATE TABLE arc.Other (Id int);\n", "snapshot", "scm", LongBefore);
            Drop(repo, TablePath, "snapshot", Latest);

            var comparison = GitHistoryEndpoints.Compare(repo, TablePath, WindowStart);

            Assert.NotNull(comparison);
            // A dropped object is the one nobody should scroll past: its last known script is what makes the
            // deletion reviewable, so it survives on the before side.
            Assert.Equal("CREATE TABLE arc.Cyclehire_Bikes (BikeId int);\n", comparison!.BeforeText);
            Assert.Null(comparison.AfterText);
            Assert.Equal(1, comparison.LinesDeleted);
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void Compare_OnARepositoryWithNoCommits_IsNull()
    {
        var dir = NewTempDir();
        try
        {
            Repository.Init(dir);
            using var repo = new Repository(dir);

            // Nothing to compare is not the same as nothing changed, so the caller gets null and answers 400.
            Assert.Null(GitHistoryEndpoints.Compare(repo, TablePath, WindowStart));
        }
        finally
        {
            DeleteDir(dir);
        }
    }

    [Fact]
    public void SnapshotRepository_IsRecoveredFromAnScmFlowDefinition()
    {
        var repository = GitHistoryEndpoints.ReadSnapshotRepository("""
            {
              "flow": {
                "sysAlias": "dbsource_dwh_00_scm",
                "repository": {
                  "workingDirectory": "/tmp/sqlflow/scm/repo-dbsource-dwh",
                  "remote": "https://bitbucket.org/example/repo-dbsource.git",
                  "branch": "main",
                  "username": "${env:SQLFLOW_GIT_USERNAME}",
                  "secret": "${env:SQLFLOW_GIT_TOKEN}"
                }
              }
            }
            """);

        Assert.NotNull(repository);
        Assert.Equal("https://bitbucket.org/example/repo-dbsource.git", repository!.Value.Remote);
        Assert.Equal("main", repository.Value.Branch);
        // References, never values: what is stored is a pointer the control plane resolves at read time.
        Assert.Equal("${env:SQLFLOW_GIT_TOKEN}", repository.Value.Secret);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"flow\":{}}")]
    [InlineData("{\"flow\":{\"repository\":{\"workingDirectory\":\"/tmp/x\"}}}")]
    public void SnapshotRepository_WithoutARemote_IsNull(string? definitionJson)
    {
        // A local-only snapshot has no remote to read back, and a malformed definition must not be guessed at.
        Assert.Null(GitHistoryEndpoints.ReadSnapshotRepository(definitionJson));
    }
}
