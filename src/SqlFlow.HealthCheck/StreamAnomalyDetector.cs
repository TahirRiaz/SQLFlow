using System.Globalization;

namespace SqlFlow.HealthCheck;

/// <summary>
/// Estate-wide anomaly detection over the run history's own write statistics. It answers three questions
/// about every loaded table, ranked in that order because they are not equally urgent: <b>zero data</b> (an
/// outage), <b>less data than normal</b> (a degradation), and <b>more data than normal</b> (information).
///
/// <para>
/// It is the zero-configuration counterpart of the <c>hc</c> flow. A health-check flow is the deep instrument:
/// it queries the monitored table along a declared date column, so it sees the shape of the data. That depth
/// costs a flow per table, which is why in practice a handful of tables get one and the rest of the estate
/// goes unwatched. This detector trades depth for coverage: the catalog already records rows inserted,
/// updated, and deleted for every run of every flow, so every stream is monitored the moment it runs once. The
/// estimators are the SAME ones (<see cref="RobustStatistics"/>, <see cref="TrendEstimator"/>,
/// <see cref="EsdDetector"/>, <see cref="PeltDetector"/>), so the two surfaces cannot disagree about what
/// abnormal means.
/// </para>
///
/// <para>
/// <b>Reprocessing is removed before anything is fitted.</b> The caller drops the runs the log FLAGS as
/// backfills, and this detector then trims what those flags miss: a catch-up after an outage, a re-run kicked
/// off without backfill parameters, a source that delivered a year in one file. Any of those can be a thousand
/// times an ordinary day, and one of them left in the sample drags the median, the trend, and the rate
/// baseline so far up that every ordinary day afterwards reads as a collapse. The trim happens first, on
/// purpose: every number below is computed from the trimmed series, while the reported series keeps the truth.
/// </para>
///
/// <para>
/// <b>A zero-row day is judged against what this stream normally does on that kind of day.</b> Reliability is
/// learned per weekday from the stream's own history, so a feed that has never loaded at a weekend is not
/// reported every Saturday, and a Monday-only feed is reported the first Monday it misses. That is what makes
/// "no data" mean something without anyone declaring a schedule for the table.
/// </para>
///
/// <para>
/// Six detectors from different statistical families vote, and which one fires decides which of the three
/// questions was answered. Three are PRIMARY, covering zero data: <see cref="StreamDetector.Silence"/> (the
/// drought running right now), <see cref="StreamDetector.NullDays"/> (more empty days in the window than the
/// learned rates can explain), and <see cref="StreamDetector.Cadence"/> (the flow stopped executing at all).
/// Three cover volume: <see cref="StreamDetector.RateChange"/> (an overdispersion-adjusted count-rate test),
/// <see cref="StreamDetector.LevelShift"/> (PELT change point: halved and STAYED halved), and
/// <see cref="StreamDetector.VolumeOutlier"/> (generalized ESD on the residuals). Two thresholds on one
/// statistic agreeing would say nothing; two of these agreeing is real corroboration, which is why no single
/// detector at any strength can raise a critical on its own, and why a surplus never reaches one at all.
/// </para>
///
/// Pure and deterministic: no IO, no ML context, no clock of its own.
/// </summary>
public static class StreamAnomalyDetector
{
    /// <summary>How much each detector contributes to the ensemble confidence. The missing-data tests lead
    /// because they answer the question the surface exists for; the volume tests trail because they are
    /// corroboration.</summary>
    private static readonly IReadOnlyDictionary<StreamDetector, double> Weights =
        new Dictionary<StreamDetector, double>
        {
            [StreamDetector.Silence] = 1.0,
            [StreamDetector.NullDays] = 0.95,
            [StreamDetector.Cadence] = 0.8,
            [StreamDetector.RateChange] = 0.6,
            [StreamDetector.LevelShift] = 0.5,
            [StreamDetector.VolumeOutlier] = 0.4,
        };

    /// <summary>The detectors that may raise a finding on their own: the ones that answer "is data still
    /// arriving". The rest corroborate.</summary>
    private static readonly IReadOnlySet<StreamDetector> PrimaryDetectors =
        new HashSet<StreamDetector> { StreamDetector.Silence, StreamDetector.NullDays, StreamDetector.Cadence };

    /// <summary>The detectors that must agree before a finding may be called critical.</summary>
    public const int ConfirmationThreshold = 2;

    /// <summary>
    /// Analyses one stream's daily write statistics.
    /// </summary>
    /// <param name="buckets">The stream's per-day activity, one entry per day that had a run. Days with no
    /// run are simply absent; the detector fills the calendar itself.</param>
    /// <param name="fromUtc">The window's inclusive start, so gap and rate arithmetic is bounded by what the
    /// caller actually read rather than by the oldest surviving row.</param>
    /// <param name="asOfUtc">"Now": the day the drought and maturity windows are anchored at.</param>
    /// <param name="options">The tuning, including the declared cadence when the stream has a schedule.</param>
    public static StreamAnalysis Analyze(
        IReadOnlyList<StreamBucket> buckets, DateTime fromUtc, DateTime asOfUtc, StreamAnomalyOptions options)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(options);

        var asOfDate = asOfUtc.Date;
        var fromDate = fromUtc.Date;
        var ordered = buckets.OrderBy(b => b.Date.Date).ToList();
        var loadDays = ordered.Where(b => b.RowsWritten > 0).ToList();
        var runDays = ordered.Where(b => b.Runs > 0).ToList();

        // A stream that has never written a row has no normal, and it is not necessarily broken either: a
        // staged endpoint or an assertions-only flow reads exactly like this. Report it plainly instead of
        // inventing a verdict from an empty sample.
        if (loadDays.Count == 0)
        {
            return NothingLoadedInWindow(ordered, runDays, asOfDate, fromDate, options);
        }

        // ---- Step 1: strip reprocessing, before anything is fitted ---------------------------------------

        var trim = ReprocessingTrim.Fit(loadDays.Select(b => (double)b.RowsWritten).ToList(), options);

        // ---- Step 2: the calendar, and what this stream normally does on each kind of day ----------------

        var firstLoad = loadDays[0].Date.Date;
        var lastLoad = loadDays[^1].Date.Date;
        var lastRun = runDays.Count > 0 ? runDays[^1].Date.Date : (DateTime?)null;
        var eraStart = firstLoad > fromDate ? firstLoad : fromDate;

        // The learned rates come from the HEALTHY ERA (through the last load), never from the drought itself.
        // A stream broken for three weeks would otherwise teach the detector that empty days are what it does,
        // so the longer it stayed broken the less broken it would look.
        var calendar = BuildCalendar(ordered, eraStart, asOfDate, trim);
        var reliability = LoadReliability.Learn(calendar.Where(d => d.Date <= lastLoad).ToList(), options);
        foreach (var day in calendar)
        {
            day.ExpectedLoadRate = reliability.RateFor(day.Date);
            day.Immature = options.MaturityDays > 0 && day.Date >= asOfDate.AddDays(-(options.MaturityDays - 1));
            day.UnexpectedNull = !day.Loaded && !day.Immature && reliability.LoadsOn(day.Date);
        }

        // ---- Step 3: cadence, for the streams whose rhythm is a gap rather than a weekday ----------------

        var loadDates = loadDays.Select(b => b.Date.Date).ToList();
        var loadGaps = Gaps(loadDates);
        var inferredGap = loadGaps.Count > 0 ? RobustStatistics.Median(loadGaps) : 1.0;
        var expectedGap = Math.Max(options.ExpectedGapDaysOverride ?? inferredGap, 0.5);
        var cadenceSource = options.ExpectedGapDaysOverride is null ? "observed" : "schedule";
        var maxObservedGap = loadGaps.Count > 0 ? loadGaps.Max() : 0;
        var frequency = loadDates.Count >= 2 ? HealthCheckEngine.DetectFrequency(loadDates) : DataFrequency.Irregular;

        // ---- Step 4: the scored volume series, on the TRIMMED values -------------------------------------

        var (series, cycle) = ScoreVolumes(loadDays, loadDates, trim, frequency, asOfDate, options);
        var observedMature = series.Where(r => r is { IsImmature: false, IsNoData: 0 }).ToList();
        var shifts = PeltDetector.Detect(
            observedMature.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList());

        var pattern = DerivePattern(calendar, reliability, expectedGap, cadenceSource, cycle, asOfDate, options);
        var profile = BuildProfile(
            ordered, runDays, loadDays, calendar, frequency, expectedGap, cadenceSource, maxObservedGap,
            lastLoad, lastRun, asOfDate, series, trim, pattern, reliability);
        var points = BuildSeries(calendar, series);

        // ---- Step 5: how much of the analysis this sample can carry ---------------------------------------

        // Seven loading days is a week of a daily stream, two months of a weekly one, and half a year of a
        // monthly one. Holding the WHOLE analysis to that count made every low-frequency stream permanently
        // unjudged: a monthly vendor could go dead for a year and never be reported, because it could never
        // accumulate the loads its own rhythm forbids. So the bar is split by what each detector actually
        // needs. The volume tests need a SAMPLE and keep the count. The presence tests need a CADENCE, and a
        // cadence is established by three loads, or by one declared cron.
        var volumeReady = loadDays.Count >= options.MinObservedDays;
        var windowSpan = Math.Max((asOfDate - fromDate).TotalDays + 1, 1);
        var wouldBeLoads = windowSpan / Math.Max(expectedGap, 0.5);
        var daysSinceLastLoad = (asOfDate - lastLoad).TotalDays;

