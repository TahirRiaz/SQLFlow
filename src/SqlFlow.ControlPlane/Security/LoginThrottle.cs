using System.Collections.Concurrent;

namespace SqlFlow.ControlPlane.Security;

/// <summary>
/// A per-node, in-memory brake on password guessing: after <see cref="MaxFailures"/> consecutive failures for the
/// same (username, client IP) pair within the tracking window, sign-in attempts for that pair are refused for
/// <see cref="LockoutSeconds"/>. This complements the global per-IP rate limiter (which caps request volume but
/// not targeted guessing) and is deliberately in-memory: it needs no schema, resets on restart, and in a
/// multi-node deployment each node brakes independently, which still caps the aggregate guess rate per node.
/// A successful sign-in clears the pair's history.
/// </summary>
public sealed class LoginThrottle
{
    public const int MaxFailures = 5;

    public const int LockoutSeconds = 300;

    /// <summary>Failures older than this no longer count toward the threshold.</summary>
    public const int WindowSeconds = 900;

    /// <summary>A hard cap on tracked pairs so a spray across many usernames cannot balloon memory; when full,
    /// expired entries are evicted and, if none are expired, the oldest entries are dropped (fail-open for the
    /// dropped pair, which the rate limiter still covers).</summary>
    private const int MaxEntries = 100_000;

    private sealed record Entry(int Failures, DateTime FirstFailureUtc, DateTime LastFailureUtc);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>Whether an attempt for this pair is currently refused, without recording anything.</summary>
    public bool IsLockedOut(string username, string clientKey, DateTime nowUtc)
    {
        var key = Key(username, clientKey);
        if (!_entries.TryGetValue(key, out var entry))
        {
            return false;
        }

        if (Expired(entry, nowUtc))
        {
            _entries.TryRemove(key, out _);
            return false;
        }

        return entry.Failures >= MaxFailures
            && nowUtc < entry.LastFailureUtc.AddSeconds(LockoutSeconds);
    }

    /// <summary>Records a failed attempt for the pair.</summary>
    public void RecordFailure(string username, string clientKey, DateTime nowUtc)
    {
        EvictIfFull(nowUtc);
        var key = Key(username, clientKey);
        _entries.AddOrUpdate(
            key,
            _ => new Entry(1, nowUtc, nowUtc),
            (_, entry) => Expired(entry, nowUtc)
                ? new Entry(1, nowUtc, nowUtc)
                : entry with { Failures = entry.Failures + 1, LastFailureUtc = nowUtc });
    }

    /// <summary>Clears the pair's failure history after a successful sign-in.</summary>
    public void RecordSuccess(string username, string clientKey)
        => _entries.TryRemove(Key(username, clientKey), out _);

    private static bool Expired(Entry entry, DateTime nowUtc)
    {
        // A completed lockout, or a stale streak that never reached the threshold, no longer counts.
        var horizon = entry.Failures >= MaxFailures
            ? entry.LastFailureUtc.AddSeconds(LockoutSeconds)
            : entry.LastFailureUtc.AddSeconds(WindowSeconds);
        return nowUtc >= horizon;
    }

    private void EvictIfFull(DateTime nowUtc)
    {
        if (_entries.Count < MaxEntries)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (Expired(pair.Value, nowUtc))
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }

        if (_entries.Count < MaxEntries)
        {
            return;
        }

        // Nothing expired: drop the oldest streaks. Losing a tracked pair fails open for that pair only.
        foreach (var pair in _entries.OrderBy(p => p.Value.LastFailureUtc).Take(MaxEntries / 10))
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    private static string Key(string username, string clientKey)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{username.Trim().ToUpperInvariant()}|{clientKey}");
}
