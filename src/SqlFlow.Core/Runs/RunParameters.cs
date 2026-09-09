namespace SqlFlow.Core.Runs;

/// <summary>
/// Per-run substitution parameters: operational overrides supplied at trigger time (API, CLI flags, or the GUI),
/// carried on the queued run, and applied by the engine for THAT run only. They never touch the flow's definition
/// in git, which is what makes a backfill an audited operational act instead of a temporary YAML edit. The set is
/// deliberately typed and closed (never free-form SQL or YAML patches), so a parameter can be validated at the
/// trust boundary and can never smuggle an injection into generated statements.
/// <para>Semantics per flow kind:</para>
/// <list type="bullet">
/// <item><see cref="FullLoad"/>: ignore the watermark entirely. File flows read every file their definition
/// selects; ingestion flows read the whole (optionally filtered) source. Keyed targets still upsert, so a keyed
/// full reload is idempotent; a keyless append will duplicate (the callers warn).</item>
/// <item><see cref="BackfillFrom"/>/<see cref="BackfillTo"/>: an externally-bounded window replacing the probed
/// watermark. File flows bound file dates (the init window, inclusive from / inclusive to); ingestion flows bound
/// the incremental date column (<c>&gt;= from</c>, <c>&lt; to</c>), and an InitLoad backfill re-windows its chunk
/// plan. This is the V3 equivalent of the legacy SetFileDate: rewind by parameter, not by mutating state.</item>
/// <item><see cref="FilePattern"/>: narrows file selection to a glob for this run (reprocess one file or one
/// prefix). File flows only; ignored by relational flows.</item>
/// <item><see cref="SourceFilter"/>: an extra predicate ANDed onto the relational source read for this run,
/// REPLACING the probed watermark the way a backfill window does, so the flow's own incremental bound cannot
/// clamp the slice away. Written in the SOURCE's dialect (it is composed into the source SELECT, whose
/// identifiers and functions are the source's), so it serves SQL Server, MySQL, Oracle and Postgres alike, and
/// unlike a backfill window it needs no declared date column: any column the source exposes will do, including
/// a surrogate key (<c>AND pk &gt; 92992</c>). Relational ingestion only.</item>
/// </list>
/// </summary>
public sealed record RunParameters
{
    public static readonly RunParameters None = new();

    /// <summary>The longest accepted <see cref="FilePattern"/>; a bound so the value is always indexable and
    /// displayable.</summary>
    public const int MaxFilePatternLength = 200;

    /// <summary>The longest accepted <see cref="SourceFilter"/>. Generous enough for a real composite predicate,
    /// bounded so the value stays loggable and indexable on the run record.</summary>
    public const int MaxSourceFilterLength = 4000;

    /// <summary>Sequences a predicate continuation has no legitimate need for, and which are the lever a caller
    /// would use to break out of the WHERE into another statement or comment the rest of the query away.</summary>
    private static readonly string[] ForbiddenSourceFilterSequences = [";", "--", "/*", "*/"];

    /// <summary>Ignore the watermark and read everything the definition selects (a forced full reload).</summary>
    public bool FullLoad { get; init; }

    /// <summary>Low bound of the externally-bounded window (inclusive), UTC.</summary>
    public DateTime? BackfillFrom { get; init; }

    /// <summary>High bound of the externally-bounded window (exclusive for date columns, inclusive for file
    /// dates, matching each mechanism's native window semantics), UTC.</summary>
    public DateTime? BackfillTo { get; init; }

    /// <summary>A glob narrowing which files a file flow reads this run (for example <c>orders_2023-01*.csv</c>).</summary>
    public string? FilePattern { get; init; }

    /// <summary>An extra predicate fragment ANDed onto a relational source read for this run, in the SOURCE's own
    /// dialect and beginning with <c>AND</c>/<c>OR</c> (the same raw-append contract the flow's declared
    /// <c>source.filter</c> uses), for example <c>AND pk &gt; 92992</c>. Supplying it replaces the probed
    /// watermark for the run, so the slice it names is read whatever the target's high-water mark says. The
    /// flow's own <c>source.filter</c> still applies: this narrows the read, it does not unlock rows the
    /// definition excludes. Validated by <see cref="Validate"/>; ignored by file, copy and export flows.</summary>
    public string? SourceFilter { get; init; }

    /// <summary>Evaluate the flow's data-quality assertions against the CURRENT target and do nothing else: no
    /// source read, no staging, no load. The on-demand path for assertions declared <c>mode: manual</c> (an
    /// assertions-only run evaluates the flow's whole assertion list, auto and manual alike). Ingestion flows
    /// only; every other kind refuses the run rather than loading data the caller did not ask for.</summary>
    public bool AssertionsOnly { get; init; }

    /// <summary>Read MIN from the SOURCE instead of MAX from the TARGET for this run, so back-dated rows already
    /// sitting in the source (from an upstream backfill) are re-pulled rather than filtered out below the target's
    /// high-water mark. This is the run-time form of a flow's declared <c>fetchMinValuesFromSource</c>: when a group
    /// run backfills an anchor with a window, its downstream members carry this so the back-dated data flows through
    /// instead of stopping at staging. Relational ingestion only; a non-incremental or file/copy flow ignores it.</summary>
    public bool ReprocessFromSourceMin { get; init; }

