using SqlFlow.SourceControl.GitHub;
using Xunit;

namespace SqlFlow.Tests.SourceControl.GitHub;

/// <summary>
/// The <c>${githubapp:...}</c> reference: its settings are read strictly (a typo, a repeat, a half-named App or a
/// malformed value is refused with what to write instead), what it leaves out comes from the host's App, and it is
/// completed for a repository source's remote without ever widening what it names.
/// </summary>
public sealed class GitHubAppReferenceTests
{
    private static readonly GitHubAppSettings NoHostApp = GitHubAppEnvironment.Read(_ => null);

    private static readonly GitHubAppSettings HostApp = GitHubAppEnvironment.Read(name => name switch
    {
        GitHubAppEnvironment.AppIdVariable => "Iv23liHost",
        GitHubAppEnvironment.PrivateKeyVariable => "${keyvault:host-kv/app-key}",
        _ => null,
    });

    [Fact]
    public void Parse_WithoutAppAndKey_UsesTheHostsApp()
    {
        var request = GitHubAppReference.Parse("repository=acme/flows", HostApp);

        Assert.Equal("Iv23liHost", request.App.AppId);
        Assert.Equal("${keyvault:host-kv/app-key}", request.App.Key.Reference);
        Assert.Equal(new GitHubRepositoryName("acme", "flows"), request.Repository);
        Assert.Null(request.InstallationId);
        Assert.Empty(request.Permissions);
        Assert.Equal("https://api.github.com", request.ApiRoot);
    }

    [Fact]
    public void Parse_WithAppAndKey_NamesItsOwnApp_ReadingTheKeyThroughItsReference()
    {
        var request = GitHubAppReference.Parse(" App = 123456 ; KEY=keyvault:estate-kv/github-app-key ; installation=987654321 ; ", HostApp);

        Assert.Equal("123456", request.App.AppId);
        Assert.Equal("${keyvault:estate-kv/github-app-key}", request.App.Key.Reference);
        Assert.Equal(987654321L, request.InstallationId);
        Assert.Null(request.Repository);
    }

