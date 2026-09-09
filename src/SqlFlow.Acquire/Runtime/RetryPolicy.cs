using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Runtime;

/// <summary>The outcome of a retry decision: stop (surface the failure) or wait the given delay and retry.</summary>
public readonly record struct RetryDecision(bool ShouldRetry, TimeSpan Delay)
{
    public static readonly RetryDecision Stop = new(false, TimeSpan.Zero);

    public static RetryDecision Retry(TimeSpan delay) => new(true, delay);
}

/// <summary>
/// Exponential backoff with a retryable-status allowlist and <c>Retry-After</c> honoring. Transport failures (no
/// status) and the transient statuses 408/425/429/500/502/503/504 retry up to the attempt cap; every other status
/// is permanent. <c>Retry-After</c> is treated as a <b>floor</b> raised to (never below) the computed backoff and
/// bounded by the max delay, so a <c>Retry-After: 0</c> from a CDN edge cannot spin and a huge value cannot stall.
/// </summary>
public sealed class RetryPolicy
{
    private static readonly HashSet<int> Retryable = [408, 425, 429, 500, 502, 503, 504];

    private readonly AcquireRetry _config;
    private readonly TimeProvider _time;

    public RetryPolicy(AcquireRetry config, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(time);
        _config = config;
        _time = time;
    }

    /// <param name="attempt">1-based attempt that just failed.</param>
    /// <param name="status">The HTTP status, or null for a transport failure (DNS/TCP/TLS/timeout).</param>
    /// <param name="headers">Response headers, for <c>Retry-After</c>; null for a transport failure.</param>
    public RetryDecision Next(int attempt, HttpStatusCode? status, HttpResponseHeaders? headers)
    {
        if (attempt >= _config.MaxAttempts)
        {
            return RetryDecision.Stop;
        }

        var code = status is { } s ? (int)s : (int?)null;
        if (code is { } c && !Retryable.Contains(c))
        {
            return RetryDecision.Stop;
        }

        var backoff = Backoff(attempt);
        if (_config.HonorRetryAfter && headers?.RetryAfter is { } retryAfter && ParseRetryAfter(retryAfter) is { } floor)
        {
            backoff = floor > backoff ? floor : backoff;
        }

        var max = TimeSpan.FromMilliseconds(_config.MaxDelayMs);
        return RetryDecision.Retry(backoff > max ? max : backoff);
    }

    private TimeSpan Backoff(int attempt)
    {
        // base * 2^(attempt-1), clamped so the shift cannot overflow and the result cannot exceed the max delay.
        var exponent = Math.Min(attempt - 1, 20);
        var millis = (double)_config.BaseDelayMs * Math.Pow(2, exponent);
        var capped = Math.Min(millis, _config.MaxDelayMs);
        return TimeSpan.FromMilliseconds(capped);
    }

    private TimeSpan? ParseRetryAfter(RetryConditionHeaderValue retryAfter)
    {
        if (retryAfter.Delta is { } delta)
        {
            return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        }

        return null;
    }

    /// <summary>Parses a raw <c>Retry-After</c> header value (delta-seconds or HTTP-date) for callers holding the string form.</summary>
    public static bool TryParseHeader(string value, TimeProvider time, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return true;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - time.GetUtcNow();
            delay = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
            return true;
        }

        return false;
    }
}
