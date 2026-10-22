using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Mvc;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core.Secrets;
using SqlFlow.Node;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The control plane's git-credential surface through its real host: what the host's GitHub App and token are (never a
/// secret), the check that asks GitHub about the App as the App, and the access check that reads a remote's tip through
/// the same credential path as a sync. The host's environment is set per test and put back after it; the assembly runs
/// serially, so no other test sees it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitCredentialEndpointTests
{
    private static readonly Lazy<RSA> Key = new(() => RSA.Create(2048));

    private static readonly System.Text.Json.JsonSerializerOptions Web = new(System.Text.Json.JsonSerializerDefaults.Web);

    [Fact]
    public async Task CredentialsStatus_WithNoApp_SaysSo_AndWhetherTheHostHasAToken()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, null), (GitHubAppEnvironment.PrivateKeyVariable, null),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null),
            (GitMaterializer.HostTokenVariable, "host-token-value"), (GitMaterializer.HostTokenUsernameVariable, null));
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        var status = await GetJsonAsync<GitCredentialsStatusDto>(client, token, "/api/v1/repos/credentials");

        Assert.False(status.GitHubApp.Configured);
        Assert.Null(status.GitHubApp.Problem);
        Assert.Equal("https://api.github.com", status.GitHubApp.ApiUrl);
        Assert.Equal("github.com", status.GitHubApp.Host);
        Assert.True(status.HostToken.Configured);
        Assert.Null(status.HostToken.Username);
    }

    [Fact]
    public async Task CredentialsStatus_WithAnApp_NamesItAndWhereItsKeyIsRead_NeverTheKey()
    {
        var pem = Key.Value.ExportRSAPrivateKeyPem();
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, "Iv23liStatus"), (GitHubAppEnvironment.PrivateKeyVariable, pem),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null),
            (GitMaterializer.HostTokenVariable, null), (GitMaterializer.HostTokenUsernameVariable, null));
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        var body = await GetStringAsync(client, token, "/api/v1/repos/credentials");
        var status = System.Text.Json.JsonSerializer.Deserialize<GitCredentialsStatusDto>(body, Web)!;

        Assert.True(status.GitHubApp.Configured);
        Assert.Equal("Iv23liStatus", status.GitHubApp.AppId);
        Assert.Equal(GitHubAppEnvironment.PrivateKeyVariable, status.GitHubApp.KeySource);
        Assert.False(status.HostToken.Configured);
        Assert.DoesNotContain("PRIVATE KEY", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CredentialsStatus_WithAPartialApp_ReportsTheProblem()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, "Iv23liPartial"), (GitHubAppEnvironment.PrivateKeyVariable, null),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null));
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["read"]);

        var status = await GetJsonAsync<GitCredentialsStatusDto>(client, token, "/api/v1/repos/credentials");

        Assert.False(status.GitHubApp.Configured);
        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, status.GitHubApp.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckGitHubApp_WithNoApp_IsAConflict_NamingTheVariables()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, null), (GitHubAppEnvironment.PrivateKeyVariable, null),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null));
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/credentials/github-app/check", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("No GitHub App", problem!.Title);
        Assert.Contains(GitHubAppEnvironment.AppIdVariable, problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckGitHubApp_AsksGitHubAsTheApp_AndListsWhereItIsInstalled()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, "Iv23liCheck"), (GitHubAppEnvironment.PrivateKeyVariable, Key.Value.ExportRSAPrivateKeyPem()),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null));
        var github = new ScriptedGitHub(path => path == "/app"
            ? (HttpStatusCode.OK, """{"name":"SQLFlow sync","slug":"sqlflow-sync","owner":{"login":"acme"},"html_url":"https://github.com/apps/sqlflow-sync"}""")
            : (HttpStatusCode.OK, """[{"id":77,"account":{"login":"acme","type":"Organization"},"repository_selection":"selected","suspended_at":null}]"""));
        await using var factory = new ControlPlaneAppFactory().WithServices(services => UseGitHub(services, github));
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/credentials/github-app/check", new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = await response.Content.ReadFromJsonAsync<GitHubAppCheckDto>();
        Assert.Equal("Iv23liCheck", check!.AppId);
        Assert.Equal("SQLFlow sync", check.Name);
        Assert.Equal("acme", check.Owner);
        var installation = Assert.Single(check.Installations);
        Assert.Equal(77, installation.Id);
        Assert.Equal("selected", installation.RepositorySelection);
        Assert.False(check.MoreInstallations);
    }

    [Fact]
    public async Task CheckGitHubApp_WhenGitHubRefusesTheApp_SaysWhatToCheck()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, "Iv23liRefused"), (GitHubAppEnvironment.PrivateKeyVariable, Key.Value.ExportRSAPrivateKeyPem()),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null));
        var github = new ScriptedGitHub(_ => (HttpStatusCode.Unauthorized, """{"message":"A JSON web token could not be decoded"}"""));
        await using var factory = new ControlPlaneAppFactory().WithServices(services => UseGitHub(services, github));
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/credentials/github-app/check", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal("GitHub App check failed", problem!.Title);
        Assert.Contains("one of the App's current keys", problem.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckRepoAccess_ReadsTheTip_AndNamesTheCredentialItReadWith()
    {
        using var environment = new EnvironmentScope(
            (GitHubAppEnvironment.AppIdVariable, null), (GitHubAppEnvironment.PrivateKeyVariable, null),
            (GitHubAppEnvironment.PrivateKeyPathVariable, null), (GitHubAppEnvironment.ApiUrlVariable, null),
            (GitMaterializer.HostTokenVariable, null));
        var remote = NewRepository(out var branch, out var sha);
        try
        {
            await using var factory = new ControlPlaneAppFactory();
            using var client = factory.CreateClient();
            var token = await IssueTokenAsync(client, ["operate"]);

            using var response = await PostAsync(client, token, "/api/v1/repos/check", new CheckRepoAccessRequest(remote, branch, null, null));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var access = await response.Content.ReadFromJsonAsync<RepoAccessDto>();
            Assert.Equal(sha, access!.CommitSha);
            Assert.Equal(branch, access.Branch);
            Assert.Equal("none", access.Credential.Kind);

            using var missing = await PostAsync(client, token, "/api/v1/repos/check", new CheckRepoAccessRequest(remote, "no-such-branch", null, null));
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var problem = await missing.Content.ReadFromJsonAsync<ProblemDetails>();
            Assert.Equal("Repository unreachable", problem!.Title);
            Assert.Contains("has no branch 'no-such-branch'", problem.Detail, StringComparison.Ordinal);
            Assert.Contains("(read with no credential)", problem.Detail, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDir(remote);
        }
    }

    [Theory]
    [InlineData("ghp_pastedRawTokenValue0123456789abcdef", "Invalid request")]
    [InlineData("${githubapp:instalation=1}", "Credential unavailable")]
    public async Task CheckRepoAccess_RefusesARawTokenOrAnUnusableReference(string credential, string title)
    {
        await using var factory = new ControlPlaneAppFactory();
        using var client = factory.CreateClient();
        var token = await IssueTokenAsync(client, ["operate"]);

        using var response = await PostAsync(client, token, "/api/v1/repos/check",
            new CheckRepoAccessRequest("https://github.com/acme/flows.git", "main", credential, null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.Equal(title, problem!.Title);
        Assert.DoesNotContain("ghp_pastedRawTokenValue", problem.Detail, StringComparison.Ordinal);
    }

    private static void UseGitHub(IServiceCollection services, ScriptedGitHub github)
    {
        services.RemoveAll<GitHubAppTokenClient>();
        services.AddSingleton(sp => new GitHubAppTokenClient(
            () => sp.GetRequiredService<ISecretResolver>(), TimeProvider.System, logger: null, github, options: null));
    }

    private static string NewRepository(out string branch, out string sha)
    {
        var path = Path.Combine(Path.GetTempPath(), "sqlflow_access_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Repository.Init(path);
        using var repo = new Repository(path);
        var signature = new Signature("Test", "test@example.com", DateTimeOffset.UtcNow);
        File.WriteAllText(Path.Combine(path, "flow.yaml"), "v1");
        Commands.Stage(repo, "*");
        sha = repo.Commit("v1", signature, signature).Sha;
        branch = repo.Head.FriendlyName;
        return path;
    }

    private static void DeleteDir(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string url, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url, UriKind.Relative)) { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task<string> GetStringAsync(HttpClient client, string token, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url, UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<T> GetJsonAsync<T>(HttpClient client, string token, string url)
        => System.Text.Json.JsonSerializer.Deserialize<T>(
            await GetStringAsync(client, token, url), Web)!;

    private static async Task<string> IssueTokenAsync(HttpClient client, IReadOnlyList<string> scopes)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/token", UriKind.Relative),
            new TokenRequest(ControlPlaneAppFactory.BootstrapSecret, null, scopes));
        response.EnsureSuccessStatusCode();
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>();
        Assert.NotNull(token);
        return token.AccessToken;
    }

    /// <summary>Sets process environment variables for one test (null removes one) and puts every one back after it.</summary>
    private sealed class EnvironmentScope : IDisposable
    {
        private readonly List<(string Name, string? Previous)> _saved = [];

        public EnvironmentScope(params (string Name, string? Value)[] variables)
        {
            foreach (var (name, value) in variables)
            {
                _saved.Add((name, Environment.GetEnvironmentVariable(name)));
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, previous) in _saved)
            {
                Environment.SetEnvironmentVariable(name, previous);
            }
        }
    }

    /// <summary>A GitHub API answering each path as the test scripts it, dated now.</summary>
    private sealed class ScriptedGitHub(Func<string, (HttpStatusCode Status, string Json)> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var (status, json) = answer(request.RequestUri!.AbsolutePath);
            var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            response.Headers.Date = DateTimeOffset.UtcNow;
            return Task.FromResult(response);
        }
    }
}
