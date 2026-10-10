using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The parts of the assistant instructions that encode a product decision rather than wording: saving an answer is
/// a separate, explicit decision from running a query, and a rejection is never recorded. The model follows these
/// sentences, so a rewrite that drops one silently changes what the assistant stores.
/// </summary>
public sealed class AssistantInstructionsTests
{
    private static string Build(AssistantSurface surface)
        => AssistantInstructions.Build(new AssistantSettings { Surface = surface, GuiBaseUrl = string.Empty });

    [Fact]
    public void Slack_OffersToSaveAResult_AndNeverTreatsARunApprovalAsASave()
    {
        var slack = Build(AssistantSurface.Slack);

        // The offer's wording travels on the run_query result (`saveOffer`), so every client shows the same line.
        Assert.Contains("`saveOffer`", slack, StringComparison.Ordinal);
        Assert.Contains("Saying yes to running a query is never a request to save it.", slack, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSurface_IsToldToRecordARejection()
    {
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            var instructions = Build(surface);

            Assert.DoesNotContain("outcome \"rejected\"", instructions, StringComparison.Ordinal);
            Assert.Contains("nothing is saved, so do not call confirm_question", instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NoSurface_IsToldItCannotRunSql()
    {
        // Both surfaces can answer a business question by running a query now; a blanket "you cannot run SQL" would
        // contradict the tools they are given.
        foreach (var surface in new[] { AssistantSurface.Slack, AssistantSurface.Gui })
        {
            Assert.DoesNotContain("You cannot run SQL", Build(surface), StringComparison.Ordinal);
        }
    }
}
