using SqlFlow.Assistant;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The parts of the assistant instructions that encode a product decision rather than wording: saving an answer is a
/// separate, explicit decision from running a query, a rejection is never recorded, and each kind of question is told
/// about the tools it is given and no others. The model follows these sentences, so a rewrite that drops one silently
/// changes what the assistant stores or which tools it reaches for.
/// </summary>
public sealed class AssistantInstructionsTests
{
    private static string Build(AssistantSurface surface, AssistantQuestionKind kind)
        => AssistantInstructions.Build(new AssistantSettings { Surface = surface, GuiBaseUrl = string.Empty }, kind);

    private static readonly AssistantSurface[] Surfaces = [AssistantSurface.Slack, AssistantSurface.Gui];

    [Fact]
    public void Slack_OffersToSaveAResult_AndNeverTreatsARunApprovalAsASave()
    {
        var slack = Build(AssistantSurface.Slack, AssistantQuestionKind.Business);

        // The offer's wording travels on the run_query result (`saveOffer`), so every client shows the same line.
        Assert.Contains("`saveOffer`", slack, StringComparison.Ordinal);
        Assert.Contains("Saying yes to running a query is never a request to save it.", slack, StringComparison.Ordinal);
    }

    [Fact]
    public void Slack_AsksForRepliesThatStartWithTheCommand_SinceOnlyThoseReachTheBusinessTools()
    {
        var slack = Build(AssistantSurface.Slack, AssistantQuestionKind.Business);

        Assert.Contains("\"!run\"", slack, StringComparison.Ordinal);
        Assert.Contains("\"!save\"", slack, StringComparison.Ordinal);
    }

    [Fact]
    public void NoSurface_IsToldToRecordARejection()
    {
        foreach (var surface in Surfaces)
        {
            var instructions = Build(surface, AssistantQuestionKind.Business);

            Assert.DoesNotContain("outcome \"rejected\"", instructions, StringComparison.Ordinal);
            Assert.Contains("nothing is saved, so do not call confirm_question", instructions, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ABusinessQuestion_IsToldOnlyAboutTheSemanticLayer_TheStoredQuestions_AndTheQueryTools()
    {
        foreach (var surface in Surfaces)
        {
            var business = Build(surface, AssistantQuestionKind.Business);

            Assert.Contains("find_similar_questions FIRST", business, StringComparison.Ordinal);
            Assert.Contains("SEMANTIC LAYER", business, StringComparison.Ordinal);
            foreach (var estateTool in new[] { "describe_object", "search_schema", "search_all", "list_runs", "object_lineage", "search_docs" })
            {
                Assert.DoesNotContain(estateTool, business, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AnEstateQuestion_IsToldAboutTheWholeSchema_AndNeverAboutTheBusinessTools()
    {
        foreach (var surface in Surfaces)
        {
            var estate = Build(surface, AssistantQuestionKind.Estate);

            Assert.Contains("describe_object", estate, StringComparison.Ordinal);
            Assert.Contains("every table, view, and column the catalog holds", estate, StringComparison.Ordinal);
            foreach (var businessTool in new[]
            {
                "get_semantic_layer", "search_semantic_layer", "describe_semantic_table", "find_similar_questions",
                "prepare_query", "run_query", "auto_run_trusted_match", "confirm_question",
            })
            {
                Assert.DoesNotContain(businessTool, estate, StringComparison.Ordinal);
            }

            // And it says how to ask a business question instead of answering one from the schema.
            Assert.Contains("starting with \"!\"", estate, StringComparison.Ordinal);
        }
    }
}
