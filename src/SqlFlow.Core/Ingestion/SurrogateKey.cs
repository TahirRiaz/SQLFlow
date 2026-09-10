namespace SqlFlow.Core.Ingestion;

/// <summary>
/// One surrogate-key configuration attached to an ingestion flow (legacy flw.SurrogateKey row). For each
/// distinct business key in <see cref="KeyColumns"/> it generates an IDENTITY-backed surrogate in the lookup
/// table <see cref="SurrogateTable"/> (get-or-create: insert only keys not already present), then writes the
/// assigned surrogate back onto the flow's loaded target. The surrogate is generated on <see cref="Server"/>
/// (the legacy SurrogateServer): empty means the flow's own target (local), a different registry alias means a
/// remote/production server reached over a second connection. Immutable.
/// </summary>
public sealed record SurrogateKeySpec
{
    /// <summary>flw.SurrogateKey.SurrogateKeyID (the config row identity).</summary>
    public int SurrogateKeyId { get; init; }

    /// <summary>The ingestion flow this surrogate config attaches to (legacy FlowID).</summary>
    public int FlowId { get; init; }

    /// <summary>The data-source alias where keys are generated (legacy SurrogateServer). Null/blank means the
    /// flow's target server (local); a different alias than the flow's target means remote.</summary>
    public string? Server { get; init; }

    /// <summary>The three-part surrogate-key lookup table (legacy SurrogateDbSchTbl).</summary>
    public required RelationalObject SurrogateTable { get; init; }

    /// <summary>The IDENTITY surrogate column name (legacy SurrogateColumn).</summary>
    public required string SurrogateColumn { get; init; }

    /// <summary>The source/base business-key columns (legacy KeyColumns); drives the DISTINCT and the joins.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Optional lookup-table key-column names the source keys map onto (legacy sKeyColumns). Empty
    /// means the lookup table uses the same names as <see cref="KeyColumns"/>; when set it must have the same
    /// count as <see cref="KeyColumns"/> (positional pairing).</summary>
    public IReadOnlyList<string> SKeyColumns { get; init; } = [];

    /// <summary>Raw T-SQL run on the surrogate connection BEFORE generation (legacy PreProcess; gate length &gt; 2).</summary>
    public string? PreProcess { get; init; }

    /// <summary>Raw T-SQL run on the surrogate connection AFTER generation/push-back (legacy PostProcess; gate length &gt; 2).</summary>
    public string? PostProcess { get; init; }

    /// <summary>Lineage linkage to the target object (legacy ToObjectMK); metadata only.</summary>
    public int? ToObjectMK { get; init; }

    /// <summary>The connection reference understood by the resolver (<c>@Server</c>), or null when local.</summary>
    public string? ConnectionReference => string.IsNullOrWhiteSpace(Server) ? null : "@" + Server.Trim();
}

/// <summary>The outcome of one surrogate-key spec. Log-only: a failure is surfaced here but never rolls back
/// the committed load (the same contract as <see cref="AssertionResult"/>).</summary>
public sealed record SurrogateKeyResult
{
    public required int SurrogateKeyId { get; init; }

    public required string SurrogateTable { get; init; }

    public required string SurrogateColumn { get; init; }

    public bool IsRemote { get; init; }

    /// <summary>Rows inserted into the surrogate lookup table (new distinct business keys).</summary>
    public long KeysGenerated { get; init; }

    /// <summary>Base/target rows stamped with a surrogate value.</summary>
    public long RowsStamped { get; init; }

    public bool Executed { get; init; }

    public string? Error { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The SQL executed for this spec, in order (pre-hook, ensure-table, generation, push-back,
    /// post-hook), surfaced for the run's SQL trace.</summary>
    public IReadOnlyList<string> Statements { get; init; } = [];
}

/// <summary>
/// Generates surrogate keys for a flow's <see cref="SurrogateKeySpec"/> configs against the loaded target and
/// writes them back, post-load and post-commit. A two-mode seam like the run log and assertion runner:
/// without-database mode uses <see cref="NullSurrogateKeyExecutor"/> (runs nothing); with-database mode injects
/// the SQL-backed executor. Log-only: a surrogate outcome never fails or rolls back the committed load.
/// </summary>
public interface ISurrogateKeyExecutor
{
    Task<IReadOnlyList<SurrogateKeyResult>> RunAsync(IngestionFlow flow, string targetConnectionString, CancellationToken ct = default);
}

/// <summary>The without-database default: generates no surrogate keys.</summary>
public sealed class NullSurrogateKeyExecutor : ISurrogateKeyExecutor
{
    public static readonly NullSurrogateKeyExecutor Instance = new();

    public Task<IReadOnlyList<SurrogateKeyResult>> RunAsync(IngestionFlow flow, string targetConnectionString, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SurrogateKeyResult>>([]);
}
