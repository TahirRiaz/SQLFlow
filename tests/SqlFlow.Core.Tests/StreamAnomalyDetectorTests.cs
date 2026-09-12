using System.Globalization;
using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The estate-wide stream detector, tested at the failures it exists to catch and, just as deliberately, at
/// the false positives that would make it unusable. A monitoring surface that cries wolf is switched off
/// within a week, so "a healthy stream stays silent" is as much a requirement here as "a dead stream is
/// reported".
/// </summary>
public sealed class StreamAnomalyDetectorTests
{
    /// <summary>A Wednesday, so a weekday-pattern series built backwards from here spans whole weeks.</summary>
    private static readonly DateTime AsOf = new(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc);

    private static StreamBucket Day(DateTime date, long rows, int runs = 1, int failures = 0, int backfills = 0)
        => new()
        {
            Date = date,
            Runs = runs,
            Failures = failures,
            ExcludedBackfillRuns = backfills,
            RowsInserted = rows,
            RowsUpdated = 0,
            RowsDeleted = 0,
        };

    /// <summary>A daily stream loading a steady volume for the given span, ending <paramref name="endsDaysAgo"/>
    /// before "now".</summary>
    private static List<StreamBucket> Daily(int days, long rows, int endsDaysAgo = 0)
    {
        var buckets = new List<StreamBucket>();
        for (var i = days - 1 + endsDaysAgo; i >= endsDaysAgo; i--)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), rows));
        }

        return buckets;
    }

    private static StreamAnalysis Analyze(IReadOnlyList<StreamBucket> buckets, StreamAnomalyOptions? options = null)
        => StreamAnomalyDetector.Analyze(buckets, AsOf.Date.AddDays(-59), AsOf, options ?? new StreamAnomalyOptions());

    // ---- The false positives that would sink the surface --------------------------------------------------

    [Fact]
    public void SteadyDailyStream_IsHealthy_AndNoDetectorFires()
    {
        var analysis = Analyze(Daily(60, 10_000));

        Assert.Equal(StreamStatus.Healthy, analysis.Status);
        Assert.Equal("healthy", analysis.Category);
        Assert.Equal(0, analysis.AgreeingDetectors);
        Assert.All(analysis.Signals, s => Assert.False(s.Fired, $"{s.Detector} fired on a steady stream: {s.Detail}"));
    }

    [Fact]
    public void WeekdayOnlyStream_IsHealthy_BecauseWeekendsAreLearnedAsNormal()
    {
        // The single largest false-positive class on a warehouse: a feed that has never once loaded at a
        // weekend must not be reported every Saturday.
        var buckets = new List<StreamBucket>();
        for (var i = 59; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            buckets.Add(Day(date, 10_000));
        }

        var analysis = Analyze(buckets);

        Assert.Equal(StreamStatus.Healthy, analysis.Status);
        Assert.Equal("weekdays", analysis.Profile.Pattern.Shape);
        Assert.Equal(0, analysis.Profile.UnexpectedNullDays);
    }

    [Fact]
    public void NoisyButSteadyStream_IsHealthy()
    {
        // Real volumes wander. A detector that fires on ordinary variation reports the whole estate.
        var buckets = new List<StreamBucket>();
        for (var i = 59; i >= 0; i--)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), 10_000 + (i * 37 % 2_500)));
        }

        var analysis = Analyze(buckets);

        Assert.Equal(StreamStatus.Healthy, analysis.Status);
    }

    // ---- Reprocessing must not become the baseline --------------------------------------------------------

    [Fact]
    public void UnflaggedBackfill_IsTrimmed_AndOrdinaryDaysAfterItAreNotACollapse()
    {
        // The failure this whole trim exists for: one enormous day that carried no backfill flag. Left in the
        // sample it lifts the rate baseline by two orders of magnitude, and every ordinary day afterwards
        // reads as a collapse.
        var buckets = Daily(60, 1_000);
        buckets[20] = Day(buckets[20].Date, 5_000_000);

        var analysis = Analyze(buckets);

        Assert.Equal(1, analysis.Profile.TrimmedLoadDays);
        Assert.Equal(StreamStatus.Healthy, analysis.Status);
        var rate = analysis.Signals.First(s => s.Detector == StreamDetector.RateChange);
        Assert.False(rate.Fired, rate.Detail);
    }

    [Fact]
    public void TrimmedDay_KeepsItsTrueRowCountInTheReportedSeries()
    {
        // The trim is a fitting decision, not a reporting one: the chart must still show what really landed.
        var buckets = Daily(60, 1_000);
        buckets[20] = Day(buckets[20].Date, 5_000_000);

        var point = Analyze(buckets).Series.Single(p => p.Date == buckets[20].Date.Date);

        Assert.True(point.Trimmed);
        Assert.Equal(5_000_000, point.RowsWritten);
    }

    [Fact]
    public void FlaggedBackfillRuns_AreCountedButNeverAnalysed()
    {
        var buckets = Daily(60, 1_000);
        buckets[30] = buckets[30] with { ExcludedBackfillRuns = 3 };

        var analysis = Analyze(buckets);

        Assert.Equal(3, analysis.Series.Single(p => p.Date == buckets[30].Date.Date).ExcludedBackfillRuns);
        Assert.Equal(StreamStatus.Healthy, analysis.Status);
    }

    // ---- Zero data: the failure the surface exists for ----------------------------------------------------

    [Fact]
    public void DailyStreamGoneSilent_IsStalled()
    {
        var analysis = Analyze(Daily(50, 10_000, endsDaysAgo: 6));

        Assert.Equal(StreamStatus.Stalled, analysis.Status);
        Assert.Equal("stalled", analysis.Category);
        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.Silence).Fired);
    }

    [Fact]
    public void LongOutage_IsCritical_AndNeedsTwoDetectors()
    {
        var analysis = Analyze(Daily(40, 10_000, endsDaysAgo: 19));

        Assert.Equal("critical", analysis.Severity);
        Assert.True(analysis.AgreeingDetectors >= StreamAnomalyDetector.ConfirmationThreshold);
    }

    [Fact]
    public void ScatteredEmptyDays_AreReportedEvenThoughDataIsArrivingAgain()
    {
        // A drought that already ended is invisible to any current-state test, and it is exactly the kind of
        // gap nobody notices: the table is loading today, so nothing looks wrong.
        var buckets = Daily(60, 10_000);
        buckets.RemoveAll(b => b.Date >= AsOf.Date.AddDays(-25) && b.Date <= AsOf.Date.AddDays(-18));

        var analysis = Analyze(buckets);

        Assert.Equal("gap-days", analysis.Category);
        Assert.True(analysis.Profile.UnexpectedNullDays >= 8);
        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.NullDays).Fired);
    }

    [Fact]
    public void DaysWhereTheFlowRanAndHadNothingNew_AreNotReportedAsMissingData()
    {
        // The false positive that put a healthy stream at the top of the board. An incremental flow over a
        // source that produced nothing writes zero rows and SAYS SO by succeeding; that is the flow reporting
        // there was nothing new, not data going missing. It stays visible, at the lowest severity, and never
        // wears the same words as a stream that stopped.
        var buckets = Daily(60, 10_000);
        for (var i = 20; i <= 25; i++)
        {
            var date = AsOf.Date.AddDays(-i);
            buckets[buckets.FindIndex(b => b.Date == date)] = Day(date, rows: 0, runs: 1);
        }

        var analysis = Analyze(buckets);

        Assert.Equal("idle-days", analysis.Category);
        Assert.Equal(StreamStatus.Watch, analysis.Status);
        Assert.Equal("info", analysis.Severity);
        Assert.Equal(0, analysis.Profile.NoRunDays);
        Assert.Contains("loaded no new rows", analysis.Summary, StringComparison.OrdinalIgnoreCase);
        // It must still say, in the same breath, that data IS arriving; that is the sentence whose absence
        // made a healthy stream read as a broken one.
        Assert.Contains("still arriving", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DaysTheFlowDidNotRunAtAll_AreStillReportedAsAGap()
    {
        // The counterpart: nothing ran, so nothing said there was nothing to load. That is a gap.
        var buckets = Daily(60, 10_000);
        buckets.RemoveAll(b => b.Date >= AsOf.Date.AddDays(-25) && b.Date <= AsOf.Date.AddDays(-20));

        var analysis = Analyze(buckets);

        Assert.Equal("gap-days", analysis.Category);
        Assert.True(analysis.Profile.NoRunDays > 0);
    }

    [Fact]
    public void EmptyDays_AreSplitByWhetherTheFlowRan()
    {
        // Same symptom, different owner: a flow that ran and wrote nothing is an upstream problem, a flow that
        // never ran is a scheduling one.
        var buckets = Daily(60, 10_000);
        for (var i = 20; i <= 24; i++)
        {
            var date = AsOf.Date.AddDays(-i);
            buckets[buckets.FindIndex(b => b.Date == date)] = Day(date, rows: 0);
        }

        buckets.RemoveAll(b => b.Date >= AsOf.Date.AddDays(-30) && b.Date <= AsOf.Date.AddDays(-28));

        var profile = Analyze(buckets).Profile;

        Assert.Equal(5, profile.EmptyRunDays);
        Assert.Equal(3, profile.NoRunDays);
        Assert.Equal(8, profile.UnexpectedNullDays);
        // The denominator the eight are read against: a daily stream is expected on every mature day of the
        // sixty-day window, and only the trailing day still arriving is left out of it.
        Assert.Equal(59, profile.ExpectedDays);
    }

    [Fact]
    public void ExpectedDays_CountOnlyTheWeekdaysAStreamLoadsOn()
    {
        // A weekday-only feed is not expected at the weekend, so those days are neither missed nor expected:
        // "3 of 43" has to mean 43 working days, not 60 calendar ones.
        var buckets = new List<StreamBucket>();
        var workingDays = 0;
        for (var i = 59; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                continue;
            }

            buckets.Add(Day(date, 10_000));
            if (i > 0)
            {
                workingDays++;
            }
        }

        var profile = Analyze(buckets).Profile;

        Assert.Equal(workingDays, profile.ExpectedDays);
        Assert.Equal(0, profile.UnexpectedNullDays);
    }

    [Fact]
    public void SilentStreamWhoseEveryRunFailed_IsReportedAsFailing_NotAsAQuietUpstream()
    {
        var buckets = Daily(50, 10_000, endsDaysAgo: 6);
        for (var i = 5; i >= 0; i--)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), rows: 0, runs: 1, failures: 1));
        }

        var analysis = Analyze(buckets);

        Assert.Equal("failing", analysis.Category);
        Assert.Contains("failed", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FlowThatStoppedRunningEntirely_IsReportedSeparatelyFromAQuietUpstream()
    {
        var analysis = Analyze(Daily(50, 10_000, endsDaysAgo: 6));

        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.Cadence).Fired);
        Assert.Contains("no run at all", analysis.Summary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- A declared cadence beats an inferred one ---------------------------------------------------------

    [Fact]
    public void DeclaredDailySchedule_ReportsAStreamThatOnlyEverLoadsWeekly()
    {
        // Inferred from its own behaviour this stream looks fine: it always waits a week. Its cron says daily,
        // and the cron is what it is supposed to do.
        var buckets = new List<StreamBucket>();
        for (var i = 56; i >= 3; i -= 7)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), 10_000));
        }

        var inferred = Analyze(buckets);
        var declared = Analyze(buckets, new StreamAnomalyOptions { ExpectedGapDaysOverride = 1, MinObservedDays = 5 });

        Assert.Equal(StreamStatus.Healthy, inferred.Status);
        Assert.Equal("schedule", declared.Profile.CadenceSource);
        Assert.True(declared.Signals.First(s => s.Detector == StreamDetector.Silence).Fired);
    }

    // ---- Volume: less than normal, and more than normal ---------------------------------------------------

    [Fact]
    public void StreamThatHalvedAndStayedHalved_IsLessThanNormal_AndNeverCritical()
    {
        var buckets = Daily(60, 10_000);
        for (var i = 0; i < 14; i++)
        {
            var index = buckets.Count - 1 - i;
            buckets[index] = Day(buckets[index].Date, 800);
        }

        var analysis = Analyze(buckets);

        Assert.Equal("less-than-normal", analysis.Category);
        Assert.NotEqual("critical", analysis.Severity);
        Assert.Contains(analysis.Signals, s => s.Fired && s.Direction == StreamDirection.Below);
    }

    [Fact]
    public void StreamDeliveringMuchMoreThanUsual_IsInformationOnly()
    {
        var buckets = Daily(60, 10_000);
        for (var i = 0; i < 10; i++)
        {
            var index = buckets.Count - 1 - i;
            buckets[index] = Day(buckets[index].Date, 30_000);
        }

        var analysis = Analyze(buckets);

        Assert.Equal("more-than-normal", analysis.Category);
        Assert.Equal("info", analysis.Severity);
        Assert.Equal(StreamStatus.Watch, analysis.Status);
    }

    [Fact]
    public void GrowingStream_IsNotReported_BecauseTheTrendIsExpectation()
    {
        // A table that adds a little more every day is healthy; a detector without a robust trend calls its
        // growth an anomaly every single day.
        var buckets = new List<StreamBucket>();
        for (var i = 59; i >= 0; i--)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), 10_000 + (59 - i) * 200));
        }

        var analysis = Analyze(buckets);

        Assert.Equal(StreamStatus.Healthy, analysis.Status);
        Assert.True(analysis.Profile.TrendRowsPerDay > 0);
    }

    // ---- Patterns -----------------------------------------------------------------------------------------

    [Fact]
    public void DailyStream_IsDescribedAsDaily_WithItsTypicalBand()
    {
        var pattern = Analyze(Daily(60, 10_000)).Profile.Pattern;

        Assert.Equal("daily", pattern.Shape);
        Assert.Equal(7, pattern.LoadDays.Count);
        Assert.Equal(10_000, pattern.TypicalRows);
        Assert.Equal(1, pattern.Reliability);
        Assert.Contains("every day", pattern.Description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MondayOnlyStream_IsDescribedAsWeekly()
    {
        var buckets = new List<StreamBucket>();
        for (var i = 59; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            if (date.DayOfWeek == DayOfWeek.Monday)
            {
                buckets.Add(Day(date, 5_000));
            }
        }

        var pattern = Analyze(buckets).Profile.Pattern;

        Assert.Equal("weekly", pattern.Shape);
        Assert.Equal([DayOfWeek.Monday], pattern.LoadDays);
    }

    // ---- Refusing to judge --------------------------------------------------------------------------------

    [Fact]
    public void StreamWithTooLittleHistory_IsHeldBack_NotFlagged()
    {
        var analysis = Analyze(Daily(3, 10_000, endsDaysAgo: 10));

        Assert.Equal(StreamStatus.InsufficientHistory, analysis.Status);
        Assert.Equal("insufficient-history", analysis.Category);
        Assert.All(analysis.Signals, s => Assert.False(s.Fired));
    }

    [Fact]
    public void StreamDeadLongerThanTheWindow_IsStalledAndCritical_NotNeverLoaded()
    {
        // The worst case must not be the most invisible one. A table that stopped months ago has no load
        // inside the window at all, so there is no pattern to build; judged on the window alone it reads as
        // "never loaded" at the lowest severity, which buries the longest-broken tables at the bottom of the
        // board. The caller supplies the last load it can see, and the verdict follows from that.
        var buckets = Enumerable.Range(0, 30)
            .Select(i => Day(AsOf.Date.AddDays(-i), rows: 0))
            .ToList();

        var analysis = Analyze(buckets, new StreamAnomalyOptions
        {
            LastKnownLoadUtc = AsOf.Date.AddDays(-120),
        });

        Assert.Equal(StreamStatus.Stalled, analysis.Status);
        Assert.Equal("stalled", analysis.Category);
        Assert.Equal("critical", analysis.Severity);
        Assert.Equal(120, analysis.Profile.DaysSinceLastLoad);
        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.Silence).Fired);
    }

    [Fact]
    public void StreamDeadLongerThanTheWindow_WhoseEveryRunFailed_IsReportedAsFailing()
    {
        var buckets = Enumerable.Range(0, 20)
            .Select(i => Day(AsOf.Date.AddDays(-i), rows: 0, runs: 1, failures: 1))
            .ToList();

        var analysis = Analyze(buckets, new StreamAnomalyOptions
        {
            LastKnownLoadUtc = AsOf.Date.AddDays(-90),
        });

        Assert.Equal("failing", analysis.Category);
        Assert.Equal(StreamStatus.Stalled, analysis.Status);
    }

    [Fact]
    public void StreamThatRunsButHasNeverWrittenARow_IsNeverLoaded_NotStalled()
    {
        // A staged endpoint or an assertions-only flow reads exactly like this, and neither is broken.
        var buckets = Enumerable.Range(0, 30)
            .Select(i => Day(AsOf.Date.AddDays(-i), rows: 0))
            .ToList();

        var analysis = Analyze(buckets);

        Assert.Equal("never-loaded", analysis.Category);
        Assert.Equal(StreamStatus.InsufficientHistory, analysis.Status);
    }

    [Fact]
    public void StreamWithNoRunsAtAll_IsReportedWithoutThrowing()
    {
        var analysis = Analyze([]);

        Assert.Equal(StreamStatus.InsufficientHistory, analysis.Status);
        Assert.Empty(analysis.Series);
    }

    [Fact]
    public void TodaysPartialLoad_IsNeverFlagged()
    {
        // Data may still be arriving for today; flagging it would page someone every morning.
        var buckets = Daily(60, 10_000);
        buckets[^1] = Day(buckets[^1].Date, 12);

        var analysis = Analyze(buckets);

        Assert.True(analysis.Series[^1].Immature);
        Assert.False(analysis.Series[^1].Anomaly);
    }

    // ---- The confirmation rule -----------------------------------------------------------------------------

    [Fact]
    public void EveryDetectorReportsWhatItMeasured_EvenWhenQuiet()
    {
        // A healthy verdict has to be auditable, or it is just an assertion.
        var analysis = Analyze(Daily(60, 10_000));

        Assert.Equal(Enum.GetValues<StreamDetector>().Length, analysis.Signals.Count);
        Assert.All(analysis.Signals, s => Assert.False(string.IsNullOrWhiteSpace(s.Detail)));
    }

    [Fact]
    public void OnlyTheMissingDataDetectorsAreMarkedPrimary()
    {
        var signals = Analyze(Daily(60, 10_000)).Signals.ToDictionary(s => s.Detector, s => s.Primary);

        Assert.True(signals[StreamDetector.Silence]);
        Assert.True(signals[StreamDetector.NullDays]);
        Assert.True(signals[StreamDetector.Cadence]);
        Assert.False(signals[StreamDetector.RateChange]);
        Assert.False(signals[StreamDetector.LevelShift]);
        Assert.False(signals[StreamDetector.VolumeOutlier]);
    }

    // ---- The recurring delivery a weekday model cannot express --------------------------------------------

    /// <summary>A deterministic wobble in [-1, 1], so a series built "with realistic noise" is the same series
    /// on every run.</summary>
    private static double Jitter(int seed)
    {
        var x = Math.Sin((seed + 1) * 12.9898) * 43758.5453;
        return 2 * (x - Math.Floor(x)) - 1;
    }

    /// <summary>A daily stream that ships an ordinary load most days and a refill of
    /// <paramref name="refillRows"/> every <paramref name="period"/> days, the most recent of which lands
    /// three days before "now". <paramref name="skipLastRefill"/> delivers an ordinary load on that day
    /// instead, which is the failure the cycle model exists to make visible.</summary>
    private static List<StreamBucket> DailyWithRefill(
        int days, long ordinaryRows, long refillRows, int period, bool skipLastRefill = false)
    {
        var lastRefill = AsOf.Date.AddDays(-3);
        var phase = (lastRefill - DateTime.UnixEpoch.Date).Days % period;
        var buckets = new List<StreamBucket>();
        for (var i = days - 1; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            var onCycle = (date - DateTime.UnixEpoch.Date).Days % period == phase;
            var rows = onCycle && !(skipLastRefill && date == lastRefill) ? refillRows : ordinaryRows;
            buckets.Add(Day(date, rows + (long)(0.02 * ordinaryRows * Jitter(i))));
        }

        return buckets;
    }

    [Fact]
    public void FortnightlyRefill_IsLearned_AndTheRefillIsNotReportedAsAnOutlier()
    {
        // The false positive this cost us before: a vendor who has always shipped a bigger refill every
        // fortnight was reported every fortnight for doing exactly that, which is the most corrosive kind of
        // wrong answer because it is perfectly regular.
        var analysis = Analyze(DailyWithRefill(60, 9_000, 18_000, 14));

        var cycle = analysis.Profile.Pattern.Cycle;
        Assert.NotNull(cycle);
        Assert.Equal(14, cycle.PeriodDays);
        Assert.False(cycle.Monthly);
        Assert.True(cycle.Heavier);
        Assert.Equal(AsOf.Date.AddDays(-3), cycle.LastOccurrenceUtc);
        Assert.Equal(AsOf.Date.AddDays(11), cycle.NextExpectedUtc);
        Assert.Contains("every 14 days", cycle.Description, StringComparison.Ordinal);

        var outlier = analysis.Signals.First(s => s.Detector == StreamDetector.VolumeOutlier);
        Assert.False(outlier.Fired, $"the refill was reported as an outlier: {outlier.Detail}");
        Assert.Equal(StreamStatus.Healthy, analysis.Status);
    }

    [Fact]
    public void AMissedRefill_IsReportedAsAShortfall()
    {
        // The inverse failure, and the one that was invisible before: with the cycle in the expectation, a
        // refill that does not arrive is a shortfall on the day it was due rather than an ordinary day nobody
        // looks at.
        var analysis = Analyze(DailyWithRefill(60, 9_000, 18_000, 14, skipLastRefill: true));

        var outlier = analysis.Signals.First(s => s.Detector == StreamDetector.VolumeOutlier);
        Assert.True(outlier.Fired, "a refill that never arrived went unreported");
        Assert.Equal(StreamDirection.Below, outlier.Direction);
        Assert.Contains(AsOf.Date.AddDays(-3).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            outlier.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefillIsWrittenIntoThePatternSentence()
    {
        var analysis = Analyze(DailyWithRefill(60, 9_000, 18_000, 14));

        Assert.Contains("heavier delivery every 14 days", analysis.Profile.Pattern.Description,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ASteadyStream_LearnsNoCycle()
    {
        // Nothing to explain, so nothing may be claimed: a cycle invented here would excuse the real outliers
        // this surface exists to report.
        Assert.Null(Analyze(Daily(60, 10_000)).Profile.Pattern.Cycle);
    }

    // ---- Low-frequency streams: weekly and monthly rhythms ------------------------------------------------

    /// <summary>A stream that loads once every <paramref name="everyDays"/> days, the last load
    /// <paramref name="endsDaysAgo"/> before "now".</summary>
    private static List<StreamBucket> EveryNDays(int windowDays, int everyDays, long rows, int endsDaysAgo = 0)
    {
        var buckets = new List<StreamBucket>();
        for (var i = endsDaysAgo; i < windowDays; i += everyDays)
        {
            buckets.Add(Day(AsOf.Date.AddDays(-i), rows));
        }

        return buckets;
    }

    [Fact]
    public void AMonthlyStreamThatDied_IsReportedStalled_NotHeldBackForeverAsTooNew()
    {
        // The blind spot a flat "seven loading days" bar creates: a monthly vendor can never accumulate seven
        // loads in any window this surface reads, so it could go dead for a year and never be reported. The
        // sample is small because the RHYTHM is slow, which is a different thing from not being able to see
        // the stream.
        var buckets = EveryNDays(180, 30, 40_000, endsDaysAgo: 75);
        var analysis = StreamAnomalyDetector.Analyze(
            buckets, AsOf.Date.AddDays(-179), AsOf, new StreamAnomalyOptions());

        Assert.Equal(StreamStatus.Stalled, analysis.Status);
        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.Silence).Fired);
    }

    [Fact]
    public void AHealthyMonthlyStream_IsNotFlagged_AndItsVolumeTestsSayTheyWereHeldBack()
    {
        var buckets = EveryNDays(180, 30, 40_000);
        var analysis = StreamAnomalyDetector.Analyze(
            buckets, AsOf.Date.AddDays(-179), AsOf, new StreamAnomalyOptions());

        Assert.Equal(StreamStatus.Healthy, analysis.Status);
        Assert.Equal("monthly", analysis.Profile.Pattern.Shape);
        Assert.Contains("about once a month", analysis.Profile.Pattern.Description, StringComparison.Ordinal);

        // Held back rather than silently passed: a verdict has to say what it did not test.
        var volume = analysis.Signals.First(s => s.Detector == StreamDetector.VolumeOutlier);
        Assert.False(volume.Fired);
        Assert.Contains("held back", volume.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AWeeklyStreamOnAShortWindow_IsStillJudged()
    {
        // Four loads in a thirty-day window is every load a weekly feed can make, and it used to be too few
        // for the surface to say anything at all.
        var buckets = EveryNDays(30, 7, 20_000, endsDaysAgo: 21);
        var analysis = StreamAnomalyDetector.Analyze(
            buckets, AsOf.Date.AddDays(-29), AsOf, new StreamAnomalyOptions());

        Assert.Equal(StreamStatus.Stalled, analysis.Status);
        Assert.True(analysis.Signals.First(s => s.Detector == StreamDetector.Silence).Fired);
    }

    [Fact]
    public void ADailyStreamWithAlmostNoLoads_IsStillHeldBack()
    {
        // The guard the split must not lose: a daily stream that has only ever loaded twice, most recently
        // yesterday, is not a slow rhythm. Nothing about it can be judged, and saying so is the honest answer.
        var buckets = new List<StreamBucket>
        {
            Day(AsOf.Date.AddDays(-30), 10_000),
            Day(AsOf.Date.AddDays(-1), 10_000),
        };
        for (var i = 59; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            if (date != AsOf.Date.AddDays(-30) && date != AsOf.Date.AddDays(-1))
            {
                buckets.Add(Day(date, 0));
            }
        }

        var analysis = Analyze(buckets, new StreamAnomalyOptions { ExpectedGapDaysOverride = 1 });

        Assert.Equal(StreamStatus.InsufficientHistory, analysis.Status);
    }

    [Fact]
    public void AStreamWithNoCycle_IsScoredExactlyAsItWasBefore()
    {
        // The cycle is an ENRICHMENT of the expectation, never a replacement for it. Where a stream has no
        // recurring delivery to learn, nothing is learned and the trend-plus-weekday model is left exactly as
        // it was, day for day. Anything else would mean every stream on the estate got a new verdict to pay
        // for one vendor's fortnightly refill.
        var buckets = new List<StreamBucket>();
        for (var i = 59; i >= 0; i--)
        {
            var date = AsOf.Date.AddDays(-i);
            var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            buckets.Add(Day(date, (weekend ? 3_000 : 10_000) + (long)(400 * Jitter(i))));
        }

        var enriched = Analyze(buckets);
        var plain = Analyze(buckets, new StreamAnomalyOptions { DetectDeliveryCycles = false });

        Assert.Null(enriched.Profile.Pattern.Cycle);
        Assert.Equal(plain.Status, enriched.Status);
        Assert.Equal(plain.Summary, enriched.Summary);
        Assert.Equal(
            plain.Series.Select(p => p.Expected).ToList(),
            enriched.Series.Select(p => p.Expected).ToList());
        Assert.Equal(
            plain.Series.Select(p => p.Severity).ToList(),
            enriched.Series.Select(p => p.Severity).ToList());
    }

    [Fact]
    public void CycleDetectionOff_LeavesTheFortnightlyStreamFullOfFlaggedDays()
    {
        // What the surface did before, and the reason this is worth modelling: a weekday model can give a
        // weekday ONE level, so on a stream whose every other Thursday is a refill it is wrong on every
        // Thursday, in one direction or the other. Days get flagged either way; only which ones changes.
        var buckets = DailyWithRefill(60, 9_000, 18_000, 14);

        var without = Analyze(buckets, new StreamAnomalyOptions { DetectDeliveryCycles = false });
        var with = Analyze(buckets);

        Assert.Null(without.Profile.Pattern.Cycle);
        Assert.Contains(without.Series, p => p is { Anomaly: true, Imputed: false });
        Assert.DoesNotContain(with.Series, p => p is { Anomaly: true, Imputed: false });
    }

    [Fact]
    public void PromoteVolumeFindingsOff_DemotesAVolumeDropToALeadRatherThanAFinding()
    {
        var buckets = Daily(60, 10_000);
        for (var i = 0; i < 14; i++)
        {
            var index = buckets.Count - 1 - i;
            buckets[index] = Day(buckets[index].Date, 800);
        }

        var analysis = Analyze(buckets, new StreamAnomalyOptions { PromoteVolumeFindings = false });

        Assert.Equal(StreamStatus.Watch, analysis.Status);
        Assert.Equal("info", analysis.Severity);
    }
}
