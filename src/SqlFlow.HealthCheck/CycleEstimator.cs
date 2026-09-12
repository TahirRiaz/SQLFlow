namespace SqlFlow.HealthCheck;

/// <summary>Which calendar a delivery cycle repeats on.</summary>
public enum CycleKind
{
    /// <summary>A fixed number of days between occurrences: the fortnightly refill, the every-third-day
    /// consolidation. Anchored at the Unix epoch, so the phase of a date never depends on where the analysed
    /// window happens to start.</summary>
    FixedDays,

    /// <summary>A position in the calendar month: the first of the month, the month-end settlement file.
    /// Distinct from a fixed-day cycle because months are not all the same length, so a vendor shipping on the
    /// 1st drifts through every fixed-day phase there is.</summary>
    MonthDay,
}

/// <summary>
/// One position in a cycle that carries a repeatable offset: which phase, how many times it was observed, and
/// how far its deliveries sit from an ordinary day.
/// </summary>
/// <param name="Phase">The phase index. For <see cref="CycleKind.FixedDays"/>, days since the Unix epoch
/// modulo the period. For <see cref="CycleKind.MonthDay"/>, the day of the month, with
/// <see cref="CycleModel.LastDayPhase"/> standing for "the last day of the month", whatever its number.</param>
/// <param name="Observations">Deliveries seen on this phase, which is what separates a pattern from a
/// coincidence.</param>
/// <param name="Offset">The robust (median) offset from an ordinary day, in rows. Positive for a heavier
/// delivery, negative for a lighter one.</param>
public sealed record CyclePhase(int Phase, int Observations, double Offset);

/// <summary>
/// A recurring delivery pattern that a weekday model cannot express: a vendor that ships a larger refill every
/// fortnight, a consolidation every third day, a settlement file on the first of the month.
/// <para>
/// The model is deliberately SPARSE. It does not learn a level for every position in the cycle, which on a
/// sixty-day window would be more free parameters than data and would fit noise beautifully. It learns an
/// offset only for the phases that repeat the same deviation, in the same direction, across the cycles they
/// were observed in; every other phase predicts nothing. In practice that is one phase, and what it encodes is
/// exactly the sentence an operator would say: "the big one lands every other Thursday".
/// </para>
/// </summary>
public sealed class CycleModel
{
    /// <summary>The phase index standing for the last day of the month in a <see cref="CycleKind.MonthDay"/>
    /// cycle. A month-end feed lands on the 28th, 30th, or 31st depending on the month, so it has no day
    /// number of its own; folding it into one phase is what lets three month-ends be recognised as three
    /// observations of one pattern rather than one observation of three.</summary>
    public const int LastDayPhase = 32;

    private readonly Dictionary<int, CyclePhase> _phases;

    private CycleModel(CycleKind kind, int periodDays, IReadOnlyList<CyclePhase> phases, double lift)
    {
        Kind = kind;
        PeriodDays = periodDays;
        Phases = phases;
        Lift = lift;
        _phases = phases.ToDictionary(p => p.Phase);
    }

    public CycleKind Kind { get; }

    /// <summary>The cycle length in days for <see cref="CycleKind.FixedDays"/>; 0 for a monthly cycle, whose
    /// length is whatever the calendar says that month.</summary>
    public int PeriodDays { get; }

    /// <summary>The phases that carry an offset, largest deviation first. Never empty.</summary>
    public IReadOnlyList<CyclePhase> Phases { get; }

    /// <summary>How much of the residual this cycle explains, in [0, 1]: the reduction in mean absolute
    /// residual from applying it. The selection criterion, and worth reporting, because a cycle that explains
    /// a fifth of the noise and one that explains four fifths are different claims.</summary>
    public double Lift { get; }

    /// <summary>The strongest phase: the one an operator means by "the big delivery".</summary>
    public CyclePhase Dominant => Phases[0];

    /// <summary>The phase index <paramref name="date"/> falls on, whether or not that phase carries an
    /// offset.</summary>
    public int PhaseOf(DateTime date) => PhaseOf(Kind, PeriodDays, date);

    /// <summary>The offset this cycle predicts for <paramref name="date"/>, in rows; zero on every phase that
    /// did not repeat.</summary>
    public double Predict(DateTime date)
        => _phases.TryGetValue(PhaseOf(date), out var phase) ? phase.Offset : 0;

    /// <summary>Whether <paramref name="date"/> is one of the cycle's occurrences.</summary>
    public bool Occurs(DateTime date) => _phases.ContainsKey(PhaseOf(date));

