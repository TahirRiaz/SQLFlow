using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;

namespace SqlFlow.SqlServer.Health;

/// <summary>One missing-index advisory from the engine's own tuning DMVs, ranked by
/// <see cref="ImprovementMeasure"/> (the standard seeks-times-cost-times-impact score), with a ready-to-review
/// CREATE INDEX statement. The advisory is evidence from real workload compilations, not a guess; it still
/// deserves review (overlapping indexes, write cost) before running the suggestion.</summary>
public sealed record MissingIndexAdvisory
{
    public required string Database { get; init; }

    public required string Schema { get; init; }

    public required string Table { get; init; }

    /// <summary>Columns the workload filtered with equality predicates, as the DMV lists them ("[a], [b]").</summary>
    public string? EqualityColumns { get; init; }

    /// <summary>Columns filtered with inequality/range predicates.</summary>
    public string? InequalityColumns { get; init; }

    /// <summary>Columns the workload selected, suggested as INCLUDE columns.</summary>
    public string? IncludedColumns { get; init; }

    public required long UserSeeks { get; init; }

    public required long UserScans { get; init; }

    /// <summary>Server-local time of the last seek that would have used the index.</summary>
    public DateTime? LastUserSeek { get; init; }

    public required double AvgTotalUserCost { get; init; }

    /// <summary>Average percentage cost reduction the optimizer estimated (0-100).</summary>
    public required double AvgUserImpactPercent { get; init; }

    /// <summary>(seeks + scans) * avg cost * impact: the standard ranking score for missing-index advisories.</summary>
    public required double ImprovementMeasure { get; init; }

    /// <summary>A generated CREATE INDEX statement implementing the advisory, for review before execution.</summary>
    public required string SuggestedIndexSql { get; init; }
}

/// <summary>Statistics freshness for one statistics object: how many rows changed since the last update, the
/// sample rate it was built with, and whether it has crossed the engine's own auto-update staleness threshold
/// (MIN(500 + 20% of rows, SQRT(1000 * rows)), the SQL Server 2016+ dynamic rule).</summary>
public sealed record StatisticsAdvisory
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string StatisticName { get; init; }

    public required long Rows { get; init; }

    public required long RowsSampled { get; init; }

    /// <summary>The percentage of rows sampled when the statistic was last built (0-100).</summary>
    public required double SamplePercent { get; init; }

    /// <summary>Rows modified since the statistic was last updated.</summary>
    public required long ModificationCounter { get; init; }

    /// <summary>Modifications as a percentage of the table's rows (0-100+; can exceed 100 on churny tables).</summary>
    public required double ModificationPercent { get; init; }

    /// <summary>Server-local time the statistic was last updated; null when it was never built.</summary>
    public DateTime? LastUpdated { get; init; }

    /// <summary>True when the modification counter has crossed the engine's dynamic staleness threshold.</summary>
    public required bool IsStale { get; init; }

    /// <summary>A generated UPDATE STATISTICS statement for the entry, for review before execution.</summary>
    public required string SuggestedUpdateSql { get; init; }
}

/// <summary>Read/write usage for one index since the counters last reset (instance restart, or index rebuild
/// on older versions). <see cref="IsUnused"/> flags a non-constraint index that has been written but never read
/// in that window: a drop candidate, but only after judging the window's length via the report's
/// <c>countersSince</c>.</summary>
public sealed record IndexUsageEntry
{
    public required string Schema { get; init; }

    public required string Table { get; init; }

    public required string IndexName { get; init; }

    public required string IndexType { get; init; }

    public required bool IsUnique { get; init; }

    public required bool IsPrimaryKey { get; init; }

    public required long UserSeeks { get; init; }

    public required long UserScans { get; init; }

    public required long UserLookups { get; init; }

    /// <summary>Seeks + scans + lookups: every read the index served.</summary>
    public required long Reads { get; init; }

    /// <summary>User updates: every write that had to maintain the index.</summary>
    public required long Writes { get; init; }

