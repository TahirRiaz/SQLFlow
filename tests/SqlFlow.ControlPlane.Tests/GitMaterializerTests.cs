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
