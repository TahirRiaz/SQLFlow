namespace SqlFlow.Assistant;

/// <summary>Which model provider answers questions. The MCP tool source, the instructions, and
/// the whole assistant experience are identical across providers; only the model endpoint and its
/// credential differ.</summary>
public enum AssistantProvider
{
    /// <summary>Azure AI Foundry via the Responses API, authenticated with the Azure credential
    /// (managed identity in the container). The default, and the only mode with no API key.</summary>
    AzureFoundry,

    /// <summary>The OpenAI platform directly (api.openai.com) via the same Responses API wire
    /// format, authenticated with an OpenAI API key. No Azure dependency.</summary>
    OpenAI,

    /// <summary>The Anthropic Claude API via the Messages API's MCP connector, authenticated with
    /// an Anthropic API key. No Azure dependency.</summary>
    Anthropic,
}

/// <summary>Where the assistant's answers are rendered. The knowledge and behavior are identical;
/// only the output formatting differs (Slack mrkdwn versus GitHub-flavored Markdown) and how
/// entities are linked.</summary>
public enum AssistantSurface
{
    /// <summary>Answers are posted into Slack threads: mrkdwn formatting, Slack link syntax.</summary>
    Slack,

    /// <summary>Answers render in the SQLFlow GUI's chat: GitHub-flavored Markdown, in-app links.</summary>
    Gui,
}

/// <summary>
/// Everything a gateway needs to answer questions, independent of the hosting surface: the
/// provider choice with its per-provider settings, the SQLFlow MCP server acting as the tool
/// source, and the shared behavioral knobs. Hosts (the Slack bot, the control plane) bind their
/// own configuration sections and map them onto this type once at startup.
/// </summary>
public sealed class AssistantSettings
{
    /// <summary>The model provider answering questions.</summary>
    public AssistantProvider Provider { get; set; } = AssistantProvider.AzureFoundry;

    /// <summary>The surface the answers are formatted for.</summary>
    public AssistantSurface Surface { get; set; } = AssistantSurface.Slack;

    public McpOptions Mcp { get; set; } = new();
    public FoundryOptions Foundry { get; set; } = new();
    public OpenAIOptions OpenAI { get; set; } = new();
    public AnthropicOptions Anthropic { get; set; } = new();

    /// <summary>Ceiling for one agent run before it is cancelled and reported as timed out.</summary>
    public int RunTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// How many prior conversation messages are replayed when the provider-side conversation must
    /// be (re)built. The host's transcript (a Slack thread, the chat store) is the durable record;
    /// provider state is only a cache of it (Anthropic has no server-side state at all and sends
    /// this many turns every call).
    /// </summary>
    public int MaxReplayMessages { get; set; } = 20;

    /// <summary>How many image attachments on one message are sent to the vision model. 0 disables image
    /// reading. Extra images past the cap are ignored.</summary>
    public int MaxImages { get; set; } = 4;

    /// <summary>Largest image (bytes) sent to the model; a larger attachment is skipped rather than
    /// inflating the request. Providers also enforce their own per-image ceilings.</summary>
    public long MaxImageBytes { get; set; } = 8_000_000;

    /// <summary>Optional GUI base URL; when set, answers link entities to their GUI pages. The GUI
    /// surface links relative to its own origin when this is empty.</summary>
    public string GuiBaseUrl { get; set; } = "";

    /// <summary>
    /// Appends one entry per missing or invalid shared setting to <paramref name="missing"/>, each
    /// prefixed with <paramref name="sectionPrefix"/> (for example <c>SlackBot</c> or
    /// <c>ControlPlane:Assistant</c>) so the startup failure names the exact configuration key to
    /// set. Host-specific settings (Slack tokens, chat toggles) are validated by the host.
    /// </summary>
    public void CollectMissing(string sectionPrefix, ICollection<string> missing)
    {
        ArgumentNullException.ThrowIfNull(sectionPrefix);
        ArgumentNullException.ThrowIfNull(missing);

        void Require(string? value, string name)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add($"{sectionPrefix}:{name}");
            }
        }

        Require(Mcp.ServerUrl, "Mcp:ServerUrl (https://<sqlflow-mcp host>/mcp)");

        switch (Provider)
        {
            case AssistantProvider.AzureFoundry:
                Require(Foundry.ProjectEndpoint, "Foundry:ProjectEndpoint (https://<account>.services.ai.azure.com/api/projects/<project>)");
                Require(Foundry.ModelDeploymentName, "Foundry:ModelDeploymentName");
                break;
            case AssistantProvider.OpenAI:
                Require(OpenAI.ApiKey, "OpenAI:ApiKey (sk-..., an OpenAI platform API key)");
                Require(OpenAI.Model, "OpenAI:Model (a Responses-API + MCP-capable model, e.g. gpt-5-mini)");
                Require(OpenAI.BaseUrl, "OpenAI:BaseUrl");
                break;
            case AssistantProvider.Anthropic:
                Require(Anthropic.ApiKey, "Anthropic:ApiKey (sk-ant-..., an Anthropic API key)");
                Require(Anthropic.Model, "Anthropic:Model (e.g. claude-opus-4-8)");
                if (Anthropic.MaxTokens < 1024)
                {
                    missing.Add($"{sectionPrefix}:Anthropic:MaxTokens must be at least 1024 (was {Anthropic.MaxTokens})");
                }
                break;
            default:
                missing.Add($"{sectionPrefix}:Provider '{Provider}' is not a supported provider (AzureFoundry, OpenAI, Anthropic)");
                break;
        }

        if (RunTimeoutSeconds < 10)
        {
            missing.Add($"{sectionPrefix}:RunTimeoutSeconds must be at least 10 (was {RunTimeoutSeconds})");
        }
        if (MaxReplayMessages < 0)
        {
            missing.Add($"{sectionPrefix}:MaxReplayMessages must not be negative (was {MaxReplayMessages})");
        }
    }
}

