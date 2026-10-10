using Microsoft.Extensions.Configuration;
using SqlFlow.Assistant;
using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

/// <summary>
/// The tool list the Slack bot actually runs with, bound from configuration exactly the way Program.cs binds it
/// (<c>SlackBot</c> section, then <see cref="SlackBotOptions.Validate"/>). Constructing the options in code, as the
/// other option tests do, cannot catch how the configuration binder treats a list property that already holds a
/// default, which is exactly what decides whether a deployment's own allowlist replaces the default or is added to it.
/// </summary>
public sealed class SlackBotAllowlistBindingTests
{
    private static SlackBotOptions Bind(IReadOnlyDictionary<string, string?> extra)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["SlackBot:Provider"] = "OpenAI",
            ["SlackBot:OpenAI:ApiKey"] = "sk-test",
            ["SlackBot:Slack:AppToken"] = "xapp-1",
            ["SlackBot:Slack:BotToken"] = "xoxb-1",
            ["SlackBot:Mcp:ServerUrl"] = "https://mcp.example.com/mcp",
            ["SlackBot:SqlFlow:AccessToken"] = "sqlf_token",
        };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var options = new SlackBotOptions();
        configuration.GetSection("SlackBot").Bind(options);
        options.Validate();
        return options;
    }

    [Fact]
    public void Unconfigured_RunsWithTheSlackList()
    {
        var options = Bind(new Dictionary<string, string?>());

        Assert.Empty(options.Mcp.AllowedTools);
        Assert.Equal(McpOptions.SlackDefaultTools, options.ToAssistantSettings().AllowedTools);
    }

    [Fact]
    public void TheSlackList_AnswersBusinessQuestions_ButNeverStartsWork()
    {
        var tools = Bind(new Dictionary<string, string?>()).ToAssistantSettings().AllowedTools;

        Assert.Contains("find_similar_questions", tools);
        Assert.Contains("prepare_query", tools);
        Assert.Contains("run_query", tools);
        Assert.Contains("auto_run_trusted_match", tools);
        Assert.Contains("confirm_question", tools);
        Assert.DoesNotContain("trigger_run", tools);
        Assert.DoesNotContain("cancel_run", tools);
        Assert.DoesNotContain("propose_pipelines", tools);
        Assert.DoesNotContain("check_duplicate_keys", tools);
    }

    [Fact]
    public void AConfiguredList_ReplacesTheDefault_RatherThanBeingAddedToIt()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["SlackBot:Mcp:AllowedTools:0"] = "summary",
            ["SlackBot:Mcp:AllowedTools:1"] = "list_runs",
        });

        Assert.Equal(["summary", "list_runs"], options.ToAssistantSettings().AllowedTools);
    }
}
