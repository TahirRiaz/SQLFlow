using SqlFlow.SourceControl.Proposals;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>The remote-URL resolution that decides which pull-request API a proposal targets: it accepts only HTTPS
/// clone URLs of the two supported hosts with a <c>{owner}/{repo}</c> path, and rejects everything else so the
/// authoring endpoint can refuse an unsupported remote cleanly instead of guessing an API shape.</summary>
public sealed class RemoteUrlParserTests
{
    [Theory]
    [InlineData("https://github.com/acme/warehouse-flows.git", PullRequestProvider.GitHub, "acme", "warehouse-flows")]
    [InlineData("https://github.com/acme/warehouse-flows", PullRequestProvider.GitHub, "acme", "warehouse-flows")]
    [InlineData("https://www.github.com/acme/warehouse-flows/", PullRequestProvider.GitHub, "acme", "warehouse-flows")]
    [InlineData("https://x-token@github.com/acme/repo.git", PullRequestProvider.GitHub, "acme", "repo")]
    [InlineData("https://bitbucket.org/team/data-flows.git", PullRequestProvider.Bitbucket, "team", "data-flows")]
    [InlineData("https://bitbucket.org/team/data-flows", PullRequestProvider.Bitbucket, "team", "data-flows")]
    public void Parse_SupportedHttpsRemote_ResolvesProviderOwnerRepo(
        string url, PullRequestProvider provider, string owner, string repo)
    {
        var coordinates = RemoteUrlParser.Parse(url);

        Assert.NotNull(coordinates);
        Assert.Equal(provider, coordinates.Provider);
        Assert.Equal(owner, coordinates.Owner);
        Assert.Equal(repo, coordinates.Repo);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("git@github.com:acme/repo.git")]                 // ssh, not https
    [InlineData("http://github.com/acme/repo.git")]              // http, not https
    [InlineData("https://gitlab.com/acme/repo.git")]             // unsupported host
    [InlineData("https://dev.azure.com/org/project/_git/repo")] // unsupported host
    [InlineData("https://github.com/acme")]                      // no repo segment
    [InlineData("https://github.com")]                           // no path
    [InlineData("not-a-url")]
    public void Parse_UnsupportedOrMalformedRemote_ReturnsNull(string? url)
    {
        Assert.Null(RemoteUrlParser.Parse(url));
    }
}
