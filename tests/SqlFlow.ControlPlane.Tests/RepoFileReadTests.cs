using System.Text;
using LibGit2Sharp;
using Microsoft.AspNetCore.Http;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// One repository file read as text for the repo page's code window (<see cref="RepoFileEndpoints"/>), without a host or a
/// database: which paths name a file of a repository, which names are credential stores whose content is not shown, how
/// text, binary and oversized files read, and the two reads themselves, a git repository's branch tip and a local-path
/// repo's folder, where a file git ignores is not shown and a path leaving the folder is refused.
/// </summary>
public sealed class RepoFileReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqlflow_repofile_" + Guid.NewGuid().ToString("N"));

    public RepoFileReadTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("sales/orders.flow.yaml")]
    [InlineData("data/curves/LOG-0001/chunk-0.json")]
    [InlineData(".editorconfig")]
    public void APathAsTheListingGivesIt_NamesAFile(string path)
        => Assert.Null(RepoFileEndpoints.PathProblem(path));

    [Theory]
    [InlineData(null, "Name the file with ?path=")]
    [InlineData("  ", "Name the file with ?path=")]
    [InlineData("/etc/passwd", "not a repo-relative, forward-slashed path")]
    [InlineData("sales\\orders.flow.yaml", "not a repo-relative, forward-slashed path")]
    [InlineData("C:/Windows/win.ini", "not a repo-relative, forward-slashed path")]
    [InlineData("notes.txt:hidden", "not a repo-relative, forward-slashed path")]
    [InlineData("sales/../../outside.txt", "names a folder by '.', '..' or an empty name")]
    [InlineData("./README.md", "names a folder by '.', '..' or an empty name")]
    [InlineData("sales//orders.flow.yaml", "names a folder by '.', '..' or an empty name")]
    [InlineData("sales/", "names a folder by '.', '..' or an empty name")]
    [InlineData(".git/config", "git's own file (.git)")]
    [InlineData("sales/.gitignore", "git's own file (.gitignore)")]
    [InlineData("sales/a\u0001b.yaml", "control character")]
    public void APathLeavingTheRepositoryOrNamingGitsOwnFiles_IsRefusedSayingWhy(string? path, string reason)
        => Assert.Contains(reason, RepoFileEndpoints.PathProblem(path), StringComparison.Ordinal);

    [Fact]
    public void APathPastTheLongestOne_IsRefused()
        => Assert.Contains("at most 1024", RepoFileEndpoints.PathProblem(new string('a', 1025)), StringComparison.Ordinal);

    [Theory]
    [InlineData(".env", "an environment file")]
    [InlineData("deploy/.env.production", "an environment file")]
    [InlineData("deploy/.ENV", "an environment file")]
    [InlineData(".npmrc", "a login or key file")]
    [InlineData("keys/id_ed25519", "a login or key file")]
    [InlineData("certs/server.pem", "a key or certificate store")]
    [InlineData("certs/client.PFX", "a key or certificate store")]
    [InlineData("vault.kdbx", "a key or certificate store")]
    public void ACredentialStoresName_IsRecognised(string path, string store)
        => Assert.Equal(store, RepoFileEndpoints.CredentialStore(path));

    [Theory]
    [InlineData("README.md")]
    [InlineData(".envrc.example.md")]
    [InlineData("flows/env.yaml")]
    [InlineData("mappings/key.yaml")]
    [InlineData("data/keys.csv")]
    public void AnyOtherName_IsCode(string path)
        => Assert.Null(RepoFileEndpoints.CredentialStore(path));

    [Fact]
    public void TextReadsAsText_AndANulByteAmongTheFirst8000BytesReadsAsBinary()
    {
        Assert.False(RepoFileEndpoints.LooksBinary("name: orders\n"u8));
        Assert.True(RepoFileEndpoints.LooksBinary([0x50, 0x4B, 0x03, 0x04, 0x00, 0x14]));

        // A UTF-16 text holds a NUL in every ASCII character, and says so with its byte order mark.
        Assert.False(RepoFileEndpoints.LooksBinary(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("name: x")).ToArray()));

        // Past the first 8,000 bytes a NUL no longer decides.
        var late = Enumerable.Repeat((byte)'a', 8_000).Append((byte)0).ToArray();
        Assert.False(RepoFileEndpoints.LooksBinary(late));
    }

    [Fact]
    public void AFileOverTheCap_IsShownInPart_AndSaysSo_WithoutEndingInHalfACharacter()
    {
        // The cap falls between the two bytes of an 'é', which is dropped rather than shown as a replacement mark.
        var bytes = Enumerable.Repeat((byte)'a', RepoFileEndpoints.MaxTextBytes - 1).Concat("éé"u8.ToArray()).ToArray();
        using var stream = new MemoryStream(bytes);

        var file = RepoFileEndpoints.Text("big.txt", "disk", null, null, stream, bytes.Length);

        Assert.True(file.Truncated);
        Assert.False(file.Binary);
        Assert.Equal(bytes.Length, file.SizeBytes);
        Assert.Equal(RepoFileEndpoints.MaxTextBytes - 1, file.Text!.Length);
        Assert.DoesNotContain('\uFFFD', file.Text);
    }

    [Fact]
    public void AByteOrderMark_IsReadAndNotShown()
    {
        var bytes = Encoding.UTF8.GetPreamble().Concat("naïve: true\n"u8.ToArray()).ToArray();
        using var stream = new MemoryStream(bytes);

        var file = RepoFileEndpoints.Text("a.yaml", "disk", null, null, stream, bytes.Length);

        Assert.Equal("naïve: true\n", file.Text);
        Assert.False(file.Truncated);
    }

    [Fact]
    public void ABranchTip_GivesAFilesText_ABinaryFilesSizeAlone_AndSaysWhatAPathIsWhenItIsNoFile()
    {
        Repository.Init(_root);
        string sha;
        string branch;
        using (var repo = new Repository(_root))
        {
            Directory.CreateDirectory(Path.Combine(_root, "flows"));
            File.WriteAllText(Path.Combine(_root, "flows", "orders.flow.yaml"), "name: orders\nflowType: ing\n");
            File.WriteAllBytes(Path.Combine(_root, "flows", "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00]);
            Commands.Stage(repo, "*");
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            sha = repo.Commit("flows", signature, signature).Sha;
            branch = repo.Head.FriendlyName;
        }

        using var repository = new Repository(_root);

        var (flow, none) = RepoFileEndpoints.ReadFromRepository(repository, "flows/orders.flow.yaml", branch);
        Assert.Null(none);
        Assert.Equal("name: orders\nflowType: ing\n", flow!.Text);
        Assert.Equal("git", flow.ReadFrom);
        Assert.Equal(branch, flow.Branch);
        Assert.Equal(sha, flow.CommitSha);

        var (image, _) = RepoFileEndpoints.ReadFromRepository(repository, "flows/logo.png", branch);
        Assert.True(image!.Binary);
        Assert.Null(image.Text);
        Assert.Equal(6, image.SizeBytes);

        var (_, missing) = RepoFileEndpoints.ReadFromRepository(repository, "flows/gone.yaml", branch);
        Assert.Equal(StatusCodes.Status404NotFound, missing!.StatusCode);
        Assert.Contains($"Branch '{branch}' holds no file 'flows/gone.yaml'", missing.ProblemDetails.Detail, StringComparison.Ordinal);

        var (_, folder) = RepoFileEndpoints.ReadFromRepository(repository, "flows", branch);
        Assert.Equal(StatusCodes.Status400BadRequest, folder!.StatusCode);
        Assert.Contains("is a folder", folder.ProblemDetails.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ALocalPathRepo_GivesWhatItHolds_ButNotWhatGitIgnores_NorAnythingOutsideIt()
    {
        Repository.Init(_root);
        Directory.CreateDirectory(Path.Combine(_root, "flows"));
        Directory.CreateDirectory(Path.Combine(_root, "data"));
        File.WriteAllText(Path.Combine(_root, ".gitignore"), "data/\n*.tmp\n");
        File.WriteAllText(Path.Combine(_root, "flows", "orders.flow.yaml"), "name: orders\n");
        File.WriteAllText(Path.Combine(_root, "data", "rows.csv"), "id,name\n1,a\n");
        File.WriteAllText(Path.Combine(_root, "scratch.tmp"), "draft\n");
        File.WriteAllText(Path.Combine(_root, "kept.tmp"), "tracked although a pattern matches it\n");
        using (var repo = new Repository(_root))
        {
            Commands.Stage(repo, "kept.tmp", new StageOptions { IncludeIgnored = true });
            var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
            repo.Commit("kept", signature, signature);
        }

        // A file in the working tree, committed or not, is the repository's.
        var (flow, none) = RepoFileEndpoints.ReadFromDisk(_root, "flows/orders.flow.yaml");
        Assert.Null(none);
        Assert.Equal("disk", flow!.ReadFrom);
        Assert.Equal("name: orders\n", flow.Text);
        Assert.Null(flow.Branch);

        // An untracked file git ignores never reaches the repository, so its content is not shown.
        foreach (var ignored in new[] { "data/rows.csv", "scratch.tmp" })
        {
            var (_, notShown) = RepoFileEndpoints.ReadFromDisk(_root, ignored);
            Assert.Equal(StatusCodes.Status403Forbidden, notShown!.StatusCode);
            Assert.Contains($"Git ignores '{ignored}'", notShown.ProblemDetails.Detail, StringComparison.Ordinal);
        }

        // A tracked file is the repository's even where an ignore pattern matches it.
        var (kept, _) = RepoFileEndpoints.ReadFromDisk(_root, "kept.tmp");
        Assert.Equal("tracked although a pattern matches it\n", kept!.Text);

        var (_, missing) = RepoFileEndpoints.ReadFromDisk(_root, "flows/gone.yaml");
        Assert.Equal(StatusCodes.Status404NotFound, missing!.StatusCode);

        var (_, folder) = RepoFileEndpoints.ReadFromDisk(_root, "flows");
        Assert.Equal(StatusCodes.Status400BadRequest, folder!.StatusCode);

        // The read holds itself to the folder even for a path the route would have refused.
        var (_, outside) = RepoFileEndpoints.ReadFromDisk(Path.Combine(_root, "flows"), "../data/rows.csv");
        Assert.Equal(StatusCodes.Status400BadRequest, outside!.StatusCode);
        Assert.Contains("is not inside the repository", outside.ProblemDetails.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderInNoGitWorkingTree_HasNoIgnoreRulesToAsk()
    {
        File.WriteAllText(Path.Combine(_root, "notes.tmp"), "kept\n");

        var (file, none) = RepoFileEndpoints.ReadFromDisk(_root, "notes.tmp");

        Assert.Null(none);
        Assert.Equal("kept\n", file!.Text);
    }
}
