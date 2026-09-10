using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The pure health-check stages: cadence detection, missing-date completion (internal gaps AND the
/// trailing dead-pipeline gap), calendar features, imputation, maturity marking, ESD-based anomaly tagging,
/// and fit metrics.</summary>
public sealed class HealthCheckEngineTests
{
    internal static List<SeriesRow> DailySeries(int days, float value, params int[] zeroDays)
    {
        var start = new DateTime(2026, 3, 2);
        var rows = new List<SeriesRow>();
        for (var i = 0; i < days; i++)
        {
            rows.Add(new SeriesRow
            {
                Date = start.AddDays(i),
                BaseValue = zeroDays.Contains(i) ? 0 : value,
            });
        }

        return rows;
    }

    [Theory]
    [InlineData(1, DataFrequency.Daily)]
    [InlineData(2, DataFrequency.BiDaily)]
    [InlineData(7, DataFrequency.Weekly)]
    [InlineData(14, DataFrequency.BiWeekly)]
    public void DetectFrequency_MapsTheMostCommonGap(int gapDays, DataFrequency expected)
    {
        var start = new DateTime(2026, 1, 5);
        var dates = Enumerable.Range(0, 8).Select(i => start.AddDays((double)i * gapDays)).ToList();

        Assert.Equal(expected, HealthCheckEngine.DetectFrequency(dates));
    }

    [Fact]
    public void DetectFrequency_MonthlyAndIrregular()
    {
        var start = new DateTime(2026, 1, 1);
        var monthly = Enumerable.Range(0, 6).Select(start.AddMonths).ToList();
        Assert.Equal(DataFrequency.Monthly, HealthCheckEngine.DetectFrequency(monthly));

        var irregular = new List<DateTime> { start, start.AddDays(400), start.AddDays(800) };
        Assert.Equal(DataFrequency.Irregular, HealthCheckEngine.DetectFrequency(irregular));
    }

    [Fact]
    public void DetectFrequency_NeedsTwoDates()
    {
        Assert.Throws<ArgumentException>(() => HealthCheckEngine.DetectFrequency([new DateTime(2026, 1, 1)]));
    }

    [Fact]
    public void DetectMissingDates_FindsTheGapsTheCadenceExpects()
    {
        var start = new DateTime(2026, 3, 2);
        var dates = Enumerable.Range(0, 10).Where(i => i != 3 && i != 7).Select(i => start.AddDays(i)).ToList();

        var missing = HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Daily);

        Assert.Equal([start.AddDays(3), start.AddDays(7)], missing);
    }

    [Fact]
    public void DetectMissingDates_TrailingGap_CatchesTheDeadPipeline()
    {
        // The series simply stops four days before the check runs: no internal gap exists, only the trailing
        // one, the deadliest warehouse failure.
        var start = new DateTime(2026, 3, 2);
        var dates = Enumerable.Range(0, 20).Select(i => start.AddDays(i)).ToList();
        var asOf = start.AddDays(23);

        var missing = HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Daily, expectThrough: asOf);

        Assert.Equal([start.AddDays(20), start.AddDays(21), start.AddDays(22), start.AddDays(23)], missing);
    }

    [Fact]
    public void DetectMissingDates_WalksMonthlyCadence_IncludingTrailing()
    {
        var dates = new List<DateTime> { new(2026, 1, 1), new(2026, 2, 1), new(2026, 4, 1) };

        var internalOnly = HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Monthly);
        Assert.Equal([new DateTime(2026, 3, 1)], internalOnly);

        var withTrailing = HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Monthly, expectThrough: new DateTime(2026, 6, 12));
        Assert.Equal([new DateTime(2026, 3, 1), new DateTime(2026, 5, 1), new DateTime(2026, 6, 1)], withTrailing);
    }

    [Fact]
    public void AddMissingDates_AppendsZeroFlaggedRowsInOrder()
    {
        var series = DailySeries(3, 100);
        var missing = new List<DateTime> { series[0].Date.AddDays(-1) };

        HealthCheckEngine.AddMissingDates(series, missing);

        Assert.Equal(4, series.Count);
        Assert.Equal(missing[0], series[0].Date);
        Assert.Equal(0, series[0].BaseValue);
        Assert.Equal(1, series[0].IsNoData);
    }

    [Fact]
    public void ApplyDateFeatures_ComputesTheCalendarInEngine()
    {
        var newYear = new DateTime(2026, 1, 1);       // Thursday, ISO week 1
        var lastDay = new DateTime(2026, 12, 31);     // Thursday, ISO week 53
        var saturday = new DateTime(2026, 6, 13);
        var sunday = new DateTime(2026, 6, 14);
        var series = new List<SeriesRow>
        {
            new() { Date = newYear, BaseValue = 1 },
            new() { Date = lastDay, BaseValue = 1 },
            new() { Date = saturday, BaseValue = 1 },
            new() { Date = sunday, BaseValue = 1 },
        };

        HealthCheckEngine.ApplyDateFeatures(series, [new DateOnly(2026, 1, 1)]);

        Assert.Equal(2026, series[0].Year);
        Assert.Equal(1, series[0].Quarter);
        Assert.Equal(1, series[0].WeekOfYear);
        Assert.Equal(1, series[0].MonthNumber);
        Assert.Equal(5, series[0].DayOfWeekNumber);   // Thursday, Sunday=1 convention
        Assert.Equal(0, series[0].IsWeekend);
        Assert.Equal(1, series[0].IsHoliday);

        Assert.Equal(53, series[1].WeekOfYear);
        Assert.Equal(4, series[1].Quarter);
        Assert.Equal(0, series[1].IsHoliday);

        Assert.Equal(7, series[2].DayOfWeekNumber);   // Saturday
        Assert.Equal(1, series[2].IsWeekend);
        Assert.Equal(2, series[2].Quarter);

        Assert.Equal(1, series[3].DayOfWeekNumber);   // Sunday
        Assert.Equal(1, series[3].IsWeekend);
    }

    [Fact]
    public void Impute_FillsMissingFromTheWeekdayAverage()
    {
        // Day 7 is the same weekday as day 0 and 14; its weekday average is 100.
        var series = DailySeries(15, 100, zeroDays: 7);

        HealthCheckEngine.Impute(series);

        var imputed = series[7];
        Assert.Equal(1, imputed.IsNoData);
        Assert.Equal(100, imputed.BaseValueAdjusted);

        var real = series[0];
        Assert.Equal(0, real.IsNoData);
        Assert.Equal(100, real.BaseValueAdjusted);
    }

    [Fact]
    public void Impute_AllZeroSeries_FailsClearly()
    {
        var series = DailySeries(5, 0);

        var ex = Assert.Throws<InvalidOperationException>(() => HealthCheckEngine.Impute(series));
        Assert.Contains("no nonzero values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MovingAverages_AverageTheNonZeroWindow()
    {
        var series = DailySeries(5, 100, zeroDays: 2);

        var averages = HealthCheckEngine.MovingAverages(series, window: 3);

        // Window of index 2 spans indices 1..3: the zero is excluded, leaving 100s.
        Assert.Equal(100, averages[2]);
        Assert.Equal(series.Count, averages.Count);
    }

    [Fact]
    public void MarkImmaturePoints_FlagsTheTrailingWindowOnly()
    {
        var series = DailySeries(10, 100);
        var asOf = series[^1].Date;

        HealthCheckEngine.MarkImmaturePoints(series, asOf, maturityDays: 2);

        Assert.True(series[^1].IsImmature);
        Assert.True(series[^2].IsImmature);
        Assert.False(series[^3].IsImmature);

        HealthCheckEngine.MarkImmaturePoints(series, asOf, maturityDays: 0);
        Assert.All(series, r => Assert.False(r.IsImmature));
    }

    private static void PredictPerfectly(List<SeriesRow> series)
    {
        foreach (var row in series)
        {
            row.BaseValueAdjusted = row.BaseValue == 0 ? 100 : row.BaseValue;
            row.IsNoData = row.BaseValue == 0 ? 1 : 0;
            row.PredictedValue = 100;
        }
    }

    [Fact]
    public void TagAnomalies_TagsMissingAndEsdOutlier_LeavesNormalAlone()
    {
        // 30 clean days at 100 with mild noise, one missing day, one 60% collapse.
        var series = DailySeries(31, 100, zeroDays: 30);
        PredictPerfectly(series);
        for (var i = 0; i < 30; i++)
        {
            series[i].BaseValue = 100 + (i % 5) - 2;  // tame, structured noise
            series[i].BaseValueAdjusted = series[i].BaseValue;
        }

        series[15].BaseValue = 40;
        series[15].BaseValueAdjusted = 40;

        Assert.True(HealthCheckEngine.TagAnomalies(series, thresholdStdDev: 2.0, alpha: 0.025, maxAnomalyFraction: 0.2));

        Assert.True(series[30].AnomalyDetected);
        Assert.Equal("Missing Data", series[30].AnomalyReason);

        Assert.True(series[15].AnomalyDetected);
        Assert.Equal("Absolute Difference", series[15].AnomalyReason);
        Assert.True(series[15].Severity >= 2.0);

        Assert.False(series[0].AnomalyDetected);
        Assert.Null(series[0].AnomalyReason);
    }

    [Fact]
    public void TagAnomalies_ImmatureMissingDay_IsScoredButNeverCounted()
    {
        var series = DailySeries(31, 100, zeroDays: 30);
        PredictPerfectly(series);
        HealthCheckEngine.MarkImmaturePoints(series, series[^1].Date, maturityDays: 1);

        Assert.True(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1));

        // The trailing missing day is inside the maturity window: today's load simply has not landed yet.
        Assert.True(series[^1].IsImmature);
        Assert.False(series[^1].AnomalyDetected);

        // With maturity off, the same day is an incident.
        HealthCheckEngine.MarkImmaturePoints(series, series[^1].Date, maturityDays: 0);
        Assert.True(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1));
        Assert.True(series[^1].AnomalyDetected);
        Assert.Equal("Missing Data", series[^1].AnomalyReason);
    }

    [Fact]
    public void TagAnomalies_FloorsSuppressBusinessNoise()
    {
        // A near-constant series with a one-row wiggle: statistically extreme (every other residual is 0),
        // operationally meaningless. The floors keep it quiet.
        var series = DailySeries(30, 1000);
        PredictPerfectly(series);
        foreach (var row in series)
        {
            row.PredictedValue = 1000;
        }

        series[10].BaseValue = 1000.6f;
        series[10].BaseValueAdjusted = 1000.6f;

        Assert.True(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1));
        Assert.False(series[10].AnomalyDetected);
    }

    [Fact]
    public void TagAnomalies_SkipsStatisticsBelowThreePoints_MissingStillTagged()
    {
        var series = DailySeries(3, 100, zeroDays: 2);
        PredictPerfectly(series);

        Assert.False(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1));

        // The statistical pass is skipped, the rule-based missing-data pass is not.
        Assert.True(series[2].AnomalyDetected);
        Assert.Equal("Missing Data", series[2].AnomalyReason);
        Assert.False(series[0].AnomalyDetected);
    }

    [Fact]
    public void TagAnomalies_NegativeMetric_JudgedByTheAbsoluteView()
    {
        // A SUM of adjustments lives below zero; the relative view is undefined there by the legacy
        // convention, so a flagged negative point reads "Absolute Difference".
        var series = DailySeries(30, 0);
        for (var i = 0; i < series.Count; i++)
        {
            series[i].BaseValue = -50 + (i % 3);
            series[i].BaseValueAdjusted = series[i].BaseValue;
            series[i].PredictedValue = -50;
            series[i].IsNoData = 0;
        }

        series[20].BaseValue = -500;
        series[20].BaseValueAdjusted = -500;

        Assert.True(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1));
        Assert.True(series[20].AnomalyDetected);
        Assert.Equal("Absolute Difference", series[20].AnomalyReason);
    }

    [Fact]
    public void ComputeMetrics_PerfectFitScoresOne_AndExcludesImmature()
    {
        var series = DailySeries(12, 100);
        for (var i = 0; i < series.Count; i++)
        {
            series[i].BaseValue = 100 + i;
            series[i].PredictedValue = 100 + i;
        }

        // A wildly wrong immature tail must not poison the fit numbers.
        series[^1].IsImmature = true;
        series[^1].PredictedValue = 0;

        var metrics = HealthCheckEngine.ComputeMetrics(series);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.MeanAbsoluteError);
        Assert.Equal(0, metrics.RootMeanSquaredError);
        Assert.Equal(0, metrics.MeanAbsolutePercentageError);
        Assert.Equal(1, metrics.RSquared);
    }

    [Fact]
    public void ComputeMetrics_WithheldBelowTenPoints()
    {
        Assert.Null(HealthCheckEngine.ComputeMetrics(DailySeries(9, 100)));
    }
}
