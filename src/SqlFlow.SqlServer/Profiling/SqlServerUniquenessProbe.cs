using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Profiling;

namespace SqlFlow.SqlServer.Profiling;

/// <summary>
/// The SQL Server measurement behind <see cref="UniqueKeyDetector"/>, shaped to stay cheap on any table at any size.
/// Before a single row is read, one catalog batch resolves the object: keys the engine already enforces (enabled,
/// unfiltered unique indexes and constraints) come back as <see cref="DeclaredUniqueKeys"/> so the detector can
/// answer from metadata alone, columns whose types can never form a practical key (LOB, floating point, CLR,
/// rowversion, sql_variant, non-persisted computed) are excluded up front, and the partition metadata supplies a
/// near-exact row count so a huge table never pays an exact <c>COUNT</c> just to learn it must be sampled.
///
/// <see cref="MeasureManyAsync"/> measures many column sets per scan: each set contributes a
/// <c>COUNT_BIG(DISTINCT ...)</c> (a single column directly, a composite via a <c>HASHBYTES</c> of its non-null
/// values) and a non-null count. Batches are capped at <see cref="MaxSetsPerMeasurement"/> sets per query, because
/// SQL Server executes every additional distinct aggregate as its own pass over a shared spool; a bounded batch keeps
/// the plan compilable and its memory sane on a wide table, while a greedy level still costs a handful of queries
/// rather than one per trial. When a sample is requested - or auto-selected for a large table - it is materialized
/// once into a session temp table and every set is measured against that same fixed set, so the search is coherent.
/// The sample is random, not a physical prefix: page sampling (<c>TABLESAMPLE</c>) for tables, a Bernoulli row filter
/// for views, so a table clustered by date does not eject globally-identifying columns as prefix constants.
/// <see cref="VerifyAsync"/> confirms a finalist against the whole table with <c>EXISTS</c> probes for a null or a
/// duplicate group. The probe owns one open connection for its lifetime (the temp table lives on it), released on
/// <see cref="DisposeAsync"/>; every command runs without a timeout, matching the engine's other long-running work.
/// </summary>
public sealed class SqlServerUniquenessProbe : IUniquenessProbe
{
    /// <summary>Above this row count, a run with no explicit sample size auto-samples the search.</summary>
    public const int AutoSampleThreshold = 2_000_000;

    /// <summary>The sample size used when a large table is auto-sampled.</summary>
    public const int AutoSampleSize = 500_000;

    /// <summary>The most column sets measured in one query. Each set adds a distinct aggregate, and SQL Server runs
    /// every additional distinct aggregate as its own pass over a shared spool, so an unbounded batch on a wide table
    /// explodes the plan; sixteen sets (33 aggregates) keeps each query cheap while a level stays a few round trips.</summary>
    public const int MaxSetsPerMeasurement = 16;

    private readonly SqlConnection _connection;
    private readonly string _table;
    private readonly string _workingSet;

    private SqlServerUniquenessProbe(
        SqlConnection connection, string table, string workingSet, long totalRows, long workingRows, bool sampled, ObjectMetadata meta)
    {
        _connection = connection;
        _table = table;
        _workingSet = workingSet;
        TotalRows = totalRows;
        WorkingRows = workingRows;
        Sampled = sampled;
        DeclaredUniqueKeys = meta.DeclaredKeys;
        EligibleColumns = meta.EligibleColumns;
        ExcludedColumns = meta.ExcludedColumns;
    }

    public long TotalRows { get; }

    public long WorkingRows { get; }

    public bool Sampled { get; }

    /// <summary>Column sets the engine already enforces as unique: enabled, unfiltered, non-hypothetical unique
    /// indexes and constraints (a filtered or disabled index proves nothing about the whole table). Empty when the
    /// caller opted out of metadata or the object declares none.</summary>
    public IReadOnlyList<IReadOnlyList<string>> DeclaredUniqueKeys { get; }

    /// <summary>The requested columns that can take part in the search, in the requested order.</summary>
    public IReadOnlyList<string> EligibleColumns { get; }

    /// <summary>The requested columns removed before any row was read, each with the reason.</summary>
    public IReadOnlyList<ExcludedColumn> ExcludedColumns { get; }

