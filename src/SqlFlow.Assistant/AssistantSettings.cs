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

    /// <summary>The MCP tools this surface offers the model: the deployment's configured list, or the surface's
    /// default when it configured none.</summary>
    public IReadOnlyList<string> AllowedTools => Mcp.EffectiveTools(Surface);

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
        if (!string.IsNullOrWhiteSpace(Mcp.ServerUrl) && !Uri.TryCreate(Mcp.ServerUrl, UriKind.Absolute, out _))
        {
            missing.Add($"{sectionPrefix}:Mcp:ServerUrl must be an absolute URL (was '{Mcp.ServerUrl}')");
        }

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

    /// <summary>The query parameter marking an MCP session as the assistant surface. The MCP server reads it and
    /// tags its control-plane calls, which then serve only the semantic layer (<c>AssistantScope</c> in the control
    /// plane). Mirrored by <c>request_surface</c> in the MCP server's <c>server.rs</c>.</summary>
    public const string AssistantSurfaceQuery = "surface=assistant";

    /// <summary><see cref="ServerUrl"/> with <see cref="AssistantSurfaceQuery"/> added (once, keeping any existing
    /// query): the address every assistant gateway gives its MCP connector, so no provider can reach the MCP server
    /// without the assistant narrowing.</summary>
    public Uri AssistantServerUri
    {
        get
        {
            var builder = new UriBuilder(ServerUrl);
            var query = builder.Query.TrimStart('?');
            if (!query.Split('&').Contains(AssistantSurfaceQuery, StringComparer.Ordinal))
            {
                builder.Query = query.Length == 0 ? AssistantSurfaceQuery : $"{query}&{AssistantSurfaceQuery}";
            }

            return builder.Uri;
        }
    }

    /// <summary>The MCP server label shared between the tool definition and its per-run resources.</summary>
    public string ServerLabel { get; set; } = "sqlflow";

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
        "pipeline_file_stats",
        "list_runs", "get_run", "run_statements", "run_assertions", "run_files", "run_health_metrics",
        // The schema, as the semantic layer serves it: only allow-listed tables and columns, with their business
        // context. These are the assistant's ONLY schema readers; the raw ones are in ExcludedTools.
        "get_semantic_layer", "search_semantic_layer", "list_semantic_tables", "describe_semantic_table",
        "describe_object_refresh", "object_lineage", "list_file_sources", "file_provenance",
        "lineage_edges", "lineage_waves", "lineage_dependencies",
        "list_subscribers", "describe_subscriber", "describe_subscriber_report", "find_similar_questions",
        "search",
        "list_schedules", "get_schedule", "get_schedule_plan", "list_nodes", "list_repo_sources", "summary",
        "insights_flows", "insights_attention", "insights_recommendations", "insights_steps",
        "detect_stream_anomalies",
    ];

    /// <summary>
    /// The GUI's surface: everything shared, plus the tools that reach a datasource. The GUI is a signed-in,
    /// per-user surface where a token delegated from the caller authorises every call, so the data-model tools and the
    /// data-operations surface belong here.
    /// </summary>
    public static readonly IReadOnlyList<string> GuiDefaultTools =
    [
        .. SharedReadTools,
        // The data-operations surface. Read-only, and behind ControlPlane:DataOps:Enabled, which is the switch
        // that actually governs them. What identifies a row and how tables join come from
        // describe_semantic_table, which serves both restricted to allow-listed columns.
        "dataops_capabilities", "check_duplicate_keys", "compare_baseline",
        // Running a business question. prepare_query executes nothing, and run_query only redeems a single-use
        // token minted by a prepare whose exact SQL was shown to a person.
        "prepare_query", "run_query",
        // Running a TRUSTED retrieval match with no fresh approval: its exact SQL was confirmed by a person when
        // it was stored, and it runs under the deployment's own row/timeout caps behind its own AutoRun switch.
        "auto_run_trusted_match",
        // Recording a person's verdict on an answer into the PowerAI example store, which the instructions tell
        // the assistant to do once someone has actually judged it. Behind the operate scope the caller's own
        // bearer carries; it runs nothing.
        "confirm_question",
    ];

    /// <summary>
    /// Slack's surface: the shared read tools, plus answering a business question end to end: running a query a
    /// person approved in the thread, running a trusted saved answer, and saving an answer a person said is right.
    ///
    /// Slack is a SHARED channel with one bot identity rather than a signed-in per-user session, so anyone in a
    /// thread can approve a query and every query and saved answer is attributed to the bot token's owner. What
    /// makes that acceptable holds whoever approves: a query is only ever a single read-only SELECT over
    /// allow-listed columns (ReadOnlyQueryGuard and ColumnPolicyGuard run on prepare, run, confirm, and auto-run),
    /// a prepared plan is single-use, and saved answers are curated by admins on the AI knowledge page. The
    /// rest of the data-operations surface (the duplicate-key and baseline checks) stays GUI-only, since those are an
    /// engineer's diagnostics rather than a business answer, and nothing that starts work is allowed anywhere.
    /// </summary>
    public static readonly IReadOnlyList<string> SlackDefaultTools =
    [
        .. SharedReadTools,
        "prepare_query", "run_query", "auto_run_trusted_match", "confirm_question",
    ];

    /// <summary>
    /// The MCP tools a deployment configured for its assistant (<c>Mcp:AllowedTools</c>), or empty when it configured
    /// none, in which case the surface's own default applies (<see cref="EffectiveTools"/>).
    ///
    /// The default deliberately does NOT live in this property. The configuration binder adds configured items to a
    /// list that already holds values rather than replacing it, so a pre-filled default here turned a deployment's
    /// narrower list into the whole default plus that list: silently the opposite of what was configured.
    /// </summary>
    public List<string> AllowedTools { get; set; } = [];

    /// <summary>
    /// The tools a surface actually offers the model: the configured list when there is one, otherwise the surface's
    /// default (<see cref="SlackDefaultTools"/> or <see cref="GuiDefaultTools"/>). Never empty, so no gateway can offer
    /// every tool the MCP server ships by omission.
    ///
    /// A tool absent from this list does not look restricted to the model, it looks ABSENT: the assistant reports the
    /// product cannot do the thing, which is worse than refusing, because it is wrong. That is why
    /// <see cref="ExcludedTools"/> exists beside the defaults and why a test asserts the two together cover every tool
    /// the MCP server ships.
    /// </summary>
    public IReadOnlyList<string> EffectiveTools(AssistantSurface surface)
        => AllowedTools.Count > 0
            ? AllowedTools
            : surface == AssistantSurface.Slack ? SlackDefaultTools : GuiDefaultTools;

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
        // The raw schema readers, kept off chat so the allow-listed schema is the only schema an assistant is
        // given. Not every one of them is unfiltered: the column search serves allow-listed columns only, and
        // describe_object's column list is filtered the same way. They are excluded as a group because the rest
        // of what they carry is not, and cannot be - an object or definition search returns table names, CREATE
        // TABLE text, and module bodies naming every column a table has, and detect_unique_key profiles live
        // rows. They stay available to other MCP clients, and their data stays on the GUI's own Catalog pages,
        // which is the surface a signed-in engineer browses the catalog from.
        "list_schemas", "catalog_tree", "lineage_objects", "lineage_object_detail", "lineage_object_columns",
        "describe_object", "search_all", "search_schema", "pipeline_columns", "get_table_key",
        "get_table_joins", "detect_unique_key",
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
