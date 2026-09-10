using SqlFlow.ControlPlane.Api;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The parsing and excerpt rules behind the global search. The behavior that matters here is the one a single
/// literal LIKE could never give: a multi-word query is matched word by word, so "ferry passengers" reaches
/// <c>FerryPassengers_PerDeparture</c>, whose words are run together and would never contain the phrase with its
/// space. Needs no database.
/// </summary>
public sealed class SearchQueryTests
{
    [Fact]
    public void Parse_SplitsAMultiWordQueryIntoTokens_SoRunTogetherNamesAreReachable()
    {
        var query = SearchQuery.Parse("ferry passengers");

        Assert.NotNull(query);
        Assert.Equal("ferry passengers", query.Phrase);
        Assert.Equal(["ferry", "passengers"], query.Tokens);

        // The point of tokenizing: every token is present in the run-together name, the phrase never is.
        const string Name = "FerryPassengers_PerDeparture";
        Assert.Equal(2, query.TokenHits(Name));
        Assert.DoesNotContain(query.Phrase, Name, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_KeepsASingleTokenQueryWhole()
    {
        var query = SearchQuery.Parse("  SourceRank  ");

        Assert.NotNull(query);
        Assert.Equal("SourceRank", query.Phrase);
        Assert.Equal(["SourceRank"], query.Tokens);
    }

    [Fact]
    public void Parse_DropsSingleCharacterWords_SoAStrayLetterCannotEmptyAGoodQuery()
    {
        var query = SearchQuery.Parse("a SourceRank x");

        Assert.NotNull(query);
        Assert.Equal(["SourceRank"], query.Tokens);
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitivelyAndCapsTokenCount()
    {
        var query = SearchQuery.Parse("rank RANK Rank");
        Assert.NotNull(query);
        Assert.Single(query.Tokens);

        var many = SearchQuery.Parse(string.Join(' ', Enumerable.Range(0, 20).Select(i => $"token{i}")));
        Assert.NotNull(many);
        Assert.Equal(SearchQuery.MaxTokens, many.Tokens.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a b")]
    public void Parse_ReturnsNullWhenNothingIsSearchable(string? raw)
        => Assert.Null(SearchQuery.Parse(raw));

    [Fact]
    public void TokenHits_CountsCaseInsensitivelyAndTreatsMissingTextAsZero()
    {
        var query = SearchQuery.Parse("ferry passengers");
        Assert.NotNull(query);

        Assert.Equal(2, query.TokenHits("FERRYPASSENGERS"));
        Assert.Equal(1, query.TokenHits("ferry terminals"));
        Assert.Equal(0, query.TokenHits(null));
        Assert.Equal(0, query.TokenHits(string.Empty));
    }

    [Fact]
    public void FirstMatch_PrefersTheVerbatimPhraseOverAnEarlierToken()
    {
        var query = SearchQuery.Parse("source rank");
        Assert.NotNull(query);

        // "source" occurs first, but the whole phrase occurs later: the phrase wins, because that is the match a
        // reader wants centered in the excerpt.
        const string Text = "source_system, then source rank";
        var (index, length) = query.FirstMatch(Text);
        Assert.Equal(Text.IndexOf("source rank", StringComparison.Ordinal), index);
        Assert.Equal("source rank".Length, length);
    }

    [Fact]
    public void FirstMatch_FallsBackToTheEarliestToken_AndReportsMissWhenNeitherOccurs()
    {
        var query = SearchQuery.Parse("ferry passengers");
        Assert.NotNull(query);

        var (index, length) = query.FirstMatch("counting passengers on the ferry");
        Assert.Equal("counting ".Length, index);
        Assert.Equal("passengers".Length, length);

        // A row can match because a sibling field carried the tokens; this text itself has none.
        Assert.Equal((-1, 0), query.FirstMatch("nothing relevant here"));
        Assert.Equal((-1, 0), query.FirstMatch(string.Empty));
    }
}
