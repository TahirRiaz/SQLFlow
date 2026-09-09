using SqlFlow.Core.Ingestion;

namespace SqlFlow.Core.Calendar;

/// <summary>Which observances the generator marks on the calendar (the <c>IsHoliday</c>/<c>HolidayName</c>
/// pair).</summary>
public enum CalendarObservanceSet
{
    /// <summary>No day is marked: <c>IsHoliday</c> stays NULL and <c>HolidayName</c> empty for every row.</summary>
    None,

    /// <summary>Only the statutory public holidays (Norway: the thirteen <c>helligdager</c>).</summary>
    PublicHolidays,

    /// <summary>Public holidays plus the named observances a general-purpose calendar carries: the eves,
    /// flag days, royal birthdays, advent and other nth-weekday days, the equinoxes and solstices, and the two
    /// daylight-saving transitions. This is the set the legacy scraped calendar carried.</summary>
    Full,
}

/// <summary>
/// A calendar-dimension flow (<c>flowType: cal</c>): a DAG node that GENERATES a date dimension for a declared
/// range and merges it into a table on a resolved server. Unlike every other flow kind it has no data source:
/// the rows are computed from the declared range, country and fiscal-year start, so the flow is completely
/// deterministic and depends on nothing outside the engine.
///
/// The range is explicit (<see cref="From"/> to <see cref="To"/>) rather than rolling. A date dimension should
/// hold exactly the span its facts can reference, so widening it is a deliberate edit to the flow file, not a
/// silent side effect of the schedule firing. Immutable.
/// </summary>
public sealed record CalendarFlow
{
    /// <summary>The stable numeric identity derived from the flow name, as every other document kind derives it.</summary>
    public int FlowId { get; init; }

    public required string SysAlias { get; init; }

    public string? Batch { get; init; }

    /// <summary>The flow's declared lifecycle (production by default): a development flow runs identically but
    /// never generates notification events.</summary>
    public Runs.FlowLifecycle Lifecycle { get; init; } = Runs.FlowLifecycle.Production;

    /// <summary>The connection-registry alias of the server the dimension is written to.</summary>
    public required string Server { get; init; }

    /// <summary>The three-part table the dimension is merged into.</summary>
    public required RelationalObject Table { get; init; }

    /// <summary>The first date in the dimension, inclusive.</summary>
    public required DateOnly From { get; init; }

    /// <summary>The last date in the dimension, inclusive.</summary>
    public required DateOnly To { get; init; }

    /// <summary>The ISO 3166-1 alpha-2 country whose observances and season names are used.</summary>
    public required string Country { get; init; }

    /// <summary>The culture whose day and month names are emitted (defaulted from <see cref="Country"/>).</summary>
    public required string Culture { get; init; }

    /// <summary>The IANA time zone the daylight-saving flag and the astronomical observances are resolved in.
    /// The legacy generator used the host's local zone, which made the result depend on which machine ran it.</summary>
    public required string TimeZone { get; init; }

    /// <summary>The month the fiscal year starts in, 1 through 12.</summary>
    public int FiscalYearStartMonth { get; init; } = 1;

    public CalendarObservanceSet Observances { get; init; } = CalendarObservanceSet.Full;

    /// <summary>When true the target table is dropped and rebuilt instead of merged. Off by default: the merge
    /// is idempotent, so a re-run refreshes in place and leaves any surrogate keys downstream may hold intact.</summary>
    public bool Rebuild { get; init; }

    public string? Description { get; init; }

    public string FlowType { get; } = "cal";

    /// <summary>The connection reference understood by the connection resolver.</summary>
    public string ConnectionReference => "@" + Server;
}
