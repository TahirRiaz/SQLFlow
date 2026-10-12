using SqlFlow.ControlPlane.Background;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The word rules <see cref="QuestionSearch"/> scores stored questions by, with no database: how a word is stemmed,
/// when a stored question matches a term, which words a question means, and what a search falls back to when an
/// expansion gives it nothing to search on.
/// </summary>
public sealed class QuestionSearchRulesTests
{
    [Theory]
    // A plural of a word ending in "e" is that word plus "s", not a sibilant "-es" plural.
    [InlineData("employees", "employee")]
    [InlineData("sales", "sale")]
    [InlineData("prices", "price")]
    [InlineData("invoices", "invoice")]
    [InlineData("revenues", "revenue")]
    [InlineData("stores", "store")]
    // The sibilant "-es" plural, and a singular already ending in "ss", "us" or "is".
    [InlineData("boxes", "box")]
    [InlineData("taxes", "tax")]
    [InlineData("classes", "class")]
    [InlineData("matches", "match")]
    [InlineData("wishes", "wish")]
    [InlineData("statuses", "status")]
    [InlineData("addresses", "address")]
    [InlineData("buses", "bus")]
    // "-ies" and "-ied" to "y".
    [InlineData("categories", "category")]
    [InlineData("carried", "carry")]
    // "-ing", "-ed" and a silent "e".
    [InlineData("selling", "sell")]
    [InlineData("sells", "sell")]
    [InlineData("ordered", "order")]
    [InlineData("pricing", "price")]
    [InlineData("priced", "price")]
    [InlineData("stored", "store")]
    // Case and surrounding space do not matter.
    [InlineData("  Employees ", "EMPLOYEE")]
    public void TwoFormsOfAWord_ShareAStem(string one, string other)
    {
        Assert.Equal(QuestionSearch.Stem(one), QuestionSearch.Stem(other));
    }

    [Theory]
    [InlineData("use", "use")]
    [InlineData("uses", "use")]
    [InlineData("yes", "yes")]
    [InlineData("bus", "bus")]
    [InlineData("class", "class")]
    [InlineData("analysis", "analysis")]
    [InlineData("ice", "ice")]
    public void AStem_IsNeverCutBelowThreeCharacters_NorFromASingular(string word, string stem)
    {
        Assert.Equal(stem, QuestionSearch.Stem(word));
    }

    [Theory]
    [InlineData("sale", "wholesale")]
    [InlineData("sell", "seller")]
    [InlineData("order", "border")]
    public void DifferentWords_DoNotShareAStem(string one, string other)
    {
        Assert.NotEqual(QuestionSearch.Stem(one), QuestionSearch.Stem(other));
    }

    [Theory]
    [InlineData("How many employees joined last year?", "employee", true)]
    [InlineData("What were sales by region?", "sale", true)]
    [InlineData("What were wholesale prices?", "sale", false)]
    [InlineData("Show the price list", "pricing", true)]
    [InlineData("Revenue per sales/region", "region", true)]
    [InlineData("Net revenue by quarter", "net revenue", true)]
    [InlineData("Cabinet revenue by quarter", "net revenue", false)]
    [InlineData("Revenue, net revenue!", "net revenue", true)]
    [InlineData("anything", "", false)]
    public void AQuestionMatchesATerm_AsAWholeWordOrPhrase_OrByStem(string question, string term, bool matches)
    {
        Assert.Equal(matches, QuestionSearch.MatchesTerm(question, term));
    }

    [Fact]
    public void TheSameQuestionInOtherForms_MeansTheSameWords()
    {
        Assert.True(QuestionSearch.MeaningfulStems("How many employees are there?")
            .SetEquals(QuestionSearch.MeaningfulStems("how many EMPLOYEE is there")));
        Assert.False(QuestionSearch.MeaningfulStems("How many employees?")
            .SetEquals(QuestionSearch.MeaningfulStems("How many customers?")));
    }

    [Fact]
    public void Normalize_DropsStopWordsShortTokensAndDuplicates_AndLowercases()
    {
        Assert.Equal(["revenue", "region"], QuestionSearch.Normalize([" Revenue ", "the", "x", "\"region\"", "REVENUE", ""]));
    }

    [Theory]
    [InlineData("sales/region", new[] { "sales", "region" })]
    [InlineData("year-over-year growth", new[] { "year", "growth" })]
    [InlineData("What is the (net) margin?", new[] { "net", "margin" })]
    [InlineData("the of and", new string[0])]
    public void TheTypedWords_AreSplitAsStoredQuestionsAre(string question, string[] expected)
    {
        Assert.Equal(expected, QuestionSearch.TypedTerms(question));
    }
}
