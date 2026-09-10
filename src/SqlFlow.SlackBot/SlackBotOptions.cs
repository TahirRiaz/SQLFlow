using SqlFlow.Assistant;

namespace SqlFlow.SlackBot;

/// <summary>
/// Configuration for the Slack bot, bound from the <c>SlackBot</c> section (or the equivalent
/// <c>SlackBot__*</c> environment variables in a container). <see cref="Validate"/> runs once at
/// startup: it first normalizes legacy keys (the MCP tool settings historically lived under
/// <c>Foundry</c>), then fails fast with the full list of missing settings for the selected
/// provider, so a misconfigured deployment dies with one actionable message instead of a stream
/// of runtime errors. The provider, MCP, and model settings are the shared assistant types from
/// SqlFlow.Assistant, so the Slack bot and the GUI chat configure the same assistant the same way.
/// </summary>
public sealed class SlackBotOptions
{
    /// <summary>The model provider answering questions. AzureFoundry preserves the original
    /// deployment shape; OpenAI and Anthropic run on a plain API key with no Azure dependency.</summary>
    public AssistantProvider Provider { get; set; } = AssistantProvider.AzureFoundry;

    public SlackOptions Slack { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
    public FoundryOptions Foundry { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();
    public SqlFlowOptions SqlFlow { get; set; } = new();

    /// <summary>Ceiling for one agent run before it is cancelled and reported as timed out.</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How many questions are answered concurrently. A burst beyond this queues (each question
    /// already has its eyes acknowledgement) instead of stampeding the model provider's
    /// rate limits.
    /// </summary>
    public int MaxConcurrentAnswers { get; set; } = 4;

    /// <summary>
    /// How many prior Slack-thread messages are replayed when the provider-side conversation must
    /// be (re)built. The Slack thread is the durable transcript; provider state is only a cache
    /// of it (Anthropic has no server-side state at all and sends this many turns every call).
    /// </summary>
    public int MaxReplayMessages { get; set; } = 20;

    /// <summary>How many image attachments on one message are sent to the vision model. 0 disables image
    /// reading. Extra images past the cap are ignored.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest image (bytes) sent to the model; a larger attachment is skipped rather than
    /// inflating the request. Providers also enforce their own per-image ceilings.</summary>
    public long MaxImageBytes { get; set; } = 8_000_000;

    /// <summary>Maps the bound configuration onto the shared assistant settings the gateways
    /// consume. The nested option instances are shared, not copied, so this stays a projection of
    /// the same validated state.</summary>
    public AssistantSettings ToAssistantSettings() => new()
    {
        Provider = Provider,
        Surface = AssistantSurface.Slack,
        Mcp = Mcp,
        Foundry = Foundry,
        OpenAI = OpenAI,
        Anthropic = Anthropic,
        RunTimeoutSeconds = RunTimeoutSeconds,
        MaxReplayMessages = MaxReplayMessages,
        MaxImages = MaxImages,
        MaxImageBytes = MaxImageBytes,
        GuiBaseUrl = SqlFlow.GuiBaseUrl,
    };

    public void Validate()
    {
        NormalizeLegacyMcpSettings();

        var missing = new List<string>();
        void Require(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(name);
            }
        }

        Require(Slack.AppToken, "SlackBot:Slack:AppToken (xapp-..., Socket Mode app-level token)");
        Require(Slack.BotToken, "SlackBot:Slack:BotToken (xoxb-..., bot user OAuth token)");
        Require(SqlFlow.AccessToken, "SlackBot:SqlFlow:AccessToken (sqlf_..., a read-scoped personal access token)");

        ToAssistantSettings().CollectMissing("SlackBot", missing);

        if (MaxConcurrentAnswers < 1)
        {
            missing.Add($"SlackBot:MaxConcurrentAnswers must be at least 1 (was {MaxConcurrentAnswers})");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "SqlFlow.SlackBot configuration is incomplete:\n  - " + string.Join("\n  - ", missing));
        }
    }

    /// <summary>
    /// The MCP tool settings originally lived under <c>Foundry</c> (McpServerUrl, McpServerLabel,
    /// AllowedTools); live deployments still set them there. They now live under <c>Mcp</c> because
    /// they apply to every provider. This is the single translation point: when <c>Mcp:ServerUrl</c>
    /// is not set explicitly, the legacy values are promoted, so existing configuration keeps
    /// working unchanged and all gateways read only <see cref="Mcp"/>.
    /// </summary>
    private void NormalizeLegacyMcpSettings()
    {
        // Slack is a shared channel, not a signed-in per-user session, so it gets a narrower tool set than the
        // GUI: the read surface plus the join lookup, and nothing that reaches a datasource. Applied before
        // the legacy promotion below, so an explicitly configured list still wins.
        Mcp.ApplySurfaceDefault(McpOptions.SlackDefaultTools);

        if (!string.IsNullOrWhiteSpace(Mcp.ServerUrl))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(Foundry.McpServerUrl))
        {
            return;
        }
        Mcp.ServerUrl = Foundry.McpServerUrl;
        if (!string.IsNullOrWhiteSpace(Foundry.McpServerLabel))
        {
            Mcp.ServerLabel = Foundry.McpServerLabel;
        }
        if (Foundry.AllowedTools.Count > 0)
        {
            Mcp.AllowedTools = Foundry.AllowedTools;
        }
    }
}

public sealed class SlackOptions
{
    /// <summary>App-level token with the <c>connections:write</c> scope; opens the Socket Mode websocket.</summary>
    public string AppToken { get; set; } = "";

    /// <summary>Bot user OAuth token; posts messages, adds reactions, reads thread replies.</summary>
    public string BotToken { get; set; } = "";
}

public sealed class SqlFlowOptions
{
    /// <summary>
    /// The SQLFlow personal access token forwarded to the MCP server as the Authorization header
    /// on every agent run. Mint it read-scoped: this is the bot's whole authority.
    /// </summary>
    public string AccessToken { get; set; } = "";

    /// <summary>Optional GUI base URL; when set, answers link entities to their GUI pages.</summary>
    public string GuiBaseUrl { get; set; } = "";
}