    /// <summary>Server-local time of the most recent read of any kind; null when never read.</summary>
    public DateTime? LastRead { get; init; }

    public required long SizeKb { get; init; }

    public required long RowCount { get; init; }

    /// <summary>True for a nonclustered, non-constraint index that is maintained by writes but served no reads
    /// since the counters last reset.</summary>
    public required bool IsUnused { get; init; }
}

/// <summary>One cached statement ranked by total elapsed time, from the plan cache's per-statement counters.
/// The plan cache is a rolling window (plans age out, and the counters reset with them), so this answers "what
/// is expensive lately", not "what was expensive ever".</summary>
public sealed record ExpensiveQuery
{
    /// <summary>The database the statement's batch resolved against; null for ad-hoc batches without one.</summary>
    public string? Database { get; init; }

    /// <summary>The statement text, truncated to a reviewable length.</summary>
    public required string StatementText { get; init; }

    public required long ExecutionCount { get; init; }

    public required double TotalElapsedMs { get; init; }

    public required double AvgElapsedMs { get; init; }

    public required double MaxElapsedMs { get; init; }

    public required double TotalCpuMs { get; init; }

    public required double AvgCpuMs { get; init; }

    public required long TotalLogicalReads { get; init; }

    public required long AvgLogicalReads { get; init; }

    /// <summary>Server-local time of the statement's most recent execution.</summary>
    public required DateTime LastExecutionTime { get; init; }

    /// <summary>Server-local time the plan entered the cache; the counters cover this window.</summary>
    public required DateTime CachedSince { get; init; }
}

/// <summary>
/// The warehouse-health measurement behind the <c>missingIndexes</c> / <c>statisticsHealth</c> / <c>indexUsage</c>
/// / <c>topQueries</c> compute operations: read-only probes over SQL Server's dynamic management views, executed
/// on whatever worker node claimed the task (the control plane never opens a datasource connection). Every query
/// is parameterized, scoped to the connection's current database where the DMV is database-scoped, and excludes
/// system objects. The DMVs require VIEW SERVER STATE (VIEW DATABASE STATE on Azure SQL Database); a login
/// without it fails with the engine's own permission error, which the task records verbatim for the operator.
/// Commands run without their own timeout, bounded by the executor's cancellation budget, matching the catalog
/// readers.
/// </summary>
public static class SqlServerHealthProbe
{
    /// <summary>The longest statement text an expensive-query entry carries; beyond this the text is truncated
    /// with a marker, keeping a pathological batch from bloating the task result.</summary>
    public const int MaxStatementTextChars = 4000;

    public static async Task<IReadOnlyList<MissingIndexAdvisory>> MissingIndexesAsync(
        string connectionString, string? database, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, database, ct).ConfigureAwait(false);
        const string sql = """
            SELECT TOP (@limit)
                DB_NAME() AS database_name,
                OBJECT_SCHEMA_NAME(mid.object_id) AS schema_name,
                OBJECT_NAME(mid.object_id) AS table_name,
                mid.equality_columns,
                mid.inequality_columns,
                mid.included_columns,
                migs.user_seeks,
                migs.user_scans,
                migs.last_user_seek,
                migs.avg_total_user_cost,
                migs.avg_user_impact,
                CONVERT(float, migs.user_seeks + migs.user_scans)
                    * migs.avg_total_user_cost * (migs.avg_user_impact / 100.0) AS improvement_measure
            FROM sys.dm_db_missing_index_group_stats AS migs
            JOIN sys.dm_db_missing_index_groups AS mig ON mig.index_group_handle = migs.group_handle
            JOIN sys.dm_db_missing_index_details AS mid ON mid.index_handle = mig.index_handle
            WHERE mid.database_id = DB_ID()
              AND OBJECT_SCHEMA_NAME(mid.object_id) IS NOT NULL
            ORDER BY improvement_measure DESC;
            """;

