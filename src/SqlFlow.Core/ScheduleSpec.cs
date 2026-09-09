namespace SqlFlow.Core;

/// <summary>The product defaults a schedule declaration falls back to when its YAML leaves a key out.</summary>
public static class ScheduleDefaults
{
    /// <summary>
    /// How many members of one fire execute at once when the schedule does not say. Bounded rather than unbounded
    /// because the members of a wave almost always share one upstream, and a wide source (23 flows off a single
    /// modest SQL Server, say) exhausts that server's connections long before the estate runs out of workers. Four
    /// keeps a fire meaningfully parallel while leaving a small source room to breathe; a source that can take more
    /// raises it explicitly, and <c>maxConcurrency: 0</c> opts out entirely.
    /// </summary>
    public const int MaxConcurrency = 4;

    /// <summary>
    /// How recently a chained schedule's parents must have fired for the fire to count as fed by current data, when
    /// the schedule does not say. Twenty-four hours, because the overwhelming majority of parents are daily: a
    /// window shorter than the parent's own cadence would report every fire as stale, and a much longer one would
    /// stay quiet through several missed days. A schedule whose parents run on another cadence sets its own.
    /// </summary>
    public const int ParentFreshnessHours = 24;

    /// <summary>
    /// Resolves a declared <c>maxConcurrency</c> to the effective bound, where null means UNBOUNDED. The one
    /// implementation both YAML loaders and the API create path use, so a schedule means the same thing however it
    /// was declared:
    /// <list type="bullet">
    /// <item>omitted (<paramref name="declared"/> null) takes <see cref="MaxConcurrency"/>;</item>
    /// <item><c>0</c> is the explicit opt-out and returns null (unbounded);</item>
    /// <item>a positive value is taken verbatim;</item>
    /// <item>a negative value is meaningless, so <paramref name="invalid"/> is signalled and the default applies;
    /// it is never stored, because a bound below zero would leave every member of the fire unclaimable.</item>
    /// </list>
    /// </summary>
    public static int? Resolve(int? declared, out bool invalid)
    {
        invalid = false;
        switch (declared)
        {
            case null:
                return MaxConcurrency;
            case 0:
                return null;
            case > 0:
                return declared;
            default:
                invalid = true;
                return MaxConcurrency;
        }
    }
}

/// <summary>
/// A flow's schedule declaration, as written in the <c>schedule:</c> key of its YAML and carried through the
/// engine. It is written one of two ways, and the two mean different things:
/// <list type="bullet">
/// <item><description>A reference (<c>schedule: nightly</c>, or <c>schedule: [nightly, hourly]</c>) declares
/// MEMBERSHIP: this flow joins those named schedules. It carries only <see cref="Refs"/>; the cadence lives with
/// the named definition and is never copied onto the flow. A named schedule fires ONCE and runs every flow that
/// joined it as one wave-ordered group.</description></item>
/// <item><description>An inline block (<c>schedule: { cron: ... }</c>) declares a cadence on this flow, which
/// joins it implicitly. A <see cref="Name"/> publishes that cadence so other flows can join it too, making the
/// declaring flow and every referencing flow members of the one schedule.</description></item>
/// </list>
/// Purely declarative data: parsing it has no scheduling-library dependency, so it travels freely from the YAML
/// loaders through lineage collection into the catalog. What a fire RUNS is the schedule's member set; there is no
/// per-flow scope, because membership is the only selector.
/// </summary>
public sealed record ScheduleSpec
{
    /// <summary>
    /// The names of the shared schedules this flow joins, when the flow wrote its <c>schedule:</c> as a bare scalar
    /// or a sequence of names instead of an inline block. The named definitions live in a <c>schedules.yaml</c>
    /// library file or as a <see cref="Name"/>d inline block on another flow in the same repo; the repo-wide estate
    /// scan binds each reference to its definition. Empty for an inline schedule.
    /// </summary>
    public IReadOnlyList<string> Refs { get; init; } = [];

    /// <summary>The name this inline schedule publishes, when the flow's <c>schedule:</c> block carries a
    /// <c>name:</c> key. Other flows in the repo then join it with <c>schedule: &lt;name&gt;</c>, and the fire runs
    /// the declaring flow together with every flow that joined. Null for an unnamed inline block or a
    /// reference.</summary>
    public string? Name { get; init; }

    /// <summary>A standard cron expression (5 fields, or 6 with a leading seconds field), evaluated in
    /// <see cref="Timezone"/>. Null when the schedule is interval-based or is a <see cref="Refs"/> membership
    /// declaration.</summary>
    public string? Cron { get; init; }

    /// <summary>A fixed interval, in seconds, between runs. Null when the schedule is cron-based.</summary>
    public int? IntervalSeconds { get; init; }

