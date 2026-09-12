namespace SqlFlow.HealthCheck;

/// <summary>
/// One day of one data stream's write activity, as the run history records it: how many times the stream's
/// flow executed that day and how many rows it inserted, updated, and deleted. This is the input the
/// estate-wide detector analyses, so a stream needs no per-table configuration to be monitored: every flow
/// that has ever run already produces these numbers.
/// </summary>
public sealed record StreamBucket
{
    /// <summary>The UTC day the activity is attributed to.</summary>
    public required DateTime Date { get; init; }

    /// <summary>Terminal runs (succeeded or failed) recorded on the day.</summary>
    public required int Runs { get; init; }

    /// <summary>How many of <see cref="Runs"/> failed.</summary>
    public required int Failures { get; init; }

    /// <summary>Runs the caller EXCLUDED from <see cref="Runs"/> and the row counts because they were
    /// backfills or other operator-driven reprocessing. Carried rather than dropped so the series can show
    /// that something happened on the day and the analysis can still refuse to treat it as normal traffic:
    /// a backfill's millions of rows would otherwise redefine the stream's normal and make every ordinary
    /// day afterwards look like a collapse.</summary>
    public required int ExcludedBackfillRuns { get; init; }

    public required long RowsInserted { get; init; }

    public required long RowsUpdated { get; init; }

    public required long RowsDeleted { get; init; }

    /// <summary>The analysed metric: every row the stream wrote that day, whichever way it wrote it. An
    /// upsert-style flow splits its volume across inserts and updates run to run, and a purge-and-reload
    /// flow across deletes and inserts, so watching any one of the three alone reads a routine change of
    /// load shape as a collapse.</summary>
    public long RowsWritten => RowsInserted + RowsUpdated + RowsDeleted;
}

/// <summary>The tuning of the stream detector. The defaults mirror the health-check flow's own
/// (<c>AnomalyThreshold</c> 2 sigma, ESD alpha 0.025), because both analyse the same kind of series with the
/// same estimators; the stream-specific knobs govern the silence and rate tests that have no flow analogue.</summary>
public sealed record StreamAnomalyOptions
{
    /// <summary>
    /// The Tukey far-outlier multiplier building the reprocessing fence: a day above
    /// <c>Q3 + k * IQR</c> is treated as reprocessing and TRIMMED to that fence for fitting purposes, while
    /// the reported series keeps the true number.
    /// <para>
    /// This is the second line of defence behind the caller's flag-based exclusion, and it is needed because
    /// the flags catch only what an operator DECLARED. A catch-up after an outage, a re-run kicked off without
    /// backfill parameters, a source that dumped a year of history in one delivery: none of those carry a
    /// backfill flag, and any one can be a thousand times an ordinary day. Left in, a single such day drags
    /// the trend and the rate baseline so far up that every ordinary day after it reads as a collapse, which
    /// is exactly the false positive that makes a monitoring surface unusable.
    /// </para>
    /// <para>
    /// Quartiles rather than MAD, deliberately. MAD collapses to zero on a near-constant stream and falls back
    /// to a MEAN absolute deviation, which the contaminating point inflates by its own magnitude: a single
    /// backfill of five million rows would raise the fence above itself and be kept, which is the one case
    /// the trim exists for. An order statistic cannot be moved by how large an outlier is, only by how many
    /// there are.
    /// </para>
    /// </summary>
    public double ReprocessTrimIqrMultiplier { get; init; } = 3.0;

    /// <summary>The floor under the fence, as a multiple of the median load. A stream that loads almost
    /// exactly the same amount every day has an IQR of zero, which would put the Tukey fence on Q3 itself and
    /// trim ordinary variation; this keeps the fence at a sane distance from normal traffic while still
    /// catching the day that is orders of magnitude out.</summary>
    public double ReprocessTrimMedianMultiplier { get; init; } = 3.0;

