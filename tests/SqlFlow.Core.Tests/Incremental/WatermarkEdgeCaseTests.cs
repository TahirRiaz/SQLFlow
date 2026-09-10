using System.Data;
using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Engine;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Incremental;

/// <summary>
/// Additional, non-overlapping edge cases for the watermark primitives (<see cref="WatermarkPredicate"/>) and the
/// reader-agnostic <see cref="WatermarkFilteringDataReader"/>. These press on the boundaries the happy-path tests
/// skip: the Floating / Fixed / DateTimeOffset kinds, the strict ">" boundary across every kind, argument
/// validation at the trust boundary, DuckDB literal escaping and the T-to-space rewrite, numeric coercion of a
/// typed cell (including banker's rounding of a decimal into the Whole kind), and round-trip consistency so the
/// serialized bound and the comparison can never drift apart. Everything is pure in-memory and deterministic:
/// fixed inputs only, no clock, no randomness, no database.
/// </summary>
public sealed class WatermarkEdgeCaseTests
{
    // ---- Format: kinds and boundaries the happy path does not cover -------------------------------------

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(-2.25, "-2.25")]
    [InlineData(0.0, "0")]
    [InlineData(100.0, "100")]
    [InlineData(0.1, "0.1")]
    public void Format_Floating_UsesRoundTripFormat(double value, string expected)
        => Assert.Equal(expected, WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Floating, value)));

    [Fact]
    public void Format_Floating_AcceptsIntegerBoxedValue()
    {
        // The Value is declared as object; an int boxed under the Floating kind must coerce through Convert.ToDouble.
        Assert.Equal("7", WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Floating, 7)));
    }

    [Fact]
    public void Format_Whole_AcceptsBoxedIntAndCoercesToInt64()
        => Assert.Equal("7", WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Whole, 7)));

    [Theory]
    [InlineData(long.MaxValue, "9223372036854775807")]
    [InlineData(long.MinValue, "-9223372036854775808")]
    [InlineData(-1L, "-1")]
    public void Format_Whole_SerializesExtremeLongs(long value, string expected)
        => Assert.Equal(expected, WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Whole, value)));

    [Fact]
    public void Format_Fixed_PreservesNegativeAndTrailingScale()
        => Assert.Equal("-3.000", WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Fixed, -3.000m)));

    [Fact]
    public void Format_Fixed_Zero_IsBareZero()
        => Assert.Equal("0", WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Fixed, 0m)));

    [Fact]
    public void Format_DateTimeOffset_RoundTripsTheOffset()
        => Assert.Equal("2026-01-02T03:04:05.0000000+02:00",
            WatermarkPredicate.Format(WholeFreeValue(
                WatermarkKind.DateTimeOffset, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2)))));

    [Fact]
    public void Format_Text_EmptyString_IsEmpty()
        => Assert.Equal(string.Empty, WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Text, string.Empty)));

    [Fact]
    public void Format_NullBound_Throws()
        => Assert.Throws<ArgumentNullException>(() => WatermarkPredicate.Format(null!));

    [Fact]
    public void Format_UnsupportedKind_Throws()
        => Assert.Throws<SqlFlowException>(() => WatermarkPredicate.Format(WholeFreeValue((WatermarkKind)999, 0L)));

    // ---- ParseKind: names and null/blank rejection ------------------------------------------------------

    [Theory]
    [InlineData("fixed", WatermarkKind.Fixed)]
    [InlineData("FLOATING", WatermarkKind.Floating)]
    [InlineData("datetimeoffset", WatermarkKind.DateTimeOffset)]
    [InlineData("Whole", WatermarkKind.Whole)]
    public void ParseKind_ResolvesEveryKindCaseInsensitively(string name, WatermarkKind expected)
        => Assert.Equal(expected, WatermarkPredicate.ParseKind(name));

    [Fact]
    public void ParseKind_Null_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => WatermarkPredicate.ParseKind(null!));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseKind_Blank_ThrowsArgument(string name)
        => Assert.Throws<ArgumentException>(() => WatermarkPredicate.ParseKind(name));

    [Fact]
    public void ParseKind_NumericText_ResolvesToUnderlyingEnumValue()
    {
        // Enum.TryParse accepts a numeric string as the underlying value: "1" is the second declared kind (Fixed).
        // This documents the real behavior so a caller is not surprised that a digit string is not rejected.
        Assert.Equal(WatermarkKind.Fixed, WatermarkPredicate.ParseKind("1"));
    }

    // ---- DuckDbPredicate: literal rendering, escaping, validation ---------------------------------------

    [Fact]
    public void DuckDbPredicate_Fixed_IsBareLiteral()
        => Assert.Equal("\"Amount\" > 12.50", WatermarkPredicate.DuckDbPredicate("Amount", "12.50", WatermarkKind.Fixed));

    [Fact]
    public void DuckDbPredicate_Floating_IsBareLiteral()
        => Assert.Equal("\"Score\" > 1.5", WatermarkPredicate.DuckDbPredicate("Score", "1.5", WatermarkKind.Floating));

    [Fact]
    public void DuckDbPredicate_DateTimeOffset_UsesTimestampTzAndRewritesT()
    {
        var sql = WatermarkPredicate.DuckDbPredicate(
            "Ts", "2026-01-02T03:04:05.0000000+02:00", WatermarkKind.DateTimeOffset);

        Assert.Equal("\"Ts\" > TIMESTAMPTZ '2026-01-02 03:04:05.0000000+02:00'", sql);
    }

    [Fact]
    public void DuckDbPredicate_DateTime_RewritesTheDateTimeSeparatorToSpace()
    {
        // The canonical bound uses 'T' between date and time; the DuckDB literal must use a space so it parses as a
        // timestamp. The exact match proves the 'T' inside the quoted literal became a space.
        var sql = WatermarkPredicate.DuckDbPredicate("d", "2026-01-02T03:04:05.0000000", WatermarkKind.DateTime);

        Assert.Equal("\"d\" > TIMESTAMP '2026-01-02 03:04:05.0000000'", sql);
    }

    [Fact]
    public void DuckDbPredicate_Text_EscapesEveryQuote()
        => Assert.Equal("\"v\" > 'a''b''c'", WatermarkPredicate.DuckDbPredicate("v", "a'b'c", WatermarkKind.Text));

    [Fact]
    public void DuckDbPredicate_Text_EmptyValue_IsEmptyQuotedLiteral()
        => Assert.Equal("\"v\" > ''", WatermarkPredicate.DuckDbPredicate("v", string.Empty, WatermarkKind.Text));

    [Fact]
    public void DuckDbPredicate_NullColumn_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => WatermarkPredicate.DuckDbPredicate(null!, "1", WatermarkKind.Whole));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void DuckDbPredicate_BlankColumn_ThrowsArgument(string column)
        => Assert.Throws<ArgumentException>(() => WatermarkPredicate.DuckDbPredicate(column, "1", WatermarkKind.Whole));

    [Fact]
    public void DuckDbPredicate_UnsupportedKind_Throws()
        => Assert.Throws<SqlFlowException>(() => WatermarkPredicate.DuckDbPredicate("c", "1", (WatermarkKind)999));

    // ---- IsAfter: strict boundary across every kind -----------------------------------------------------

    [Theory]
    [InlineData("13.00", true)]   // strictly above the bound
    [InlineData("12.50", false)]  // exactly the bound is not "after"
    [InlineData("12.49", false)]  // below
    public void IsAfter_Fixed_BoundaryIsStrict(string cell, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, "12.50", WatermarkKind.Fixed));

    [Fact]
    public void IsAfter_Fixed_TypedDecimalCell_IsCompared()
    {
        Assert.True(WatermarkPredicate.IsAfter(12.51m, "12.50", WatermarkKind.Fixed));
        Assert.False(WatermarkPredicate.IsAfter(12.50m, "12.50", WatermarkKind.Fixed));
    }

    [Theory]
    [InlineData(1.51, true)]
    [InlineData(1.5, false)]
    [InlineData(1.49, false)]
    [InlineData(-1.0, false)]
    public void IsAfter_Floating_BoundaryIsStrict(double cell, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, "1.5", WatermarkKind.Floating));

    [Fact]
    public void IsAfter_Floating_NegativeBound_AcceptsHigherNegative()
    {
        Assert.True(WatermarkPredicate.IsAfter(-1.0, "-2.5", WatermarkKind.Floating));
        Assert.False(WatermarkPredicate.IsAfter(-3.0, "-2.5", WatermarkKind.Floating));
    }

    [Fact]
    public void IsAfter_Floating_StringCell_IsCoerced()
    {
        Assert.True(WatermarkPredicate.IsAfter("2.0", "1.5", WatermarkKind.Floating));
        Assert.False(WatermarkPredicate.IsAfter("1.0", "1.5", WatermarkKind.Floating));
    }

    [Theory]
    [InlineData(long.MinValue, "0", false)]
    [InlineData(long.MaxValue, "0", true)]
    [InlineData(-1, "-1", false)]
    [InlineData(0, "-1", true)]
    public void IsAfter_Whole_HandlesNegativesAndExtremes(long cell, string bound, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, bound, WatermarkKind.Whole));

    [Theory]
    [InlineData((short)6, true)]
    [InlineData((short)5, false)]
    public void IsAfter_Whole_CoercesNarrowerIntegerCellTypes(short cell, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, "5", WatermarkKind.Whole));

    [Fact]
    public void IsAfter_Whole_DecimalCell_UsesBankersRounding()
    {
        // Convert.ToInt64 rounds half to even: 2.5m -> 2 (not after 2), 3.5m -> 4 (after 3). This documents the
        // real coercion behavior so a fractional cell under a Whole watermark is not mistaken for an exact compare.
        Assert.False(WatermarkPredicate.IsAfter(2.5m, "2", WatermarkKind.Whole));
        Assert.True(WatermarkPredicate.IsAfter(3.5m, "3", WatermarkKind.Whole));
    }

    [Fact]
    public void IsAfter_DateTime_ExactBound_IsNotAfter()
    {
        const string bound = "2026-01-15T00:00:00.0000000";
        Assert.False(WatermarkPredicate.IsAfter(new DateTime(2026, 1, 15), bound, WatermarkKind.DateTime));
        Assert.True(WatermarkPredicate.IsAfter(new DateTime(2026, 1, 15, 0, 0, 1), bound, WatermarkKind.DateTime));
    }

    [Fact]
    public void IsAfter_DateTimeOffset_NaiveDateTimeCell_IsTreatedAsUtc()
    {
        // A DateTime cell (no offset) is read as UTC for the comparison. 13:00 (as UTC) is past the 12:00Z bound;
        // 11:00 (as UTC) is before it.
        var bound = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

        Assert.True(WatermarkPredicate.IsAfter(new DateTime(2026, 1, 1, 13, 0, 0), bound, WatermarkKind.DateTimeOffset));
        Assert.False(WatermarkPredicate.IsAfter(new DateTime(2026, 1, 1, 11, 0, 0), bound, WatermarkKind.DateTimeOffset));
    }

    [Fact]
    public void IsAfter_DateTimeOffset_StringCell_IsParsedWithOffset()
    {
        var bound = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);

        // 14:00+01:00 == 13:00Z (after); 12:30+01:00 == 11:30Z (before).
        Assert.True(WatermarkPredicate.IsAfter("2026-01-01T14:00:00.0000000+01:00", bound, WatermarkKind.DateTimeOffset));
        Assert.False(WatermarkPredicate.IsAfter("2026-01-01T12:30:00.0000000+01:00", bound, WatermarkKind.DateTimeOffset));
    }

    [Theory]
    [InlineData("apple", "apple", false)] // equal is not after
    [InlineData("apply", "apple", true)]  // later ordinally
    [InlineData("appl", "apple", false)]  // shorter prefix sorts before
    [InlineData("apple", "appl", true)]   // longer extends past
    public void IsAfter_Text_OrdinalBoundary(string cell, string bound, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, bound, WatermarkKind.Text));

    [Fact]
    public void IsAfter_Text_IsOrdinalNotCultureAware()
    {
        // Ordinal comparison: uppercase 'Z' (90) sorts before lowercase 'a' (97), so "Z" is not after "a".
        Assert.False(WatermarkPredicate.IsAfter("Z", "a", WatermarkKind.Text));
        Assert.True(WatermarkPredicate.IsAfter("a", "Z", WatermarkKind.Text));
    }

    [Fact]
    public void IsAfter_Text_EmptyCell_IsAfterOnlyWhenBoundIsAlsoEmptyIsFalse()
    {
        // An empty cell is below any non-empty bound and equal to an empty bound: never strictly after.
        Assert.False(WatermarkPredicate.IsAfter(string.Empty, "a", WatermarkKind.Text));
        Assert.False(WatermarkPredicate.IsAfter(string.Empty, string.Empty, WatermarkKind.Text));
        Assert.True(WatermarkPredicate.IsAfter("a", string.Empty, WatermarkKind.Text));
    }

    [Theory]
    [InlineData(WatermarkKind.Fixed)]
    [InlineData(WatermarkKind.Floating)]
    [InlineData(WatermarkKind.DateTime)]
    [InlineData(WatermarkKind.DateTimeOffset)]
    [InlineData(WatermarkKind.Text)]
    public void IsAfter_NullCell_IsNeverAfter_ForEveryKind(WatermarkKind kind)
    {
        // The null guard runs before any coercion, so the (otherwise unparseable for some kinds) bound is irrelevant.
        Assert.False(WatermarkPredicate.IsAfter(null, "0", kind));
        Assert.False(WatermarkPredicate.IsAfter(DBNull.Value, "0", kind));
    }

    [Fact]
    public void IsAfter_UnsupportedKind_Throws()
        => Assert.Throws<SqlFlowException>(() => WatermarkPredicate.IsAfter(1L, "0", (WatermarkKind)999));

    // ---- Round-trip consistency: Format then compare ----------------------------------------------------

    [Fact]
    public void RoundTrip_Whole_FormatThenIsAfter_AgreesOnTheBoundary()
    {
        var canonical = WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Whole, 5L));
        Assert.False(WatermarkPredicate.IsAfter(5L, canonical, WatermarkKind.Whole));
        Assert.True(WatermarkPredicate.IsAfter(6L, canonical, WatermarkKind.Whole));
    }

    [Fact]
    public void RoundTrip_Floating_FormatThenIsAfter_AgreesOnTheBoundary()
    {
        var canonical = WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Floating, 0.1));
        Assert.False(WatermarkPredicate.IsAfter(0.1, canonical, WatermarkKind.Floating));
        Assert.True(WatermarkPredicate.IsAfter(0.2, canonical, WatermarkKind.Floating));
    }

    [Fact]
    public void RoundTrip_DateTimeOffset_FormatThenIsAfter_AgreesAcrossOffsets()
    {
        var bound = new DateTimeOffset(2026, 6, 1, 10, 0, 0, TimeSpan.FromHours(2));
        var canonical = WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.DateTimeOffset, bound));

        // The same instant expressed in UTC equals the bound: not strictly after.
        Assert.False(WatermarkPredicate.IsAfter(bound.ToUniversalTime(), canonical, WatermarkKind.DateTimeOffset));
        Assert.True(WatermarkPredicate.IsAfter(bound.AddTicks(1), canonical, WatermarkKind.DateTimeOffset));
    }

    [Fact]
    public void RoundTrip_Text_FormatThenDuckDbPredicate_PreservesEscaping()
    {
        var canonical = WatermarkPredicate.Format(WholeFreeValue(WatermarkKind.Text, "o'brien"));
        Assert.Equal("\"name\" > 'o''brien'", WatermarkPredicate.DuckDbPredicate("name", canonical, WatermarkKind.Text));
    }

    // ---- WatermarkFilteringDataReader: validation and non-Whole kinds end to end ------------------------

    [Fact]
    public void Reader_NullInner_ThrowsArgumentNull()
        => Assert.Throws<ArgumentNullException>(() => new WatermarkFilteringDataReader(null!, "Id", "0", WatermarkKind.Whole));

    [Fact]
    public void Reader_NullColumn_ThrowsArgumentNull()
    {
        using var inner = SingleIntColumnReader("Id", 1);
        Assert.Throws<ArgumentNullException>(() => new WatermarkFilteringDataReader(inner, null!, "0", WatermarkKind.Whole));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reader_BlankColumn_ThrowsArgument(string column)
    {
        using var inner = SingleIntColumnReader("Id", 1);
        Assert.Throws<ArgumentException>(() => new WatermarkFilteringDataReader(inner, column, "0", WatermarkKind.Whole));
    }

    [Fact]
    public void Reader_NullCanonicalValue_ThrowsArgumentNull()
    {
        using var inner = SingleIntColumnReader("Id", 1);
        Assert.Throws<ArgumentNullException>(() => new WatermarkFilteringDataReader(inner, "Id", null!, WatermarkKind.Whole));
    }

    [Fact]
    public void Reader_DecimalKind_FiltersOnFixedBound()
    {
        var table = new DataTable();
        table.Columns.Add("Amount", typeof(decimal));
        table.Rows.Add(10.00m);
        table.Rows.Add(12.50m);
        table.Rows.Add(12.51m);
        table.Rows.Add(99.99m);

        using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Amount", "12.50", WatermarkKind.Fixed);
        var ordinal = filtered.GetOrdinal("Amount");

        var kept = new List<decimal>();
        while (filtered.Read())
        {
            kept.Add(filtered.GetDecimal(ordinal));
        }

        Assert.Equal([12.51m, 99.99m], kept);
    }

    [Fact]
    public void Reader_TextKind_FiltersOrdinally()
    {
        var table = new DataTable();
        table.Columns.Add("Version", typeof(string));
        foreach (var v in new[] { "v1", "v10", "v2", "v9" })
        {
            table.Rows.Add(v);
        }

        using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Version", "v2", WatermarkKind.Text);
        var ordinal = filtered.GetOrdinal("Version");

        var kept = new List<string>();
        while (filtered.Read())
        {
            kept.Add(filtered.GetString(ordinal));
        }

        // Ordinal text: only "v9" is strictly greater than "v2" ("v1"/"v10" sort below it, "v2" equals the bound).
        Assert.Equal(["v9"], kept);
    }

    [Fact]
    public void Reader_DateTimeKind_FiltersStrictlyPastTheBound()
    {
        var table = new DataTable();
        table.Columns.Add("LoadDate", typeof(DateTime));
        table.Rows.Add(new DateTime(2026, 1, 1));
        table.Rows.Add(new DateTime(2026, 1, 15)); // exactly the bound
        table.Rows.Add(new DateTime(2026, 2, 1));

        using var filtered = new WatermarkFilteringDataReader(
            table.CreateDataReader(), "LoadDate", "2026-01-15T00:00:00.0000000", WatermarkKind.DateTime);
        var ordinal = filtered.GetOrdinal("LoadDate");

        var kept = new List<DateTime>();
        while (filtered.Read())
        {
            kept.Add(filtered.GetDateTime(ordinal));
        }

        Assert.Equal([new DateTime(2026, 2, 1)], kept);
    }

    [Fact]
    public async Task Reader_StringTypedWholeColumn_CoercesAndFilters()
    {
        // A string-typed reader (CSV-like) carrying integers: the Whole kind coerces each cell, so the rows compare
        // numerically (10 > 9) rather than as text (where "10" sorts before "9").
        var table = new DataTable();
        table.Columns.Add("Id", typeof(string));
        foreach (var s in new[] { "8", "9", "10", "100" })
        {
            table.Rows.Add(s);
        }

        await using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Id", "9", WatermarkKind.Whole);
        var ordinal = filtered.GetOrdinal("Id");

        var kept = new List<string>();
        while (await filtered.ReadAsync())
        {
            kept.Add(filtered.GetString(ordinal));
        }

        Assert.Equal(["10", "100"], kept);
    }

    [Fact]
    public void Reader_AllRowsPast_KeepsEveryRow()
    {
        using var filtered = new WatermarkFilteringDataReader(SingleIntColumnReader("Id", 5, 6, 7), "Id", "0", WatermarkKind.Whole);
        var ordinal = filtered.GetOrdinal("Id");

        var kept = new List<int>();
        while (filtered.Read())
        {
            kept.Add(filtered.GetInt32(ordinal));
        }

        Assert.Equal([5, 6, 7], kept);
    }

    [Fact]
    public void Reader_EmptySource_ReadReturnsFalseImmediately()
    {
        using var filtered = new WatermarkFilteringDataReader(SingleIntColumnReader("Id"), "Id", "0", WatermarkKind.Whole);
        Assert.False(filtered.Read());
    }

    [Fact]
    public void Reader_PassthroughMetadata_MatchesInner()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add(3, "keep");

        using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Id", "1", WatermarkKind.Whole);

        Assert.Equal(2, filtered.FieldCount);
        Assert.Equal(0, filtered.Depth);
        Assert.False(filtered.IsClosed);
        Assert.Equal("Id", filtered.GetName(0));
        Assert.Equal("Name", filtered.GetName(1));
        Assert.Equal(1, filtered.GetOrdinal("Name"));
        Assert.Equal(typeof(int), filtered.GetFieldType(0));
    }

    [Fact]
    public void Reader_IndexersAndGetValues_ReadCurrentRowThroughDecorator()
    {
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Name", typeof(string));
        table.Rows.Add(2, "below"); // filtered out (== bound)
        table.Rows.Add(5, "kept");

        using var filtered = new WatermarkFilteringDataReader(table.CreateDataReader(), "Id", "2", WatermarkKind.Whole);

        Assert.True(filtered.Read());
        Assert.Equal(5, Convert.ToInt32(filtered[0], CultureInfo.InvariantCulture));
        Assert.Equal("kept", filtered["Name"]);

        var values = new object[filtered.FieldCount];
        var count = filtered.GetValues(values);
        Assert.Equal(2, count);
        Assert.Equal(5, Convert.ToInt32(values[0], CultureInfo.InvariantCulture));
        Assert.Equal("kept", values[1]);
        Assert.False(filtered.Read());
    }

    [Fact]
    public void Reader_NextResult_DelegatesToInnerAndIsFalseForSingleResult()
    {
        using var filtered = new WatermarkFilteringDataReader(SingleIntColumnReader("Id", 1, 2), "Id", "0", WatermarkKind.Whole);
        Assert.False(filtered.NextResult());
    }

    [Fact]
    public async Task Reader_MissingColumn_ThrowsOnSyncReadToo()
    {
        // The async path is covered elsewhere; this asserts the same loud failure on the synchronous Read path,
        // and that the message names both the requested and the available columns.
        using var inner = SingleIntColumnReader("Other", 1, 2);
        using var filtered = new WatermarkFilteringDataReader(inner, "Missing", "0", WatermarkKind.Whole);

        var ex = Assert.Throws<SqlFlowException>(() =>
        {
            while (filtered.Read())
            {
            }
        });

        Assert.Contains("Missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Other", ex.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    // ---- Private, uniquely named helpers ----------------------------------------------------------------

    /// <summary>Builds a <see cref="WatermarkValue"/> with an arbitrary kind/value (the constructor itself does no coercion).</summary>
    private static WatermarkValue WholeFreeValue(WatermarkKind kind, object value)
        => new() { Kind = kind, Value = value };

    /// <summary>A one-int-column reader over a real <see cref="DataTable"/>; nulls are mapped to <see cref="DBNull"/>.</summary>
    private static DataTableReader SingleIntColumnReader(string column, params object?[] ids)
    {
        var table = new DataTable();
        var col = table.Columns.Add(column, typeof(int));
        col.AllowDBNull = true;
        foreach (var id in ids)
        {
            table.Rows.Add(id ?? (object)DBNull.Value);
        }

        return table.CreateDataReader();
    }
}
