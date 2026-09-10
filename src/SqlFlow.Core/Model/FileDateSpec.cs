using System.Globalization;
using System.Text.RegularExpressions;

namespace SqlFlow.Core.Model;

/// <summary>Where a file's business date is read from when a date window is applied during discovery.</summary>
public enum FileDateSource
{
    /// <summary>The file-system modified timestamp (the legacy behavior, and the default when no fileDate is set).</summary>
    Modified,

    /// <summary>A date encoded in the file's full path (e.g. Hive partitions <c>year=2025/month=03</c>).</summary>
    Path,

    /// <summary>A date encoded in the file's name (e.g. <c>orders_2025-03-01.csv</c>).</summary>
    Name,
}

/// <summary>
/// A half-open-free, fully-closed date interval <c>[Lo, Hi]</c> in UTC. A coarse partition (a whole year, a
/// whole month) is a wide interval; a single timestamp is a point where <c>Lo == Hi</c>. Discovery keeps a
/// file (or descends into a folder) when its interval overlaps the requested window, which is the correct
/// test for coarse partitions: a <c>year=2025</c> folder overlaps a window that starts mid-2025.
/// </summary>
public readonly record struct DateInterval(DateTime Lo, DateTime Hi);

/// <summary>
/// The parsed, validated <c>fileDate</c> discovery configuration: where a file's business date lives and how to
/// parse it. It turns a path or file name into a <see cref="DateInterval"/> so the discovery layer can prune whole
/// out-of-window partition folders before listing them, instead of enumerating a lake and discarding most of it.
/// <see cref="FileDateSource.Modified"/> is represented by a null spec (the caller uses the file timestamp), so a
/// present spec always means a path- or name-derived date.
/// </summary>
public sealed class FileDateSpec
{
    // Hive key=value token, e.g. year=2025 or month=03. The value stops at a path separator.
    private static readonly Regex HiveToken = new(
        @"(?<k>[A-Za-z][A-Za-z0-9_]*)=(?<v>[^/\\]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Regex? _pattern;
    private readonly bool _hive;

    private FileDateSpec(FileDateSource source, bool hive, Regex? pattern)
    {
        Source = source;
        _hive = hive;
        _pattern = pattern;
    }

    /// <summary>Where the date is read from.</summary>
    public FileDateSource Source { get; }

    /// <summary>True when the date is read from Hive <c>key=value</c> partition tokens in the path.</summary>
    public bool Hive => _hive;

    /// <summary>
    /// Parses the <c>fileDate.*</c> flat option keys. Returns null when <c>fileDate.from</c> is absent or
    /// <c>modified</c> (the default): discovery then keeps its legacy modified-timestamp behavior. A path or name
    /// source requires exactly one of <c>fileDate.hive</c> (Hive <c>key=value</c> tokens) or <c>fileDate.pattern</c>
    /// (a regex whose named groups <c>year/month/day/hour</c>, or unnamed groups in that order, yield the date).
    /// </summary>
    public static FileDateSpec? FromOptions(IReadOnlyDictionary<string, string?> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var from = Value(options, "fileDate.from");
        if (string.IsNullOrWhiteSpace(from) || from.Equals("modified", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var source = from.ToLowerInvariant() switch
        {
            "path" => FileDateSource.Path,
            "name" => FileDateSource.Name,
            _ => throw new SqlFlowException($"Invalid 'fileDate.from' value '{from}'. Use path, name, or modified."),
        };

        var hive = ParseBool(Value(options, "fileDate.hive"));
        var patternText = Value(options, "fileDate.pattern");
        var hasPattern = !string.IsNullOrWhiteSpace(patternText);

        if (hive && hasPattern)
        {
            throw new SqlFlowException("fileDate cannot set both 'fileDate.hive' and 'fileDate.pattern'; choose one.");
        }

        if (!hive && !hasPattern)
        {
            throw new SqlFlowException(
                "fileDate with from 'path' or 'name' needs either 'fileDate.hive: true' (Hive key=value tokens) "
                + "or 'fileDate.pattern' (a regex yielding year/month/day).");
        }

        if (hive && source == FileDateSource.Name)
        {
            throw new SqlFlowException("fileDate.hive applies to the path; use 'fileDate.pattern' for a name-derived date.");
        }

        Regex? pattern = null;
        if (hasPattern)
        {
            try
            {
                pattern = new Regex(patternText!, RegexOptions.CultureInvariant);
            }
            catch (ArgumentException ex)
            {
                throw new SqlFlowException($"Invalid 'fileDate.pattern' regular expression '{patternText}': {ex.Message}", ex);
            }
        }

        return new FileDateSpec(source, hive, pattern);
    }

    /// <summary>
    /// Extracts the date interval encoded in <paramref name="text"/> (a full path for <see cref="FileDateSource.Path"/>,
    /// a file name for <see cref="FileDateSource.Name"/>). Returns null when the text carries no recognizable date -
    /// used two ways: a folder with no date tokens yet is not prunable (descend), and a file with no date is not part
    /// of the dated selection (exclude). Missing finer components widen the interval (a bare <c>year=2025</c> spans
    /// the whole of 2025), so overlap tests stay correct for coarse partitions.
    /// </summary>
    public DateInterval? Extract(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var parts = _hive ? ParseHive(text) : ParsePattern(text);
        return Compose(parts);
    }

    /// <summary>
    /// The single instant the text encodes: the START of the interval <see cref="Extract"/> would return, or null
    /// when the text carries no recognizable date. This is the file's BUSINESS timestamp, and it is what
    /// <c>FileDate_DW</c> is stamped with when a flow configures a name/path date source.
    /// <para>
    /// It exists because an object store's last-modified time is not a durable property of the data: a
    /// server-side copy, a lifecycle tier move or a re-upload rewrites it, and it cannot be set back. A pipeline
    /// whose watermark and provenance rest on that timestamp silently replays its whole history the first time
    /// the files are moved, and cannot express a backfill window at all. A timestamp read out of the file's own
    /// name survives every one of those, so the watermark, the stored provenance and a <c>--from/--to</c>
    /// backfill all agree on one clock that belongs to the data rather than to the storage account.
    /// </para>
    /// A name that encodes only a date yields midnight; finer components are used when the pattern captures them.
    /// </summary>
    public DateTime? ExtractTimestamp(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        var parts = _hive ? ParseHive(text) : ParsePattern(text);
        return Compose(parts)?.Lo;
    }

    /// <summary>The date components a name or path yielded; a null component was not captured.</summary>
    private readonly record struct DateParts(int? Year, int? Month, int? Day, int? Hour, int? Minute, int? Second);

    private static DateParts ParseHive(string text)
    {
        int? year = null, month = null, day = null, hour = null, minute = null, second = null;
        foreach (Match token in HiveToken.Matches(text))
        {
            var key = token.Groups["k"].Value.ToLowerInvariant();
            var raw = token.Groups["v"].Value;
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            switch (key)
            {
                case "year" or "yyyy": year = value; break;
                case "month" or "mm": month = value; break;
                case "day" or "dd": day = value; break;
                case "hour" or "hh": hour = value; break;
                case "minute" or "min" or "mi": minute = value; break;
                case "second" or "sec" or "ss": second = value; break;
            }
        }

        return new DateParts(year, month, day, hour, minute, second);
    }

    private DateParts ParsePattern(string text)
    {
        var match = _pattern!.Match(text);
        if (!match.Success)
        {
            return default;
        }

        // Named groups win when present; otherwise the unnamed groups are read positionally as
        // year, month, day, hour, minute, second.
        var named = _pattern.GetGroupNames().Any(n => !int.TryParse(n, out _));
        if (named)
        {
            return new DateParts(
                NamedInt(match, "year", "y"),
                NamedInt(match, "month", "m"),
                NamedInt(match, "day", "d"),
                NamedInt(match, "hour", "h"),
                NamedInt(match, "minute", "mi"),
                NamedInt(match, "second", "ss"));
        }

        return new DateParts(
            PositionalInt(match, 1),
            PositionalInt(match, 2),
            PositionalInt(match, 3),
            PositionalInt(match, 4),
            PositionalInt(match, 5),
            PositionalInt(match, 6));
    }

    private static int? NamedInt(Match match, string primary, string alias)
    {
        var group = match.Groups[primary];
        if (!group.Success)
        {
            group = match.Groups[alias];
        }

        return group.Success && int.TryParse(group.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static int? PositionalInt(Match match, int index)
        => index < match.Groups.Count && match.Groups[index].Success
            && int.TryParse(match.Groups[index].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static DateInterval? Compose(DateParts parts)
    {
        var (year, month, day, hour, minute, second) = parts;

        if (year is null || year < 1 || year > 9999)
        {
            return null;
        }

        if (month is < 1 or > 12)
        {
            return null;
        }

        try
        {
            if (month is null)
            {
                var lo = new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return new DateInterval(lo, lo.AddYears(1).AddTicks(-1));
            }

            if (day is null)
            {
                var lo = new DateTime(year.Value, month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
                return new DateInterval(lo, lo.AddMonths(1).AddTicks(-1));
            }

            if (day < 1 || day > DateTime.DaysInMonth(year.Value, month.Value))
            {
                return null;
            }

            if (hour is null)
            {
                var lo = new DateTime(year.Value, month.Value, day.Value, 0, 0, 0, DateTimeKind.Utc);
                return new DateInterval(lo, lo.AddDays(1).AddTicks(-1));
            }

            if (hour is < 0 or > 23)
            {
                return null;
            }

            if (minute is null)
            {
                var loHour = new DateTime(year.Value, month.Value, day.Value, hour.Value, 0, 0, DateTimeKind.Utc);
                return new DateInterval(loHour, loHour.AddHours(1).AddTicks(-1));
            }

            if (minute is < 0 or > 59)
            {
                return null;
            }

            if (second is null)
            {
                var loMinute = new DateTime(year.Value, month.Value, day.Value, hour.Value, minute.Value, 0, DateTimeKind.Utc);
                return new DateInterval(loMinute, loMinute.AddMinutes(1).AddTicks(-1));
            }

            if (second is < 0 or > 59)
            {
                return null;
            }

            // A full timestamp is a one-second interval, not a point: a name that resolves to the second still
            // covers that second, so an overlap test against a window bounded at the same second includes it.
            var loSecond = new DateTime(
                year.Value, month.Value, day.Value, hour.Value, minute.Value, second.Value, DateTimeKind.Utc);
            return new DateInterval(loSecond, loSecond.AddSeconds(1).AddTicks(-1));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? Value(IReadOnlyDictionary<string, string?> options, string key)
        => options.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

    private static bool ParseBool(string? value) => bool.TryParse(value, out var b) && b;
}
