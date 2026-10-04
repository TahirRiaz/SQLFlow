using LibGit2Sharp;
using SqlFlow.Node;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// SHA-pinned git materialization against a real (local) git repository: the materializer checks out the exact
/// commit requested, reuses an already-materialized commit, and fails clearly on an unknown commit. Uses a local
/// repo so it needs no network and runs everywhere.
/// </summary>
public sealed class GitMaterializerTests
{
    [Fact]
    public void Materialize_ChecksOutTheExactCommit_AndReusesIt()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (firstSha, secondSha) = SeedRepoWithTwoVersions(remote);

            var materializer = new GitMaterializer(cache);

            // Each commit materializes to the file content as it was AT that commit.
            var firstDir = materializer.Materialize(remote, firstSha, credentials: null);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(firstDir, "flow.yaml")));

            var secondDir = materializer.Materialize(remote, secondSha, credentials: null);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(secondDir, "flow.yaml")));

            // The two commits materialize to distinct directories (so they never disturb each other).
            Assert.NotEqual(firstDir, secondDir);

            // Re-materializing the same commit reuses the same directory.
            Assert.Equal(firstDir, materializer.Materialize(remote, firstSha, credentials: null));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    [Fact]
    public void Materialize_UnknownCommit_ThrowsAClearError()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);

            var ex = Assert.Throws<SqlFlowNodeException>(
                () => materializer.Materialize(remote, new string('0', 40), credentials: null));
            // The commit clones fine but is absent, so it is reported as "not found" with the offending SHA.
            Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    [Fact]
    public async Task Materialize_ConcurrentSameCommit_DoesNotRaceOnTheGitLock()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (firstSha, _) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);

            // Several runs pinned to the same commit materialize at once (a schedule firing several batches of one
            // source). Before the per-directory lock this raced two clones into the same .git and one failed with
            // "failed to create locked file '.../config.lock': File exists". Every task must now return the same
            // checked-out directory with the correct content.
            const int concurrency = 8;
            using var start = new ManualResetEventSlim(false);
            var tasks = new Task<string>[concurrency];
            for (var i = 0; i < concurrency; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    start.Wait();
                    return materializer.Materialize(remote, firstSha, credentials: null);
                });
            }

            start.Set();
            var dirs = await Task.WhenAll(tasks);

            Assert.All(dirs, d => Assert.Equal(dirs[0], d));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(dirs[0], "flow.yaml")));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    [Fact]
    public void Materialize_LeftoverInvalidCheckout_IsRebuiltInsteadOfFailing()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (firstSha, _) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);

            // A first materialization leaves a valid checkout in the cache.
            var dir = materializer.Materialize(remote, firstSha, credentials: null);

            // Simulate a run that was killed mid-clone: the cache directory still exists and is non-empty, but it
            // is no longer a valid repository. Removing .git leaves the working files behind; a read-only stray
            // stands in for the leftover read-only git objects. Before the hardened cleanup this next call blew up
            // with a raw "Directory not empty" IOException; it must now tear the directory down and re-clone.
            DeleteDir(Path.Combine(dir, ".git"));
            var stray = Path.Combine(dir, "leftover.pack");
            File.WriteAllText(stray, "partial");
            File.SetAttributes(stray, FileAttributes.ReadOnly);

            var rebuilt = materializer.Materialize(remote, firstSha, credentials: null);

            Assert.Equal(dir, rebuilt);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(rebuilt, "flow.yaml")));
            Assert.False(File.Exists(stray));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    /// <summary>
    /// The managed sync materializes the branch tip into the commit's own folder and records it as the repo's root.
    /// While the tip stays, every sync reuses that folder and writes nothing, so nothing a reader holds is replaced.
    /// </summary>
    [Fact]
    public void MaterializeBranch_UsesTheCommitFolder_AndReusesItWhileTheTipStays()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (_, secondSha) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);

            var first = materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);
            var again = materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);

            Assert.Equal(secondSha, first.CommitSha);
            Assert.Equal(secondSha, Path.GetFileName(first.WorkingDirectory));
            Assert.Equal("v2", File.ReadAllText(Path.Combine(first.WorkingDirectory, "flow.yaml")));
            Assert.False(first.Reused);
            Assert.Equal(first.WorkingDirectory, again.WorkingDirectory);
            Assert.True(again.Reused);

            // The pinned run of the same commit reads the very same folder.
            Assert.Equal(first.WorkingDirectory, materializer.Materialize(remote, secondSha, credentials: null));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    /// <summary>A new commit gets a folder of its own; the folder an earlier sync recorded as the root stays exactly as
    /// it was, so a reader that resolved a flow file under it a moment before still finds it.</summary>
    [Fact]
    public void MaterializeBranch_OnANewCommit_LeavesThePreviousFolderUntouched()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (_, secondSha) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);
            var before = materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);

            var thirdSha = CommitVersion(remote, "v3");
            var after = materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);

            Assert.Equal(thirdSha, after.CommitSha);
            Assert.NotEqual(before.WorkingDirectory, after.WorkingDirectory);
            Assert.Equal("v3", File.ReadAllText(Path.Combine(after.WorkingDirectory, "flow.yaml")));
            Assert.Equal("v2", File.ReadAllText(Path.Combine(before.WorkingDirectory, "flow.yaml")));
            using var previous = new Repository(before.WorkingDirectory);
            Assert.Equal(secondSha, previous.Head.Tip.Sha);
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    /// <summary>A sync that fails (here a branch the remote does not have) changes nothing in the cache: the root an
    /// earlier sync recorded is still there for every reader.</summary>
    [Fact]
    public void MaterializeBranch_ThatFails_LeavesTheCacheAsItWas()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);
            var synced = materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);

            var ex = Assert.Throws<SqlFlowNodeException>(
                () => materializer.MaterializeBranch(remote, "no-such-branch", credentials: null));

            Assert.Contains("no branch 'no-such-branch'", ex.Message, StringComparison.Ordinal);
            Assert.Equal("v2", File.ReadAllText(Path.Combine(synced.WorkingDirectory, "flow.yaml")));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    /// <summary>
    /// When a commit folder's name cannot be taken (here a leftover tree with a file held open, which on Windows blocks
    /// both removing and renaming it, as a deleted folder's name stays blocked until its last handle closes), the
    /// checkout is published under a unique sibling instead of failing, the held tree is not touched, and the sibling
    /// is reused from then on. Elsewhere the leftover can be moved aside, and the commit's own name is used.
    /// </summary>
    [Fact]
    public void Materialize_WhenTheCommitFolderNameIsHeld_PublishesBesideIt_AndReusesThat()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (firstSha, secondSha) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);
            var repoFolder = Path.GetDirectoryName(materializer.Materialize(remote, firstSha, credentials: null))!;

            var held = Path.Combine(repoFolder, secondSha);
            Directory.CreateDirectory(held);
            var heldFile = Path.Combine(held, "held.txt");
            File.WriteAllText(heldFile, "in use");

            string published;
            using (new FileStream(heldFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                published = materializer.Materialize(remote, secondSha, credentials: null);

                Assert.Equal("v2", File.ReadAllText(Path.Combine(published, "flow.yaml")));
                if (OperatingSystem.IsWindows())
                {
                    Assert.StartsWith(held + ".alt-", published, StringComparison.OrdinalIgnoreCase);
                    Assert.True(File.Exists(heldFile));
                }
            }

            Assert.Equal(published, materializer.Materialize(remote, secondSha, credentials: null));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    /// <summary>A sync sweeps what no reader can be using (trees moved aside, staging older than any clone in progress,
    /// and the previous layout's branch folder once unwritten for a day) and leaves everything else alone: commit
    /// folders, a staging directory a clone may be writing, and a recent branch folder.</summary>
    [Fact]
    public void MaterializeBranch_SweepsOnlyWhatIsAbandoned()
    {
        var remote = NewTempDir();
        var cache = NewTempDir();
        try
        {
            var (firstSha, _) = SeedRepoWithTwoVersions(remote);
            var materializer = new GitMaterializer(cache);
            var pinned = materializer.Materialize(remote, firstSha, credentials: null);
            var repoFolder = Path.GetDirectoryName(pinned)!;

            string Folder(string name, TimeSpan age)
            {
                var path = Path.Combine(repoFolder, name);
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "x"), "x");
                Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
                return path;
            }

            var stale = Folder(firstSha + ".stale-0001", TimeSpan.Zero);
            var oldStaging = Folder(".staging-old", TimeSpan.FromHours(2));
            var liveStaging = Folder(".staging-live", TimeSpan.Zero);
            var legacyBranch = Folder("branch", TimeSpan.FromDays(2));

            materializer.MaterializeBranch(remote, CurrentBranch(remote), credentials: null);

            Assert.False(Directory.Exists(stale));
            Assert.False(Directory.Exists(oldStaging));
            Assert.False(Directory.Exists(legacyBranch));
            Assert.True(Directory.Exists(liveStaging));
            Assert.Equal("v1", File.ReadAllText(Path.Combine(pinned, "flow.yaml")));

            // A branch folder written recently may still be a root an older host recorded: it stays.
            var recentBranch = Folder("branch", TimeSpan.FromHours(1));
            GitMaterializer.SweepAbandoned(repoFolder, DateTime.UtcNow);
            Assert.True(Directory.Exists(recentBranch));
        }
        finally
        {
            DeleteDir(remote);
            DeleteDir(cache);
        }
    }

    private static string CurrentBranch(string path)
    {
        using var repo = new Repository(path);
        return repo.Head.FriendlyName;
    }

    private static string CommitVersion(string path, string content)
    {
        using var repo = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(path, "flow.yaml"), content);
        Commands.Stage(repo, "*");
        return repo.Commit(content, signature, signature).Sha;
    }

    private static (string FirstSha, string SecondSha) SeedRepoWithTwoVersions(string path)
    {
        Repository.Init(path);
        using var repo = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);

        File.WriteAllText(Path.Combine(path, "flow.yaml"), "v1");
        Commands.Stage(repo, "*");
        var first = repo.Commit("v1", signature, signature);

        File.WriteAllText(Path.Combine(path, "flow.yaml"), "v2");
        Commands.Stage(repo, "*");
        var second = repo.Commit("v2", signature, signature);

        return (first.Sha, second.Sha);
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sqlflow_git_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        // Git keeps read-only objects under .git; clear the attribute before deleting.
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp directory.
        }
    }
}
