using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The locale-deterministic selection rule: within the date and numeric families the inferencer takes
/// the FIRST candidate that meets the threshold (locale-preferred), not the highest count. So ambiguous
/// values resolve to the server locale consistently and only genuinely foreign-formatted columns fall
/// back. Numerics are normalized through the bound locale in the emitted expression.
/// </summary>
public sealed class LocaleInferenceTests
{
    private static readonly TypeInferencer Inferencer = new();

    private static readonly ServerLocale Norwegian = new()
    {
        Culture = "nb-NO",
        DateOrder = DateOrder.Dmy,
        DecimalSeparator = ',',
        GroupSeparator = '.',
    };

    [Fact]
    public void Date_AmbiguousAcrossStyles_PicksThePreferredCandidateNotTheHighestCount()
    {
        // Both styles convert every value (an ambiguous slash date). The day-first 103 is listed first,
        // so it must win even though 101 has the same count - this is what stops per-column flipping.
        var profile = new ColumnProfile
        {
            ColumnName = "C",
            Total = 100,
            NonNull = 100,
            DateCandidates =
            [
                new DateConversionCandidate { Style = 103, Count = 100 },
                new DateConversionCandidate { Style = 101, Count = 100 },
            ],
        };

        var result = Inferencer.Infer(profile, new TypeInferencePolicy(), Norwegian);

        Assert.Equal("date", result.DataType);
        Assert.Equal(103, result.Style);
    }

    [Fact]
    public void Date_PreferredBelowThreshold_FallsBackToTheNextStyle()
    {
        // The preferred style cannot parse the column (a genuinely month-first column on a day-first
        // server); only the fallback converts everything, so the fallback is chosen.
        var profile = new ColumnProfile
        {
            ColumnName = "C",
            Total = 100,
            NonNull = 100,
            DateCandidates =
            [
                new DateConversionCandidate { Style = 103, Count = 40 },
                new DateConversionCandidate { Style = 101, Count = 100 },
            ],
        };

        var result = Inferencer.Infer(profile, new TypeInferencePolicy(), Norwegian);

        Assert.Equal(101, result.Style);
    }

    [Fact]
    public void Numeric_PrefersLocaleConvention_AndNormalizesInTheExpression()
    {
        var profile = new ColumnProfile
        {
            ColumnName = "Price",
            Total = 100,
            NonNull = 100,
            NumericCandidates =
            [
                new NumericConversionCandidate { Format = NumericFormat.Locale, AsDecimal = 100, AsFloat = 100, MaxScale = 2, MaxIntegerDigits = 3 },
                new NumericConversionCandidate { Format = NumericFormat.Invariant, AsDecimal = 100, AsFloat = 100, MaxScale = 2, MaxIntegerDigits = 3 },
            ],
        };

        var result = Inferencer.Infer(profile, new TypeInferencePolicy(), Norwegian);

        Assert.Equal("decimal(5, 2)", result.DataType);
        Assert.Equal(NumericFormat.Locale, result.NumericFormat);
        // Norwegian normalization: strip '.' grouping, rewrite ',' decimal to '.'.
        Assert.Contains("REPLACE", result.SelectExpression, StringComparison.Ordinal);
        Assert.Contains("', '.'", result.SelectExpression, StringComparison.Ordinal); // the ',' -> '.' rewrite
        Assert.StartsWith("CONVERT(decimal(5, 2),", result.SelectExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void Numeric_LocaleBelowThreshold_FallsBackToInvariant()
    {
        var profile = new ColumnProfile
        {
            ColumnName = "Price",
            Total = 100,
            NonNull = 100,
            NumericCandidates =
            [
                new NumericConversionCandidate { Format = NumericFormat.Locale, AsDecimal = 0, AsFloat = 0, MaxScale = 0, MaxIntegerDigits = 0 },
                new NumericConversionCandidate { Format = NumericFormat.Invariant, AsDecimal = 100, AsFloat = 100, MaxScale = 2, MaxIntegerDigits = 3 },
            ],
        };

        var result = Inferencer.Infer(profile, new TypeInferencePolicy(), Norwegian);

        Assert.Equal("decimal(5, 2)", result.DataType);
        Assert.Equal(NumericFormat.Invariant, result.NumericFormat);
    }

    [Fact]
    public void DateTime_BeatsDate_AndPicksThePreferredDateTimeStyle()
    {
        var profile = new ColumnProfile
        {
            ColumnName = "C",
            Total = 100,
            NonNull = 100,
            DateTimeCandidates =
            [
                new DateConversionCandidate { Style = 104, Count = 100 },
                new DateConversionCandidate { Style = 121, Count = 100 },
            ],
            DateCandidates = [new DateConversionCandidate { Style = 104, Count = 100 }],
        };

        var result = Inferencer.Infer(profile, new TypeInferencePolicy(), Norwegian);

        Assert.Equal("datetime2", result.DataType);
        Assert.Equal(104, result.Style);
    }
}