    /// <summary>
    /// The share of a weekday's occurrences a stream must have loaded on for that weekday to be one it loads
    /// on AT ALL. This is what makes "no data today" mean different things for a stream that loads every
    /// morning and a stream that loads on Mondays: a warehouse feed that has never once loaded at a weekend is
    /// not reported every Saturday, and a Monday-only feed is reported the first Monday it misses.
    /// <para>
    /// Half, and deliberately not higher, because this decides SHAPE and not reliability. The two were the
    /// same knob once and it made the detector blind in exactly the case it exists for: an eight-day outage
    /// inside a sixty-day window drags every weekday's rate to around 0.78, so a high bar concluded that no
    /// weekday was one the stream reliably loads on, and the outage erased its own evidence. Shape is a
    /// question an outage cannot answer away, and how OFTEN the stream delivers on its shape days is measured
    /// separately and robustly (the median week, not the mean day).
    /// </para>
    /// </summary>
    public double ExpectedLoadRateThreshold { get; init; } = 0.5;

    /// <summary>Observations of a weekday needed before its own load rate is trusted over the stream's
    /// overall rate. Below this the weekday sample is too small to tell a pattern from a coincidence.</summary>
    public int MinWeekdaySamples { get; init; } = 3;

    /// <summary>How far the count of empty days must exceed what the learned rates predict before the
    /// missing-days test fires, in standard deviations of that count. A stream that loads on 90% of its
    /// expected days produces empty days by ordinary chance; this is what separates that from a stream
    /// producing more of them than its own history can explain.</summary>
    public double NullDayZ { get; init; } = 3.0;

    /// <summary>
    /// Lets the volume tests (outlier, level shift, rate collapse) raise findings of their own rather than
    /// only corroborating a missing-data finding. True: the surface answers all three questions a loaded
    /// table can fail at, which are zero data, less data than normal, and more data than normal.
    /// <para>
    /// The three are NOT peers, and the ranking is what keeps the board readable. Zero data is an outage and
    /// can reach critical. Less data than normal is a degradation and caps at warning, because a quieter
    /// upstream and a broken one look identical from here. More data than normal is information and stays at
    /// the lowest severity. Setting this false collapses the surface to zero-data findings alone, with the
    /// volume tests demoted to corroboration.
    /// </para>
    /// </summary>
    public bool PromoteVolumeFindings { get; init; } = true;

    /// <summary>Days on which the stream actually loaded rows, below which nothing is flagged: the robust
    /// estimators need a sample, and a stream with three loads in its whole history has no normal to deviate
    /// from. Such a stream is reported <see cref="StreamStatus.InsufficientHistory"/>, visibly, rather than
    /// quietly passing as healthy.</summary>
    public int MinObservedDays { get; init; } = 7;

    /// <summary>The robust-sigma floor a point deviation must clear to be an anomaly, on top of statistical
    /// significance (see <see cref="HealthCheckEngine.TagAnomalies"/>).</summary>
    public double AnomalyThreshold { get; init; } = 2.0;

    /// <summary>The generalized-ESD significance level per outward test.</summary>
    public double EsdAlpha { get; init; } = 0.025;

    /// <summary>The share of the series ESD may flag at most.</summary>
    public double MaxAnomalyFraction { get; init; } = 0.10;

    /// <summary>How many multiples of the stream's own expected gap may pass without data before silence is
    /// called. Two means "twice as long as it normally waits", which on a daily stream is a two-day drought
    /// and on a monthly stream two months, so one constant fits every cadence.</summary>
    public double SilenceTolerance { get; init; } = 2.0;

    /// <summary>The trailing slice the rate test compares against the rest of the window.</summary>
    public int RecentWindowDays { get; init; } = 7;

    /// <summary>The overdispersion-adjusted z the rate test must reach, in either direction, to call a
    /// change.</summary>
    public double RateCollapseZ { get; init; } = 4.0;

    /// <summary>The share of expected volume the recent slice must also have moved by, so a statistically
    /// significant but operationally trivial wobble on a huge stream stays quiet. Applied to both
    /// directions: a shortfall must lose this much, a surplus must exceed expectation by this much.</summary>
    public double RateCollapseDropPercent { get; init; } = 50.0;

    /// <summary>Rows the baseline must expect in the recent slice before the rate test may fire at all.
    /// Below this the normal approximation to a count distribution is not trustworthy, and a stream loading
    /// a handful of rows a week would be flagged by ordinary luck.</summary>
    public double RateCollapseMinExpected { get; init; } = 30.0;

    /// <summary>The robust-sigma magnitude a PELT level shift must reach to count as a regime change.</summary>
    public double LevelShiftSigma { get; init; } = 3.0;

