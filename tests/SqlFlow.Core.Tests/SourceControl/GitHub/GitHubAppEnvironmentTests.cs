using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>
/// A host's GitHub App is read from its environment: complete and valid, or refused with the variable to fix. A host
/// with none of the variables has no App, and a status read never throws and never shows the key.
/// </summary>
public sealed class GitHubAppEnvironmentTests
{
    [Fact]
    public void Read_WithNoVariables_HasNoApp_AndTheDefaultApi()
    {
        var settings = GitHubAppEnvironment.Read(Env());

        Assert.Null(settings.App);
        Assert.Equal("https://api.github.com", settings.ApiRoot);
        Assert.Equal("github.com", settings.WebHost);
        Assert.Equal("the default", settings.ApiSource);
    }

    [Fact]
    public void Read_WithBlankVariables_TreatsThemAsUnset()
    {
        var settings = GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, "  "), (GitHubAppEnvironment.PrivateKeyVariable, ""), (GitHubAppEnvironment.ApiUrlVariable, " ")));

        Assert.Null(settings.App);
        Assert.Equal("https://api.github.com", settings.ApiRoot);
    }

    [Fact]
    public void Read_WithAnIdAndAPemKey_HasTheApp_DescribedByTheVariableName()
    {
        var settings = GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, " Iv23liAbCdEf "), (GitHubAppEnvironment.PrivateKeyVariable, TestKeys.Pkcs1Pem)));

        Assert.NotNull(settings.App);
        Assert.Equal("Iv23liAbCdEf", settings.App.AppId);
        Assert.Equal(GitHubAppEnvironment.PrivateKeyVariable, settings.App.Key.Description);
        Assert.Null(settings.App.Key.Reference);
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("Iv1.0123456789abcdef")]
    [InlineData("Iv23liAbCdEf0123")]
    public void Read_TakesEveryFormOfAppIdGitHubIssues(string appId)
    {
        var settings = GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, appId), (GitHubAppEnvironment.PrivateKeyVariable, "${keyvault:vault/key}")));

        Assert.Equal(appId, settings.App!.AppId);
    }

    [Theory]
    [InlineData("my app")]
    [InlineData("acme/app")]
    [InlineData("-leading")]
    [InlineData("${env:APP_ID}")]
    public void Read_RefusesAnAppIdGitHubCouldNotHaveIssued(string appId)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, appId), (GitHubAppEnvironment.PrivateKeyVariable, TestKeys.Pkcs1Pem))));

        Assert.Contains(GitHubAppEnvironment.AppIdVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("client ID", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithAReferenceKey_ReadsTheKeyThroughIt()
    {
        var settings = GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"), (GitHubAppEnvironment.PrivateKeyVariable, "${keyvault:estate-kv/github-app-key}")));

        Assert.Equal("${keyvault:estate-kv/github-app-key}", settings.App!.Key.Reference);
        Assert.Contains("${keyvault:estate-kv/github-app-key}", settings.App.Key.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithAKeyPath_ReadsTheFile_AndRefusesARelativeOne()
    {
        var absolute = Path.Combine(Path.GetTempPath(), "app.pem");
        var settings = GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"), (GitHubAppEnvironment.PrivateKeyPathVariable, absolute)));
        Assert.Contains(absolute, settings.App!.Key.Description, StringComparison.Ordinal);

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"), (GitHubAppEnvironment.PrivateKeyPathVariable, "keys/app.pem"))));
        Assert.Contains(GitHubAppEnvironment.PrivateKeyPathVariable, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithAnIdButNoKey_NamesBothKeyVariables()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env((GitHubAppEnvironment.AppIdVariable, "Iv23li"))));

        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(GitHubAppEnvironment.PrivateKeyPathVariable, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(GitHubAppEnvironment.PrivateKeyVariable)]
    [InlineData(GitHubAppEnvironment.PrivateKeyPathVariable)]
    public void Read_WithAKeyButNoId_NamesTheIdVariable(string keyVariable)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env((keyVariable, "${keyvault:v/k}"))));

        Assert.Contains($"{keyVariable} is set but {GitHubAppEnvironment.AppIdVariable} is not", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_WithBothKeyVariables_RefusesTheAmbiguity()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"),
            (GitHubAppEnvironment.PrivateKeyVariable, TestKeys.Pkcs1Pem),
            (GitHubAppEnvironment.PrivateKeyPathVariable, Path.Combine(Path.GetTempPath(), "app.pem")))));

        Assert.Contains("set only one of them", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://api.github.com/", "https://api.github.com", "github.com")]
    [InlineData("https://ghe.example.com/api/v3", "https://ghe.example.com/api/v3", "ghe.example.com")]
    [InlineData("https://ghe.example.com/api/v3/", "https://ghe.example.com/api/v3", "ghe.example.com")]
    [InlineData("https://api.acme.ghe.com", "https://api.acme.ghe.com", "acme.ghe.com")]
    [InlineData("HTTPS://API.GITHUB.COM", "https://api.github.com", "github.com")]
    public void Read_TakesAnApiRoot_AndTheHostItServes(string apiUrl, string root, string webHost)
    {
        var settings = GitHubAppEnvironment.Read(Env((GitHubAppEnvironment.ApiUrlVariable, apiUrl)));

        Assert.Equal(root, settings.ApiRoot);
        Assert.Equal(webHost, settings.WebHost, ignoreCase: true);
        Assert.Equal(GitHubAppEnvironment.ApiUrlVariable, settings.ApiSource);
    }

    [Theory]
    [InlineData("http://api.github.com", "https URL")]
    [InlineData("api.github.com", "not a URL")]
    [InlineData("https://user:pass@ghe.example.com/api/v3", "no user, query or fragment")]
    [InlineData("https://ghe.example.com/api/v3?x=1", "no user, query or fragment")]
    [InlineData("https://ghe.example.com/api/v3#top", "no user, query or fragment")]
    public void Read_RefusesAnApiRootTheAppsTokenMustNotGoTo(string apiUrl, string reason)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppEnvironment.Read(Env((GitHubAppEnvironment.ApiUrlVariable, apiUrl))));

        Assert.Contains(GitHubAppEnvironment.ApiUrlVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains(reason, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("www.github.com", true)]
    [InlineData("GitHub.com", true)]
    [InlineData("gist.github.com", false)]
    [InlineData("github.com.evil.example", false)]
    [InlineData("bitbucket.org", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Serves_OnlyTheApisOwnHost(string? host, bool served)
        => Assert.Equal(served, GitHubAppEnvironment.Read(Env()).Serves(host));

    [Fact]
    public void Describe_ReportsAConfiguredApp_WithoutTheKey()
    {
        var status = GitHubAppEnvironment.Describe(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"), (GitHubAppEnvironment.PrivateKeyVariable, TestKeys.Pkcs1Pem)));

        Assert.True(status.Configured);
        Assert.Equal("Iv23li", status.AppId);
        Assert.Equal(GitHubAppEnvironment.PrivateKeyVariable, status.KeySource);
        Assert.Equal("https://api.github.com", status.ApiUrl);
        Assert.Equal("github.com", status.Host);
        Assert.Null(status.Problem);
        Assert.DoesNotContain("PRIVATE KEY", status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReportsNoApp_WhenNoneIsConfigured()
    {
        var status = GitHubAppEnvironment.Describe(Env());

        Assert.False(status.Configured);
        Assert.Null(status.AppId);
        Assert.Null(status.Problem);
    }

    [Fact]
    public void Describe_ReportsAPartialConfiguration_AsAProblem_AndNeverThrows()
    {
        var status = GitHubAppEnvironment.Describe(Env((GitHubAppEnvironment.AppIdVariable, "Iv23li")));

        Assert.False(status.Configured);
        Assert.Equal("Iv23li", status.AppId);
        Assert.Contains(GitHubAppEnvironment.PrivateKeyVariable, status.Problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReportsAKeyThatIsNotAKey_ByItsVariable_NeverItsValue()
    {
        const string pasted = "ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var status = GitHubAppEnvironment.Describe(Env(
            (GitHubAppEnvironment.AppIdVariable, "Iv23li"), (GitHubAppEnvironment.PrivateKeyVariable, pasted)));

        Assert.False(status.Configured);
        Assert.Equal(GitHubAppEnvironment.PrivateKeyVariable, status.KeySource);
        Assert.NotNull(status.Problem);
        Assert.DoesNotContain(pasted, status.Problem, StringComparison.Ordinal);
        Assert.DoesNotContain(pasted, status.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_ReportsAnUnusableApiRoot_AsAProblem()
    {
        var status = GitHubAppEnvironment.Describe(Env((GitHubAppEnvironment.ApiUrlVariable, "http://ghe.example.com/api/v3")));

        Assert.False(status.Configured);
        Assert.Contains("https", status.Problem, StringComparison.Ordinal);
    }

    private static Func<string, string?> Env(params (string Name, string Value)[] variables)
    {
        var values = variables.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
        return name => values.TryGetValue(name, out var value) ? value : null;
    }
}
