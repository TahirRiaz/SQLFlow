namespace SqlFlow.HealthCheck;

/// <summary>A fitted robust linear trend: expected value = Intercept + SlopePerDay * days-since-anchor.</summary>
public sealed record TrendFit
{
    /// <summary>The date the day index counts from (day 0).</summary>
    public required DateTime AnchorDate { get; init; }

    public required double SlopePerDay { get; init; }

    public required double Intercept { get; init; }

    /// <summary>How many trailing points the fit used.</summary>
    public required int WindowPoints { get; init; }

    public double ValueAt(DateTime date) => Intercept + SlopePerDay * (date.Date - AnchorDate).TotalDays;
}

/// <summary>
/// The Theil-Sen estimator: the median of all pairwise slopes, with the intercept as the median residual. The
/// canonical robust trend fit (it shrugs off up to ~29% contaminated points), and the piece that makes the
/// detector honest on GROWING tables: a least-squares or flat baseline on a table adding rows every day either
/// chases the anomalies or tags healthy growth as anomalous. The fit uses a trailing window (recent regime,
/// not ancient history) and full pairwise slopes up to a budget, sampled deterministically past it.
/// </summary>
public static class TrendEstimator
{
    /// <summary>The default trailing window: about two quarters of daily data, enough for a stable slope
    /// without dragging in last year's regime.</summary>
    public const int DefaultWindowPoints = 180;

    private const int MaxSlopePairs = 50_000;

    /// <summary>Fits the robust trend over the trailing <paramref name="windowPoints"/> of
    /// <paramref name="points"/> (date-ordered). With fewer than 2 points in the window the trend is flat at
    /// the window median (slope 0): a short series earns no extrapolation.</summary>
    public static TrendFit Fit(IReadOnlyList<(DateTime Date, double Value)> points, int windowPoints = DefaultWindowPoints)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentOutOfRangeException.ThrowIfLessThan(windowPoints, 2);
        if (points.Count == 0)
        {
            throw new ArgumentException("A trend fit needs at least one point.", nameof(points));
        }

        var window = points.Skip(Math.Max(0, points.Count - windowPoints)).ToList();
        var anchor = window[0].Date.Date;
        var xs = window.Select(p => (p.Date.Date - anchor).TotalDays).ToList();
        var ys = window.Select(p => p.Value).ToList();

        if (window.Count < 2 || xs.Distinct().Count() < 2)
        {
            return new TrendFit
            {
                AnchorDate = anchor,
                SlopePerDay = 0,
                Intercept = RobustStatistics.Median(ys),
                WindowPoints = window.Count,
            };
        }

        var slopes = PairwiseSlopes(xs, ys);
        var slope = RobustStatistics.Median(slopes);
        var intercept = RobustStatistics.Median(window.Select((p, i) => ys[i] - slope * xs[i]).ToList());

        return new TrendFit
        {
            AnchorDate = anchor,
            SlopePerDay = slope,
            Intercept = intercept,
            WindowPoints = window.Count,
        };
    }

    private static List<double> PairwiseSlopes(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
    {
        var n = xs.Count;
        var totalPairs = (long)n * (n - 1) / 2;
        var slopes = new List<double>((int)Math.Min(totalPairs, MaxSlopePairs));

        if (totalPairs <= MaxSlopePairs)
        {
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (xs[j] != xs[i])
                    {
                        slopes.Add((ys[j] - ys[i]) / (xs[j] - xs[i]));
                    }
                }
            }
        }
        else
        {
            // Deterministic stride sampling over the pair triangle: same inputs, same fit, no RNG.
            var stride = (totalPairs + MaxSlopePairs - 1) / MaxSlopePairs;
            long pair = 0;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++, pair++)
                {
                    if (pair % stride == 0 && xs[j] != xs[i])
                    {
                        slopes.Add((ys[j] - ys[i]) / (xs[j] - xs[i]));
                    }
                }
            }
        }

        return slopes;
    }
}
