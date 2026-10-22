using System.Globalization;
using System.Security.Cryptography;
using LibGit2Sharp;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;
using SqlFlow.SourceControl.GitHub;
using SqlFlow.SourceControl.Proposals;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A real GitHub App against real GitHub: the full tier proves the whole credential path works where it matters, not
/// only against a scripted API. It needs a test App installed on a private test repository, named by
/// <see cref="LiveGitHubApp"/>'s variables (from the environment, where the full tier's runner puts them from
/// <c>.sqlflow/env</c>), and fails, never skips, without them. The App holds read-only access, as a deployment's
/// does, and a run writes nothing: the App and the repository are the suite's standing fixtures.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitHubAppLiveTests : IDisposable
{
    private readonly LiveGitHubApp _app = LiveGitHubApp.Require();
    private readonly GitHubAppTokenClient _client;
    private readonly SecretResolver _resolver;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "sqlflow_live_" + Guid.NewGuid().ToString("N"));

    public GitHubAppLiveTests()
    {
        _client = new GitHubAppTokenClient(() => _resolver!);
        _resolver = new SecretResolver([new EnvSecretProvider(), new LiveKeyProvider(_app), new GitHubAppSecretProvider(_client, _app.HostVariable)]);
    }

    public void Dispose()
    {
        _client.Dispose();
        DeleteDir(_cache);
    }

    [Fact]
    public async Task TheApp_IsKnownToGitHub_AndInstalledWhereTheTestRepositoryLives()
    {
        var settings = GitHubAppEnvironment.Read(_app.HostVariable);

        var details = await _client.GetAppAsync(settings.App!, settings.ApiRoot);

        Assert.False(string.IsNullOrWhiteSpace(details.Name));
        Assert.Contains(details.Installations, i => string.Equals(i.Account, _app.Repository.Owner, StringComparison.OrdinalIgnoreCase) && i.SuspendedUtc is null);
    }

    [Fact]
    public async Task TheHostsApp_ReadsThePrivateTestRepository_WhichNoCredentialCanRead()
    {
        var anonymous = Assert.Throws<SqlFlowNodeException>(() => GitMaterializer.ReadBranchTip(_app.RemoteUrl, string.Empty, credentials: null));
        Assert.Contains("none was given", anonymous.Message, StringComparison.Ordinal);

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.True(resolution.FromHost);
        var tip = GitMaterializer.ReadBranchTip(_app.RemoteUrl, string.Empty, resolution.Credentials);
        var checkout = new GitMaterializer(_cache).MaterializeBranch(_app.RemoteUrl, string.Empty, resolution.Credentials);
        Assert.Equal(tip, checkout.CommitSha);
        Assert.Contains(Directory.EnumerateFileSystemEntries(checkout.WorkingDirectory), e => !e.EndsWith(".git", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASourceReferenceNamingTheApp_ReadsTheRepository()
    {
        var resolution = await Resolve($"${{githubapp:app={_app.AppId};key=livekey:app}}", GitAccess.Read);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.False(resolution.FromHost);
        Assert.Matches("^[0-9a-f]{40}$", GitMaterializer.ReadBranchTip(_app.RemoteUrl, string.Empty, resolution.Credentials));
    }

    [Fact]
    public async Task AReadOnlyApp_CannotPush_AndAWriteTokenIsRefused_NamingWhatToGrant()
    {
        var read = await Resolve(null, GitAccess.Read);
        var branch = "sqlflow-live-test/" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..12];
        var publisher = new GitProposalPublisher(Path.Combine(_cache, "proposals"));

        // The token a sync reads with may read the repository's contents and nothing more: the push is refused, so
        // nothing reaches the repository.
        await Assert.ThrowsAsync<SqlFlowException>(() => publisher.PublishAsync(Proposal(DefaultBranch(read), branch, read.Credentials!)));
        Assert.Throws<SqlFlowNodeException>(() => GitMaterializer.ReadBranchTip(_app.RemoteUrl, branch, read.Credentials));

        // A proposal asks for more than a read-only App holds, and is told what to grant.
        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => Resolve(null, GitAccess.Write));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("is not granted contents:write,pull_requests:write", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APublicRepositoryTheAppIsNotInstalledOn_IsReadWithoutACredential()
    {
        const string publicRemote = "https://github.com/octocat/Hello-World.git";

        var resolution = await GitMaterializer.ResolveCredentialAsync(
            _resolver, null, null, publicRemote, GitAccess.Read, _app.HostVariable, CancellationToken.None);

        Assert.Equal(GitCredentialKind.None, resolution.Kind);
        Assert.Contains("is not installed on octocat/Hello-World", resolution.Summary, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", GitMaterializer.ReadBranchTip(publicRemote, string.Empty, resolution.Credentials));
    }

    [Fact]
    public async Task AKeyThatIsNotTheApps_IsRefusedByGitHub_SayingWhatToCheck()
    {
        using var stranger = RSA.Create(2048);
        var pem = stranger.ExportRSAPrivateKeyPem();
        string? Variable(string name) => name switch
        {
            GitHubAppEnvironment.AppIdVariable => _app.AppId,
            GitHubAppEnvironment.PrivateKeyVariable => pem,
            _ => null,
        };
        using var client = new GitHubAppTokenClient(() => _resolver);
        var resolver = new SecretResolver([new GitHubAppSecretProvider(client, Variable)]);

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => GitMaterializer.ResolveCredentialAsync(
            resolver, null, null, _app.RemoteUrl, GitAccess.Read, Variable, CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("one of the App's current keys", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APermissionTheAppLacks_IsNamed()
    {
        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => _resolver.ResolveAsync(
            $"${{githubapp:repository={_app.Repository};permissions=administration:write}}"));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("is not granted administration:write", ex.Message, StringComparison.Ordinal);
    }

    private Task<GitCredentialResolution> Resolve(string? reference, GitAccess access)
        => GitMaterializer.ResolveCredentialAsync(_resolver, reference, null, _app.RemoteUrl, access, _app.HostVariable, CancellationToken.None);

    private ProposalPublishRequest Proposal(string baseBranch, string branch, GitMaterializerCredentials credentials)
        => new(
            _app.RemoteUrl, baseBranch, branch, "SQLFlow live test: a branch this test removes again",
            "SQLFlow live test", "sqlflow-live-test@users.noreply.github.com", credentials.Username, credentials.Secret,
            [new ProposalFile($"live-test/{branch.Replace('/', '-')}.txt", "Written by the SQLFlow GitHub App live test, and removed with its branch.\n")]);

    private string DefaultBranch(GitCredentialResolution resolution)
    {
        var handler = new LibGit2Sharp.Handlers.CredentialsHandler((_, _, _) =>
            new UsernamePasswordCredentials { Username = "x-access-token", Password = resolution.Credentials!.Secret });
        var head = Repository.ListRemoteReferences(_app.RemoteUrl, handler).OfType<SymbolicReference>().Single(r => r.CanonicalName == "HEAD");
        return head.TargetIdentifier["refs/heads/".Length..];
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

    /// <summary>The test App's key as <c>${livekey:app}</c>, for a reference naming the App itself.</summary>
    private sealed class LiveKeyProvider(LiveGitHubApp app) : ISecretProvider
    {
        public string Scheme => "livekey";

        public string Resolve(string locator)
            => locator == "app" ? app.PrivateKeyPem() : throw new SqlFlowException($"livekey '{locator}' is not set.");
    }
}

/// <summary>
/// The full tier's test App: its client ID or App ID, its private key (the PEM, or the path of the .pem file), and the
/// private repository it is installed on, with read-only access to its contents (Contents: Read-only, Metadata:
/// Read-only), as a deployment's App has. The live suite reads them from these variables and stands them in for the
/// host's own <c>SQLFLOW_GITHUB_APP_*</c>.
/// </summary>
internal sealed class LiveGitHubApp
{
    public const string AppIdVariable = "SQLFLOW_TEST_GITHUB_APP_ID";
    public const string PrivateKeyVariable = "SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY";
    public const string PrivateKeyPathVariable = "SQLFLOW_TEST_GITHUB_APP_PRIVATE_KEY_PATH";
    public const string RepositoryVariable = "SQLFLOW_TEST_GITHUB_REPOSITORY";

    private LiveGitHubApp(string appId, string? key, string? keyPath, GitHubRepositoryName repository)
    {
        AppId = appId;
        Key = key;
        KeyPath = keyPath;
        Repository = repository;
    }

    public string AppId { get; }

    public string? Key { get; }

    public string? KeyPath { get; }

    public GitHubRepositoryName Repository { get; }

    public string RemoteUrl => $"https://github.com/{Repository}.git";

    /// <summary>The test App, or a failure naming every variable that is missing and what it holds.</summary>
    public static LiveGitHubApp Require()
    {
        var appId = Value(AppIdVariable);
        var key = Value(PrivateKeyVariable);
        var keyPath = Value(PrivateKeyPathVariable);
        var repository = GitHubRepositoryName.TryParse(Value(RepositoryVariable));
        if (appId is null || (key is null && keyPath is null) || repository is null)
        {
            throw new InvalidOperationException(
                $"The GitHub App live suite runs in the full tier and needs a test App: set {AppIdVariable} (its client ID), "
                + $"{PrivateKeyPathVariable} (its .pem file) or {PrivateKeyVariable} (the PEM), and {RepositoryVariable} "
                + "(owner/name of a private repository the App is installed on, with Contents: Read-only), in the environment "
                + "(the full tier reads them from .sqlflow/env). docs/reference/guides/github-app.md says how to make one.");
        }

        return new LiveGitHubApp(appId, key, keyPath, repository);
    }

    /// <summary>The test App as the host's App: what the code under test reads for <c>SQLFLOW_GITHUB_APP_*</c>.</summary>
    public string? HostVariable(string name) => name switch
    {
        GitHubAppEnvironment.AppIdVariable => AppId,
        GitHubAppEnvironment.PrivateKeyVariable => Key,
        GitHubAppEnvironment.PrivateKeyPathVariable => Key is null ? KeyPath : null,
        _ => null,
    };

    public string PrivateKeyPem() => Key ?? File.ReadAllText(KeyPath!);

    private static string? Value(string name)
        => Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
}
