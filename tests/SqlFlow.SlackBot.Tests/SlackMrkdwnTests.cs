using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

public class SlackMrkdwnTests
{
    [Fact]
    public void ConvertsDoubleAsteriskBoldToSingle()
    {
        Assert.Equal("The run *failed* at 03:12.", SlackMrkdwn.FromMarkdown("The run **failed** at 03:12."));
    }

    [Fact]
    public void ConvertsMarkdownLinksToSlackLinks()
    {
        Assert.Equal(
            "See <https://sqlflow.example.com/runs/42|the run> for details.",
            SlackMrkdwn.FromMarkdown("See [the run](https://sqlflow.example.com/runs/42) for details."));
    }

    [Fact]
    public void ConvertsHeadingsToBoldLines()
    {
        Assert.Equal("*Last night's failures*", SlackMrkdwn.FromMarkdown("## Last night's failures"));
    }

    [Fact]
    public void LeavesFencedCodeBlocksUntouched()
    {
        var input = "Before **bold**\n```sql\nSELECT ** FROM [t](x)\n## not a heading\n```\nAfter **bold**";
        var expected = "Before *bold*\n```sql\nSELECT ** FROM [t](x)\n## not a heading\n```\nAfter *bold*";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void LeavesExistingSlackMrkdwnAlone()
    {
        var input = "*already bold* with <https://example.com|a link> and `code`.";
        Assert.Equal(input, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void HandlesMultipleBoldRunsOnOneLine()
    {
        Assert.Equal("*a* and *b*", SlackMrkdwn.FromMarkdown("**a** and **b**"));
    }

    [Fact]
    public void TruncatesOversizedAnswersWithANote()
    {
        var input = new string('x', 60_000);
        var result = SlackMrkdwn.FromMarkdown(input);
        Assert.True(result.Length < 40_000);
        Assert.EndsWith("_(answer truncated for Slack's message size limit)_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalizesWindowsLineEndings()
    {
        Assert.Equal("line one\nline two", SlackMrkdwn.FromMarkdown("line one\r\nline two"));
    }

    [Fact]
    public void EmptyInputYieldsEmptyOutput()
    {
        Assert.Equal("", SlackMrkdwn.FromMarkdown(""));
    }
}