    /// <summary>The most recent occurrence at or before <paramref name="on"/>, or null when the search window
    /// (two cycles, or three months for a monthly one) holds none.</summary>
    public DateTime? LastOccurrenceOnOrBefore(DateTime on)
    {
        for (var back = 0; back <= SearchDays; back++)
        {
            var date = on.Date.AddDays(-back);
            if (Occurs(date))
            {
                return date;
            }
        }

        return null;
    }

    /// <summary>The next occurrence strictly after <paramref name="on"/>, or null when the search window holds
    /// none.</summary>
    public DateTime? NextOccurrenceAfter(DateTime on)
    {
        for (var forward = 1; forward <= SearchDays; forward++)
        {
            var date = on.Date.AddDays(forward);
            if (Occurs(date))
            {
                return date;
            }
        }

        return null;
    }

    private int SearchDays => Kind == CycleKind.FixedDays ? PeriodDays * 2 : 93;

    // ---- Fitting -------------------------------------------------------------------------------------------

    /// <summary>Cycles shorter than this are not a delivery pattern, they are the series itself.</summary>
    public const int MinPeriodDays = 2;

    /// <summary>The longest cycle considered. Past a month and a half a "cycle" on any window this surface
    /// reads has been seen two or three times, which is not enough to tell a pattern from an accident.</summary>
    public const int MaxPeriodDays = 45;

    /// <summary>Full cycles the analysed span must cover before a period is even considered. Three is the
    /// smallest number that can distinguish a repeating pattern from two unrelated events.</summary>
    public const int MinCycles = 3;

    /// <summary>Deliveries a phase needs before its offset is trusted.</summary>
    public const int MinPhaseObservations = 3;

    /// <summary>Observations the whole series needs before any cycle is fitted.</summary>
    public const int MinObservations = 12;

    /// <summary>How far a phase's median must sit from an ordinary day, in robust sigmas of the residual,
    /// before it counts as carrying an offset at all.</summary>
    public const double PhaseSigma = 1.5;

    /// <summary>The share of a phase's observations that must deviate the SAME way by at least one robust
    /// sigma. This is the bar that noise fails: a genuine fortnightly refill is heavy every fortnight, while a
    /// phase that happens to contain one large day and two ordinary ones is an accident of alignment.</summary>
    public const double PhaseAgreement = 0.75;

    /// <summary>The share of the mean absolute residual a cycle must explain to be accepted.</summary>
    public const double MinLift = 0.15;

    /// <summary>
    /// How much better a longer cycle must fit before it displaces a shorter one that already explains the
    /// same deliveries.
    /// <para>
    /// Every multiple of a true period fits it: a delivery every third day also lands perfectly on a
    /// six-day cycle with two active phases, and on a fifteen-day one with five. The longer candidate always
    /// scores a shade higher, because more phases means more medians to absorb the noise, so without a margin
    /// the estimator would reliably report a harmonic instead of the period and tell an operator the refill
    /// comes every twenty days when it comes every ten. Candidates are tried shortest first, so this margin
    /// makes the simplest explanation of the same evidence win.
    /// </para>
    /// </summary>
    public const double LiftMargin = 0.02;