    /// <summary>
    /// Opens the probe. One catalog batch first resolves declared unique keys, column eligibility, and a
    /// partition-metadata row count. When <paramref name="trustDeclaredKeys"/> and the object declares a key, no data
    /// is read at all. Otherwise <paramref name="sampleSize"/> null auto-samples a table over
    /// <see cref="AutoSampleThreshold"/> rows (0 forces a full scan; a positive value sets an explicit sample). A
    /// sample is a random draw of the eligible columns, materialized into a session temp table on the probe's
    /// connection, which is held open for the probe's lifetime because that temp table lives on it.
    /// </summary>
    public static async Task<SqlServerUniquenessProbe> CreateAsync(
        string connectionString, string qualifiedTable, IReadOnlyList<string> columns, int? sampleSize,
        bool trustDeclaredKeys = true, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(qualifiedTable);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required to profile.", nameof(columns));
        }

        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var meta = await LoadMetadataAsync(connection, qualifiedTable, columns, ct).ConfigureAwait(false);

            if (trustDeclaredKeys && meta.DeclaredKeys.Count > 0)
            {
                // A declared key answers without profiling; the row count is informational here, so the instant
                // partition-metadata count stands in for an exact COUNT of a possibly huge table.
                var total = meta.ApproxRows
                    ?? await ScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {qualifiedTable};", ct).ConfigureAwait(false);
                return new SqlServerUniquenessProbe(connection, qualifiedTable, qualifiedTable, total, total, sampled: false, meta);
            }

            if (!trustDeclaredKeys)
            {
                meta = meta with { DeclaredKeys = [] };
            }

            // Sampling decision. The partition-metadata count (instant, near-exact) decides whether to sample, so a
            // huge table never pays an exact COUNT just to learn it must be sampled; the exact count is only taken
            // when the search will run against the full table, where the algorithm compares distinct counts to it.
            if (sampleSize is not 0 && meta.EligibleColumns.Count > 0 && meta.ApproxRows is { } approx)
            {
                var target = sampleSize ?? (approx > AutoSampleThreshold ? AutoSampleSize : 0);
                if (target > 0 && approx > target)
                {
                    var sampled = await TryMaterializeSampleAsync(connection, qualifiedTable, meta, approx, target, ct).ConfigureAwait(false);
                    if (sampled is not null)
                    {
                        return sampled;
                    }
                }
            }

            var exact = await ScalarLongAsync(connection, $"SELECT COUNT_BIG(*) FROM {qualifiedTable};", ct).ConfigureAwait(false);
            var resolved = sampleSize ?? (exact > AutoSampleThreshold ? AutoSampleSize : 0);
            if (resolved > 0 && exact > resolved && meta.EligibleColumns.Count > 0)
            {
                var sampled = await TryMaterializeSampleAsync(connection, qualifiedTable, meta, exact, resolved, ct).ConfigureAwait(false);
                if (sampled is not null)
                {
                    return sampled;
                }
            }