    /// <summary>
    /// The share of the stream's level a shift must also amount to, in percent, before it is a finding.
    /// <para>
    /// Sigma alone cannot carry this test, because sigma is the stream's OWN noise and a steady stream has
    /// almost none. A feed that writes 107,300 rows every weekday give or take thirty moved to 107,220 one
    /// Tuesday and stayed there: eighty rows, a twelfth of a percent, and 4.2 sigma. That is a fact about how
    /// regular the vendor is, not a fault, and reporting it teaches the operator that this surface cries
    /// wolf. Ten percent is far below anything that reads as "less data than usual" on a chart and far above
    /// the drift a steady feed shows between its regimes, which is the same floor the point test uses.
    /// </para>
    /// </summary>
    public double LevelShiftMinPercent { get; init; } = 10.0;

    /// <summary>
    /// The share of the expected volume a single day must deviate by, in percent, before it can be flagged
    /// as an outlying day, whatever its sigma.
    /// <para>
    /// The shared tagging already carries a relative floor, but waives it once a point is overwhelmingly
    /// significant, and on a steady stream everything is: with thirty rows of noise a day that is 800 rows
    /// (0.75%) short of its 107,000 scores 12.7 sigma, passes the waiver, and is painted red. For a
    /// monitoring board the floor is absolute. A sub-ten-percent day is never the reason anyone opens a
    /// stream, and the sustained version of the same drift is the level-shift test's job, with its own
    /// floor above.
    /// </para>
    /// </summary>
    public double VolumeOutlierMinPercent { get; init; } = 10.0;

    /// <summary>Trailing days whose data may still be arriving: scored and charted, never flagged.</summary>
    public int MaturityDays { get; init; } = 1;

    /// <summary>
    /// Whether the volume expectation may learn a recurring delivery cycle the weekday model cannot express:
    /// the fortnightly refill, the every-third-day consolidation, the month-end settlement file. True.
    /// <para>
    /// Without it those vendors are reported every single time they behave exactly as they always have, which
    /// is the worst kind of false positive because it is perfectly regular and therefore trains an operator to
    /// ignore the surface. Worse, the inverse failure is invisible: a refill that never arrives just looks
    /// like an ordinary day. With the cycle in the expectation, both read correctly, and the learned pattern
    /// says so in words. See <see cref="CycleModel"/> for the bars a candidate cycle has to clear.
    /// </para>
    /// </summary>
    public bool DetectDeliveryCycles { get; init; } = true;

    /// <summary>The longest delivery cycle to look for, in days. Capped by the analysed span regardless: a
    /// period is only considered when the window covers <see cref="CycleModel.MinCycles"/> of it, so a
    /// thirty-day window can learn a ten-day cycle and a fortnightly one needs sixty.</summary>
    public int CycleMaxPeriodDays { get; init; } = CycleModel.MaxPeriodDays;

    /// <summary>
    /// Whether a stream loading MORE than expected is reported. True, and it is only safe to be true because
    /// <see cref="ReprocessTrimIqrMultiplier"/> runs first: without the trim, every backfill and catch-up in the
    /// history is itself a surge, and a surge test would spend all its output on reprocessing nobody needs
    /// telling about. With the outsized days removed, what remains is a genuine one: a duplicate load, a
    /// re-delivered file, an upstream that started sending more than it was supposed to. It is reported at
    /// the lowest severity and can never reach critical, because more data is not an outage.
    /// </summary>
    public bool FlagIncreases { get; init; } = true;

    /// <summary>
    /// The expected gap between loads in days, taken from the stream's DECLARED schedule rather than inferred
    /// from its history; null when the stream joins no schedule and the detector must infer the cadence.
    /// <para>
    /// A declared cadence is strictly better evidence than an inferred one for the failure this surface
    /// exists to catch. A stream that has been silent for three weeks teaches an inferred detector that
    /// three-week gaps are normal, so the longer it stays broken the less abnormal it looks. A cron says
    /// "every day at 04:00" no matter how long the stream has been down.
    /// </para>
    /// </summary>
    public double? ExpectedGapDaysOverride { get; init; }

