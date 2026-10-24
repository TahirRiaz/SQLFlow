namespace SqlFlow.Catalog;

/// <summary>
/// How long a run took when its flow kind does not say: the seconds between the instant it started and the instant it
/// ended. A kind that reports its own duration keeps that figure; every other run (a kind that reports none, or a run
/// failed, cancelled or interrupted before it wrote an artifact) is timed by these two instants, so a run that started
/// and ended always says how long it took. A run that never started (cancelled or skipped while still queued) has no
/// duration, and neither has one whose end is recorded before its start: two clocks that disagree say nothing about how
/// long it ran.
/// </summary>
public static class RunElapsed
{
    /// <summary>The longest span SQL Server's <c>DATEDIFF</c> counts in milliseconds without overflowing its
    /// <c>int</c> (a little under 25 days). A set-based write times a longer run in whole seconds instead, so a run that
    /// went on for weeks still ends rather than failing its terminal write on an arithmetic overflow.</summary>
    public static readonly TimeSpan MillisecondSpan = TimeSpan.FromDays(24);

    /// <summary>The seconds from <paramref name="startUtc"/> to <paramref name="endUtc"/>, or null when either is
    /// unknown or the end comes before the start.</summary>
    public static double? Seconds(DateTime? startUtc, DateTime? endUtc)
        => startUtc is { } start && endUtc is { } end && end >= start ? (end - start).TotalSeconds : null;
}
