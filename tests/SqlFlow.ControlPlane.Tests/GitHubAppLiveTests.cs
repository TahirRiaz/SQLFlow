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
/// <c>.sqlflow/env</c>). A test App is optional, as a GitHub App is for a deployment that reads with a token: with none
/// of the variables set the suite is skipped, saying which to set, and with only some of them it fails, naming the
/// rest. The App holds read-only access, as a deployment's does, and a run writes nothing: the App and the repository
/// are the suite's standing fixtures.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitHubAppLiveTests : IDisposable
{
    private readonly LiveGitHubApp? _app = LiveGitHubApp.FromEnvironment();
    private readonly GitHubAppTokenClient _client;
    private readonly SecretResolver _resolver;
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "sqlflow_live_" + Guid.NewGuid().ToString("N"));

    public GitHubAppLiveTests()
    {
        _client = new GitHubAppTokenClient(() => _resolver!);
        _resolver = new SecretResolver([new EnvSecretProvider(), new LiveKeyProvider(() => App), new GitHubAppSecretProvider(_client, name => _app?.HostVariable(name))]);
    }

    /// <summary>The test App, or a skip naming the variables that configure one.</summary>
    private LiveGitHubApp App
    {
        get
        {
            Skip.If(_app is null, LiveGitHubApp.NotConfigured);
            return _app!;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
        DeleteDir(_cache);
    }

    [SkippableFact]
    public async Task TheApp_IsKnownToGitHub_AndInstalledWhereTheTestRepositoryLives()
    {
        var settings = GitHubAppEnvironment.Read(App.HostVariable);

        var details = await _client.GetAppAsync(settings.App!, settings.ApiRoot);

        Assert.False(string.IsNullOrWhiteSpace(details.Name));
        Assert.Contains(details.Installations, i => string.Equals(i.Account, App.Repository.Owner, StringComparison.OrdinalIgnoreCase) && i.SuspendedUtc is null);
    }

    [SkippableFact]
    public async Task TheHostsApp_ReadsThePrivateTestRepository_WhichNoCredentialCanRead()
    {
        var anonymous = Assert.Throws<SqlFlowNodeException>(() => GitMaterializer.ReadBranchTip(App.RemoteUrl, string.Empty, credentials: null));
        Assert.Contains("none was given", anonymous.Message, StringComparison.Ordinal);

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.True(resolution.FromHost);
        var tip = GitMaterializer.ReadBranchTip(App.RemoteUrl, string.Empty, resolution.Credentials);
        var checkout = new GitMaterializer(_cache).MaterializeBranch(App.RemoteUrl, string.Empty, resolution.Credentials);
        Assert.Equal(tip, checkout.CommitSha);
        Assert.Contains(Directory.EnumerateFileSystemEntries(checkout.WorkingDirectory), e => !e.EndsWith(".git", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task ASourceReferenceNamingTheApp_ReadsTheRepository()
    {
        var resolution = await Resolve($"${{githubapp:app={App.AppId};key=livekey:app}}", GitAccess.Read);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.False(resolution.FromHost);
        Assert.Matches("^[0-9a-f]{40}$", GitMaterializer.ReadBranchTip(App.RemoteUrl, string.Empty, resolution.Credentials));
    }

    [SkippableFact]
    public async Task AReadOnlyApp_CannotPush_AndAWriteTokenIsRefused_NamingWhatToGrant()
    {
        var read = await Resolve(null, GitAccess.Read);
        var branch = "sqlflow-live-test/" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..12];
        var publisher = new GitProposalPublisher(Path.Combine(_cache, "proposals"));

        // The token a sync reads with may read the repository's contents and nothing more: the push is refused, so
        // nothing reaches the repository.
        await Assert.ThrowsAsync<SqlFlowException>(() => publisher.PublishAsync(Proposal(DefaultBranch(read), branch, read.Credentials!)));
        Assert.Throws<SqlFlowNodeException>(() => GitMaterializer.ReadBranchTip(App.RemoteUrl, branch, read.Credentials));

        // A proposal asks for more than a read-only App holds, and is told what to grant.
        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => Resolve(null, GitAccess.Write));
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("is not granted contents:write,pull_requests:write", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task APublicRepositoryTheAppIsNotInstalledOn_IsReadWithoutACredential()
    {
        const string publicRemote = "https://github.com/octocat/Hello-World.git";

        var resolution = await GitMaterializer.ResolveCredentialAsync(
            _resolver, null, null, publicRemote, GitAccess.Read, App.HostVariable, CancellationToken.None);

        Assert.Equal(GitCredentialKind.None, resolution.Kind);
        Assert.Contains("is not installed on octocat/Hello-World", resolution.Summary, StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{40}$", GitMaterializer.ReadBranchTip(publicRemote, string.Empty, resolution.Credentials));
    }

    [SkippableFact]
    public async Task AKeyThatIsNotTheApps_IsRefusedByGitHub_SayingWhatToCheck()
    {
        using var stranger = RSA.Create(2048);
        var pem = stranger.ExportRSAPrivateKeyPem();
        string? Variable(string name) => name switch
        {
            GitHubAppEnvironment.AppIdVariable => App.AppId,
            GitHubAppEnvironment.PrivateKeyVariable => pem,
            _ => null,
        };
        using var client = new GitHubAppTokenClient(() => _resolver);
        var resolver = new SecretResolver([new GitHubAppSecretProvider(client, Variable)]);

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => GitMaterializer.ResolveCredentialAsync(
            resolver, null, null, App.RemoteUrl, GitAccess.Read, Variable, CancellationToken.None));

        Assert.Equal(401, ex.StatusCode);
        Assert.Contains("one of the App's current keys", ex.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task APermissionTheAppLacks_IsNamed()
    {
        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => _resolver.ResolveAsync(
            $"${{githubapp:repository={App.Repository};permissions=administration:write}}"));

        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("is not granted administration:write", ex.Message, StringComparison.Ordinal);
    }

    private Task<GitCredentialResolution> Resolve(string? reference, GitAccess access)
        => GitMaterializer.ResolveCredentialAsync(_resolver, reference, null, App.RemoteUrl, access, App.HostVariable, CancellationToken.None);

    private ProposalPublishRequest Proposal(string baseBranch, string branch, GitMaterializerCredentials credentials)
        => new(
            App.RemoteUrl, baseBranch, branch, "SQLFlow live test: a branch this test removes again",
            "SQLFlow live test", "sqlflow-live-test@users.noreply.github.com", credentials.Username, credentials.Secret,
            [new ProposalFile($"live-test/{branch.Replace('/', '-')}.txt", "Written by the SQLFlow GitHub App live test, and removed with its branch.\n")]);

    private string DefaultBranch(GitCredentialResolution resolution)
    {
        var handler = new LibGit2Sharp.Handlers.CredentialsHandler((_, _, _) =>
            new UsernamePasswordCredentials { Username = "x-access-token", Password = resolution.Credentials!.Secret });
        var head = Repository.ListRemoteReferences(App.RemoteUrl, handler).OfType<SymbolicReference>().Single(r => r.CanonicalName == "HEAD");
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
    private sealed class LiveKeyProvider(Func<LiveGitHubApp> app) : ISecretProvider
    {
        public string Scheme => "livekey";

        public string Resolve(string locator)
            => locator == "app" ? app().PrivateKeyPem() : throw new SqlFlowException($"livekey '{locator}' is not set.");
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

    /// <summary>Why the suite is skipped where no test App is configured, and how to configure one.</summary>
    public const string NotConfigured =
        $"No test GitHub App is configured, so the GitHub App live suite is skipped: set {AppIdVariable} (its client ID), "
        + $"{PrivateKeyPathVariable} (its .pem file) or {PrivateKeyVariable} (the PEM), and {RepositoryVariable} (owner/name "
        + "of a private repository the App is installed on, with Contents: Read-only), in the environment or in .sqlflow/env, "
        + "to run it. docs/reference/guides/github-app.md says how to make one.";

    /// <summary>
    /// The test App the environment names; null when it names none of it, which skips the suite. A test App named in part
    /// is a mistake rather than a choice, so it is refused, naming the settings it lacks.
    /// </summary>
    public static LiveGitHubApp? FromEnvironment()
    {
        var appId = Value(AppIdVariable);
        var key = Value(PrivateKeyVariable);
        var keyPath = Value(PrivateKeyPathVariable);
        var repositoryText = Value(RepositoryVariable);
        if (appId is null && key is null && keyPath is null && repositoryText is null)
        {
            return null;
        }

        var repository = GitHubRepositoryName.TryParse(repositoryText);
        var missing = new List<string>();
        if (appId is null)
        {
            missing.Add($"{AppIdVariable} (the App's client ID)");
        }

        if (key is null && keyPath is null)
        {
            missing.Add($"{PrivateKeyPathVariable} (its .pem file) or {PrivateKeyVariable} (the PEM)");
        }

        if (repository is null)
        {
            missing.Add(repositoryText is null
                ? $"{RepositoryVariable} (owner/name of the private repository the App is installed on)"
                : $"{RepositoryVariable} as owner/name ('{repositoryText}' is not)");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "The test GitHub App is configured only in part: set " + string.Join(", and ", missing)
                + ", or remove its settings to skip the GitHub App live suite. docs/reference/guides/github-app.md says how to make one.");
        }

        return new LiveGitHubApp(appId!, key, keyPath, repository!);
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
