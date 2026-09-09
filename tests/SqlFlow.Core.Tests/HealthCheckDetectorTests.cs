using SqlFlow.Core.HealthChecks;
using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>The detection algorithms one by one: generalized ESD (masking resistance, false-positive
/// control, the anomaly-fraction cap), PELT level shifts (real shifts found, spikes and noise ignored),
/// the Theil-Sen trend (exact on clean data, unmoved by outliers), the weekday baseline, the date-column
/// chooser, and the training-decision table.</summary>
public sealed class HealthCheckDetectorTests
{
    /// <summary>Structured residual noise in a tight band: boring by construction, no RNG in tests.</summary>
    private static List<double> Noise(int n, double amplitude = 1.0)
        => Enumerable.Range(0, n).Select(i => amplitude * ((i % 7) - 3) / 3.0).ToList();

    [Fact]
    public void Esd_CleanSeries_FlagsNothing()
    {
        Assert.Empty(EsdDetector.Detect(Noise(60), alpha: 0.025, maxAnomalyFraction: 0.1));
    }

    [Fact]
    public void Esd_SingleGrossOutlier_IsFlagged()
    {
        var values = Noise(60);
        values[30] = 50;

        var flagged = EsdDetector.Detect(values, 0.025, 0.1);

        var anomaly = Assert.Single(flagged);
        Assert.Equal(30, anomaly.Index);
        Assert.True(anomaly.Severity > 10);
    }

    [Fact]
    public void Esd_Masking_MultipleHugeOutliersDoNotHideTheModerateOne()
    {
        // The classic failure of mean/stddev thresholds: three -80 spikes inflate a plain standard
        // deviation to ~20, hiding the -15 day completely (|z| < 1). The robust ESD walks them off one by
        // one and still convicts the moderate one.
        var values = Noise(80);
        values[10] = -80;
        values[30] = -85;
        values[50] = -78;
        values[65] = -15;

        var flagged = EsdDetector.Detect(values, 0.025, 0.1).Select(a => a.Index).ToHashSet();

        Assert.Contains(10, flagged);
        Assert.Contains(30, flagged);
        Assert.Contains(50, flagged);
        Assert.Contains(65, flagged);
    }

    [Fact]
    public void Esd_MaxAnomalyFraction_CapsTheSearch()
    {
        var values = Noise(40);
        for (var i = 0; i < 10; i++)
        {
            values[i * 4] = 100 + i;
        }

        // The cap bounds how many points the test may even consider.
        Assert.True(EsdDetector.Detect(values, 0.025, 0.1).Count <= (int)Math.Ceiling(40 * 0.1));
    }

    [Fact]
    public void Esd_TightAlpha_IsStricterThanLooseAlpha()
    {
        var values = Noise(50);
        values[25] = 4.2;  // borderline against the noise band

        var loose = EsdDetector.Detect(values, 0.10, 0.1).Count;
        var tight = EsdDetector.Detect(values, 0.001, 0.1).Count;

        Assert.True(tight <= loose);
    }

    [Fact]
    public void Esd_TooFewPoints_ReturnsEmpty()
    {
        Assert.Empty(EsdDetector.Detect([1.0, 50.0, 2.0], 0.025, 0.4));
    }

    [Fact]
    public void Pelt_FindsTheBackfillShift_AtTheRightDate()
    {
        // Residuals sit near 0 for 30 days, then a backfill doubles the level: a +20 regime for 30 days.
        var values = Noise(30);
        values.AddRange(Noise(30).Select(v => v + 20));

        var shift = Assert.Single(PeltDetector.Detect(values));
        Assert.InRange(shift.Index, 28, 32);
        Assert.True(shift.MedianAfter > shift.MedianBefore + 15);
        Assert.True(shift.MagnitudeSigma > 3);
    }

    [Fact]
    public void Pelt_PureNoiseAndConstant_ReportNoRegimes()
    {
        Assert.Empty(PeltDetector.Detect(Noise(90)));
        Assert.Empty(PeltDetector.Detect(Enumerable.Repeat(5.0, 90).ToList()));
    }

    [Fact]
    public void Pelt_SingleSpike_IsNotARegimeChange()
    {
        // One catastrophic day is an anomaly (ESD's job), not a new normal (PELT's job). The L1 cost makes
        // a one-point segment unaffordable.
        var values = Noise(60);
        values[30] = 500;

        Assert.Empty(PeltDetector.Detect(values));
    }

    [Fact]
    public void Pelt_TwoRegimeChanges_BothFound()
    {
        var values = Noise(30);
        values.AddRange(Noise(30).Select(v => v + 25));
        values.AddRange(Noise(30).Select(v => v - 10));

        var shifts = PeltDetector.Detect(values);

        Assert.Equal(2, shifts.Count);
        Assert.InRange(shifts[0].Index, 28, 32);
        Assert.InRange(shifts[1].Index, 58, 62);
    }

    [Fact]
    public void Pelt_SeriesShorterThanTwoSegments_ReturnsEmpty()
    {
        Assert.Empty(PeltDetector.Detect(Noise(2 * PeltDetector.MinSegmentLength - 1)));
    }

    [Fact]
    public void TheilSen_ExactOnCleanLinearData()
    {
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 60).Select(i => (start.AddDays(i), 100.0 + 2.5 * i)).ToList();

        var fit = TrendEstimator.Fit(points);

        Assert.Equal(2.5, fit.SlopePerDay, 9);
        Assert.Equal(100.0, fit.ValueAt(start), 9);
        Assert.Equal(100.0 + 2.5 * 90, fit.ValueAt(start.AddDays(90)), 9);
    }

    [Fact]
    public void TheilSen_ShrugsOffAQuarterOfContamination()
    {
        // Every fourth day is a catastrophic drop; least squares would buckle, the pairwise median holds.
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 80)
            .Select(i => (start.AddDays(i), i % 4 == 3 ? 0.0 : 100.0 + 2.0 * i))
            .ToList();

        var fit = TrendEstimator.Fit(points);

        Assert.InRange(fit.SlopePerDay, 1.8, 2.2);
    }

    [Fact]
    public void TheilSen_FlatAndShortSeries_GiveFlatTrends()
    {
        var start = new DateTime(2026, 1, 1);

        var flat = TrendEstimator.Fit(Enumerable.Range(0, 30).Select(i => (start.AddDays(i), 42.0)).ToList());
        Assert.Equal(0, flat.SlopePerDay, 12);
        Assert.Equal(42.0, flat.ValueAt(start.AddDays(100)), 9);

        var single = TrendEstimator.Fit([(start, 7.0)]);
        Assert.Equal(0, single.SlopePerDay);
        Assert.Equal(7.0, single.Intercept);
    }

    [Fact]
    public void TheilSen_UsesTheRecentWindow_NotAncientHistory()
    {
        // 200 flat days followed by 60 growing days: the default 180-point window sees mostly the recent
        // regime, so the slope is well above zero.
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 260)
            .Select(i => (start.AddDays(i), i < 200 ? 100.0 : 100.0 + 5.0 * (i - 200)))
            .ToList();

        var fit = TrendEstimator.Fit(points);

        Assert.True(fit.SlopePerDay > 0.5);
        Assert.Equal(TrendEstimator.DefaultWindowPoints, fit.WindowPoints);
    }

    [Fact]
    public void Baseline_WeekdayMedians_WithOverallFallback()
    {
        // Mondays at 100, Tuesdays at 50; a weekday never observed falls back to the overall median.
        var monday = new DateTime(2026, 3, 2);
        var series = new List<SeriesRow>();
        for (var week = 0; week < 3; week++)
        {
            series.Add(new SeriesRow { Date = monday.AddDays(week * 7), BaseValue = 100 });
            series.Add(new SeriesRow { Date = monday.AddDays(week * 7 + 1), BaseValue = 50 });
        }

        var baseline = BaselineModel.Fit(series);

        Assert.Equal(100, baseline.Predict(monday.AddDays(21)));      // a future Monday
        Assert.Equal(50, baseline.Predict(monday.AddDays(22)));       // a future Tuesday
        Assert.Equal(75, baseline.Predict(monday.AddDays(25)));       // an unseen Friday -> overall median
    }

    [Fact]
    public void Baseline_NeedsOneObservedPoint()
    {
        var imputedOnly = new List<SeriesRow> { new() { Date = new DateTime(2026, 1, 1), BaseValue = 0, IsNoData = 1 } };

        Assert.Throws<InvalidOperationException>(() => BaselineModel.Fit(imputedOnly));
    }

    [Theory]
    [InlineData("OrderDate", 1)]
    [InlineData("date", 1)]
    [InlineData("DateOfBirth", 2)]
    [InlineData("CreatedAt", 3)]
    [InlineData("ModifiedTimestamp", 3)]
    [InlineData("ValidFrom", 4)]
    [InlineData("InsertedDate_DW", 5)]
    public void DateColumnSelector_Tiers(string name, int expectedTier)
    {
        Assert.Equal(expectedTier, DateColumnSelector.Tier(name));
    }

    [Fact]
    public void DateColumnSelector_PrefersTheBusinessDate_OverSystemColumns()
    {
        var choice = DateColumnSelector.Choose(
        [
            ("OrderID", "int"),
            ("InsertedDate_DW", "datetime2(3)"),
            ("UpdatedDate_DW", "datetime2(3)"),
            ("OrderDate", "date"),
            ("CreatedAt", "datetime2(7)"),
            ("CustomerName", "nvarchar(200)"),
        ]);

        Assert.Equal("OrderDate", choice.Column);
        Assert.Equal(4, choice.Candidates.Count);
        Assert.Contains("alternatives", choice.Reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public void DateColumnSelector_NoDateTypedColumns_ExplainsItself()
    {
        var choice = DateColumnSelector.Choose([("Id", "int"), ("Name", "nvarchar(50)"), ("DateKey", "int")]);

        Assert.Null(choice.Column);
        Assert.Empty(choice.Candidates);
        Assert.Contains("no date-typed columns", choice.Reasoning, StringComparison.Ordinal);
    }

    [Fact]
    public void DateColumnSelector_TieBreaks_AreDeterministic()
    {
        // Same tier (both end in 'date'): the shorter name wins; equal length falls to ordinal order.
        var choice = DateColumnSelector.Choose([("ShipmentDate", "date"), ("OrderDate", "date")]);

        Assert.Equal("OrderDate", choice.Column);
    }

    private static HealthCheckModelMetadata Stored(int engineVersion, DateTime trainedAtUtc) => new()
    {
        EngineVersion = engineVersion,
        FlowName = "f",
        Trainer = "T",
        TrainedAtUtc = trainedAtUtc,
        TrainingSeconds = 1,
        Trials = 1,
    };

    [Fact]
    public void TrainingDecision_CoversEveryRow()
    {
        var now = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var fresh = Stored(HealthCheckModelMetadata.CurrentEngineVersion, now.AddDays(-3));

        // always: trains, stored or not.
        Assert.Equal("policy 'always'", HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Always, null, fresh, now, "p"));

        // never: scores with the stored model, or fails clearly without one.
        Assert.Null(HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Never, null, fresh, now, "p"));
        var ex = Assert.Throws<InvalidOperationException>(
            () => HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Never, null, null, now, "the/model/path"));
        Assert.Contains("the/model/path", ex.Message, StringComparison.Ordinal);

        // auto: missing, outdated engine, stale, fresh.
        Assert.Equal("no stored model", HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, null, null, now, "p"));
        Assert.Contains("detection engine v1",
            HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, null, Stored(1, now.AddDays(-1)), now, "p"), StringComparison.Ordinal);
        Assert.Contains("older than 30 day(s)",
            HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, 30, Stored(HealthCheckModelMetadata.CurrentEngineVersion, now.AddDays(-31)), now, "p"), StringComparison.Ordinal);
        Assert.Null(HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, 30, fresh, now, "p"));
        Assert.Null(HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, null, Stored(HealthCheckModelMetadata.CurrentEngineVersion, now.AddDays(-400)), now, "p"));
    }
}
