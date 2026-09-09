namespace SqlFlow.Acquire.Runtime;

/// <summary>
/// A token-bucket rate limiter: <see cref="_capacity"/> tokens refilled continuously at <see cref="_rate"/>
/// tokens/second, one token per request. A non-positive rate is unlimited (a no-op). Fractional refill is capped at
/// capacity so an idle limiter cannot build a burst beyond the bucket. Uses the injected <see cref="TimeProvider"/>
/// so tests run under a paused clock. Thread-safe for the engine's bounded concurrency.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The SemaphoreSlim's wait handle is never accessed, so it holds no unmanaged resource to release; the limiter's lifetime is the run's HttpExecutor, and making it disposable would cascade IDisposable through the executor for no benefit.")]
public sealed class RateLimiter
{
    private readonly double _rate;
    private readonly double _capacity;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private double _tokens;
    private long _lastRefillTimestamp;

    public RateLimiter(double ratePerSecond, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _rate = ratePerSecond;
        _capacity = Math.Max(ratePerSecond, 1.0);
        _time = time;
        _tokens = _capacity;
        _lastRefillTimestamp = time.GetTimestamp();
    }

    /// <summary>Blocks until a token is available, then consumes it. Returns immediately when unlimited.</summary>
    public async Task AcquireAsync(CancellationToken ct = default)
    {
        if (_rate <= 0)
        {
            return;
        }

        while (true)
        {
            TimeSpan wait;
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                Refill();
                if (_tokens >= 1.0)
                {
                    _tokens -= 1.0;
                    return;
                }

                wait = TimeSpan.FromSeconds((1.0 - _tokens) / _rate);
            }
            finally
            {
                _gate.Release();
            }

            await Task.Delay(wait, _time, ct).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        var now = _time.GetTimestamp();
        var elapsed = _time.GetElapsedTime(_lastRefillTimestamp, now);
        _lastRefillTimestamp = now;
        _tokens = Math.Min(_capacity, _tokens + (elapsed.TotalSeconds * _rate));
    }
}
