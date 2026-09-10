using System.Text.Json;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Core.Acquire;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Tracks the incremental watermark across a run: seeded from the prior run's value (or the configured seed), read
/// back into the request context so the first request resumes from it, and advanced to the lexicographic max of the
/// configured response column observed across pages. The advance is only committed to the next run when the whole
/// run succeeds, so a partial failure re-fetches the same window (downstream MERGE dedupes the overlap).
/// </summary>
public sealed class WatermarkState
{
    private readonly AcquireIncremental? _config;
    // A concurrent fan-out observes pages from several fetches at once; guard the max-advance compare-and-set.
    private readonly Lock _sync = new();

    public WatermarkState(AcquireIncremental? config, string? priorValue)
    {
        _config = config;
        Before = string.IsNullOrWhiteSpace(priorValue) ? config?.Seed : priorValue;
        Current = Before;
    }

    /// <summary>The watermark this run resumes from (prior run's value, or the seed).</summary>
    public string? Before { get; }

    /// <summary>The max observed so far (the candidate next watermark).</summary>
    public string? Current { get; private set; }

    /// <summary>The response column tracked, or null when no response watermark is configured.</summary>
    public string? Column => _config?.Source == AcquireWatermarkSource.Response ? _config.Column : null;

    /// <summary>Observe a page: advance the watermark to the max of the tracked column across its records.</summary>
    public void Observe(JsonElement page, string? recordsPath)
    {
        if (Column is not { } column)
        {
            return;
        }

        var max = JsonPathReader.MaxColumn(page, column, recordsPath);
        if (max is null)
        {
            return;
        }

        lock (_sync)
        {
            Current = WatermarkOrder.Max(Current, max);
        }
    }

    /// <summary>Observe a keyset id taken from a response header (a binary-body feed has no JSON to read the id
    /// from). The id is the resume watermark directly, so it advances regardless of the configured response column.
    /// Ordering is <see cref="WatermarkOrder"/>'s, so an id that grows past a digit boundary still advances.</summary>
    public void ObserveId(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate))
        {
            return;
        }

        lock (_sync)
        {
            Current = WatermarkOrder.Max(Current, candidate);
        }
    }
}