            return new SqlServerUniquenessProbe(connection, qualifiedTable, qualifiedTable, exact, exact, sampled: false, meta);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<SetMeasure>> MeasureManyAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sets);
        if (sets.Count == 0)
        {
            return [];
        }

        if (sets.Any(s => s.Count == 0))
        {
            throw new ArgumentException("A measured column set must have at least one column.", nameof(sets));
        }

        var results = new List<SetMeasure>(sets.Count);
        for (var offset = 0; offset < sets.Count; offset += MaxSetsPerMeasurement)
        {
            var batch = sets.Skip(offset).Take(MaxSetsPerMeasurement).ToList();
            results.AddRange(await MeasureBatchAsync(batch, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<IReadOnlyList<SetMeasure>> MeasureBatchAsync(IReadOnlyList<IReadOnlyList<string>> sets, CancellationToken ct)
    {
        // One scan, two aggregates per set: distinct non-null tuples, and the non-null row count.
        var select = new StringBuilder("SELECT COUNT_BIG(*) AS Scanned");
        for (var i = 0; i < sets.Count; i++)
        {
            select.Append(",\n    ").Append(DistinctExpression(sets[i])).Append(" AS D").Append(i);
            select.Append(",\n    ").Append(NonNullExpression(sets[i])).Append(" AS V").Append(i);
        }

        select.Append("\nFROM ").Append(_workingSet).Append(';');

        await using var command = new SqlCommand(select.ToString(), _connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The uniqueness measurement returned no row.");
        }

        var scanned = reader.GetInt64(0);
        var results = new List<SetMeasure>(sets.Count);
        for (var i = 0; i < sets.Count; i++)
        {
            var distinct = reader.GetInt64(1 + (i * 2));
            var nonNull = reader.GetInt64(2 + (i * 2));
            results.Add(new SetMeasure
            {
                Columns = sets[i].ToList(),
                Scanned = scanned,
                Distinct = distinct,
                Nulls = scanned - nonNull,
                Exact = !Sampled,
            });
        }

        return results;
    }

    public async Task<SetVerdict> VerifyAsync(IReadOnlyList<string> columns, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("At least one column is required to verify.", nameof(columns));
        }

        var columnList = string.Join(", ", columns.Select(Bracket));
        var anyNull = string.Join(" OR ", columns.Select(c => $"{Bracket(c)} IS NULL"));
        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));

        // Two existence probes: a null row anywhere, and a repeated tuple anywhere. Neither computes a full distinct
        // count, and the null probe stops at the first offending row.
        var sql = $"""
            SELECT
                CONVERT(bit, IIF(EXISTS (SELECT 1 FROM {_table} WHERE {anyNull}), 1, 0)) AS HasNulls,
                CONVERT(bit, IIF(EXISTS (SELECT 1 FROM {_table} WHERE {allNotNull} GROUP BY {columnList} HAVING COUNT_BIG(*) > 1), 1, 0)) AS HasDuplicate;
            """;

        await using var command = new SqlCommand(sql, _connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The uniqueness verification returned no row.");
        }

        var hasNulls = reader.GetBoolean(0);
        var hasDuplicate = reader.GetBoolean(1);
        return new SetVerdict
        {
            Columns = columns.ToList(),
            IsUnique = TotalRows > 0 && !hasNulls && !hasDuplicate,
            HasNulls = hasNulls,
            Rows = TotalRows,
        };
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    /// <summary>Everything one catalog batch says about the object: whether it is a base table (page sampling only
    /// works on those), the partition-metadata row count (null for a plain view or an unresolvable object), the
    /// requested columns split into searchable and excluded, and the declared unique keys. When the object cannot be
    /// resolved in the catalog (a synonym, a missing permission), the fallback treats every requested column as
    /// eligible so the probe degrades to plain profiling instead of failing.</summary>
    private sealed record ObjectMetadata
    {
        public bool IsUserTable { get; init; }

        public long? ApproxRows { get; init; }

        public required IReadOnlyList<string> EligibleColumns { get; init; }

        public required IReadOnlyList<ExcludedColumn> ExcludedColumns { get; init; }

        public required IReadOnlyList<IReadOnlyList<string>> DeclaredKeys { get; init; }
    }

    private static async Task<ObjectMetadata> LoadMetadataAsync(
        SqlConnection connection, string qualifiedTable, IReadOnlyList<string> requested, CancellationToken ct)
    {
        var parts = SplitQualifiedName(qualifiedTable);
        if (parts.Count > 3)
        {
            throw new ArgumentException(
                $"'{qualifiedTable}' has more than three name parts; server-qualified names are not supported.", nameof(qualifiedTable));
        }

        // A database-qualified object needs its own database's catalog views; the object id itself resolves either way.
        var pfx = parts.Count == 3 ? Bracket(parts[0]) + "." : string.Empty;

        var sql = $"""
            DECLARE @id int = OBJECT_ID(@object);

            SELECT o.type,
                   (SELECT SUM(p.rows) FROM {pfx}sys.partitions p WHERE p.object_id = @id AND p.index_id IN (0, 1)) AS approx_rows
            FROM {pfx}sys.objects o
            WHERE o.object_id = @id;

            SELECT c.name, c.system_type_id, c.max_length, c.is_computed, ISNULL(cc.is_persisted, 0) AS is_persisted,
                   ISNULL(t.name, N'type ' + CONVERT(nvarchar(12), c.system_type_id)) AS type_name
            FROM {pfx}sys.columns c
            LEFT JOIN {pfx}sys.types t ON t.user_type_id = c.user_type_id
            LEFT JOIN {pfx}sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
            WHERE c.object_id = @id;

            SELECT i.index_id, col.name
            FROM {pfx}sys.indexes i
            JOIN {pfx}sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.key_ordinal > 0
            JOIN {pfx}sys.columns col ON col.object_id = ic.object_id AND col.column_id = ic.column_id
            WHERE i.object_id = @id AND i.is_unique = 1 AND i.is_disabled = 0 AND i.is_hypothetical = 0 AND i.has_filter = 0
            ORDER BY i.index_id, ic.key_ordinal;
            """;

        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        command.Parameters.AddWithValue("@object", qualifiedTable);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        string? objectType = null;
        long? approxRows = null;
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            objectType = reader.GetString(0).Trim();
            approxRows = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }

        var columnInfo = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            columnInfo[reader.GetString(0)] = ExclusionReason(
                systemTypeId: reader.GetByte(1),
                maxLength: reader.GetInt16(2),
                computed: reader.GetBoolean(3),
                persisted: reader.GetBoolean(4),
                typeName: reader.GetString(5));
        }

        var declaredRaw = new List<List<string>>();
        await reader.NextResultAsync(ct).ConfigureAwait(false);
        var currentIndex = int.MinValue;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var indexId = reader.GetInt32(0);
            if (indexId != currentIndex)
            {
                declaredRaw.Add([]);
                currentIndex = indexId;
            }

            declaredRaw[^1].Add(reader.GetString(1));
        }

        if (objectType is null || columnInfo.Count == 0)
        {
            // The catalog cannot see the object (a synonym, a linked construct, a permission gap): degrade to plain
            // profiling of the requested columns rather than failing on metadata the search can live without.
            return new ObjectMetadata
            {
                IsUserTable = false,
                ApproxRows = null,
                EligibleColumns = requested.ToList(),
                ExcludedColumns = [],
                DeclaredKeys = [],
            };
        }

        var eligible = new List<string>(requested.Count);
        var excluded = new List<ExcludedColumn>();
        foreach (var column in requested)
        {
            if (!columnInfo.TryGetValue(column, out var reason))
            {
                excluded.Add(new ExcludedColumn { Column = column, Reason = "not present on the live object" });
            }
            else if (reason is not null)
            {
                excluded.Add(new ExcludedColumn { Column = column, Reason = reason });
            }
            else
            {
                eligible.Add(column);
            }
        }

        // A primary key and a duplicate unique index over the same columns are one declared key, narrowest first.
        var declared = new List<IReadOnlyList<string>>();
        foreach (var set in declaredRaw.OrderBy(s => s.Count))
        {
            if (!declared.Any(d => d.Count == set.Count && new HashSet<string>(d, StringComparer.OrdinalIgnoreCase).SetEquals(set)))
            {
                declared.Add(set);
            }
        }

        return new ObjectMetadata
        {
            IsUserTable = objectType == "U",
            ApproxRows = approxRows,
            EligibleColumns = eligible,
            ExcludedColumns = excluded,
            DeclaredKeys = declared,
        };
    }

    /// <summary>Why a column can never take part in a practical key, or null when it can. LOB and legacy LOB types
    /// cannot be compared or indexed as keys, floating point is imprecise by definition, CLR types have no total
    /// order the engine exposes, rowversion changes on every write, and a non-persisted computed column would be
    /// recomputed for every measured row.</summary>
    private static string? ExclusionReason(byte systemTypeId, short maxLength, bool computed, bool persisted, string typeName)
    {
        if (computed && !persisted)
        {
            return "non-persisted computed column";
        }

        return systemTypeId switch
        {
            34 or 35 or 99 => $"legacy LOB type ({typeName})",
            241 => "xml column",
            98 => "sql_variant column",
            189 => "rowversion changes on every write",
            59 or 62 => $"imprecise floating-point type ({typeName})",
            240 => $"CLR type ({typeName})",
            _ => maxLength == -1 ? $"LOB type ({typeName}(max))" : null,
        };
    }

    /// <summary>Materializes a random sample of the eligible columns into a session temp table and returns the
    /// sampled probe, or null when the source yielded no rows at all (stale metadata over an emptied table), in
    /// which case the caller falls back to a full scan. Tables get page sampling (<c>TABLESAMPLE</c>), which reads
    /// only the sampled pages; views, and a page sample that whiffed on stale statistics, get a Bernoulli row filter:
    /// one streaming pass with no sort in which every row has the same selection chance regardless of physical
    /// order, so a date-clustered table cannot bias the sample to a prefix.</summary>
    private static async Task<SqlServerUniquenessProbe?> TryMaterializeSampleAsync(
        SqlConnection connection, string table, ObjectMetadata meta, long total, long target, CancellationToken ct)
    {
        var columnList = string.Join(", ", meta.EligibleColumns.Select(Bracket));
        long working = 0;
        if (meta.IsUserTable)
        {
            try
            {
                var rows = target.ToString(CultureInfo.InvariantCulture);
                await ExecuteAsync(
                    connection,
                    $"DROP TABLE IF EXISTS #uk_work; SELECT {columnList} INTO #uk_work FROM {table} TABLESAMPLE SYSTEM ({rows} ROWS);",
                    ct).ConfigureAwait(false);
                working = await ScalarLongAsync(connection, "SELECT COUNT_BIG(*) FROM #uk_work;", ct).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // Some tables reject TABLESAMPLE (memory-optimized ones, for instance). Page sampling is only an
                // optimization, so the row filter below takes over; a fundamental failure (permissions, a dropped
                // object) resurfaces on the very next query against the same object.
                working = 0;
            }
        }

        if (working == 0)
        {
            // The modulus keeps roughly one row in total/target. CHECKSUM must be seeded with a column: a predicate
            // that references no column is evaluated once per scan, not once per row, turning the filter into
            // all-or-nothing. CHECKSUM is reduced before ABS so int.MinValue cannot overflow the ABS.
            var modulus = Math.Max(1, total / target).ToString(CultureInfo.InvariantCulture);
            var seed = Bracket(meta.EligibleColumns[0]);
            try
            {
                await ExecuteAsync(
                    connection,
                    $"DROP TABLE IF EXISTS #uk_work; SELECT {columnList} INTO #uk_work FROM {table} WHERE ABS(CHECKSUM(NEWID(), {seed}) % {modulus}) = 0;",
                    ct).ConfigureAwait(false);
                working = await ScalarLongAsync(connection, "SELECT COUNT_BIG(*) FROM #uk_work;", ct).ConfigureAwait(false);
            }
            catch (SqlException)
            {
                // Only reachable with metadata-invisible objects whose first column CHECKSUM cannot digest (the
                // eligible pool otherwise excludes such types). Sampling is an optimization: fall back to the full
                // scan, where a fundamental failure resurfaces immediately with its own error.
                working = 0;
            }
        }

        return working > 0
            ? new SqlServerUniquenessProbe(connection, table, "#uk_work", Math.Max(total, working), working, sampled: true, meta)
            : null;
    }

    /// <summary>The distinct-tuple count of a column set over the non-null rows: a single column directly, a composite
    /// through a <c>HASHBYTES</c> of its length-safe concatenation so many composites fit in one query.</summary>
    private static string DistinctExpression(IReadOnlyList<string> columns)
    {
        if (columns.Count == 1)
        {
            return $"COUNT_BIG(DISTINCT {Bracket(columns[0])})";
        }

        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));
        // NCHAR(31) (unit separator) delimits the parts; every part is converted to nvarchar(max) so the hash reflects
        // the exact tuple. A hash collision could only make the count too low (a key would be missed, never invented),
        // and the final candidate is confirmed exactly by VerifyAsync.
        var concat = "CONCAT(" + string.Join(", NCHAR(31), ", columns.Select(c => $"CONVERT(nvarchar(max), {Bracket(c)})")) + ")";
        return $"COUNT_BIG(DISTINCT CASE WHEN {allNotNull} THEN HASHBYTES('SHA2_256', {concat}) END)";
    }

    private static string NonNullExpression(IReadOnlyList<string> columns)
    {
        if (columns.Count == 1)
        {
            return $"COUNT_BIG({Bracket(columns[0])})";
        }

        var allNotNull = string.Join(" AND ", columns.Select(c => $"{Bracket(c)} IS NOT NULL"));
        return $"COUNT_BIG(CASE WHEN {allNotNull} THEN 1 END)";
    }

    private static async Task<long> ScalarLongAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        var value = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql, CancellationToken ct)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 0 };
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Splits a possibly bracket-quoted qualified name on its top-level dots (dots inside brackets do not
    /// split), returning the unquoted parts.</summary>
    private static IReadOnlyList<string> SplitQualifiedName(string qualified)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inBrackets = false;
        for (var i = 0; i < qualified.Length; i++)
        {
            var ch = qualified[i];
            if (inBrackets)
            {
                if (ch == ']')
                {
                    if (i + 1 < qualified.Length && qualified[i + 1] == ']')
                    {
                        current.Append(']');
                        i++;
                    }
                    else
                    {
                        inBrackets = false;
                    }
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch == '[')
            {
                inBrackets = true;
            }
            else if (ch == '.')
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }

    private static string Bracket(string identifier) => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";
}
