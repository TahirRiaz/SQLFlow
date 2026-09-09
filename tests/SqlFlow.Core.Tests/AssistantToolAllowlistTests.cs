using System.Text.RegularExpressions;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The assistant's MCP tool allowlist against the tools the MCP server actually ships.
///
/// This exists because of a specific failure that already happened: tools were added to the MCP server and not
/// to the allowlist, so the assistant could not see them and told users the product could not do the thing.
/// That is worse than a refusal. A refusal is a limit someone can work around; an absent capability reported
/// as absent is simply wrong, and nothing failed to make it visible.
///
/// So the invariant is coverage, not membership: every tool must be explicitly ALLOWED or explicitly EXCLUDED.
/// Adding one without deciding fails here rather than silently narrowing what the assistant admits to.
/// </summary>
public sealed class AssistantToolAllowlistTests
{
    /// <summary>
    /// The tool names the MCP server exposes, read from its source. The server is Rust and the allowlist is
    /// C#, so there is no shared symbol to bind them; reading the one from the other is what keeps a
    /// cross-language contract honest instead of hoping both sides are remembered together.
    /// </summary>
    private static IReadOnlyList<string> McpToolNames()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "tools", "sqlflow-mcp", "src", "server.rs");
        Assert.True(File.Exists(path), $"Expected the MCP server source at {Path.GetFullPath(path)}.");

        var source = File.ReadAllText(path);

        // A tool is an async fn immediately preceded by a #[tool(...)] attribute. Matching on the attribute
        // rather than on every async fn is what separates the tools from the helpers beside them.
        var matches = Regex.Matches(
            source,
            @"#\[tool\((?:[^\]]|\][^\)])*?\)\]\s*(?:pub\s+)?async\s+fn\s+(\w+)",
            RegexOptions.Singleline);

        var names = matches.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
        Assert.True(names.Count > 40, $"Only found {names.Count} MCP tools; the parse is probably wrong.");
        return names;
    }

    [Fact]
    public void EveryMcpTool_IsEitherAllowedOrExplicitlyExcluded()
    {
        var settings = new McpOptions();
        var allowed = settings.AllowedTools.ToHashSet(StringComparer.Ordinal);
        var excluded = McpOptions.ExcludedTools.ToHashSet(StringComparer.Ordinal);

        var undecided = McpToolNames()
            .Where(name => !allowed.Contains(name) && !excluded.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undecided.Count == 0,
            "These MCP tools are in neither the allowlist nor the exclusion list, so the assistant cannot see " +
            "them and will report the capability as missing: " + string.Join(", ", undecided) +
            ". Add each to McpOptions.AllowedTools or to McpOptions.ExcludedTools.");
    }

    [Fact]
    public void TheAllowlistNamesNoToolThatDoesNotExist()
    {
        // A stale name is quieter than a missing one but still wrong: it configures a tool that is gone, and
        // hides a rename behind a list that still looks complete.
        var shipped = McpToolNames().ToHashSet(StringComparer.Ordinal);
        var phantom = new McpOptions().AllowedTools
            .Where(name => !shipped.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            phantom.Count == 0,
            "The allowlist names tools the MCP server does not ship (renamed or removed): "
            + string.Join(", ", phantom));
    }

    [Fact]
    public void TheExclusionListNamesNoToolThatDoesNotExist()
    {
        var shipped = McpToolNames().ToHashSet(StringComparer.Ordinal);
        var phantom = McpOptions.ExcludedTools
            .Where(name => !shipped.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            phantom.Count == 0,
            "The exclusion list names tools the MCP server does not ship: " + string.Join(", ", phantom));
    }

    [Fact]
    public void NoToolIsBothAllowedAndExcluded()
    {
        var both = new McpOptions().AllowedTools
            .Intersect(McpOptions.ExcludedTools, StringComparer.Ordinal)
            .ToList();

        Assert.True(both.Count == 0, "Listed as both allowed and excluded: " + string.Join(", ", both));
    }

    [Fact]
    public void TheQuerySurfaceIsReachable_BecauseThatIsThePointOfBuildingIt()
    {
        // Pinned by name: these are the tools whose absence produced "I can only see metadata, not the rows".
        var allowed = new McpOptions().AllowedTools;
        Assert.Contains("prepare_query", allowed);
        Assert.Contains("run_query", allowed);
        Assert.Contains("get_table_key", allowed);
        Assert.Contains("get_table_joins", allowed);
    }

    [Fact]
    public void TheToolsThatStartWorkStayOff()
    {
        var allowed = new McpOptions().AllowedTools;
        Assert.DoesNotContain("trigger_run", allowed);
        Assert.DoesNotContain("cancel_run", allowed);
        Assert.DoesNotContain("propose_pipelines", allowed);
    }

    // ---- The Slack surface, which is narrower than the GUI's on purpose ------------------------------

    [Fact]
    public void SlackNeverGetsMoreThanTheGui()
    {
        // Slack is a shared channel rather than a signed-in per-user session. Whatever else changes, it must
        // never end up with a tool the GUI does not also have: that would mean the least-controlled surface
        // had the widest reach.
        var extra = McpOptions.SlackDefaultTools
            .Except(McpOptions.GuiDefaultTools, StringComparer.Ordinal)
            .ToList();

        Assert.True(extra.Count == 0, "Slack allows tools the GUI does not: " + string.Join(", ", extra));
    }

    [Fact]
    public void SlackGetsTheJoinLookup_AndNothingThatReachesADatasource()
    {
        var slack = McpOptions.SlackDefaultTools;

        // The one addition Slack gets: answering "how do I join these tables" is a metadata question.
        Assert.Contains("get_table_joins", slack);

        // Everything that reaches a datasource stays off, because the two-step confirmation the query surface
        // relies on is a weak guarantee in a room where the approver need not be the asker.
        Assert.DoesNotContain("prepare_query", slack);
        Assert.DoesNotContain("run_query", slack);
        Assert.DoesNotContain("check_duplicate_keys", slack);
        Assert.DoesNotContain("compare_baseline", slack);
        Assert.DoesNotContain("detect_unique_key", slack);
    }

    [Fact]
    public void TheSlackListNamesNoToolThatDoesNotExist()
    {
        var shipped = McpToolNames().ToHashSet(StringComparer.Ordinal);
        var phantom = McpOptions.SlackDefaultTools
            .Where(name => !shipped.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(phantom.Count == 0, "The Slack list names tools that do not ship: " + string.Join(", ", phantom));
    }

    [Fact]
    public void ApplyingASurfaceDefault_NarrowsTheShippedListButNeverAConfiguredOne()
    {
        // Untouched: narrowed to the surface.
        var shipped = new McpOptions();
        shipped.ApplySurfaceDefault(McpOptions.SlackDefaultTools);
        Assert.Equal(McpOptions.SlackDefaultTools, shipped.AllowedTools);

        // Configured by a deployment: left exactly as configured, because an operator's explicit decision
        // outranks a built-in default.
        var configured = new McpOptions { AllowedTools = ["summary", "list_runs"] };
        configured.ApplySurfaceDefault(McpOptions.SlackDefaultTools);
        Assert.Equal(["summary", "list_runs"], configured.AllowedTools);
    }
}