    [Fact]
    public void Parse_WithoutAppAndKey_OnAHostWithoutAnApp_NamesTheVariablesAndTheReferenceForm()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse("repository=acme/flows", NoHostApp));

        Assert.Contains(GitHubAppEnvironment.AppIdVariable, ex.Message, StringComparison.Ordinal);
        Assert.Contains("app=<client ID>;key=keyvault:", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app=123;repository=acme/flows", "an app but no key")]
    [InlineData("key=keyvault:v/k;repository=acme/flows", "a key but no app")]
    public void Parse_RefusesAHalfNamedApp(string locator, string expected)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse(locator, HostApp));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("12a")]
    [InlineData("+12")]
    [InlineData("99999999999999999999")]
    public void Parse_RefusesAnInstallationThatIsNotAPositiveId(string installation)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse($"installation={installation}", HostApp));
        Assert.Contains("numeric id", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("acme/flows", "acme", "flows")]
    [InlineData("acme/flows.git", "acme", "flows")]
    [InlineData("Acme-Corp/my.flows_repo-2", "Acme-Corp", "my.flows_repo-2")]
    public void Parse_ReadsARepository(string value, string owner, string name)
    {
        var request = GitHubAppReference.Parse($"repository={value}", HostApp);
        Assert.Equal(new GitHubRepositoryName(owner, name), request.Repository);
    }

    [Theory]
    [InlineData("acme")]
    [InlineData("acme/flows/extra")]
    [InlineData("-acme/flows")]
    [InlineData("ac--me/flows")]
    [InlineData("acme/..")]
    [InlineData("acme/fl ows")]
    [InlineData("acme/")]
    public void Parse_RefusesWhatIsNotARepository(string value)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse($"repository={value}", HostApp));
        Assert.Contains("owner/name", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAReferenceNamingNeitherInstallationNorRepository_UnlessACallerCompletesIt()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse("app=1;key=keyvault:v/k", HostApp));
        Assert.Contains("neither an installation nor a repository", ex.Message, StringComparison.Ordinal);

        var deferred = GitHubAppReference.Parse("app=1;key=keyvault:v/k", HostApp, requireTarget: false);
        Assert.Null(deferred.Repository);
        Assert.Null(deferred.InstallationId);
    }

    [Theory]
    [InlineData("instalation=42", "unknown setting 'instalation'")]
    [InlineData("installation=42;Installation=43", "names installation twice")]
    [InlineData("installation=", "gives installation no value")]
    [InlineData("installation=42;repository", "is not name=value")]
    [InlineData("=42", "is not name=value")]
    public void Parse_RefusesAMalformedSetting(string locator, string expected)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse(locator, HostApp));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesALocatorFarLongerThanAnyReference()
    {
        var ex = Assert.Throws<GitHubAppException>(
            () => GitHubAppReference.Parse("installation=42;" + new string(';', GitHubAppReference.MaxLocatorLength), HostApp));
        Assert.Contains("characters long", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("vault/key")]
    [InlineData("keyvault:")]
    [InlineData("${keyvault:vault/key")]
    [InlineData("key vault:vault/key")]
    public void Parse_RefusesAKeyThatIsNotAReference(string key)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse($"app=1;key={key};installation=2", HostApp));
        Assert.Contains("written without its ${ }", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RefusesAKeyThatIsAnotherAppReference()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse("app=1;key=githubapp:x;installation=2", HostApp));
        Assert.Contains("names another githubapp reference", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ReadsPermissions_NormalizedAndInOrder()
    {
        var request = GitHubAppReference.Parse("installation=1;permissions= Pull_Requests:Write , contents:read ,", HostApp);

        Assert.Equal(new[] { "contents", "pull_requests" }, request.Permissions.Keys);
        Assert.Equal("contents:read,pull_requests:write", GitHubAppPermissions.Format(request.Permissions));
    }

    [Theory]
    [InlineData("contents", "name:level")]
    [InlineData("contents:own", "name:level")]
    [InlineData("contents-x:read", "name:level")]
    [InlineData("contents:read,contents:write", "name contents twice")]
    [InlineData(",", "names no permission")]
    public void Parse_RefusesMalformedPermissions(string permissions, string expected)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse($"installation=1;permissions={permissions}", HostApp));
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_TakesTheApiFromTheReference_ThenTheHost()
    {
        Assert.Equal("https://ghe.example.com/api/v3", GitHubAppReference.Parse("installation=1;api=https://ghe.example.com/api/v3/", HostApp).ApiRoot);

        var ghes = GitHubAppEnvironment.Read(name => name == GitHubAppEnvironment.ApiUrlVariable ? "https://ghe.example.com/api/v3" : null);
        Assert.Equal("https://ghe.example.com/api/v3", GitHubAppReference.Parse("app=1;key=keyvault:v/k;installation=1", ghes).ApiRoot);

        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.Parse("installation=1;api=http://ghe.example.com", HostApp));
        Assert.Contains("https URL", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("${githubapp:installation=1}", true)]
    [InlineData("  ${GitHubApp:installation=1}  ", true)]
    [InlineData("${keyvault:vault/key}", false)]
    [InlineData("${githubapp:installation=1} and more", false)]
    [InlineData("${githubapp:a}${githubapp:b}", false)]
    [InlineData("githubapp:installation=1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsReference_RecognizesAWholeAppReferenceOnly(string? value, bool expected)
        => Assert.Equal(expected, GitHubAppReference.IsReference(value));

    [Fact]
    public void ForRepository_WritesTheHostAppReference()
        => Assert.Equal(
            "${githubapp:repository=acme/flows;permissions=contents:read}",
            GitHubAppReference.ForRepository(new GitHubRepositoryName("acme", "flows"), GitHubAppPermissions.ReadContents));

    [Fact]
    public void Complete_AddsTheRepositoryAndPermissions_ItLeavesOut()
        => Assert.Equal(
            "${githubapp:app=1;key=keyvault:v/k;repository=acme/flows;permissions=contents:write,pull_requests:write}",
            GitHubAppReference.Complete("${githubapp:app=1;key=keyvault:v/k;}", new GitHubRepositoryName("acme", "flows"), GitHubAppPermissions.ProposeChanges));

    [Fact]
    public void Complete_KeepsWhatTheReferenceNamesItself()
        => Assert.Equal(
            "${githubapp:repository=acme/flows;permissions=contents:read}",
            GitHubAppReference.Complete("${githubapp:repository=acme/flows;permissions=contents:read}", new GitHubRepositoryName("acme", "flows"), GitHubAppPermissions.ProposeChanges));

    [Theory]
    [InlineData("https://github.com/acme/flows.git")]
    [InlineData("https://www.github.com/Acme/Flows")]
    [InlineData("https://user@github.com/acme/flows/")]
    public void ForRemote_NarrowsTheReferenceToTheRemotesRepository(string remote)
    {
        var completed = GitHubAppReference.ForRemote("${githubapp:app=1;key=keyvault:v/k}", remote, GitHubAppPermissions.ReadContents, HostApp);

        Assert.Contains(";repository=", completed, StringComparison.Ordinal);
        Assert.EndsWith(";permissions=contents:read}", completed, StringComparison.Ordinal);
        Assert.Equal("acme/flows", GitHubAppReference.Parse(GitHubAppReference.LocatorOf(completed), HostApp).Repository!.ToString(), ignoreCase: true);
    }

    [Fact]
    public void ForRemote_RefusesAReferenceNamingAnotherRepository()
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.ForRemote(
            "${githubapp:repository=acme/other}", "https://github.com/acme/flows.git", GitHubAppPermissions.ReadContents, HostApp));

        Assert.Contains("names the repository acme/other, but the remote is acme/flows", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://bitbucket.org/acme/flows.git")]
    [InlineData("http://github.com/acme/flows.git")]
    [InlineData("git@github.com:acme/flows.git")]
    [InlineData("C:\\repos\\flows")]
    [InlineData(null)]
    public void ForRemote_RefusesAReferenceWithoutATarget_ForARemoteItsGitHubDoesNotServe(string? remote)
    {
        var ex = Assert.Throws<GitHubAppException>(() => GitHubAppReference.ForRemote(
            "${githubapp:app=1;key=keyvault:v/k}", remote, GitHubAppPermissions.ReadContents, HostApp));

        Assert.Contains("names no repository", ex.Message, StringComparison.Ordinal);
        Assert.Contains("github.com", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForRemote_KeepsAReferenceWithItsOwnTarget_ForARemoteItsGitHubDoesNotServe()
        => Assert.Equal(
            "${githubapp:installation=42}",
            GitHubAppReference.ForRemote("${githubapp:installation=42}", "https://bitbucket.org/acme/flows.git", GitHubAppPermissions.ReadContents, HostApp));

    [Fact]
    public void ForRemote_ServesAGitHubEnterpriseRemote_ThroughTheReferencesApi()
    {
        var completed = GitHubAppReference.ForRemote(
            "${githubapp:installation=42;api=https://ghe.example.com/api/v3}", "https://ghe.example.com/acme/flows.git",
            GitHubAppPermissions.ReadContents, HostApp);

        Assert.Equal("${githubapp:installation=42;api=https://ghe.example.com/api/v3;repository=acme/flows;permissions=contents:read}", completed);
    }

    [Theory]
    [InlineData("${githubapp:repository=acme/flows;permissions=contents:read}", "acme/flows (contents:read)")]
    [InlineData("${githubapp:installation=42}", "installation 42")]
    [InlineData("${githubapp:app=1;key=keyvault:v/k}", "the installation")]
    public void Narrowing_SaysWhatTheTokenReaches(string reference, string expected)
        => Assert.Equal(expected, GitHubAppReference.Narrowing(reference));

    [Fact]
    public void CacheKey_IsEqualForTheSameToken_AndDiffersForAnyDifference()
    {
        string Key(string locator) => GitHubAppReference.Parse(locator, HostApp).CacheKey;

        Assert.Equal(Key("repository=acme/flows;permissions=contents:read,metadata:read"), Key("permissions=METADATA:read,contents:read;repository=Acme/Flows"));
        Assert.NotEqual(Key("repository=acme/flows;permissions=contents:read"), Key("repository=acme/flows;permissions=contents:write"));
        Assert.NotEqual(Key("repository=acme/flows"), Key("repository=acme/other"));
        Assert.NotEqual(Key("installation=1"), Key("installation=2"));
        Assert.NotEqual(Key("installation=1"), Key("installation=1;api=https://ghe.example.com/api/v3"));
        Assert.NotEqual(Key("installation=1"), Key("app=Iv23liHost;key=keyvault:other-kv/app-key;installation=1"));
        Assert.DoesNotContain("PRIVATE KEY", Key("installation=1"), StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_SaysWhatTheTokenIsFor()
        => Assert.Equal(
            "GitHub App Iv23liHost, installation 7, repository acme/flows, contents:read",
            GitHubAppReference.Parse("installation=7;repository=acme/flows;permissions=contents:read", HostApp).Describe());
}
