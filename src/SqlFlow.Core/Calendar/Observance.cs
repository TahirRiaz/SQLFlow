namespace SqlFlow.Core.Calendar;

/// <summary>
/// How strongly an observance claims its date. A single date carries a single name, so when two observances
/// land on the same day (Easter Sunday falling on the spring daylight-saving switch, say) the higher rank wins.
/// The legacy scraped calendar had no such rule and simply kept whichever row the parser wrote last, which is
/// why old production shows "Sommertid starter" on Easter Sunday 2024.
/// </summary>
public enum ObservanceRank
{
    /// <summary>An astronomical marker or a clock change: real, but the first to yield.</summary>
    Marker = 0,

    /// <summary>A named day that is not a day off: an eve, a flag day, a royal birthday, an advent Sunday.</summary>
    Named = 1,

    /// <summary>A statutory public holiday.</summary>
    PublicHoliday = 2,
}

/// <summary>One named day in a country's calendar.</summary>
/// <param name="Date">The day it falls on.</param>
/// <param name="Name">The name, in the country's own language.</param>
/// <param name="Rank">How strongly it claims the date when two observances collide.</param>
public readonly record struct Observance(DateOnly Date, string Name, ObservanceRank Rank);