    /// <summary>
    /// When this stream last wrote a row BEFORE the analysed window, or null if it never has. Supplied by the
    /// caller, which can see history the window does not.
    /// <para>
    /// Without it the detector's worst blind spot is its worst case. A table that stopped six months ago has
    /// no load inside a sixty-day window at all, so there is nothing to build a pattern from and the analysis
    /// reads it as "never loaded": the lowest severity, at the bottom of the board. The tables that have been
    /// broken longest would be the ones hardest to see, which is precisely backwards. With this, a stream that
    /// loaded before the window and not inside it is what it plainly is, a stalled one, and the gap is
    /// measured from the real last load rather than from the edge of what was read.
    /// </para>
    /// </summary>
    public DateTime? LastKnownLoadUtc { get; init; }
}

/// <summary>The independent tests the ensemble runs. They are deliberately drawn from different families:
/// a survival-style gap test, a cadence test on execution rather than volume, a robust point-outlier test,
/// a change-point test, and a count-rate test. Agreement between two of them is real corroboration in a way
/// that two thresholds on the same statistic would not be, which is what the confirmation rule rests on.</summary>
public enum StreamDetector
{
    /// <summary>No data has arrived for far longer than this stream's own history says it ever waits: the
    /// drought that is running RIGHT NOW. Primary.</summary>
    Silence,

    /// <summary>More zero-row days inside the window than the stream's own learned per-weekday reliability
    /// can explain: the droughts that already ended, which a current-state test cannot see. Primary.</summary>
    NullDays,

    /// <summary>The flow itself has stopped executing, whatever it would have loaded. Primary, and kept
    /// apart from the two above because a flow that stopped running is a scheduling or worker problem while a
    /// flow that runs on time and writes nothing is an upstream problem.</summary>
    Cadence,

    /// <summary>A point deviates from the trend-plus-weekday expectation beyond ESD's critical value.
    /// Evidence only by default.</summary>
    VolumeOutlier,

    /// <summary>PELT found a sustained regime change in the residuals, not a one-day excursion. Evidence
    /// only by default.</summary>
    LevelShift,

    /// <summary>The recent slice's volume moved against the baseline rate by more than count noise explains:
    /// the shortfall-and-surplus test, run on the trimmed series so reprocessing cannot be either.</summary>
    RateChange,
}

/// <summary>Which way a deviation went. The whole point of carrying it is that the three are not equally
/// urgent: no data at all is an outage, less than normal is a degradation, more than normal is
/// information.</summary>
public enum StreamDirection
{
    /// <summary>No direction applies: the detector measures presence rather than volume, or it did not fire.</summary>
    None,

    /// <summary>The stream delivered less than expected.</summary>
    Below,

    /// <summary>The stream delivered more than expected.</summary>
    Above,
}

/// <summary>One detector's verdict: whether it fired, how strongly, and the sentence that says why.</summary>
public sealed record StreamSignal
{
    public required StreamDetector Detector { get; init; }

    public required bool Fired { get; init; }

    /// <summary>The detector's own confidence in [0, 1]: 0 at the exact firing boundary, rising to 1 where
    /// the evidence is unambiguous. Comparable across detectors by construction, so the ensemble can average
    /// them.</summary>
    public required double Score { get; init; }

    /// <summary>The evidence, with its numbers inline, whether or not the detector fired. A detector that
    /// stayed quiet still explains what it measured, so a stream reported healthy is auditable.</summary>
    public required string Detail { get; init; }

    /// <summary>Whether this detector may raise a finding on its own. A non-primary detector that fires
    /// corroborates a primary one (raising confidence and severity) and, alone, produces a lead to check
    /// rather than a finding. See <see cref="StreamAnomalyOptions.PromoteVolumeFindings"/>.</summary>
    public required bool Primary { get; init; }

    /// <summary>Which way the deviation went, so the ensemble can rank an outage above a shortfall above a
    /// surplus instead of treating every deviation as equally bad.</summary>
    public required StreamDirection Direction { get; init; }
}

/// <summary>How a stream came out of the ensemble.</summary>
public enum StreamStatus
{
    /// <summary>Too few loads to judge; no detector was allowed to fire.</summary>
    InsufficientHistory,

    /// <summary>Every detector stayed quiet.</summary>
    Healthy,

    /// <summary>Exactly one detector fired: worth an eye, not an alarm.</summary>
    Watch,

