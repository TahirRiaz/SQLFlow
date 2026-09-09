using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The real-world warehouse scenarios, composed through the full pure pipeline (complete, featurize, impute,
/// maturity, trend, score, tag, shift): a growing fact table, a backfill regime change, a dead pipeline, a
/// weekly snapshot that skipped a week, a holiday lull, a duplicate storm, a brand-new short-history table,
/// and a near-constant reference table. The expectation function is the known generator (these tests pin the
/// DETECTION semantics; AutoML's fit quality has its own integration coverage).
/// </summary>
public sealed class HealthCheckEdgeCaseTests
{
    private static readonly DateTime Start = new(2026, 1, 5); // a Monday

    /// <summary>A weekday/weekend retail rhythm with optional growth: the canonical warehouse row-count shape.</summary>
    private static double Rhythm(DateTime date, int dayIndex, double growthPerDay = 0)
        => (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 300.0 : 1000.0)
           + growthPerDay * dayIndex
           + (dayIndex % 5) - 2; // tame structured noise, no RNG

    private static List<SeriesRow> Build(int days, Func<int, DateTime, double?> actual)
    {
        var rows = new List<SeriesRow>();
        for (var i = 0; i < days; i++)
        {
            var date = Start.AddDays(i);
            if (actual(i, date) is { } value)
            {
                rows.Add(new SeriesRow { Date = date, BaseValue = (float)value });
            }
        }

        return rows;
    }

    /// <summary>Runs the pure pipeline with the generator as the expectation: completion against
    /// <paramref name="asOf"/>, features, imputation, maturity, prediction, tagging.</summary>
    private static List<SeriesRow> Score(
        List<SeriesRow> series, Func<SeriesRow, double> expected, DateTime asOf, int maturityDays = 1,
        double threshold = 2.0, double alpha = 0.025, double maxFraction = 0.1)
    {
        var frequency = HealthCheckEngine.DetectFrequency(series.Select(r => r.Date).ToList());
        var missing = HealthCheckEngine.DetectMissingDates(series.Select(r => r.Date).ToList(), frequency, expectThrough: asOf);
        HealthCheckEngine.AddMissingDates(series, missing);
        HealthCheckEngine.MarkImmaturePoints(series, asOf, maturityDays);
        HealthCheckEngine.ApplyDateFeatures(series, []);
        HealthCheckEngine.Impute(series);
        foreach (var row in series)
        {
            row.PredictedValue = (float)expected(row);
        }

        HealthCheckEngine.TagAnomalies(series, threshold, alpha, maxFraction);
        return series;
    }

    [Fact]
    public void GrowingFactTable_GrowthIsExpectation_TheDropIsTheAnomaly()
    {
        // A table adding ~15 rows/day of volume. Day 70 loses 60% of its load. Healthy growth must not be
        // anomalous; the partial load must be.
        const int dropDay = 70;
        var series = Build(100, (i, d) => i == dropDay ? Rhythm(d, i, 15) * 0.4 : Rhythm(d, i, 15));

        // The trend the runner would fit, from the adjusted values, recent window.
        var prelim = series.Select(r => (r.Date, (double)r.BaseValue)).ToList();
        var trend = TrendEstimator.Fit(prelim);
        Assert.InRange(trend.SlopePerDay, 5, 25); // growth seen, weekday mix flattens it below the pure 15

        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays, 15), asOf: Start.AddDays(99));

        var anomalies = scored.Where(r => r.AnomalyDetected).ToList();
        var drop = Assert.Single(anomalies);
        Assert.Equal(Start.AddDays(dropDay), drop.Date);
        Assert.Equal("Absolute Difference", drop.AnomalyReason);
        Assert.True(drop.Severity > 10);
    }

    [Fact]
    public void Backfill_BecomesOneLevelShift_NotAWallOfAnomalies()
    {
        // Day 45 onward, a new source doubles the weekday volume. The expectation still rides the old level,
        // so every later residual is high: PELT names the regime change once; the ESD cap keeps the anomaly
        // list from exploding.
        var series = Build(90, (i, d) => Rhythm(d, i) + (i >= 45 ? 800 : 0));
        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf: Start.AddDays(89), maxFraction: 0.1);

        var observed = scored.Where(r => r.IsNoData == 0 && !r.IsImmature).ToList();
        var shifts = PeltDetector.Detect(observed.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList());

        var shift = Assert.Single(shifts);
        Assert.Equal(Start.AddDays(45), observed[shift.Index].Date);
        Assert.True(shift.MedianAfter - shift.MedianBefore > 600);

        // The anomaly list stays bounded by the fraction cap instead of flagging 45 days.
        Assert.True(scored.Count(r => r.AnomalyDetected) <= (int)Math.Ceiling(observed.Count * 0.1));
    }

    [Fact]
    public void RetentionPurge_DownwardShift_IsAlsoARegime()
    {
        var series = Build(90, (i, d) => Rhythm(d, i) - (i >= 50 ? 500 : 0));
        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf: Start.AddDays(89));

        var observed = scored.Where(r => r.IsNoData == 0 && !r.IsImmature).ToList();
        var shift = Assert.Single(PeltDetector.Detect(observed.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList()));

        Assert.Equal(Start.AddDays(50), observed[shift.Index].Date);
        Assert.True(shift.MedianAfter < shift.MedianBefore);
    }

    [Fact]
    public void DeadPipeline_TrailingSilence_IsMissingData_OutsideTheMaturityWindow()
    {
        // The table stopped loading four days before the check runs. Three of the silent days are mature
        // incidents; the as-of day itself is still inside the maturity window.
        var series = Build(60, (i, d) => Rhythm(d, i));
        var asOf = Start.AddDays(63);

        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf, maturityDays: 1);

        var missingTagged = scored.Where(r => r.AnomalyReason == "Missing Data").Select(r => r.Date).ToList();
        Assert.Equal([Start.AddDays(60), Start.AddDays(61), Start.AddDays(62)], missingTagged);
        Assert.True(scored.Single(r => r.Date == asOf).IsImmature);
        Assert.False(scored.Single(r => r.Date == asOf).AnomalyDetected);
    }

    [Fact]
    public void LateArrivingToday_IsImmature_NotAnIncident()
    {
        // This morning's partial load sits at 30% of normal. With the default one-day maturity window the
        // point is scored but never counted; with the window off it pages.
        var series = Build(60, (i, d) => i == 59 ? Rhythm(d, i) * 0.3 : Rhythm(d, i));
        var asOf = Start.AddDays(59);

        var withWindow = Score(Build(60, (i, d) => i == 59 ? Rhythm(d, i) * 0.3 : Rhythm(d, i)),
            r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf, maturityDays: 1);
        var today = withWindow.Single(r => r.Date == asOf);
        Assert.True(today.IsImmature);
        Assert.False(today.AnomalyDetected);
        Assert.True(today.Severity > 2); // still visibly abnormal in the report

        var withoutWindow = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf, maturityDays: 0);
        Assert.True(withoutWindow.Single(r => r.Date == asOf).AnomalyDetected);
    }

    [Fact]
    public void WeeklySnapshot_TheSkippedWeek_IsFound()
    {
        // A weekly load (every Monday); the week of day 28 never landed.
        var series = Build(70, (i, d) => i % 7 == 0 && i != 28 ? 5000.0 + (i % 3) : null);

        var frequency = HealthCheckEngine.DetectFrequency(series.Select(r => r.Date).ToList());
        Assert.Equal(DataFrequency.Weekly, frequency);

        var scored = Score(series, _ => 5000.0, asOf: Start.AddDays(63), maturityDays: 0);

        var missing = Assert.Single(scored, r => r.AnomalyReason == "Missing Data");
        Assert.Equal(Start.AddDays(28), missing.Date);
    }

    [Fact]
    public void MonthlySnapshot_TheSkippedMonth_IsFound()
    {
        var months = new[] { 0, 1, 2, 4, 5 }; // March is missing
        var series = months.Select(m => new SeriesRow { Date = new DateTime(2026, 1, 1).AddMonths(m), BaseValue = 900 + m }).ToList();

        var scored = Score(series, _ => 902.0, asOf: new DateTime(2026, 6, 1), maturityDays: 0);

        var missing = Assert.Single(scored, r => r.AnomalyReason == "Missing Data");
        Assert.Equal(new DateTime(2026, 4, 1), missing.Date);
    }

    [Fact]
    public void HolidayLull_PredictedLow_StaysQuiet()
    {
        // Christmas volume drops to weekend levels. The expectation function knows the holiday (the trained
        // model learns it from the IsHoliday feature), so the lull is not an anomaly.
        var holiday = Start.AddDays(80);
        var series = Build(100, (i, d) => d == holiday ? 280.0 : Rhythm(d, i));

        var scored = Score(series,
            r => r.Date == holiday ? 280.0 : Rhythm(r.Date, (int)(r.Date - Start).TotalDays),
            asOf: Start.AddDays(99));

        Assert.False(scored.Single(r => r.Date == holiday).AnomalyDetected);
    }

    [Fact]
    public void DuplicateStorm_TheSpikeIsTagged()
    {
        // A retried loader doubled day 40.
        var series = Build(80, (i, d) => i == 40 ? Rhythm(d, i) * 2 : Rhythm(d, i));
        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf: Start.AddDays(79));

        var spike = Assert.Single(scored, r => r.AnomalyDetected);
        Assert.Equal(Start.AddDays(40), spike.Date);
    }

    [Fact]
    public void BrandNewTable_FiveDaysOld_StillCatchesItsMissingDay()
    {
        // Short history: the statistical pass needs MinimumPointsForTagging observed points; missing-data
        // detection works from day one, and the baseline gives a usable expectation.
        var series = Build(5, (i, d) => i == 3 ? null : 100.0 + i);
        var baseline = BaselineModel.Fit(series);

        var scored = Score(series, r => baseline.Predict(r.Date), asOf: Start.AddDays(5), maturityDays: 1);

        Assert.Contains(scored, r => r.Date == Start.AddDays(3) && r.AnomalyReason == "Missing Data");
    }

    [Fact]
    public void NearConstantReferenceTable_OneBlip_NoCrash_FloorsDecide()
    {
        // A currency table loads exactly 168 rows every day; one day loads 169. Zero variance breaks naive
        // z-scores (division by zero); here the robust scale collapses, severity goes infinite, and the
        // business floors keep the one-row wiggle quiet.
        var series = Build(40, (i, _) => i == 20 ? 169.0 : 168.0);
        var scored = Score(series, _ => 168.0, asOf: Start.AddDays(39));

        Assert.False(scored.Single(r => r.Date == Start.AddDays(20)).AnomalyDetected);

        // The same table missing 80 rows one day IS an incident, floors or not.
        var broken = Build(40, (i, _) => i == 20 ? 88.0 : 168.0);
        var scoredBroken = Score(broken, _ => 168.0, asOf: Start.AddDays(39));
        var incident = Assert.Single(scoredBroken, r => r.AnomalyDetected);
        Assert.Equal(Start.AddDays(20), incident.Date);
    }

    [Fact]
    public void MissingDataSeverity_ReflectsTheExpectedVolume()
    {
        // A missing Monday (expected ~1000) must read as more severe than noise; severity is filled for
        // every point including the imputed ones.
        var series = Build(40, (i, d) => i == 21 ? null : Rhythm(d, i));
        var scored = Score(series, r => Rhythm(r.Date, (int)(r.Date - Start).TotalDays), asOf: Start.AddDays(39), maturityDays: 0);

        var missing = scored.Single(r => r.Date == Start.AddDays(21));
        Assert.Equal("Missing Data", missing.AnomalyReason);
        Assert.True(missing.Severity > 5);
    }
}
