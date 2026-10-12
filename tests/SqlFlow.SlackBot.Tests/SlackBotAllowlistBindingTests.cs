using Microsoft.Extensions.Configuration;
using SqlFlow.Assistant;
using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

/// <summary>
/// The tools the Slack bot runs with, bound from configuration exactly the way Program.cs binds it (<c>SlackBot</c>
/// section, then <see cref="SlackBotOptions.Validate"/>). The MCP server decides what each mode lists, so an unconfigured
/// bot narrows nothing; a deployment's own list must replace the empty default rather than be added to a pre-filled one,
/// which only binding through the configuration, as the bot does, can show.
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
    public void Unconfigured_LeavesTheToolsToTheMcpServer()
    {
        var settings = Bind(new Dictionary<string, string?>()).ToAssistantSettings();

        Assert.Empty(settings.Mcp.AllowedTools);
        Assert.Equal(AssistantSurface.Slack, settings.Surface);
        Assert.Equal("https://mcp.example.com/mcp?surface=assistant&mode=data",
            settings.Mcp.AssistantServerUri(AssistantMode.Data).AbsoluteUri);
    }

    [Fact]
    public void AConfiguredList_ReplacesTheEmptyDefault_RatherThanBeingAddedToIt()
    {
        var options = Bind(new Dictionary<string, string?>
        {
            ["SlackBot:Mcp:AllowedTools:0"] = "summary",
            ["SlackBot:Mcp:AllowedTools:1"] = "list_runs",
        });

        Assert.Equal(["summary", "list_runs"], options.ToAssistantSettings().Mcp.AllowedTools);
    }
}
