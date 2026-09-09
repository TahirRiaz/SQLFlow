using System.Globalization;
using SqlFlow.Core;
using SqlFlow.Core.Model;
using Xunit;

namespace SqlFlow.Tests.Incremental;

/// <summary>
/// The watermark predicate primitives that both pushdown and the client-side filter share: canonical
/// serialization round-trips, the DuckDB literal is injection-safe per kind, and the cell comparison coerces a
/// source value (typed or string) to the bound's kind so a key-based and a date/numeric watermark all compare
/// correctly. These are the rules the engine, DuckDB pushdown, and the Parquet pruning all rely on.
/// </summary>
public sealed class WatermarkPredicateTests
{
    [Fact]
    public void Format_Integer_IsInvariant()
        => Assert.Equal("42", WatermarkPredicate.Format(new WatermarkValue { Kind = WatermarkKind.Whole, Value = 42L }));

    [Fact]
    public void Format_Decimal_IsInvariant()
        => Assert.Equal("12.50", WatermarkPredicate.Format(new WatermarkValue { Kind = WatermarkKind.Fixed, Value = 12.50m }));

    [Fact]
    public void Format_DateTime_IsRoundTrippable()
        => Assert.Equal("2026-01-02T03:04:05.0000000",
            WatermarkPredicate.Format(new WatermarkValue { Kind = WatermarkKind.DateTime, Value = new DateTime(2026, 1, 2, 3, 4, 5) }));

    [Fact]
    public void Format_Text_IsTheValue()
        => Assert.Equal("v9", WatermarkPredicate.Format(new WatermarkValue { Kind = WatermarkKind.Text, Value = "v9" }));

    [Theory]
    [InlineData("whole", WatermarkKind.Whole)]
    [InlineData("DateTime", WatermarkKind.DateTime)]
    [InlineData("TEXT", WatermarkKind.Text)]
    public void ParseKind_IsCaseInsensitive(string name, WatermarkKind expected)
        => Assert.Equal(expected, WatermarkPredicate.ParseKind(name));

    [Fact]
    public void ParseKind_Unknown_Throws()
        => Assert.Throws<SqlFlowException>(() => WatermarkPredicate.ParseKind("bogus"));

    [Fact]
    public void DuckDbPredicate_Integer_IsBareLiteral()
        => Assert.Equal("\"Id\" > 5", WatermarkPredicate.DuckDbPredicate("Id", "5", WatermarkKind.Whole));

    [Fact]
    public void DuckDbPredicate_DateTime_UsesTimestampLiteral()
        => Assert.Equal("\"LoadDate\" > TIMESTAMP '2026-01-02 03:04:05.0000000'",
            WatermarkPredicate.DuckDbPredicate("LoadDate", "2026-01-02T03:04:05.0000000", WatermarkKind.DateTime));

    [Fact]
    public void DuckDbPredicate_Text_QuotesAndEscapes()
        => Assert.Equal("\"v\" > 'o''brien'", WatermarkPredicate.DuckDbPredicate("v", "o'brien", WatermarkKind.Text));

    [Fact]
    public void DuckDbPredicate_ColumnName_IsQuoteEscaped()
        => Assert.Equal("\"we\"\"ird\" > 1", WatermarkPredicate.DuckDbPredicate("we\"ird", "1", WatermarkKind.Whole));

    [Theory]
    [InlineData(6, true)]   // typed int past the bound
    [InlineData(5, false)]  // equal is not strictly past
    [InlineData(4, false)]  // below
    public void IsAfter_Integer_TypedCell(int cell, bool expected)
        => Assert.Equal(expected, WatermarkPredicate.IsAfter(cell, "5", WatermarkKind.Whole));

    [Fact]
    public void IsAfter_Integer_StringCell_IsCoerced()
    {
        // A string-typed reader (CSV) presents "10"; the Integer kind coerces it, so it is not compared as text.
        Assert.True(WatermarkPredicate.IsAfter("10", "9", WatermarkKind.Whole));
        Assert.False(WatermarkPredicate.IsAfter("9", "10", WatermarkKind.Whole));
    }

    [Fact]
    public void IsAfter_DateTime_AcceptsDateTimeAndStringCells()
    {
        Assert.True(WatermarkPredicate.IsAfter(new DateTime(2026, 2, 1), "2026-01-15T00:00:00.0000000", WatermarkKind.DateTime));
        Assert.False(WatermarkPredicate.IsAfter("2026-01-01T00:00:00.0000000", "2026-01-15T00:00:00.0000000", WatermarkKind.DateTime));
    }

    [Fact]
    public void IsAfter_DateTimeOffset_ComparesAcrossOffsets()
    {
        var bound = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero).ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture);
        // 14:00+01:00 == 13:00Z, which is past 12:00Z.
        Assert.True(WatermarkPredicate.IsAfter(new DateTimeOffset(2026, 1, 1, 14, 0, 0, TimeSpan.FromHours(1)), bound, WatermarkKind.DateTimeOffset));
        // 12:30+01:00 == 11:30Z, which is before 12:00Z.
        Assert.False(WatermarkPredicate.IsAfter(new DateTimeOffset(2026, 1, 1, 12, 30, 0, TimeSpan.FromHours(1)), bound, WatermarkKind.DateTimeOffset));
    }

    [Fact]
    public void IsAfter_Text_IsOrdinal()
    {
        Assert.True(WatermarkPredicate.IsAfter("b", "a", WatermarkKind.Text));
        Assert.False(WatermarkPredicate.IsAfter("a", "a", WatermarkKind.Text));
    }

    [Fact]
    public void IsAfter_NullOrDbNull_IsNeverPastTheBound()
    {
        Assert.False(WatermarkPredicate.IsAfter(null, "0", WatermarkKind.Whole));
        Assert.False(WatermarkPredicate.IsAfter(DBNull.Value, "0", WatermarkKind.Whole));
    }
}