    /// <summary>
    /// The names of the schedules this one CHAINS BEHIND, making it a shadow schedule: it has no cadence of its own
    /// and never becomes due on the clock. Written as a scalar for a single parent (<c>after: nightly</c>) or a
    /// sequence for several (<c>after: [apc_daily, norled_daily, mpc_daily]</c>); both land here, so a chain like
    /// <c>a -> b -> c</c> and a fan-in of four sources are the same mechanism.
    /// <para>
    /// With ONE parent it fires once each time that parent's fire completes. With SEVERAL it is a FAN-IN: it fires
    /// once all of them have completed a fire newer than the one it last reacted to, which is what a step reading
    /// several independent sources needs. A cross-source fact whose inputs land on four different schedules cannot
    /// express its real dependency as a single link, and pinning it to one parent's clock and hoping the other three
    /// have run is not ordering, it is a coincidence that usually holds.
    /// </para>
    /// <para>
    /// Completion means every run the parent's last fire enqueued has reached a terminal state, whatever that state
    /// is. The chain deliberately does NOT require the parent to have SUCCEEDED: these links exist to serialise work
    /// that must not overlap, and gating on success would let one failed link park every downstream schedule
    /// indefinitely, which is a far worse operational failure than running the next link after a bad one. A link that
    /// genuinely must not run on bad upstream data belongs in the same schedule as its parent, where wave ordering
    /// already skips a member whose dependency failed. <see cref="ParentFreshnessHours"/> applies the same reasoning
    /// to staleness: a parent that has not run lately is reported, never blocking.
    /// </para>
    /// Mutually exclusive with <see cref="Cron"/> and <see cref="IntervalSeconds"/>: a schedule is driven by the
    /// clock or by its parents, never both. Empty for an ordinary scheduled or referencing declaration.
    /// </summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>
    /// How recently every parent must have fired for this schedule's fire to be considered fed by current data, in
    /// hours; <see cref="ScheduleDefaults.ParentFreshnessHours"/> when the YAML says nothing, and <c>0</c> to opt out
    /// of the check entirely.
    /// <para>
    /// A parent older than the window does NOT hold the fire back. The fire proceeds and the stale parents are named
    /// on the schedule row and in the scheduler log, because the alternative is worse: blocking would let one quiet
    /// upstream silently stop a downstream fact updating, with nothing failing anywhere to show it. A fan-in step is
    /// almost always a rebuild that self-corrects on its next run, so a stale parent costs one cycle of accuracy,
    /// while blocking costs every cycle until somebody notices the absence.
    /// </para>
    /// </summary>
    public int ParentFreshnessHours { get; init; } = ScheduleDefaults.ParentFreshnessHours;

    /// <summary>Whether this schedule is driven by its parents' completion rather than by the clock.</summary>
    public bool IsChained => After.Count > 0;

    /// <summary>The IANA time zone the cron is evaluated in (for example <c>Europe/Oslo</c>); <c>UTC</c> by default.</summary>
    public string Timezone { get; init; } = "UTC";

    /// <summary>Whether the schedule is active. A disabled schedule is recorded but never fires, which pauses every
    /// member at once.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>Whether missed occurrences are backfilled. When false (the default) a fire that was missed because the
    /// host was down is skipped and the schedule resumes at the next occurrence after now. When true the schedule
    /// catches up, firing one missed occurrence per scheduler tick until it is current again.</summary>
    public bool Catchup { get; init; }

    /// <summary>
    /// How many of this schedule's members may EXECUTE at the same time, already resolved through
    /// <see cref="ScheduleDefaults.Resolve"/>: a positive bound, or null for UNBOUNDED (which the YAML asks for with
    /// <c>maxConcurrency: 0</c>). A schedule that says nothing gets <see cref="ScheduleDefaults.MaxConcurrency"/>;
    /// a value of 1 makes the fire strictly serial, one member after another.
    /// <para>
    /// The bound is per FIRE, and because a group's waves are gated (no member is claimable until every lower wave
    /// is terminal), only one wave is ever eligible at a time; this is therefore the width of the running wave.
    /// It exists because the members of a wave usually share one upstream: 23 flows reading a single modest source
    /// server can exhaust its connections even while the estate has capacity to spare. Bounding the estate's whole
    /// worker concurrency to protect one source would throttle every other source too, so the knob belongs to the
    /// schedule that fans out.
    /// </para>
    /// </summary>
    public int? MaxConcurrency { get; init; } = ScheduleDefaults.MaxConcurrency;

    /// <summary>Whether this declaration is a membership reference rather than a cadence of its own.</summary>
    public bool IsReference => Refs.Count > 0;
}
