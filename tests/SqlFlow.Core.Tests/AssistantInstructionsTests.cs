using System.Text.RegularExpressions;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// What a surface adds to the MCP server's instructions, and nothing more. The rules (what a <c>!</c> question is, the
/// modes, which tool answers what, how a business answer reads) are the server's, the one text every client of it
/// follows; a surface says only how an answer is laid out where it is read. A rule written here would be a second copy
/// that drifts from the server's, so the surface text naming any of the server's tools fails the build.
/// </summary>
public sealed class AssistantInstructionsTests
{
    private static readonly AssistantSurface[] Surfaces = [AssistantSurface.Slack, AssistantSurface.Gui];

    [Fact]
    public void TheSystemPrompt_IsTheServersInstructions_ThenTheSurfaces()
    {
        foreach (var surface in Surfaces)
        {
            var settings = AssistantModesTests.Settings(surface);

            var prompt = AssistantInstructions.Compose("SERVER RULES: the modes and the tools.\n\n", settings);

            Assert.StartsWith("SERVER RULES: the modes and the tools.\n\nWHERE YOU ARE ANSWERING: ", prompt, StringComparison.Ordinal);
            Assert.EndsWith(AssistantInstructions.Surface(settings), prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ASurfaceNamesNoToolOfTheServer_SoItCarriesNoRuleOfItsOwn()
    {
        var tools = McpToolNames();
        foreach (var surface in Surfaces)
        {
            var text = AssistantInstructions.Surface(AssistantModesTests.Settings(surface));
            foreach (var tool in tools)
            {
                Assert.False(Regex.IsMatch(text, $@"\b{Regex.Escape(tool)}\b"), $"The {surface} section names {tool}.");
            }
        }
    }

    [Fact]
    public void Slack_LaysOutAQueryAnswerByTheResultsOwnFields_AndSaysARepliesMustMentionTheBot()
    {
        var slack = AssistantInstructions.Surface(AssistantModesTests.Settings(AssistantSurface.Slack));

        Assert.Contains("Slack mrkdwn", slack, StringComparison.Ordinal);
        Assert.Contains("`approvalFormat`", slack, StringComparison.Ordinal);
        Assert.Contains("`saveOffer`", slack, StringComparison.Ordinal);
        Assert.Contains("only when it mentions you", slack, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGui_DrawsTheResultItself_AndRunsAndSavesWithItsButtons()
    {
        var gui = AssistantInstructions.Surface(AssistantModesTests.Settings(AssistantSurface.Gui));

        Assert.Contains("query-result", gui, StringComparison.Ordinal);
        Assert.Contains("pressing Run shows the result", gui, StringComparison.Ordinal);
        Assert.Contains("never ask them to reply to run or save it", gui, StringComparison.Ordinal);
        Assert.Contains("ignore them here", gui, StringComparison.Ordinal);
    }

    [Fact]
    public void Links_PointAtTheGui_OnlyWhenItsAddressIsKnown()
    {
        var withGui = AssistantModesTests.Settings(AssistantSurface.Slack);
        withGui.GuiBaseUrl = "https://sqlflow.example/";
        Assert.Contains("prefix it with https://sqlflow.example.", AssistantInstructions.Surface(withGui), StringComparison.Ordinal);

        var withoutGui = AssistantModesTests.Settings(AssistantSurface.Slack);
        Assert.Contains("without links", AssistantInstructions.Surface(withoutGui), StringComparison.Ordinal);
    }

    /// <summary>The MCP server's tools, read from its source: the server is Rust and this check C#, so reading the one
    /// from the other is what keeps the check honest as tools are added.</summary>
    private static IReadOnlyList<string> McpToolNames()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "sqlflow-mcp", "src", "server.rs");
        Assert.True(File.Exists(path), $"Expected the MCP server source at {Path.GetFullPath(path)}.");
        var names = Regex.Matches(
                File.ReadAllText(path),
                @"#\[tool\((?:[^\]]|\][^\)])*?\)\]\s*(?:pub\s+)?async\s+fn\s+(\w+)",
                RegexOptions.Singleline)
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(names.Count > 40, $"Only found {names.Count} MCP tools; the parse is probably wrong.");
        Assert.Contains("find_similar_questions", names);
        return names;
    }
}
