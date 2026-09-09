namespace SqlFlow.Core.Comparison;

/// <summary>
/// What a baseline comparison measures. The three modes are a deliberate ladder, and an operator is meant to
/// climb it in order: <see cref="Inventory"/> says which tables disagree at all, <see cref="Schema"/> says
/// whether a table's shape still matches (and therefore whether a straight transfer is even legal), and
/// <see cref="Data"/> decomposes one table's disagreement into duplicates, missing keys, and value drift.
/// </summary>
public enum BaselineCompareMode
{
    /// <summary>Object inventory and row counts across a whole schema, both sides.</summary>
    Inventory,

    /// <summary>One object's columns, position by position: name, type, nullability, identity.</summary>
    Schema,

    /// <summary>One object's rows: count decomposition, bidirectional key anti-join, and value parity.</summary>
    Data,
}

/// <summary>
/// The ask for one comparison between the NEW V3 estate and the OLD production baseline. The new side is the
/// task's own connection; the old side is reached server-side through a configured linked server, so the
/// comparison runs in the engine and a billion-row anti-join never travels to a worker node.
/// </summary>
public sealed record BaselineComparisonRequest
{
    public required BaselineCompareMode Mode { get; init; }

    /// <summary>The linked server naming the OLD estate. Must be one the deployment allowlisted.</summary>
    public required string LinkedServer { get; init; }

    /// <summary>The database on the linked server (the old estate's database name).</summary>
    public required string BaselineDatabase { get; init; }

    /// <summary>The schema on the NEW side.</summary>
    public required string Schema { get; init; }

    /// <summary>The object on the NEW side. Required for <see cref="BaselineCompareMode.Schema"/> and
    /// <see cref="BaselineCompareMode.Data"/>; null in inventory mode, which walks the whole schema.</summary>
    public string? ObjectName { get; init; }

    /// <summary>The schema on the OLD side when it differs; defaults to <see cref="Schema"/>.</summary>
    public string? BaselineSchema { get; init; }

    /// <summary>The object on the OLD side when the name differs (arc.Bysykkel_Trips versus
    /// arc.Citybike_Trips); defaults to <see cref="ObjectName"/>.</summary>
    public string? BaselineObjectName { get; init; }

    /// <summary>
    /// The LOGICAL key in data mode: the expressions that identify one real-world reading on BOTH estates.
    /// Not the surrogate primary key, which the two estates assign independently and which therefore proves
    /// nothing. Every expression passes <see cref="SqlFragmentGuard"/>.
    /// </summary>
    public IReadOnlyList<string> KeyExpressions { get; init; } = [];

    /// <summary>The columns compared for value parity; empty means every column on the new side except the
    /// identity column, the bare key columns, and anything matching <see cref="ExcludeColumnPattern"/>.</summary>
    public IReadOnlyList<string> CompareColumns { get; init; } = [];

    /// <summary>A LIKE pattern (ESCAPE '\') for columns excluded from value parity by default. The provenance
    /// and audit columns legitimately differ between estates, so they are excluded rather than reported as
    /// mismatches every time.</summary>
    public string ExcludeColumnPattern { get; init; } = DefaultExcludeColumnPattern;

    /// <summary>The default exclusion: the provenance and audit columns the framework stamps. They carry the
    /// load instant, which legitimately differs between the two estates, so comparing them would report a
    /// mismatch on every row and drown the real findings.</summary>
    public const string DefaultExcludeColumnPattern = @"%\_DW";

    /// <summary>A predicate applied to BOTH sides, without the WHERE keyword, so a large table can be compared
    /// one era at a time. Passes <see cref="SqlFragmentGuard"/>; null applies no filter.</summary>
    public string? Where { get; init; }

    /// <summary>The most rows any one section of the report returns.</summary>
    public int Limit { get; init; } = 200;

    /// <summary>How many example keys each anti-join direction carries; 0 suppresses examples.</summary>
    public int SampleRows { get; init; } = 5;

    /// <summary>The most key expressions a request may name.</summary>
    public const int MaxKeyExpressions = 8;

    /// <summary>The most columns a request may name for value parity.</summary>
    public const int MaxCompareColumns = 200;

    public string EffectiveBaselineSchema => string.IsNullOrWhiteSpace(BaselineSchema) ? Schema : BaselineSchema;

    public string? EffectiveBaselineObject
        => string.IsNullOrWhiteSpace(BaselineObjectName) ? ObjectName : BaselineObjectName;

