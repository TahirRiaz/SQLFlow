using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A second, non-overlapping band of robustness edge cases for the published health-check canon, picked to
/// pin behavior the other suites leave unstated: ESD ties and all-equal residuals and the argument guards,
/// PELT plateaus and the exact too-short boundary and a clean step with zero noise, Theil-Sen on a single
/// trailing-window day and monotone-but-noisy data and unsorted timestamps, the cadence boundary thresholds
/// (the quarterly/half-yearly/yearly arms and the tie-toward-the-smaller-gap rule), missing-date detection
/// when the series is already complete or expectThrough is in the past, maturity at and beyond the series
/// span, imputation seasonality and moving-average windows, multi-metric one-scan SQL with quirky names and
/// expressions, model-store round trips for empty trend metadata and engine-version skew and unicode names,
/// and the data-quality probe shape. Every input is fixed; nothing touches a clock, a disk it does not own,
/// the network, or an unseeded RNG.
/// </summary>
public sealed class HealthCheckExtraEdgeCaseTests
{
    private static readonly DateTime ExtraStart = new(2026, 1, 5); // a Monday

    /// <summary>A small, fully deterministic structured-noise residual band (no RNG), distinct in shape from
    /// the helpers in the sibling suites.</summary>
    private static List<double> ExtraNoise(int n)
        => Enumerable.Range(0, n).Select(i => ((i % 6) - 2.5) / 2.0).ToList();

    private static List<SeriesRow> ExtraDaily(int days, Func<int, DateTime, float> value)
    {
        var rows = new List<SeriesRow>(days);
        for (var i = 0; i < days; i++)
        {
            var date = ExtraStart.AddDays(i);
            rows.Add(new SeriesRow { Date = date, BaseValue = value(i, date) });
        }

        return rows;
    }

    private static HealthCheckFlow ExtraFlow(
        string dateColumn = "OrderDate",
        string? filter = null,
        DateOnly? floor = null,
        params (string Name, string Expression)[] metrics) => new()
        {
            FlowId = 7,
            SysAlias = "extra-watch",
            Server = "dwh",
            Target = RelationalObject.Parse("DW.dbo.Sales"),
            DateColumn = dateColumn,
            Metrics = metrics.Length == 0
                ? [new HealthCheckMetric { Name = "rowCount", Expression = "COUNT(*)" }]
                : metrics.Select(m => new HealthCheckMetric { Name = m.Name, Expression = m.Expression }).ToList(),
            FilterCriteria = filter,
            SentinelDateFloor = floor ?? new DateOnly(1990, 1, 1),
        };

    private static HealthCheckModelMetadata ExtraMetadata(
        int engineVersion = HealthCheckModelMetadata.CurrentEngineVersion, string flowName = "extra-watch") => new()
        {
            EngineVersion = engineVersion,
            FlowName = flowName,
            Trainer = "FastForestRegression",
            TrainedAtUtc = new DateTime(2026, 5, 1, 8, 30, 0, DateTimeKind.Utc),
            TrainingSeconds = 12.0,
            Trials = 4,
        };

    // ---- RobustStatistics: untested guard and symmetry corners --------------------------------------------

