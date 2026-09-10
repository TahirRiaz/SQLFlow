using System.Globalization;
using SqlFlow.Core.HealthChecks;

namespace SqlFlow.HealthCheck;

/// <summary>
/// The pure (no IO, no ML context) analysis stages of a health check, ported faithfully from the legacy
/// ExecMLHealthCheck: frequency detection, missing-date completion, calendar featurization, weekday/seasonal
/// imputation, adaptive anomaly tagging, and fit metrics. Every method mutates or reads the
/// <see cref="SeriesRow"/> list the way the legacy passes did, so the scored output is comparable run-for-run
/// with the legacy engine's.
/// </summary>
public static class HealthCheckEngine
{
    /// <summary>The centered moving-average window (days) used as the imputation fallback.</summary>
    public const int MovingAverageWindow = 7;

    /// <summary>Actual points below which anomaly tagging is skipped (the thresholds would be noise).</summary>
    public const int MinimumPointsForTagging = 3;

    /// <summary>Actual points below which fit metrics are withheld.</summary>
    public const int MinimumPointsForMetrics = 10;

    /// <summary>Detects the series cadence as the most common gap between consecutive dates (ties resolved
    /// toward the smaller gap). Requires at least two dates.</summary>
    public static DataFrequency DetectFrequency(IReadOnlyList<DateTime> dates)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.Count < 2)
        {
            throw new ArgumentException("Frequency detection needs at least two dates.", nameof(dates));
        }

        var ordered = dates.OrderBy(d => d).ToList();
        var gaps = new List<double>(ordered.Count - 1);
        for (var i = 1; i < ordered.Count; i++)
        {
            gaps.Add((ordered[i] - ordered[i - 1]).TotalDays);
        }

        var mostCommon = gaps
            .GroupBy(g => g)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .First().Key;

        return mostCommon switch
        {
            < 1.5 => DataFrequency.Daily,
            >= 1.5 and < 2.5 => DataFrequency.BiDaily,
            >= 2.5 and < 8 => DataFrequency.Weekly,
            >= 8 and < 15 => DataFrequency.BiWeekly,
            >= 15 and < 32 => DataFrequency.Monthly,
            >= 32 and < 100 => DataFrequency.Quarterly,
            >= 100 and < 200 => DataFrequency.HalfYearly,
            >= 200 and < 400 => DataFrequency.Yearly,
            _ => DataFrequency.Irregular,
        };
    }

    /// <summary>
    /// The dates the cadence expects but the series lacks: the gaps between the first and last observed date,
    /// PLUS the trailing dates between the last observed date and <paramref name="expectThrough"/>. The
    /// trailing walk is what catches the deadliest warehouse failure, the pipeline that simply stopped: a
    /// table whose last load was four days ago has no gap INSIDE its series, only after it. Irregular
    /// cadences walk daily, the legacy behavior.
    /// </summary>
    public static List<DateTime> DetectMissingDates(IReadOnlyList<DateTime> dates, DataFrequency frequency, DateTime? expectThrough = null)
    {
        ArgumentNullException.ThrowIfNull(dates);
        if (dates.Count == 0)
        {
            throw new ArgumentException("Missing-date detection needs a non-empty series.", nameof(dates));
        }

        var ordered = dates.OrderBy(d => d).ToList();
        var present = new HashSet<DateTime>(ordered.Select(d => d.Date));
        var missing = new List<DateTime>();

        var end = expectThrough?.Date is { } through && through > ordered[^1].Date ? through : ordered[^1].Date;
        for (var date = ordered[0].Date; date <= end; date = NextDate(date, frequency))
        {
            if (!present.Contains(date))
            {
                missing.Add(date);
            }
        }

        return missing;
    }

    /// <summary>Appends the missing dates as zero-valued, <see cref="SeriesRow.IsNoData"/>-flagged rows and
    /// re-sorts the series chronologically.</summary>
    public static void AddMissingDates(List<SeriesRow> series, IReadOnlyList<DateTime> missingDates)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(missingDates);

        foreach (var date in missingDates)
        {
            series.Add(new SeriesRow { Date = date, BaseValue = 0, IsNoData = 1 });
        }

        series.Sort((a, b) => a.Date.CompareTo(b.Date));
    }

    /// <summary>
    /// Fills the seven calendar features of every row, computed in-engine (the without-database replacement
    /// for the legacy flw.SysDateFeatures lookup): year, quarter, ISO 8601 week, month, weekday (1=Sunday),
    /// weekend, and holiday membership in <paramref name="holidays"/>.
    /// </summary>
    public static void ApplyDateFeatures(List<SeriesRow> series, IReadOnlyCollection<DateOnly> holidays)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentNullException.ThrowIfNull(holidays);

        var holidaySet = holidays as IReadOnlySet<DateOnly> ?? new HashSet<DateOnly>(holidays);
        foreach (var row in series)
        {
            var date = row.Date.Date;
            row.Year = date.Year;
            row.Quarter = (float)Math.Ceiling(date.Month / 3.0);
            row.WeekOfYear = ISOWeek.GetWeekOfYear(date);
            row.MonthNumber = date.Month;
            row.DayOfWeekNumber = (float)date.DayOfWeek + 1;
            row.IsWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 1 : 0;
            row.IsHoliday = holidaySet.Contains(DateOnly.FromDateTime(date)) ? 1 : 0;
        }
    }

    /// <summary>
    /// Sets every row's training label (<see cref="SeriesRow.BaseValueAdjusted"/>): the observed value for a
    /// real point; for a zero/missing point, the weekday average (seasonally adjusted once two months of data
    /// exist), falling back to the centered moving average, then the overall average. Rows imputed here are
    /// flagged <see cref="SeriesRow.IsNoData"/>. Requires at least one nonzero observation.
    /// </summary>
    public static void Impute(List<SeriesRow> series, int movingAverageWindow = MovingAverageWindow)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(movingAverageWindow, 1);

        series.Sort((a, b) => a.Date.CompareTo(b.Date));

        var valid = series.Where(r => r.BaseValue > 0).ToList();
        if (valid.Count == 0)
        {
            throw new InvalidOperationException(
                "The series has no nonzero values to learn from; a health check needs at least one observed value.");
        }

        var weekdayAverages = valid
            .GroupBy(r => r.Date.DayOfWeek)
            .ToDictionary(g => g.Key, g => g.Average(r => r.BaseValue));

        var movingAverages = MovingAverages(series, movingAverageWindow);

        // Monthly seasonality only once roughly two months of observations exist, the legacy guard.
        Dictionary<int, float>? monthlyAverages = null;
        if (valid.Count >= 60)
        {
            monthlyAverages = valid
                .GroupBy(r => r.Date.Month)
                .ToDictionary(g => g.Key, g => g.Average(r => r.BaseValue));
        }

        for (var i = 0; i < series.Count; i++)
        {
            var row = series[i];
            if (row.BaseValue == 0)
            {
                float imputed;
                if (weekdayAverages.TryGetValue(row.Date.DayOfWeek, out var weekdayAverage))
                {
                    imputed = weekdayAverage;
                    if (monthlyAverages is not null && monthlyAverages.TryGetValue(row.Date.Month, out var monthlyAverage))
                    {
                        var overallMonthly = monthlyAverages.Values.Average();
                        if (overallMonthly > 0)
                        {
                            imputed *= monthlyAverage / overallMonthly;
                        }
                    }
                }
                else if (i < movingAverages.Count)
                {
                    imputed = movingAverages[i];
                }
                else
                {
                    imputed = valid.Average(r => r.BaseValue);
                }

                row.BaseValueAdjusted = imputed;
                row.IsNoData = 1;
            }
            else
            {
                row.BaseValueAdjusted = row.BaseValue;
                row.IsNoData = 0;
            }
        }
    }

    /// <summary>The detection floors below which a deviation is never an anomaly, no matter how
    /// statistically extreme: a sub-row absolute difference or a sub-10% relative difference is business
    /// noise on a warehouse metric (the legacy floors, kept for tiny-variance series).</summary>
    public const double AbsoluteFloor = 1.0;

    /// <summary>See <see cref="AbsoluteFloor"/>; in percent of the actual value.</summary>
    public const double RelativeFloorPercent = 10.0;

    /// <summary>Marks the trailing dates inside the maturity window: data may still be arriving for them, so
    /// they are scored and reported but never counted as anomalies. The window is anchored at
    /// <paramref name="asOfDate"/> (the day the check runs): with <paramref name="maturityDays"/> = 1,
    /// today's partial load stops crying wolf every morning.</summary>
    public static void MarkImmaturePoints(List<SeriesRow> series, DateTime asOfDate, int maturityDays)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfNegative(maturityDays);

        var firstImmature = asOfDate.Date.AddDays(-(maturityDays - 1));
        foreach (var row in series)
        {
            row.IsImmature = maturityDays > 0 && row.Date.Date >= firstImmature;
        }
    }

    /// <summary>
    /// Tags anomalies with the generalized ESD test over the prediction residuals: a missing/zero MATURE
    /// point is "Missing Data" unconditionally; among the mature observed points, the statistically
    /// significant outliers (robust-studentized deviation beyond Rosner's critical value at
    /// <paramref name="alpha"/>, capped at <paramref name="maxAnomalyFraction"/> of the series) are tagged
    /// "Absolute Difference" or "Relative Difference" by which deviation dominates. A flagged point must also
    /// clear <paramref name="thresholdStdDev"/> robust sigmas and the business floors
    /// (<see cref="AbsoluteFloor"/> row / <see cref="RelativeFloorPercent"/>%), so statistical significance
    /// on a near-constant series cannot page anyone over a one-row wiggle. Every point receives its
    /// <see cref="SeriesRow.Severity"/>; immature points are scored but never tagged. With fewer than
    /// <see cref="MinimumPointsForTagging"/> mature observed points nothing is tagged and the method reports
    /// false so the caller can log why.
    /// </summary>
    public static bool TagAnomalies(List<SeriesRow> series, double thresholdStdDev, double alpha, double maxAnomalyFraction)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(thresholdStdDev);

        foreach (var row in series)
        {
            row.AnomalyDetected = false;
            row.AnomalyReason = null;
        }

        // Observed means "the source had data", whatever the sign: a SUM of adjustments lives below zero
        // and still deserves statistics (zero-valued dates were already flagged as missing by Impute).
        var mature = series.Where(r => !r.IsImmature).ToList();
        var observed = mature.Where(r => r.IsNoData == 0).ToList();

        // Severity for every point, immature included, from the robust geometry of the mature residuals.
        var residualsAll = series.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList();
        var matureResiduals = observed.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList();
        if (matureResiduals.Count >= 2)
        {
            var center = RobustStatistics.Median(matureResiduals);
            var scale = RobustStatistics.Scale(matureResiduals);
            for (var i = 0; i < series.Count; i++)
            {
                var z = RobustStatistics.Z(residualsAll[i], center, scale);
                series[i].Severity = double.IsPositiveInfinity(z) ? 999 : Math.Round(z, 4);
            }
        }

        // A mature date with no data is an incident, not a statistic.
        foreach (var row in mature.Where(r => r.IsNoData == 1 || r.BaseValue == 0))
        {
            row.AnomalyDetected = true;
            row.AnomalyReason = "Missing Data";
        }

        if (observed.Count < MinimumPointsForTagging)
        {
            return false;
        }

        var flagged = EsdDetector.Detect(matureResiduals, alpha, maxAnomalyFraction);
        foreach (var anomaly in flagged)
        {
            var row = observed[anomaly.Index];
            var absolute = Math.Abs(row.PredictedValue - row.BaseValue);
            var relativePercent = row.BaseValue > 0 ? absolute / row.BaseValue * 100 : double.PositiveInfinity;

            // Statistical significance must still mean something operationally: a sub-row deviation never
            // pages anyone (a frozen reference table makes ANY wiggle infinitely significant), and a
            // small-relative deviation needs overwhelming evidence (a steady table drifting 3% is noise;
            // a million-row table losing 5% is half a sigma short of nothing).
            var suppressed = absolute <= AbsoluteFloor
                || (relativePercent <= RelativeFloorPercent && anomaly.Severity < 3 * thresholdStdDev);
            if (anomaly.Severity < thresholdStdDev || suppressed)
            {
                continue;
            }

            row.AnomalyDetected = true;

            // The deviation view that exceeds its business floor by the larger factor names the reason: a
            // 3-row miss on a 6-row Sunday is relative (50% vs 3 rows); a 400-row miss on a 10k-row Monday
            // is absolute (400 rows vs 4%).
            var absoluteFactor = absolute / AbsoluteFloor;
            var relativeFactor = row.BaseValue > 0 ? relativePercent / RelativeFloorPercent : 0;
            row.AnomalyReason = relativeFactor > absoluteFactor ? "Relative Difference" : "Absolute Difference";
            row.Severity = Math.Round(anomaly.Severity, 4);
        }

        return true;
    }

    /// <summary>Fit metrics (MAE, RMSE, MAPE, R-squared) over the mature actual points (immature partial
    /// days would poison the fit numbers), or null with fewer than <see cref="MinimumPointsForMetrics"/> of
    /// them, where the numbers would be noise.</summary>
    public static HealthCheckFitMetrics? ComputeMetrics(IReadOnlyList<SeriesRow> series)
    {
        ArgumentNullException.ThrowIfNull(series);

        var actual = series.Where(r => r.IsNoData == 0 && !r.IsImmature).ToList();
        if (actual.Count < MinimumPointsForMetrics)
        {
            return null;
        }

        var mae = actual.Average(r => Math.Abs(r.BaseValue - r.PredictedValue));
        var rmse = Math.Sqrt(actual.Average(r => Math.Pow(r.BaseValue - r.PredictedValue, 2)));
        var nonZero = actual.Where(r => r.BaseValue != 0).ToList();
        var mape = nonZero.Count > 0
            ? nonZero.Average(r => Math.Abs((r.BaseValue - r.PredictedValue) / r.BaseValue)) * 100
            : 0;

        var meanActual = actual.Average(r => r.BaseValue);
        var ssTot = actual.Sum(r => Math.Pow(r.BaseValue - meanActual, 2));
        var ssRes = actual.Sum(r => Math.Pow(r.BaseValue - r.PredictedValue, 2));

        // A flat series has no variance to explain: R-squared is 1 for a perfect fit and 0 otherwise,
        // rather than a division by zero.
        var rSquared = ssTot > 0 ? 1 - ssRes / ssTot : (ssRes == 0 ? 1 : 0);

        return new HealthCheckFitMetrics
        {
            MeanAbsoluteError = mae,
            RootMeanSquaredError = rmse,
            MeanAbsolutePercentageError = mape,
            RSquared = rSquared,
        };
    }

    /// <summary>The date one cadence step after <paramref name="date"/> (irregular steps daily, the legacy
    /// behavior).</summary>
    public static DateTime NextDate(DateTime date, DataFrequency frequency) => frequency switch
    {
        DataFrequency.Daily => date.AddDays(1),
        DataFrequency.BiDaily => date.AddDays(2),
        DataFrequency.Weekly => date.AddDays(7),
        DataFrequency.BiWeekly => date.AddDays(14),
        DataFrequency.Monthly => date.AddMonths(1),
        DataFrequency.Quarterly => date.AddMonths(3),
        DataFrequency.HalfYearly => date.AddMonths(6),
        DataFrequency.Yearly => date.AddYears(1),
        _ => date.AddDays(1),
    };

    /// <summary>Centered moving averages over the nonzero values of the window (all values when the whole
    /// window is zero), one per row.</summary>
    public static List<float> MovingAverages(IReadOnlyList<SeriesRow> series, int window)
    {
        ArgumentNullException.ThrowIfNull(series);
        ArgumentOutOfRangeException.ThrowIfLessThan(window, 1);

        var averages = new List<float>(series.Count);
        for (var i = 0; i < series.Count; i++)
        {
            var start = Math.Max(i - window / 2, 0);
            var end = Math.Min(i + window / 2, series.Count - 1);

            var nonZero = new List<float>();
            float all = 0;
            for (var j = start; j <= end; j++)
            {
                all += series[j].BaseValue;
                if (series[j].BaseValue > 0)
                {
                    nonZero.Add(series[j].BaseValue);
                }
            }

            averages.Add(nonZero.Count > 0 ? nonZero.Average() : all / (end - start + 1));
        }

        return averages;
    }
}
