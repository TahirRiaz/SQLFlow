using SqlFlow.Core.Comparison;
using SqlFlow.Core.Connections;

namespace SqlFlow.Core.Quality;

/// <summary>
/// A decision the duplicate check could not make for the operator, and stopped rather than guessing. A
/// duplicate check run against the wrong key answers confidently and wrongly, which is worse than not
/// answering, so where the table declares no usable key the check ASKS. A client presents
/// <see cref="Prompt"/>, collects columns, and runs again.
/// </summary>
public sealed record DuplicateKeyQuestion
{
    /// <summary>The question, phrased for the person who has to answer it.</summary>
    public required string Prompt { get; init; }

    /// <summary>The request field the answer goes into.</summary>
    public string Parameter { get; init; } = "columns";

    /// <summary>The candidate columns to choose from. Never a default the check would silently apply.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];
}

/// <summary>One key value that occurs more than once, with the number of rows carrying it.</summary>
public sealed record DuplicateKeyGroup(IReadOnlyList<string?> Key, long Rows);

/// <summary>
/// The answer to "does this table hold more than one row per key": which key was used and where it came from,
/// the counts, and the worst offending values. Read-only throughout; the SQL it carries lists the offending
/// rows for a human to look at and is never executed.
/// </summary>
public sealed record DuplicateKeyReport
{
    public required string Target { get; init; }

    public string? Database { get; init; }

    /// <summary>The columns grouped by.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Where the key came from, in words ("SQLFlow's key index NCI_KeyColumn (the key the load merges
    /// on)", "the columns the request named").</summary>
    public required string KeySource { get; init; }

    /// <summary>The index the key came from; null when the caller named the columns.</summary>
    public string? KeyIndex { get; init; }

    /// <summary>The key index's filter predicate when it is a filtered unique index. SQLFlow's SCD2 key index
    /// is filtered to the current rows, so the check applies the same predicate.</summary>
    public string? KeyFilter { get; init; }

    /// <summary>True when the key index makes duplicates IMPOSSIBLE (unique, enabled, unfiltered), so a zero
    /// is guaranteed rather than measured. A client must not present that as a discovered fact.</summary>
    public bool KeyEnforcesUniqueness { get; init; }

    public bool KeyIndexDisabled { get; init; }

    public long TotalRows { get; init; }

    public long DistinctKeys { get; init; }

    public long DuplicateGroups { get; init; }

    /// <summary>Rows that would have to be removed to leave one per key.</summary>
    public long ExcessRows { get; init; }

    public IReadOnlyList<DuplicateKeyGroup> WorstGroups { get; init; } = [];

    /// <summary>True when the worst-group list hit its limit, so it is a page and not the whole set.</summary>
    public bool Truncated { get; init; }

    /// <summary>The SELECT that lists the offending rows, for a human to review and run. Never executed here.</summary>
    public string? ListDuplicatesSql { get; init; }

    /// <summary>What the numbers do and do not mean.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Set when the check asked instead of measuring. The task SUCCEEDS carrying this, so a client
    /// must check it before reading the counts as a verdict.</summary>
    public DuplicateKeyQuestion? Question { get; init; }

    public bool AwaitingInput => Question is not null;
}

/// <summary>
/// A validated ask for one duplicate-key check. The columns are optional by design: left empty the check reads
/// the key the TABLE declares, and only where that fails does it come back asking for them.
/// </summary>
public sealed record DuplicateKeyRequest
{
    public required string Schema { get; init; }

    public required string ObjectName { get; init; }

    public string? Database { get; init; }

    /// <summary>The columns that identify one real row. Empty lets the check read the declared key.</summary>
    public IReadOnlyList<string> Columns { get; init; } = [];

    /// <summary>How many of the worst duplicate groups to return.</summary>
    public int Limit { get; init; } = 20;

    /// <summary>The most columns a key may name.</summary>
    public const int MaxColumns = 16;

    /// <summary>The providers the check runs on. It is authored in T-SQL.</summary>
    public static readonly IReadOnlyList<DataSourceKind> SupportedKinds =
        [DataSourceKind.MSSQL, DataSourceKind.AZDB];

    /// <summary>
    /// Validates the request and returns it normalized. Every identifier that reaches generated SQL passes the
    /// same strict guard the baseline comparison uses, so the property is checked in one place rather than
    /// re-argued at each call site. Throws <see cref="SqlFlowException"/> naming the offending field.
    /// </summary>
    public DuplicateKeyRequest Validate(DataSourceKind? kind)
    {
        if (kind is { } declared && !SupportedKinds.Contains(declared))
        {
            throw new SqlFlowException(
                $"The duplicate-key check is authored in T-SQL; the source must be SQL Server or Azure SQL, " +
                $"not {declared}.");
        }

        if (Limit is < 1 or > 1000)
        {
            throw new SqlFlowException("limit must be between 1 and 1000.");
        }

        if (Columns.Count > MaxColumns)
        {
            throw new SqlFlowException(
                $"columns names {Columns.Count} columns; at most {MaxColumns} are allowed.");
        }

        return new DuplicateKeyRequest
        {
            Schema = SqlFragmentGuard.ValidateIdentifier(Schema, nameof(Schema)),
            ObjectName = SqlFragmentGuard.ValidateIdentifier(ObjectName, nameof(ObjectName)),
            Database = string.IsNullOrWhiteSpace(Database)
                ? null
                : SqlFragmentGuard.ValidateIdentifier(Database, nameof(Database)),
            Columns = Columns.Select(c => SqlFragmentGuard.ValidateIdentifier(c, nameof(Columns))).ToArray(),
            Limit = Limit,
        };
    }

    /// <summary>The two-part name as an operator reads it.</summary>
    public string Target => $"{Schema}.{ObjectName}";
}