/// <summary>The SQLFlow MCP server every provider uses as its tool source.</summary>
public sealed class McpOptions
{
    /// <summary>The deployed SQLFlow MCP server's endpoint, e.g. https://sqlflow-mcp.internal.example/mcp.</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>The MCP server label shared between the tool definition and its per-run resources.</summary>
    public string ServerLabel { get; set; } = "sqlflow";

    /// <summary>
    /// The MCP tools the assistant may call. The default is the WHOLE read-only surface: everything that only
    /// reads belongs here, because a missing reader is an answer the assistant cannot give. A tool absent from
    /// this list does not look restricted to the model, it looks ABSENT: the assistant reports the product
    /// cannot do the thing, which is worse than refusing, because it is wrong.
    ///
    /// That failure mode is why <see cref="ExcludedTools"/> exists beside this list and why a test asserts the
    /// two together cover every tool the MCP server ships. Adding a tool without deciding which side it falls
    /// on fails that test rather than silently making the assistant deny a capability it has.
    ///
    /// Excluded on purpose: the operate tools that TRIGGER work (trigger_run, cancel_run), the authoring tool
    /// (propose_pipelines), and the stdio-only or sign-in tools (inert over HTTP anyway). The data-operations
    /// tools ARE included: they are read-only by construction, they sit behind their own deployment switch,
    /// and running a query is gated by a human approving the exact statement first. An empty list means all
    /// tools, so leave this populated unless the MCP server itself is restricted.
    /// </summary>
    /// <summary>
    /// The read surface every host gets: catalog, lineage, runs, search, schedules, insights, docs. Nothing
    /// here touches a datasource, so it is safe on any surface however public.
    /// </summary>
    private static readonly string[] SharedReadTools =
    [
        "search_docs", "get_doc", "get_doc_by_yaml_path", "get_doc_by_cli_command", "related_docs", "list_docs",
        "validate_flow", "list_flow_keys", "describe_flow_key",
        "check_connectivity", "get_control_plane_url",
        "list_repos", "get_repo", "list_pipelines", "list_flow_batches", "get_pipeline", "pipeline_definition",
        "pipeline_file_stats", "pipeline_columns",
        "list_runs", "get_run", "run_statements", "run_assertions", "run_files", "run_health_metrics",
        "list_schemas", "catalog_tree", "lineage_objects", "lineage_object_detail", "lineage_object_columns",
        "describe_object", "describe_object_refresh", "object_lineage", "list_file_sources", "file_provenance",
        "lineage_edges", "lineage_waves", "lineage_dependencies",
        "list_subscribers", "describe_subscriber",
        "search_all", "search_objects", "search_columns", "search_definitions", "search_flows",
        "search_flow_columns", "search_files", "search_statements",
        "list_schedules", "get_schedule", "get_schedule_plan", "list_nodes", "list_repo_sources", "summary",
        "insights_flows", "insights_attention", "insights_recommendations", "insights_steps",
        "detect_stream_anomalies",
    ];

    /// <summary>
    /// The GUI's surface: everything shared, plus the tools that reach a datasource. The GUI is a signed-in,
    /// per-user surface where the caller's own bearer authorises every call, so the data-model tools and the
    /// data-operations surface belong here.
    /// </summary>
    public static readonly IReadOnlyList<string> GuiDefaultTools =
    [
        .. SharedReadTools,
        // The data model, one question per tool: what identifies a row, and how tables join. These are what an
        // assistant composes correct SQL from, so leaving them out is what makes it guess or give up.
        "get_table_key", "get_table_joins", "detect_unique_key",
        // The data-operations surface. Read-only, and behind ControlPlane:DataOps:Enabled, which is the switch
        // that actually governs them.
        "dataops_capabilities", "check_duplicate_keys", "compare_baseline",
        // Running a business question. prepare_query executes nothing, and run_query only redeems a single-use
        // token minted by a prepare whose exact SQL was shown to a person.
        "prepare_query", "run_query",
    ];

