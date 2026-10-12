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

    [Theory]
    // Numbers, and codes kept whole with their slashes, dashes and decimal points.
    [InlineData("Which stores sold SKU-1042 from 3000 to 3005 units in EMEA?", new[] { "SKU-1042", "3000", "3005", "EMEA" })]
    [InlineData("Which orders shipped to UK warehouse 12/4-K?", new[] { "UK", "12/4-K" })]
    [InlineData("Revenue in Q3 2024, by region.", new[] { "Q3", "2024" })]
    // A capitalised word is a name anywhere but at the start.
    [InlineData("Which customers in Oslo and Bergen bought VAT-exempt items?", new[] { "Oslo", "Bergen", "VAT-exempt" })]
    // Every word in quotes, whatever its case.
    [InlineData("Sales for 'new york' at 0.5 percent", new[] { "new", "york", "0.5" })]
    [InlineData("Sales in “north west”.", new[] { "north", "west" })]
    // A question naming nothing in particular, and an apostrophe inside a word, which opens no quote.
    [InlineData("How many customers do we have?", new string[0])]
    [InlineData("Which customer's orders are newest", new string[0])]
    public void AQuestionsValues_AreItsNumbersCodesNamesAndQuotedWords(string question, string[] expected)
    {
        Assert.Equal(expected, QuestionSearch.NamedValues(question));
    }

    [Theory]
    // The same values however the question is written: case, inflection and the words around them aside.
    [InlineData("Which customers bought VAT items?", "which customers bought vat items", new string[0])]
    [InlineData("Which Customers Hold VAT Numbers?", "Which customer holds VAT numbers?", new string[0])]
    [InlineData("Which stores in the Oslo region sold most?", "which stores in oslo sold most", new string[0])]
    [InlineData("What were sales in 2024?", "Sales in 2024, please", new string[0])]
    [InlineData("How many customers do we have?", "How many customers are there?", new string[0])]
    // Another value on either side, each as its own question writes it, the typed question's first.
    [InlineData("What was revenue in 2023?", "What was revenue in 2024?", new[] { "2023", "2024" })]
    [InlineData(
        "Which stores sold SKU-77 from 2000 to 2100 units?",
        "Which stores sold SKU-42 from 3000 to 3005 units in EMEA?",
        new[] { "SKU-77", "2000", "2100", "SKU-42", "3000", "3005", "EMEA" })]
    [InlineData(
        "What were sales for order PO-2291/4-B-17?",
        "What were sales for order PO-8813/2-C-05?",
        new[] { "PO-2291/4-B-17", "PO-8813/2-C-05" })]
    // A value only one of them names: the typed question narrows the stored answer, or the stored answer is narrower.
    [InlineData("Which stores in the Bergen region sold VAT items?", "Which stores sold VAT items?", new[] { "Bergen" })]
    [InlineData("Which stores sold VAT items?", "Which stores in the Oslo region sold VAT items?", new[] { "Oslo" })]
    public void TwoQuestions_DifferInTheValuesOnlyOneOfThemNames(string typed, string stored, string[] expected)
    {
        Assert.Equal(expected, QuestionSearch.DifferingValues(typed, stored));
    }

    [Theory]
    // One word of six: a stored question about a breakdown nobody asked for.
    [InlineData("How many wellbores and well logs are there per country, basin and field?", new[] { "wellbores" }, 1.0 / 6)]
    // A close variant keeps most of its words.
    [InlineData("Which wellbores have NMR data?", new[] { "wellbores", "nmr", "logs" }, 2.0 / 3)]
    // Words holding a digit are values, not counted; "m" is too short to count.
    [InlineData("Which wellbores have a DT curve from 3000 to 3005 m MD?", new[] { "wellbores", "dt", "curve", "2000" }, 3.0 / 4)]
    // Words that only ask for a count or a list are phrasing, not what the question is about.
    [InlineData("Show me the number of customers", new[] { "customers" }, 1.0)]
    [InlineData("List all customers with each total", new[] { "customer" }, 1.0)]
    // Forms of a word count once and cover each other.
    [InlineData("Which categories sell most, and which category sells least?", new[] { "category", "selling", "least" }, 1.0)]
    // A phrase covers its words only where the stored question holds the whole phrase.
    [InlineData("What is net revenue by region?", new[] { "net revenue" }, 2.0 / 3)]
    [InlineData("What is cabinet revenue by region?", new[] { "net revenue" }, 0.0)]
    // A question left with no word to count covers nothing.
    [InlineData("Show all 2024?", new[] { "2024", "show" }, 0.0)]
    [InlineData("How many customers?", new string[0], 0.0)]
    public void AStoredQuestionsCoverage_IsTheShareOfItsOwnWordsTheSearchCarries(
        string stored, string[] terms, double expected)
    {
        Assert.Equal(expected, QuestionSearch.CoverageOf(stored, terms), precision: 6);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void ACoverageFloor_OutsideZeroToOne_IsRefused(double floor)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => QuestionSearch.CheckCoverage(floor));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void ACoverageFloor_FromZeroToOne_IsAccepted(double floor)
    {
        QuestionSearch.CheckCoverage(floor);
    }
}
