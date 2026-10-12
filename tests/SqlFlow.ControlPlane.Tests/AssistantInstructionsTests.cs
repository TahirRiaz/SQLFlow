using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant answered "is the orders table incremental?" with an invented watermark probe because nothing it had
/// read said otherwise and nothing forbade filling the gap. These tests pin the sentences that stop that, on both
/// surfaces the instructions are built for: the grounding rule (state only what a tool returned), on every kind of
/// question, and the routing of load-mode questions to the server-computed load profile instead of the YAML, on the
/// estate questions that have the tools for it.
/// </summary>
public sealed class AssistantInstructionsTests
{
    [Theory]
    [InlineData(AssistantSurface.Slack, AssistantQuestionKind.Estate)]
    [InlineData(AssistantSurface.Gui, AssistantQuestionKind.Estate)]
    [InlineData(AssistantSurface.Slack, AssistantQuestionKind.Business)]
    [InlineData(AssistantSurface.Gui, AssistantQuestionKind.Business)]
    public void EverySurfaceAndKind_CarriesTheGroundingRule(AssistantSurface surface, AssistantQuestionKind kind)
    {
        var text = AssistantInstructions.Build(new AssistantSettings { Surface = surface, GuiBaseUrl = "https://sqlflow.example" }, kind);

        Assert.Contains("Every fact you state", text, StringComparison.Ordinal);
        Assert.Contains("must come from a value a tool returned", text, StringComparison.Ordinal);
        Assert.Contains("say you do not know and name the tool", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantSurface.Slack)]
    [InlineData(AssistantSurface.Gui)]
    public void EverySurface_QuotesExecutedSqlOnlyFromRunStatements(AssistantSurface surface)
    {
        var text = AssistantInstructions.Build(
            new AssistantSettings { Surface = surface, GuiBaseUrl = "https://sqlflow.example" }, AssistantQuestionKind.Estate);

        Assert.Contains("ONLY by quoting run_statements", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantSurface.Slack)]
    [InlineData(AssistantSurface.Gui)]
    public void EverySurface_RoutesLoadModeQuestionsToTheLoadProfile(AssistantSurface surface)
    {
        var text = AssistantInstructions.Build(
            new AssistantSettings { Surface = surface, GuiBaseUrl = "https://sqlflow.example" }, AssistantQuestionKind.Estate);

        Assert.Contains("`loadProfile`", text, StringComparison.Ordinal);
        Assert.Contains("`readMode` is", text, StringComparison.Ordinal);
        Assert.Contains("The upsert key is not", text, StringComparison.Ordinal);
        Assert.Contains("`lastSuccessfulRun`", text, StringComparison.Ordinal);
        Assert.Contains("`runsOnSchedule`", text, StringComparison.Ordinal);
    }
}
