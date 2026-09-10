using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using SqlFlow.Core;
using SqlFlow.Core.Comparison;

namespace SqlFlow.SqlServer.Comparison;

/// <summary>
/// Compares the NEW V3 estate against the OLD production baseline, server-side. The task's own connection is
/// the new side; the old side is reached through a configured linked server with <c>OPENQUERY</c>, so the
/// aggregation and the anti-join execute on the two engines and only the answer travels. That is what lets a
/// billion-row table be compared at all: nothing here streams rows to a worker node.
///
/// Read-only against both estates. The only thing it writes is a session-scoped temp table in tempdb on the
/// new side, which is how the two sides are brought together for a single-pass comparison; it is dropped when
/// the connection closes. Every identifier and expression it interpolates has already passed
/// <see cref="SqlFragmentGuard"/> at the request boundary.
/// </summary>
public static class SqlServerBaselineComparer
{
    /// <summary>The staging tables the data comparison brings both sides into. Named distinctively so they can
    /// never collide with a temp table a caller's session already holds.</summary>
    private const string OldStage = "#sf_cmp_old";
    private const string NewStage = "#sf_cmp_new";
    private const string OldDedup = "#sf_cmp_old1";
    private const string NewDedup = "#sf_cmp_new1";

    /// <summary>The most columns the per-column parity pass will break a mismatch down by. Each column costs a
    /// UNION ALL branch over the staged rows, so a very wide table is summarised rather than exploded.</summary>
    private const int MaxPerColumnParity = 100;

    // ----------------------------------------------------------------------------------------------------
    // Inventory: which tables exist on each side, and how far their row counts have drifted.
    // ----------------------------------------------------------------------------------------------------

    public static async Task<BaselineInventoryReport> InventoryAsync(
        string connectionString, BaselineComparisonRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await OpenAsync(connectionString, ct).ConfigureAwait(false);

        var current = await ReadInventoryAsync(
            connection, CurrentInventorySql(), new SqlParameter("@schema", request.Schema), ct).ConfigureAwait(false);

        var remote = RemoteInventorySql(request.BaselineDatabase, request.EffectiveBaselineSchema);
        var baseline = await ReadInventoryAsync(
            connection, OpenQuery(request.LinkedServer, remote), null, ct).ConfigureAwait(false);

        var names = current.Keys.Union(baseline.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var entries = new List<BaselineInventoryEntry>(names.Length);
        foreach (var name in names)
        {
            var inCurrent = current.TryGetValue(name, out var currentRows);
            var inBaseline = baseline.TryGetValue(name, out var baselineRows);
            var status = (inBaseline, inCurrent) switch
            {
                (true, false) => "missingInCurrent",
                (false, true) => "missingInBaseline",
                _ when currentRows == baselineRows => "match",
                _ => "drift",
            };

            entries.Add(new BaselineInventoryEntry
            {
                Schema = request.Schema,
                Name = name,
                InBaseline = inBaseline,
                InCurrent = inCurrent,
                BaselineRows = inBaseline ? baselineRows : null,
                CurrentRows = inCurrent ? currentRows : null,
                Status = status,
            });
        }

        // Worst first: a table one estate does not have at all outranks a table that merely drifted, and a
        // bigger drift outranks a smaller one. A caller reading only the first page then reads the real news.
        var ranked = entries
            .OrderBy(e => e.Status switch
            {
                "missingInCurrent" => 0,
                "missingInBaseline" => 1,
                "drift" => 2,
                _ => 3,
            })
            .ThenByDescending(e => Math.Abs(e.Delta ?? 0))
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var truncated = ranked.Count > request.Limit;
        return new BaselineInventoryReport
        {
            LinkedServer = request.LinkedServer,
            BaselineDatabase = request.BaselineDatabase,
            Schema = request.Schema,
            BaselineObjects = baseline.Count,
            CurrentObjects = current.Count,
            Matching = entries.Count(e => e.Status == "match"),
            Drifting = entries.Count(e => e.Status == "drift"),
            MissingInCurrent = entries.Count(e => e.Status == "missingInCurrent"),
            MissingInBaseline = entries.Count(e => e.Status == "missingInBaseline"),
            Truncated = truncated,
            CountsFromMetadata = true,
            Entries = truncated ? ranked.Take(request.Limit).ToArray() : ranked,
        };
    }

    /// <summary>Row counts from the partition metadata rather than COUNT(*): exact for a settled table, and
    /// free, which is what makes a whole-schema inventory affordable on either estate.</summary>
    private static string CurrentInventorySql() => """
        SELECT o.name AS table_name,
               SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS row_count
        FROM sys.objects AS o
        JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        JOIN sys.dm_db_partition_stats AS ps ON ps.object_id = o.object_id
        WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND s.name = @schema
        GROUP BY o.name;
        """;

    private static string RemoteInventorySql(string database, string schema) => string.Concat(
        "SELECT o.name AS table_name, ",
        "SUM(CASE WHEN ps.index_id IN (0, 1) THEN ps.row_count ELSE 0 END) AS row_count ",
        $"FROM {Quote(database)}.sys.objects AS o ",
        $"JOIN {Quote(database)}.sys.schemas AS s ON s.schema_id = o.schema_id ",
        $"JOIN {Quote(database)}.sys.dm_db_partition_stats AS ps ON ps.object_id = o.object_id ",
        $"WHERE o.type = 'U' AND o.is_ms_shipped = 0 AND s.name = {Literal(schema)} ",
        "GROUP BY o.name");

    private static async Task<Dictionary<string, long>> ReadInventoryAsync(
        SqlConnection connection, string sql, SqlParameter? parameter, CancellationToken ct)
    {
        await using var command = Command(connection, sql);
        if (parameter is not null)
        {
            command.Parameters.Add(parameter);
        }

        var rows = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows[reader.GetString(0)] = reader.GetInt64(1);
        }

        return rows;
    }