    /// <summary>
    /// Slack's surface: the shared read tools plus the JOIN lookup, and nothing that reaches a datasource.
    ///
    /// Slack is a SHARED, semi-public channel rather than a signed-in per-user session, so the trust model is
    /// different from the GUI's: a message is visible to a room, and the two-step confirmation the query
    /// surface relies on ("show the SQL, get agreement, then run") is a much weaker guarantee when the person
    /// who approves it need not be the person who asked. Answering "how do I join these tables" is a metadata
    /// question with no such property, which is why it is the one addition Slack gets.
    /// </summary>
    public static readonly IReadOnlyList<string> SlackDefaultTools =
    [
        .. SharedReadTools,
        "get_table_joins",
    ];

    public List<string> AllowedTools { get; set; } = [.. GuiDefaultTools];

    /// <summary>
    /// Narrows the allowed tools to a surface's default, but ONLY when the list is still the shipped default:
    /// a deployment that configured its own list keeps it. Called by a host whose surface is not the GUI, so
    /// the per-surface decision lives beside the list rather than in each host's binding code.
    /// </summary>
    public void ApplySurfaceDefault(IReadOnlyList<string> surfaceDefault)
    {
        ArgumentNullException.ThrowIfNull(surfaceDefault);
        if (AllowedTools.SequenceEqual(GuiDefaultTools, StringComparer.Ordinal))
        {
            AllowedTools = [.. surfaceDefault];
        }
    }

    /// <summary>
    /// The tools deliberately kept from the assistant, listed rather than merely absent so the omission is a
    /// decision on the record. Everything here either starts work, authors code, or is inert over HTTP.
    /// </summary>
    public static readonly IReadOnlyList<string> ExcludedTools =
    [
        // Start or stop work in the estate.
        "trigger_run", "cancel_run",
        // Executes DMV probes as a side effect; the read-only insights_* tools already expose their results.
        "analyze_warehouse_health",
        // Authors code and opens a pull request.
        "propose_pipelines",
        // Scans a live location and generates YAML; an authoring step, not a question.
        "discover_source",
        // Introspects a source and generates ing-flow YAML; the required lead-in to propose_pipelines,
        // so it is excluded for the same reason: authoring, not a question.
        "scaffold_ingestion_flow",
        // Session and transport plumbing, inert or meaningless over HTTP with a forwarded bearer.
        "login", "logout", "check_auth_status", "set_access_token", "set_control_plane_url",
        // Git history and schema-diff readers reachable through the GUI, kept off the chat surface to bound
        // the tool count the model has to choose between.
        "database_schema_changes", "database_schema_history_databases", "database_object_ddl",
        "database_object_compare", "flow_definition_history", "flow_definition_file_history",
        "flow_definition_diff",
    ];
}

/// <summary>Settings for <see cref="AssistantProvider.AzureFoundry"/> mode.</summary>
public sealed class FoundryOptions
{
    /// <summary>The Foundry project endpoint the assistant runs against.</summary>
    public string ProjectEndpoint { get; set; } = "";

    /// <summary>The model deployment (in the same Foundry account) the assistant runs on.</summary>
    public string ModelDeploymentName { get; set; } = "";

    /// <summary>Optional audio-transcription deployment (for example gpt-4o-mini-transcribe or whisper)
    /// in the same Foundry account. Empty disables voice input on surfaces that offer it.</summary>
    public string TranscriptionDeploymentName { get; set; } = "";

    /// <summary>Legacy alias for the host's Mcp:ServerUrl; read only when the new key is unset.</summary>
    public string McpServerUrl { get; set; } = "";

    /// <summary>Legacy alias for the host's Mcp:ServerLabel; read only when the new key is unset.</summary>
    public string McpServerLabel { get; set; } = "";

    /// <summary>Legacy alias for the host's Mcp:AllowedTools; read only when the new key is unset.
    /// Empty here means "not customized" (the effective default list lives on <see cref="McpOptions"/>).</summary>
    public List<string> AllowedTools { get; set; } = [];
}

/// <summary>Settings for <see cref="AssistantProvider.OpenAI"/> mode: the OpenAI platform speaks
/// the same Responses API + MCP tool wire format as Foundry, so this mode reuses that gateway
/// with a different endpoint and credential.</summary>
public sealed class OpenAIOptions
{
    /// <summary>The OpenAI platform API key (sk-...).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The model, which must support the Responses API with the hosted MCP tool.</summary>
    public string Model { get; set; } = "gpt-5-mini";

    /// <summary>The API base; override only for an OpenAI-compatible proxy that supports the
    /// Responses API and its MCP tool.</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com";

    /// <summary>Optional audio-transcription model (for example gpt-4o-mini-transcribe or
    /// whisper-1). Empty disables voice input on surfaces that offer it.</summary>
    public string TranscriptionModel { get; set; } = "";
}

/// <summary>Settings for <see cref="AssistantProvider.Anthropic"/> mode: the Claude Messages API
/// with the MCP connector calling the same SQLFlow MCP server.</summary>
public sealed class AnthropicOptions
{
    /// <summary>The Anthropic API key (sk-ant-...).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>The Claude model id.</summary>
    public string Model { get; set; } = "claude-opus-4-8";

    /// <summary>Per-response output-token ceiling. 16000 keeps one response inside SDK HTTP
    /// timeouts while leaving ample room for a thorough answer.</summary>
    public int MaxTokens { get; set; } = 16_000;
}