    /// <summary>True when nothing is overridden: the run behaves exactly as its definition says.</summary>
    public bool IsDefault
        => !FullLoad && BackfillFrom is null && BackfillTo is null && string.IsNullOrWhiteSpace(FilePattern)
           && !AssertionsOnly && !ReprocessFromSourceMin && string.IsNullOrWhiteSpace(SourceFilter);

    /// <summary>True when this run is an explicit file reprocess (a full load, or a backfill window). The file-fetch
    /// engines (copy, acquire, sftp) read this to DISABLE their unchanged-file skip for the run, so every selected
    /// file re-lands with a fresh timestamp instead of being deduplicated away, which is what lets the downstream
    /// incremental flows pick it up again. A plain run keeps deduplication (an idempotent re-run transfers nothing).</summary>
    public bool ReprocessFiles => FullLoad || BackfillFrom is not null;

    /// <summary>Validates the combination, throwing <see cref="SqlFlowException"/> with a caller-safe message.
    /// Called at every trust boundary (API trigger, CLI flags) so a run can never be queued with parameters no
    /// engine path could honor.</summary>
    public void Validate()
    {
        if (AssertionsOnly && (FullLoad || BackfillFrom is not null || BackfillTo is not null || FilePattern is not null
                               || SourceFilter is not null))
        {
            throw new SqlFlowException(
                "assertionsOnly cannot be combined with fullLoad, a backfill window, a file pattern, or a source " +
                "filter: an assertions-only run reads no source data, so a selection override has nothing to apply to.");
        }

        if (FullLoad && (BackfillFrom is not null || BackfillTo is not null))
        {
            throw new SqlFlowException(
                "fullLoad and a backfill window are mutually exclusive: full load ignores every bound; a window IS the bound.");
        }

        if (BackfillFrom is { } from && BackfillTo is { } to && to <= from)
        {
            throw new SqlFlowException("backfillTo must be after backfillFrom.");
        }

        if (BackfillTo is not null && BackfillFrom is null)
        {
            throw new SqlFlowException("backfillTo requires backfillFrom (an upper bound alone is not a window).");
        }

        if (FilePattern is { } pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxFilePatternLength)
            {
                throw new SqlFlowException($"filePattern must be 1 to {MaxFilePatternLength} characters.");
            }

            if (pattern.Any(char.IsControl))
            {
                throw new SqlFlowException("filePattern must not contain control characters.");
            }
        }

        if (SourceFilter is { } sourceFilter)
        {
            var trimmed = sourceFilter.Trim();
            if (trimmed.Length == 0 || trimmed.Length > MaxSourceFilterLength)
            {
                throw new SqlFlowException($"sourceFilter must be 1 to {MaxSourceFilterLength} characters.");
            }

            if (trimmed.Any(char.IsControl))
            {
                throw new SqlFlowException("sourceFilter must not contain control characters.");
            }

            // A predicate CONTINUATION, never a standalone clause: the fragment is appended to the source read's
            // `WHERE 1=1`, so requiring a leading boolean connector is what keeps it a predicate and not the start
            // of something else. This is the same contract the flow's declared source.filter already follows.
            if (!trimmed.StartsWith("AND ", StringComparison.OrdinalIgnoreCase)
                && !trimmed.StartsWith("OR ", StringComparison.OrdinalIgnoreCase))
            {
                throw new SqlFlowException(
                    "sourceFilter must begin with AND or OR: it is appended to the source read's WHERE clause as a " +
                    "predicate continuation, for example \"AND pk > 92992\".");
            }

            foreach (var forbidden in ForbiddenSourceFilterSequences)
            {
                if (trimmed.Contains(forbidden, StringComparison.Ordinal))
                {
                    throw new SqlFlowException(
                        $"sourceFilter must not contain '{forbidden}': a predicate has no need to terminate the " +
                        "statement or open a comment, and allowing it would let the fragment rewrite the query.");
                }
            }
        }

        if (ReprocessFromSourceMin && (FullLoad || BackfillFrom is not null || BackfillTo is not null || AssertionsOnly))
        {
            throw new SqlFlowException(
                "reprocessFromSourceMin is a distinct incremental strategy and cannot be combined with fullLoad, a " +
                "backfill window, or assertionsOnly: a backfill's anchor carries the window, its descendants carry " +
                "this flag, never both on one flow.");
        }
    }

    /// <summary>A one-line human description for run logs ("full load", "window 2023-01-01 .. 2023-02-01").</summary>
    public string Describe()
    {
        if (IsDefault)
        {
            return "none";
        }

        var parts = new List<string>(5);
        if (AssertionsOnly)
        {
            parts.Add("assertions only");
        }

        if (FullLoad)
        {
            parts.Add("full load");
        }

        if (BackfillFrom is { } from)
        {
            parts.Add(BackfillTo is { } to
                ? $"window {from:yyyy-MM-dd HH:mm:ss} .. {to:yyyy-MM-dd HH:mm:ss}"
                : $"from {from:yyyy-MM-dd HH:mm:ss}");
        }

        if (!string.IsNullOrWhiteSpace(FilePattern))
        {
            parts.Add($"files '{FilePattern}'");
        }

        if (!string.IsNullOrWhiteSpace(SourceFilter))
        {
            parts.Add($"source filter '{SourceFilter.Trim()}'");
        }

        if (ReprocessFromSourceMin)
        {
            parts.Add("reprocess from source min");
        }

        return string.Join(", ", parts);
    }
}