        // Is the sample small because the RHYTHM is slow (or because the stream died partway), or because we
        // simply cannot see this stream properly? Counting the loads the current drought swallowed alongside
        // the loads actually made separates the two: a monthly feed accounts for its window either way, while
        // a daily feed that has only ever loaded twice does not, and stays held back exactly as before.
        var rhythmExplainsSample = loadDays.Count + (daysSinceLastLoad / Math.Max(expectedGap, 0.5))
            >= 0.5 * wouldBeLoads;

        if (!volumeReady && !rhythmExplainsSample)
        {
            return new StreamAnalysis
            {
                Status = StreamStatus.InsufficientHistory,
                Category = "insufficient-history",
                Severity = "info",
                Confidence = 0,
                AgreeingDetectors = 0,
                Summary = $"Only {loadDays.Count} loading day(s) in the window against the " +
                    $"{wouldBeLoads:0.#} its {Days(expectedGap)} cadence implies; " +
                    $"{options.MinObservedDays} are needed before an empty day means anything. The series is " +
                    "charted, nothing is flagged.",
                Signals = QuietSignals($"held back: {loadDays.Count} of {options.MinObservedDays} loading days"),
                Profile = profile,
                Series = points,
            };
        }

        // ---- Step 6: the detectors ------------------------------------------------------------------------

        var heldBack = $"held back: {loadDays.Count} of {options.MinObservedDays} loading days, too few to " +
            $"judge volume on a {Days(expectedGap)} cadence. Whether data is still ARRIVING is still tested.";
        var signals = new List<StreamSignal>
        {
            DetectSilence(profile, cadenceSource, options),
            DetectNullDays(calendar, reliability, profile, options),
            DetectCadence(ordered, runDays, profile, options),
            volumeReady
                ? DetectRateChange(ordered, trim, eraStart, asOfDate, options)
                : Signal(StreamDetector.RateChange, fired: false, 0, heldBack),
            volumeReady
                ? DetectLevelShift(shifts, observedMature, options)
                : Signal(StreamDetector.LevelShift, fired: false, 0, heldBack),
            volumeReady
                ? DetectVolumeOutlier(series, asOfDate, options)
                : Signal(StreamDetector.VolumeOutlier, fired: false, 0, heldBack),
        };

