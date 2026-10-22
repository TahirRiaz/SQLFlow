using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using SqlFlow.Core;
using SqlFlow.Core.Secrets;
using SqlFlow.Execution;
using SqlFlow.Node;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The one credential path of every git operation, against a scripted GitHub: a source's own reference wins (a token
/// reference as written, a GitHub App reference narrowed to the source's repository and the operation's permissions),
/// and a source naming none takes the host's GitHub App for a remote on the App's GitHub, then the host's token, then
/// nothing, saying in each case which credential it is and, where the App was passed over, why. The description a page
/// shows follows the same order without resolving anything.
/// </summary>
public sealed class GitCredentialResolutionTests : IDisposable
{
    private const string AppToken = "ghs_resolutionToken0123456789abcdefAB";
    private const string Remote = "https://github.com/acme/flows.git";

    private static readonly Lazy<RSA> Key = new(() => RSA.Create(2048));

    private readonly Dictionary<string, string> _environment = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal) { ["app"] = Key.Value.ExportRSAPrivateKeyPem() };
    private readonly ConcurrentQueue<(HttpMethod Method, string Path, string? Body)> _requests = new();
    private readonly GitHubAppTokenClient _client;
    private readonly SecretResolver _resolver;
    private Func<HttpMethod, string, HttpResponseMessage> _answer;

    public GitCredentialResolutionTests()
    {
        _answer = (method, _) => method == HttpMethod.Get ? Installation(77) : Token();
        _client = new GitHubAppTokenClient(() => _resolver!, TimeProvider.System, logger: null, new Handler(this), options: null);
        _resolver = new SecretResolver([new EnvSecretProvider(), new KeyProvider(_secrets), new GitHubAppSecretProvider(_client, Variable)]);
    }

    public void Dispose() => _client.Dispose();

    [Fact]
    public async Task NoReference_NoAppAndNoToken_ReadsWithoutACredential()
    {
        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Null(resolution.Credentials);
        Assert.Equal(GitCredentialKind.None, resolution.Kind);
        Assert.Equal("no credential", resolution.Summary);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task NoReference_TheHostToken_WithItsUserName()
    {
        _environment[GitMaterializer.HostTokenVariable] = " host-token-value ";
        _environment[GitMaterializer.HostTokenUsernameVariable] = "x-bitbucket-api-token-auth";

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Equal(GitCredentialKind.HostToken, resolution.Kind);
        Assert.True(resolution.FromHost);
        Assert.Equal("host-token-value", resolution.Credentials!.Secret);
        Assert.Equal("x-bitbucket-api-token-auth", resolution.Credentials.Username);
        Assert.Equal("this host's SQLFLOW_GIT_TOKEN", resolution.Summary);
    }

    [Fact]
    public async Task NoReference_TheHostsApp_MintsATokenForTheSourcesRepositoryAlone_ToRead()
    {
        ConfigureHostApp();
        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.True(resolution.FromHost);
        Assert.Equal(AppToken, resolution.Credentials!.Secret);
        Assert.Null(resolution.Credentials.Username);
        Assert.Equal("this host's GitHub App Iv23liHost, with a token for acme/flows (contents:read)", resolution.Summary);
        Assert.Equal("/repos/acme/flows/installation", _requests.First().Path);
        AssertTokenBody(repository: "flows", ("contents", "read"));
    }

    [Fact]
    public async Task NoReference_TheHostsApp_ToProposeChanges_MayPushAndOpenAPullRequest()
    {
        ConfigureHostApp();

        var resolution = await Resolve(null, GitAccess.Write);

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        AssertTokenBody(repository: "flows", ("contents", "write"), ("pull_requests", "write"));
    }

    [Fact]
    public async Task NoReference_WhereTheHostsAppIsNotInstalled_FallsBackToTheHostToken_SayingWhy()
    {
        ConfigureHostApp();
        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";
        _answer = (_, _) => Error(HttpStatusCode.NotFound, "Not Found");

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Equal(GitCredentialKind.HostToken, resolution.Kind);
        Assert.Equal("host-token-value", resolution.Credentials!.Secret);
        Assert.StartsWith("this host's SQLFLOW_GIT_TOKEN, because GitHub App Iv23liHost is not installed on acme/flows", resolution.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoReference_WhereTheHostsAppIsNotInstalled_AndNoToken_ReadsWithoutACredential_SayingWhy()
    {
        ConfigureHostApp();
        _answer = (_, _) => Error(HttpStatusCode.NotFound, "Not Found");

        var resolution = await Resolve(null, GitAccess.Read);

        Assert.Null(resolution.Credentials);
        Assert.Equal(GitCredentialKind.None, resolution.Kind);
        Assert.StartsWith("no credential, because GitHub App Iv23liHost is not installed on acme/flows", resolution.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoReference_WhenGitHubRefusesTheHostsApp_TheFailureIsReported_NotHidden()
    {
        ConfigureHostApp();
        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";
        _answer = (_, _) => Error(HttpStatusCode.Unauthorized, "A JSON web token could not be decoded");

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => Resolve(null, GitAccess.Read));

        Assert.Contains("refused the signed token of GitHub App Iv23liHost", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bitbucket.org/acme/flows.git")]
    [InlineData("http://github.com/acme/flows.git")]
    [InlineData("C:\\repos\\flows")]
    public async Task NoReference_ARemoteTheHostsAppDoesNotServe_TakesTheHostToken_WithoutAskingGitHub(string remote)
    {
        ConfigureHostApp();
        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";

        var resolution = await Resolve(null, GitAccess.Read, remote);

        Assert.Equal(GitCredentialKind.HostToken, resolution.Kind);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task NoReference_AHostAppMisconfigured_IsRefused_NamingTheVariable()
    {
        _environment[GitHubAppEnvironment.AppIdVariable] = "Iv23liHost";
        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => Resolve(null, GitAccess.Read));

        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATokenReference_IsUsedAsWritten_WithTheSourcesUserName()
    {
        ConfigureHostApp();
        _secrets["source-token"] = "source-token-value";

        var resolution = await Resolve("${testkey:source-token}", GitAccess.Read, username: "git-user");

        Assert.Equal(GitCredentialKind.Reference, resolution.Kind);
        Assert.False(resolution.FromHost);
        Assert.Equal("source-token-value", resolution.Credentials!.Secret);
        Assert.Equal("git-user", resolution.Credentials.Username);
        Assert.Equal("the token the source's reference ${testkey:source-token} names", resolution.Summary);
        Assert.Empty(_requests);
    }

    [Fact]
    public async Task AReferenceResolvingToNothing_IsRefused()
    {
        _secrets["empty"] = " ";

        var ex = await Assert.ThrowsAsync<SqlFlowNodeException>(() => Resolve("${testkey:empty}", GitAccess.Read));

        Assert.Contains("resolved to an empty value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppReference_IsNarrowedToTheSourcesRepository_AndSignsInAsTheApp()
    {
        var resolution = await Resolve("${githubapp:app=123456;key=testkey:app}", GitAccess.Read, username: "ignored-user");

        Assert.Equal(GitCredentialKind.GitHubApp, resolution.Kind);
        Assert.False(resolution.FromHost);
        Assert.Equal(AppToken, resolution.Credentials!.Secret);
        Assert.Null(resolution.Credentials.Username);
        Assert.Equal(
            "the GitHub App the source's reference ${githubapp:app=123456;key=testkey:app} names, with a token for acme/flows (contents:read)",
            resolution.Summary);
        AssertTokenBody(repository: "flows", ("contents", "read"));
    }

    [Fact]
    public async Task AnAppReference_NamingItsOwnPermissions_KeepsThem()
    {
        await Resolve("${githubapp:app=123456;key=testkey:app;permissions=contents:read,metadata:read}", GitAccess.Write);

        AssertTokenBody(repository: "flows", ("contents", "read"), ("metadata", "read"));
    }

    [Fact]
    public async Task AnAppReference_NamingAnotherRepository_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<GitHubAppException>(
            () => Resolve("${githubapp:app=123456;key=testkey:app;repository=acme/other}", GitAccess.Read));

        Assert.Contains("names the repository acme/other, but the remote is acme/flows", ex.Message, StringComparison.Ordinal);
        Assert.Empty(_requests);
    }

    [Fact]
    public void Describe_ALocalFolder_NeedsNoCredential()
    {
        var plan = Describe(null, remote: null);

        Assert.Equal(GitCredentialKind.None, plan.Kind);
        Assert.Contains("local folder", plan.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_FollowsTheResolutionOrder_WithoutAskingAnything()
    {
        Assert.Equal(GitCredentialKind.None, Describe(null).Kind);

        _environment[GitMaterializer.HostTokenVariable] = "host-token-value";
        Assert.Equal(new GitCredentialPlan(GitCredentialKind.HostToken, true, "this host's SQLFLOW_GIT_TOKEN", null), Describe(null));

        ConfigureHostApp();
        var app = Describe(null);
        Assert.Equal(GitCredentialKind.GitHubApp, app.Kind);
        Assert.True(app.FromHost);
        Assert.Equal("this host's GitHub App Iv23liHost, with a token for acme/flows (or SQLFLOW_GIT_TOKEN where the App is not installed)", app.Summary);

        Assert.Equal(GitCredentialKind.HostToken, Describe(null, "https://bitbucket.org/acme/flows.git").Kind);

        var reference = Describe("${keyvault:vault/git-token}");
        Assert.Equal(GitCredentialKind.Reference, reference.Kind);
        Assert.False(reference.FromHost);

        var appReference = Describe("${githubapp:app=1;key=keyvault:v/k}");
        Assert.Equal(GitCredentialKind.GitHubApp, appReference.Kind);
        Assert.Null(appReference.Problem);
        Assert.Contains("acme/flows (contents:read)", appReference.Summary, StringComparison.Ordinal);

        Assert.Empty(_requests);
    }

    [Fact]
    public void Describe_SaysWhyAConfigurationCannotBeUsed()
    {
        var malformed = Describe("${githubapp:instalation=1}");
        Assert.Equal(GitCredentialKind.GitHubApp, malformed.Kind);
        Assert.Contains("unknown setting", malformed.Problem, StringComparison.Ordinal);

        _environment[GitHubAppEnvironment.AppIdVariable] = "Iv23liHost";
        var misconfigured = Describe(null);
        Assert.Equal(GitCredentialKind.GitHubApp, misconfigured.Kind);
        Assert.True(misconfigured.FromHost);
        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, misconfigured.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Reason_SaysHowToGiveACredential_WhenARemoteAskedForOneAndNoneWasGiven()
    {
        var refused = new LibGit2SharpException("too many redirects or authentication replays");

        Assert.Contains("install this host's GitHub App on the repository", GitMaterializer.Reason(refused, credentials: null), StringComparison.Ordinal);
        Assert.Equal(refused.Message, GitMaterializer.Reason(refused, new GitMaterializerCredentials(null, "secret")));
        Assert.Equal("object not found", GitMaterializer.Reason(new LibGit2SharpException("object not found"), credentials: null));
    }

    [Fact]
    public void Credentials_ToString_NeverShowsTheSecret()
    {
        var credentials = new GitMaterializerCredentials("user", "very-secret-value");

        Assert.DoesNotContain("very-secret-value", credentials.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-value", new GitCredentialResolution(credentials, GitCredentialKind.Reference, false, "x").ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("very-secret-value", new SqlFlow.SourceControl.GitCredentials { Secret = "very-secret-value" }.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadBranchTip_ReadsTheTipWithoutCloning_AndSaysWhenTheBranchIsMissing()
    {
        var remote = Path.Combine(Path.GetTempPath(), "sqlflow_tip_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(remote);
        try
        {
            Repository.Init(remote);
            string sha;
            string branch;
            using (var repo = new Repository(remote))
            {
                var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
                File.WriteAllText(Path.Combine(remote, "flow.yaml"), "v1");
                Commands.Stage(repo, "*");
                sha = repo.Commit("v1", signature, signature).Sha;
                branch = repo.Head.FriendlyName;
            }

            Assert.Equal(sha, GitMaterializer.ReadBranchTip(remote, branch, credentials: null));
            var ex = Assert.Throws<SqlFlowNodeException>(() => GitMaterializer.ReadBranchTip(remote, "no-such-branch", credentials: null));
            Assert.Contains("has no branch 'no-such-branch'", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            foreach (var file in Directory.EnumerateFiles(remote, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(remote, recursive: true);
        }
    }

    [Fact]
    public void TheEngine_RegistersTheGitHubAppScheme_BesideEnvAndKeyVault_WithoutACycle()
    {
        using var services = new ServiceCollection().AddLogging().AddSqlFlowEngine().BuildServiceProvider();

        var schemes = services.GetServices<ISecretProvider>().Select(p => p.Scheme).ToList();
        Assert.Contains("env", schemes);
        Assert.Contains("keyvault", schemes);
        Assert.Contains("githubapp", schemes);
        Assert.Same(services.GetRequiredService<GitHubAppTokenClient>(), services.GetRequiredService<GitHubAppTokenClient>());

        // The resolver reaches the provider, which parses before anything is asked of GitHub.
        var resolver = services.GetRequiredService<ISecretResolver>();
        var ex = Assert.Throws<GitHubAppException>(() => resolver.Resolve("${githubapp:instalation=1}"));
        Assert.Contains("unknown setting", ex.Message, StringComparison.Ordinal);
    }

    private Task<GitCredentialResolution> Resolve(string? reference, GitAccess access, string remote = Remote, string? username = null)
        => GitMaterializer.ResolveCredentialAsync(_resolver, reference, username, remote, access, Variable, CancellationToken.None);

    private GitCredentialPlan Describe(string? reference, string? remote = Remote)
        => GitMaterializer.DescribeCredential(reference, remote, Variable);

    private string? Variable(string name) => _environment.TryGetValue(name, out var value) ? value : null;

    private void ConfigureHostApp()
    {
        _environment[GitHubAppEnvironment.AppIdVariable] = "Iv23liHost";
        _environment[GitHubAppEnvironment.PrivateKeyVariable] = "${testkey:app}";
    }

    private void AssertTokenBody(string repository, params (string Name, string Level)[] permissions)
    {
        var mint = _requests.Single(r => r.Method == HttpMethod.Post);
        using var body = JsonDocument.Parse(mint.Body!);
        Assert.Equal(repository, Assert.Single(body.RootElement.GetProperty("repositories").EnumerateArray()).GetString());
        var granted = body.RootElement.GetProperty("permissions").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        Assert.Equal(permissions.Length, granted.Count);
        foreach (var (name, level) in permissions)
        {
            Assert.Equal(level, granted[name]);
        }
    }

    private static HttpResponseMessage Token()
        => Json(HttpStatusCode.Created, $$"""{"token":"{{AppToken}}","expires_at":"{{DateTimeOffset.UtcNow.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}}"}""");

    private static HttpResponseMessage Installation(long id) => Json(HttpStatusCode.OK, $$$"""{"id":{{{id}}},"account":{"login":"acme"}}""");

    private static HttpResponseMessage Error(HttpStatusCode status, string message) => Json(status, JsonSerializer.Serialize(new { message }));

    private static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        response.Headers.Date = DateTimeOffset.UtcNow;
        return response;
    }

    /// <summary>The test's secrets (the App's key is <c>${testkey:app}</c>), without the process environment, which
    /// <c>${env:...}</c> would read.</summary>
    private sealed class KeyProvider(Dictionary<string, string> secrets) : ISecretProvider
    {
        public string Scheme => "testkey";

        public string Resolve(string locator)
            => secrets.TryGetValue(locator, out var value) ? value : throw new SqlFlowException($"testkey '{locator}' is not set.");
    }

    private sealed class Handler(GitCredentialResolutionTests owner) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            owner._requests.Enqueue((request.Method, request.RequestUri!.AbsolutePath, body));
            return owner._answer(request.Method, request.RequestUri.AbsolutePath);
        }
    }
}