    /// <summary>Two or more independent detectors agree the stream is behaving abnormally.</summary>
    Degraded,

    /// <summary>Data has stopped arriving: the failure this whole surface exists to catch.</summary>
    Stalled,
}

/// <summary>
/// What one table's traffic NORMALLY looks like, learned from its own history after reprocessing has been
/// excluded. This is the reference every verdict is stated against, and it is the reason a verdict is
/// legible: "silent for four days" means nothing until you know the table loads every weekday, and it means
/// nothing else once you do.
/// </summary>
public sealed record StreamPattern
{
    /// <summary>The rhythm, named: <c>daily</c>, <c>weekdays</c> (Monday to Friday, nothing at weekends),
    /// <c>weekly</c>, <c>several-days-a-week</c>, <c>fortnightly</c>, <c>monthly</c>, <c>periodic</c> (a
    /// regular gap that fits none of those bands), or <c>sporadic</c> (no rhythm the history supports).</summary>
    public required string Shape { get; init; }

    /// <summary>The weekdays this table reliably loads on, in week order. Empty for a periodic or sporadic
    /// stream, whose rhythm is a gap rather than a calendar.</summary>
    public required IReadOnlyList<DayOfWeek> LoadDays { get; init; }

    /// <summary>The typical load on a day it does load: the median of the trimmed volumes, so a backfill
    /// cannot be what "typical" means.</summary>
    public required double TypicalRows { get; init; }

    /// <summary>The middle half of its loads (the first and third quartiles of the trimmed volumes): the band
    /// an ordinary day falls in.</summary>
    public required double LowRows { get; init; }

    public required double HighRows { get; init; }

    /// <summary>How often this table NORMALLY delivers on a day it loads on, in [0, 1]: the median week's
    /// delivery rate, not the mean day's. A table at 1.0 does not normally miss, so a miss is a finding; one at
    /// 0.85 misses roughly one expected day in seven by its own nature and must not be reported for doing so.
    /// Taking the median WEEK is what keeps an outage from redefining this: a bad fortnight moves two of nine
    /// weekly rates and leaves the middle one where it was.</summary>
    public required double Reliability { get; init; }

    /// <summary>The recurring delivery cycle on top of the rhythm, when the stream has one that its weekday
    /// pattern cannot express: the vendor who ships a bigger refill every fortnight, the month-end file. Null
    /// for the streams that simply deliver the same kind of load every time.</summary>
    public StreamCycle? Cycle { get; init; }

    /// <summary>The pattern as a sentence, for a person reading one row of a board.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// A recurring delivery the stream makes on top of its ordinary rhythm, learned from its own history: how
/// often it comes, how much bigger (or smaller) it is, when it last landed, and when the next one is due.
/// <para>
/// This is what turns a fortnightly refill from a monthly false positive into a fact about the vendor. It is
/// also the only thing that makes the opposite failure visible: once the cycle is part of the expectation, a
/// refill that does not arrive is a shortfall on the day it was due rather than an ordinary day nobody looks
/// at.
/// </para>
/// </summary>
public sealed record StreamCycle
{
    /// <summary>The cycle length in days, or 0 for a monthly cycle whose length is whatever the calendar says
    /// that month.</summary>
    public required int PeriodDays { get; init; }

    /// <summary>True when the cycle repeats on a position in the calendar month (the 1st, the last day)
    /// rather than every fixed number of days.</summary>
    public required bool Monthly { get; init; }

    /// <summary>Occurrences of the cycle observed in the window: how many times this was actually seen, which
    /// is what separates a pattern from two coincidences.</summary>
    public required int Occurrences { get; init; }

    /// <summary>Whether the cycle days are HEAVIER than an ordinary day. False for the rarer stream whose
    /// cycle is a regular light day.</summary>
    public required bool Heavier { get; init; }

    /// <summary>The typical load on a cycle day, in rows.</summary>
    public required double CycleRows { get; init; }

    /// <summary>The typical load on an ordinary day, in rows: what <see cref="CycleRows"/> is a departure
    /// from.</summary>
    public required double OrdinaryRows { get; init; }

    /// <summary>The share of the unexplained variation this cycle accounts for, in [0, 1]. Reported because a
    /// cycle that explains most of the residual and one that explains a fifth of it are different claims.</summary>
    public required double Lift { get; init; }

