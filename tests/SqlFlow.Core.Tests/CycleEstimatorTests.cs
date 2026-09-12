using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The delivery-cycle estimator, tested from both sides. A vendor that ships a bigger refill every fortnight
/// must be LEARNED, because reporting it every fortnight for behaving exactly as it always has is the false
/// positive that trains an operator to stop reading the board. A series with no such pattern must learn
/// NOTHING, because a model that finds a cycle in noise would quietly excuse the real outliers it exists to
/// surface.
/// </summary>
public sealed class CycleEstimatorTests
{
    private static readonly DateTime Anchor = new(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A deterministic wobble, so "with realistic noise" does not mean "with a different series on
    /// every run". Values land in [-1, 1].</summary>
    private static double Jitter(int seed)
    {
        var x = Math.Sin((seed + 1) * 12.9898) * 43758.5453;
        return 2 * (x - Math.Floor(x)) - 1;
    }

    /// <summary>Days since the epoch modulo <paramref name="period"/>: the estimator's own phase, so a test can
    /// place a spike exactly where it will be looked for.</summary>
    private static int Phase(DateTime date, int period) => (date.Date - DateTime.UnixEpoch.Date).Days % period;

    private static List<(DateTime Date, double Value)> Series(int days, Func<DateTime, int, double> value)
    {
        var points = new List<(DateTime, double)>();
        for (var i = days - 1; i >= 0; i--)
        {
            var date = Anchor.Date.AddDays(-i);
            points.Add((date, value(date, i)));
        }

        return points;
    }

    [Fact]
    public void FortnightlyRefill_IsLearned_WithItsPeriodAndOffset()
    {
        var period = 14;
        var target = Phase(Anchor.Date, period);
        var points = Series(60, (date, i) =>
            (Phase(date, period) == target ? 8_000 : 0) + (200 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(CycleKind.FixedDays, cycle.Kind);
        Assert.Equal(period, cycle.PeriodDays);
        Assert.Single(cycle.Phases);
        Assert.Equal(target, cycle.Dominant.Phase);
        Assert.InRange(cycle.Dominant.Offset, 7_800, 8_200);
        Assert.True(cycle.Lift > 0.5, $"a clean fortnightly spike explained only {cycle.Lift:P0} of the residual");
    }

    [Fact]
    public void EveryThirdDayConsolidation_IsLearned()
    {
        var target = Phase(Anchor.Date, 3);
        var points = Series(45, (date, i) => (Phase(date, 3) == target ? 5_000 : 0) + (150 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(3, cycle.PeriodDays);
    }

    [Fact]
    public void PlainNoise_LearnsNothing()
    {
        // The bar that matters most: a model willing to find a cycle here would explain away the genuine
        // outliers this surface exists to report.
        var points = Series(90, (_, i) => 1_000 * Jitter(i));

        Assert.Null(CycleModel.Fit(points));
    }

    [Fact]
    public void OneOffSpike_IsNotACycle()
    {
        // A single large day aligns with SOME phase of every period there is. Only repetition makes a cycle,
        // which is what the per-phase observation count and the agreement bar enforce.
        var points = Series(60, (_, i) => (i == 20 ? 50_000 : 0) + (200 * Jitter(i)));

        Assert.Null(CycleModel.Fit(points));
    }

    [Fact]
    public void TwoOccurrences_AreNotEnough()
    {
        // Two events 14 days apart are an anecdote. Three are a pattern, and the window has to hold three
        // whole cycles before the period is even considered.
        var period = 14;
        var target = Phase(Anchor.Date, period);
        var points = Series(30, (date, i) => (Phase(date, period) == target ? 8_000 : 0) + (200 * Jitter(i)));

        Assert.Null(CycleModel.Fit(points));
    }

    [Fact]
    public void WeeklyRhythm_IsLeftToTheWeekdayModel()
    {
        // Period 7 is excluded by construction: the weekday medians already carry it, and letting both hold
        // one effect would split it across two components that then fight over the same variation.
        var points = Series(60, (date, i) =>
            (date.DayOfWeek == DayOfWeek.Monday ? 6_000 : 0) + (150 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.True(cycle is null || cycle.PeriodDays != 7, "period 7 must be left to the weekday model");
    }

    [Fact]
    public void AQuietWeekend_IsAWeekdayEffect_NotAFortnightlyCycle()
    {
        // Every multiple of seven can re-describe a weekday rhythm with one phase per occurrence, and it
        // always fits a shade better for having more parameters. A feed that is simply quiet at weekends must
        // not come back as "a lighter delivery every 14 days": true arithmetic, useless sentence, and it would
        // take the effect away from the model that should own it.
        var points = Series(60, (date, i) =>
            (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? -7_000 : 0) + (250 * Jitter(i)));

        Assert.Null(CycleModel.Fit(points));
    }

    [Fact]
    public void EveryOtherThursday_SubdividesItsWeekday_SoItIsKept()
    {
        // The distinction the coverage rule turns on: here only HALF of that weekday's occurrences are heavy,
        // which no weekday model can express, so it is a genuine cycle.
        var target = Phase(Anchor.Date, 14);
        var points = Series(60, (date, i) => (Phase(date, 14) == target ? 9_000 : 0) + (250 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(14, cycle.PeriodDays);
        Assert.Single(cycle.Phases);
    }

    [Fact]
    public void MonthEndDelivery_IsLearnedAsAMonthlyCycle()
    {
        var points = Series(
            120,
            (date, i) => (date.Day == DateTime.DaysInMonth(date.Year, date.Month) ? 40_000 : 0) + (300 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(CycleKind.MonthDay, cycle.Kind);
        Assert.Equal(CycleModel.LastDayPhase, cycle.Dominant.Phase);
    }

    [Fact]
    public void FirstOfTheMonthDelivery_IsLearnedOnItsDayNumber()
    {
        var points = Series(120, (date, i) => (date.Day == 1 ? 40_000 : 0) + (300 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(CycleKind.MonthDay, cycle.Kind);
        Assert.Equal(1, cycle.Dominant.Phase);
    }

    [Fact]
    public void ALighterRecurringDay_IsLearnedToo()
    {
        // The pattern runs both ways: a vendor whose Sunday-adjacent tenth-day delivery is a fraction of the
        // rest is just as much a pattern, and modelling it is what stops it reading as a shortfall.
        var target = Phase(Anchor.Date, 10);
        var points = Series(60, (date, i) => (Phase(date, 10) == target ? -6_000 : 0) + (200 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(10, cycle.PeriodDays);
        Assert.True(cycle.Dominant.Offset < 0);
    }

    [Fact]
    public void PhaseIsAnchoredAtTheEpoch_SoItDoesNotMoveWithTheWindow()
    {
        // A phase that drifted with the window's first observation would make yesterday's verdict
        // irreproducible today, which is the one thing a monitoring surface may not do.
        var period = 14;
        var target = Phase(Anchor.Date, period);
        var full = Series(60, (date, i) => (Phase(date, period) == target ? 8_000 : 0) + (200 * Jitter(i)));
        var shifted = full.Skip(1).ToList();

        var a = CycleModel.Fit(full);
        var b = CycleModel.Fit(shifted);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.Equal(a.Dominant.Phase, b.Dominant.Phase);
    }

    [Fact]
    public void NextOccurrence_IsOnePeriodOnFromTheLast()
    {
        var period = 14;
        var target = Phase(Anchor.Date, period);
        var points = Series(60, (date, i) => (Phase(date, period) == target ? 8_000 : 0) + (200 * Jitter(i)));

        var cycle = CycleModel.Fit(points);

        Assert.NotNull(cycle);
        Assert.Equal(Anchor.Date, cycle.LastOccurrenceOnOrBefore(Anchor.Date));
        Assert.Equal(Anchor.Date.AddDays(period), cycle.NextOccurrenceAfter(Anchor.Date));
    }

    [Fact]
    public void TooFewPoints_LearnNothing()
        => Assert.Null(CycleModel.Fit(Series(8, (_, i) => 1_000 * Jitter(i))));

    [Fact]
    public void AConstantSeries_LearnsNothing()
        => Assert.Null(CycleModel.Fit(Series(60, (_, _) => 0)));
}
