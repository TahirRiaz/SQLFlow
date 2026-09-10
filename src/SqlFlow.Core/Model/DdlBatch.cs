namespace SqlFlow.Core.Model;

/// <summary>The lock/data cost of a DDL statement, carried from the planner's footprint to execution so the
/// applier can give each metadata-only batch and each rewrite its own transaction boundary.</summary>
public enum DdlCost
{
    MetadataOnly,
    Rewrite,
}

/// <summary>One generated DDL statement, classified by cost.</summary>
public sealed record DdlStatement
{
    public required string Text { get; init; }
    public required DdlCost Cost { get; init; }

    /// <summary>The single CREATE TABLE; a benign 2714 (object already exists) is swallowed on a create race.</summary>
    public bool IsCreateTable { get; init; }
}

/// <summary>
/// A classified DDL batch for one target object. The raw (unbracketed) <see cref="Schema"/> and
/// <see cref="Table"/> are the source of the case-canonical object lock key; the escaped, bracketed name
/// lives only inside each <see cref="DdlStatement.Text"/>.
/// </summary>
public sealed record DdlBatch
{
    public required string Schema { get; init; }
    public required string Table { get; init; }
    public required IReadOnlyList<DdlStatement> Statements { get; init; }

    /// <summary>
    /// True when the statements are semantically ORDERED and must run in list order. Schema evolution's
    /// statements are independent, so the applier normally groups the metadata-only ones into a single
    /// transaction and runs each rewrite alone afterwards, which reorders them. A batch whose statements
    /// depend on each other (the temporal transition, where SYSTEM_VERSIONING cannot be turned on until the
    /// SYSTEM_TIME period the previous statement adds exists) sets this instead: every statement runs in list
    /// order, each in its own transaction, so a partial apply leaves a state the next run can resume from.
    /// </summary>
    public bool PreserveOrder { get; init; }

    public bool HasChanges => Statements.Count > 0;
}

/// <summary>Timeouts governing how the DDL applier acquires locks, so a schema change fails fast instead of
/// blocking. AppLockTimeoutMs bounds same-object schema-run contention (sp_getapplock); DdlLockTimeoutMs is
/// the session LOCK_TIMEOUT that bounds each statement's schema-modification wait against active table users.</summary>
public sealed record SchemaApplyOptions
{
    public int AppLockTimeoutMs { get; init; } = 30_000;
    public int DdlLockTimeoutMs { get; init; } = 5_000;
    public int RewriteMaxDurationMinutes { get; init; } = 1;
}