    // ----------------------------------------------------------------------------------------------------
    // Schema: the column-for-column, position-by-position comparison.
    // ----------------------------------------------------------------------------------------------------

    public static async Task<BaselineSchemaReport> SchemaAsync(
        string connectionString, BaselineComparisonRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await OpenAsync(connectionString, ct).ConfigureAwait(false);

        var currentName = $"{request.Schema}.{request.ObjectName}";
        var baselineName = $"{request.EffectiveBaselineSchema}.{request.EffectiveBaselineObject}";

        var currentColumns = await ReadColumnsAsync(
            connection, CurrentColumnSql(), new SqlParameter("@qualified", $"{Quote(request.Schema)}.{Quote(request.ObjectName!)}"),
            ct).ConfigureAwait(false);
        if (currentColumns.Count == 0)
        {
            throw new SqlFlowException($"The object {currentName} does not exist on the current estate.");
        }

        var remote = RemoteColumnSql(
            request.BaselineDatabase, request.EffectiveBaselineSchema, request.EffectiveBaselineObject!);
        var baselineColumns = await ReadColumnsAsync(
            connection, OpenQuery(request.LinkedServer, remote), null, ct).ConfigureAwait(false);
        if (baselineColumns.Count == 0)
        {
            throw new SqlFlowException(
                $"The object {baselineName} does not exist in {request.BaselineDatabase} on linked server " +
                $"{request.LinkedServer}.");
        }

        var differences = DiffColumns(baselineColumns, currentColumns);
        var identical = differences.Count == 0;
        var sameColumnSet = baselineColumns.Select(c => c.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(
                currentColumns.Select(c => c.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);

        return new BaselineSchemaReport
        {
            LinkedServer = request.LinkedServer,
            BaselineObject = $"{request.BaselineDatabase}.{baselineName}",
            CurrentObject = currentName,
            Identical = identical,
            SameColumnSet = sameColumnSet,
            BaselineColumns = baselineColumns,
            CurrentColumns = currentColumns,
            Differences = differences,
            Verdict = Verdict(identical, sameColumnSet, differences.Count),
        };
    }

    /// <summary>
    /// What the shape difference means for the migration, in the terms the estate's rules are written in: an
    /// identical shape may be transferred directly; a same-names-different-order shape is exactly the case
    /// that needs a compatibility view under the old name so downstream keeps seeing its old format; a
    /// different column SET is a mapping problem that no view alone solves.
    /// </summary>
    private static string Verdict(bool identical, bool sameColumnSet, int differenceCount)
    {
        if (identical)
        {
            return "Identical: same columns, same order, same types, same nullability. A direct " +
                   "INSERT ... SELECT transfer from the baseline is structurally valid.";
        }

        if (sameColumnSet)
        {
            return $"Same column set, but {differenceCount} position(s) differ in order, type, or nullability. " +
                   "This is the case a compatibility view is for: keep the physical table as V3 built it and " +
                   "create a view under the OLD name that CASTs every column to its old type in old order, so " +
                   "downstream sees an unchanged shape. Do not blind-transfer; map columns explicitly.";
        }

        return $"The column SETS differ across {differenceCount} position(s). Establish which columns " +
               "correspond before comparing data or transferring anything; a positional transfer would " +
               "silently write values into the wrong columns.";
    }

    private static string CurrentColumnSql() => ColumnProjection("OBJECT_ID(@qualified)");

    private static string RemoteColumnSql(string database, string schema, string name)
    {
        // OBJECT_ID resolves in the database it is called in, so the whole remote statement is qualified and
        // the name it is given is the two-part name inside that database.
        var target = $"{Quote(database)}.sys";
        var objectId = $"OBJECT_ID({Literal($"{Quote(database)}.{Quote(schema)}.{Quote(name)}")})";
        return string.Concat(
            "SELECT c.column_id, c.name, t.name AS type_name, c.max_length, c.precision, c.scale, ",
            "c.is_nullable, c.is_identity ",
            $"FROM {target}.columns AS c ",
            $"JOIN {target}.types AS t ON t.user_type_id = c.user_type_id ",
            $"WHERE c.object_id = {objectId} ",
            "ORDER BY c.column_id");
    }

    private static string ColumnProjection(string objectIdExpression) => $"""
        SELECT c.column_id, c.name, t.name AS type_name, c.max_length, c.precision, c.scale,
               c.is_nullable, c.is_identity
        FROM sys.columns AS c
        JOIN sys.types AS t ON t.user_type_id = c.user_type_id
        WHERE c.object_id = {objectIdExpression}
        ORDER BY c.column_id;
        """;

    private static async Task<IReadOnlyList<BaselineColumn>> ReadColumnsAsync(
        SqlConnection connection, string sql, SqlParameter? parameter, CancellationToken ct)
    {
        await using var command = Command(connection, sql);
        if (parameter is not null)
        {
            command.Parameters.Add(parameter);
        }

        var columns = new List<BaselineColumn>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var typeName = reader.GetString(2);
            var maxLength = reader.GetInt16(3);
            var precision = reader.GetByte(4);
            var scale = reader.GetByte(5);
            columns.Add(new BaselineColumn
            {
                Position = reader.GetInt32(0),
                Name = reader.GetString(1),
                TypeFull = FormatType(typeName, maxLength, precision, scale),
                Nullable = reader.GetBoolean(6),
                IsIdentity = reader.GetBoolean(7),
            });
        }

        return columns;
    }

    /// <summary>
    /// The type with its length, precision, and scale inline. A comparison that reports "both varchar" while
    /// one side is varchar(50) and the other varchar(100) is not a comparison; the n-types halve their
    /// byte length back to characters so the two estates are described the same way.
    /// </summary>
    private static string FormatType(string typeName, short maxLength, byte precision, byte scale)
    {
        switch (typeName.ToLowerInvariant())
        {
            case "varchar" or "char" or "varbinary" or "binary":
                return $"{typeName}({(maxLength == -1 ? "max" : maxLength.ToString(CultureInfo.InvariantCulture))})";
            case "nvarchar" or "nchar":
                return $"{typeName}({(maxLength == -1 ? "max" : (maxLength / 2).ToString(CultureInfo.InvariantCulture))})";
            case "decimal" or "numeric":
                return string.Create(CultureInfo.InvariantCulture, $"{typeName}({precision},{scale})");
            case "datetime2" or "time" or "datetimeoffset":
                return string.Create(CultureInfo.InvariantCulture, $"{typeName}({scale})");
            default:
                return typeName;
        }
    }

    /// <summary>
    /// The position-by-position diff. Walking positions rather than matching names is deliberate: column ORDER
    /// is part of the contract for any consumer doing SELECT * or positional access, so a reordering must be
    /// reported as a difference and not silently matched away.
    /// </summary>
    private static IReadOnlyList<BaselineSchemaDifference> DiffColumns(
        IReadOnlyList<BaselineColumn> baseline, IReadOnlyList<BaselineColumn> current)
    {
        var differences = new List<BaselineSchemaDifference>();
        var count = Math.Max(baseline.Count, current.Count);
        for (var i = 0; i < count; i++)
        {
            var oldColumn = i < baseline.Count ? baseline[i] : null;
            var newColumn = i < current.Count ? current[i] : null;
            if (oldColumn is null && newColumn is null)
            {
                continue;
            }

            if (oldColumn is not null && newColumn is not null && oldColumn.Describe() == newColumn.Describe())
            {
                continue;
            }

            differences.Add(new BaselineSchemaDifference
            {
                Position = i + 1,
                Kind = ClassifyDifference(oldColumn, newColumn, baseline, current),
                Baseline = oldColumn?.Describe(),
                Current = newColumn?.Describe(),
            });
        }

        return differences;
    }

    private static string ClassifyDifference(
        BaselineColumn? oldColumn, BaselineColumn? newColumn,
        IReadOnlyList<BaselineColumn> baseline, IReadOnlyList<BaselineColumn> current)
    {
        if (oldColumn is null)
        {
            // A name the baseline holds somewhere else is a reorder, not an addition; the distinction decides
            // whether a compatibility view is enough.
            return baseline.Any(c => string.Equals(c.Name, newColumn!.Name, StringComparison.OrdinalIgnoreCase))
                ? "reordered"
                : "added";
        }

        if (newColumn is null)
        {
            return current.Any(c => string.Equals(c.Name, oldColumn.Name, StringComparison.OrdinalIgnoreCase))
                ? "reordered"
                : "removed";
        }

        if (!string.Equals(oldColumn.Name, newColumn.Name, StringComparison.OrdinalIgnoreCase))
        {
            var bothPresent =
                current.Any(c => string.Equals(c.Name, oldColumn.Name, StringComparison.OrdinalIgnoreCase))
                && baseline.Any(c => string.Equals(c.Name, newColumn.Name, StringComparison.OrdinalIgnoreCase));
            return bothPresent ? "reordered" : "renamed";
        }

        return !string.Equals(oldColumn.TypeFull, newColumn.TypeFull, StringComparison.OrdinalIgnoreCase)
            ? "typeChanged"
            : "nullabilityChanged";
    }

    // ----------------------------------------------------------------------------------------------------
    // Data: count decomposition, bidirectional anti-join, and value parity on the shared keys.
    // ----------------------------------------------------------------------------------------------------

    public static async Task<BaselineDataReport> DataAsync(
        string connectionString, BaselineComparisonRequest request, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await OpenAsync(connectionString, ct).ConfigureAwait(false);

        var plan = await PlanAsync(connection, request, ct).ConfigureAwait(false);
        await StageAsync(connection, request, plan, ct).ConfigureAwait(false);

        var counts = await ReadCountsAsync(connection, plan, ct).ConfigureAwait(false);
        var antiJoin = await ReadAntiJoinAsync(connection, request, plan, ct).ConfigureAwait(false);

        BaselineValueParity? parity = null;
        if (plan.CompareColumns.Count > 0)
        {
            await DedupAsync(connection, plan, ct).ConfigureAwait(false);
            parity = await ReadValueParityAsync(connection, plan, ct).ConfigureAwait(false);
        }

        var findings = Interpret(counts, antiJoin, parity, plan);
        return new BaselineDataReport
        {
            LinkedServer = request.LinkedServer,
            BaselineObject =
                $"{request.BaselineDatabase}.{request.EffectiveBaselineSchema}.{request.EffectiveBaselineObject}",
            CurrentObject = $"{request.Schema}.{request.ObjectName}",
            KeyExpressions = request.KeyExpressions,
            ComparedColumns = plan.CompareColumns,
            Filter = request.Where,
            Counts = counts,
            AntiJoin = antiJoin,
            ValueParity = parity,
            Verdict = Classify(antiJoin, parity),
            Findings = findings,
        };
    }

    /// <summary>The comparison's resolved shape: the key aliases, the columns staged, and the columns compared
    /// for value parity, worked out once from the new side's own catalog.</summary>
    private sealed record ComparisonPlan
    {
        public required IReadOnlyList<string> KeyExpressions { get; init; }

        public required IReadOnlyList<string> KeyAliases { get; init; }

        public required IReadOnlyList<string> CompareColumns { get; init; }

        /// <summary>The columns staged on both sides: the compared columns, which is what the value parity
        /// pass reads. The key expressions ride alongside them under their aliases.</summary>
        public IReadOnlyList<string> StagedColumns => CompareColumns;

        public string AliasList => string.Join(", ", KeyAliases);

        public string KeyJoin => string.Join(
            " AND ", KeyAliases.Select(a => $"o.{a} = n.{a}"));

        /// <summary>The projection both sides are staged with: the key expressions under stable aliases, then
        /// the compared columns by name, so the two temp tables are column-for-column comparable.</summary>
        public string Projection => string.Join(", ",
            KeyExpressions.Select((e, i) => $"{e} AS {KeyAliases[i]}")
                .Concat(CompareColumns.Select(Quote)));
    }

    /// <summary>
    /// Resolves which columns the value-parity pass compares. The default is every column on the NEW side
    /// except the identity column (the two estates assign surrogates independently, so comparing them is
    /// guaranteed noise), the bare key columns (already compared as the key), and anything matching the
    /// exclusion pattern, which exists so the provenance and audit columns do not report a mismatch on every
    /// row for legitimately differing load timestamps.
    /// </summary>
    private static async Task<ComparisonPlan> PlanAsync(
        SqlConnection connection, BaselineComparisonRequest request, CancellationToken ct)
    {
        var aliases = request.KeyExpressions.Select((_, i) => $"k{i}").ToArray();
        if (request.CompareColumns.Count > 0)
        {
            return new ComparisonPlan
            {
                KeyExpressions = request.KeyExpressions,
                KeyAliases = aliases,
                CompareColumns = request.CompareColumns,
            };
        }

        const string sql = """
            SELECT c.name
            FROM sys.columns AS c
            WHERE c.object_id = OBJECT_ID(@qualified)
              AND c.is_identity = 0
              AND c.name NOT LIKE @excluded ESCAPE '\'
            ORDER BY c.column_id;
            """;

        await using var command = Command(connection, sql);
        command.Parameters.Add(new SqlParameter(
            "@qualified", $"{Quote(request.Schema)}.{Quote(request.ObjectName!)}"));
        command.Parameters.Add(new SqlParameter("@excluded", request.ExcludeColumnPattern));

        // A key given as a bare column name is already compared as part of the key; a key given as an
        // expression over a column is not, so that column stays in the value-parity set.
        var bareKeys = request.KeyExpressions
            .Select(e => e.Trim().Trim('[', ']'))
            .Where(e => e.All(ch => char.IsLetterOrDigit(ch) || ch is '_' or '#' or '$' or ' '))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            if (!bareKeys.Contains(name))
            {
                columns.Add(name);
            }
        }

        return new ComparisonPlan
        {
            KeyExpressions = request.KeyExpressions,
            KeyAliases = aliases,
            CompareColumns = columns,
        };
    }