    /// <summary>
    /// Finds the strongest recurring pattern in <paramref name="points"/>, or null when nothing clears the
    /// bars. The values are expected to be RESIDUALS: what the trend and the weekday model between them could
    /// not explain. Fitting on the raw volumes instead would let a cycle re-describe the weekday rhythm that
    /// is already modelled, and the two would then fight over the same variation.
    /// </summary>
    /// <param name="points">Observed days only (no imputed or immature ones), oldest first.</param>
    /// <param name="maxPeriodDays">The longest cycle to consider, capped at <see cref="MaxPeriodDays"/>.</param>
    public static CycleModel? Fit(IReadOnlyList<(DateTime Date, double Value)> points, int maxPeriodDays = MaxPeriodDays)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count < MinObservations)
        {
            return null;
        }

        var values = points.Select(p => p.Value).ToList();
        var scale = RobustStatistics.Scale(values);
        var baseLoss = values.Sum(Math.Abs);
        if (scale <= 0 || baseLoss <= 0)
        {
            // A residual with no spread has nothing left to explain, and dividing by it would manufacture a
            // cycle out of arithmetic.
            return null;
        }

        var ordered = points.OrderBy(p => p.Date).ToList();
        var span = (ordered[^1].Date.Date - ordered[0].Date.Date).Days + 1;
        var longest = Math.Min(Math.Min(maxPeriodDays, MaxPeriodDays), span / MinCycles);

        CycleModel? best = null;
        for (var period = MinPeriodDays; period <= longest; period++)
        {
            // Seven is the weekday model's own period: a cycle there could only re-learn what the weekday
            // medians already carry, and letting both hold it would split one effect across two components.
            if (period == 7)
            {
                continue;
            }

            var candidate = FitCandidate(ordered, CycleKind.FixedDays, period, scale, baseLoss);
            best = Better(best, candidate);
        }

        // A monthly pattern needs three months to be seen three times, which only the widest windows give.
        if (span >= 28 * MinCycles)
        {
            best = Better(best, FitCandidate(ordered, CycleKind.MonthDay, 0, scale, baseLoss));
        }

        return best;
    }

    /// <summary>Prefers the stronger cycle, and on a tie the shorter one: two explanations of the same
    /// evidence, the simpler wins.</summary>
    private static CycleModel? Better(CycleModel? incumbent, CycleModel? challenger)
    {
        if (challenger is null)
        {
            return incumbent;
        }

        if (incumbent is null || challenger.Lift > incumbent.Lift + LiftMargin)
        {
            return challenger;
        }

        return incumbent;
    }

    private static CycleModel? FitCandidate(
        IReadOnlyList<(DateTime Date, double Value)> points, CycleKind kind, int periodDays, double scale,
        double baseLoss)
    {
        List<CyclePhase> active = [];
        foreach (var group in points.GroupBy(p => PhaseOf(kind, periodDays, p.Date)))
        {
            var observations = group.Select(p => p.Value).ToList();
            if (observations.Count < MinPhaseObservations)
            {
                continue;
            }

            var offset = RobustStatistics.Median(observations);
            if (Math.Abs(offset) < PhaseSigma * scale)
            {
                continue;
            }

            // Every occurrence must lean the same way. This is what a real delivery pattern does and what an
            // accidental alignment of one big day with two ordinary ones does not.
            var sign = Math.Sign(offset);
            var agreeing = observations.Count(v => Math.Sign(v) == sign && Math.Abs(v) >= scale);
            if (agreeing < Math.Ceiling(PhaseAgreement * observations.Count))
            {
                continue;
            }

            active.Add(new CyclePhase(group.Key, observations.Count, offset));
        }

        if (kind == CycleKind.FixedDays && periodDays % 7 == 0)
        {
            active = WithoutWeekdayEffects(active, periodDays);
        }

        // A "cycle" that carries an offset on half its phases is not describing an event, it is redescribing
        // the series with one parameter per point.
        var phaseCount = kind == CycleKind.FixedDays ? periodDays : 31;
        if (active.Count == 0 || active.Count > Math.Max(1, phaseCount / 2))
        {
            return null;
        }

        var offsets = active.ToDictionary(p => p.Phase, p => p.Offset);
        var afterLoss = points.Sum(p => Math.Abs(
            p.Value - (offsets.TryGetValue(PhaseOf(kind, periodDays, p.Date), out var offset) ? offset : 0)));
        var lift = 1 - (afterLoss / baseLoss);
        if (lift < MinLift)
        {
            return null;
        }

        return new CycleModel(
            kind, periodDays,
            active.OrderByDescending(p => Math.Abs(p.Offset)).ToList(),
            Math.Round(lift, 4));
    }

    /// <summary>
    /// Drops the phases that are really a weekday effect wearing a cycle's clothes.
    /// <para>
    /// Every multiple of seven can re-describe a weekday rhythm, with one phase per occurrence of that weekday
    /// instead of one weekday median, and it always fits a shade better because it has more parameters. A feed
    /// that is quiet at weekends would then be reported as "a lighter delivery every 14 days", which is true
    /// arithmetic and a useless sentence. The tell is coverage: when EVERY phase belonging to one weekday
    /// carries an offset in the same direction, the effect does not vary within that weekday, so it is the
    /// weekday, and the weekday model owns it. When only some of them do, the effect genuinely subdivides the
    /// weekday, which is what a fortnightly refill landing on every other Thursday actually is.
    /// </para>
    /// </summary>
    private static List<CyclePhase> WithoutWeekdayEffects(IReadOnlyList<CyclePhase> active, int periodDays)
    {
        var phasesPerWeekday = periodDays / 7;
        return active
            .GroupBy(p => p.Phase % 7)
            .Where(g => g.Count() < phasesPerWeekday || g.Select(p => Math.Sign(p.Offset)).Distinct().Count() > 1)
            .SelectMany(g => g)
            .ToList();
    }

    private static int PhaseOf(CycleKind kind, int periodDays, DateTime date)
    {
        if (kind == CycleKind.MonthDay)
        {
            return date.Day == DateTime.DaysInMonth(date.Year, date.Month) ? LastDayPhase : date.Day;
        }

        // Anchored at the epoch rather than at the first observation, so a stream's phase is a property of the
        // calendar and does not shift when the window moves by a day.
        var days = (date.Date - DateTime.UnixEpoch.Date).Days;
        return ((days % periodDays) + periodDays) % periodDays;
    }
}
