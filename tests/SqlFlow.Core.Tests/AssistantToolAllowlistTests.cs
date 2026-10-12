using System.Text.RegularExpressions;
using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The assistant's MCP tool sets against the tools the MCP server actually ships, and against each other.
///
/// This exists because of a specific failure that already happened: tools were added to the MCP server and not
/// to the allowlist, so the assistant could not see them and told users the product could not do the thing.
/// That is worse than a refusal. A refusal is a limit someone can work around; an absent capability reported
/// as absent is simply wrong, and nothing failed to make it visible.
///
/// So the invariant is coverage, not membership: every tool must be in a set or explicitly EXCLUDED. Adding one
/// without deciding fails here rather than silently narrowing what the assistant admits to. And the two sets stay
/// apart: a business question (a message starting with "!") gets the semantic layer, the stored questions and the
/// query tools, and nothing else; any other question gets the estate, with its whole schema, and no query.
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

    /// <summary>Every list a surface can be given, by name, for the messages.</summary>
    private static readonly (string Name, IReadOnlyList<string> Tools)[] Sets =
    [
        ("GuiDefaultTools", McpOptions.GuiDefaultTools),
        ("SlackDefaultTools", McpOptions.SlackDefaultTools),
        ("BusinessDefaultTools", McpOptions.BusinessDefaultTools),
    ];

    /// <summary>The semantic layer's readers and the stored questions, which only a business question is given.</summary>
    private static readonly string[] SemanticTools =
    [
        "get_semantic_layer", "search_semantic_layer", "list_semantic_tables", "describe_semantic_table", "find_similar_questions",
    ];

    /// <summary>The tools that run a query or record an answer, which only a business question is given.</summary>
    private static readonly string[] QueryTools = ["prepare_query", "run_query", "auto_run_trusted_match", "confirm_question"];

    /// <summary>The readers that see every catalogued table and column, which an estate question is given.</summary>
    private static readonly string[] RawSchemaTools =
    [
        "list_schemas", "catalog_tree", "lineage_objects", "lineage_object_detail", "lineage_object_columns",
        "describe_object", "search_all", "search_schema", "pipeline_columns", "get_table_key", "get_table_joins",
    ];

    [Fact]
    public void EveryMcpTool_IsInASetOrExplicitlyExcluded()
    {
        var decided = Sets.SelectMany(set => set.Tools).Concat(McpOptions.ExcludedTools).ToHashSet(StringComparer.Ordinal);

        var undecided = McpToolNames()
            .Where(name => !decided.Contains(name))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            undecided.Count == 0,
            "These MCP tools are in no set and not in the exclusion list, so the assistant cannot see them and will " +
            "report the capability as missing: " + string.Join(", ", undecided) +
            ". Add each to McpOptions.EstateReadTools, GuiDefaultTools, BusinessDefaultTools, or ExcludedTools.");
    }

    [Fact]
    public void NoSetNamesAToolThatDoesNotExist()
    {
        // A stale name is quieter than a missing one but still wrong: it configures a tool that is gone, and
        // hides a rename behind a list that still looks complete.
        var shipped = McpToolNames().ToHashSet(StringComparer.Ordinal);
        foreach (var (name, tools) in Sets.Append(("ExcludedTools", McpOptions.ExcludedTools)))
        {
            var phantom = tools.Where(tool => !shipped.Contains(tool)).OrderBy(n => n, StringComparer.Ordinal).ToList();
            Assert.True(phantom.Count == 0, $"{name} names tools the MCP server does not ship: " + string.Join(", ", phantom));
        }
    }

    [Fact]
    public void NoToolIsBothInASetAndExcluded_OrTwiceInOneSet()
    {
        foreach (var (name, tools) in Sets)
        {
            var both = tools.Intersect(McpOptions.ExcludedTools, StringComparer.Ordinal).ToList();
            Assert.True(both.Count == 0, $"Listed in {name} and excluded: " + string.Join(", ", both));

            var twice = tools.GroupBy(tool => tool, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0, $"Listed twice in {name}: " + string.Join(", ", twice));
        }
    }

    [Fact]
    public void ABusinessQuestion_GetsTheSemanticLayer_TheStoredQuestions_AndTheQueryTools_AndNothingElse()
    {
        Assert.Equal([.. SemanticTools, .. QueryTools], McpOptions.BusinessDefaultTools);
    }

    [Fact]
    public void AnEstateQuestion_SeesTheWholeSchema_AndHasNoSemanticLayerAndNoQuery()
    {
        foreach (var surface in new[] { McpOptions.GuiDefaultTools, McpOptions.SlackDefaultTools })
        {
            foreach (var raw in RawSchemaTools)
            {
                Assert.Contains(raw, surface);
            }

            foreach (var business in SemanticTools.Concat(QueryTools))
            {
                Assert.DoesNotContain(business, surface);
            }
        }
    }

    [Fact]
    public void TheToolsThatStartWork_AndTheProfilerOfLiveRows_StayOffEverySet()
    {
        foreach (var (_, tools) in Sets)
        {
            Assert.DoesNotContain("trigger_run", tools);
            Assert.DoesNotContain("cancel_run", tools);
            Assert.DoesNotContain("propose_pipelines", tools);
            Assert.DoesNotContain("detect_unique_key", tools);
        }
    }

    [Fact]
    public void SlackNeverGetsMoreThanTheGui_AndTheEngineersChecksStayOnTheGui()
    {
        // Slack is a shared channel rather than a signed-in per-user session. Whatever else changes, it must never end
        // up with a tool the GUI does not also have: that would mean the least-controlled surface had the widest reach.
        var extra = McpOptions.SlackDefaultTools.Except(McpOptions.GuiDefaultTools, StringComparer.Ordinal).ToList();
        Assert.True(extra.Count == 0, "Slack allows tools the GUI does not: " + string.Join(", ", extra));

        Assert.Contains("check_duplicate_keys", McpOptions.GuiDefaultTools);
        Assert.DoesNotContain("check_duplicate_keys", McpOptions.SlackDefaultTools);
        Assert.DoesNotContain("compare_baseline", McpOptions.SlackDefaultTools);
        Assert.DoesNotContain("dataops_capabilities", McpOptions.SlackDefaultTools);
    }

    [Theory]
    [InlineData("!what is our revenue by region", AssistantQuestionKind.Business)]
    [InlineData("  ! how many customers", AssistantQuestionKind.Business)]
    [InlineData("!!", AssistantQuestionKind.Business)]
    [InlineData("!cwd total sales", AssistantQuestionKind.Business)]
    [InlineData("what is our revenue by region", AssistantQuestionKind.Estate)]
    [InlineData("why did the run fail!", AssistantQuestionKind.Estate)]
    [InlineData("", AssistantQuestionKind.Estate)]
    [InlineData(null, AssistantQuestionKind.Estate)]
    public void AMessage_IsABusinessQuestion_OnlyWhenItStartsWithTheCommand(string? message, AssistantQuestionKind expected)
    {
        Assert.Equal(expected, AssistantQuestions.KindOf(message));
    }

    [Fact]
    public void TheRouterEveryGatewayAsks_GivesEachQuestionTheToolsAndInstructionsOfItsKind()
    {
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            var router = new AssistantRouter(new AssistantSettings { Surface = surface, GuiBaseUrl = string.Empty });

            var business = router.For("!what is our revenue by region");
            Assert.Equal(AssistantQuestionKind.Business, business.Kind);
            Assert.Equal(McpOptions.BusinessDefaultTools, business.Tools);
            Assert.Contains("find_similar_questions FIRST", business.Instructions, StringComparison.Ordinal);

            var estate = router.For("why did the orders load fail last night?");
            Assert.Equal(AssistantQuestionKind.Estate, estate.Kind);
            Assert.Equal(surface == AssistantSurface.Slack ? McpOptions.SlackDefaultTools : McpOptions.GuiDefaultTools, estate.Tools);
            Assert.Contains("describe_object", estate.Instructions, StringComparison.Ordinal);
            Assert.DoesNotContain("find_similar_questions", estate.Instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EachSurface_OffersItsOwnDefaults_UntilADeploymentConfiguresAList()
    {
        // Unconfigured: each surface offers its own default for an estate question, every surface the business default
        // for a business question, and never an empty list (which a gateway would read as "every tool").
        Assert.Equal(McpOptions.SlackDefaultTools, new AssistantSettings { Surface = AssistantSurface.Slack }.ToolsFor(AssistantQuestionKind.Estate));
        Assert.Equal(McpOptions.GuiDefaultTools, new AssistantSettings { Surface = AssistantSurface.Gui }.ToolsFor(AssistantQuestionKind.Estate));
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            Assert.Equal(McpOptions.BusinessDefaultTools, new AssistantSettings { Surface = surface }.ToolsFor(AssistantQuestionKind.Business));
        }

        // Configured by a deployment: exactly that list, per kind, on either surface, because an operator's explicit
        // decision outranks a built-in default; configuring one kind leaves the other on its default.
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            var estateOnly = new AssistantSettings { Surface = surface, Mcp = new McpOptions { AllowedTools = ["summary", "list_runs"] } };
            Assert.Equal(["summary", "list_runs"], estateOnly.ToolsFor(AssistantQuestionKind.Estate));
            Assert.Equal(McpOptions.BusinessDefaultTools, estateOnly.ToolsFor(AssistantQuestionKind.Business));

            var businessOnly = new AssistantSettings { Surface = surface, Mcp = new McpOptions { BusinessTools = ["find_similar_questions"] } };
            Assert.Equal(["find_similar_questions"], businessOnly.ToolsFor(AssistantQuestionKind.Business));
            Assert.NotEmpty(businessOnly.ToolsFor(AssistantQuestionKind.Estate));
        }
    }
}
