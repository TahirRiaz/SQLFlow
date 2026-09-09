namespace SqlFlow.Core.Acquire;

/// <summary>
/// The single ordering rule for acquisition watermarks. Two values that both parse as integers compare NUMERICALLY;
/// anything else falls back to an ordinal compare (ISO-8601 timestamps and zero-padded ids order correctly that way).
/// The numeric case is not a nicety: keyset ids grow past digit boundaries, where an ordinal compare ranks "9999999"
/// above "14526632" and the resume point silently walks backwards, re-fetching history every run. Every place that
/// advances or selects a maximum watermark goes through here, so the run, the run history, and the lake all agree.
/// </summary>
public static class WatermarkOrder
{
    /// <summary>True when <paramref name="candidate"/> orders strictly after <paramref name="current"/>.</summary>
    public static bool IsGreater(string candidate, string current)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(current);

        return long.TryParse(candidate, out var candidateId) && long.TryParse(current, out var currentId)
            ? candidateId > currentId
            : string.CompareOrdinal(candidate, current) > 0;
    }

    /// <summary>The greater of the two, treating null/empty as "no value" so a seedless first run still yields the
    /// candidate.</summary>
    public static string? Max(string? current, string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return current;
        }

        return string.IsNullOrEmpty(current) || IsGreater(candidate, current) ? candidate : current;
    }
}
