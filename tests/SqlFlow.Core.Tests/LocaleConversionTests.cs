using System.Globalization;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The pure locale-to-SQL mapping: date-style candidate ordering, numeric-normalization SQL, and the
/// culture-to-<see cref="ServerLocale"/> derivation. No database, so it always runs.
/// </summary>
public sealed class LocaleConversionTests
{
    private static readonly ServerLocale Norwegian = new()
    {
        Culture = "nb-NO",
        DateOrder = DateOrder.Dmy,
        DecimalSeparator = ',',
        GroupSeparator = '.',
    };

    [Theory]
    [InlineData(DateOrder.Dmy, 104)] // dd.mm.yyyy leads for day-first locales
    [InlineData(DateOrder.Mdy, 101)] // mm/dd/yyyy leads for month-first locales
    [InlineData(DateOrder.Ymd, 23)]  // ISO leads for year-first locales
    public void DateStyleCandidates_LeadWithThePreferredStyle(DateOrder order, int expectedFirst)
    {
        var candidates = LocaleConversion.DateStyleCandidates(order);

        Assert.Equal(expectedFirst, candidates[0]);
        Assert.Contains(23, candidates); // ISO is always available as a safe fallback
    }

    [Theory]
    [InlineData(DateOrder.Dmy, 104)]
    [InlineData(DateOrder.Mdy, 101)]
    [InlineData(DateOrder.Ymd, 121)] // ISO datetime leads for year-first locales
    public void DateTimeStyleCandidates_LeadWithThePreferredStyle_AndOfferIso(DateOrder order, int expectedFirst)
    {
        var candidates = LocaleConversion.DateTimeStyleCandidates(order);

        Assert.Equal(expectedFirst, candidates[0]);
        Assert.Contains(121, candidates); // ODBC-canonical ISO datetime is always offered
    }

    [Fact]
    public void NumericInput_Locale_OnlyStripsDotGroupingWhenACommaIsPresent()
    {
        var sql = LocaleConversion.NumericInput("[C]", NumericFormat.Locale, Norwegian);

        // Only when a comma is present: strip '.', spaces and nbsp (grouping), then turn ',' into '.'.
        // Without a comma the value is left untouched, so dotted dates are not seen as numbers.
        Assert.Equal(
            "CASE WHEN CHARINDEX(',', [C]) > 0 THEN REPLACE(REPLACE(REPLACE(REPLACE([C], '.', ''), ' ', ''), NCHAR(160), ''), ',', '.') ELSE [C] END",
            sql);
    }

    [Fact]
    public void NumericInput_Invariant_StripsCommaGroupingOnly()
    {
        var sql = LocaleConversion.NumericInput("[C]", NumericFormat.Invariant, Norwegian);

        // The decimal is already '.', so only the ',' grouping and spaces are stripped (no decimal rewrite).
        Assert.Equal("REPLACE(REPLACE(REPLACE([C], ',', ''), ' ', ''), NCHAR(160), '')", sql);
    }

    [Fact]
    public void NumericInput_DotDecimalLocale_MatchesInvariant()
    {
        var american = new ServerLocale { Culture = "en-US", DateOrder = DateOrder.Mdy, DecimalSeparator = '.', GroupSeparator = ',' };

        Assert.Equal(
            LocaleConversion.NumericInput("[C]", NumericFormat.Invariant, american),
            LocaleConversion.NumericInput("[C]", NumericFormat.Locale, american));
    }

    [Theory]
    [InlineData("nb-NO", DateOrder.Dmy)]
    [InlineData("en-US", DateOrder.Mdy)]
    [InlineData("sv-SE", DateOrder.Ymd)]
    public void DateOrderFromCulture_ReadsTheShortDatePattern(string culture, DateOrder expected)
        => Assert.Equal(expected, LocaleConversion.DateOrderFromCulture(CultureInfo.GetCultureInfo(culture)));

    [Fact]
    public void FromCulture_NorwegianUsesCommaDecimalAndDotGrouping()
    {
        var locale = LocaleConversion.FromCulture(CultureInfo.GetCultureInfo("nb-NO"), DateOrder.Dmy);

        Assert.Equal("nb-NO", locale.Culture);
        Assert.Equal(',', locale.DecimalSeparator);
        Assert.Equal('.', locale.GroupSeparator); // normalized to the conventional pair, not the culture's nbsp
    }

    [Fact]
    public void FromCulture_AmericanUsesDotDecimalAndCommaGrouping()
    {
        var locale = LocaleConversion.FromCulture(CultureInfo.GetCultureInfo("en-US"), DateOrder.Mdy);

        Assert.Equal('.', locale.DecimalSeparator);
        Assert.Equal(',', locale.GroupSeparator);
    }
}
