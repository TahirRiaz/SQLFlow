namespace SqlFlow.Core.Calendar;

/// <summary>
/// Norway's calendar conventions. The observance set reproduces what the legacy dimension carried (which came
/// from scraping a public holiday site) but derives every date from a rule instead of a web page, so the same
/// year always produces the same answer and a name never drifts between runs. Old production shows that drift
/// plainly: "Grunnlovsdag" in some years and "Grunnlovsdagen" in others, for the same 17 May.
/// </summary>
public sealed class NorwegianCalendar : CountryCalendar
{
    public override string Country => "NO";

    public override string DefaultCulture => "nb-NO";

    public override string DefaultTimeZone => "Europe/Oslo";

    public override string SeasonName(int month) => month switch
    {
        12 or 1 or 2 => "Vinter",
        3 or 4 or 5 => "Vår",
        6 or 7 or 8 => "Sommer",
        9 or 10 or 11 => "Høst",
        _ => throw new ArgumentOutOfRangeException(nameof(month), month, "A month number must be 1 through 12."),
    };

    /// <summary>Fixed-date observances: (month, day, name, rank).</summary>
    private static readonly (int Month, int Day, string Name, ObservanceRank Rank)[] FixedDates =
    [
        (1, 1, "Første nyttårsdag", ObservanceRank.PublicHoliday),
        (1, 21, "Prinsesse Ingrid Alexandras fødselsdag", ObservanceRank.Named),
        (2, 6, "Samenes nasjonaldag", ObservanceRank.Named),
        (2, 14, "Valentinsdagen", ObservanceRank.Named),
        (2, 21, "Kong Harald Vs fødselsdag", ObservanceRank.Named),
        (3, 8, "Den internasjonale kvinnedagen", ObservanceRank.Named),
        (5, 1, "Arbeidernes dag", ObservanceRank.PublicHoliday),
        (5, 8, "Frigjøringsdagen 1945", ObservanceRank.Named),
        (5, 17, "Grunnlovsdagen", ObservanceRank.PublicHoliday),
        (6, 7, "Unionsoppløsningen", ObservanceRank.Named),
        (6, 23, "Sankthansaften", ObservanceRank.Named),
        (6, 24, "Sankthans", ObservanceRank.Named),
        (7, 4, "Dronning Sonjas fødselsdag", ObservanceRank.Named),
        (7, 20, "Kronprins Haakons fødselsdag", ObservanceRank.Named),
        (7, 29, "Olsok", ObservanceRank.Named),
        (8, 19, "Kronprinsesse Mette-Marits fødselsdag", ObservanceRank.Named),
        (10, 24, "FN-dagen", ObservanceRank.Named),
        (10, 31, "Halloween", ObservanceRank.Named),
        (12, 24, "Julaften", ObservanceRank.Named),
        (12, 25, "Første juledag", ObservanceRank.PublicHoliday),
        (12, 26, "Andre juledag", ObservanceRank.PublicHoliday),
        (12, 31, "Nyttårsaften", ObservanceRank.Named),
    ];

    /// <summary>Observances at a fixed offset from Easter Sunday: (offset in days, name, rank).</summary>
    private static readonly (int Offset, string Name, ObservanceRank Rank)[] EasterRelative =
    [
        (-49, "Fastelavn", ObservanceRank.Named),
        (-7, "Palmesøndag", ObservanceRank.Named),
        (-3, "Skjærtorsdag", ObservanceRank.PublicHoliday),
        (-2, "Langfredag", ObservanceRank.PublicHoliday),
        (-1, "Påskeaften", ObservanceRank.Named),
        (0, "Første påskedag", ObservanceRank.PublicHoliday),
        (1, "Andre påskedag", ObservanceRank.PublicHoliday),
        (39, "Kristi himmelfartsdag", ObservanceRank.PublicHoliday),
        (48, "Pinseaften", ObservanceRank.Named),
        (49, "Første pinsedag", ObservanceRank.PublicHoliday),
        (50, "Andre pinsedag", ObservanceRank.PublicHoliday),
    ];

    /// <summary>The first Norwegian parliamentary election of the modern four-year cycle this engine covers;
    /// elections fall on the second Monday of September every fourth year from here.</summary>
    private const int ElectionAnchorYear = 2013;

    public override IEnumerable<Observance> Observances(int year, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);

        foreach (var (month, day, name, rank) in FixedDates)
        {
            yield return new Observance(new DateOnly(year, month, day), name, rank);
        }

        var easter = EasterSunday(year);
        foreach (var (offset, name, rank) in EasterRelative)
        {
            yield return new Observance(easter.AddDays(offset), name, rank);
        }

        // Nth-weekday observances.
        yield return new Observance(NthWeekdayOfMonth(year, 2, DayOfWeek.Sunday, 2), "Morsdag", ObservanceRank.Named);
        yield return new Observance(LastWeekdayOfMonth(year, 10, DayOfWeek.Sunday), "Bots- og bededag", ObservanceRank.Named);
        yield return new Observance(NthWeekdayOfMonth(year, 11, DayOfWeek.Sunday, 1), "Allehelgensdag", ObservanceRank.Named);
        yield return new Observance(NthWeekdayOfMonth(year, 11, DayOfWeek.Sunday, 2), "Farsdag", ObservanceRank.Named);

        // Advent: the fourth Sunday of advent is the last Sunday on or before Christmas Eve, and the earlier
        // three are the preceding Sundays.
        var christmasEve = new DateOnly(year, 12, 24);
        var fourthAdvent = christmasEve.AddDays(-(int)christmasEve.DayOfWeek);
        yield return new Observance(fourthAdvent.AddDays(-21), "Første søndag i advent", ObservanceRank.Named);
        yield return new Observance(fourthAdvent.AddDays(-14), "Andre søndag i advent", ObservanceRank.Named);
        yield return new Observance(fourthAdvent.AddDays(-7), "Tredje søndag i advent", ObservanceRank.Named);
        yield return new Observance(fourthAdvent, "Fjerde søndag i advent", ObservanceRank.Named);

        if ((year - ElectionAnchorYear) % 4 == 0 && year >= ElectionAnchorYear)
        {
            yield return new Observance(
                NthWeekdayOfMonth(year, 9, DayOfWeek.Monday, 2), "Stortingsvalg", ObservanceRank.Named);
        }

        // Astronomical markers, resolved to the local date in the flow's zone: an equinox just before midnight
        // UTC falls on the following day in Oslo.
        var events = SeasonEvents.ForYear(year);
        yield return new Observance(LocalDate(events.MarchEquinoxUtc, timeZone), "Vårjevndøgn", ObservanceRank.Marker);
        yield return new Observance(LocalDate(events.JuneSolsticeUtc, timeZone), "Sommersolverv", ObservanceRank.Marker);
        yield return new Observance(LocalDate(events.SeptemberEquinoxUtc, timeZone), "Høstjevndøgn", ObservanceRank.Marker);
        yield return new Observance(LocalDate(events.DecemberSolsticeUtc, timeZone), "Vintersolverv", ObservanceRank.Marker);

        // Clock changes, read from the zone's own rules rather than assumed to be the last Sunday of March and
        // October, so the calendar stays right if Norway ever changes or abolishes them.
        foreach (var (date, entering) in ClockChanges(year, timeZone))
        {
            yield return new Observance(date, entering ? "Sommertid starter" : "Sommertid slutter", ObservanceRank.Marker);
        }
    }

    private static DateOnly LocalDate(DateTime instantUtc, TimeZoneInfo timeZone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(instantUtc, timeZone));
}
