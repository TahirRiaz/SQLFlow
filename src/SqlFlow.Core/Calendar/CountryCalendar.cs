namespace SqlFlow.Core.Calendar;

/// <summary>
/// A country's calendar conventions: the culture its day and month names come from, the time zone its clock
/// follows, what it calls the seasons, and which days it names. One implementation per supported country;
/// <see cref="For"/> is the only way to get one, and an unsupported country is a loud error rather than a
/// silent fallback to some other country's holidays.
/// </summary>
public abstract class CountryCalendar
{
    /// <summary>The ISO 3166-1 alpha-2 code this calendar covers.</summary>
    public abstract string Country { get; }

    /// <summary>The culture day and month names are taken from when the flow does not override it.</summary>
    public abstract string DefaultCulture { get; }

    /// <summary>The IANA zone the daylight-saving flag follows when the flow does not override it.</summary>
    public abstract string DefaultTimeZone { get; }

    /// <summary>The season a month belongs to, named in the country's language.</summary>
    public abstract string SeasonName(int month);

    /// <summary>Every observance the country names in the given year, resolved in <paramref name="timeZone"/>
    /// (which the astronomical events and the clock changes depend on).</summary>
    public abstract IEnumerable<Observance> Observances(int year, TimeZoneInfo timeZone);

    /// <summary>The countries this engine can generate a calendar for.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["NO"];

    /// <summary>Resolves a country code to its calendar, case-insensitively.</summary>
    /// <exception cref="SqlFlowException">The country has no implementation.</exception>
    public static CountryCalendar For(string country)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(country);
        var code = country.Trim().ToUpperInvariant();
        if (string.Equals(code, "NO", StringComparison.Ordinal))
        {
            return new NorwegianCalendar();
        }

        throw new SqlFlowException(
            $"calendar country '{country}' is not supported. Supported countries: {string.Join(", ", Supported)}. " +
            "A country is added by implementing its observance rules in the engine, not by configuration: a date " +
            "dimension whose holidays are guessed is worse than one that refuses to generate.");
    }

    /// <summary>The Gregorian Easter Sunday for a year, by the anonymous (Meeus/Jones/Butcher) algorithm.</summary>
    protected static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = ((19 * a) + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + (2 * e) + (2 * i) - h - k) % 7;
        var m = (a + (11 * h) + (22 * l)) / 451;
        var month = (h + l - (7 * m) + 114) / 31;
        var day = ((h + l - (7 * m) + 114) % 31) + 1;
        return new DateOnly(year, month, day);
    }

    /// <summary>The <paramref name="occurrence"/>-th <paramref name="day"/> of a month (1 = first).</summary>
    protected static DateOnly NthWeekdayOfMonth(int year, int month, DayOfWeek day, int occurrence)
    {
        var first = new DateOnly(year, month, 1);
        var offset = ((int)day - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (7 * (occurrence - 1)));
    }

    /// <summary>The last <paramref name="day"/> of a month.</summary>
    protected static DateOnly LastWeekdayOfMonth(int year, int month, DayOfWeek day)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        var offset = ((int)last.DayOfWeek - (int)day + 7) % 7;
        return last.AddDays(-offset);
    }

    /// <summary>
    /// The days in <paramref name="year"/> on which the zone's clock changes, as (date, movedToDaylightSaving)
    /// pairs. Derived from the zone's own rules by walking the year, so it stays correct for a country that
    /// changes its transition dates (or abolishes the practice) without any rule duplicated here.
    /// </summary>
    protected static IEnumerable<(DateOnly Date, bool EnteringDaylightSaving)> ClockChanges(int year, TimeZoneInfo timeZone)
    {
        // Noon rather than midnight: every real transition happens in the small hours, so sampling at midday
        // reads the state the date is actually in and never lands inside the skipped or repeated hour.
        var previous = IsDaylightSaving(new DateOnly(year, 1, 1), timeZone);
        for (var date = new DateOnly(year, 1, 2); date.Year == year; date = date.AddDays(1))
        {
            var current = IsDaylightSaving(date, timeZone);
            if (current != previous)
            {
                yield return (date, current);
            }

            previous = current;
        }
    }

    /// <summary>
    /// Whether the zone is on daylight saving time during the given date, sampled at local noon. An unspecified
    /// <see cref="DateTime"/> is interpreted as wall-clock time in the zone itself, and midday is far enough
    /// from any real transition to never land inside the skipped or repeated hour.
    /// </summary>
    public static bool IsDaylightSaving(DateOnly date, TimeZoneInfo timeZone)
        => timeZone.IsDaylightSavingTime(
            DateTime.SpecifyKind(date.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified));
}
