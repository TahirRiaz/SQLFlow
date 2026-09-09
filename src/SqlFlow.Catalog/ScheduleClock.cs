using Cronos;

using SqlFlow.Core.Secrets;

namespace SqlFlow.Catalog;

/// <summary>
/// Computes when a schedule fires next, and validates a schedule's timing. A cron schedule is evaluated in its IANA
/// time zone with correct daylight-saving handling (via Cronos); an interval schedule is simply "now plus N
/// seconds". This lives with the schedule table and store so the whole scheduling domain (persistence, claim, and
/// timing math) is one cohesive unit; the catalog sync uses it to arm YAML schedules and the control plane's
/// scheduler uses it to advance them.
/// </summary>
public static class ScheduleClock
{
    /// <summary>The next fire strictly after <paramref name="afterUtc"/>, or null when there is none: an interval
    /// always has one; a cron returns null when it has no further occurrence OR when the cron / time zone is
    /// malformed (a parked schedule). Use <see cref="TryValidate"/> when the caller needs to know WHY.</summary>
    public static DateTime? NextFire(string? cron, int? intervalSeconds, string timezone, DateTime afterUtc)
    {
        var fromUtc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (intervalSeconds is { } seconds && seconds > 0)
        {
            return fromUtc.AddSeconds(seconds);
        }

        if (string.IsNullOrWhiteSpace(cron))
        {
            return null;
        }

        try
        {
            return ParseCron(cron).GetNextOccurrence(fromUtc, ResolveTimeZone(timezone));
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // A malformed cron / unknown time zone simply has no computable next fire; the scheduler parks it and the
            // API validates up front with TryValidate, so this never silently swallows a real schedule.
            return null;
        }
    }

    /// <summary>
    /// How many days a schedule normally leaves between fires: the median gap over its next
    /// <paramref name="occurrences"/> occurrences, or null when the timing is not computable (a malformed cron,
    /// an unknown time zone, a chained schedule with no cadence of its own, or an expression with no further
    /// occurrence).
    /// <para>
    /// The MEDIAN rather than the mean, because a cron is free to be irregular: "0 4 * * 1-5" fires four times a
    /// day apart and once three days apart, and the median calls that daily while the mean invents a cadence of
    /// 1.4 days that the schedule never actually keeps. Monitoring compares against this to say whether a stream
    /// is overdue, so a DECLARED cadence beats one inferred from history: a stream silent for three weeks teaches
    /// an inferred detector that three-week gaps are normal, where a cron keeps saying "every day".
    /// </para>
    /// </summary>
    public static double? ExpectedGapDays(
        string? cron, int? intervalSeconds, string timezone, DateTime fromUtc, int occurrences = 16)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(occurrences, 2);

        if (intervalSeconds is { } seconds && seconds > 0)
        {
            return seconds / 86400.0;
        }

        var gaps = new List<double>(occurrences - 1);
        var cursor = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        for (var i = 0; i < occurrences; i++)
        {
            var next = NextFire(cron, intervalSeconds, timezone, cursor);
            if (next is not { } fire)
            {
                break;
            }

            if (i > 0)
            {
                gaps.Add((fire - cursor).TotalDays);
            }

            cursor = fire;
        }

        if (gaps.Count == 0)
        {
            return null;
        }

        gaps.Sort();
        var mid = gaps.Count / 2;
        return gaps.Count % 2 == 1 ? gaps[mid] : (gaps[mid - 1] + gaps[mid]) / 2.0;
    }

    /// <summary>Validates that a schedule sets exactly one of cron / interval and that the cron and time zone parse,
    /// returning a precise message for the API to surface as a 400 rather than letting a bad schedule reach storage.</summary>
    public static bool TryValidate(string? cron, int? intervalSeconds, string timezone, out string? error)
    {
        error = null;
        var hasCron = !string.IsNullOrWhiteSpace(cron);
        var hasInterval = intervalSeconds is > 0;
        if (hasCron == hasInterval)
        {
            error = "a schedule must set exactly one of 'cron' or 'intervalSeconds'.";
            return false;
        }

        if (intervalSeconds is { } seconds && seconds < 1)
        {
            error = "'intervalSeconds' must be a positive number of seconds.";
            return false;
        }

        try
        {
            if (hasCron)
            {
                ParseCron(cron!);
                ResolveTimeZone(timezone);
            }

            return true;
        }
        catch (Exception ex) when (ex is CronFormatException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            error = SecretHygiene.RedactedMessage(ex);
            return false;
        }
    }

    private static CronExpression ParseCron(string cron)
    {
        var fields = cron.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Six fields means the expression includes a leading seconds field; five is the standard minute granularity.
        return CronExpression.Parse(cron, fields.Length >= 6 ? CronFormat.IncludeSeconds : CronFormat.Standard);
    }

    private static TimeZoneInfo ResolveTimeZone(string timezone)
        => string.IsNullOrWhiteSpace(timezone) || timezone.Equals("UTC", StringComparison.OrdinalIgnoreCase)
            ? TimeZoneInfo.Utc
            : TimeZoneInfo.FindSystemTimeZoneById(timezone);
}
