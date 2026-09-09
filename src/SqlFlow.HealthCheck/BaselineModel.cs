namespace SqlFlow.HealthCheck;

/// <summary>
/// The expectation model for series too short to train AutoML on: the robust median per weekday, falling back
/// to the overall median for weekdays not yet observed. "Plug it into any table and get results" includes the
/// table created last week: a brand-new entity still gets missing-data and gross-deviation detection from day
/// two, and graduates to the trained model as history accumulates. Medians, not means, so a bad day in a
/// five-day history does not poison the expectation.
/// </summary>
public sealed class BaselineModel
{
    /// <summary>The trainer name the report carries when the baseline scored the series.</summary>
    public const string TrainerName = "WeekdayMedianBaseline";

    private readonly Dictionary<DayOfWeek, double> _weekdayMedians;
    private readonly double _overallMedian;

    private BaselineModel(Dictionary<DayOfWeek, double> weekdayMedians, double overallMedian)
    {
        _weekdayMedians = weekdayMedians;
        _overallMedian = overallMedian;
    }

    /// <summary>
    /// Fits the baseline on the observed (non-imputed) points.
    /// </summary>
    /// <param name="series">The scored series.</param>
    /// <param name="value">Which number to take the weekday medians of; the observed value by default. A
    /// caller that has already removed a robust trend passes the DETRENDED value instead, so the baseline
    /// learns the weekday pattern AROUND the growth rather than mistaking the growth for a Friday effect.
    /// This is the same composition the trained path uses, where the trend carries the level and the model
    /// learns the calendar shape on top of it.</param>
    public static BaselineModel Fit(IReadOnlyList<SeriesRow> series, Func<SeriesRow, double>? value = null)
    {
        ArgumentNullException.ThrowIfNull(series);
        var select = value ?? (r => r.BaseValue);

        var observed = series.Where(r => r.IsNoData == 0).ToList();
        if (observed.Count == 0)
        {
            throw new InvalidOperationException("The baseline needs at least one observed point.");
        }

        var weekdayMedians = observed
            .GroupBy(r => r.Date.DayOfWeek)
            .ToDictionary(
                g => g.Key,
                g => RobustStatistics.Median(g.Select(select).ToList()));
        var overall = RobustStatistics.Median(observed.Select(select).ToList());

        return new BaselineModel(weekdayMedians, overall);
    }

    public double Predict(DateTime date)
        => _weekdayMedians.TryGetValue(date.DayOfWeek, out var median) ? median : _overallMedian;
}
