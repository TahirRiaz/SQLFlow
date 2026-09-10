using SqlFlow.Core.Model;
using SqlFlow.SqlServer;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The pure half of the SQL Server locale provider: turning an LCID and a session date_format into a
/// <see cref="ServerLocale"/>. The query itself is exercised by the integration suite.
/// </summary>
public sealed class SqlServerLocaleProviderTests
{
    [Fact]
    public void MapLocale_Norwegian_IsDmyWithCommaDecimal()
    {
        var locale = SqlServerLocaleProvider.MapLocale(1044, "dmy"); // 1044 = nb-NO

        Assert.Equal("nb-NO", locale.Culture);
        Assert.Equal(DateOrder.Dmy, locale.DateOrder);
        Assert.Equal(',', locale.DecimalSeparator);
        Assert.Equal('.', locale.GroupSeparator);
    }

    [Fact]
    public void MapLocale_American_IsMdyWithDotDecimal()
    {
        var locale = SqlServerLocaleProvider.MapLocale(1033, "mdy"); // 1033 = en-US

        Assert.Equal("en-US", locale.Culture);
        Assert.Equal(DateOrder.Mdy, locale.DateOrder);
        Assert.Equal('.', locale.DecimalSeparator);
        Assert.Equal(',', locale.GroupSeparator);
    }

    [Fact]
    public void MapLocale_DateFormatDrivesDateOrder_IndependentOfCulture()
    {
        // The session date_format is authoritative for ambiguous-date interpretation, even if it differs
        // from the culture's own default ordering.
        Assert.Equal(DateOrder.Ymd, SqlServerLocaleProvider.MapLocale(1033, "ymd").DateOrder);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(127)] // invariant LCID
    [InlineData(0)]
    [InlineData(999999)] // not a real LCID
    public void MapLocale_MissingOrUnknownLcid_FallsBackToInvariantNumbers(int? lcid)
    {
        var locale = SqlServerLocaleProvider.MapLocale(lcid, "dmy");

        Assert.Equal("invariant", locale.Culture);
        Assert.Equal('.', locale.DecimalSeparator);
        Assert.Equal(DateOrder.Dmy, locale.DateOrder); // date order still honored from date_format
    }

    [Fact]
    public void MapLocale_NullDateFormat_DefaultsToYmd()
        => Assert.Equal(DateOrder.Ymd, SqlServerLocaleProvider.MapLocale(1033, null).DateOrder);
}