        await using var command = CreateCommand(connection, sql, limit);
        var advisories = new List<MissingIndexAdvisory>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(1);
            var table = reader.GetString(2);
            var equality = NullableString(reader, 3);
            var inequality = NullableString(reader, 4);
            var included = NullableString(reader, 5);
            advisories.Add(new MissingIndexAdvisory
            {
                Database = reader.GetString(0),
                Schema = schema,
                Table = table,
                EqualityColumns = equality,
                InequalityColumns = inequality,
                IncludedColumns = included,
                UserSeeks = reader.GetInt64(6),
                UserScans = reader.GetInt64(7),
                LastUserSeek = NullableDateTime(reader, 8),
                AvgTotalUserCost = reader.GetDouble(9),
                AvgUserImpactPercent = reader.GetDouble(10),
                ImprovementMeasure = reader.GetDouble(11),
                SuggestedIndexSql = BuildIndexSuggestion(schema, table, equality, inequality, included),
            });
        }

        return advisories;
    }

    public static async Task<IReadOnlyList<StatisticsAdvisory>> StatisticsHealthAsync(
        string connectionString, string? database, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, database, ct).ConfigureAwait(false);
        const string sql = """
            SELECT TOP (@limit)
                s.name AS schema_name,
                o.name AS table_name,
                st.name AS statistic_name,
                sp.rows,
                sp.rows_sampled,
                sp.modification_counter,
                sp.last_updated
            FROM sys.stats AS st
            JOIN sys.objects AS o ON o.object_id = st.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            CROSS APPLY sys.dm_db_stats_properties(st.object_id, st.stats_id) AS sp
            WHERE o.is_ms_shipped = 0
              AND o.type IN ('U', 'V')
              AND sp.rows IS NOT NULL
              AND sp.rows > 0
            ORDER BY CONVERT(float, sp.modification_counter) / sp.rows DESC, sp.rows DESC;
            """;

        await using var command = CreateCommand(connection, sql, limit);
        var advisories = new List<StatisticsAdvisory>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var statistic = reader.GetString(2);
            var rows = reader.GetInt64(3);
            var sampled = reader.GetInt64(4);
            var modifications = reader.GetInt64(5);

            // The engine's dynamic auto-update threshold (SQL Server 2016+ default): the lower of the legacy
            // 500 + 20% rule and SQRT(1000 * rows). Crossing it is the same signal the engine itself acts on.
            var threshold = Math.Min(500.0 + 0.20 * rows, Math.Sqrt(1000.0 * rows));
            advisories.Add(new StatisticsAdvisory
            {
                Schema = schema,
                Table = table,
                StatisticName = statistic,
                Rows = rows,
                RowsSampled = sampled,
                SamplePercent = rows > 0 ? sampled * 100.0 / rows : 0,
                ModificationCounter = modifications,
                ModificationPercent = rows > 0 ? modifications * 100.0 / rows : 0,
                LastUpdated = NullableDateTime(reader, 6),
                IsStale = modifications >= threshold,
                SuggestedUpdateSql = string.Create(
                    CultureInfo.InvariantCulture,
                    $"UPDATE STATISTICS {Quote(schema)}.{Quote(table)} {Quote(statistic)} WITH RESAMPLE;"),
            });
        }

        return advisories;
    }

    public static async Task<IReadOnlyList<IndexUsageEntry>> IndexUsageAsync(
        string connectionString, string? database, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, database, ct).ConfigureAwait(false);
        const string sql = """
            SELECT TOP (@limit)
                s.name AS schema_name,
                o.name AS table_name,
                i.name AS index_name,
                i.type_desc,
                i.is_unique,
                i.is_primary_key,
                ISNULL(us.user_seeks, 0) AS user_seeks,
                ISNULL(us.user_scans, 0) AS user_scans,
                ISNULL(us.user_lookups, 0) AS user_lookups,
                ISNULL(us.user_updates, 0) AS user_updates,
                (SELECT MAX(v) FROM (VALUES (us.last_user_seek), (us.last_user_scan), (us.last_user_lookup)) AS reads(v)) AS last_read,
                ISNULL(ps.used_kb, 0) AS size_kb,
                ISNULL(ps.row_count, 0) AS row_count
            FROM sys.indexes AS i
            JOIN sys.objects AS o ON o.object_id = i.object_id
            JOIN sys.schemas AS s ON s.schema_id = o.schema_id
            LEFT JOIN sys.dm_db_index_usage_stats AS us
                ON us.database_id = DB_ID() AND us.object_id = i.object_id AND us.index_id = i.index_id
            OUTER APPLY (
                SELECT SUM(p.used_page_count) * 8 AS used_kb, SUM(p.row_count) AS row_count
                FROM sys.dm_db_partition_stats AS p
                WHERE p.object_id = i.object_id AND p.index_id = i.index_id
            ) AS ps
            WHERE o.is_ms_shipped = 0
              AND o.type = 'U'
              AND i.type > 0
              AND i.name IS NOT NULL
            ORDER BY
                CASE WHEN ISNULL(us.user_seeks, 0) + ISNULL(us.user_scans, 0) + ISNULL(us.user_lookups, 0) = 0
                     THEN ISNULL(us.user_updates, 0) ELSE -1 END DESC,
                ISNULL(us.user_updates, 0) DESC;
            """;

        await using var command = CreateCommand(connection, sql, limit);
        var entries = new List<IndexUsageEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var isUnique = reader.GetBoolean(4);
            var isPrimaryKey = reader.GetBoolean(5);
            var seeks = reader.GetInt64(6);
            var scans = reader.GetInt64(7);
            var lookups = reader.GetInt64(8);
            var writes = reader.GetInt64(9);
            var reads = seeks + scans + lookups;
            var typeDesc = reader.GetString(3);
            entries.Add(new IndexUsageEntry
            {
                Schema = reader.GetString(0),
                Table = reader.GetString(1),
                IndexName = reader.GetString(2),
                IndexType = typeDesc,
                IsUnique = isUnique,
                IsPrimaryKey = isPrimaryKey,
                UserSeeks = seeks,
                UserScans = scans,
                UserLookups = lookups,
                Reads = reads,
                Writes = writes,
                LastRead = NullableDateTime(reader, 10),
                SizeKb = reader.GetInt64(11),
                RowCount = reader.GetInt64(12),
                IsUnused = reads == 0 && writes > 0 && !isPrimaryKey && !isUnique
                    && typeDesc.Contains("NONCLUSTERED", StringComparison.OrdinalIgnoreCase),
            });
        }

        return entries;
    }

    public static async Task<IReadOnlyList<ExpensiveQuery>> TopQueriesAsync(
        string connectionString, string? database, int limit, CancellationToken ct)
    {
        await using var connection = await OpenAsync(connectionString, database, ct).ConfigureAwait(false);
        const string sql = """
            SELECT TOP (@limit)
                DB_NAME(CONVERT(int, pa.value)) AS database_name,
                SUBSTRING(
                    st.text,
                    (qs.statement_start_offset / 2) + 1,
                    ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                      ELSE qs.statement_end_offset END - qs.statement_start_offset) / 2) + 1) AS statement_text,
                qs.execution_count,
                CONVERT(float, qs.total_elapsed_time) / 1000.0 AS total_elapsed_ms,
                CONVERT(float, qs.total_elapsed_time) / 1000.0 / qs.execution_count AS avg_elapsed_ms,
                CONVERT(float, qs.max_elapsed_time) / 1000.0 AS max_elapsed_ms,
                CONVERT(float, qs.total_worker_time) / 1000.0 AS total_cpu_ms,
                CONVERT(float, qs.total_worker_time) / 1000.0 / qs.execution_count AS avg_cpu_ms,
                qs.total_logical_reads,
                qs.total_logical_reads / qs.execution_count AS avg_logical_reads,
                qs.last_execution_time,
                qs.creation_time
            FROM sys.dm_exec_query_stats AS qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) AS st
            OUTER APPLY (
                SELECT value FROM sys.dm_exec_plan_attributes(qs.plan_handle) WHERE attribute = 'dbid'
            ) AS pa
            WHERE qs.execution_count > 0
            ORDER BY qs.total_elapsed_time DESC;
            """;

        await using var command = CreateCommand(connection, sql, limit);
        var queries = new List<ExpensiveQuery>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var text = NullableString(reader, 1) ?? string.Empty;
            if (text.Length > MaxStatementTextChars)
            {
                text = string.Concat(text.AsSpan(0, MaxStatementTextChars), " /* ...truncated */");
            }

            queries.Add(new ExpensiveQuery
            {
                Database = NullableString(reader, 0),
                StatementText = text.Trim(),
                ExecutionCount = reader.GetInt64(2),
                TotalElapsedMs = reader.GetDouble(3),
                AvgElapsedMs = reader.GetDouble(4),
                MaxElapsedMs = reader.GetDouble(5),
                TotalCpuMs = reader.GetDouble(6),
                AvgCpuMs = reader.GetDouble(7),
                TotalLogicalReads = reader.GetInt64(8),
                AvgLogicalReads = reader.GetInt64(9),
                LastExecutionTime = reader.GetDateTime(10),
                CachedSince = reader.GetDateTime(11),
            });
        }

        return queries;
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, string? database, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
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

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, int limit)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0; // the executor's cancellation budget bounds the probe
        command.Parameters.Add(new SqlParameter("@limit", limit));
        return command;
    }

    /// <summary>The CREATE INDEX statement implementing a missing-index advisory: equality columns first, then
    /// inequality (the DMV already orders within each group), selected columns as INCLUDEs. The name encodes the
    /// key columns so two advisories on one table stay distinct, capped to the 128-character identifier limit.</summary>
    private static string BuildIndexSuggestion(
        string schema, string table, string? equality, string? inequality, string? included)
    {
        var keyColumns = new List<string>();
        keyColumns.AddRange(SplitColumns(equality));
        keyColumns.AddRange(SplitColumns(inequality));

        var name = new StringBuilder("IX_").Append(SanitizeIdentifierPart(table));
        foreach (var column in keyColumns)
        {
            var part = SanitizeIdentifierPart(column);
            if (name.Length + part.Length + 1 > 120)
            {
                break;
            }

            name.Append('_').Append(part);
        }

        var sql = new StringBuilder("CREATE NONCLUSTERED INDEX ")
            .Append(Quote(name.ToString()))
            .Append(" ON ").Append(Quote(schema)).Append('.').Append(Quote(table))
            .Append(" (").Append(string.Join(", ", keyColumns.Select(Quote))).Append(')');
        var includeColumns = SplitColumns(included);
        if (includeColumns.Count > 0)
        {
            sql.Append(" INCLUDE (").Append(string.Join(", ", includeColumns.Select(Quote))).Append(')');
        }

        return sql.Append(';').ToString();
    }

    /// <summary>Splits a DMV column list ("[a], [b]") into bare column names. The DMV brackets every name, so a
    /// comma inside a column name is impossible outside its brackets; a defensive trim handles both shapes.</summary>
    private static List<string> SplitColumns(string? dmvList)
    {
        if (string.IsNullOrWhiteSpace(dmvList))
        {
            return [];
        }

        return dmvList
            .Split("],", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Trim().TrimStart('[').TrimEnd(']'))
            .Where(part => part.Length > 0)
            .ToList();
    }

    private static string SanitizeIdentifierPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || ch == '_')
            {
                builder.Append(ch);
            }
        }

        return builder.Length > 0 ? builder.ToString() : "col";
    }

    private static string Quote(string identifier)
        => "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    private static string? NullableString(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static DateTime? NullableDateTime(SqlDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
}
