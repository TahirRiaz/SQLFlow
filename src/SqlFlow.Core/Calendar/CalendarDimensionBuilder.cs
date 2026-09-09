using System.Globalization;

namespace SqlFlow.Core.Calendar;

/// <summary>
/// Generates the date dimension for a flow's declared range. Pure and deterministic: the same flow definition
/// always produces the same rows, on any machine, in any host locale. Nothing here touches a database, so the
/// whole dimension is unit-testable without one.
/// </summary>
public static class CalendarDimensionBuilder
{
    /// <summary>The widest range the generator will build. A date dimension spans the years its facts can
    /// reference; a request for tens of thousands of years is a typo in the flow file, and answering it with a
    /// multi-million-row table nobody asked for is worse than refusing.</summary>
    public const int MaxYears = 200;

    /// <summary>Resolves a culture name, turning an unknown one into a flow-level error.</summary>
    public static CultureInfo ResolveCulture(string culture)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(culture);
        try
        {
            return CultureInfo.GetCultureInfo(culture);
        }
        catch (CultureNotFoundException ex)
        {
            throw new SqlFlowException($"calendar culture '{culture}' is not a culture this system knows.", ex);
        }
    }

    /// <summary>Resolves an IANA (or Windows) time-zone id, turning an unknown one into a flow-level error.</summary>
    public static TimeZoneInfo ResolveTimeZone(string timeZone)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new SqlFlowException($"calendar timezone '{timeZone}' is not a time zone this system knows.", ex);
        }
    }

    /// <summary>Builds every row in the flow's declared range, ordered by date.</summary>
    public static IReadOnlyList<CalendarRow> Build(CalendarFlow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);

        if (flow.To < flow.From)
        {
            throw new SqlFlowException(
                $"calendar range ends before it starts: from {flow.From:yyyy-MM-dd} to {flow.To:yyyy-MM-dd}.");
        }

        if (flow.FiscalYearStartMonth is < 1 or > 12)
        {
            throw new SqlFlowException(
                $"calendar fiscalYearStartMonth must be 1 through 12, not {flow.FiscalYearStartMonth}.");
        }

        var years = flow.To.Year - flow.From.Year + 1;
        if (years > MaxYears)
        {
            throw new SqlFlowException(
                $"calendar range spans {years} years ({flow.From:yyyy-MM-dd} to {flow.To:yyyy-MM-dd}), more than the " +
                $"{MaxYears}-year maximum. Declare the range the facts actually reference.");
        }

        var country = CountryCalendar.For(flow.Country);
        var culture = ResolveCulture(flow.Culture);
        var timeZone = ResolveTimeZone(flow.TimeZone);
        var observances = CollectObservances(flow, country, timeZone);

        var rows = new List<CalendarRow>(flow.To.DayNumber - flow.From.DayNumber + 1);
        for (var date = flow.From; date <= flow.To; date = date.AddDays(1))
        {
            observances.TryGetValue(date, out var observance);
            rows.Add(BuildRow(date, flow, country, culture, timeZone, observance));
        }

        return rows;
    }

    /// <summary>
    /// The observance for each date in the range, after filtering to the declared set and resolving collisions
    /// by rank. Years are walked one past each end of the range because an advent Sunday or a solstice computed
    /// for the neighbouring year can still land inside it.
    /// </summary>
    private static Dictionary<DateOnly, Observance> CollectObservances(
        CalendarFlow flow, CountryCalendar country, TimeZoneInfo timeZone)
    {
        var result = new Dictionary<DateOnly, Observance>();
        if (flow.Observances == CalendarObservanceSet.None)
        {
            return result;
        }

        var publicOnly = flow.Observances == CalendarObservanceSet.PublicHolidays;

        for (var year = flow.From.Year - 1; year <= flow.To.Year + 1; year++)
        {
            foreach (var observance in country.Observances(year, timeZone))
            {
                if (observance.Date < flow.From || observance.Date > flow.To)
                {
                    continue;
                }

                if (publicOnly && observance.Rank != ObservanceRank.PublicHoliday)
                {
                    continue;
                }

                // A higher rank takes the date; an equal rank leaves the first one in place, so the outcome does
                // not depend on enumeration order.
                if (!result.TryGetValue(observance.Date, out var existing) || observance.Rank > existing.Rank)
                {
                    result[observance.Date] = observance;
                }
            }
        }

        return result;
    }

    private static CalendarRow BuildRow(
        DateOnly date,
        CalendarFlow flow,
        CountryCalendar country,
        CultureInfo culture,
        TimeZoneInfo timeZone,
        Observance observance)
    {
        var format = culture.DateTimeFormat;
        var monthShort = Trim(format.AbbreviatedMonthNames[date.Month - 1]);
        var fiscalYear = FiscalYear(date, flow.FiscalYearStartMonth);
        var named = observance.Name is { Length: > 0 };

        return new CalendarRow
        {
            PeriodId = (date.Year * 10000) + (date.Month * 100) + date.Day,
            Date = date,
            DayOfMonth = date.Day,
            DayOfWeekName = format.GetDayName(date.DayOfWeek),
            DayOfWeekNameShort = Trim(format.AbbreviatedDayNames[(int)date.DayOfWeek]),
            DayOfWeekNumber = (int)date.DayOfWeek,
            WeekOfYear = culture.Calendar.GetWeekOfYear(
                date.ToDateTime(TimeOnly.MinValue), format.CalendarWeekRule, format.FirstDayOfWeek),
            MonthNumber = date.Month,
            MonthName = format.GetMonthName(date.Month),
            MonthNameShort = monthShort,
            MonthNumName = string.Create(CultureInfo.InvariantCulture, $"{date.Month:00}-{monthShort}"),
            Quarter = string.Create(CultureInfo.InvariantCulture, $"Q{((date.Month - 1) / 3) + 1}"),
            Year = date.Year,
            IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            IsLeapYear = DateTime.IsLeapYear(date.Year),
            IsLastDayOfMonth = date.Day == DateTime.DaysInMonth(date.Year, date.Month),
            FiscalWeekOfYear = FiscalWeekOfYear(date, fiscalYear, flow.FiscalYearStartMonth, format.FirstDayOfWeek),
            FiscalMonth = ((date.Month - flow.FiscalYearStartMonth + 12) % 12) + 1,
            FiscalQuarter = (((date.Month - flow.FiscalYearStartMonth + 12) % 12) / 3) + 1,
            FiscalYear = fiscalYear,

            // NULL, never false, on an ordinary day: the reporting views branch on IS NULL.
            IsHoliday = named ? true : null,
            HolidayName = named ? observance.Name : null,
            Season = country.SeasonName(date.Month),
            DaylightSavingTime = CountryCalendar.IsDaylightSaving(date, timeZone),
            IsoWeekNumber = ISOWeek.GetWeekOfYear(date.ToDateTime(TimeOnly.MinValue)),
        };
    }

    /// <summary>
    /// The fiscal year a date belongs to, labelled by the calendar year the fiscal year STARTS in. With a
    /// January start this is simply the calendar year, which is what makes the common case behave the way
    /// everybody expects.
    /// </summary>
    private static int FiscalYear(DateOnly date, int fiscalYearStartMonth)
        => date.Month >= fiscalYearStartMonth ? date.Year : date.Year - 1;

    /// <summary>
    /// The week of the fiscal year, counting the week that contains the fiscal year's first day as week 1.
    /// Both ends are snapped to the start of their week first, so every day inside one week reports the same
    /// number regardless of which weekday the fiscal year happened to begin on.
    /// </summary>
    private static int FiscalWeekOfYear(DateOnly date, int fiscalYear, int fiscalYearStartMonth, DayOfWeek firstDayOfWeek)
    {
        var fiscalStart = new DateOnly(fiscalYear, fiscalYearStartMonth, 1);
        var weeksApart = (StartOfWeek(date, firstDayOfWeek).DayNumber - StartOfWeek(fiscalStart, firstDayOfWeek).DayNumber) / 7;
        return weeksApart + 1;
    }

    private static DateOnly StartOfWeek(DateOnly date, DayOfWeek firstDayOfWeek)
        => date.AddDays(-(((int)date.DayOfWeek - (int)firstDayOfWeek + 7) % 7));

    /// <summary>Culture abbreviations carry a trailing period in some languages (Norwegian writes "man.");
    /// the dimension has always stored them bare.</summary>
    private static string Trim(string abbreviation) => abbreviation.Replace(".", string.Empty, StringComparison.Ordinal);
}
