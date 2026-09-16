using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The assistant answered "is Reisefrihet_Ticket incremental?" with an invented watermark probe because nothing
/// it had read said otherwise and nothing forbade filling the gap. These tests pin the two sentences that stop
/// that, on both surfaces the instructions are built for: the grounding rule (state only what a tool returned)
/// and the routing of load-mode questions to the server-computed load profile instead of the YAML.
/// </summary>
public sealed class AssistantInstructionsTests
{
    [Theory]
    [InlineData(AssistantSurface.Slack)]
    [InlineData(AssistantSurface.Gui)]
    public void EverySurface_CarriesTheGroundingRule(AssistantSurface surface)
    {
        var text = AssistantInstructions.Build(new AssistantSettings { Surface = surface, GuiBaseUrl = "https://sqlflow.example" });

        Assert.Contains("Every fact you state", text, StringComparison.Ordinal);
        Assert.Contains("must come from a value a tool returned", text, StringComparison.Ordinal);
        Assert.Contains("say you do not know and name the tool", text, StringComparison.Ordinal);
        Assert.Contains("ONLY by quoting run_statements", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AssistantSurface.Slack)]
    [InlineData(AssistantSurface.Gui)]
    public void EverySurface_RoutesLoadModeQuestionsToTheLoadProfile(AssistantSurface surface)
    {
        var text = AssistantInstructions.Build(new AssistantSettings { Surface = surface, GuiBaseUrl = "https://sqlflow.example" });

        Assert.Contains("`loadProfile`", text, StringComparison.Ordinal);
        Assert.Contains("`readMode` is", text, StringComparison.Ordinal);
        Assert.Contains("The upsert key is not", text, StringComparison.Ordinal);
        Assert.Contains("`lastSuccessfulRun`", text, StringComparison.Ordinal);
        Assert.Contains("`runsOnSchedule`", text, StringComparison.Ordinal);
    }
}