    /// <summary>
    /// Validates the request and returns it with every identifier and fragment normalized, so the comparison
    /// builder can interpolate what it holds without re-checking. Throws <see cref="SqlFlowException"/> naming
    /// the offending field. Called at BOTH ends: the control-plane endpoint (a bad ask is a 400, not a doomed
    /// queued task) and the node before it opens a connection.
    /// </summary>
    /// <param name="allowedLinkedServers">
    /// The linked servers the deployment permits. A comparison names a linked server, which is an identifier
    /// in generated SQL and a route to another estate, so it is allowlisted by configuration rather than
    /// accepted from the request. An empty allowlist means the deployment has configured none, which is a
    /// clear error rather than a silent permit.
    /// </param>
    public BaselineComparisonRequest Validate(IReadOnlyCollection<string> allowedLinkedServers)
    {
        ArgumentNullException.ThrowIfNull(allowedLinkedServers);

        var linkedServer = SqlFragmentGuard.ValidateIdentifier(LinkedServer, nameof(LinkedServer));
        if (allowedLinkedServers.Count == 0)
        {
            throw new SqlFlowException(
                "No baseline linked servers are configured. Set ControlPlane:DataOps:Comparison:LinkedServers " +
                "to the linked servers that reach the old estate before running a comparison.");
        }

        if (!allowedLinkedServers.Contains(linkedServer, StringComparer.OrdinalIgnoreCase))
        {
            throw new SqlFlowException(
                $"The linked server '{linkedServer}' is not allowlisted for baseline comparison. Configured: " +
                $"{string.Join(", ", allowedLinkedServers)}.");
        }

        var baselineDatabase = SqlFragmentGuard.ValidateIdentifier(BaselineDatabase, nameof(BaselineDatabase));
        var schema = SqlFragmentGuard.ValidateIdentifier(Schema, nameof(Schema));
        var baselineSchema = SqlFragmentGuard.ValidateIdentifier(EffectiveBaselineSchema, nameof(BaselineSchema));

        if (Limit is < 1 or > 5000)
        {
            throw new SqlFlowException("limit must be between 1 and 5000.");
        }

        if (SampleRows is < 0 or > 100)
        {
            throw new SqlFlowException("sampleRows must be between 0 and 100.");
        }

        string? objectName = null;
        string? baselineObject = null;
        if (Mode is BaselineCompareMode.Schema or BaselineCompareMode.Data)
        {
            if (string.IsNullOrWhiteSpace(ObjectName))
            {
                throw new SqlFlowException($"{Mode} mode compares one object, so objectName is required.");
            }

            objectName = SqlFragmentGuard.ValidateIdentifier(ObjectName, nameof(ObjectName));
            baselineObject = SqlFragmentGuard.ValidateIdentifier(EffectiveBaselineObject!, nameof(BaselineObjectName));
        }
        else if (!string.IsNullOrWhiteSpace(ObjectName))
        {
            throw new SqlFlowException(
                "Inventory mode compares a whole schema; drop objectName, or switch to schema or data mode.");
        }

        IReadOnlyList<string> keys = [];
        IReadOnlyList<string> compareColumns = [];
        string? where = null;

        if (Mode == BaselineCompareMode.Data)
        {
            if (KeyExpressions.Count == 0)
            {
                throw new SqlFlowException(
                    "Data mode needs the LOGICAL key: the expressions that identify one real-world reading on " +
                    "both estates. The surrogate primary key is not it, because the two estates assign it " +
                    "independently.");
            }

            if (KeyExpressions.Count > MaxKeyExpressions)
            {
                throw new SqlFlowException(
                    $"keyExpressions names {KeyExpressions.Count} expressions; at most {MaxKeyExpressions} are allowed.");
            }

            keys = KeyExpressions
                .Select(k => SqlFragmentGuard.Validate(k, nameof(KeyExpressions)))
                .ToArray();

            if (CompareColumns.Count > MaxCompareColumns)
            {
                throw new SqlFlowException(
                    $"compareColumns names {CompareColumns.Count} columns; at most {MaxCompareColumns} are allowed.");
            }

            compareColumns = CompareColumns
                .Select(c => SqlFragmentGuard.ValidateIdentifier(c, nameof(CompareColumns)))
                .ToArray();

            if (!string.IsNullOrWhiteSpace(Where))
            {
                where = SqlFragmentGuard.Validate(Where, nameof(Where));
            }

            if (string.IsNullOrWhiteSpace(ExcludeColumnPattern) || ExcludeColumnPattern.Length > 128)
            {
                throw new SqlFlowException("excludeColumnPattern must be a LIKE pattern of at most 128 characters.");
            }

            if (ExcludeColumnPattern.Any(char.IsControl) || ExcludeColumnPattern.Contains('\'', StringComparison.Ordinal))
            {
                throw new SqlFlowException("excludeColumnPattern must not contain quotes or control characters.");
            }
        }
        else if (KeyExpressions.Count > 0 || CompareColumns.Count > 0 || !string.IsNullOrWhiteSpace(Where))
        {
            throw new SqlFlowException(
                $"keyExpressions, compareColumns and where apply to data mode only, not {Mode} mode.");
        }

        return this with
        {
            LinkedServer = linkedServer,
            BaselineDatabase = baselineDatabase,
            Schema = schema,
            BaselineSchema = baselineSchema,
            ObjectName = objectName,
            BaselineObjectName = baselineObject,
            KeyExpressions = keys,
            CompareColumns = compareColumns,
            Where = where,
        };
    }
}

