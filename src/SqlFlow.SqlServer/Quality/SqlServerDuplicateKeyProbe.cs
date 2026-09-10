using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Connections;
using SqlFlow.Core.Quality;

namespace SqlFlow.SqlServer.Quality;

/// <summary>One key the table declares, as the discovery pass reads it out of the catalog views.</summary>
internal sealed record DeclaredKey
{
    public required string IndexName { get; init; }

    public required IReadOnlyList<string> Columns { get; init; }

    public required bool IsPrimaryKey { get; init; }

    public required bool IsUniqueConstraint { get; init; }

    /// <summary>Whether the index actually enforces uniqueness. SQLFlow creates NCI_KeyColumn UNIQUE, but a
    /// legacy table can carry a non-unique variant, and any index can be disabled.</summary>
    public required bool IsUnique { get; init; }

    /// <summary>A disabled index enforces nothing, so duplicates can exist behind one that looks unique.</summary>
    public required bool IsDisabled { get; init; }

    /// <summary>True when every key column is an IDENTITY. Such a key is unique by construction, so grouping
    /// by it reports zero duplicates on every table and proves nothing about the business data.</summary>
    public required bool IsSurrogate { get; init; }

    /// <summary>The index's filter predicate, verbatim from <c>sys.indexes</c>, for a filtered unique index.
    /// SQLFlow's SCD2 key index is filtered to the current rows, and uniqueness holds only inside that filter,
    /// so the duplicate check must apply the same predicate or it reports every historical version as a
    /// duplicate.</summary>
    public string? FilterDefinition { get; init; }

    /// <summary>
    /// SQLFlow's own canonical business-key index: the key the load MERGES on, and therefore the key a
    /// duplicate actually means something against. Matched by PREFIX, because legacy SQLFlow suffixed the name
    /// with a hash of the table (<c>NCI_KeyColumn_a1b2c3</c>) and a table ported from old production still
    /// carries that form; V3 dropped the suffix for a stable name.
    /// </summary>
    public bool IsSqlFlowKeyIndex
        => IndexName.StartsWith(CanonicalKeyIndexName, StringComparison.OrdinalIgnoreCase);

    /// <summary>The name <c>CanonicalIndexPlanner</c> gives the business-key index on every target it creates.
    /// Under SCD2 the same name is a FILTERED unique index (one current row per key) rather than a plain
    /// one.</summary>
    public const string CanonicalKeyIndexName = "NCI_KeyColumn";

    /// <summary>
    /// True when the index makes duplicates on this key IMPOSSIBLE rather than merely unlikely: unique,
    /// enabled, and unfiltered. It decides how a zero result must be read. A filtered unique index (SCD2)
    /// guarantees nothing outside its predicate, and a disabled one guarantees nothing at all.
    /// </summary>
    public bool EnforcesUniqueness => IsUnique && !IsDisabled && FilterDefinition is null;
}

/// <summary>
/// Answers "does this table hold more than one row per key", using the key the TABLE declares rather than one
/// the caller guessed. Read-only: it groups and counts, and the SQL it suggests is the SELECT that lists the
/// offending rows for a human to look at.
///
/// The key discovery order is the whole point of the action. SQLFlow appends a surrogate identity primary key
/// to every target it creates, so a naive "use the primary key" check would group by a column that is unique
/// by construction and report zero duplicates on every table in the estate. The order below therefore prefers
/// SQLFlow's own <c>NCI_KeyColumn</c> business-key index (the key the load merges on), then a natural primary
/// key, then any other unique index, and refuses to fall back to a surrogate. Where nothing usable is
/// declared, the action ASKS which columns to use instead of picking something plausible.
/// </summary>
public static class SqlServerDuplicateKeyProbe
{
    /// <summary>
    /// Runs one duplicate-key check. The request is already validated; the resolved provider kind is
    /// re-checked here because an @alias only resolves on the node.
    /// </summary>
    public static async Task<DuplicateKeyReport> RunAsync(
        string connectionString, DuplicateKeyRequest request, DataSourceKind kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);
        if (!DuplicateKeyRequest.SupportedKinds.Contains(kind))
        {
            throw new SqlFlowException(
                $"The duplicate-key check is authored in T-SQL; the source resolved to kind '{kind}'. Only " +
                "SQL Server and Azure SQL sources are supported.");
        }

