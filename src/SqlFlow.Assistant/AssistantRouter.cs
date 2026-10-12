namespace SqlFlow.Assistant;

/// <summary>What one question is answered with: its kind, the tools its run may call, and the instructions that
/// describe exactly those tools.</summary>
/// <param name="Kind">The question's kind (<see cref="AssistantQuestions.KindOf"/>).</param>
/// <param name="Tools">The MCP tools the run may call; never empty.</param>
/// <param name="Instructions">The system instructions for the run.</param>
public sealed record AssistantRoute(AssistantQuestionKind Kind, IReadOnlyList<string> Tools, string Instructions);

/// <summary>
/// Picks each question's <see cref="AssistantRoute"/>, the one place every gateway asks, so no provider can give a
/// business question the estate's tools or the other way round. The two routes are built once, from settings that do
/// not change after startup.
/// </summary>
public sealed class AssistantRouter
{
    private readonly AssistantRoute _estate;
    private readonly AssistantRoute _business;

    public AssistantRouter(AssistantSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _estate = Route(settings, AssistantQuestionKind.Estate);
        _business = Route(settings, AssistantQuestionKind.Business);
    }

    /// <summary>The route of <paramref name="question"/>, by its first character alone.</summary>
    public AssistantRoute For(string? question)
        => AssistantQuestions.KindOf(question) == AssistantQuestionKind.Business ? _business : _estate;

    private static AssistantRoute Route(AssistantSettings settings, AssistantQuestionKind kind)
        => new(kind, [.. settings.ToolsFor(kind).Distinct(StringComparer.Ordinal)], AssistantInstructions.Build(settings, kind));
}
