using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Non-overlapping edge-case hardening for the type system: type inference boundaries, SqlDataType parsing
/// and rendering corners, monotonic widening within and across families, CLR-to-SQL mapping boundaries, and
/// unicode-to-non-unicode conversion. Every case here is pure and in-memory so the suite always runs, and is
/// deliberately distinct from the cases already covered by TypeInferencerTests, SqlDataTypeTests,
/// SqlTypeResolutionTests, SqlServerTypeMapperTests, and UnicodeConverterTests.
/// </summary>
public sealed class TypeSystemEdgeCaseTests
{
    private static readonly TypeInferencer EdgeInferencer = new();
    private static readonly SqlServerTypeMapper EdgeMapper = new();

    // The invariant locale keeps these assertions stable; locale-specific selection is covered elsewhere.
    private static readonly ServerLocale EdgeLocale = ServerLocale.Invariant;

    // Distinct local profile builder (uniquely named so it cannot collide with helpers in sibling files).
    // Lets each test set MaxLen, float counts, and GUID counts that the existing helper hard-codes.
    private static ColumnProfile EdgeProfile(
        long nonNull,
        long asBigInt = 0,
        long asDecimal = 0,
        long asFloat = 0,
        long asDateTime2 = 0,
        long asDate = 0,
        int? dateStyle = null,
        long asGuid = 0,
        long asBitTokens = 0,
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

    private static SqlTypeResolutionResult EdgeResolve(string existing, string desired)
        => SqlTypeResolution.Resolve(SqlDataType.Parse(existing), SqlDataType.Parse(desired));

    private static ColumnDefinition EdgeMap(SourceColumn column)
        => EdgeMapper.Map(column, columnOverride: null, "varchar(255)");

    // ----------------------------------------------------------------------------------------------------
    // Type inference: integer sizing boundaries (exact tinyint/smallint/int/bigint edges).
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 255, "tinyint")]        // exact tinyint span
    [InlineData(0, 256, "smallint")]       // one past tinyint upper bound
    [InlineData(-1, 255, "smallint")]      // negative forces signed type even within 0..255 magnitude
    [InlineData(-32768, 32767, "smallint")]// exact smallint span
    [InlineData(-32769, 0, "int")]         // one below smallint lower bound
    [InlineData(0, 32768, "int")]          // one past smallint upper bound
    [InlineData(-2147483648, 2147483647, "int")] // exact int span
    [InlineData(0, 2147483648, "bigint")]  // one past int upper bound
    [InlineData(-2147483649, 0, "bigint")] // one below int lower bound
    public void Integer_PicksSmallestFittingType_AtExactBoundaries(long min, long max, string expected)
    {
        var result = EdgeInferencer.Infer(
            EdgeProfile(100, asBigInt: 100, min: min, max: max),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal(expected, result.DataType);
    }

    [Fact]
    public void Integer_NullMinMax_FallsBackToBigint()
    {
        // When the profiler did not capture min/max, the safe choice is the widest integer type.
        var result = EdgeInferencer.Infer(
            EdgeProfile(50, asBigInt: 50, min: null, max: null),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("bigint", result.DataType);
    }

    [Fact]
    public void Integer_FullLongRange_IsBigint()
    {
        var result = EdgeInferencer.Infer(
            EdgeProfile(10, asBigInt: 10, min: long.MinValue, max: long.MaxValue),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("bigint", result.DataType);
    }

    // ----------------------------------------------------------------------------------------------------
    // Type inference: decimal precision/scale clamping.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, "decimal(1, 0)")]    // no integer digits and no scale clamps precision up to 1
    [InlineData(5, 0, "decimal(5, 0)")]    // whole-number decimal
    [InlineData(0, 4, "decimal(4, 4)")]    // pure fractional: precision equals scale
    [InlineData(40, 10, "decimal(38, 10)")]// integer digits overflow clamps precision to 38
    [InlineData(0, 40, "decimal(38, 38)")] // scale beyond 38 clamps scale to 38, precision to 38
    [InlineData(38, 0, "decimal(38, 0)")]  // exact max precision, no scale
    public void Decimal_ClampsPrecisionAndScaleTo38(int intDigits, int scale, string expected)
    {
        var result = EdgeInferencer.Infer(
            EdgeProfile(100, asDecimal: 100, maxIntDigits: intDigits, maxScale: scale),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal(expected, result.DataType);
    }

    // ----------------------------------------------------------------------------------------------------
    // Type inference: detection precedence and policy interactions.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BitTokens_WinOverIntegers_WhenBothMeetThreshold()
    {
        // A column of 0/1 tokens qualifies as both bit and integer; bit is checked first and must win.
        var result = EdgeInferencer.Infer(
            EdgeProfile(100, asBigInt: 100, asBitTokens: 100, min: 0, max: 1),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("bit", result.DataType);
    }

    [Fact]
    public void LeadingZeros_DisablingPreservation_StillInfersInteger()
    {
        // With preservation off, leading-zero integers are converted rather than kept as identity strings.
        var profile = EdgeProfile(100, asBigInt: 100, leadingZeroInts: 100, min: 1, max: 9);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy { PreserveLeadingZeros = false }, EdgeLocale);

        Assert.True(result.Converted);
        Assert.Equal("tinyint", result.DataType);
    }

    [Fact]
    public void LeadingZeros_PreservationOn_ButNoLeadingZeroValues_StillInfersInteger()
    {
        // Preservation only protects columns that actually contain leading zeros.
        var profile = EdgeProfile(100, asBigInt: 100, leadingZeroInts: 0, min: 1, max: 9);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy { PreserveLeadingZeros = true }, EdgeLocale);

        Assert.True(result.Converted);
        Assert.Equal("tinyint", result.DataType);
    }

    [Fact]
    public void Float_ChosenOnlyWhenDecimalDoesNotMeetThreshold()
    {
        // Scientific-notation columns convert as float when too few values parse as plain decimal.
        var profile = EdgeProfile(100, asDecimal: 10, asFloat: 100);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("float", result.DataType);
        Assert.StartsWith("CONVERT(float,", result.SelectExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void Guid_InfersUniqueidentifier_WithTryConvert()
    {
        var result = EdgeInferencer.Infer(EdgeProfile(100, asGuid: 100), new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("uniqueidentifier", result.DataType);
        Assert.Equal("CONVERT(uniqueidentifier, [C])", result.SelectExpression);
    }

    [Fact]
    public void DateTime2_BeatsDate_WhenBothMeetThreshold()
    {
        // datetime2 is evaluated before date, so a column that satisfies both is typed as the richer type.
        var result = EdgeInferencer.Infer(
            EdgeProfile(100, asDateTime2: 100, asDate: 100, dateStyle: 121),
            new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("datetime2", result.DataType);
        Assert.Equal("CONVERT(datetime2, [C], 121)", result.SelectExpression);
    }

    [Fact]
    public void Date_EmitsResolvedStyleArgument()
    {
        // Every date candidate carries an explicit SQL style now, so the inferred date always pins one
        // (here the invariant ISO style 23) rather than relying on the session's ambiguous default.
        var result = EdgeInferencer.Infer(EdgeProfile(100, asDate: 100), new TypeInferencePolicy(), EdgeLocale);

        Assert.Equal("date", result.DataType);
        Assert.Equal("CONVERT(date, [C], 23)", result.SelectExpression);
        Assert.Equal(23, result.Style);
    }

    [Fact]
    public void KeepStringMode_PartiallyConvertibleDate_KeepsString()
    {
        // KeepString converts only fully-convertible columns; a single non-date value preserves the raw text.
        var profile = EdgeProfile(100, asDate: 99, dateStyle: 103);
        var result = EdgeInferencer.Infer(
            profile,
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.KeepString, Threshold = 0.5 }, EdgeLocale);

        Assert.False(result.Converted);
        Assert.Equal("[C]", result.SelectExpression);
    }

    [Fact]
    public void FailMode_Decimal_UsesConvertAndCarriesPrecision()
    {
        var result = EdgeInferencer.Infer(
            EdgeProfile(100, asDecimal: 100, maxIntDigits: 3, maxScale: 2),
            new TypeInferencePolicy { OnConvertError = ConvertErrorMode.Fail }, EdgeLocale);

        Assert.Equal("decimal(5, 2)", result.DataType);
        Assert.StartsWith("CONVERT(decimal(5, 2),", result.SelectExpression, StringComparison.Ordinal);
    }

    [Fact]
    public void ThresholdExactlyMet_Converts()
    {
        // Threshold uses >=, so a ratio landing exactly on the threshold must convert.
        var profile = EdgeProfile(100, asBigInt: 80, min: 1, max: 5);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy { Threshold = 0.8 }, EdgeLocale);

        Assert.True(result.Converted);
        Assert.Equal("tinyint", result.DataType);
    }

    [Fact]
    public void ThresholdOne_OneValueShort_KeepsString()
    {
        // A perfect-conversion policy (1.0) rejects a column with even a single non-conforming value.
        var profile = EdgeProfile(100, asBigInt: 99, min: 1, max: 5);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy { Threshold = 1.0 }, EdgeLocale);

        Assert.False(result.Converted);
    }

    [Theory]
    [InlineData(0, "varchar(1)")]      // empty-ish column clamps the kept length up to 1
    [InlineData(1, "varchar(1)")]
    [InlineData(8000, "varchar(8000)")]// exact non-max ceiling
    [InlineData(8001, "varchar(8000)")]// beyond ceiling clamps back to 8000
    [InlineData(50000, "varchar(8000)")]
    public void KeepString_ClampsLengthBetween1And8000(int maxLen, string expected)
    {
        // No candidate meets the threshold, so the column is kept as a clamped varchar.
        var result = EdgeInferencer.Infer(EdgeProfile(100, maxLen: maxLen), new TypeInferencePolicy(), EdgeLocale);

        Assert.False(result.Converted);
        Assert.Equal(expected, result.DataType);
    }

    [Fact]
    public void AllNull_IgnoresOtherwiseQualifyingCounts()
    {
        // With zero non-null rows the column is kept as string even though counts look fully convertible.
        var profile = EdgeProfile(0, asBigInt: 100, min: 1, max: 5);
        var result = EdgeInferencer.Infer(profile, new TypeInferencePolicy(), EdgeLocale);

        Assert.False(result.Converted);
        Assert.Equal("[C]", result.SelectExpression);
    }

    // ----------------------------------------------------------------------------------------------------
    // SqlDataType: parsing and rendering corners.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("decimal(18)", "decimal(18, 0)")]       // precision-only decimal defaults scale to 0
    [InlineData("numeric(9, 3)", "decimal(9, 3)")]      // numeric synonym normalizes to decimal
    [InlineData("dec(7,2)", "decimal(7, 2)")]           // dec synonym normalizes to decimal
    [InlineData("time(3)", "time(3)")]                  // scale-only temporal type
    [InlineData("datetimeoffset(7)", "datetimeoffset(7)")]
    [InlineData("datetime2", "datetime2")]              // bare scale-only type renders without parentheses
    [InlineData("[varchar](max)", "varchar(max)")]      // bracketed base plus max length
    [InlineData("  BiGiNt  ", "bigint")]                // surrounding whitespace and mixed case
    [InlineData("CHAR(1)", "char(1)")]                  // single-character fixed type
    [InlineData("varbinary ( 16 )", "varbinary(16)")]   // whitespace inside the length argument
    public void Parse_Then_Render_HandlesCorners(string input, string expected)
        => Assert.Equal(expected, SqlDataType.Parse(input).Render());

    [Fact]
    public void Parse_BracketedBaseWithLength_StripsBrackets()
    {
        var parsed = SqlDataType.Parse("[nvarchar](64)");
        Assert.Equal("nvarchar", parsed.BaseType);
        Assert.Equal(64, parsed.Length);
        Assert.False(parsed.IsMax);
    }

    [Fact]
    public void Parse_TimeScaleZero_IsRetained()
    {
        var parsed = SqlDataType.Parse("time(0)");
        Assert.Equal(0, parsed.Scale);
        Assert.Equal("time(0)", parsed.Render());
    }

    [Theory]
    [InlineData("smallmoney", SqlTypeFamily.Money)]
    [InlineData("real", SqlTypeFamily.Approximate)]
    [InlineData("image", SqlTypeFamily.Binary)]
    [InlineData("smalldatetime", SqlTypeFamily.DateTime)]
    [InlineData("time(2)", SqlTypeFamily.DateTime)]
    [InlineData("datetimeoffset(5)", SqlTypeFamily.DateTime)]
    [InlineData("tinyint", SqlTypeFamily.Integer)]
    [InlineData("char(3)", SqlTypeFamily.Text)]
    [InlineData("sysname", SqlTypeFamily.Text)]
    [InlineData("timestamp", SqlTypeFamily.Binary)]
    [InlineData("sql_variant", SqlTypeFamily.Other)]
    [InlineData("hierarchyid", SqlTypeFamily.Other)]
    public void Family_Classifies_AdditionalTypes(string input, SqlTypeFamily expected)
        => Assert.Equal(expected, SqlDataType.Parse(input).Family);

    [Theory]
    [InlineData("decimal(abc, 2)")]    // non-numeric precision
    [InlineData("decimal(10, xyz)")]   // non-numeric scale
    [InlineData("varchar(50")]         // missing close paren before another open is malformed by order
    [InlineData("   ")]                // whitespace-only collapses to empty
    public void Parse_Malformed_AdditionalCases_Throw(string input)
        => Assert.Throws<SqlFlowException>(() => SqlDataType.Parse(input));

    [Fact]
    public void Parse_RowversionSynonym_NormalizesToTimestamp()
    {
        var parsed = SqlDataType.Parse("rowversion");
        Assert.Equal("timestamp", parsed.BaseType);
        Assert.Equal(SqlTypeFamily.Binary, parsed.Family);
    }

    // ----------------------------------------------------------------------------------------------------
    // SqlTypeResolution: widening corners not covered by the existing suite.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("tinyint", "smallint", "smallint")] // adjacent integer promotion
    [InlineData("tinyint", "bigint", "bigint")]     // skip-rank promotion
    [InlineData("smallint", "bigint", "bigint")]
    public void Integer_Widening_PromotesToWiderRank(string existing, string desired, string expected)
    {
        var result = EdgeResolve(existing, desired);
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal(expected, result.Merged!.Render());
    }

    [Fact]
    public void Text_SysnameWidenedWithShorterVarchar_StaysUnicodeLength128()
    {
        // sysname behaves as nvarchar(128); merging with varchar(50) keeps unicode and the larger length.
        var result = EdgeResolve("sysname", "varchar(50)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("nvarchar(128)", result.Merged!.Render());
    }

    [Fact]
    public void Text_NtextWidenedWithChar_BecomesUnicodeMax()
    {
        // ntext is unicode, variable, and huge, so it dominates a fixed char into nvarchar(max).
        var result = EdgeResolve("ntext", "char(10)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("nvarchar(max)", result.Merged!.Render());
    }

    [Fact]
    public void Text_CharWidenedToLongerVarchar_BecomesVariable()
    {
        // Merging a fixed char with a longer varchar yields the variable form at the larger length.
        var result = EdgeResolve("char(5)", "varchar(20)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("varchar(20)", result.Merged!.Render());
    }

    [Fact]
    public void Binary_FixedWidenedToHugeImage_BecomesVarbinaryMax()
    {
        var result = EdgeResolve("binary(8)", "image");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("varbinary(max)", result.Merged!.Render());
    }

    [Fact]
    public void Approximate_RealWidenedToFloat_Alters()
    {
        var result = EdgeResolve("real", "float");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("float", result.Merged!.Render());
    }

    [Fact]
    public void Money_SmallmoneyWidenedToMoney_Alters()
    {
        var result = EdgeResolve("smallmoney", "money");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("money", result.Merged!.Render());
    }

    [Fact]
    public void DateTime_DatetimeoffsetScaleGrows_Alters()
    {
        var result = EdgeResolve("datetimeoffset(2)", "datetimeoffset(7)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("datetimeoffset(7)", result.Merged!.Render());
    }

    [Fact]
    public void DateTime_TimeScaleAlreadyFiner_IsKept()
    {
        // Monotonic widening keeps the existing scale when the incoming one is coarser.
        var result = EdgeResolve("time(7)", "time(3)");
        Assert.Equal(SchemaChangeAction.Keep, result.Action);
    }

    [Fact]
    public void Decimal_WidensScaleWhileKeepingIntegerHeadroom()
    {
        // Integer headroom comes from existing (12-2=10); scale grows to 5, so precision becomes 15.
        var result = EdgeResolve("decimal(12,2)", "decimal(8,5)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("decimal(15, 5)", result.Merged!.Render());
    }

    [Fact]
    public void Decimal_WideningClampsCombinedPrecisionTo38()
    {
        // 30 integer digits plus a scale of 10 would be 40; precision is capped at the SQL Server max of 38.
        var result = EdgeResolve("decimal(30,0)", "decimal(20,10)");
        Assert.Equal(SchemaChangeAction.Alter, result.Action);
        Assert.Equal("decimal(38, 10)", result.Merged!.Render());
    }

    [Theory]
    [InlineData("bit", "int")]                  // bit vs integer crosses families
    [InlineData("uniqueidentifier", "varchar(36)")] // guid vs text
    [InlineData("xml", "sql_variant")]          // two distinct Other types cannot merge
    [InlineData("money", "decimal(18,2)")]      // money vs decimal are separate families
    [InlineData("float", "decimal(18,2)")]      // approximate vs exact decimal
    [InlineData("datetime2(7)", "datetimeoffset(7)")] // differing datetime bases
    public void CrossFamilyOrUnmergeable_AdditionalCases_AreIncompatible(string existing, string desired)
    {
        var result = EdgeResolve(existing, desired);
        Assert.Equal(SchemaChangeAction.Incompatible, result.Action);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }

    [Fact]
    public void Resolve_CaseInsensitiveSameType_IsKept()
    {
        // Parsing lowercases the base type, so case-only differences resolve to Keep, not Alter.
        var result = EdgeResolve("NVARCHAR(50)", "nvarchar(50)");
        Assert.Equal(SchemaChangeAction.Keep, result.Action);
    }

    // ----------------------------------------------------------------------------------------------------
    // SqlServerTypeMapper: CLR-to-SQL mapping boundaries.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(typeof(byte), "TINYINT")]
    [InlineData(typeof(short), "SMALLINT")]
    [InlineData(typeof(float), "REAL")]
    [InlineData(typeof(DateTimeOffset), "DATETIMEOFFSET")]
    [InlineData(typeof(TimeSpan), "TIME")]
    [InlineData(typeof(byte[]), "VARBINARY(MAX)")]
    public void Map_AdditionalClrTypes_MapToSqlTypes(Type clr, string expected)
        => Assert.Equal(expected, EdgeMap(new SourceColumn { Name = "C", Type = clr }).SqlType);

    [Fact]
    public void Map_NullableInt_UnwrapsToInt()
        => Assert.Equal("INT", EdgeMap(new SourceColumn { Name = "C", Type = typeof(int?) }).SqlType);

    [Fact]
    public void Map_NullableDateTime_UnwrapsToDateTime2()
        => Assert.Equal("DATETIME2", EdgeMap(new SourceColumn { Name = "C", Type = typeof(DateTime?) }).SqlType);

    [Fact]
    public void Map_UnknownClrType_FallsBackToNvarcharMax()
        => Assert.Equal("NVARCHAR(MAX)", EdgeMap(new SourceColumn { Name = "C", Type = typeof(object) }).SqlType);

    [Theory]
    [InlineData(4000, "NVARCHAR(4000)")]   // exact inferred-length ceiling
    [InlineData(4001, "NVARCHAR(MAX)")]    // one past the ceiling spills to MAX
    [InlineData(1, "NVARCHAR(1)")]         // minimal positive length
    public void Map_StringLengthBoundaries(int maxLength, string expected)
        => Assert.Equal(
            expected,
            EdgeMap(new SourceColumn { Name = "C", Type = typeof(string), MaxLength = maxLength }).SqlType);

    [Fact]
    public void Map_StringZeroLength_FallsBackToDefaultColumnType()
    {
        // A zero MaxLength is not a usable inferred length, so the configured default applies.
        var column = new SourceColumn { Name = "C", Type = typeof(string), MaxLength = 0 };
        Assert.Equal("varchar(255)", EdgeMapper.Map(column, columnOverride: null, "varchar(255)").SqlType);
    }

    [Theory]
    [InlineData(10, 4, "DECIMAL(10, 4)")]  // both precision and scale provided
    [InlineData(null, 2, "DECIMAL(38, 2)")]// missing precision defaults to 38
    [InlineData(12, null, "DECIMAL(12, 6)")]// missing scale defaults to 6
    [InlineData(null, null, "DECIMAL(38, 6)")] // both default
    [InlineData(9, 0, "DECIMAL(9, 0)")]    // scale of zero is honored, not treated as missing
    public void Map_Decimal_AppliesPrecisionAndScaleDefaults(int? precision, int? scale, string expected)
    {
        var column = new SourceColumn { Name = "C", Type = typeof(decimal), Precision = precision, Scale = scale };
        Assert.Equal(expected, EdgeMap(column).SqlType);
    }

    [Fact]
    public void Map_DeclaredSqlType_WinsOverClrInference()
    {
        // The middle precedence rung: an explicit source SqlType beats CLR mapping when no override is set.
        var column = new SourceColumn { Name = "HashKey", Type = typeof(string), SqlType = "varbinary(64)" };
        Assert.Equal("varbinary(64)", EdgeMap(column).SqlType);
    }

    [Fact]
    public void Map_Override_WinsOverDeclaredSqlType()
    {
        // The top precedence rung: an author override beats even an explicit source SqlType.
        var column = new SourceColumn { Name = "K", Type = typeof(string), SqlType = "varbinary(64)" };
        var mapped = EdgeMapper.Map(column, new ColumnOverride { Type = "BIGINT" }, "varchar(255)");
        Assert.Equal("BIGINT", mapped.SqlType);
    }

    [Fact]
    public void Map_Override_BlankType_FallsThroughToDeclaredSqlType()
    {
        // A whitespace-only override type is not a real override and must not win.
        var column = new SourceColumn { Name = "K", Type = typeof(string), SqlType = "varbinary(64)" };
        var mapped = EdgeMapper.Map(column, new ColumnOverride { Type = "   " }, "varchar(255)");
        Assert.Equal("varbinary(64)", mapped.SqlType);
    }

    [Fact]
    public void Map_Override_TypeTrimmed()
    {
        // Override types are trimmed of surrounding whitespace.
        var column = new SourceColumn { Name = "K", Type = typeof(string) };
        var mapped = EdgeMapper.Map(column, new ColumnOverride { Type = "  BIGINT  " }, "varchar(255)");
        Assert.Equal("BIGINT", mapped.SqlType);
    }

    [Fact]
    public void Map_NullableFromSourceColumn_IsHonoredWhenNoOverride()
    {
        var column = new SourceColumn { Name = "C", Type = typeof(int), IsNullable = false };
        Assert.False(EdgeMap(column).IsNullable);
    }

    // ----------------------------------------------------------------------------------------------------
    // UnicodeConverter: additional pass-through and conversion corners.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("nvarchar(128)", "varchar(128)")] // length preserved through conversion
    [InlineData("nchar(20)", "char(20)")]
    [InlineData("text", "text")]                  // already non-unicode passes through
    [InlineData("char(8)", "char(8)")]
    [InlineData("binary(16)", "binary(16)")]      // non-text passes through unchanged
    [InlineData("decimal(18,2)", "decimal(18, 2)")]
    public void ToNonUnicode_AdditionalCases(string input, string expected)
        => Assert.Equal(expected, UnicodeConverter.ToNonUnicode(SqlDataType.Parse(input)).Render());

    [Fact]
    public void ToNonUnicode_NvarcharMax_StaysMax()
    {
        var converted = UnicodeConverter.ToNonUnicode(SqlDataType.Parse("nvarchar(max)"));
        Assert.Equal("varchar", converted.BaseType);
        Assert.True(converted.IsMax);
    }

    // ----------------------------------------------------------------------------------------------------
    // Suspected production defect (asserts the CORRECT behavior): an implicit-scale datetime2 target that
    // already accommodates a finer incoming scale should be Kept, not re-altered to an equivalent scale.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void DateTime_ImplicitScaleTargetAccommodatingFinerIncoming_ShouldKeep()
    {
        // Existing target "datetime2" (implicitly scale 7) already accommodates incoming "datetime2(3)";
        // monotonic widening should require no DDL. Production currently returns Alter(datetime2(7)),
        // an equivalent no-op alter, because the widen step does not short-circuit a compatible scale.
        var result = EdgeResolve("datetime2", "datetime2(3)");
        Assert.Equal(SchemaChangeAction.Keep, result.Action);
    }
}