/// <summary>One table in the inventory comparison: whether it exists on each side and how many rows each
/// holds. <see cref="Delta"/> is new minus old, so a negative number means the new estate is behind.</summary>
public sealed record BaselineInventoryEntry
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public required bool InBaseline { get; init; }

    public required bool InCurrent { get; init; }

    /// <summary>Rows on the old side; null when the table is absent there.</summary>
    public long? BaselineRows { get; init; }

    /// <summary>Rows on the new side; null when the table is absent there.</summary>
    public long? CurrentRows { get; init; }

    public long? Delta => InBaseline && InCurrent ? (CurrentRows ?? 0) - (BaselineRows ?? 0) : null;

    /// <summary>The delta as a percentage of the old side's rows; null when the old side is absent or empty.</summary>
    public double? DeltaPercent => InBaseline && InCurrent && BaselineRows is > 0 and var baseline
        ? ((CurrentRows ?? 0) - baseline) * 100.0 / baseline
        : null;

    /// <summary>How the entry classifies: "match", "drift", "missingInCurrent", "missingInBaseline".</summary>
    public required string Status { get; init; }
}

/// <summary>The inventory comparison: every table in the schema on either side, worst disagreement first.</summary>
public sealed record BaselineInventoryReport
{
    public required string LinkedServer { get; init; }

    public required string BaselineDatabase { get; init; }

    public required string Schema { get; init; }

    public required int BaselineObjects { get; init; }

    public required int CurrentObjects { get; init; }

    public required int Matching { get; init; }

    public required int Drifting { get; init; }

    public required int MissingInCurrent { get; init; }

    public required int MissingInBaseline { get; init; }

    public required bool Truncated { get; init; }

    /// <summary>True when row counts came from the partition metadata rather than COUNT(*). Metadata counts
    /// are exact for a settled table and effectively free; they can lag a load that is still committing.</summary>
    public required bool CountsFromMetadata { get; init; }

    public required IReadOnlyList<BaselineInventoryEntry> Entries { get; init; }
}

/// <summary>One column as one side declares it, in ordinal position. <see cref="TypeFull"/> carries the length,
/// precision, and scale inline, because a comparison that ignores <c>varchar(50)</c> versus <c>varchar(100)</c>
/// is not a comparison.</summary>
public sealed record BaselineColumn
{
    public required int Position { get; init; }

    public required string Name { get; init; }

    public required string TypeFull { get; init; }

    public required bool Nullable { get; init; }

    public required bool IsIdentity { get; init; }

    /// <summary>The one-line rendering the position-by-position diff compares.</summary>
    public string Describe() => $"{Name} {TypeFull} {(Nullable ? "NULL" : "NOT NULL")}";
}

/// <summary>One position where the two shapes disagree, with the change classified so a reader does not have
/// to diff the two strings themselves.</summary>
public sealed record BaselineSchemaDifference
{
    public required int Position { get; init; }

    /// <summary>"added" (present only on the new side), "removed" (only on the old side), "renamed",
    /// "typeChanged", "nullabilityChanged", or "reordered".</summary>
    public required string Kind { get; init; }

    public string? Baseline { get; init; }

    public string? Current { get; init; }
}

/// <summary>
/// The schema comparison for one object. <see cref="Identical"/> is the question that matters: it is what
/// decides whether a straight INSERT ... SELECT transfer from the old estate is structurally legal, and
/// whether the new table needs a compatibility view under the old name so downstream sees its old shape.
/// </summary>
public sealed record BaselineSchemaReport
{
    public required string LinkedServer { get; init; }

    public required string BaselineObject { get; init; }

    public required string CurrentObject { get; init; }

    public required bool Identical { get; init; }

    /// <summary>True when both sides hold the same column NAMES, whatever their order or type. A table that is
    /// name-identical but not identical needs a compatibility view, not a reload.</summary>
    public required bool SameColumnSet { get; init; }

    public required IReadOnlyList<BaselineColumn> BaselineColumns { get; init; }

    public required IReadOnlyList<BaselineColumn> CurrentColumns { get; init; }

