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
    public void LeavesFencedCodeUntouched_ButDropsTheLanguageTag()
    {
        // Slack does not highlight code, so ```sql would render "sql" as the block's first line.
        var input = "Before **bold**\n```sql\nSELECT ** FROM [t](x)\n## not a heading\n```\nAfter **bold**";
        var expected = "Before *bold*\n```\nSELECT ** FROM [t](x)\n## not a heading\n```\nAfter *bold*";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void KeepsContentThatSharesTheOpeningFenceLine()
    {
        var input = "```SELECT 1 AS One\n```";
        Assert.Equal(input, SlackMrkdwn.FromMarkdown(input));
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

    [Theory]
    // Bold inside a heading is unwrapped, not doubled into the Markdown the converter exists to remove.
    [InlineData("## **Summary**", "*Summary*")]
    [InlineData("# A **very** big day", "*A very big day*")]
    // A heading already written as Slack bold keeps one pair of asterisks.
    [InlineData("### *Summary*", "*Summary*")]
    // A link in a heading is converted like any other.
    [InlineData("## See [the run](https://x.example/r/1)", "*See <https://x.example/r/1|the run>*")]
    public void ConvertsHeadingsThatCarryBoldOrLinks(string markdown, string expected)
    {
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(markdown));
    }

    [Theory]
    [InlineData("```SQL")]
    [InlineData("```Sql")]
    [InlineData("```sql   ")]
    [InlineData("```tsql")]
    [InlineData("```json")]
    [InlineData("```sql title=\"Revenue\"")]
    public void DropsARecognisedLanguageTag_WhateverItsCaseOrInfoString(string opening)
    {
        Assert.Equal("```\nSELECT 1\n```", SlackMrkdwn.FromMarkdown(opening + "\nSELECT 1\n```"));
    }

    [Theory]
    // A word that is not a language is the first line of code: dropping it would corrupt the query.
    [InlineData("```SELECT\n  1 AS One\n```")]
    [InlineData("```WITH\nx AS (SELECT 1)\nSELECT * FROM x\n```")]
    [InlineData("```DECLARE\n```")]
    public void KeepsAKeywordWrittenRightAfterTheBackticks(string markdown)
    {
        Assert.Equal(markdown, SlackMrkdwn.FromMarkdown(markdown));
    }

    [Fact]
    public void AOneLineBlock_DoesNotOpenAFenceForTheRestOfTheAnswer()
    {
        var input = "Run ```SELECT 1``` first?\n```SELECT 1```\nThen **this** and [that](https://x.example)\n```sql\nSELECT 2\n```";
        var expected = "Run ```SELECT 1``` first?\n```SELECT 1```\nThen *this* and <https://x.example|that>\n```\nSELECT 2\n```";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void ATildeFence_IsWrittenWithBackticks_AndItsBodyLeftAlone()
    {
        // Slack renders only backtick fences, so a tilde block would otherwise lose its code formatting and have its body
        // converted as prose.
        var input = "~~~sql\nSELECT ** FROM t\n## not a heading\n~~~\nAfter **bold**";
        var expected = "```\nSELECT ** FROM t\n## not a heading\n```\nAfter *bold*";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void ABlockClosesOnlyOnAMatchingFence()
    {
        // Inside a tilde block a backtick line is code, and a fence carrying an info string never closes a block.
        var input = "~~~\n```\n**still code**\n```sql\n~~~\n**prose**";
        var expected = "```\n```\n**still code**\n```sql\n```\n*prose*";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void AnIndentedFenceInAList_KeepsItsIndentation()
    {
        var input = "1. Run this:\n   ```sql\n   SELECT 1\n   ```\n2. **Done**";
        var expected = "1. Run this:\n   ```\n   SELECT 1\n   ```\n2. *Done*";
        Assert.Equal(expected, SlackMrkdwn.FromMarkdown(input));
    }

    [Fact]
    public void AnUnclosedFence_LeavesTheRestAsCode()
    {
        Assert.Equal("Intro *x*\n```\n**y**", SlackMrkdwn.FromMarkdown("Intro **x**\n```sql\n**y**"));
    }

    [Fact]
    public void TruncatingInsideACodeBlock_ClosesTheBlockBeforeTheNote()
    {
        var input = "Here:\n```sql\n" + string.Join('\n', Enumerable.Repeat("SELECT 1 AS One UNION ALL", 3_000)) + "\nSELECT 1\n```";

        var result = SlackMrkdwn.FromMarkdown(input);

        Assert.True(result.Length < 40_000, $"{result.Length} characters");
        Assert.EndsWith("\n```\n_(answer truncated for Slack's message size limit)_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatingOutsideACodeBlock_AddsNoFence()
    {
        var input = "```sql\nSELECT 1\n```\n" + new string('x', 60_000);

        var result = SlackMrkdwn.FromMarkdown(input);

        Assert.EndsWith("x\n_(answer truncated for Slack's message size limit)_", result, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullAnswer_IsRefused()
    {
        Assert.Throws<ArgumentNullException>(() => SlackMrkdwn.FromMarkdown(null!));
    }
}
