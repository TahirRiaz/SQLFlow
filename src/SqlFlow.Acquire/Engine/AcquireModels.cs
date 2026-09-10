namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Per-run knobs for one acquisition run, all optional: the debugger's dry-run/probe/page-cap, caller-supplied
/// runtime parameter values overriding the flow's declared <c>params:</c> defaults, and an externally-bounded
/// window (the typed backfill contract) that replaces every date-window iteration's own from/to for this run.
/// </summary>
public sealed record AcquireRunOverrides
{
    public static readonly AcquireRunOverrides None = new();

    /// <summary>Fetch and count but write nothing (the debugger's Test invoke).</summary>
    public bool DryRun { get; init; }

    /// <summary>The debugger capture sink; null on a normal run.</summary>
    public IAcquireProbe? Probe { get; init; }

    /// <summary>Caps pages per request pipeline below the flow's own maxPages (the debugger's safety cap).</summary>
    public int? MaxPagesOverride { get; init; }

    /// <summary>Runtime values for the flow's declared <c>params:</c>, overriding their defaults for this run.</summary>
    public IReadOnlyDictionary<string, string>? Params { get; init; }

    /// <summary>Backfill window low bound (inclusive): replaces every date-window iteration's <c>from</c> this run.</summary>
    public DateTimeOffset? WindowFrom { get; init; }

    /// <summary>Backfill window high bound (exclusive): replaces every date-window iteration's <c>to</c> this run.</summary>
    public DateTimeOffset? WindowTo { get; init; }

    /// <summary>This run is an explicit reprocess (a full load or backfill window), so the landing pipeline disables
    /// its unchanged-file skip: a re-fetched byte-identical payload is re-written with a fresh timestamp rather than
    /// left untouched, so the downstream incremental flows pick it up again.</summary>
    public bool ReprocessFiles { get; init; }
}

/// <summary>One fetched payload handed by a transport to the landing pipeline. <see cref="RecordCount"/> is -1 when
/// the transport cannot cheaply count records (non-JSON), which the pipeline treats as "always land".</summary>
public sealed record LandedItem(
    ReadOnlyMemory<byte> Content,
    string? ContentType,
    string Discriminator,
    int RecordCount,
    IReadOnlyDictionary<string, string>? Headers);

/// <summary>A record of one file written to the raw zone, for the run artifact and the debugger.</summary>
public sealed record LandedFile(string Location, long Bytes, string? ContentType, int RecordCount);

/// <summary>
/// The result of one acquisition run: success plus the counters, the watermark transition, and the per-file
/// manifest. Serialized into <c>run.json</c> and projected into the control-plane run history and the debugger.
/// </summary>
public sealed record AcquireRunResult
{
    public required Guid RunId { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public double DurationSeconds { get; init; }

    /// <summary>How many request pipelines (iteration combinations) ran.</summary>
    public int Iterations { get; init; }

    /// <summary>How many HTTP pages / listed objects were fetched.</summary>
    public int PagesFetched { get; init; }

    /// <summary>How many payloads reached the raw zone (a page skipped as empty does not count). This INCLUDES the
    /// <see cref="Unchanged"/> ones, which resolved to a path that already held byte-identical content and so left
    /// the target untouched: read it as "files landed", not "files newly written". A summary that quotes this
    /// number alone reads as new data arriving even on a run that wrote nothing, so report it together with
    /// <see cref="Unchanged"/>.</summary>
    public int FilesWritten { get; init; }

    /// <summary>How many of <see cref="FilesWritten"/> were byte-identical to what the target already held, so the
    /// blob was left untouched: no last-modified bump, and deliberately no downstream re-trigger. A rolling-window
    /// feed re-fetches the same days on every run, so this is the normal steady state, and it is the difference
    /// between "the source produced nothing new" and "the source is broken".</summary>
    public int Unchanged { get; init; }

    /// <summary>How many fetched payloads were skipped because they were empty.</summary>
    public int Skipped { get; init; }

    /// <summary>How many fan-out requests were abandoned on a tolerated non-2xx status (reliability.skipStatusCodes).
    /// Distinct from <see cref="Skipped"/>: those payloads were fetched and found empty, these were never fetched.</summary>
    public int SkippedRequests { get; init; }

    public long BytesWritten { get; init; }

    /// <summary>The resolved raw-zone base the files landed under.</summary>
    public string? LandedBase { get; init; }

    public string? WatermarkBefore { get; init; }
    public string? WatermarkAfter { get; init; }

    public IReadOnlyList<LandedFile> Files { get; init; } = [];
}
