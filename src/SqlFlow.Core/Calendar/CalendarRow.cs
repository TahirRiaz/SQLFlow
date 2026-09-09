namespace SqlFlow.Core.Calendar;

/// <summary>
/// One generated day. The property set and the order they are declared in ARE the dimension's column contract:
/// the runner creates and merges the target from this shape, so a consumer selecting positionally sees the same
/// layout the legacy generator produced.
///
/// <see cref="IsHoliday"/> is deliberately nullable. The legacy table stored NULL, never 0, for an ordinary day,
/// and the reporting views distinguish the two (<c>WHEN [IsHoliday] IS NULL THEN ''</c>), so writing 0 would
/// silently change what those views return.
/// </summary>
public sealed record CalendarRow
{
    /// <summary>The date as yyyyMMdd, the dimension's surrogate key.</summary>
    public required int PeriodId { get; init; }

    public required DateOnly Date { get; init; }

    public required int DayOfMonth { get; init; }

    /// <summary>The day name in the flow's culture (Norwegian is lower-case: <c>mandag</c>).</summary>
    public required string DayOfWeekName { get; init; }

    /// <summary>The abbreviated day name, with any trailing period removed (<c>man</c>, not <c>man.</c>).</summary>
    public required string DayOfWeekNameShort { get; init; }

    /// <summary>The .NET <see cref="System.DayOfWeek"/> ordinal (Sunday = 0), which is what the legacy
    /// dimension stored and what the reporting views re-base from.</summary>
    public required int DayOfWeekNumber { get; init; }

    /// <summary>The week number under the culture's own calendar week rule.</summary>
    public required int WeekOfYear { get; init; }

    public required int MonthNumber { get; init; }

    public required string MonthName { get; init; }

    public required string MonthNameShort { get; init; }

    /// <summary>The zero-padded month number joined to the abbreviated month name (<c>01-jan</c>): a label that
    /// sorts chronologically as a string.</summary>
    public required string MonthNumName { get; init; }

    /// <summary>The calendar quarter as <c>Q1</c> through <c>Q4</c>.</summary>
    public required string Quarter { get; init; }

    public required int Year { get; init; }

    public required bool IsWeekend { get; init; }

    public required bool IsLeapYear { get; init; }

    public required bool IsLastDayOfMonth { get; init; }

    /// <summary>The week of the fiscal year, counting from 1 in the week containing the fiscal year's first day.</summary>
    public required int FiscalWeekOfYear { get; init; }

    public required int FiscalMonth { get; init; }

    public required int FiscalQuarter { get; init; }

    /// <summary>The fiscal year this date falls in. With a January fiscal start this equals the calendar year.</summary>
    public required int FiscalYear { get; init; }

    /// <summary>True on an observed day, NULL otherwise. Never false: see the type remarks.</summary>
    public required bool? IsHoliday { get; init; }

    /// <summary>The observance's name, or null on an ordinary day.</summary>
    public required string? HolidayName { get; init; }

    /// <summary>The season name in the flow's country.</summary>
    public required string Season { get; init; }

    /// <summary>Whether the flow's declared time zone is in daylight saving time on this date.</summary>
    public required bool DaylightSavingTime { get; init; }

    /// <summary>The ISO-8601 week number (weeks start Monday; week 1 contains the first Thursday).</summary>
    public required int IsoWeekNumber { get; init; }
}
