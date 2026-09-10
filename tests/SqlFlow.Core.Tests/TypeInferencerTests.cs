using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests;

public sealed class TypeInferencerTests
{
    private static readonly TypeInferencer Inferencer = new();

    // The locale only matters for numeric normalization and date-style ordering; the invariant locale
    // keeps these assertions stable (decimal '.', grouping stripped). Locale-specific behavior lives in
    // LocaleConversionTests and LocaleInferenceTests.
    private static readonly ServerLocale Locale = ServerLocale.Invariant;

    private static ColumnProfile Profile(
        long nonNull,
        long asBigInt = 0,
        long asDecimal = 0,
        long asFloat = 0,
        long asDateTime2 = 0,
        long asDate = 0,
        int? dateStyle = null,
        long asBitTokens = 0,
        long asGuid = 0,
        long leadingZeroInts = 0,
        int maxLen = 12,
        int maxIntDigits = 0,
        int maxScale = 0,
        long? min = null,
        long? max = null)
        => new()
        {
            ColumnName = "C",
            Total = nonNull,
            NonNull = nonNull,
            AsBigInt = asBigInt,
            AsGuid = asGuid,
            AsBitTokens = asBitTokens,
            LeadingZeroInts = leadingZeroInts,
            MaxLen = maxLen,
            MinValue = min,
            MaxValue = max,
            DateTimeCandidates = asDateTime2 > 0
                ? [new DateConversionCandidate { Style = dateStyle ?? 121, Count = asDateTime2 }]
                : [],
            DateCandidates = asDate > 0
                ? [new DateConversionCandidate { Style = dateStyle ?? 23, Count = asDate }]
                : [],
            NumericCandidates = asDecimal > 0 || asFloat > 0
                ? [new NumericConversionCandidate
                    {
                        Format = NumericFormat.Locale,
                        AsDecimal = asDecimal,
                        AsFloat = asFloat,
                        MaxScale = maxScale,
                        MaxIntegerDigits = maxIntDigits,
                    }]
                : [],
        };

    [Fact]
    public void AllNull_KeepsString()
    {
        var result = Inferencer.Infer(Profile(nonNull: 0), new TypeInferencePolicy(), Locale);

        Assert.False(result.Converted);
        Assert.Equal("[C]", result.SelectExpression);
    }

    [Fact]
    public void Integers_PickSmallestFittingType_WithConvertByDefault()
    {
        // Fail-loud is the default, so the emitted transform uses CONVERT (a bad value halts the load).
        var result = Inferencer.Infer(Profile(100, asBigInt: 100, min: 1, max: 1000), new TypeInferencePolicy(), Locale);

        Assert.Equal("smallint", result.DataType);
        Assert.Equal("CONVERT(smallint, [C])", result.SelectExpression);
    }

    [Fact]
    public void LeadingZeros_KeepString_WhenPreserveEnabled()
    {
        var profile = Profile(100, asBigInt: 100, leadingZeroInts: 100, min: 1, max: 9);
        var result = Inferencer.Infer(profile, new TypeInferencePolicy { PreserveLeadingZeros = true }, Locale);

        Assert.False(result.Converted);
    }

    [Fact]
    public void Decimal_ComputesPrecisionAndScale()
    {
        var result = Inferencer.Infer(Profile(100, asDecimal: 100, maxIntDigits: 5, maxScale: 2), new TypeInferencePolicy(), Locale);

        Assert.Equal("decimal(7, 2)", result.DataType);
        Assert.StartsWith("CONVERT(decimal(7, 2),", result.SelectExpression, StringComparison.Ordinal);
        Assert.Equal(NumericFormat.Locale, result.NumericFormat);
    }

    [Fact]
    public void Date_UsesChosenStyle()
    {
        var result = Inferencer.Infer(Profile(100, asDate: 100, dateStyle: 103), new TypeInferencePolicy(), Locale);

        Assert.Equal("date", result.DataType);
        Assert.Equal("CONVERT(date, [C], 103)", result.SelectExpression);
        Assert.Equal(103, result.Style);
    }

    [Fact]
    public void BitTokens_InferBit()
        => Assert.Equal("bit", Inferencer.Infer(Profile(100, asBitTokens: 100), new TypeInferencePolicy(), Locale).DataType);

    [Fact]
    public void SilentNullMode_UsesTryConvert()
    {
        var result = Inferencer.Infer(Profile(100, asBigInt: 100, min: 1, max: 5),
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.SilentNull }, Locale);

        Assert.Equal("TRY_CONVERT(tinyint, [C])", result.SelectExpression);
    }

    [Fact]
    public void FailMode_UsesConvertNotTryConvert()
    {
        var result = Inferencer.Infer(Profile(100, asBigInt: 100, min: 1, max: 5),
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.Fail }, Locale);

        Assert.Equal("CONVERT(tinyint, [C])", result.SelectExpression);
    }

    [Fact]
    public void KeepStringMode_NotFullyConvertible_KeepsString()
    {
        var profile = Profile(100, asBigInt: 90, min: 1, max: 5);
        var result = Inferencer.Infer(profile,
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.KeepString, Threshold = 0.9 }, Locale);

        Assert.False(result.Converted);
        Assert.Equal("[C]", result.SelectExpression);
    }

    [Fact]
    public void SilentNullMode_BelowFull_ButAboveThreshold_StillConverts()
    {
        var profile = Profile(100, asBigInt: 95, min: 1, max: 5);
        var result = Inferencer.Infer(profile,
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.SilentNull, Threshold = 0.9 }, Locale);

        Assert.True(result.Converted);
        Assert.Equal("TRY_CONVERT(tinyint, [C])", result.SelectExpression);
    }
}