    public required DateTime? LastOccurrenceUtc { get; init; }

    /// <summary>The next day the cycle is due, from the analysis date. The date to check when the question is
    /// "did the big one arrive".</summary>
    public required DateTime? NextExpectedUtc { get; init; }

    /// <summary>The cycle as a sentence, folded into the pattern description.</summary>
    public required string Description { get; init; }
}

/// <summary>The stream's measured normal: what it loads, how often, and where it is trending. These are the
/// averages an operator reads to sanity-check a verdict, and the numbers every detector is calibrated
/// against.</summary>
public sealed record StreamProfile
{
    /// <summary>What this table's traffic normally looks like: the reference the verdict is stated against.</summary>
    public required StreamPattern Pattern { get; init; }

    /// <summary>The load cadence inferred from the gaps between loading days.</summary>
    public required DataFrequency Cadence { get; init; }

    /// <summary>The gap in days between loads the stream is held to: its schedule's cadence when it declares
    /// one, otherwise the median gap between its loading days.</summary>
    public required double ExpectedGapDays { get; init; }

    /// <summary>Where <see cref="ExpectedGapDays"/> came from: <c>schedule</c> (declared, and therefore
    /// immune to a long outage teaching the detector that outages are normal) or <c>observed</c> (inferred
    /// from the load history).</summary>
    public required string CadenceSource { get; init; }

    /// <summary>The longest gap the stream has ever had inside the window, the bar a drought must clear.</summary>
    public required double MaxObservedGapDays { get; init; }

    public required DateTime? LastLoadUtc { get; init; }

    public required DateTime? LastRunUtc { get; init; }

    public required double DaysSinceLastLoad { get; init; }

    public required double DaysSinceLastRun { get; init; }

    /// <summary>Days in the window on which the stream ran at all.</summary>
    public required int RunDays { get; init; }

    /// <summary>Days in the window on which the stream actually wrote rows.</summary>
    public required int LoadedDays { get; init; }

    public required long Runs { get; init; }

    public required long Failures { get; init; }

    public required long TotalRowsInserted { get; init; }

    public required long TotalRowsUpdated { get; init; }

    public required long TotalRowsDeleted { get; init; }

    /// <summary>Mean rows inserted per terminal run in the window.</summary>
    public required double AvgRowsInsertedPerRun { get; init; }

    public required double AvgRowsUpdatedPerRun { get; init; }

    public required double AvgRowsDeletedPerRun { get; init; }

    /// <summary>Mean rows written per LOADING day. Averaged over loading days rather than calendar days so a
    /// weekly stream's average is its weekly load, not that load smeared across seven days.</summary>
    public required double AvgRowsWrittenPerLoadedDay { get; init; }

    /// <summary>The median load, the outlier-resistant counterpart of the average above and the number the
    /// detectors actually compare against.</summary>
    public required double MedianRowsWrittenPerLoadedDay { get; init; }

    /// <summary>The Theil-Sen slope in rows per day: a growing stream's growth is expectation, not anomaly.</summary>
    public required double TrendRowsPerDay { get; init; }

    /// <summary>Days in the window the stream was expected to load on: every mature day whose weekday its
    /// learned reliability cleared the threshold for. This is the denominator <see cref="UnexpectedNullDays"/>
    /// is read against, carried so a board can say "3 of 30" rather than a bare "3". Zero for a stream with no
    /// rhythm the history supports, which has no expected day to miss.</summary>
    public required int ExpectedDays { get; init; }

    /// <summary>Days in the window the stream was expected to load on (its learned reliability for that
    /// weekday cleared the threshold) and wrote nothing. The headline number of this surface.</summary>
    public required int UnexpectedNullDays { get; init; }

    /// <summary>Of <see cref="UnexpectedNullDays"/>, the days the flow RAN and still wrote nothing: the
    /// upstream stopped producing.</summary>
    public required int EmptyRunDays { get; init; }

    /// <summary>Of <see cref="UnexpectedNullDays"/>, the days the flow did not run at all: a scheduling or
    /// worker problem rather than an upstream one.</summary>
    public required int NoRunDays { get; init; }

