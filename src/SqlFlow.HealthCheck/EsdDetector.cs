namespace SqlFlow.HealthCheck;

/// <summary>One ESD verdict: the index the test examined and the severity it measured.</summary>
public sealed record EsdAnomaly
{
    /// <summary>The index into the residual array handed to the detector.</summary>
    public required int Index { get; init; }

    /// <summary>The robust studentized deviation (|x - median| / MAD-sigma) at the moment of the test.</summary>
    public required double Severity { get; init; }
}

/// <summary>
/// The generalized ESD test (Rosner 1983) with robust studentization: the strongest known answer to "which of
/// these points are statistical outliers, and how sure are we". Classic z-thresholds fail two ways on real
/// warehouse series: one giant spike inflates the spread and masks smaller ones (masking), and testing many
/// points at a fixed threshold mass-produces false positives (multiplicity). ESD fixes both: it tests up to k
/// candidates by repeatedly removing the most extreme point and recomputing, comparing each deviation against
/// a Student-t critical value that accounts for how many tests have run. Studentizing with median/MAD instead
/// of mean/stddev (the S-H-ESD refinement that became the industry standard) keeps the spread estimate itself
/// uncontaminated. Deterministic, O(k·n).
/// </summary>
public static class EsdDetector
{
    /// <summary>
    /// Finds the statistically significant outliers among <paramref name="values"/>.
    /// </summary>
    /// <param name="values">The residual series (actual minus expected), one entry per tested point.</param>
    /// <param name="alpha">The significance level of each outward test (the false-positive control);
    /// 0.025 flags a point only when a deviation this large has under 2.5% probability in a clean series.</param>
    /// <param name="maxAnomalyFraction">The ESD upper bound k as a fraction of n: the test cannot, by
    /// construction, flag more than this share of the series.</param>
    public static IReadOnlyList<EsdAnomaly> Detect(IReadOnlyList<double> values, double alpha, double maxAnomalyFraction)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (alpha <= 0 || alpha >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be strictly between 0 and 1.");
        }

        if (maxAnomalyFraction <= 0 || maxAnomalyFraction > 0.49)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAnomalyFraction), maxAnomalyFraction,
                "maxAnomalyFraction must be in (0, 0.49]: a robust test cannot call half the series anomalous.");
        }

        var n = values.Count;
        var maxAnomalies = Math.Min((int)Math.Ceiling(n * maxAnomalyFraction), n - 3);
        if (n < 4 || maxAnomalies < 1)
        {
            // Below four points there is no degrees-of-freedom room for a t-based verdict.
            return [];
        }

        var remaining = Enumerable.Range(0, n).ToList();
        var candidates = new List<EsdAnomaly>(maxAnomalies);

        for (var test = 1; test <= maxAnomalies; test++)
        {
            var sample = remaining.Select(i => values[i]).ToList();
            var median = RobustStatistics.Median(sample);
            var scale = RobustStatistics.Scale(sample);

            // The most extreme remaining point under the robust studentization.
            var extremePosition = 0;
            double extremeDeviation = -1;
            for (var pos = 0; pos < remaining.Count; pos++)
            {
                var deviation = Math.Abs(values[remaining[pos]] - median);
                if (deviation > extremeDeviation)
                {
                    extremeDeviation = deviation;
                    extremePosition = pos;
                }
            }

            var severity = RobustStatistics.Z(values[remaining[extremePosition]], median, scale);
            candidates.Add(new EsdAnomaly { Index = remaining[extremePosition], Severity = severity });
            remaining.RemoveAt(extremePosition);
        }

        // Rosner's critical values: lambda_i for the i-th outward test. The LAST test that exceeds its
        // critical value fixes the anomaly count (this is what defeats masking: an early non-significant
        // test does not stop the search).
        var significantCount = 0;
        for (var i = 1; i <= candidates.Count; i++)
        {
            var nRemaining = n - i + 1;
            var df = nRemaining - 2;
            if (df < 1)
            {
                break;
            }

            var p = 1 - alpha / (2 * nRemaining);
            var t = StudentT.InverseCdf(p, df);
            var lambda = (nRemaining - 1) * t / Math.Sqrt((df + t * t) * nRemaining);

            if (candidates[i - 1].Severity > lambda)
            {
                significantCount = i;
            }
        }

        return candidates.Take(significantCount).ToList();
    }
}
