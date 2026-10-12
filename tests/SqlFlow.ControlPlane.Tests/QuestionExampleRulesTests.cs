using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The rules the confirm and saved-answer endpoints store a question by, with no database: which outcomes are known,
/// how a typed question is normalized and bounded, how object keys are stored, and which pairs count as the same
/// example.
/// </summary>
public sealed class QuestionExampleRulesTests
{
    [Theory]
    [InlineData("accepted", true)]
    [InlineData(" Accepted ", true)]
    [InlineData("CORRECTED", true)]
    [InlineData("rejected\n", true)]
    [InlineData("accept", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AnOutcome_IsKnownWhateverItsCaseOrSurroundingSpace(string? outcome, bool known)
    {
        Assert.Equal(known, QuestionConfirmationOutcome.IsKnown(outcome));
    }

    [Theory]
    [InlineData("!cwd what is our revenue", "what is our revenue")]
    [InlineData("  !CWD   what is our revenue  ", "what is our revenue")]
    [InlineData("!cwd\twhat is our revenue", "what is our revenue")]
    [InlineData("!cwd", "")]
    [InlineData("!cwdx what", "!cwdx what")]
    [InlineData("what about !cwd", "what about !cwd")]
    [InlineData(null, "")]
    public void TheBusinessQuestionCommand_IsTakenOffTheFront_AndNothingElse(string? typed, string stored)
    {
        Assert.Equal(stored, QuestionExampleEndpoints.NormalizeQuestion(typed));
    }

    [Fact]
    public void AQuestionIsBounded_AfterTheCommandIsTakenOff()
    {
        var atLimit = new string('q', QuestionExampleEndpoints.MaxQuestionLength);
        Assert.Null(QuestionExampleEndpoints.QuestionProblem(atLimit));
        Assert.Null(QuestionExampleEndpoints.QuestionProblem(QuestionExampleEndpoints.NormalizeQuestion("!cwd " + atLimit)));
        Assert.Contains("over the 1000-character limit", QuestionExampleEndpoints.QuestionProblem(atLimit + "q"), StringComparison.Ordinal);
        Assert.Contains("must not be empty", QuestionExampleEndpoints.QuestionProblem(string.Empty), StringComparison.Ordinal);
    }

    [Fact]
    public void ObjectKeys_AreStoredOnePerLine_BlanksDropped_AndReadBackAsWritten()
    {
        var joined = QuestionExampleEndpoints.JoinObjectKeys([" a|b ", "", "  ", "c|d"]);
        Assert.Equal("a|b\nc|d", joined);
        Assert.Equal(["a|b", "c|d"], QuestionExampleEndpoints.SplitObjectKeys(joined));
        Assert.Equal(string.Empty, QuestionExampleEndpoints.JoinObjectKeys(null));
        Assert.Empty(QuestionExampleEndpoints.SplitObjectKeys(string.Empty));
    }

    [Fact]
    public void TheSameExample_IsTheSamePair_WhateverItsSpacingOrCase()
    {
        var hash = SemanticExampleHash.Compute("How many parcels?", "SELECT COUNT(*) FROM Parcels");
        Assert.Equal(hash, SemanticExampleHash.Compute("  how MANY parcels? ", "select\r\n\tcount(*)   from parcels"));

        // A different question, a different query, or the boundary between them moved, is a different example.
        Assert.NotEqual(hash, SemanticExampleHash.Compute("How many parcels shipped?", "SELECT COUNT(*) FROM Parcels"));
        Assert.NotEqual(hash, SemanticExampleHash.Compute("How many parcels?", "SELECT COUNT(*) FROM Parcels;"));
        Assert.NotEqual(SemanticExampleHash.Compute("a b", "c"), SemanticExampleHash.Compute("a", "b c"));
    }
}