        var qualified = $"{Quote(request.Schema)}.{Quote(request.ObjectName)}";
        var target = request.Target;

        await using var connection = await OpenAsync(connectionString, request.Database, ct).ConfigureAwait(false);

        var declared = await ReadDeclaredKeysAsync(connection, qualified, ct).ConfigureAwait(false);
        var columns = await ReadColumnNamesAsync(connection, qualified, ct).ConfigureAwait(false);
        if (columns.Count == 0)
        {
            throw new SqlFlowException(
                $"The object {target} was not found in the scoped database, or has no columns to group by.");
        }

        var (key, source, chosen) = Choose(declared, request.Columns, columns, target);
        if (key is null)
        {
            return AskForKey(request, declared, columns);
        }

        var measurement = await MeasureAsync(
                connection, qualified, key, chosen?.FilterDefinition, request.Limit, ct)
            .ConfigureAwait(false);

        return BuildReport(request, qualified, key, source, chosen, measurement);
    }

    /// <summary>
    /// Picks the key to group by. An explicit column list from the caller always wins (it is the answer to the
    /// question this action asks). Otherwise the declared keys are preferred in the order that makes a
    /// duplicate mean something, and a surrogate-only key is never chosen.
    /// </summary>
    private static (IReadOnlyList<string>? Key, string Source, DeclaredKey? Declared) Choose(
        IReadOnlyList<DeclaredKey> declared, IReadOnlyList<string> requested, IReadOnlyList<string> columns,
        string target)
    {
        if (requested.Count > 0)
        {
            var unknown = requested
                .Where(r => !columns.Contains(r, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (unknown.Length > 0)
            {
                throw new SqlFlowException(
                    $"{target} has no column(s) named {string.Join(", ", unknown)}. Its columns are: " +
                    $"{string.Join(", ", columns)}.");
            }

            return (requested, "the columns the request named", null);
        }

        // SQLFlow's own business-key index first, whether or not it currently ENFORCES uniqueness. It names
        // the key the load merges on, which is what a duplicate has to mean something against; a non-unique or
        // disabled variant is precisely the case where duplicates can have accumulated, so skipping it would
        // skip the tables most worth checking.
        if (declared.FirstOrDefault(k => k.IsSqlFlowKeyIndex && !k.IsSurrogate) is { } sqlflowKey)
        {
            return (sqlflowKey.Columns,
                $"SQLFlow's key index {sqlflowKey.IndexName} (the key the load merges on)",
                sqlflowKey);
        }

        if (declared.FirstOrDefault(k => k.IsPrimaryKey && !k.IsSurrogate) is { } naturalPrimaryKey)
        {
            return (naturalPrimaryKey.Columns, $"the primary key {naturalPrimaryKey.IndexName}", naturalPrimaryKey);
        }

        // Any remaining key that CLAIMS uniqueness, narrowest first: a two-column unique key is a stronger
        // statement about the data than a six-column one, and cheaper to group by. A merely non-unique index
        // is not a claim about identity and is never used.
        var other = declared
            .Where(k => k.IsUnique && !k.IsSurrogate)
            .OrderBy(k => k.Columns.Count)
            .ThenBy(k => k.IndexName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return other is null
            ? (null, string.Empty, null)
            : (other.Columns,
                other.IsUniqueConstraint
                    ? $"the unique constraint {other.IndexName}"
                    : $"the unique index {other.IndexName}",
                other);
    }

    /// <summary>
    /// The "I will not guess" answer. The task succeeds: establishing that no usable key is declared IS the
    /// finding, and the report carries the question plus the candidate columns so a client can ask a person
    /// and re-run with an answer.
    /// </summary>
    /// <summary>
    /// The "I will not guess" answer. The task succeeds: establishing that no usable key is declared IS the
    /// finding, and the report carries the question plus the candidate columns so a client can ask a person
    /// and run again with an answer.
    /// </summary>
    private static DuplicateKeyReport AskForKey(
        DuplicateKeyRequest request, IReadOnlyList<DeclaredKey> declared, IReadOnlyList<string> columns)
    {
        var keyClaims = declared.Where(k => k.IsUnique || k.IsSqlFlowKeyIndex).ToArray();
        var surrogateOnly = keyClaims.Length > 0 && keyClaims.All(k => k.IsSurrogate);
        var target = request.Target;
        var detail = surrogateOnly
            ? $"{target} declares only surrogate key(s) " +
              $"({string.Join(", ", keyClaims.Select(k => k.IndexName))}), whose columns are IDENTITY and " +
              "therefore unique by construction. Grouping by them would report zero duplicates on any table, " +
              "which says nothing about the business data."
            : $"{target} declares no unique index or constraint at all, so there is nothing that states what " +
              "one row of it is supposed to represent.";

        return new DuplicateKeyReport
        {
            Target = target,
            Database = request.Database,
            KeyColumns = [],
            KeySource = "none declared",
            Notes =
            [
                "A duplicate check is only as meaningful as its key: run against the wrong columns it answers " +
                "confidently and wrongly, which is worse than not answering. That is why this asks.",
            ],
            Question = new DuplicateKeyQuestion
            {
                Prompt =
                    $"Which columns identify one real row of {target}? {detail} Choose the business key: the " +
                    "columns that together should never repeat.",
                Options = columns,
            },
        };
    }

    /// <summary>The measured answer: how many rows, how many distinct keys, and the worst offending groups.</summary>
    private sealed record Measurement
    {
        public required long TotalRows { get; init; }

        public required long DistinctKeys { get; init; }

        public required long DuplicateGroups { get; init; }

        /// <summary>Rows that would have to be removed to leave one per key.</summary>
        public required long ExcessRows { get; init; }

        public required IReadOnlyList<DuplicateKeyGroup> Worst { get; init; }
    }

    private static async Task<Measurement> MeasureAsync(
        SqlConnection connection, string qualified, IReadOnlyList<string> key, string? filter, int limit,
        CancellationToken ct)
    {
        var keyList = string.Join(", ", key.Select(Quote));

        // The filter comes from sys.indexes, not from a caller, so it is server-authored text rather than
        // input. It matters: SQLFlow's SCD2 key index is unique only among CURRENT rows, and without the same
        // predicate every historical version reads as a duplicate.
        var where = string.IsNullOrWhiteSpace(filter) ? string.Empty : $" WHERE {filter}";

        var sql = $"""
            WITH grouped AS (
                SELECT {keyList}, COUNT_BIG(*) AS row_count
                FROM {qualified}{where}
                GROUP BY {keyList}
            )
            SELECT
                (SELECT COUNT_BIG(*) FROM {qualified}{where}) AS total_rows,
                (SELECT COUNT_BIG(*) FROM grouped) AS distinct_keys,
                (SELECT COUNT_BIG(*) FROM grouped WHERE row_count > 1) AS duplicate_groups,
                (SELECT ISNULL(SUM(row_count - 1), 0) FROM grouped WHERE row_count > 1) AS excess_rows;
            """;

        long totalRows;
        long distinctKeys;
        long duplicateGroups;
        long excessRows;
        await using (var command = Command(connection, sql))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new SqlFlowException("The duplicate-key measurement returned no row.");
            }

            totalRows = reader.GetInt64(0);
            distinctKeys = reader.GetInt64(1);
            duplicateGroups = reader.GetInt64(2);
            excessRows = reader.GetInt64(3);
        }

        var worst = duplicateGroups > 0
            ? await ReadWorstAsync(connection, qualified, key, where, limit, ct).ConfigureAwait(false)
            : [];

        return new Measurement
        {
            TotalRows = totalRows,
            DistinctKeys = distinctKeys,
            DuplicateGroups = duplicateGroups,
            ExcessRows = excessRows,
            Worst = worst,
        };
    }

    private static async Task<IReadOnlyList<DuplicateKeyGroup>> ReadWorstAsync(
        SqlConnection connection, string qualified, IReadOnlyList<string> key, string where, int limit,
        CancellationToken ct)
    {
        var keyList = string.Join(", ", key.Select(Quote));
        var sql = $"""
            SELECT TOP (@limit) {keyList}, COUNT_BIG(*) AS row_count
            FROM {qualified}{where}
            GROUP BY {keyList}
            HAVING COUNT_BIG(*) > 1
            ORDER BY COUNT_BIG(*) DESC;
            """;

        await using var command = Command(connection, sql);
        command.Parameters.Add(new SqlParameter("@limit", limit));

        var groups = new List<DuplicateKeyGroup>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new string?[key.Count];
            for (var i = 0; i < key.Count; i++)
            {
                values[i] = reader.IsDBNull(i)
                    ? null
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
            }

            groups.Add(new DuplicateKeyGroup(values, reader.GetInt64(key.Count)));
        }

        return groups;
    }

    private static DuplicateKeyReport BuildReport(
        DuplicateKeyRequest request, string qualified,
        IReadOnlyList<string> key, string source, DeclaredKey? chosen, Measurement measurement)
    {
        var filter = chosen?.FilterDefinition;
        var target = request.Target;
        var keyList = string.Join(", ", key.Select(Quote));
        var where = string.IsNullOrWhiteSpace(filter) ? string.Empty : $" WHERE {filter}";

        string? listSql = null;
        if (measurement.DuplicateGroups > 0)
        {
            listSql = $"""
                -- The rows behind the duplicate groups on {target}, worst first.
                SELECT t.*
                FROM {qualified} AS t
                JOIN (
                    SELECT {keyList}
                    FROM {qualified}{where}
                    GROUP BY {keyList}
                    HAVING COUNT_BIG(*) > 1
                ) AS d ON {string.Join(" AND ", key.Select(c => $"d.{Quote(c)} = t.{Quote(c)}"))}
                ORDER BY {string.Join(", ", key.Select(c => $"t.{Quote(c)}"))};
                """;
        }

        var notes = new List<string> { $"Grouped by {string.Join(" + ", key)}, taken from {source}." };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            notes.Add(
                $"The key index is filtered ({filter}), so uniqueness holds only inside that predicate and the " +
                "check applies it too. Under SCD2 that is one CURRENT row per key: historical versions live " +
                "outside the filter and are deliberately not counted as duplicates.");
        }

        if (chosen is { IsUnique: true, IsDisabled: false } && string.IsNullOrWhiteSpace(filter))
        {
            // Saying "no duplicates found" here would dress up a tautology as a measurement. The index makes
            // duplicates impossible; the engine would have rejected them at insert.
            notes.Add(
                $"{chosen.IndexName} is an enabled, unfiltered UNIQUE index, so duplicates on this key are " +
                "impossible by construction and a zero result is guaranteed rather than measured. To ask a " +
                "real question about this table, name a different candidate key in 'columns'.");
        }
        else if (chosen is { IsDisabled: true })
        {
            notes.Add(
                $"{chosen.IndexName} is DISABLED, so it enforces nothing and duplicates can have accumulated " +
                "behind it. That is worth fixing regardless of what this count says.");
        }
        else if (chosen is { IsUnique: false })
        {
            notes.Add(
                $"{chosen.IndexName} names the key but is NOT unique, so nothing has been preventing " +
                "duplicates on it. A legacy table ported from old production is the usual reason.");
        }

        if (measurement.DuplicateGroups > 0)
        {
            notes.Add(
                "A duplicate on the merge key usually means the key does not identify what the load thinks it " +
                "does: a provenance column in the key multiplies rows by the number of files loaded, and a " +
                "source that repeats a business key across exports needs the export identity in the key.");
        }

        return new DuplicateKeyReport
        {
            Target = target,
            Database = request.Database,
            KeyColumns = key,
            KeySource = source,
            KeyIndex = chosen?.IndexName,
            KeyFilter = filter,
            KeyEnforcesUniqueness = chosen?.EnforcesUniqueness ?? false,
            KeyIndexDisabled = chosen?.IsDisabled ?? false,
            TotalRows = measurement.TotalRows,
            DistinctKeys = measurement.DistinctKeys,
            DuplicateGroups = measurement.DuplicateGroups,
            ExcessRows = measurement.ExcessRows,
            WorstGroups = measurement.Worst,
            Truncated = measurement.Worst.Count >= request.Limit,
            ListDuplicatesSql = listSql,
            Notes = notes,
        };
    }

    /// <summary>
    /// Every unique index and constraint on the object, with its key columns in key order and whether all of
    /// them are IDENTITY. Included columns are excluded: they are not part of what the index enforces.
    /// </summary>
    private static async Task<IReadOnlyList<DeclaredKey>> ReadDeclaredKeysAsync(
        SqlConnection connection, string qualified, CancellationToken ct)
    {
        // Every named index, not just the unique ones: SQLFlow's key index is the authority on what the key IS
        // whether or not it currently enforces it, and a non-unique or disabled variant is exactly the case
        // where duplicates can have accumulated. Non-unique indexes that are NOT the key index are ignored by
        // the selection, but they are read here so the selection can make that judgement.
        const string sql = """
            SELECT i.index_id, i.name, i.is_primary_key, i.is_unique_constraint, i.filter_definition,
                   c.name AS column_name, c.is_identity, ic.key_ordinal, i.is_unique, i.is_disabled
            FROM sys.indexes AS i
            JOIN sys.index_columns AS ic
                ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
            JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@qualified) AND i.name IS NOT NULL
            ORDER BY i.index_id, ic.key_ordinal;
            """;

        await using var command = Command(connection, sql);
        command.Parameters.Add(new SqlParameter("@qualified", qualified));

        var byIndex = new Dictionary<int, IndexRow>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var indexId = reader.GetInt32(0);
            var column = reader.GetString(5);
            var isIdentity = reader.GetBoolean(6);

            if (byIndex.TryGetValue(indexId, out var existing))
            {
                existing.Columns.Add(column);
                byIndex[indexId] = existing with { AllIdentity = existing.AllIdentity && isIdentity };
            }
            else
            {
                byIndex[indexId] = new IndexRow(
                    reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), [column], isIdentity,
                    reader.GetBoolean(8), reader.GetBoolean(9));
            }
        }

        return byIndex.Values
            .Select(v => new DeclaredKey
            {
                IndexName = v.Name,
                Columns = v.Columns,
                IsPrimaryKey = v.IsPrimaryKey,
                IsUniqueConstraint = v.IsUniqueConstraint,
                IsUnique = v.IsUnique,
                IsDisabled = v.IsDisabled,
                IsSurrogate = v.AllIdentity,
                FilterDefinition = v.Filter,
            })
            .ToArray();
    }

    /// <summary>One index as the discovery pass accumulates it, before its columns are complete.</summary>
    private sealed record IndexRow(
        string Name, bool IsPrimaryKey, bool IsUniqueConstraint, string? Filter, List<string> Columns,
        bool AllIdentity, bool IsUnique, bool IsDisabled);

    private static async Task<IReadOnlyList<string>> ReadColumnNamesAsync(
        SqlConnection connection, string qualified, CancellationToken ct)
    {
        const string sql = """
            SELECT c.name
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@qualified)
            ORDER BY c.column_id;
            """;

        await using var command = Command(connection, sql);
        command.Parameters.Add(new SqlParameter("@qualified", qualified));

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static async Task<SqlConnection> OpenAsync(
        string connectionString, string? database, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(database))
            {
                await connection.ChangeDatabaseAsync(database, ct).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static SqlCommand Command(SqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0; // bounded by the executor's cancellation budget, matching the other probes
        return command;
    }

    private static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
