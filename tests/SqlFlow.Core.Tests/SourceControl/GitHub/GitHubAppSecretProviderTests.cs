using System.Net;
using SqlFlow.Core.Secrets;
using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>
/// The <c>githubapp</c> secret scheme resolves through the same <see cref="SecretResolver"/> as <c>env</c> and
/// <c>keyvault</c>: a whole reference becomes an installation token, the App's key is itself read through a reference,
/// and what a reference leaves out comes from the host's environment.
/// </summary>
public sealed class GitHubAppSecretProviderTests
{
    private const string TokenValue = "ghs_providerToken0123456789abcdefABCD";

    [Fact]
    public async Task AReference_ResolvesToAnInstallationToken_OfTheHostsApp()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        Assert.Equal("githubapp", h.Provider.Scheme);
        Assert.Equal(TokenValue, await h.Resolver.ResolveAsync("${githubapp:installation=42}"));
        Assert.Equal("https://api.github.com/app/installations/42/access_tokens", h.GitHub.Requests.Single().Uri.ToString());
    }

    [Fact]
    public void AReference_ResolvesOnTheSynchronousPathToo()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        Assert.Equal(TokenValue, h.Resolver.Resolve("${githubapp:installation=42}"));
    }

    [Fact]
    public async Task AReferenceNamingItsOwnApp_ReadsTheKeyThroughItsKeyReference()
    {
        using var h = new GitHubAppHarness(environment: new Dictionary<string, string>());
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Token(TokenValue));

        var token = await h.Resolver.ResolveAsync("${githubapp:app=Iv23liOwn;key=testkey:app-key;installation=42}");

        Assert.Equal(TokenValue, token);
        var jwt = h.GitHub.Requests.Single().Jwt;
        Assert.True(jwt.SignedByTestKey);
        Assert.Equal("Iv23liOwn", jwt.Claims.GetProperty("iss").GetString());
    }

    [Fact]
    public async Task AMalformedReference_IsRefusedBeforeGitHubIsAsked()
    {
        using var h = new GitHubAppHarness();

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Resolver.ResolveAsync("${githubapp:instalation=42}"));

        Assert.Contains("unknown setting 'instalation'", ex.Message, StringComparison.Ordinal);
        Assert.Empty(h.GitHub.Requests);
    }

    [Fact]
    public async Task AHostWhoseAppIsMisconfigured_SaysWhichVariableToFix()
    {
        using var h = new GitHubAppHarness(environment: new Dictionary<string, string> { [GitHubAppEnvironment.AppIdVariable] = "Iv23li" });

        var ex = await Assert.ThrowsAsync<GitHubAppException>(() => h.Resolver.ResolveAsync("${githubapp:installation=42}"));

        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GitHubsRefusal_ReachesTheCallerOfTheResolver()
    {
        using var h = new GitHubAppHarness();
        h.GitHub.Answer = (_, _, _) => Task.FromResult(h.GitHub.Error(HttpStatusCode.NotFound, "Not Found"));

        await Assert.ThrowsAsync<GitHubAppNotInstalledException>(() => h.Resolver.ResolveAsync("${githubapp:repository=acme/flows}"));
    }
}
