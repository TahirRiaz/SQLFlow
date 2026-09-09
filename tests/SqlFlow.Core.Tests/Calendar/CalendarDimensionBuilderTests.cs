using SqlFlow.Core;
using SqlFlow.Core.Calendar;
using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Core.Tests.Calendar;

public sealed class CalendarDimensionBuilderTests
{
    private static CalendarFlow Flow(string from, string to, CalendarObservanceSet set = CalendarObservanceSet.Full, int fiscalStart = 1)
        => new()
        {
            SysAlias = "calendar",
            Server = "target",
            Table = new RelationalObject { Database = "db", Schema = "edw", Name = "Dim_Calendar" },
            From = DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            To = DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            Country = "NO",
            Culture = "nb-NO",
            TimeZone = "Europe/Oslo",
            FiscalYearStartMonth = fiscalStart,
            Observances = set,
        };

    private static CalendarRow Day(IReadOnlyList<CalendarRow> rows, string date)
        => rows.Single(r => r.Date == DateOnly.Parse(date, System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void GeneratesOneRowPerDayInclusiveOfBothEnds()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));

        Assert.Equal(366, rows.Count); // 2024 is a leap year
        Assert.Equal(new DateOnly(2024, 1, 1), rows[0].Date);
        Assert.Equal(new DateOnly(2024, 12, 31), rows[^1].Date);
        Assert.Equal(20240101, rows[0].PeriodId);
        Assert.Equal(20241231, rows[^1].PeriodId);
    }

    [Fact]
    public void EmitsNorwegianNamesWithoutAbbreviationPeriods()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2013-01-01", "2013-01-07"));
        var tuesday = Day(rows, "2013-01-01");

        Assert.Equal("tirsdag", tuesday.DayOfWeekName);
        Assert.Equal("tir", tuesday.DayOfWeekNameShort);
        Assert.Equal("januar", tuesday.MonthName);
        Assert.Equal("jan", tuesday.MonthNameShort);
        Assert.Equal("Q1", tuesday.Quarter);
        Assert.Equal("Vinter", tuesday.Season);

        // The .NET DayOfWeek ordinal, which is what the reporting views re-base from: Sunday is 0.
        Assert.Equal(2, tuesday.DayOfWeekNumber);
        Assert.Equal(0, Day(rows, "2013-01-06").DayOfWeekNumber);
    }

    [Fact]
    public void MonthNumNameIsTheSortableLabelNotADate()
    {
        // The legacy dimension stored '2024-01-01' here, a date string that also embedded the year the
        // generator last ran. The intended value is the zero-padded month joined to its abbreviation.
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-15", "2024-12-15"));

        Assert.Equal("01-jan", Day(rows, "2024-01-15").MonthNumName);
        Assert.Equal("09-sep", Day(rows, "2024-09-15").MonthNumName);
        Assert.Equal("12-des", Day(rows, "2024-12-15").MonthNumName);
    }

    [Fact]
    public void JanuaryFiscalStartMakesFiscalYearTheCalendarYear()
    {
        // The legacy generator returned Year + 1 for every row and 0 for early-January fiscal weeks.
        var rows = CalendarDimensionBuilder.Build(Flow("2013-01-01", "2013-12-31"));

        Assert.All(rows, r => Assert.Equal(2013, r.FiscalYear));
        Assert.Equal(1, Day(rows, "2013-01-01").FiscalMonth);
        Assert.Equal(1, Day(rows, "2013-01-01").FiscalQuarter);
        Assert.Equal(12, Day(rows, "2013-12-31").FiscalMonth);
        Assert.Equal(4, Day(rows, "2013-12-31").FiscalQuarter);
        Assert.All(rows, r => Assert.True(r.FiscalWeekOfYear >= 1));
    }

    [Fact]
    public void NonJanuaryFiscalStartShiftsTheYearAndMonth()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31", fiscalStart: 4));

        // A fiscal year starting in April is labelled by the calendar year it starts in, so January 2024
        // belongs to fiscal 2023 and April 2024 opens fiscal 2024.
        Assert.Equal(2023, Day(rows, "2024-01-15").FiscalYear);
        Assert.Equal(2024, Day(rows, "2024-04-01").FiscalYear);
        Assert.Equal(1, Day(rows, "2024-04-01").FiscalMonth);
        Assert.Equal(10, Day(rows, "2024-01-15").FiscalMonth);
        Assert.Equal(1, Day(rows, "2024-04-01").FiscalWeekOfYear);
    }

    [Fact]
    public void MarksTheStatutoryHolidaysAtTheirComputedDates()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));

        // Easter Sunday 2024 was 31 March; the movable feasts hang off it.
        Assert.Equal("Skjærtorsdag", Day(rows, "2024-03-28").HolidayName);
        Assert.Equal("Langfredag", Day(rows, "2024-03-29").HolidayName);
        Assert.Equal("Andre påskedag", Day(rows, "2024-04-01").HolidayName);
        Assert.Equal("Kristi himmelfartsdag", Day(rows, "2024-05-09").HolidayName);
        Assert.Equal("Første pinsedag", Day(rows, "2024-05-19").HolidayName);
        Assert.Equal("Andre pinsedag", Day(rows, "2024-05-20").HolidayName);
        Assert.Equal("Grunnlovsdagen", Day(rows, "2024-05-17").HolidayName);
        Assert.Equal("Første juledag", Day(rows, "2024-12-25").HolidayName);
    }

    [Fact]
    public void MarksTheNamedObservancesAndMarkersTheLegacyCalendarCarried()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));

        Assert.Equal("Samenes nasjonaldag", Day(rows, "2024-02-06").HolidayName);
        Assert.Equal("Olsok", Day(rows, "2024-07-29").HolidayName);
        Assert.Equal("Halloween", Day(rows, "2024-10-31").HolidayName);
        Assert.Equal("Julaften", Day(rows, "2024-12-24").HolidayName);

        // Nth-weekday rules.
        Assert.Equal("Allehelgensdag", Day(rows, "2024-11-03").HolidayName);
        Assert.Equal("Farsdag", Day(rows, "2024-11-10").HolidayName);
        Assert.Equal("Første søndag i advent", Day(rows, "2024-12-01").HolidayName);
        Assert.Equal("Fjerde søndag i advent", Day(rows, "2024-12-22").HolidayName);

        // Astronomical markers, resolved to the local Oslo date.
        Assert.Equal("Vårjevndøgn", Day(rows, "2024-03-20").HolidayName);
        Assert.Equal("Sommersolverv", Day(rows, "2024-06-20").HolidayName);
        Assert.Equal("Høstjevndøgn", Day(rows, "2024-09-22").HolidayName);
        Assert.Equal("Vintersolverv", Day(rows, "2024-12-21").HolidayName);

        // Clock changes, read from the zone's own rules. The spring change is uncontested in 2025 (Easter fell
        // in April), so the marker survives there.
        var y2025 = CalendarDimensionBuilder.Build(Flow("2025-01-01", "2025-12-31"));
        Assert.Equal("Sommertid starter", Day(y2025, "2025-03-30").HolidayName);
    }

    [Fact]
    public void TheAutumnClockChangeAlwaysYieldsToBotsOgBededag()
    {
        // Both fall on the last Sunday of October by construction, so they collide every single year and a
        // date carries one name. Rank decides it the same way every time: the named church day outranks the
        // clock marker. Old production had no such rule and showed whichever row its scraper wrote last, which
        // is why "Sommertid slutter" and "Bots- og bededag" both appear across its years for the same rule.
        foreach (var year in new[] { 2024, 2025, 2026 })
        {
            var rows = CalendarDimensionBuilder.Build(Flow($"{year}-10-01", $"{year}-10-31"));
            var lastSunday = rows.Last(r => r.Date.DayOfWeek == DayOfWeek.Sunday);
            Assert.Equal("Bots- og bededag", lastSunday.HolidayName);
        }
    }

    [Fact]
    public void APublicHolidayOutranksAMarkerOnTheSameDate()
    {
        // Easter Sunday 2024 fell on the spring clock change. The legacy scraper kept whichever row it wrote
        // last and lost the holiday; rank makes the outcome deliberate instead of incidental.
        var rows = CalendarDimensionBuilder.Build(Flow("2024-03-31", "2024-03-31"));

        Assert.Equal("Første påskedag", rows[0].HolidayName);
        Assert.True(rows[0].IsHoliday);
    }

    [Fact]
    public void OrdinaryDaysCarryNullNotFalseSoTheReportingViewsKeepWorking()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-03-05", "2024-03-05"));

        Assert.Null(rows[0].IsHoliday);
        Assert.Null(rows[0].HolidayName);
    }

    [Fact]
    public void PublicHolidaysOnlyDropsTheNamedDaysAndMarkers()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31", CalendarObservanceSet.PublicHolidays));

        Assert.Equal("Første nyttårsdag", Day(rows, "2024-01-01").HolidayName);
        Assert.Null(Day(rows, "2024-10-31").HolidayName);   // Halloween is not statutory
        Assert.Null(Day(rows, "2024-12-24").HolidayName);   // nor is Christmas Eve
        Assert.Equal(12, rows.Count(r => r.IsHoliday == true));
    }

    [Fact]
    public void NoneMarksNothing()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31", CalendarObservanceSet.None));

        Assert.All(rows, r => Assert.Null(r.IsHoliday));
        Assert.All(rows, r => Assert.Null(r.HolidayName));
    }

    [Fact]
    public void DaylightSavingFollowsTheDeclaredZoneNotTheHost()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));

        Assert.False(Day(rows, "2024-01-15").DaylightSavingTime);
        Assert.True(Day(rows, "2024-07-15").DaylightSavingTime);
        Assert.False(Day(rows, "2024-11-15").DaylightSavingTime);

        // Europe/Oslo ran summer time from 31 March to 27 October in 2024: 210 days.
        Assert.Equal(210, rows.Count(r => r.DaylightSavingTime));
    }

    [Fact]
    public void IsoWeekNumberFollowsTheIsoRuleNotTheCultureRule()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2021-01-01", "2021-01-04"));

        // 1 January 2021 was a Friday, so ISO puts it in week 53 of 2020.
        Assert.Equal(53, Day(rows, "2021-01-01").IsoWeekNumber);
        Assert.Equal(1, Day(rows, "2021-01-04").IsoWeekNumber);
    }

    [Fact]
    public void FlagsWeekendsLeapYearsAndMonthEnds()
    {
        var rows = CalendarDimensionBuilder.Build(Flow("2024-02-24", "2024-03-01"));

        Assert.True(Day(rows, "2024-02-24").IsWeekend);      // Saturday
        Assert.True(Day(rows, "2024-02-25").IsWeekend);      // Sunday
        Assert.False(Day(rows, "2024-02-26").IsWeekend);     // Monday
        Assert.All(rows, r => Assert.True(r.IsLeapYear));
        Assert.True(Day(rows, "2024-02-29").IsLastDayOfMonth);
        Assert.False(Day(rows, "2024-02-28").IsLastDayOfMonth);
    }

    [Fact]
    public void GenerationIsDeterministic()
    {
        var first = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));
        var second = CalendarDimensionBuilder.Build(Flow("2024-01-01", "2024-12-31"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void RejectsABackwardsRange()
    {
        var ex = Assert.Throws<SqlFlowException>(() => CalendarDimensionBuilder.Build(Flow("2024-12-31", "2024-01-01")));
        Assert.Contains("ends before it starts", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnAbsurdlyWideRange()
    {
        var ex = Assert.Throws<SqlFlowException>(() => CalendarDimensionBuilder.Build(Flow("1900-01-01", "2500-01-01")));
        Assert.Contains("more than the", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsAnUnsupportedCountry()
    {
        var flow = Flow("2024-01-01", "2024-01-31") with { Country = "SE" };
        var ex = Assert.Throws<SqlFlowException>(() => CalendarDimensionBuilder.Build(flow));
        Assert.Contains("is not supported", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(2013, 3, 31)]
    [InlineData(2020, 4, 12)]
    [InlineData(2024, 3, 31)]
    [InlineData(2025, 4, 20)]
    [InlineData(2026, 4, 5)]
    public void EasterIsComputedCorrectly(int year, int month, int day)
    {
        var rows = CalendarDimensionBuilder.Build(
            Flow($"{year}-01-01", $"{year}-12-31", CalendarObservanceSet.PublicHolidays));

        var easter = rows.Single(r => r.HolidayName == "Første påskedag");
        Assert.Equal(new DateOnly(year, month, day), easter.Date);
    }
}