    [Fact]
    public void Median_EmptySeries_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => RobustStatistics.Median([]));
    }

    [Theory]
    [InlineData(new[] { -5.0, -1.0, -3.0 }, -3.0)]
    [InlineData(new[] { -2.0, -4.0, -6.0, -8.0 }, -5.0)]
    [InlineData(new[] { 1000000.0, 1000002.0 }, 1000001.0)]
    public void Median_NegativeAndLargeMagnitudes_AreOrderedNotSummedNaively(double[] values, double expected)
    {
        Assert.Equal(expected, RobustStatistics.Median(values), 9);
    }

    [Fact]
    public void Scale_TwoPointSpread_IsTheMadSigmaOfTheHalfGap()
    {
        // Two points: each absolute deviation from the midpoint is 5, so MAD is 5 and the scale is 5*1.4826.
        Assert.Equal(5 * RobustStatistics.MadToSigma, RobustStatistics.Scale([10.0, 20.0]), 9);
    }

    [Fact]
    public void Z_IsSignBlind_AndScalesLinearly()
    {
        // |value - median| is symmetric about the median, and doubling the scale halves the studentized z.
        Assert.Equal(RobustStatistics.Z(13, 10, 2), RobustStatistics.Z(7, 10, 2), 12);
        Assert.Equal(RobustStatistics.Z(20, 10, 5) / 2.0, RobustStatistics.Z(20, 10, 10), 12);
    }

    [Fact]
    public void Z_NegativeScale_IsTreatedAsNonPositive()
    {
        // The contract keys on scale > 0; a non-positive scale yields 0 on the center, infinity off it.
        Assert.Equal(0, RobustStatistics.Z(4, 4, -1));
        Assert.True(double.IsPositiveInfinity(RobustStatistics.Z(5, 4, -1)));
    }

    // ---- StudentT: argument guards and degenerate-domain handling -----------------------------------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    public void StudentT_InverseCdf_RejectsProbabilitiesOutsideTheOpenUnitInterval(double p)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StudentT.InverseCdf(p, 10));
    }

    [Fact]
    public void StudentT_Cdf_RejectsSubOneDegreesOfFreedom_AndNaN()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StudentT.Cdf(0, 0.5));
        Assert.Throws<ArgumentException>(() => StudentT.Cdf(double.NaN, 5));
    }

    [Fact]
    public void RegularizedIncompleteBeta_EndpointsAreExact()
    {
        // I_0(a,b) = 0 and I_1(a,b) = 1 for any positive shape parameters.
        Assert.Equal(0, StudentT.RegularizedIncompleteBeta(2, 3, 0), 12);
        Assert.Equal(1, StudentT.RegularizedIncompleteBeta(2, 3, 1), 12);
    }

    [Fact]
    public void RegularizedIncompleteBeta_SymmetricCenterOfASymmetricBeta_IsOneHalf()
    {
        // For equal shapes the distribution is symmetric about x = 1/2, so the regularized integral there is 0.5.
        Assert.Equal(0.5, StudentT.RegularizedIncompleteBeta(4, 4, 0.5), 9);
    }

    [Fact]
    public void RegularizedIncompleteBeta_RejectsXOutsideTheUnitInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => StudentT.RegularizedIncompleteBeta(2, 2, -0.01));
        Assert.Throws<ArgumentOutOfRangeException>(() => StudentT.RegularizedIncompleteBeta(2, 2, 1.01));
    }

    [Fact]
    public void LogGamma_ReflectionBranch_MatchesTheFactorialIdentityNearZero()
    {
        // The x < 0.5 reflection path: Gamma(0.25)*Gamma(0.75) = pi / sin(pi/4), so the log-sum is its log.
        var expected = Math.Log(Math.PI / Math.Sin(Math.PI * 0.25));
        Assert.Equal(expected, StudentT.LogGamma(0.25) + StudentT.LogGamma(0.75), 9);
    }

    // ---- ESD: ties, all-equal residuals, and the argument guards ------------------------------------------

    [Fact]
    public void Esd_AllEqualResiduals_FlagNothing_NoDivisionByZero()
    {
        // Every residual identical: the robust scale collapses to 0, no deviation exists, nothing is flagged.
        Assert.Empty(EsdDetector.Detect(Enumerable.Repeat(4.0, 50).ToList(), 0.025, 0.1));
    }

    [Fact]
    public void Esd_TiedExtremes_AreBothConvicted_Deterministically()
    {
        // Two identical large spikes among quiet noise: the outward walk removes one then the other, and the
        // result set is stable across runs (no RNG, fixed order).
        var values = ExtraNoise(40);
        values[12] = 60;
        values[28] = 60;

        var first = EsdDetector.Detect(values, 0.025, 0.2).Select(a => a.Index).OrderBy(i => i).ToList();
        var second = EsdDetector.Detect(values, 0.025, 0.2).Select(a => a.Index).OrderBy(i => i).ToList();

        Assert.Equal(first, second);
        Assert.Contains(12, first);
        Assert.Contains(28, first);
    }

    [Fact]
    public void Esd_ExactlyFourPoints_OneOutlier_IsTheSmallestWorkableSeries()
    {
        // n = 4 is the floor: maxAnomalies = min(ceil(4*0.25), 1) = 1, so one gross outlier can still be found.
        var anomaly = Assert.Single(EsdDetector.Detect([0.0, 0.1, -0.1, 40.0], 0.025, 0.25));
        Assert.Equal(3, anomaly.Index);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(1.0)]
    [InlineData(-0.1)]
    [InlineData(1.5)]
    public void Esd_RejectsAlphaOutsideTheOpenUnitInterval(double alpha)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EsdDetector.Detect(ExtraNoise(40), alpha, 0.1));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(-0.2)]
    public void Esd_RejectsAnomalyFractionOutsideTheRobustRange(double fraction)
    {
        // A robust test refuses to call half or more of the series anomalous; zero and negatives are invalid too.
        Assert.Throws<ArgumentOutOfRangeException>(() => EsdDetector.Detect(ExtraNoise(40), 0.025, fraction));
    }

    [Fact]
    public void Esd_FractionAtThePointFourNineCeiling_IsAccepted()
    {
        // 0.49 is the inclusive upper bound; the call must not throw and stays within its own cap.
        var values = ExtraNoise(40);
        values[20] = 75;

        var flagged = EsdDetector.Detect(values, 0.025, 0.49);

        Assert.Contains(flagged, a => a.Index == 20);
        Assert.True(flagged.Count <= (int)Math.Ceiling(40 * 0.49));
    }

    // ---- PELT: plateaus, the exact boundary, and the noise-free step --------------------------------------

    [Fact]
    public void Pelt_ExactlyTwoMinimumSegments_NoShift_IsClean()
    {
        // 2*MinSegmentLength points of flat noise: long enough to analyze, but there is no regime to find.
        Assert.Empty(PeltDetector.Detect(ExtraNoise(2 * PeltDetector.MinSegmentLength)));
    }

    [Fact]
    public void Pelt_ExactlyTwoMinimumSegments_OneCleanStep_IsFoundAtTheBoundary()
    {
        // The smallest series that can hold a single shift: a clean step right at the midpoint.
        var values = new List<double>();
        values.AddRange(Enumerable.Repeat(0.0, PeltDetector.MinSegmentLength));
        values.AddRange(Enumerable.Repeat(40.0, PeltDetector.MinSegmentLength));

        var shift = Assert.Single(PeltDetector.Detect(values));
        Assert.Equal(PeltDetector.MinSegmentLength, shift.Index);
        Assert.Equal(0, shift.MedianBefore);
        Assert.Equal(40, shift.MedianAfter);
        Assert.True(shift.MagnitudeSigma > 3);
    }

    [Fact]
    public void Pelt_NoiseFreeStep_GetsAFiniteMagnitude_NotInfinity()
    {
        // A perfectly clean step has zero marginal noise; the detector floors sigma so the magnitude stays finite.
        var values = new List<double>();
        values.AddRange(Enumerable.Repeat(10.0, 20));
        values.AddRange(Enumerable.Repeat(25.0, 20));

        var shift = Assert.Single(PeltDetector.Detect(values));
        Assert.True(double.IsFinite(shift.MagnitudeSigma));
        Assert.True(shift.MagnitudeSigma > 0);
    }

    [Fact]
    public void Pelt_RejectsNonPositivePenaltyFactor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PeltDetector.Detect(ExtraNoise(40), 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PeltDetector.Detect(ExtraNoise(40), -1.0));
    }

    [Fact]
    public void Pelt_EagerPenalty_FindsAtLeastAsManyRegimesAsAConservativeOne()
    {
        // A modest step: a lower penalty factor can only make the detector more willing to split, never less.
        var values = ExtraNoise(40);
        for (var i = 20; i < 40; i++)
        {
            values[i] += 6;
        }

        var conservative = PeltDetector.Detect(values, 3.0).Count;
        var eager = PeltDetector.Detect(values, 0.5).Count;

        Assert.True(eager >= conservative);
    }

    [Fact]
    public void Pelt_RobustNoiseScale_OfAConstantSeries_IsZero_AndOfASinglePoint_IsZero()
    {
        Assert.Equal(0, PeltDetector.RobustNoiseScale(Enumerable.Repeat(3.0, 30).ToList()));
        Assert.Equal(0, PeltDetector.RobustNoiseScale([42.0]));
    }

    // ---- Theil-Sen: single-window day, monotone-with-noise, unsorted input -------------------------------

    [Fact]
    public void TheilSen_WindowOfOneDistinctDate_IsFlatAtThatValue()
    {
        // Every point on the same calendar day (distinct x-count < 2): no slope, intercept is the median value.
        var day = new DateTime(2026, 2, 2);
        var points = new List<(DateTime, double)> { (day, 10.0), (day, 14.0), (day, 12.0) };

        var fit = TrendEstimator.Fit(points);

        Assert.Equal(0, fit.SlopePerDay);
        Assert.Equal(12.0, fit.Intercept, 9);
        Assert.Equal(3, fit.WindowPoints);
    }

    [Fact]
    public void TheilSen_MonotoneButNoisy_RecoversThePositiveSlopeSign()
    {
        // Generally increasing with a sawtooth on top: the pairwise-slope median stays positive.
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 40)
            .Select(i => (start.AddDays(i), 50.0 + 3.0 * i + ((i % 2 == 0) ? 5.0 : -5.0)))
            .ToList();

        var fit = TrendEstimator.Fit(points);

        Assert.True(fit.SlopePerDay > 0);
        Assert.InRange(fit.SlopePerDay, 2.0, 4.0);
    }

    [Fact]
    public void TheilSen_WindowTakesTheTrailingPoints_WhenInputExceedsTheWindow()
    {
        // 30 input points, a window of 10: the fit anchors on the 21st point and reports the window size.
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 30).Select(i => (start.AddDays(i), 100.0 + i)).ToList();

        var fit = TrendEstimator.Fit(points, windowPoints: 10);

        Assert.Equal(10, fit.WindowPoints);
        Assert.Equal(start.AddDays(20), fit.AnchorDate);
        Assert.Equal(1.0, fit.SlopePerDay, 9);
    }

    [Fact]
    public void TheilSen_RejectsAWindowBelowTwo()
    {
        var start = new DateTime(2026, 1, 1);
        var points = Enumerable.Range(0, 5).Select(i => (start.AddDays(i), (double)i)).ToList();

        Assert.Throws<ArgumentOutOfRangeException>(() => TrendEstimator.Fit(points, windowPoints: 1));
    }

    [Fact]
    public void TheilSen_StripsTheTimeOfDay_SoSameDayDifferentClockIsOneAnchor()
    {
        // ValueAt and the anchor are date-based; an afternoon timestamp must score like midnight of that day.
        var anchor = new DateTime(2026, 3, 10);
        var points = Enumerable.Range(0, 20).Select(i => (anchor.AddDays(i), 200.0 + 4.0 * i)).ToList();

        var fit = TrendEstimator.Fit(points);

        Assert.Equal(fit.ValueAt(anchor.AddDays(5)), fit.ValueAt(anchor.AddDays(5).AddHours(15)), 9);
    }

    // ---- DetectFrequency: the upper cadence arms and the tie rule -----------------------------------------

    [Theory]
    [InlineData(45, DataFrequency.Quarterly)]
    [InlineData(120, DataFrequency.HalfYearly)]
    [InlineData(250, DataFrequency.Yearly)]
    [InlineData(500, DataFrequency.Irregular)]
    public void DetectFrequency_CoversTheLongCadenceArms(int gapDays, DataFrequency expected)
    {
        var start = new DateTime(2026, 1, 1);
        var dates = Enumerable.Range(0, 6).Select(i => start.AddDays((double)i * gapDays)).ToList();

        Assert.Equal(expected, HealthCheckEngine.DetectFrequency(dates));
    }

    [Fact]
    public void DetectFrequency_GapTie_ResolvesTowardTheSmallerGap()
    {
        // Two gaps of 1 day and two of 7 days: the tie breaks to the smaller key, so Daily wins over Weekly.
        var start = new DateTime(2026, 1, 1);
        var dates = new List<DateTime>
        {
            start,
            start.AddDays(1),
            start.AddDays(2),
            start.AddDays(9),
            start.AddDays(16),
        };

        Assert.Equal(DataFrequency.Daily, HealthCheckEngine.DetectFrequency(dates));
    }

    [Fact]
    public void DetectFrequency_UnsortedDates_AreOrderedFirst()
    {
        // The same daily series shuffled must still read as Daily (the method sorts internally).
        var start = new DateTime(2026, 1, 1);
        var dates = new List<DateTime> { start.AddDays(3), start, start.AddDays(2), start.AddDays(1) };

        Assert.Equal(DataFrequency.Daily, HealthCheckEngine.DetectFrequency(dates));
    }

    // ---- DetectMissingDates: complete series, past expectThrough, single point ----------------------------

    [Fact]
    public void DetectMissingDates_CompleteDailySeries_FindsNothing()
    {
        var start = new DateTime(2026, 3, 2);
        var dates = Enumerable.Range(0, 14).Select(i => start.AddDays(i)).ToList();

        Assert.Empty(HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Daily));
    }

    [Fact]
    public void DetectMissingDates_ExpectThroughInThePast_IsIgnored_OnlyInternalGapsRemain()
    {
        // expectThrough earlier than the last observed date cannot shrink the walk; the internal gap stands,
        // and no trailing dates are invented.
        var start = new DateTime(2026, 3, 2);
        var dates = Enumerable.Range(0, 10).Where(i => i != 4).Select(i => start.AddDays(i)).ToList();

        var missing = HealthCheckEngine.DetectMissingDates(dates, DataFrequency.Daily, expectThrough: start.AddDays(2));

        Assert.Equal([start.AddDays(4)], missing);
    }

    [Fact]
    public void DetectMissingDates_SinglePointWithTrailingExpectation_WalksTheCadenceForward()
    {
        // One observed weekly point, expected through three more weeks: the three trailing Mondays are missing.
        var start = new DateTime(2026, 1, 5); // Monday
        var missing = HealthCheckEngine.DetectMissingDates([start], DataFrequency.Weekly, expectThrough: start.AddDays(21));

        Assert.Equal([start.AddDays(7), start.AddDays(14), start.AddDays(21)], missing);
    }

    [Fact]
    public void DetectMissingDates_EmptySeries_Throws()
    {
        Assert.Throws<ArgumentException>(() => HealthCheckEngine.DetectMissingDates([], DataFrequency.Daily));
    }

    [Fact]
    public void NextDate_QuarterlyAndHalfYearlyAndYearly_StepInCalendarMonthsAndYears()
    {
        // Month/year stepping must honor the calendar (leap years, month-length), not a fixed day count.
        Assert.Equal(new DateTime(2026, 4, 30), HealthCheckEngine.NextDate(new DateTime(2026, 1, 30), DataFrequency.Quarterly));
        Assert.Equal(new DateTime(2026, 7, 31), HealthCheckEngine.NextDate(new DateTime(2026, 1, 31), DataFrequency.HalfYearly));
        Assert.Equal(new DateTime(2025, 2, 28), HealthCheckEngine.NextDate(new DateTime(2024, 2, 29), DataFrequency.Yearly));
    }

    // ---- Maturity: at the span edge and beyond the last point ---------------------------------------------

    [Fact]
    public void MarkImmaturePoints_WindowWiderThanTheSeries_FlagsEveryPoint()
    {
        var series = ExtraDaily(5, (_, _) => 100);

        HealthCheckEngine.MarkImmaturePoints(series, series[^1].Date, maturityDays: 30);

        Assert.All(series, r => Assert.True(r.IsImmature));
    }

    [Fact]
    public void MarkImmaturePoints_FlagsEveryDateFromTheWindowStartOnward_IncludingDatesAtOrAfterAsOf()
    {
        // The window is anchored at as-of and reaches FORWARD without an upper bound: firstImmature =
        // asOf - (maturityDays - 1). With as-of mid-series and a 3-day window, day 5 is the first immature
        // date; days 5..9 (the as-of day at 7 and the two days after it) are all immature, days 0..4 mature.
        var series = ExtraDaily(10, (_, _) => 100);
        var asOf = ExtraStart.AddDays(7);

        HealthCheckEngine.MarkImmaturePoints(series, asOf, maturityDays: 3);

        for (var i = 0; i < 5; i++)
        {
            Assert.False(series[i].IsImmature);
        }

        for (var i = 5; i < 10; i++)
        {
            Assert.True(series[i].IsImmature);
        }
    }

    [Fact]
    public void MarkImmaturePoints_RejectsNegativeMaturity()
    {
        var series = ExtraDaily(3, (_, _) => 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => HealthCheckEngine.MarkImmaturePoints(series, series[^1].Date, -1));
    }

    // ---- Imputation and moving averages: seasonality guard and window edges -------------------------------

    [Fact]
    public void Impute_UnseenWeekday_FallsBackToTheCenteredMovingAverage()
    {
        // A short Mon/Tue-only history with a missing Wednesday (a weekday never observed): the moving-average
        // fallback fills it from the neighbouring nonzero days rather than throwing.
        var series = new List<SeriesRow>
        {
            new() { Date = new DateTime(2026, 3, 2), BaseValue = 100 }, // Monday
            new() { Date = new DateTime(2026, 3, 3), BaseValue = 120 }, // Tuesday
            new() { Date = new DateTime(2026, 3, 4), BaseValue = 0 },   // Wednesday, missing
            new() { Date = new DateTime(2026, 3, 5), BaseValue = 140 }, // Thursday
        };

        HealthCheckEngine.Impute(series);

        var wednesday = series.Single(r => r.Date == new DateTime(2026, 3, 4));
        Assert.Equal(1, wednesday.IsNoData);
        Assert.True(wednesday.BaseValueAdjusted > 0);
    }

    [Fact]
    public void Impute_RejectsAMovingAverageWindowBelowOne()
    {
        var series = HealthCheckEngineTests.DailySeries(5, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => HealthCheckEngine.Impute(series, movingAverageWindow: 0));
    }

    [Fact]
    public void Impute_AllNegativeMetric_FailsClearly_BecauseTheLearnSetIsStrictlyPositive()
    {
        // Impute's "valid" learn set is BaseValue > 0, so a wholly-negative SUM-of-adjustments metric has no
        // values to learn from and fails with the same clear message as an all-zero series. The negative-metric
        // path is handled later by TagAnomalies (which keys on IsNoData), not by imputation.
        var series = new List<SeriesRow>
        {
            new() { Date = new DateTime(2026, 3, 2), BaseValue = -40 },
            new() { Date = new DateTime(2026, 3, 3), BaseValue = -55 },
            new() { Date = new DateTime(2026, 3, 4), BaseValue = -48 },
        };

        var ex = Assert.Throws<InvalidOperationException>(() => HealthCheckEngine.Impute(series));
        Assert.Contains("no nonzero values", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Impute_MixedSignMetric_ImputesAZeroDayFromThePositiveLearnSet()
    {
        // With at least one positive value the learn set is non-empty; a zero day is treated as missing and
        // imputed (negatives stay observed). This pins that zero, not sign, is the missing-data signal.
        var series = new List<SeriesRow>
        {
            new() { Date = new DateTime(2026, 3, 2), BaseValue = 120 }, // Monday, positive
            new() { Date = new DateTime(2026, 3, 3), BaseValue = -30 }, // Tuesday, negative but observed
            new() { Date = new DateTime(2026, 3, 4), BaseValue = 0 },   // Wednesday, the missing day
            new() { Date = new DateTime(2026, 3, 9), BaseValue = 110 }, // next Monday, positive
        };

        HealthCheckEngine.Impute(series);

        Assert.Equal(0, series.Single(r => r.Date == new DateTime(2026, 3, 3)).IsNoData); // negative is observed
        Assert.Equal(1, series.Single(r => r.Date == new DateTime(2026, 3, 4)).IsNoData); // zero is imputed
    }

    [Fact]
    public void MovingAverages_AllZeroWindow_FallsBackToThePlainAverage()
    {
        // When every value in the window is zero the nonzero set is empty, so the plain mean (also 0) is used,
        // never a division by an empty collection.
        var series = HealthCheckEngineTests.DailySeries(5, 0);

        var averages = HealthCheckEngine.MovingAverages(series, window: 3);

        Assert.Equal(series.Count, averages.Count);
        Assert.All(averages, a => Assert.Equal(0, a));
    }

    [Fact]
    public void MovingAverages_WindowOfOne_IsTheValueItself_ForNonZeroPoints()
    {
        var series = HealthCheckEngineTests.DailySeries(4, 70);

        var averages = HealthCheckEngine.MovingAverages(series, window: 1);

        Assert.All(averages, a => Assert.Equal(70, a));
    }

    // ---- TagAnomalies: a single observed point, and a wholly immature series ------------------------------

    [Fact]
    public void TagAnomalies_OneObservedPointAmongMissing_SkipsStatistics_ButTagsTheMissingMatureDays()
    {
        // Below MinimumPointsForTagging observed points: the ESD pass is skipped and the method reports false,
        // yet the mature missing days are still incidents.
        var series = ExtraDaily(4, (i, _) => i == 0 ? 100 : 0);
        foreach (var row in series)
        {
            row.IsNoData = row.BaseValue == 0 ? 1 : 0;
            row.PredictedValue = 100;
        }

        var ran = HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1);

        Assert.False(ran);
        Assert.True(series[1].AnomalyDetected);
        Assert.Equal("Missing Data", series[1].AnomalyReason);
        Assert.False(series[0].AnomalyDetected);
    }

    [Fact]
    public void TagAnomalies_AllPointsImmature_NothingIsCounted_AndItReportsFalse()
    {
        // Every point inside the maturity window: no mature observed points, so statistics are skipped and the
        // worst day, however abnormal, is never tagged.
        var series = ExtraDaily(6, (i, _) => i == 3 ? 10 : 100);
        foreach (var row in series)
        {
            row.IsNoData = 0;
            row.PredictedValue = 100;
            row.IsImmature = true;
        }

        var ran = HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.1);

        Assert.False(ran);
        Assert.All(series, r => Assert.False(r.AnomalyDetected));
    }

    [Fact]
    public void TagAnomalies_RejectsNonPositiveThreshold()
    {
        var series = HealthCheckEngineTests.DailySeries(5, 100);

        Assert.Throws<ArgumentOutOfRangeException>(() => HealthCheckEngine.TagAnomalies(series, 0, 0.025, 0.1));
    }

    [Fact]
    public void TagAnomalies_RelativeDifferenceReason_WhenTheRelativeViewDominates()
    {
        // A tiny-volume weekend table: a 4-row miss on a 6-row Sunday clears both floors, and the relative view
        // (about 67%, 6.7x its floor) beats the absolute view (4 rows, 4x its floor), so the reason is relative.
        var series = ExtraDaily(30, (i, _) => 6 + (i % 3));
        foreach (var row in series)
        {
            row.IsNoData = 0;
            row.PredictedValue = 7;
        }

        series[15].BaseValue = 2;

        Assert.True(HealthCheckEngine.TagAnomalies(series, 2.0, 0.025, 0.2));
        Assert.True(series[15].AnomalyDetected);
        Assert.Equal("Relative Difference", series[15].AnomalyReason);
    }

    // ---- ComputeMetrics: the exact ten-point boundary and the flat-but-wrong R-squared -------------------

    [Fact]
    public void ComputeMetrics_ExactlyTenMaturePoints_IsTheReportingFloor()
    {
        // Nine observed points withhold the numbers; the tenth turns them on.
        Assert.Null(HealthCheckEngine.ComputeMetrics(PerfectActuals(9)));
        Assert.NotNull(HealthCheckEngine.ComputeMetrics(PerfectActuals(10)));
    }

    [Fact]
    public void ComputeMetrics_FlatActualButWrongPrediction_ScoresZeroRSquared_NotInfinity()
    {
        // Constant actuals have no variance to explain; a uniformly wrong prediction yields R-squared 0, not a
        // division by zero, while MAE and RMSE report the constant error honestly.
        var series = HealthCheckEngineTests.DailySeries(12, 100);
        foreach (var row in series)
        {
            row.IsNoData = 0;
            row.PredictedValue = 110;
        }

        var metrics = HealthCheckEngine.ComputeMetrics(series);

        Assert.NotNull(metrics);
        Assert.Equal(0, metrics.RSquared);
        Assert.Equal(10, metrics.MeanAbsoluteError, 9);
        Assert.Equal(10, metrics.RootMeanSquaredError, 9);
    }

    private static List<SeriesRow> PerfectActuals(int count)
    {
        var series = ExtraDaily(count, (i, _) => 100 + i);
        foreach (var row in series)
        {
            row.IsNoData = 0;
            row.PredictedValue = row.BaseValue;
        }

        return series;
    }

    // ---- ApplyDateFeatures: empty input, empty holidays, and a leap day ----------------------------------

    [Fact]
    public void ApplyDateFeatures_EmptySeries_IsANoOp()
    {
        var empty = new List<SeriesRow>();

        HealthCheckEngine.ApplyDateFeatures(empty, []);

        Assert.Empty(empty);
    }

    [Fact]
    public void ApplyDateFeatures_LeapDay_GetsQ1AndWeekday()
    {
        // 29 Feb 2024 is a Thursday in Q1; with no holidays supplied IsHoliday must be 0.
        var series = new List<SeriesRow> { new() { Date = new DateTime(2024, 2, 29), BaseValue = 1 } };

        HealthCheckEngine.ApplyDateFeatures(series, []);

        Assert.Equal(1, series[0].Quarter);
        Assert.Equal(2, series[0].MonthNumber);
        Assert.Equal(5, series[0].DayOfWeekNumber); // Thursday under the Sunday=1 convention
        Assert.Equal(0, series[0].IsWeekend);
        Assert.Equal(0, series[0].IsHoliday);
    }

    // ---- BaselineModel: single observed point, imputed-only is rejected ----------------------------------

    [Fact]
    public void Baseline_SingleObservedPoint_PredictsThatValueForEveryWeekday()
    {
        var series = new List<SeriesRow> { new() { Date = new DateTime(2026, 3, 2), BaseValue = 55, IsNoData = 0 } };

        var baseline = BaselineModel.Fit(series);

        Assert.Equal(55, baseline.Predict(new DateTime(2026, 3, 2)));  // the observed Monday
        Assert.Equal(55, baseline.Predict(new DateTime(2026, 3, 8)));  // an unseen Sunday falls to the overall median
    }

    [Fact]
    public void Baseline_IgnoresImputedPoints_WhenFittingTheWeekdayMedians()
    {
        // An imputed Monday must not move the Monday median; only the observed Mondays count.
        var monday = new DateTime(2026, 3, 2);
        var series = new List<SeriesRow>
        {
            new() { Date = monday, BaseValue = 100, IsNoData = 0 },
            new() { Date = monday.AddDays(7), BaseValue = 100, IsNoData = 0 },
            new() { Date = monday.AddDays(14), BaseValue = 9000, IsNoData = 1 }, // imputed, ignored
        };

        var baseline = BaselineModel.Fit(series);

        Assert.Equal(100, baseline.Predict(monday.AddDays(21)));
    }

    // ---- DateColumnSelector: empty input, tier-4 fallback, datetimeoffset, case-insensitive type ----------

    [Fact]
    public void DateColumnSelector_EmptyColumnList_ChoosesNothing()
    {
        var choice = DateColumnSelector.Choose([]);

        Assert.Null(choice.Column);
        Assert.Empty(choice.Candidates);
    }

    [Fact]
    public void DateColumnSelector_OnlyAnUnnamedDateColumn_IsTierFour_StillChosen()
    {
        // A date-typed column whose name carries no calendar word lands in tier 4 but is still the only choice.
        var choice = DateColumnSelector.Choose([("Snapshot", "datetimeoffset(7)"), ("Id", "int")]);

        Assert.Equal("Snapshot", choice.Column);
        Assert.Equal(4, DateColumnSelector.Tier("Snapshot"));
    }

    [Theory]
    [InlineData("DATETIME2")]
    [InlineData("Date")]
    [InlineData("SMALLDATETIME")]
    [InlineData("DateTimeOffset(7)")]
    public void DateColumnSelector_TypeMatchIsCaseInsensitive(string nativeType)
    {
        var choice = DateColumnSelector.Choose([("OrderDate", nativeType), ("Note", "nvarchar(10)")]);

        Assert.Equal("OrderDate", choice.Column);
    }

    [Fact]
    public void DateColumnSelector_EventStampOutranksAPlainSystemColumn_ButLosesToABusinessDate()
    {
        // CreatedAt (tier 3) beats LoadId_DW (tier 5) yet loses to InvoiceDate (tier 1).
        var withBusiness = DateColumnSelector.Choose([("CreatedAt", "datetime2(7)"), ("InvoiceDate", "date"), ("LoadId_DW", "datetime2(3)")]);
        Assert.Equal("InvoiceDate", withBusiness.Column);

        var withoutBusiness = DateColumnSelector.Choose([("CreatedAt", "datetime2(7)"), ("LoadId_DW", "datetime2(3)")]);
        Assert.Equal("CreatedAt", withoutBusiness.Column);
    }

    // ---- SqlBuilder: multi-metric trimming, CAST per metric, sentinel floor in the quality probe ----------

    [Fact]
    public void SeriesSelect_TrimsMetricExpressions_AndCastsEachToFloat()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(
            ExtraFlow(metrics: [("revenue", "  SUM(Amount)  "), ("avgPrice", "AVG(UnitPrice)")]), new DateTime(2026, 6, 12));

        Assert.Contains("CAST(SUM(Amount) AS float) AS [revenue]", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(AVG(UnitPrice) AS float) AS [avgPrice]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("  SUM(Amount)  ", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesSelect_NoFilter_OmitsTheFilterClauseEntirely()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(ExtraFlow(), new DateTime(2026, 6, 12));

        Assert.DoesNotContain("  AND (", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY [OrderDate]", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesSelect_WhitespaceFilter_IsTreatedAsNoFilter()
    {
        // A blank or whitespace-only filter must not produce a dangling AND (...) clause.
        var sql = HealthCheckSqlBuilder.SeriesSelect(ExtraFlow(filter: "   "), new DateTime(2026, 6, 12));

        Assert.DoesNotContain("AND (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SeriesSelect_BracketsTheThreePartTarget_AndKeepsExactlyOneScan()
    {
        var sql = HealthCheckSqlBuilder.SeriesSelect(ExtraFlow(metrics: [("a", "COUNT(*)"), ("b", "SUM(X)")]), new DateTime(2026, 6, 12));

        Assert.Contains("FROM [DW].[dbo].[Sales]", sql, StringComparison.Ordinal);
        Assert.Equal(1, sql.Split("FROM ").Length - 1);
        Assert.EndsWith(";", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataQualitySelect_NoFilter_OmitsTheFilter_AndUsesTheSentinelFloor()
    {
        var sql = HealthCheckSqlBuilder.DataQualitySelect(
            ExtraFlow(floor: new DateOnly(1998, 1, 1)), asOfDate: new DateTime(2026, 6, 12));

        Assert.Contains("CASE WHEN [OrderDate] < '1998-01-01' THEN 1 END) AS [SentinelDatedRows]", sql, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN [OrderDate] > '2026-06-12' THEN 1 END) AS [FutureDatedRows]", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AND (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void DataQualitySelect_EscapesAClosingBracketInTheDateColumn()
    {
        var sql = HealthCheckSqlBuilder.DataQualitySelect(ExtraFlow(dateColumn: "Weird]Col"), asOfDate: new DateTime(2026, 6, 12));

        Assert.Contains("[Weird]]Col] IS NULL", sql, StringComparison.Ordinal);
    }

    // ---- ModelStore: empty-trend round trip, version skew, unicode names, ephemeral isolation -------------

    [Fact]
    public void ModelStore_RoundTripsAModelWithDefaultedTrendMetadata()
    {
        // A baseline-scored metric persists with no trend (all defaults): the values survive the round trip
        // unchanged rather than being dropped.
        using var anchor = new ExtraTempDirectory();
        var store = new HealthCheckModelStore(anchor.Path);

        store.Save("flow", "rowCount", [1, 2, 3], ExtraMetadata(flowName: "flow"));
        var loaded = store.Load("flow", "rowCount");

        Assert.NotNull(loaded);
        Assert.Equal(default, loaded.Metadata.TrendAnchorDate);
        Assert.Equal(0, loaded.Metadata.TrendSlopePerDay);
        Assert.Equal(0, loaded.Metadata.TrendWindowPoints);
        Assert.Empty(loaded.Metadata.TrialResults);
    }

    [Fact]
    public void ModelStore_PreservesAnOlderEngineVersion_SoTheTrainingDecisionCanSeeTheSkew()
    {
        // The store is a faithful sidecar; it does not silently upgrade an older engine stamp. The decision
        // layer is what notices the skew and retrains.
        using var anchor = new ExtraTempDirectory();
        var store = new HealthCheckModelStore(anchor.Path);

        store.Save("flow", "m", [9], ExtraMetadata(engineVersion: HealthCheckModelMetadata.CurrentEngineVersion - 1, flowName: "flow"));
        var loaded = store.Load("flow", "m");

        Assert.NotNull(loaded);
        Assert.Equal(HealthCheckModelMetadata.CurrentEngineVersion - 1, loaded.Metadata.EngineVersion);

        var reason = HealthCheckTrainingDecision.Resolve(
            HealthCheckTraining.Auto, null, loaded.Metadata, new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc), store.ModelPath("flow", "m"));
        Assert.Contains("detection engine", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ModelStore_UnicodeFlowAndMetricNames_RoundTrip()
    {
        // Plug-into-any-table names can be non-ASCII; the safe-name rule keeps only filesystem-invalid chars out,
        // so a Norwegian flow name and a metric with letters survive a save and load.
        using var anchor = new ExtraTempDirectory();
        var store = new HealthCheckModelStore(anchor.Path);

        store.Save("salgsrapport-æøå", "antallRader", [5, 6], ExtraMetadata(flowName: "salgsrapport-æøå"));
        var loaded = store.Load("salgsrapport-æøå", "antallRader");

        Assert.NotNull(loaded);
        Assert.Equal([5, 6], loaded.ModelBytes);
    }

    [Fact]
    public void ModelStore_Save_RejectsEmptyModelBytes()
    {
        using var anchor = new ExtraTempDirectory();
        var store = new HealthCheckModelStore(anchor.Path);

        Assert.Throws<ArgumentException>(() => store.Save("flow", "m", [], ExtraMetadata(flowName: "flow")));
    }

    [Fact]
    public void ModelStore_ModelPath_LivesUnderTheStateDirectory_AndEndsInTheModelFileName()
    {
        using var anchor = new ExtraTempDirectory();
        var store = new HealthCheckModelStore(anchor.Path);

        var modelPath = store.ModelPath("flow", "rowCount");

        Assert.StartsWith(store.StateDirectory("flow", "rowCount"), modelPath, StringComparison.Ordinal);
        Assert.EndsWith("healthcheck.model", modelPath, StringComparison.Ordinal);
    }

    [Fact]
    public void EphemeralStore_IsCaseInsensitiveOnNames_AndKeepsTheLatestSave()
    {
        // The in-memory key folds case (OrdinalIgnoreCase); a re-save under different casing overwrites, not adds.
        var store = new EphemeralHealthCheckModelStore();

        store.Save("Flow", "RowCount", [1], ExtraMetadata(flowName: "Flow"));
        store.Save("flow", "rowcount", [2, 2], ExtraMetadata(flowName: "flow"));

        var loaded = store.Load("FLOW", "ROWCOUNT");
        Assert.NotNull(loaded);
        Assert.Equal([2, 2], loaded.ModelBytes);
    }

    [Fact]
    public void EphemeralStore_RejectsBlankNames()
    {
        var store = new EphemeralHealthCheckModelStore();

        Assert.Throws<ArgumentException>(() => store.Save(" ", "m", [1], ExtraMetadata()));
        Assert.Throws<ArgumentException>(() => store.Load("f", " "));
    }

    // ---- TrainingDecision: the never-with-stored reuse and the exact retrain boundary ---------------------

    [Fact]
    public void TrainingDecision_RetrainBoundary_IsStrictlyGreaterThan()
    {
        // Exactly at retainAfterDays the model is kept; one day past it retrains.
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var current = HealthCheckModelMetadata.CurrentEngineVersion;

        var atBoundary = ExtraMetadata(current) with { TrainedAtUtc = now.AddDays(-30) };
        Assert.Null(HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, 30, atBoundary, now, "p"));

        var pastBoundary = ExtraMetadata(current) with { TrainedAtUtc = now.AddDays(-30).AddHours(-1) };
        Assert.Contains("older than 30 day(s)",
            HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, 30, pastBoundary, now, "p"), StringComparison.Ordinal);
    }

    [Fact]
    public void TrainingDecision_NeverWithAStoredModel_ReusesIt_EvenWhenAncientAndOutdated()
    {
        // 'never' means score with whatever is stored, full stop: neither an old engine nor an ancient date
        // forces a retrain when the operator pinned the model.
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var ancientOutdated = ExtraMetadata(engineVersion: 1) with { TrainedAtUtc = now.AddDays(-9999) };

        Assert.Null(HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Never, 1, ancientOutdated, now, "p"));
    }

    [Fact]
    public void TrainingDecision_Auto_OutdatedEngineWins_OverAFreshTrainingDate()
    {
        // Even a model trained moments ago is stale if the engine generation changed underneath it.
        var now = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        var freshButOldEngine = ExtraMetadata(engineVersion: HealthCheckModelMetadata.CurrentEngineVersion - 1) with { TrainedAtUtc = now.AddMinutes(-1) };

        var reason = HealthCheckTrainingDecision.Resolve(HealthCheckTraining.Auto, 365, freshButOldEngine, now, "p");

        Assert.Contains("detection engine", reason, StringComparison.Ordinal);
    }

    /// <summary>A self-deleting temp directory, named so it cannot collide with the sibling store suite's
    /// fixtures, used by the file-store round-trip cases here.</summary>
    private sealed class ExtraTempDirectory : IDisposable
    {
        public ExtraTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "sqlflow-hc-extra-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
