namespace SqlFlow.Assistant;

/// <summary>What one run is answered with: the conversation's mode, the tools the deployment narrows the session to
/// (empty for every tool the session lists), the instructions, and the MCP address that opens the session in that
/// mode.</summary>
/// <param name="Mode">The conversation's mode (<see cref="AssistantQuestions.ModeOf"/>).</param>
/// <param name="Tools">The deployment's own narrowing (<see cref="McpOptions.AllowedTools"/>); empty means every tool
/// the MCP server lists in the session's mode.</param>
/// <param name="Instructions">The MCP server's instructions with the surface's rendering added.</param>
/// <param name="ServerUri">The MCP address naming the assistant surface and the mode.</param>
public sealed record AssistantRoute(AssistantMode Mode, IReadOnlyList<string> Tools, string Instructions, Uri ServerUri);

/// <summary>
/// Picks each run's <see cref="AssistantRoute"/>, the one place every gateway asks. The mode comes from the
/// conversation's commands; what the mode lists, and the rules the model follows, come from the MCP server, which every
/// other client of it reads too.
/// </summary>
public sealed class AssistantRouter
{
    private readonly AssistantSettings _settings;
    private readonly McpServerInstructions _instructions;
    private readonly IReadOnlyList<string> _tools;

    public AssistantRouter(AssistantSettings settings, McpServerInstructions instructions)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(instructions);
        _settings = settings;
        _instructions = instructions;
        _tools = [.. settings.Mcp.AllowedTools.Where(tool => !string.IsNullOrWhiteSpace(tool)).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>The route of <paramref name="request"/>, in the mode its conversation's commands set.</summary>
    public Task<AssistantRoute> ForAsync(AssistantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return InAsync(AssistantQuestions.ModeOf(request.Question, request.PriorTurns), request.McpBearer, ct);
    }

    /// <summary>The route of a run in <paramref name="mode"/>, read with <paramref name="bearer"/>.</summary>
    public async Task<AssistantRoute> InAsync(AssistantMode mode, string bearer, CancellationToken ct)
    {
        var server = await _instructions.GetAsync(mode, bearer, ct).ConfigureAwait(false);
        return new AssistantRoute(
            mode, _tools, AssistantInstructions.Compose(server, _settings), _settings.Mcp.AssistantServerUri(mode));
    }
}
