using System.Globalization;
using System.Text.RegularExpressions;
using SqlFlow.Core;

namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// Parses the relative and absolute time expressions a date-window iteration accepts, against a supplied "now" so
/// runs are deterministic and testable. Grammar: <c>now</c>, <c>today</c>, <c>yesterday</c>, <c>startOfMonth</c>,
/// <c>startOfDay</c>, an offset (<c>now-3d</c>, <c>now-2h</c>, <c>today-1d</c>, <c>startOfMonth-1mo</c>) with units
/// <c>y</c>|<c>mo</c>|<c>w</c>|<c>d</c>|<c>h</c>|<c>mi</c>, or an ISO-8601 date / datetime.
/// </summary>
public static partial class RelativeTime
{
    /// <summary>
    /// Resolves only the ANCHOR grammar (<c>now</c>, <c>today-1d</c>, <c>startOfMonth-1mo</c>), never the ISO-8601
    /// fallback, returning false for anything else. Template rendering uses this to tell a relative-date token from
    /// an ordinary variable name: a token like <c>{now-6mo:yyyy-MM-dd}</c> is a date expression, while
    /// <c>{operatorId}</c> is a variable that must still fail loudly when it is unbound.
    /// </summary>
    public static bool TryResolveAnchor(string expression, DateTimeOffset now, out DateTimeOffset resolved)
    {
        resolved = default;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var match = Offset().Match(expression.Trim());
        if (!match.Success)
        {
            return false;
        }

        var anchor = Anchor(match.Groups["anchor"].Value, now);
        if (!match.Groups["delta"].Success)
        {
            resolved = anchor;
            return true;
        }

        var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
        resolved = Apply(anchor, sign * int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture), match.Groups["unit"].Value);
        return true;
    }

    public static DateTimeOffset Resolve(string expression, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(expression);
        var text = expression.Trim();

        var match = Offset().Match(text);
        if (match.Success)
        {
            var anchor = Anchor(match.Groups["anchor"].Value, now);
            if (!match.Groups["delta"].Success)
            {
                return anchor;
            }

            var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
            var amount = sign * int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
            return Apply(anchor, amount, match.Groups["unit"].Value);
        }

        if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var absolute))
        {
            return absolute;
        }

        throw new SqlFlowException(
            $"Could not parse the time expression '{expression}'. Use 'now', 'today', 'yesterday', 'startOfMonth', " +
            "an offset like 'now-3d' / 'startOfMonth-1mo' (units y|mo|w|d|h|mi), or an ISO-8601 date.");
    }

    private static DateTimeOffset Anchor(string anchor, DateTimeOffset now) => anchor.ToLowerInvariant() switch
    {
        "now" => now,
        "today" or "startofday" => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero),
        "yesterday" => new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddDays(-1),
        "startofmonth" => new DateTimeOffset(new DateTime(now.UtcDateTime.Year, now.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc), TimeSpan.Zero),
        _ => throw new SqlFlowException($"Unknown time anchor '{anchor}'."),
    };

    private static DateTimeOffset Apply(DateTimeOffset anchor, int amount, string unit) => unit.ToLowerInvariant() switch
    {
        "y" => anchor.AddYears(amount),
        "mo" => anchor.AddMonths(amount),
        "w" => anchor.AddDays(amount * 7),
        "d" => anchor.AddDays(amount),
        "h" => anchor.AddHours(amount),
        "mi" => anchor.AddMinutes(amount),
        _ => throw new SqlFlowException($"Unknown time unit '{unit}'. Use y, mo, w, d, h, or mi."),
    };

    [GeneratedRegex(@"^(?<anchor>now|today|yesterday|startOfMonth|startOfDay)(?<delta>(?<sign>[+-])(?<n>\d+)(?<unit>y|mo|w|d|h|mi))?$", RegexOptions.IgnoreCase)]
    private static partial Regex Offset();
}