        return Classify(signals, profile, points, ordered, options);
    }

    // ---- Step 1: reprocessing trim ---------------------------------------------------------------------

    /// <summary>
    /// The upper fence beyond which a day's load is treated as reprocessing rather than traffic, and the
    /// clamp that applies it: the higher of a Tukey far-outlier fence (<c>Q3 + k * IQR</c>) and a floor at a
    /// multiple of the median.
    /// <para>
    /// Order statistics rather than MAD, and that choice is the whole reason this works on a contaminated
    /// sample. On a near-constant stream MAD collapses to zero and falls back to a MEAN absolute deviation,
    /// which a single five-million-row backfill inflates by its own magnitude: the fence lands above the
    /// backfill and keeps it, in precisely the case the trim exists for. A quartile cannot be moved by how
    /// large an outlier is, only by how many there are. The floor then handles the opposite failure: a stream
    /// loading the same amount every day has an IQR of zero, which would otherwise put the fence on Q3 and
    /// trim ordinary variation.
    /// </para>
    /// </summary>
    private sealed record ReprocessingTrim(double Fence, int TrimmedDays)
    {
        public static ReprocessingTrim Fit(IReadOnlyList<double> loads, StreamAnomalyOptions options)
        {
            // Under four loads there is no shape to fence against, and clamping a three-day history to its own
            // quartiles would erase the only signal there is.
            if (loads.Count < 4)
            {
                return new ReprocessingTrim(double.PositiveInfinity, 0);
            }

            var q1 = RobustStatistics.Quantile(loads, 0.25);
            var q3 = RobustStatistics.Quantile(loads, 0.75);
            var median = RobustStatistics.Median(loads);
            var fence = Math.Max(
                q3 + options.ReprocessTrimIqrMultiplier * (q3 - q1),
                median * options.ReprocessTrimMedianMultiplier);
            if (fence <= 0)
            {
                return new ReprocessingTrim(double.PositiveInfinity, 0);
            }

            return new ReprocessingTrim(fence, loads.Count(v => v > fence));
        }

        public double Apply(double rows) => Math.Min(rows, Fence);

        public bool Trims(double rows) => rows > Fence;
    }

    // ---- Step 2: the calendar and the learned reliability ------------------------------------------------

    /// <summary>One calendar day of the analysed span, whether or not anything happened on it.</summary>
    private sealed class CalendarDay
    {
        public required DateTime Date { get; init; }

        public required int Runs { get; init; }

        public required int Failures { get; init; }

        public required int ExcludedBackfillRuns { get; init; }

        public required long RowsInserted { get; init; }

        public required long RowsUpdated { get; init; }

        public required long RowsDeleted { get; init; }

        public required long RowsWritten { get; init; }

        /// <summary>The volume the analysis saw: <see cref="RowsWritten"/> clamped to the reprocessing fence.</summary>
        public required double TrimmedRows { get; init; }

        public required bool Trimmed { get; init; }

        public bool Loaded => RowsWritten > 0;

        public double ExpectedLoadRate { get; set; }

        public bool UnexpectedNull { get; set; }

        public bool Immature { get; set; }
    }

    private static List<CalendarDay> BuildCalendar(
        IReadOnlyList<StreamBucket> ordered, DateTime start, DateTime end, ReprocessingTrim trim)
    {
        var byDate = ordered.ToDictionary(b => b.Date.Date);
        var calendar = new List<CalendarDay>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var bucket = byDate.GetValueOrDefault(date);
            var rows = bucket?.RowsWritten ?? 0;
            calendar.Add(new CalendarDay
            {
                Date = date,
                Runs = bucket?.Runs ?? 0,
                Failures = bucket?.Failures ?? 0,
                ExcludedBackfillRuns = bucket?.ExcludedBackfillRuns ?? 0,
                RowsInserted = bucket?.RowsInserted ?? 0,
                RowsUpdated = bucket?.RowsUpdated ?? 0,
                RowsDeleted = bucket?.RowsDeleted ?? 0,
                RowsWritten = rows,
                TrimmedRows = trim.Apply(rows),
                Trimmed = trim.Trims(rows),
            });
        }

        return calendar;
    }

    /// <summary>
    /// How reliably the stream loads on each weekday, learned from the healthy era. This is what "is a zero
    /// row day normal here" reduces to, and learning it per weekday rather than overall is what removes the
    /// largest class of false positive on a warehouse: the feeds that simply do not run at weekends. A weekday
    /// with too small a sample falls back to the stream's overall rate rather than to a coincidence.
    /// </summary>
    private sealed record LoadReliability(
        IReadOnlyDictionary<DayOfWeek, (int Days, double Rate)> ByWeekday,
        IReadOnlySet<DayOfWeek> LoadDays,
        double Overall,
        double NormalDeliveryRate,
        int MinSamples)
    {
        /// <summary>
        /// Learns the two separate things a zero-row day has to be judged against, and separating them is what
        /// makes the judgement survive an outage.
        /// <para>
        /// SHAPE is which weekdays this stream loads on at all, from a low bar that an outage cannot argue
        /// away: a feed that has never loaded at a weekend still shows 0% on Saturdays after any number of bad
        /// weeks, and a daily feed still shows well over half on every weekday after a fortnight down.
        /// </para>
        /// <para>
        /// NORMAL DELIVERY is how often it actually delivers on those shape days, and it is the MEDIAN WEEK
        /// rather than the mean day. That choice is the whole fix for the failure that a naive version has:
        /// counting empty days and comparing them against a rate learned from the same empty days is circular,
        /// and it concludes that every stream misses exactly as often as it misses. A bad fortnight moves two
        /// of nine weekly rates and leaves the middle one untouched, so the comparison finally has something
        /// uncontaminated to measure against.
        /// </para>
        /// </summary>
        public static LoadReliability Learn(IReadOnlyList<CalendarDay> era, StreamAnomalyOptions options)
        {
            if (era.Count == 0)
            {
                return new LoadReliability(
                    new Dictionary<DayOfWeek, (int, double)>(), new HashSet<DayOfWeek>(), 0, 0,
                    options.MinWeekdaySamples);
            }

            var byWeekday = era
                .GroupBy(d => d.Date.DayOfWeek)
                .ToDictionary(
                    g => g.Key,
                    g => (Days: g.Count(), Rate: (double)g.Count(d => d.Loaded) / g.Count()));
            var overall = (double)era.Count(d => d.Loaded) / era.Count;

            var loadDays = byWeekday
                .Where(w => w.Value.Days >= options.MinWeekdaySamples
                    && w.Value.Rate >= options.ExpectedLoadRateThreshold)
                .Select(w => w.Key)
                .ToHashSet();

            // Too little history for any weekday to qualify on its own: if the stream loads most days at all,
            // treat every weekday as a load day rather than concluding it has no shape.
            if (loadDays.Count == 0 && overall >= options.ExpectedLoadRateThreshold)
            {
                loadDays = Enum.GetValues<DayOfWeek>().ToHashSet();
            }

            return new LoadReliability(
                byWeekday, loadDays, overall, NormalDelivery(era, loadDays, overall),
                options.MinWeekdaySamples);
        }

        /// <summary>The median week's delivery rate over the shape days. Weeks are counted from the start of
        /// the era rather than by calendar week, so the window's own edges do not create two half weeks whose
        /// rates are noise.</summary>
        private static double NormalDelivery(
            IReadOnlyList<CalendarDay> era, IReadOnlySet<DayOfWeek> loadDays, double overall)
        {
            var shapeDays = era.Where(d => loadDays.Contains(d.Date.DayOfWeek)).ToList();
            if (shapeDays.Count == 0)
            {
                return overall;
            }

            var start = shapeDays[0].Date;
            var weekly = shapeDays
                .GroupBy(d => (int)(d.Date - start).TotalDays / 7)
                .Select(g => (double)g.Count(d => d.Loaded) / g.Count())
                .ToList();

            // Under two weeks there is no middle to take, and one week's rate is as contaminated as the mean.
            return weekly.Count >= 2
                ? RobustStatistics.Median(weekly)
                : (double)shapeDays.Count(d => d.Loaded) / shapeDays.Count;
        }

        /// <summary>How reliably the stream loads on this date's weekday, falling back to its overall rate
        /// where that weekday has too thin a sample to tell a pattern from a coincidence. This is the SHAPE
        /// rate, charted per day; the bar an empty day is judged against is <see cref="LoadsOn"/>.</summary>
        public double RateFor(DateTime date)
            => ByWeekday.TryGetValue(date.DayOfWeek, out var weekday) && weekday.Days >= MinSamples
                ? weekday.Rate
                : Overall;

        /// <summary>Whether this is a kind of day the stream loads on at all.</summary>
        public bool LoadsOn(DateTime date) => LoadDays.Contains(date.DayOfWeek);
    }

    /// <summary>
    /// Derives the table's pattern: its rhythm, the band an ordinary load falls in, and how reliably it
    /// delivers. Everything here is read off the TRIMMED volumes and the learned per-weekday rates, which is
    /// what makes it a description of normal traffic rather than of the estate's backfill history.
    /// <para>
    /// The rhythm is named from the weekdays the table reliably loads on, because that is the shape almost
    /// every warehouse feed actually has and it is the one a person recognises. A stream whose rhythm is not a
    /// calendar (every third day, every six hours) has no reliable weekday, so it falls back to describing its
    /// gap, and a stream with neither is called sporadic rather than being given a rhythm it does not have.
    /// </para>
    /// </summary>
    private static StreamPattern DerivePattern(
        IReadOnlyList<CalendarDay> calendar, LoadReliability reliability, double expectedGap,
        string cadenceSource, CycleModel? cycle, DateTime asOfDate, StreamAnomalyOptions options)
    {
        var loads = calendar.Where(d => d.Loaded).Select(d => d.TrimmedRows).ToList();
        var typical = loads.Count > 0 ? RobustStatistics.Median(loads) : 0;
        var low = loads.Count > 0 ? RobustStatistics.Quantile(loads, 0.25) : 0;
        var high = loads.Count > 0 ? RobustStatistics.Quantile(loads, 0.75) : 0;

        var loadDays = reliability.LoadDays
            .OrderBy(d => ((int)d + 6) % 7) // Monday first, the way a work week reads
            .ToList();

        var weekdaysOnly = loadDays.Count == 5
            && loadDays.All(d => d is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
        // With no reliable weekday the rhythm has to be named from the gap. The named bands are the ones a
        // person actually says, because "about every 30 days" and "monthly" are the same fact and only one of
        // them is a word: a monthly feed is the commonest low-frequency shape on a warehouse, and calling it
        // periodic tells a reader to work it out for themselves.
        var shape = loadDays.Count switch
        {
            7 => "daily",
            5 when weekdaysOnly => "weekdays",
            1 => "weekly",
            > 1 => "several-days-a-week",
            _ => expectedGap switch
            {
                <= 1.5 => "daily",
                >= 6 and <= 8 => "weekly",
                >= 12 and <= 17 => "fortnightly",
                >= 26 and <= 32 => "monthly",
                <= 45 => "periodic",
                _ => "sporadic",
            },
        };

        // Measured only over the days the table was expected to deliver on; averaging in the weekends a feed
        // has never loaded at would report every healthy weekday stream as 70% reliable.
        var expectedDays = calendar.Where(d => !d.Immature && reliability.LoadsOn(d.Date)).ToList();
        var delivered = expectedDays.Count > 0
            ? (double)expectedDays.Count(d => d.Loaded) / expectedDays.Count
            : 0;

        var rhythm = shape switch
        {
            "daily" => "every day",
            "weekdays" => "every weekday, never at weekends",
            "weekly" => loadDays.Count == 1 ? $"every {loadDays[0]}" : "about once a week",
            "several-days-a-week" => "on " + string.Join(", ", loadDays),
            "fortnightly" => "about every two weeks",
            "monthly" => "about once a month",
            "periodic" => $"about every {Days(expectedGap)}",
            _ => "on no regular rhythm",
        };
        var declared = cadenceSource == "schedule" ? " (its schedule says so)" : string.Empty;
        var band = loads.Count > 0 && high > low
            ? $"typically {Rows(typical)} (ordinary days run {Rows(low)} to {Rows(high)})"
            : $"typically {Rows(typical)}";
        var record = expectedDays.Count > 0
            ? $"; it has delivered on {delivered * 100:0}% of the {expectedDays.Count} days it was expected to"
            : string.Empty;

        var delivery = DescribeCycle(calendar, cycle, asOfDate);

        return new StreamPattern
        {
            Shape = shape,
            LoadDays = loadDays,
            TypicalRows = Math.Round(typical, 2),
            LowRows = Math.Round(low, 2),
            HighRows = Math.Round(high, 2),
            Reliability = Math.Round(reliability.NormalDeliveryRate, 4),
            Cycle = delivery,
            Description = $"Loads {rhythm}{declared}, {band}{record}." +
                (delivery is null ? string.Empty : $" On top of that, {delivery.Description}."),
        };
    }

    /// <summary>
    /// Turns a fitted cycle into the operator-facing fact: how often the bigger delivery comes, how big it
    /// actually is, when it last landed, and when the next is due. The volumes are read off the calendar's
    /// TRIMMED rows rather than off the model's offsets, because the question being answered is "what does
    /// this vendor send" and the answer should be in rows a person can check against the chart, not in a
    /// residual.
    /// </summary>
    private static StreamCycle? DescribeCycle(
        IReadOnlyList<CalendarDay> calendar, CycleModel? cycle, DateTime asOfDate)
    {
        if (cycle is null)
        {
            return null;
        }

        var loads = calendar.Where(d => d.Loaded).ToList();
        var onCycle = loads.Where(d => cycle.Occurs(d.Date)).Select(d => d.TrimmedRows).ToList();
        var ordinary = loads.Where(d => !cycle.Occurs(d.Date)).Select(d => d.TrimmedRows).ToList();
        if (onCycle.Count == 0 || ordinary.Count == 0)
        {
            // The cycle was fitted on residuals, which can in principle mark days the calendar does not count
            // as loads. Nothing to describe in rows then, and a description in residuals would be worse than
            // none.
            return null;
        }

        var cycleRows = RobustStatistics.Median(onCycle);
        var ordinaryRows = RobustStatistics.Median(ordinary);
        var heavier = cycle.Dominant.Offset > 0;
        var last = cycle.LastOccurrenceOnOrBefore(asOfDate);
        var next = cycle.NextOccurrenceAfter(asOfDate);
        var every = cycle.Kind == CycleKind.MonthDay
            ? cycle.Dominant.Phase == CycleModel.LastDayPhase
                ? "at the end of each month"
                : $"on the {Ordinal(cycle.Dominant.Phase)} of each month"
            : $"every {Days(cycle.PeriodDays)}";
        var multiple = ordinaryRows > 0
            ? $" ({cycleRows / ordinaryRows:0.#}x an ordinary day)"
            : string.Empty;

        return new StreamCycle
        {
            PeriodDays = cycle.PeriodDays,
            Monthly = cycle.Kind == CycleKind.MonthDay,
            Occurrences = onCycle.Count,
            Heavier = heavier,
            CycleRows = Math.Round(cycleRows, 2),
            OrdinaryRows = Math.Round(ordinaryRows, 2),
            Lift = cycle.Lift,
            LastOccurrenceUtc = last,
            NextExpectedUtc = next,
            Description =
                $"a {(heavier ? "heavier" : "lighter")} delivery {every} of about {Rows(cycleRows)}{multiple}, " +
                $"seen {onCycle.Count} time(s)" +
                (next is null ? string.Empty : $", next due {next.Value:yyyy-MM-dd}"),
        };
    }

    private static string Ordinal(int day) => day + (day % 100 is >= 11 and <= 13
        ? "th"
        : (day % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    // ---- Step 4: the scored volume series ----------------------------------------------------------------

    /// <summary>Scores the load volumes with the health-check stack: robust trend, the weekday pattern of what
    /// the trend does not explain, the recurring delivery cycle neither of those can express, then generalized
    /// ESD over what is left. It runs on the TRIMMED values, so a backfill cannot become the expectation every
    /// later day is measured against.</summary>
    private static (List<SeriesRow> Series, CycleModel? Cycle) ScoreVolumes(
        IReadOnlyList<StreamBucket> loadDays, IReadOnlyList<DateTime> loadDates, ReprocessingTrim trim,
        DataFrequency frequency, DateTime asOfDate, StreamAnomalyOptions options)
    {
        var series = loadDays
            .Select(b => new SeriesRow { Date = b.Date.Date, BaseValue = (float)trim.Apply(b.RowsWritten) })
            .ToList();
        var missing = HealthCheckEngine.DetectMissingDates(loadDates, frequency, expectThrough: asOfDate);
        HealthCheckEngine.AddMissingDates(series, missing);
        HealthCheckEngine.MarkImmaturePoints(series, asOfDate, options.MaturityDays);

        // No holiday calendar reaches the catalog, so the calendar features carry weekday and weekend only.
        // The weekday rhythm is what matters here anyway, and the null-day model above uses it directly.
        HealthCheckEngine.ApplyDateFeatures(series, []);
        HealthCheckEngine.Impute(series);

        var mature = series.Where(r => !r.IsImmature).ToList();
        var fitPoints = mature.Count > 0 ? mature : series;
        var trend = TrendEstimator.Fit(fitPoints.Select(r => (r.Date, (double)r.BaseValueAdjusted)).ToList());
        foreach (var row in series)
        {
            row.DetrendedLabel = (float)(row.BaseValueAdjusted - trend.ValueAt(row.Date));
        }

        // The health-check flow's baseline path, which is the right one for an estate sweep: AutoML over
        // calendar features needs a training budget per stream, and this scores hundreds in one request.
        var baseline = BaselineModel.Fit(fitPoints, r => r.DetrendedLabel);
        var cycle = options.DetectDeliveryCycles ? FitCycle(fitPoints, ref baseline, options) : null;
        foreach (var row in series)
        {
            row.PredictedValue = (float)(
                trend.ValueAt(row.Date) + baseline.Predict(row.Date) + (cycle?.Predict(row.Date) ?? 0));
        }

        HealthCheckEngine.TagAnomalies(series, options.AnomalyThreshold, options.EsdAlpha, options.MaxAnomalyFraction);
        SuppressTrivialOutliers(series, options);
        return (series, cycle);
    }

    /// <summary>
    /// Untags the statistically flagged days whose deviation is too small to matter, per
    /// <see cref="StreamAnomalyOptions.VolumeOutlierMinPercent"/>. The severity stays on the row, because it
    /// is still what the point measured; only the verdict is withdrawn. Days tagged for having no data at all
    /// are not deviations of a size and are left alone: an empty day is judged by the null-day model.
    /// </summary>
    private static void SuppressTrivialOutliers(IReadOnlyList<SeriesRow> series, StreamAnomalyOptions options)
    {
        foreach (var row in series)
        {
            if (!row.AnomalyDetected || row.AnomalyReason is null or "Missing Data")
            {
                continue;
            }

            if (DeviationPercent(row.BaseValue, row.PredictedValue) < options.VolumeOutlierMinPercent)
            {
                row.AnomalyDetected = false;
                row.AnomalyReason = null;
            }
        }
    }

    /// <summary>The size of a deviation as a share of the larger of the two values, in percent, so a surge
    /// and a shortfall of the same proportion read the same and a zero on either side is 100%.</summary>
    private static double DeviationPercent(double actual, double expected)
    {
        var scale = Math.Max(Math.Abs(actual), Math.Abs(expected));
        return scale > 0 ? Math.Abs(actual - expected) / scale * 100 : 0;
    }

    /// <summary>
    /// Learns the recurring delivery cycle and the weekday model TOGETHER, and hands back the pair that
    /// explains the series with the fewest moving parts.
    /// <para>
    /// Two components over one series have to be fitted against each other or each absorbs part of the other.
    /// A fortnightly refill lands on the same weekday every time, so half of that weekday's observations are
    /// refills, and a weekday median taken over all of them lands on whichever mode happens to have one more
    /// member. So each candidate is BACKFITTED: estimate one component on what the other leaves over, then
    /// re-estimate the other, twice, which is where it settles.
    /// </para>
    /// <para>
    /// Backfitting alone does not decide WHICH component owns the effect, because it cannot: "this weekday is
    /// a big day, and every other one of them is smaller" and "ordinary days all week, with a bigger delivery
    /// every fortnight" predict the same numbers for every day. They are the same function and a different
    /// sentence, and the sentence is the product. So both orders are fitted and the tie is broken the way a
    /// person would break it: whichever explanation needs fewer parts that depart from the ordinary level. A
    /// genuine Monday effect is one departing weekday and no cycle; a fortnightly refill is no departing
    /// weekday and one cycle phase; the inverted readings of each need two. Loss decides first, and only where
    /// the two fits genuinely disagree about the data.
    /// </para>
    /// </summary>
    private static CycleModel? FitCycle(
        IReadOnlyList<SeriesRow> fitPoints, ref BaselineModel baseline, StreamAnomalyOptions options)
    {
        var observed = fitPoints.Where(r => r.IsNoData == 0).ToList();
        if (observed.Count == 0)
        {
            return null;
        }

        var weekdayFirst = Backfit(fitPoints, observed, cycleFirst: false, options);
        var cycleFirst = Backfit(fitPoints, observed, cycleFirst: true, options);
        var chosen = Simpler(weekdayFirst, cycleFirst, observed);
        baseline = chosen.Baseline;
        return chosen.Cycle;
    }

    /// <summary>One backfitted decomposition of the detrended series into a weekday model and a delivery
    /// cycle.</summary>
    private readonly record struct Decomposition(BaselineModel Baseline, CycleModel? Cycle);

    /// <summary>Backfits the two components, starting from whichever one <paramref name="cycleFirst"/>
    /// names. Which one goes first decides which gets the benefit of the doubt on an effect they could both
    /// carry, which is exactly why both orders are tried.</summary>
    private static Decomposition Backfit(
        IReadOnlyList<SeriesRow> fitPoints, IReadOnlyList<SeriesRow> observed, bool cycleFirst,
        StreamAnomalyOptions options)
    {
        CycleModel? cycle = null;
        if (cycleFirst)
        {
            cycle = CycleModel.Fit(
                observed.Select(r => (r.Date, (double)r.DetrendedLabel)).ToList(), options.CycleMaxPeriodDays);
        }

        var baseline = BaselineModel.Fit(fitPoints, r => r.DetrendedLabel - (cycle?.Predict(r.Date) ?? 0));
        for (var pass = 0; pass < 2; pass++)
        {
            var current = baseline;
            var found = CycleModel.Fit(
                observed.Select(r => (r.Date, (double)r.DetrendedLabel - current.Predict(r.Date))).ToList(),
                options.CycleMaxPeriodDays);
            if (found is null)
            {
                break;
            }

            cycle = found;
            baseline = BaselineModel.Fit(fitPoints, r => r.DetrendedLabel - found.Predict(r.Date));
        }

        return new Decomposition(baseline, cycle);
    }

    /// <summary>Picks between two decompositions: the one that fits materially better, or failing that the one
    /// with fewer components departing from the stream's ordinary level.</summary>
    private static Decomposition Simpler(
        Decomposition weekdayFirst, Decomposition cycleFirst, IReadOnlyList<SeriesRow> observed)
    {
        var lossA = Loss(weekdayFirst, observed);
        var lossB = Loss(cycleFirst, observed);
        var tolerance = 0.01 * Math.Max(Math.Max(lossA, lossB), 1);
        if (lossB < lossA - tolerance)
        {
            return cycleFirst;
        }

        if (lossA < lossB - tolerance)
        {
            return weekdayFirst;
        }

        // The same function written two ways. Prefer the shorter sentence, and on a true tie the weekday
        // model, which is the rhythm almost every feed actually has.
        var scale = RobustStatistics.Scale(observed.Select(r => (double)r.DetrendedLabel).ToList());
        return Parts(cycleFirst, observed, scale) < Parts(weekdayFirst, observed, scale)
            ? cycleFirst
            : weekdayFirst;
    }

    private static double Loss(Decomposition model, IReadOnlyList<SeriesRow> observed)
        => observed.Sum(r => Math.Abs(
            r.DetrendedLabel - model.Baseline.Predict(r.Date) - (model.Cycle?.Predict(r.Date) ?? 0)));

    /// <summary>How many parts of a decomposition depart from the ordinary level: the weekdays carrying a
    /// level of their own, plus the cycle phases carrying an offset.</summary>
    private static int Parts(Decomposition model, IReadOnlyList<SeriesRow> observed, double scale)
    {
        var levels = observed
            .Select(r => r.Date.DayOfWeek)
            .Distinct()
            .Select(day => model.Baseline.Predict(observed.First(r => r.Date.DayOfWeek == day).Date))
            .ToList();
        var ordinary = levels.Count > 0 ? RobustStatistics.Median(levels) : 0;
        var departing = levels.Count(level => Math.Abs(level - ordinary) > CycleModel.PhaseSigma * scale);
        return departing + (model.Cycle?.Phases.Count ?? 0);
    }

    // ---- The detectors -----------------------------------------------------------------------------------

    /// <summary>
    /// The drought running RIGHT NOW: has this stream been silent for longer than its own history says it
    /// ever waits? Working on the real gap distribution rather than a calendar walk is what makes one rule
    /// cover a stream that loads hourly and a stream that loads every quarter.
    /// <para>
    /// The bar is set differently depending on where the cadence came from. An INFERRED cadence must also
    /// clear the longest gap the stream has actually survived, or a stream that genuinely pauses for ten days
    /// now and then is reported every time. A DECLARED cadence ignores that history: if the cron says daily,
    /// a ten-day gap in the past was an incident, not a licence to stay silent for eleven.
    /// </para>
    /// </summary>
    private static StreamSignal DetectSilence(
        StreamProfile profile, string cadenceSource, StreamAnomalyOptions options)
    {
        var toleranceThreshold = profile.ExpectedGapDays * options.SilenceTolerance;
        var threshold = cadenceSource == "schedule"
            ? toleranceThreshold
            : Math.Max(toleranceThreshold, profile.MaxObservedGapDays + 1);
        var days = profile.DaysSinceLastLoad;
        var fired = days > threshold;
        var cadenceText = cadenceSource == "schedule"
            ? $"its schedule fires every {Days(profile.ExpectedGapDays)}"
            : $"it normally loads every {Days(profile.ExpectedGapDays)}, longest gap so far {Days(profile.MaxObservedGapDays)}";

        return Signal(StreamDetector.Silence, fired, fired ? Saturate(days / threshold - 1) : 0,
            fired
                ? $"No data for {Days(days)} ({cadenceText}), past the {Days(threshold)} this stream tolerates."
                : $"Last load {Days(days)} ago; {cadenceText}, so nothing is overdue.");
    }

    /// <summary>
    /// The empty days inside the window, judged against what this stream normally does on that kind of day.
    /// This is the detector the surface is built around, and it sees what a current-state test cannot: the
    /// droughts that already ended, the stream that misses one Monday in three, the feed that quietly went
    /// from every day to twice a week.
    /// <para>
    /// It fires only when the empty days outnumber what the learned rates themselves predict, by a margin
    /// measured in standard deviations of that count. A stream that loads on 90% of its expected days WILL
    /// produce empty days; counting them without that comparison would report every reliable-but-imperfect
    /// stream in the estate, which is the same as reporting none of them.
    /// </para>
    /// </summary>
    private static StreamSignal DetectNullDays(
        IReadOnlyList<CalendarDay> calendar, LoadReliability reliability, StreamProfile profile,
        StreamAnomalyOptions options)
    {
        var expectedDays = calendar.Where(d => !d.Immature && reliability.LoadsOn(d.Date)).ToList();
        if (expectedDays.Count == 0)
        {
            return Signal(StreamDetector.NullDays, fired: false, 0,
                "No day of the week is one this stream reliably loads on, so no empty day is unexpected.");
        }

        var observed = profile.UnexpectedNullDays;
        var predicted = profile.PredictedNullDays;

        // The count's own standard deviation under the NORMAL delivery rate, floored at 1 so a stream with a
        // spotless history cannot make a single missed day infinitely significant. Using the median week's
        // rate rather than the window's own is what stops this being circular: a rate learned from the same
        // empty days it is counting would conclude that every stream misses exactly as often as it misses.
        var normal = reliability.NormalDeliveryRate;
        var deviation = Math.Sqrt(Math.Max(expectedDays.Count * normal * (1 - normal), 1));
        var z = (observed - predicted) / deviation;
        var fired = observed >= 2 && z >= options.NullDayZ;

        if (observed == 0)
        {
            return Signal(StreamDetector.NullDays, fired: false, 0,
                $"Loaded on all {expectedDays.Count} of the days it was expected to.");
        }

        var recent = calendar.Count(d => d.UnexpectedNull && d.Date >= profile.LastLoadUtc);
        var split = $"{profile.EmptyRunDays} where the flow ran and wrote nothing, {profile.NoRunDays} where it did not run";
        return Signal(StreamDetector.NullDays, fired, fired ? Saturate((z - options.NullDayZ) / options.NullDayZ) : 0,
            fired
                ? $"{observed} empty day(s) out of {expectedDays.Count} it was expected to load on ({split}); " +
                    $"its own history predicts {predicted.ToString("0.#", CultureInfo.InvariantCulture)}, " +
                    $"so this is {z.ToString("0.#", CultureInfo.InvariantCulture)} sigma beyond ordinary misses." +
                    (recent > 0 ? $" {recent} of them are the current gap." : string.Empty)
                : $"{observed} empty day(s) out of {expectedDays.Count} expected ({split}); its own history " +
                    $"predicts {predicted.ToString("0.#", CultureInfo.InvariantCulture)}, so this is within its normal miss rate.");
    }

    /// <summary>
    /// The same drought test on EXECUTION rather than volume. Kept separate because the two failures have
    /// different owners: a stream that stopped running is a scheduling or worker problem, and a stream that
    /// runs on time and loads nothing is an upstream problem. Reporting them as one finding sends half of them
    /// to the wrong person.
    /// </summary>
    private static StreamSignal DetectCadence(
        IReadOnlyList<StreamBucket> ordered, IReadOnlyList<StreamBucket> runDays, StreamProfile profile,
        StreamAnomalyOptions options)
    {
        if (runDays.Count < 2)
        {
            return Signal(StreamDetector.Cadence, fired: false, 0,
                $"{runDays.Count} day(s) with a run: too few to know the execution cadence.");
        }

        var gaps = Gaps(runDays.Select(b => b.Date.Date).ToList());
        var expected = Math.Max(options.ExpectedGapDaysOverride ?? RobustStatistics.Median(gaps), 0.5);
        var threshold = options.ExpectedGapDaysOverride is not null
            ? expected * options.SilenceTolerance
            : Math.Max(expected * options.SilenceTolerance, gaps.Max() + 1);
        var days = profile.DaysSinceLastRun;
        var fired = days > threshold;

        // A flow that runs and fails has not stopped running; that is the failing-flow finding, not this one.
        var failures = ordered.Sum(b => b.Failures);
        var failureNote = failures > 0 ? $" {failures} run(s) failed in the window." : string.Empty;

        return Signal(StreamDetector.Cadence, fired, fired ? Saturate(days / threshold - 1) : 0,
            fired
                ? $"The flow has not run for {Days(days)}; it normally runs every {Days(expected)}.{failureNote}"
                : $"Last run {Days(days)} ago, against a {Days(expected)} cadence.{failureNote}");
    }

    /// <summary>
    /// The count-rate test: did the recent slice deliver the volume the baseline rate predicts? Evidence
    /// rather than a finding, because a stream delivering less is usually a quieter upstream, not a broken
    /// one. It earns its place by corroborating a drought: silence plus a rate that had already collapsed is
    /// a stream that was dying before it died.
    /// <para>
    /// A plain Poisson z would flag most real streams, because warehouse loads are overdispersed: daily
    /// variance far exceeds the mean, so ordinary bursty traffic looks impossible under Poisson. The
    /// dispersion is therefore estimated from the baseline's own daily counts and divided out. Two further
    /// gates keep it honest: the baseline must expect enough rows for a normal approximation to mean anything,
    /// and the loss must be operationally large rather than merely significant.
    /// </para>
    /// </summary>
    private static StreamSignal DetectRateChange(
        IReadOnlyList<StreamBucket> ordered, ReprocessingTrim trim, DateTime eraStart, DateTime asOfDate,
        StreamAnomalyOptions options)
    {
        var recentStart = asOfDate.AddDays(-(options.RecentWindowDays - 1));
        var baselineDays = (int)(recentStart - eraStart).TotalDays;
        if (baselineDays < options.RecentWindowDays)
        {
            return Signal(StreamDetector.RateChange, fired: false, 0,
                $"{Math.Max(baselineDays, 0)} baseline day(s) before the recent slice: too short to compare a rate against.");
        }

        var byDate = ordered.ToDictionary(b => b.Date.Date, b => trim.Apply(b.RowsWritten));
        var baselineCounts = new List<double>(baselineDays);
        for (var date = eraStart; date < recentStart; date = date.AddDays(1))
        {
            baselineCounts.Add(byDate.GetValueOrDefault(date));
        }

        double recent = 0;
        for (var date = recentStart; date <= asOfDate; date = date.AddDays(1))
        {
            recent += byDate.GetValueOrDefault(date);
        }

        var dailyMean = baselineCounts.Average();
        var expected = dailyMean * options.RecentWindowDays;
        if (expected < options.RateCollapseMinExpected)
        {
            return Signal(StreamDetector.RateChange, fired: false, 0,
                $"The baseline expects only {Rows(expected)} in {options.RecentWindowDays} day(s), below the " +
                "floor where a rate test can tell a real change from luck.");
        }

        // Quasi-Poisson: the dispersion the baseline actually shows, never below the Poisson floor of 1.
        var variance = baselineCounts.Sum(c => (c - dailyMean) * (c - dailyMean)) / Math.Max(baselineCounts.Count - 1, 1);
        var dispersion = Math.Max(variance / dailyMean, 1.0);
        var z = (recent - expected) / Math.Sqrt(dispersion * expected);
        var movePercent = Math.Abs(recent - expected) / expected * 100;
        var below = z < 0;
        var significant = Math.Abs(z) >= options.RateCollapseZ && movePercent >= options.RateCollapseDropPercent;
        var fired = significant && (below || options.FlagIncreases);
        var slice = $"The last {options.RecentWindowDays} day(s) delivered {Rows(recent)} against an expected {Rows(expected)}";

        return Signal(StreamDetector.RateChange, fired,
            fired ? Saturate((Math.Abs(z) - options.RateCollapseZ) / options.RateCollapseZ) : 0,
            fired
                ? $"{slice}: {movePercent.ToString("0", CultureInfo.InvariantCulture)}% {(below ? "down" : "up")}, " +
                    $"{Math.Abs(z).ToString("0.#", CultureInfo.InvariantCulture)} sigma past ordinary variation."
                : significant
                    ? $"{slice}: {movePercent.ToString("0", CultureInfo.InvariantCulture)}% UP, which is more data than usual rather than a fault."
                    : $"{slice} ({z.ToString("+0.#;-0.#;0", CultureInfo.InvariantCulture)} sigma): within normal variation.",
            below ? StreamDirection.Below : StreamDirection.Above);
    }

    /// <summary>
    /// PELT change-point detection over the residuals: a SUSTAINED move to a new level, which no point test
    /// can see. This is the "halved and stayed halved" case, where every individual day looks unremarkable
    /// against its neighbours while the stream quietly loses most of its volume. Both directions fire, ranked
    /// apart by the ensemble (a drop is a degradation, a rise is information), but only a shift in the RECENT
    /// part of the series: an older regime change is history the trend has already absorbed.
    /// </summary>
    private static StreamSignal DetectLevelShift(
        IReadOnlyList<LevelShift> shifts, IReadOnlyList<SeriesRow> observedMature, StreamAnomalyOptions options)
    {
        if (shifts.Count == 0)
        {
            return Signal(StreamDetector.LevelShift, fired: false, 0,
                observedMature.Count < 2 * PeltDetector.MinSegmentLength
                    ? $"{observedMature.Count} scored point(s): below the {2 * PeltDetector.MinSegmentLength} a regime change needs."
                    : "No regime change in the series: the stream's level is stable.");
        }

        var last = shifts[^1];
        var recentCutoff = observedMature.Count - Math.Max(14, observedMature.Count / 3);
        var isRecent = last.Index >= recentCutoff;
        var down = last.MedianAfter < last.MedianBefore;
        var significant = last.MagnitudeSigma >= options.LevelShiftSigma;

        // The shift in the stream's own units, against the level it was expected to hold over the new regime:
        // the residual medians are what PELT segmented, and the expectation is what they are residuals of. A
        // stream expected at 107,000 whose residual median moved from +40 to -45 shifted 85 rows, and that is
        // the number that decides whether anyone should care (see LevelShiftMinPercent).
        var shiftRows = Math.Abs(last.MedianAfter - last.MedianBefore);
        var regime = observedMature.Skip(last.Index).Select(r => (double)r.PredictedValue).ToList();
        var level = regime.Count > 0 ? Math.Abs(RobustStatistics.Median(regime)) : 0;
        var shiftPercent = level > 0 ? shiftRows / level * 100 : 100;
        var material = shiftPercent >= options.LevelShiftMinPercent;

        var fired = isRecent && significant && material && (down || options.FlagIncreases);
        var date = observedMature[Math.Min(last.Index, observedMature.Count - 1)].Date;
        var magnitude = last.MagnitudeSigma.ToString("0.#", CultureInfo.InvariantCulture);
        var size = $"{Rows(shiftRows)} ({shiftPercent.ToString("0.#", CultureInfo.InvariantCulture)}% of its {Rows(level)} level)";

        var detail = fired
            ? $"The stream shifted {(down ? "down" : "up")} to a new level on {date:yyyy-MM-dd}: by {size}, {magnitude} sigma, and has stayed there."
            : !isRecent
                ? $"The newest regime change ({date:yyyy-MM-dd}, {magnitude} sigma) is old enough to be the current normal."
                : significant && !material
                    ? $"The level moved {(down ? "down" : "up")} on {date:yyyy-MM-dd} by {size}: {magnitude} sigma against this " +
                        "stream's very steady history, but far too small to be a change in what it delivers."
                    : $"The newest regime change ({date:yyyy-MM-dd}, {magnitude} sigma) is too small to call.";

        return Signal(StreamDetector.LevelShift, fired,
            fired ? Saturate((last.MagnitudeSigma - options.LevelShiftSigma) / options.LevelShiftSigma) : 0,
            detail, down ? StreamDirection.Below : StreamDirection.Above);
    }

    /// <summary>
    /// Generalized ESD over the trend-and-weekday residuals, restricted to the recent slice: a day whose
    /// volume is a statistical outlier against what this stream does on that weekday at that point in its
    /// trend. Both directions are reported, and the ensemble ranks a short day above a long one.
    /// <para>
    /// Only points ESD tagged STATISTICALLY count here, never the "Missing Data" tag. Missing-date detection
    /// walks the calendar at the detected cadence, so a weekly stream that drifts by a day produces a row of
    /// phantom missing dates. Empty days are the null-day detector's job, where they are judged against the
    /// stream's learned per-weekday reliability instead of a rigid calendar walk. The missing marks still ride
    /// in the series for the chart, where they are informative rather than actionable.
    /// </para>
    /// </summary>
    private static StreamSignal DetectVolumeOutlier(
        IReadOnlyList<SeriesRow> series, DateTime asOfDate, StreamAnomalyOptions options)
    {
        var recentStart = asOfDate.AddDays(-(options.RecentWindowDays - 1));
        var flagged = series
            .Where(r => r is { AnomalyDetected: true, IsImmature: false }
                && r.AnomalyReason is not null and not "Missing Data"
                && r.Date >= recentStart
                && (options.FlagIncreases || r.BaseValue < r.PredictedValue))
            .OrderByDescending(r => r.Severity)
            .ToList();

        if (flagged.Count == 0)
        {
            var tested = series.Count(r => r is { IsImmature: false, IsNoData: 0 });
            return Signal(StreamDetector.VolumeOutlier, fired: false, 0,
                $"No outlying load in the last {options.RecentWindowDays} day(s) across {tested} scored point(s).");
        }

        var worst = flagged[0];
        var below = worst.BaseValue < worst.PredictedValue;
        return Signal(StreamDetector.VolumeOutlier, fired: true,
            Saturate((worst.Severity - options.AnomalyThreshold) / (2 * options.AnomalyThreshold)),
            $"{worst.Date:yyyy-MM-dd} loaded {Rows(worst.BaseValue)} against an expected {Rows(worst.PredictedValue)}, " +
            $"{(below ? "short" : "over")} by {worst.Severity.ToString("0.#", CultureInfo.InvariantCulture)} sigma" +
            (flagged.Count > 1 ? $", and {flagged.Count - 1} more outlying day(s) in the slice." : "."),
            below ? StreamDirection.Below : StreamDirection.Above);
    }

    // ---- The ensemble ------------------------------------------------------------------------------------

    /// <summary>
    /// Turns the verdicts into one finding, ranked by what an operator must do about it. An outage comes
    /// first and is the only category that can be critical: data has stopped arriving, or the flow has stopped
    /// running, or the empty days outnumber what the stream's own reliability predicts. A shortfall with no
    /// missing day is next and caps at warning, because from here a quieter upstream and a broken one look
    /// identical. A surplus is last and stays at information, because more data is not an outage.
    /// </summary>
    private static StreamAnalysis Classify(
        IReadOnlyList<StreamSignal> signals, StreamProfile profile, IReadOnlyList<StreamPoint> series,
        IReadOnlyList<StreamBucket> ordered, StreamAnomalyOptions options)
    {
        var fired = signals.Where(s => s.Fired).ToList();
        var findings = fired.Where(s => s.Primary || options.PromoteVolumeFindings).ToList();
        var excludedRuns = ordered.Sum(b => b.ExcludedBackfillRuns);
        var backfillNote = excludedRuns > 0 ? $" ({excludedRuns} backfill run(s) excluded from the analysis.)" : string.Empty;
        var trimNote = profile.TrimmedLoadDays > 0
            ? $" ({profile.TrimmedLoadDays} outsized day(s) trimmed as suspected reprocessing before fitting.)"
            : string.Empty;

        if (findings.Count == 0)
        {
            // Reached only when the volume tests are demoted to corroboration
            // (PromoteVolumeFindings false): the data is still arriving, so whatever they saw is a lead.
            var evidence = fired.MaxBy(f => f.Score);
            return evidence is null
                ? new StreamAnalysis
                {
                    Status = StreamStatus.Healthy,
                    Category = "healthy",
                    Severity = "info",
                    Confidence = 0,
                    AgreeingDetectors = 0,
                    Summary = profile.Pattern.Description +
                        $" Last load {Days(profile.DaysSinceLastLoad)} ago, on pattern." + trimNote,
                    Signals = signals,
                    Profile = profile,
                    Series = series,
                }
                : new StreamAnalysis
                {
                    Status = StreamStatus.Watch,
                    Category = evidence.Direction == StreamDirection.Above ? "more-than-normal" : "less-than-normal",
                    Severity = "info",
                    Confidence = Confidence(fired),
                    AgreeingDetectors = fired.Count,
                    Summary = "Data is still arriving on schedule, in a different volume. " + evidence.Detail +
                        backfillNote + trimNote,
                    Signals = signals,
                    Profile = profile,
                    Series = series,
                };
        }

        var silence = signals.First(s => s.Detector == StreamDetector.Silence);
        var nullDays = signals.First(s => s.Detector == StreamDetector.NullDays);
        var cadence = signals.First(s => s.Detector == StreamDetector.Cadence);

        // A stream whose every run since the last load errored has not gone quiet upstream: it is failing, and
        // saying so sends it to the person who can fix it rather than to whoever owns the source system.
        var sinceLastLoad = ordered.Where(b => b.Date.Date > profile.LastLoadUtc).ToList();
        var runsSince = sinceLastLoad.Sum(b => b.Runs);
        var failing = runsSince > 0 && sinceLastLoad.Sum(b => b.Failures) == runsSince;

        // The strongest volume verdict in each direction, for the two categories no missing day explains.
        var shortfall = findings
            .Where(f => f.Direction == StreamDirection.Below).MaxBy(f => f.Score);
        var surplus = findings
            .Where(f => f.Direction == StreamDirection.Above).MaxBy(f => f.Score);

        var (category, summary) = (silence.Fired, nullDays.Fired, cadence.Fired) switch
        {
            (true, _, _) when failing => (
                "failing",
                $"No data for {Days(profile.DaysSinceLastLoad)}, and every one of the {runsSince} run(s) since " +
                "has failed. Fix the flow: the upstream is not the suspect here."),
            (true, _, true) => (
                "stalled",
                $"Stopped: no data for {Days(profile.DaysSinceLastLoad)} and no run at all for " +
                $"{Days(profile.DaysSinceLastRun)}, against a {Days(profile.ExpectedGapDays)} cadence."),
            (true, _, false) => (
                "stalled",
                $"No data for {Days(profile.DaysSinceLastLoad)} against a {Days(profile.ExpectedGapDays)} " +
                "cadence, although the flow keeps running: the upstream has stopped producing."),
            // Every empty day had a run that SUCCEEDED and wrote nothing. That is the flow reporting there was
            // nothing new, not data going missing, and the two must not read the same. An incremental stream
            // over a source that only produces on some days does this by design, and calling it missing data
            // puts a healthy stream at the top of the board next to a broken one. A sustained version of this
            // is still caught, by the silence test above, which is the case where it really does mean the
            // upstream has stopped.
            (false, true, _) when profile.NoRunDays == 0 => (
                "idle-days",
                $"{profile.UnexpectedNullDays} day(s) ran and loaded no new rows, against a history that " +
                $"predicts {profile.PredictedNullDays.ToString("0.#", CultureInfo.InvariantCulture)}. Data is " +
                $"still arriving: the last load was {Days(profile.DaysSinceLastLoad)} ago."),
            (false, true, _) => (
                "gap-days", nullDays.Detail),
            (false, false, true) => (
                "not-running",
                $"The flow has not run for {Days(profile.DaysSinceLastRun)}; its schedule or its worker has " +
                "stopped feeding it."),
            _ when shortfall is not null => ("less-than-normal", shortfall.Detail),
            _ when surplus is not null => ("more-than-normal", surplus.Detail),
            _ => ("less-than-normal", findings[0].Detail),
        };

        var zeroData = category is "stalled" or "failing" or "gap-days" or "not-running";
        var status = category switch
        {
            "stalled" or "failing" => StreamStatus.Stalled,
            "more-than-normal" or "idle-days" => StreamStatus.Watch,
            _ when fired.Count >= ConfirmationThreshold => StreamStatus.Degraded,
            _ => StreamStatus.Watch,
        };

        // The ranking, and the confirmation rule inside it. Only an outage can be critical, and only when a
        // second independent test agrees or the drought is itself unambiguous: a lone detector describes
        // something worth a look, never something worth a page. A shortfall caps at warning, because a
        // quieter upstream and a broken one look identical from here. A surplus never rises above information,
        // because more data is not an outage.
        var severity = category switch
        {
            "more-than-normal" or "idle-days" => "info",
            _ when !zeroData => fired.Count >= ConfirmationThreshold ? "warning" : "info",
            _ when fired.Count >= ConfirmationThreshold || silence.Score >= 0.5 => "critical",
            _ => "warning",
        };

        var also = fired.Count > 1
            ? " Also: " + string.Join("; ", fired
                .Where(s => !summary.Contains(s.Detail, StringComparison.Ordinal))
                .Select(s => s.Detail))
            : string.Empty;

        return new StreamAnalysis
        {
            Status = status,
            Category = category,
            Severity = severity,
            Confidence = Confidence(fired),
            AgreeingDetectors = fired.Count,
            Summary = summary + also + backfillNote + trimNote,
            Signals = signals,
            Profile = profile,
            Series = series,
        };
    }

    private static double Confidence(IReadOnlyList<StreamSignal> fired)
    {
        if (fired.Count == 0)
        {
            return 0;
        }

        var weightTotal = fired.Sum(s => Weights[s.Detector]);
        return Math.Round(fired.Sum(s => Weights[s.Detector] * s.Score) / weightTotal, 4);
    }

    // ---- Shaping -------------------------------------------------------------------------------------------

    /// <summary>
    /// The stream wrote nothing at all inside the window, which is two very different situations that must
    /// not be reported the same way.
    /// <para>
    /// If it loaded BEFORE the window (<see cref="StreamAnomalyOptions.LastKnownLoadUtc"/>) then it is not a
    /// stream without a pattern, it is a stream that has been dead longer than we looked, and it is reported
    /// as stalled with the gap measured from its real last load. That case used to read as "never loaded" at
    /// the lowest severity, which put the longest-broken tables at the bottom of the board.
    /// </para>
    /// <para>
    /// If it has never loaded at all, it is left alone: a staged endpoint, an assertions-only flow, or a
    /// brand-new pipeline reads exactly like this, and none of them is broken.
    /// </para>
    /// </summary>
    private static StreamAnalysis NothingLoadedInWindow(
        IReadOnlyList<StreamBucket> ordered, IReadOnlyList<StreamBucket> runDays, DateTime asOfDate,
        DateTime fromDate, StreamAnomalyOptions options)
    {
        var lastLoad = options.LastKnownLoadUtc?.Date;
        var silentDays = lastLoad is { } load ? (asOfDate - load).TotalDays : double.PositiveInfinity;
        var windowDays = Math.Max((asOfDate - fromDate).TotalDays + 1, 1);
        var runs = ordered.Sum(b => (long)b.Runs);
        var failures = ordered.Sum(b => (long)b.Failures);

        var profile = new StreamProfile
        {
            Pattern = new StreamPattern
            {
                Shape = "sporadic",
                LoadDays = [],
                TypicalRows = 0,
                LowRows = 0,
                HighRows = 0,
                Reliability = 0,
                Description = lastLoad is { } seen
                    ? $"Last loaded on {seen:yyyy-MM-dd}, before this window opened, so there is no current " +
                        "pattern to compare against."
                    : "No load has ever been recorded, so there is no pattern yet.",
            },
            Cadence = DataFrequency.Irregular,
            ExpectedGapDays = 0,
            CadenceSource = "observed",
            MaxObservedGapDays = 0,
            LastLoadUtc = lastLoad,
            LastRunUtc = runDays.Count > 0 ? runDays[^1].Date.Date : null,
            DaysSinceLastLoad = silentDays,
            DaysSinceLastRun = runDays.Count > 0 ? (asOfDate - runDays[^1].Date.Date).TotalDays : double.PositiveInfinity,
            RunDays = runDays.Count,
            LoadedDays = 0,
            Runs = ordered.Sum(b => (long)b.Runs),
            Failures = ordered.Sum(b => (long)b.Failures),
            TotalRowsInserted = 0,
            TotalRowsUpdated = 0,
            TotalRowsDeleted = 0,
            AvgRowsInsertedPerRun = 0,
            AvgRowsUpdatedPerRun = 0,
            AvgRowsDeletedPerRun = 0,
            AvgRowsWrittenPerLoadedDay = 0,
            MedianRowsWrittenPerLoadedDay = 0,
            TrendRowsPerDay = 0,
            ExpectedDays = 0,
            UnexpectedNullDays = 0,
            EmptyRunDays = 0,
            NoRunDays = 0,
            PredictedNullDays = 0,
            TrimmedLoadDays = 0,
            TrimFence = 0,
        };

        // Dead longer than we looked. There is no series to run detectors over, but there is nothing
        // uncertain about it either: the stream loaded before, it has not loaded since, and the gap is longer
        // than the whole window. Reported at full severity, because a table silent for months is the most
        // broken thing this surface can find, not the least.
        if (lastLoad is not null)
        {
            var failing = runs > 0 && failures == runs;
            var detail = $"No data since {lastLoad:yyyy-MM-dd} ({Days(silentDays)} ago), which is longer than " +
                $"the whole {windowDays:0} day window." +
                (runs == 0
                    ? " The flow has not run in the window either."
                    : failing
                        ? $" All {runs} run(s) in the window failed."
                        : $" It ran {runs} time(s) in the window and wrote nothing every time.");

            return new StreamAnalysis
            {
                Status = StreamStatus.Stalled,
                Category = failing ? "failing" : "stalled",
                Severity = "critical",
                Confidence = 1,
                AgreeingDetectors = 1,
                Summary = detail,
                Signals = Enum.GetValues<StreamDetector>()
                    .Select(d => d == StreamDetector.Silence
                        ? Signal(d, fired: true, 1, detail)
                        : Signal(d, fired: false, 0,
                            "No load inside the window, so there is no series for this test to run on."))
                    .ToList(),
                Profile = profile,
                Series = RawSeries(ordered),
            };
        }

        return new StreamAnalysis
        {
            Status = StreamStatus.InsufficientHistory,
            Category = runs > 0 ? "never-loaded" : "insufficient-history",
            Severity = "info",
            Confidence = 0,
            AgreeingDetectors = 0,
            Summary = runs > 0
                ? $"{runs} run(s) in the window wrote no rows, and this stream has never written any. A staged " +
                    "endpoint or an assertions-only flow reads exactly like this."
                : "No runs in the window, so there is nothing to judge.",
            Signals = QuietSignals("the stream has never written a row"),
            Profile = profile,
            Series = RawSeries(ordered),
        };
    }

    private static StreamProfile BuildProfile(
        IReadOnlyList<StreamBucket> ordered, IReadOnlyList<StreamBucket> runDays,
        IReadOnlyList<StreamBucket> loadDays, IReadOnlyList<CalendarDay> calendar, DataFrequency frequency,
        double expectedGap, string cadenceSource, double maxObservedGap, DateTime lastLoad, DateTime? lastRun,
        DateTime asOfDate, IReadOnlyList<SeriesRow> series, ReprocessingTrim trim, StreamPattern pattern,
        LoadReliability reliability)
    {
        var runs = ordered.Sum(b => (long)b.Runs);
        var inserted = ordered.Sum(b => b.RowsInserted);
        var updated = ordered.Sum(b => b.RowsUpdated);
        var deleted = ordered.Sum(b => b.RowsDeleted);
        var unexpectedNulls = calendar.Where(d => d.UnexpectedNull).ToList();
        var expectedDays = calendar.Count(d => !d.Immature && reliability.LoadsOn(d.Date));
        var mature = series.Where(r => !r.IsImmature).ToList();
        var trend = TrendEstimator.Fit(
            (mature.Count > 0 ? mature : series).Select(r => (r.Date, (double)r.BaseValueAdjusted)).ToList());

        return new StreamProfile
        {
            Pattern = pattern,
            Cadence = frequency,
            ExpectedGapDays = Math.Round(expectedGap, 2),
            CadenceSource = cadenceSource,
            MaxObservedGapDays = Math.Round(maxObservedGap, 2),
            LastLoadUtc = lastLoad,
            LastRunUtc = lastRun,
            DaysSinceLastLoad = (asOfDate - lastLoad).TotalDays,
            DaysSinceLastRun = lastRun is { } run ? (asOfDate - run).TotalDays : double.PositiveInfinity,
            RunDays = runDays.Count,
            LoadedDays = loadDays.Count,
            Runs = runs,
            Failures = ordered.Sum(b => (long)b.Failures),
            TotalRowsInserted = inserted,
            TotalRowsUpdated = updated,
            TotalRowsDeleted = deleted,
            AvgRowsInsertedPerRun = runs > 0 ? Math.Round((double)inserted / runs, 2) : 0,
            AvgRowsUpdatedPerRun = runs > 0 ? Math.Round((double)updated / runs, 2) : 0,
            AvgRowsDeletedPerRun = runs > 0 ? Math.Round((double)deleted / runs, 2) : 0,
            AvgRowsWrittenPerLoadedDay = Math.Round((double)loadDays.Sum(b => b.RowsWritten) / loadDays.Count, 2),
            MedianRowsWrittenPerLoadedDay = Math.Round(
                RobustStatistics.Median(loadDays.Select(b => (double)b.RowsWritten).ToList()), 2),
            TrendRowsPerDay = Math.Round(trend.SlopePerDay, 4),
            ExpectedDays = expectedDays,
            UnexpectedNullDays = unexpectedNulls.Count,
            EmptyRunDays = unexpectedNulls.Count(d => d.Runs > 0),
            NoRunDays = unexpectedNulls.Count(d => d.Runs == 0),
            PredictedNullDays = Math.Round(expectedDays * (1 - reliability.NormalDeliveryRate), 2),
            TrimmedLoadDays = trim.TrimmedDays,
            TrimFence = double.IsFinite(trim.Fence) ? Math.Round(trim.Fence, 2) : 0,
        };
    }

    /// <summary>Joins the calendar to the scored volume series, so each charted day carries what the detector
    /// concluded, the expectation it was judged against, and the untrimmed insert/update/delete split behind
    /// it.</summary>
    private static IReadOnlyList<StreamPoint> BuildSeries(
        IReadOnlyList<CalendarDay> calendar, IReadOnlyList<SeriesRow> series)
    {
        var scored = series.GroupBy(r => r.Date.Date).ToDictionary(g => g.Key, g => g.First());
        return calendar.Select(day =>
        {
            var row = scored.GetValueOrDefault(day.Date);
            return new StreamPoint
            {
                Date = day.Date,
                RowsWritten = day.RowsWritten,
                RowsInserted = day.RowsInserted,
                RowsUpdated = day.RowsUpdated,
                RowsDeleted = day.RowsDeleted,
                Runs = day.Runs,
                Failures = day.Failures,
                ExcludedBackfillRuns = day.ExcludedBackfillRuns,
                Expected = row is null ? 0 : Math.Round(row.PredictedValue, 2),
                Severity = row is null ? 0 : Math.Round(row.Severity, 4),
                // An unexpected empty day is a finding of this surface whether or not the volume scorer saw
                // it, so the chart marks it from the calendar rather than from the ESD tags.
                Anomaly = day.UnexpectedNull || (row?.AnomalyDetected == true && day.Loaded),
                Reason = day.UnexpectedNull ? "No data (expected a load)" : day.Loaded ? row?.AnomalyReason : null,
                Imputed = !day.Loaded,
                Immature = day.Immature,
                ExpectedLoadRate = Math.Round(day.ExpectedLoadRate, 4),
                UnexpectedNull = day.UnexpectedNull,
                Trimmed = day.Trimmed,
            };
        }).ToList();
    }

    /// <summary>The unscored series for a stream the detector could not analyse: the raw days, so the GUI
    /// still charts what happened rather than an empty panel.</summary>
    private static IReadOnlyList<StreamPoint> RawSeries(IReadOnlyList<StreamBucket> ordered)
        => ordered.Select(b => new StreamPoint
        {
            Date = b.Date.Date,
            RowsWritten = b.RowsWritten,
            RowsInserted = b.RowsInserted,
            RowsUpdated = b.RowsUpdated,
            RowsDeleted = b.RowsDeleted,
            Runs = b.Runs,
            Failures = b.Failures,
            ExcludedBackfillRuns = b.ExcludedBackfillRuns,
            Expected = 0,
            Severity = 0,
            Anomaly = false,
            Reason = null,
            Imputed = false,
            Immature = false,
            ExpectedLoadRate = 0,
            UnexpectedNull = false,
            Trimmed = false,
        }).ToList();

    private static StreamSignal Signal(
        StreamDetector detector, bool fired, double score, string detail,
        StreamDirection direction = StreamDirection.None)
        => new()
        {
            Detector = detector,
            Fired = fired,
            Score = score,
            Detail = detail,
            Primary = PrimaryDetectors.Contains(detector),
            Direction = fired ? direction : StreamDirection.None,
        };

    /// <summary>Every detector reported as not fired, with one shared reason. Used where the analysis is held
    /// back rather than passed, so the response shape never varies with the verdict.</summary>
    private static IReadOnlyList<StreamSignal> QuietSignals(string reason)
        => Enum.GetValues<StreamDetector>().Select(d => Signal(d, fired: false, 0, reason)).ToList();

    private static List<double> Gaps(IReadOnlyList<DateTime> dates)
    {
        var gaps = new List<double>(Math.Max(dates.Count - 1, 0));
        for (var i = 1; i < dates.Count; i++)
        {
            gaps.Add((dates[i].Date - dates[i - 1].Date).TotalDays);
        }

        return gaps;
    }

    /// <summary>Clamps a detector's raw evidence ratio into the [0, 1] confidence every detector reports on.</summary>
    private static double Saturate(double value) => Math.Round(Math.Clamp(value, 0, 1), 4);

    private static string Days(double days) => days switch
    {
        double.PositiveInfinity => "ever",
        < 1 => $"{days * 24:0} hour(s)",
        < 2 => "1 day",
        _ => $"{days:0.#} days",
    };

    private static string Rows(double rows) => $"{rows:#,0} row(s)";
}
