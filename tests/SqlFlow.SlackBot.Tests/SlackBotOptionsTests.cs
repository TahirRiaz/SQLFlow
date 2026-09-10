using SqlFlow.Assistant;
using SqlFlow.SlackBot;
using Xunit;

namespace SqlFlow.SlackBot.Tests;

public class SlackBotOptionsTests
{
    /// <summary>A configuration that satisfies every provider-independent requirement.</summary>
    private static SlackBotOptions BaseOptions() => new()
    {
        Slack = new SlackOptions { AppToken = "xapp-1", BotToken = "xoxb-1" },
        Mcp = new McpOptions { ServerUrl = "https://mcp.example.com/mcp" },
        SqlFlow = new SqlFlowOptions { AccessToken = "sqlf_token" },
    };

    [Fact]
    public void AzureFoundry_RequiresFoundrySettings()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.AzureFoundry;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Foundry:ProjectEndpoint", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Foundry:ModelDeploymentName", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AzureFoundry_ValidConfigurationPasses()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.AzureFoundry;
        options.Foundry.ProjectEndpoint = "https://acct.services.ai.azure.com/api/projects/sqlflow";
        options.Foundry.ModelDeploymentName = "gpt-5-mini";

        options.Validate();
    }

    [Fact]
    public void OpenAI_RequiresApiKey_ButNotFoundry()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.OpenAI;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("OpenAI:ApiKey", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundry:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAI_ValidConfigurationPasses_WithDefaults()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.OpenAI;
        options.OpenAI.ApiKey = "sk-test";

        options.Validate();
        Assert.Equal("gpt-5-mini", options.OpenAI.Model);
        Assert.Equal("https://api.openai.com", options.OpenAI.BaseUrl);
    }

    [Fact]
    public void Anthropic_RequiresApiKey_ButNotFoundry()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.Anthropic;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("Anthropic:ApiKey", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Foundry:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Anthropic_ValidConfigurationPasses_WithDefaults()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.Anthropic;
        options.Anthropic.ApiKey = "sk-ant-test";

        options.Validate();
        Assert.Equal("claude-opus-4-8", options.Anthropic.Model);
        Assert.Equal(16_000, options.Anthropic.MaxTokens);
    }

    [Fact]
    public void Anthropic_RejectsTinyMaxTokens()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.Anthropic;
        options.Anthropic.ApiKey = "sk-ant-test";
        options.Anthropic.MaxTokens = 100;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("MaxTokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyFoundryMcpSettings_ArePromotedToMcpSection()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.Anthropic;
        options.Anthropic.ApiKey = "sk-ant-test";
        // A live deployment configured before the Mcp section existed sets only the legacy keys.
        options.Mcp = new McpOptions { ServerUrl = "" };
        options.Foundry.McpServerUrl = "https://legacy.example.com/mcp";
        options.Foundry.McpServerLabel = "legacy-label";
        options.Foundry.AllowedTools = ["summary", "list_runs"];

        options.Validate();

        Assert.Equal("https://legacy.example.com/mcp", options.Mcp.ServerUrl);
        Assert.Equal("legacy-label", options.Mcp.ServerLabel);
        Assert.Equal(["summary", "list_runs"], options.Mcp.AllowedTools);
    }

    [Fact]
    public void LegacyPromotion_KeepsDefaultAllowlist_WhenLegacyListNotCustomized()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.OpenAI;
        options.OpenAI.ApiKey = "sk-test";
        options.Mcp = new McpOptions { ServerUrl = "" };
        options.Foundry.McpServerUrl = "https://legacy.example.com/mcp";

        options.Validate();

        Assert.Equal("https://legacy.example.com/mcp", options.Mcp.ServerUrl);
        Assert.Contains("describe_object", options.Mcp.AllowedTools);
        Assert.DoesNotContain("trigger_run", options.Mcp.AllowedTools);
    }

    [Fact]
    public void ExplicitMcpSection_WinsOverLegacyKeys()
    {
        var options = BaseOptions();
        options.Provider = AssistantProvider.OpenAI;
        options.OpenAI.ApiKey = "sk-test";
        options.Foundry.McpServerUrl = "https://legacy.example.com/mcp";
        options.Foundry.McpServerLabel = "legacy-label";

        options.Validate();

        Assert.Equal("https://mcp.example.com/mcp", options.Mcp.ServerUrl);
        Assert.Equal("sqlflow", options.Mcp.ServerLabel);
    }

    [Fact]
    public void MissingMcpServerUrl_IsReported_ForEveryProvider()
    {
        foreach (var provider in new[] { AssistantProvider.AzureFoundry, AssistantProvider.OpenAI, AssistantProvider.Anthropic })
        {
            var options = BaseOptions();
            options.Provider = provider;
            options.Mcp = new McpOptions { ServerUrl = "" };

            var ex = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains("Mcp:ServerUrl", ex.Message, StringComparison.Ordinal);
        }
    }
}

public class AnthropicGatewayDataUriTests
{
    [Fact]
    public void ParsesAWellFormedImageDataUri()
    {
        var ok = AnthropicGateway.TryParseDataUri("data:image/png;base64,aGVsbG8=", out var mediaType, out var base64);

        Assert.True(ok);
        Assert.Equal("image/png", mediaType);
        Assert.Equal("aGVsbG8=", base64);
    }

    [Theory]
    [InlineData("https://example.com/image.png")]
    [InlineData("data:image/png,rawbytes")]
    [InlineData("data:;base64,aGVsbG8=")]
    [InlineData("data:image/png;base64,")]
    [InlineData("")]
    public void RejectsMalformedDataUris(string input)
    {
        Assert.False(AnthropicGateway.TryParseDataUri(input, out _, out _));
    }
}