    /// <summary>
    /// Brings both sides into session temp tables with the same projection: the old side pulled through
    /// OPENQUERY so its scan and filter run on the old server, the new side read locally. Indexing the key on
    /// each staged table is what keeps the anti-join and the parity join from degenerating into a scan-per-row.
    /// </summary>
    private static async Task StageAsync(
        SqlConnection connection, BaselineComparisonRequest request, ComparisonPlan plan, CancellationToken ct)
    {
        var filter = request.Where is null ? string.Empty : $" WHERE {request.Where}";
        var remote =
            $"SELECT {plan.Projection} FROM {Quote(request.BaselineDatabase)}." +
            $"{Quote(request.EffectiveBaselineSchema)}.{Quote(request.EffectiveBaselineObject!)}{filter}";

        var sql = $"""
            SET NOCOUNT ON;
            DROP TABLE IF EXISTS {OldStage};
            DROP TABLE IF EXISTS {NewStage};
            SELECT * INTO {OldStage} FROM {OpenQuery(request.LinkedServer, remote)} AS remote_side;
            SELECT {plan.Projection} INTO {NewStage}
            FROM {Quote(request.Schema)}.{Quote(request.ObjectName!)}{filter};
            CREATE INDEX ix_key ON {OldStage} ({plan.AliasList});
            CREATE INDEX ix_key ON {NewStage} ({plan.AliasList});
            """;

        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<BaselineCountDecomposition> ReadCountsAsync(
        SqlConnection connection, ComparisonPlan plan, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                (SELECT COUNT_BIG(*) FROM {OldStage}) AS baseline_rows,
                (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT {plan.AliasList} FROM {OldStage}) AS d) AS baseline_keys,
                (SELECT COUNT_BIG(*) FROM {NewStage}) AS current_rows,
                (SELECT COUNT_BIG(*) FROM (SELECT DISTINCT {plan.AliasList} FROM {NewStage}) AS d) AS current_keys;
            """;

        await using var command = Command(connection, sql);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new SqlFlowException("The count decomposition returned no row.");
        }

        return new BaselineCountDecomposition
        {
            BaselinePhysicalRows = reader.GetInt64(0),
            BaselineDistinctKeys = reader.GetInt64(1),
            CurrentPhysicalRows = reader.GetInt64(2),
            CurrentDistinctKeys = reader.GetInt64(3),
        };
    }

    /// <summary>
    /// The anti-join in BOTH directions, always. A table that is "sometimes more, sometimes less" after a
    /// migration is the normal case, and a one-directional check reads it as clean.
    /// </summary>
    private static async Task<BaselineAntiJoin> ReadAntiJoinAsync(
        SqlConnection connection, BaselineComparisonRequest request, ComparisonPlan plan, CancellationToken ct)
    {
        var sql = $"""
            SELECT
                (SELECT COUNT_BIG(*) FROM (
                    SELECT DISTINCT {plan.AliasList} FROM {OldStage} AS o
                    WHERE NOT EXISTS (SELECT 1 FROM {NewStage} AS n WHERE {plan.KeyJoin})) AS a) AS missing_from_current,
                (SELECT COUNT_BIG(*) FROM (
                    SELECT DISTINCT {plan.AliasList} FROM {NewStage} AS n
                    WHERE NOT EXISTS (SELECT 1 FROM {OldStage} AS o WHERE {plan.KeyJoin})) AS b) AS missing_from_baseline;
            """;

        long missingFromCurrent;
        long missingFromBaseline;
        await using (var command = Command(connection, sql))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new SqlFlowException("The anti-join returned no row.");
            }

            missingFromCurrent = reader.GetInt64(0);
            missingFromBaseline = reader.GetInt64(1);
        }

        var samples = new List<BaselineKeySample>();
        if (request.SampleRows > 0 && (missingFromCurrent > 0 || missingFromBaseline > 0))
        {
            samples.AddRange(await ReadSamplesAsync(connection, plan, request.SampleRows, ct).ConfigureAwait(false));
        }

        return new BaselineAntiJoin
        {
            BaselineKeysMissingFromCurrent = missingFromCurrent,
            CurrentKeysMissingFromBaseline = missingFromBaseline,
            Samples = samples,
        };
    }

    private static async Task<IReadOnlyList<BaselineKeySample>> ReadSamplesAsync(
        SqlConnection connection, ComparisonPlan plan, int sampleRows, CancellationToken ct)
    {
        var sql = $"""
            SELECT TOP (@sample) 'missingFromCurrent' AS direction, {plan.AliasList}
            FROM {OldStage} AS o
            WHERE NOT EXISTS (SELECT 1 FROM {NewStage} AS n WHERE {plan.KeyJoin})
            UNION ALL
            SELECT TOP (@sample) 'missingFromBaseline', {plan.AliasList}
            FROM {NewStage} AS n
            WHERE NOT EXISTS (SELECT 1 FROM {OldStage} AS o WHERE {plan.KeyJoin});
            """;

        await using var command = Command(connection, sql);
        command.Parameters.Add(new SqlParameter("@sample", sampleRows));

        var samples = new List<BaselineKeySample>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var key = new string?[plan.KeyAliases.Count];
            for (var i = 0; i < key.Length; i++)
            {
                var ordinal = i + 1;
                key[i] = reader.IsDBNull(ordinal)
                    ? null
                    : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
            }

            samples.Add(new BaselineKeySample(reader.GetString(0), key));
        }

        return samples;
    }

    /// <summary>
    /// Collapses each side to one representative row per logical key before the parity join. Without this the
    /// join is many-to-many across duplicates and reports more compared pairs than either side holds, which
    /// silently inflates every mismatch number below it.
    /// </summary>
    private static async Task DedupAsync(SqlConnection connection, ComparisonPlan plan, CancellationToken ct)
    {
        var sql = $"""
            SET NOCOUNT ON;
            DROP TABLE IF EXISTS {OldDedup};
            DROP TABLE IF EXISTS {NewDedup};
            SELECT * INTO {OldDedup} FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY {plan.AliasList} ORDER BY (SELECT NULL)) AS sf_rn
                FROM {OldStage}) AS x WHERE sf_rn = 1;
            SELECT * INTO {NewDedup} FROM (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY {plan.AliasList} ORDER BY (SELECT NULL)) AS sf_rn
                FROM {NewStage}) AS x WHERE sf_rn = 1;
            CREATE INDEX ix_key ON {OldDedup} ({plan.AliasList});
            CREATE INDEX ix_key ON {NewDedup} ({plan.AliasList});
            """;

        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<BaselineValueParity> ReadValueParityAsync(
        SqlConnection connection, ComparisonPlan plan, CancellationToken ct)
    {
        var oldProjection = string.Join(", ", plan.CompareColumns.Select(c => $"o.{Quote(c)}"));
        var newProjection = string.Join(", ", plan.CompareColumns.Select(c => $"n.{Quote(c)}"));

        // EXCEPT is the NULL-safe comparison: it treats two NULLs as equal, which "=" does not, so a column
        // that is legitimately NULL on both sides is not counted as a mismatch on every row.
        var totalsSql = $"""
            SELECT
                (SELECT COUNT_BIG(*) FROM {OldDedup} AS o JOIN {NewDedup} AS n ON {plan.KeyJoin}) AS shared_keys,
                (SELECT COUNT_BIG(*) FROM {OldDedup} AS o JOIN {NewDedup} AS n ON {plan.KeyJoin}
                 WHERE EXISTS (SELECT {oldProjection} EXCEPT SELECT {newProjection})) AS mismatched_keys;
            """;

        long sharedKeys;
        long mismatchedKeys;
        await using (var command = Command(connection, totalsSql))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new SqlFlowException("The value-parity pass returned no row.");
            }

            sharedKeys = reader.GetInt64(0);
            mismatchedKeys = reader.GetInt64(1);
        }

        // The per-column breakdown costs one pass per column, so it runs only when there is a mismatch to
        // explain, and only over the columns a single statement can carry.
        var columns = new List<BaselineColumnParity>();
        var analyzed = 0;
        if (mismatchedKeys > 0)
        {
            analyzed = Math.Min(plan.CompareColumns.Count, MaxPerColumnParity);
            columns.AddRange(await ReadColumnParityAsync(connection, plan, ct).ConfigureAwait(false));
        }

        return new BaselineValueParity
        {
            SharedKeysCompared = sharedKeys,
            KeysWithMismatch = mismatchedKeys,
            Columns = columns,
            ColumnsAnalyzed = analyzed,
            ColumnsCompared = plan.CompareColumns.Count,
        };
    }

    private static async Task<IReadOnlyList<BaselineColumnParity>> ReadColumnParityAsync(
        SqlConnection connection, ComparisonPlan plan, CancellationToken ct)
    {
        var considered = plan.CompareColumns.Take(MaxPerColumnParity).ToArray();
        var branches = considered.Select(column =>
        {
            var quoted = Quote(column);
            return $"""
                SELECT {Literal(column)} AS column_name,
                       COUNT_BIG(*) AS mismatches,
                       SUM(CONVERT(bigint, CASE WHEN o.{quoted} IS NULL AND n.{quoted} IS NOT NULL THEN 1 ELSE 0 END)) AS baseline_null,
                       SUM(CONVERT(bigint, CASE WHEN n.{quoted} IS NULL AND o.{quoted} IS NOT NULL THEN 1 ELSE 0 END)) AS current_null
                FROM {OldDedup} AS o JOIN {NewDedup} AS n ON {plan.KeyJoin}
                WHERE EXISTS (SELECT o.{quoted} EXCEPT SELECT n.{quoted})
                """;
        });

        var sql = string.Join($"{Environment.NewLine}UNION ALL{Environment.NewLine}", branches) + ";";

        await using var command = Command(connection, sql);
        var results = new List<BaselineColumnParity>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var mismatches = reader.GetInt64(1);
            if (mismatches == 0)
            {
                continue;
            }

            results.Add(new BaselineColumnParity
            {
                Column = reader.GetString(0),
                Mismatches = mismatches,
                BaselineNullCurrentNot = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
                CurrentNullBaselineNot = reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
            });
        }

        return results.OrderByDescending(c => c.Mismatches).ToArray();
    }

    private static string Classify(BaselineAntiJoin antiJoin, BaselineValueParity? parity)
    {
        var rowsMissing = antiJoin.BaselineKeysMissingFromCurrent > 0 || antiJoin.CurrentKeysMissingFromBaseline > 0;
        var valuesDiverged = parity is { KeysWithMismatch: > 0 };
        return (rowsMissing, valuesDiverged) switch
        {
            (false, false) => "identical",
            (true, false) => "rowsMissing",
            (false, true) => "valuesDiverged",
            _ => "both",
        };
    }

    /// <summary>
    /// Turns the three sections into the sentences an operator acts on. Every one of these is a distinction
    /// that decides what to DO, which is why the report states them rather than leaving a reader to infer them
    /// from the numbers.
    /// </summary>
    private static IReadOnlyList<string> Interpret(
        BaselineCountDecomposition counts, BaselineAntiJoin antiJoin, BaselineValueParity? parity,
        ComparisonPlan plan)
    {
        var findings = new List<string>();

        if (counts.PhysicalDelta != counts.LogicalDelta)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"The physical row difference ({counts.PhysicalDelta:N0}) is not the logical difference " +
                $"({counts.LogicalDelta:N0}): duplicates account for the rest (baseline " +
                $"{counts.BaselineDuplicates:N0}, current {counts.CurrentDuplicates:N0}). Duplicates on one " +
                $"side are a defect on that side, never a reason to copy rows across."));
        }

        if (counts.BaselineDuplicates > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"The BASELINE holds {counts.BaselineDuplicates:N0} duplicate rows on the logical key. A " +
                $"shortfall of that size in the current estate is old production's defect, not lost data."));
        }

        if (counts.CurrentDuplicates > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"The CURRENT estate holds {counts.CurrentDuplicates:N0} duplicate rows on the logical key. " +
                $"Check the flow's merge key before anything else: a provenance column in the key multiplies " +
                $"rows by the number of files loaded."));
        }

        if (antiJoin.BaselineKeysMissingFromCurrent > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{antiJoin.BaselineKeysMissingFromCurrent:N0} baseline readings are absent from the current " +
                $"estate. Classify them by cause (an era boundary, a filter that drops rows, a watermark that " +
                $"skipped them) before transferring anything."));
        }

        if (antiJoin.CurrentKeysMissingFromBaseline > 0)
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{antiJoin.CurrentKeysMissingFromBaseline:N0} current readings are absent from the baseline. " +
                $"Usually rows the legacy load never took; prove they are real before keeping them."));
        }

        if (parity is { KeysWithMismatch: > 0 })
        {
            findings.Add(string.Create(CultureInfo.InvariantCulture,
                $"{parity.KeysWithMismatch:N0} of {parity.SharedKeysCompared:N0} shared keys carry different " +
                $"VALUES. This is the most serious finding: the same reading holds different data on the two " +
                $"estates, so a transform diverged. Transferring rows will not fix it."));

            if (parity.BreakdownTruncated)
            {
                // Never let a capped breakdown read as the complete explanation: a column past the cap could
                // hold mismatches that no line below mentions.
                findings.Add(string.Create(CultureInfo.InvariantCulture,
                    $"The per-column breakdown covers only the first {parity.ColumnsAnalyzed} of " +
                    $"{parity.ColumnsCompared} compared columns, so it does NOT explain the whole mismatch " +
                    $"count above. Narrow the comparison with compareColumns to see the rest."));
            }

            foreach (var column in parity.Columns.Where(c => c.IsPureNullDrift))
            {
                findings.Add(
                    $"Every mismatch on '{column.Column}' is the current estate holding NULL where the " +
                    "baseline held a value. That is the empty-string-versus-NULL landing difference, not lost " +
                    "data: V3 lands an empty cell as NULL where the legacy reader landed an empty string.");
            }
        }
        else if (parity is not null)
        {
            findings.Add(
                $"Every shared key agrees on all {plan.CompareColumns.Count} compared columns.");
        }

        if (findings.Count == 0)
        {
            findings.Add("The two estates hold exactly the same logical keys, with no duplicates on either side.");
        }

        return findings;
    }

    // ----------------------------------------------------------------------------------------------------
    // Shared helpers.
    // ----------------------------------------------------------------------------------------------------

    /// <summary>Wraps a remote statement in OPENQUERY so it executes on the linked server and only its result
    /// crosses. The linked-server name is allowlisted by configuration and validated as an identifier before
    /// it reaches here; the statement is emitted as a SQL literal with its quotes doubled.</summary>
    private static string OpenQuery(string linkedServer, string remoteSql)
        => $"OPENQUERY({Quote(linkedServer)}, '{remoteSql.Replace("'", "''", StringComparison.Ordinal)}')";

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
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

    /// <summary>A single-quoted SQL literal with its quotes doubled. Used for the identifiers that must travel
    /// INSIDE an OPENQUERY statement, where they are text rather than syntax.</summary>
    private static string Literal(string value)
        => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
}
