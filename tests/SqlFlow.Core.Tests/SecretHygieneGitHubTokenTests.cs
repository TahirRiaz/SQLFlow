using SqlFlow.Core.Secrets;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A GitHub token quoted in third-party error text (a git host's message, a pasted credential) is scrubbed wherever
/// such text is reported or logged, in every shape GitHub issues, while text that merely resembles one is left alone.
/// </summary>
public sealed class SecretHygieneGitHubTokenTests
{
    [Theory]
    [InlineData("ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("gho_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("ghu_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("ghs_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("ghr_1B4a2e77838347a7E420ce178F2E7c6912E169246c34E1ccbF66C46812d16D5B1A9Dc86A1498")]
    [InlineData("github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV")]
    [InlineData("ghs_123456_eyJhbGciOiJSUzI1NiJ9.eyJpc3MiOiIxMjM0NTYifQ.c2lnbmF0dXJlLXBhcnQ")]
    public void RedactedMessage_ScrubsEveryGitHubTokenShape(string token)
    {
        var message = SecretHygiene.RedactedMessage($"remote: Invalid username or password ({token}) for https://github.com/acme/flows.git");

        Assert.DoesNotContain(token, message, StringComparison.Ordinal);
        Assert.Contains("[redacted]", message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/acme/flows.git", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedMessage_ScrubsEveryTokenInTheMessage()
    {
        var message = SecretHygiene.RedactedMessage(
            "first ghp_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa then ghs_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbb and password=hunter2;");

        Assert.Equal("first [redacted] then [redacted] and password=[redacted];", message);
    }

    [Theory]
    [InlineData("the branch ghp_fix was merged")]
    [InlineData("see ghs_ in the docs")]
    [InlineData("highs_and_lows_of_the_day")]
    public void RedactedMessage_LeavesTextThatOnlyResemblesAToken(string text)
        => Assert.Equal(text, SecretHygiene.RedactedMessage(text));
}
