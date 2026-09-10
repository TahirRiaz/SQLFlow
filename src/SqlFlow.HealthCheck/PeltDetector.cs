namespace SqlFlow.HealthCheck;

/// <summary>One detected level shift: the first index of the new regime and the medians on each side.</summary>
public sealed record LevelShift
{
    /// <summary>The index (into the analyzed series) of the first point AFTER the change.</summary>
    public required int Index { get; init; }

    public required double MedianBefore { get; init; }

    public required double MedianAfter { get; init; }

    /// <summary>The jump in robust sigmas of the whole series: how violent the regime change was.</summary>
    public required double MagnitudeSigma { get; init; }
}

/// <summary>
/// PELT (Pruned Exact Linear Time, Killick et al. 2012), the best-known exact change-point method, with a
/// robust L1 segment cost (sum of absolute deviations from the segment median). This is what separates "the
/// table got backfilled / a retention purge halved the volume / a new source doubled it" from point
/// anomalies: a level shift is a REGIME, and flagging it day by day as anomalies both buries the signal and
/// exhausts the anomaly budget. The L1 cost keeps single spikes from masquerading as shifts; the BIC-style
/// penalty (penaltyFactor * sigma * ln n per change) decides when a split genuinely pays for itself.
/// Exact dynamic programming with PELT pruning; deterministic.
/// </summary>
public static class PeltDetector
{
    /// <summary>Points a segment must have on each side of a change: one full weekly cycle, so weekday
    /// rhythm cannot be mistaken for a regime change.</summary>
    public const int MinSegmentLength = 7;

    /// <summary>Finds the level shifts in <paramref name="values"/> (typically the prediction residuals).</summary>
    /// <param name="values">The analyzed series, in time order.</param>
    /// <param name="penaltyFactor">How much robust-sigma evidence a change must buy per ln(n); 3.0 is
    /// conservative (few false regimes), lower is more eager.</param>
    public static IReadOnlyList<LevelShift> Detect(IReadOnlyList<double> values, double penaltyFactor = 3.0)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(penaltyFactor);

        var n = values.Count;
        if (n < 2 * MinSegmentLength)
        {
            return [];
        }

        // The noise scale must be shift-immune: the global scale of a shifted series is inflated by the very
        // regimes the detector is looking for, which would both dull the penalty and understate every
        // magnitude. Deviations from a one-cycle rolling median estimate the marginal noise while a level
        // shift only pollutes the few boundary points, which the robust scale shrugs off.
        var globalScale = RobustStatistics.Scale(values.ToList());
        if (globalScale == 0)
        {
            // A truly constant series has no regimes to find.
            return [];
        }

        // A zero NOISE scale on a non-constant series means the structure is all signal (a perfectly clean
        // step): any shift is then maximally significant, so a tiny effective sigma keeps the penalty real
        // without letting noise-free regimes hide.
        var sigma = RobustNoiseScale(values);
        if (sigma == 0)
        {
            sigma = globalScale * 1e-3;
        }

        var penalty = penaltyFactor * sigma * Math.Log(n);

        // F[t] = minimal total cost of segmenting values[0..t); cp[t] = the change point that starts the last
        // segment on that optimal path. F[0] = -penalty so the mandatory first segment pays no penalty.
        var f = new double[n + 1];
        var cp = new int[n + 1];
        Array.Fill(f, double.PositiveInfinity);
        f[0] = -penalty;
        var candidates = new List<int> { 0 };

        for (var t = MinSegmentLength; t <= n; t++)
        {
            // Minimize over the surviving candidate starts whose final segment would be long enough,
            // remembering each candidate's path cost for the pruning pass below.
            var pathCosts = new List<(int Start, double PathCost)>(candidates.Count);
            foreach (var s in candidates)
            {
                if (t - s < MinSegmentLength || double.IsPositiveInfinity(f[s]))
                {
                    continue;
                }

                var pathCost = f[s] + SegmentCost(values, s, t);
                pathCosts.Add((s, pathCost));
                if (pathCost + penalty < f[t])
                {
                    f[t] = pathCost + penalty;
                    cp[t] = s;
                }
            }

            // PELT pruning: a start whose path already costs more than the chosen optimum can never win at
            // any later t (the L1 cost is subadditive), so it leaves the candidate set for good.
            foreach (var (start, pathCost) in pathCosts)
            {
                if (pathCost > f[t])
                {
                    candidates.Remove(start);
                }
            }

            // t becomes a potential change point for future ends, when a segment can still fit after it.
            if (t <= n - MinSegmentLength && !double.IsPositiveInfinity(f[t]))
            {
                candidates.Add(t);
            }
        }

        // Walk the optimal path back to the change points.
        var changes = new List<int>();
        for (var t = n; cp[t] > 0; t = cp[t])
        {
            changes.Add(cp[t]);
        }

        changes.Sort();

        var shifts = new List<LevelShift>(changes.Count);
        var previousStart = 0;
        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            var nextEnd = i + 1 < changes.Count ? changes[i + 1] : n;
            var before = RobustStatistics.Median(Slice(values, previousStart, change));
            var after = RobustStatistics.Median(Slice(values, change, nextEnd));
            shifts.Add(new LevelShift
            {
                Index = change,
                MedianBefore = before,
                MedianAfter = after,
                MagnitudeSigma = Math.Abs(after - before) / sigma,
            });
            previousStart = change;
        }

        return shifts;
    }

    /// <summary>The robust marginal noise scale: MAD-sigma of the deviations from a one-cycle
    /// (<see cref="MinSegmentLength"/>-point) rolling median. Level shifts only touch the boundary points,
    /// so the estimate stays honest on the exact series the detector exists for.</summary>
    public static double RobustNoiseScale(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count < 2)
        {
            return 0;
        }

        var half = MinSegmentLength / 2;
        var deviations = new List<double>(values.Count);
        for (var i = 0; i < values.Count; i++)
        {
            var start = Math.Max(0, i - half);
            var end = Math.Min(values.Count, i + half + 1);
            deviations.Add(values[i] - RobustStatistics.Median(Slice(values, start, end)));
        }

        return RobustStatistics.Scale(deviations);
    }

    private static List<double> Slice(IReadOnlyList<double> values, int start, int end)
    {
        var slice = new List<double>(end - start);
        for (var i = start; i < end; i++)
        {
            slice.Add(values[i]);
        }

        return slice;
    }

    /// <summary>The robust L1 cost of values[start..end): total absolute deviation from the segment median.</summary>
    private static double SegmentCost(IReadOnlyList<double> values, int start, int end)
    {
        var slice = Slice(values, start, end);
        var median = RobustStatistics.Median(slice);
        double cost = 0;
        foreach (var v in slice)
        {
            cost += Math.Abs(v - median);
        }

        return cost;
    }
}