    public required IReadOnlyList<BaselineSchemaDifference> Differences { get; init; }

    /// <summary>What the differences mean for a transfer, in a sentence an operator can act on.</summary>
    public required string Verdict { get; init; }
}

/// <summary>
/// The count decomposition. Physical rows are NOT the comparison: a table with duplicates on one side can hold
/// exactly the same readings as the other and still report a different <c>COUNT(*)</c>. The number that means
/// something is the distinct logical key count.
/// </summary>
public sealed record BaselineCountDecomposition
{
    public required long BaselinePhysicalRows { get; init; }

    public required long BaselineDistinctKeys { get; init; }

    public required long CurrentPhysicalRows { get; init; }

    public required long CurrentDistinctKeys { get; init; }

    public long BaselineDuplicates => BaselinePhysicalRows - BaselineDistinctKeys;

    public long CurrentDuplicates => CurrentPhysicalRows - CurrentDistinctKeys;

    public long PhysicalDelta => CurrentPhysicalRows - BaselinePhysicalRows;

    /// <summary>New distinct keys minus old distinct keys: the difference that actually means rows are absent.</summary>
    public long LogicalDelta => CurrentDistinctKeys - BaselineDistinctKeys;
}

/// <summary>The bidirectional anti-join. BOTH directions are always reported: a table that is "sometimes more,
/// sometimes less" is the normal case after a migration, and a one-directional check reads it as clean.</summary>
public sealed record BaselineAntiJoin
{
    public required long BaselineKeysMissingFromCurrent { get; init; }

    public required long CurrentKeysMissingFromBaseline { get; init; }

    /// <summary>Example keys, at most the requested sample size per direction. Each entry is the key
    /// expressions' values in the order the request declared them.</summary>
    public required IReadOnlyList<BaselineKeySample> Samples { get; init; }
}

/// <summary>One example key from an anti-join direction.</summary>
public sealed record BaselineKeySample(string Direction, IReadOnlyList<string?> Key);

/// <summary>One column's contribution to a value mismatch, with the NULL asymmetry split out. A column whose
/// mismatches are ALL "current is NULL where baseline was not" is the empty-string-versus-NULL landing
/// difference, not lost data, and it is the single most common false alarm in a migration.</summary>
public sealed record BaselineColumnParity
{
    public required string Column { get; init; }

    public required long Mismatches { get; init; }

    public required long BaselineNullCurrentNot { get; init; }

    public required long CurrentNullBaselineNot { get; init; }

    /// <summary>True when every mismatch is the new side holding NULL where the old side held a value.</summary>
    public bool IsPureNullDrift => Mismatches > 0 && CurrentNullBaselineNot == Mismatches;
}

/// <summary>Value parity across the keys both estates hold. Matching keys is not matching data: a transform
/// that diverged produces the same keys with different values, and no amount of transferring rows fixes it.</summary>
public sealed record BaselineValueParity
{
    public required long SharedKeysCompared { get; init; }

    public required long KeysWithMismatch { get; init; }

    public required IReadOnlyList<BaselineColumnParity> Columns { get; init; }

    /// <summary>How many columns the per-column breakdown actually examined. The mismatch TOTAL above covers
    /// every compared column; the breakdown costs one pass each, so a very wide table is capped.</summary>
    public required int ColumnsAnalyzed { get; init; }

    /// <summary>How many columns the mismatch total was computed over. Greater than
    /// <see cref="ColumnsAnalyzed"/> means the breakdown is partial and must be reported as such: a column
    /// beyond the cap could hold mismatches that no row of <see cref="Columns"/> mentions.</summary>
    public required int ColumnsCompared { get; init; }

    public bool BreakdownTruncated => ColumnsAnalyzed < ColumnsCompared;
}

/// <summary>The full data comparison for one object, and the verdict the three sections add up to.</summary>
public sealed record BaselineDataReport
{
    public required string LinkedServer { get; init; }

    public required string BaselineObject { get; init; }

    public required string CurrentObject { get; init; }

    public required IReadOnlyList<string> KeyExpressions { get; init; }

    public required IReadOnlyList<string> ComparedColumns { get; init; }

    public string? Filter { get; init; }

    public required BaselineCountDecomposition Counts { get; init; }

    public required BaselineAntiJoin AntiJoin { get; init; }

    /// <summary>Null when the request compared no columns (every candidate column was excluded).</summary>
    public BaselineValueParity? ValueParity { get; init; }

    /// <summary>"identical", "rowsMissing", "valuesDiverged", or "both": what an operator must act on.</summary>
    public required string Verdict { get; init; }

    /// <summary>What the numbers mean and what to do next, one sentence per finding.</summary>
    public required IReadOnlyList<string> Findings { get; init; }
}