    /// <summary>How many empty days the learned rates predict over the same days: the bar
    /// <see cref="UnexpectedNullDays"/> is judged against, so a stream that is 90% reliable by nature is not
    /// reported for behaving exactly that way.</summary>
    public required double PredictedNullDays { get; init; }

    /// <summary>Load days whose volume was trimmed as suspected reprocessing before anything was fitted, and
    /// the fence they were trimmed to. A non-zero count is worth seeing on its own: it says the stream had
    /// days that could not be routine traffic, whether or not anyone flagged them as a backfill.</summary>
    public required int TrimmedLoadDays { get; init; }

    public required double TrimFence { get; init; }
}

/// <summary>One analysed day of the stream: what arrived, what was expected, and how the point was judged.
/// This is what the chart draws.</summary>
public sealed record StreamPoint
{
    public required DateTime Date { get; init; }

    public required long RowsWritten { get; init; }

    public required long RowsInserted { get; init; }

    public required long RowsUpdated { get; init; }

    public required long RowsDeleted { get; init; }

    public required int Runs { get; init; }

    public required int Failures { get; init; }

    /// <summary>Backfill runs on the day, excluded from every number above and from the analysis.</summary>
    public required int ExcludedBackfillRuns { get; init; }

    /// <summary>The trend-plus-weekday expectation for the day.</summary>
    public required double Expected { get; init; }

    /// <summary>The robust studentized deviation of the point, in MAD-sigmas.</summary>
    public required double Severity { get; init; }

    public required bool Anomaly { get; init; }

    /// <summary>Why the point was flagged ("Missing Data", "Absolute Difference", "Relative Difference"),
    /// or null for a normal point.</summary>
    public required string? Reason { get; init; }

    /// <summary>True when the day had no load and the value shown is the imputed expectation.</summary>
    public required bool Imputed { get; init; }

    /// <summary>True for trailing days whose data may still be arriving.</summary>
    public required bool Immature { get; init; }

    /// <summary>How reliably this stream loads on this kind of day, learned from its own history, in [0, 1].
    /// A zero-row day is a finding only when this is high: it is the difference between a Saturday a
    /// warehouse feed has never loaded on and a Tuesday it always has.</summary>
    public required double ExpectedLoadRate { get; init; }

    /// <summary>True when the day wrote nothing AND <see cref="ExpectedLoadRate"/> says it should have.</summary>
    public required bool UnexpectedNull { get; init; }

    /// <summary>True when the day's volume was trimmed as suspected reprocessing before fitting. The row
    /// counts above are the untrimmed truth; only the analysis saw the fence.</summary>
    public required bool Trimmed { get; init; }
}

/// <summary>The ensemble's verdict on one stream: the status, the corroborating detectors, the profile they
/// judged it against, and the day-by-day series behind it.</summary>
public sealed record StreamAnalysis
{
    public required StreamStatus Status { get; init; }

    /// <summary>The machine-usable finding: <c>stalled</c>, <c>not-running</c>, <c>volume-collapse</c>,
    /// <c>gap-days</c> (days it did not run), <c>idle-days</c> (days it ran and had nothing to load),
    /// <c>failing</c>, <c>less-than-normal</c>, <c>more-than-normal</c>, <c>never-loaded</c>,
    /// <c>insufficient-history</c>, or <c>healthy</c>.</summary>
    public required string Category { get; init; }

    /// <summary>The severity in the estate's shared vocabulary (<c>critical</c> / <c>warning</c> /
    /// <c>info</c>), so a stream finding ranks beside every other advisory.</summary>
    public required string Severity { get; init; }

    /// <summary>The weighted mean confidence of the detectors that fired, in [0, 1]; 0 when none did.</summary>
    public required double Confidence { get; init; }

    /// <summary>How many independent detectors fired. Two is the confirmation bar: no single detector, at
    /// any strength, is allowed to raise a critical on its own.</summary>
    public required int AgreeingDetectors { get; init; }

    /// <summary>The finding as a sentence, with the evidence numbers inline.</summary>
    public required string Summary { get; init; }

    /// <summary>Every detector's verdict, fired or not, in declaration order.</summary>
    public required IReadOnlyList<StreamSignal> Signals { get; init; }

    public required StreamProfile Profile { get; init; }

    /// <summary>The analysed daily series, oldest first.</summary>
    public required IReadOnlyList<StreamPoint> Series { get; init; }
}
