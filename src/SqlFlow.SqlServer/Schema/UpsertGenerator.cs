using SqlFlow.Core;
using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Schema;

/// <summary>Which branch of the two-step upsert a statement is, so a caller can attribute its affected-row
/// count to updates vs inserts without relying on statement order.</summary>
public enum UpsertStatementKind
{
    Update,
    Insert,

    /// <summary>A set-based DELETE that purges the target rows belonging to the datasets present in the staged
    /// batch (the per-file replace, <see cref="UpsertOptions.ReloadColumn"/>). Its affected-row count is a
    /// deletion, not an insert or update, so the caller attributes it to rows removed.</summary>
    Purge,

    /// <summary>A single script that performs both the update and the insert (the dataset-partitioned loop):
    /// it reports its affected-row totals as one row with <c>Inserts</c> and <c>Updates</c> columns rather than
    /// via a rows-affected count, so the caller reads both counts from the result set.</summary>
    Combined,
}

/// <summary>One tagged statement of the two-step upsert.</summary>
public sealed record UpsertStatement
{
    public required UpsertStatementKind Kind { get; init; }

    public required string Sql { get; init; }

    /// <summary>True when the statement is a multi-batch script that reports its affected-row total as a
    /// single scalar result set (the batched lock-escalation-avoiding apply) rather than via the
    /// rows-affected count.</summary>
    public bool CountFromScalar { get; init; }

    /// <summary>True when the statement reports both affected-row totals as one result-set row with
    /// <c>Inserts</c> and <c>Updates</c> columns (the dataset-partitioned loop, which does both branches).</summary>
    public bool CountFromResultSet { get; init; }
}

/// <summary>The type one checksummed column carries in staging and the type the target stores it under. They
/// differ whenever the target was not created from this staging shape (a pre-created table loaded with schema
/// sync off), and change detection has to account for that to compare stored values.</summary>
public sealed record ChecksumColumnType
{
    /// <summary>The column's type in the staging table, which mirrors the source.</summary>
    public required SqlDataType Staging { get; init; }

    /// <summary>The column's type in the target table, which is what the load actually stores.</summary>
    public required SqlDataType Target { get; init; }
}

/// <summary>Settings for a staging-to-target upsert.</summary>
public sealed record UpsertOptions
{
    /// <summary>The data columns copied from staging to target (excludes the target identity column).</summary>
    public required IReadOnlyList<string> DataColumns { get; init; }

    /// <summary>The business keys that match a staging row to a target row.</summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Omit the UPDATE branch (insert-only).</summary>
    public bool SkipUpdate { get; init; }

    /// <summary>Omit the INSERT branch (update-only).</summary>
    public bool SkipInsert { get; init; }

    /// <summary>HASHBYTES algorithm for the change-detection checksum.</summary>
    public string HashAlgorithm { get; init; } = HashKey.DefaultAlgorithm;

    /// <summary>System column stamped on inserted rows (for example InsertedDate_DW); null to skip. When the
    /// UPDATE branch runs it also NULL-backfills this column (legacy parity), so a row that predates the
    /// column still gets a date the first time it is touched.</summary>
    public string? InsertedDateColumn { get; init; }

    /// <summary>System column stamped whenever a row is written (for example UpdatedDate_DW): set on UPDATE, and
    /// on INSERT too, so it always reads as "when did this row last change" rather than going NULL until the row
    /// happens to be updated. Null to skip.</summary>
    public string? UpdatedDateColumn { get; init; }

    /// <summary>Row-status system column (for example RowStatus_DW): stamped 'I' on insert and 'U' on update;
    /// null to skip.</summary>
    public string? RowStatusColumn { get; init; }

    /// <summary>Columns excluded from the change-detection checksum (the flow's IgnoreColumnsInHash plus any
    /// column whose data type cannot participate in CONCAT, per the legacy invalid-checksum-type rule). They
    /// are still copied by the SET list. When every comparable column is excluded, the UPDATE drops its change
    /// predicate and rewrites all matched rows (legacy semantics: better to over-update than never update).</summary>
    public IReadOnlySet<string> ExcludeFromChecksum { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How each checksummed column is stored on the two sides, keyed by column name. It makes change
    /// detection compare the value the target STORES rather than two renderings of it: the staging value is
    /// converted to the target's type when the two differ, and a type whose default string form is lossy is
    /// rendered with an explicit lossless style. A column absent from the map is hashed as-is (the caller omits
    /// the types it cannot reconcile, and a target SqlFlow is about to create has nothing to reconcile
    /// against).</summary>
    public IReadOnlyDictionary<string, ChecksumColumnType> ChecksumColumnTypes { get; init; } =
        new Dictionary<string, ChecksumColumnType>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Apply through key-windowed batches to keep each DML under the lock-escalation threshold
    /// (the legacy #UpdateKeys / #InsertKeys pattern). Each batch commits on its own, trading the single-
    /// transaction atomicity of the default apply for lock friendliness on large deltas.</summary>
    public bool BatchToAvoidLockEscalation { get; init; }

    /// <summary>Rows per batch window when <see cref="BatchToAvoidLockEscalation"/> is on.</summary>
    public int BatchRowCount { get; init; } = 2000;

    /// <summary>When set, the upsert is applied one dataset at a time, partitioned by this column's distinct
    /// values in ascending order, each dataset's UPDATE-then-INSERT running against the target as the prior
    /// datasets left it (the legacy DataSetColumn loop). Staging is first deduplicated to one row per
    /// (dataset, key). Preserves file/partition load order in a shared staging area: a key recurring across
    /// datasets collapses to the last dataset carrying it. Honors <see cref="BatchToAvoidLockEscalation"/>
    /// within each dataset. Not combinable with <see cref="Scd2Enabled"/>. Must be one of
    /// <see cref="DataColumns"/>. Null applies the plain set-based upsert.</summary>
    public string? DataSetColumn { get; init; }

    /// <summary>When set, the apply is a per-file (per-dataset) full replace keyed on this column instead of a
    /// keyed upsert: a set-based <c>DELETE</c> purges every target row whose <see cref="ReloadColumn"/> value is
    /// present in staging (NULL-safe), then the staged rows are inserted. With <see cref="KeyColumns"/> the insert
    /// collapses staging to one row per key so the target's unique key holds; without keys every staged row is
    /// inserted. Must be one of <see cref="DataColumns"/>. Not combinable with <see cref="DataSetColumn"/> or
    /// <see cref="Scd2Enabled"/>. Null applies the normal upsert.</summary>
    public string? ReloadColumn { get; init; }

    /// <summary>Maintain application-managed SCD Type 2 history instead of a plain upsert: close the changed
    /// current rows and insert new current versions (the dimension-history load). When set, the SCD2 columns
    /// below are required and <see cref="SkipUpdate"/>/<see cref="SkipInsert"/> do not apply.</summary>
    public bool Scd2Enabled { get; init; }

    public string? Scd2ValidFromColumn { get; init; }

    public string? Scd2ValidToColumn { get; init; }

    public string? Scd2CurrentFlagColumn { get; init; }

    /// <summary>The attributes whose change opens a new version (target names); empty means every comparable
    /// non-key column.</summary>
    public IReadOnlyList<string> Scd2TrackedColumns { get; init; } = [];

    /// <summary>The single load instant, captured once by the caller and inlined so the closed row's ValidTo
    /// equals the new row's ValidFrom exactly (contiguous periods). A datetime2 literal, e.g. 2026-06-16 11:22:33.444.</summary>
    public string? Scd2AsOfLiteral { get; init; }
}

/// <summary>
/// Generates the two-step upsert from a staging table into a target by key: an UPDATE of matched rows whose
/// non-key data changed (detected by a HASHBYTES checksum, so an unchanged row is not rewritten), then an
/// INSERT of rows that do not yet exist. It deliberately does NOT use a T-SQL MERGE (which the legacy engine
/// avoids and which has documented concurrency and trigger hazards). System date and row-status columns are
/// stamped on the branch that touches a row. The optional batched form applies the same logic through
/// ROW_NUMBER key windows (the legacy lock-escalation-avoiding pattern, with its checksum and anti-join bugs
/// fixed). Pure text generation.
/// </summary>
public static class UpsertGenerator
{
    public static IReadOnlyList<string> Generate(RelationalObject target, RelationalObject staging, UpsertOptions options)
        => GenerateStatements(target, staging, options).Select(s => s.Sql).ToList();

    public static IReadOnlyList<UpsertStatement> GenerateStatements(RelationalObject target, RelationalObject staging, UpsertOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(staging);
        ArgumentNullException.ThrowIfNull(options);

        // Per-file replace (purge-then-insert) is its own apply shape: it needs no business key (the file is the
        // unit of replacement), so it is resolved before the keyed-upsert key requirement below.
        if (!string.IsNullOrEmpty(options.ReloadColumn))
        {
            if (options.Scd2Enabled)
            {
                throw new SqlFlowException("A per-file replace (ReloadColumn) cannot be combined with SCD2 versioning.");
            }

            if (!string.IsNullOrEmpty(options.DataSetColumn))
            {
                throw new SqlFlowException("A per-file replace (ReloadColumn) cannot be combined with the dataset-upsert loop (DataSetColumn).");
            }

            if (!options.DataColumns.Contains(options.ReloadColumn, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"ReloadColumn '{options.ReloadColumn}' is not among the data columns.");
            }

            // Validate the hash algorithm for parity with the other branches (it is unused here, but a bad value
            // should still fail the same way rather than silently pass).
            _ = HashKey.BinaryTypeFor(options.HashAlgorithm);
            return GenerateReloadStatements(Qualify(target), Qualify(staging), options);
        }

        if (options.KeyColumns.Count == 0)
        {
            throw new SqlFlowException("An upsert requires at least one key column.");
        }

        var dataSet = new HashSet<string>(options.DataColumns, StringComparer.OrdinalIgnoreCase);
        foreach (var key in options.KeyColumns)
        {
            if (!dataSet.Contains(key))
            {
                throw new SqlFlowException($"Key column '{key}' is not among the data columns.");
            }
        }

        if (options.BatchToAvoidLockEscalation && options.BatchRowCount < 1)
        {
            throw new SqlFlowException($"BatchRowCount must be at least 1, got {options.BatchRowCount}.");
        }

        if (!string.IsNullOrEmpty(options.DataSetColumn))
        {
            if (options.Scd2Enabled)
            {
                throw new SqlFlowException("A dataset-column load (DataSetColumn) cannot be combined with SCD2 versioning.");
            }

            if (!options.DataColumns.Contains(options.DataSetColumn, StringComparer.OrdinalIgnoreCase))
            {
                throw new SqlFlowException($"DataSetColumn '{options.DataSetColumn}' is not among the data columns.");
            }
        }

        // Validate the hash algorithm against the known set (throws on an unknown value), so the name is safe
        // to interpolate into the HASHBYTES call.
        _ = HashKey.BinaryTypeFor(options.HashAlgorithm);

        var trg = Qualify(target);
        var stg = Qualify(staging);
        var keyEquality = KeyEquality(options.KeyColumns, "src", "trg");
        var nonKey = options.DataColumns
            .Where(c => !options.KeyColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var checksumColumns = nonKey
            .Where(c => !options.ExcludeFromChecksum.Contains(c))
            .ToList();

        if (options.Scd2Enabled)
        {
            return GenerateScd2Statements(trg, stg, options, keyEquality, nonKey, checksumColumns);
        }

        if (!string.IsNullOrEmpty(options.DataSetColumn))
        {
            return [GenerateDataSetLoopStatement(trg, stg, options, keyEquality, nonKey, checksumColumns)];
        }

        var statements = new List<UpsertStatement>();

        // UPDATE only when there is something non-key to update.
        if (!options.SkipUpdate && nonKey.Count > 0)
        {
            var setList = string.Join(", ", UpdateSetClauses(nonKey, options));

            // No comparable column left to detect change on: rewrite all matched rows (legacy semantics).
            var changePredicate = checksumColumns.Count > 0
                ? $"{Checksum("src", checksumColumns, options, staging: true)} <> {Checksum("trg", checksumColumns, options, staging: false)}"
                : null;

            statements.Add(options.BatchToAvoidLockEscalation
                ? new UpsertStatement
                {
                    Kind = UpsertStatementKind.Update,
                    Sql = BatchedUpdateScript(trg, stg, options, keyEquality, setList, changePredicate),
                    CountFromScalar = true,
                }
                : new UpsertStatement
                {
                    Kind = UpsertStatementKind.Update,
                    // Collapsed to one row per key for the same reason the INSERT is: staging legitimately
                    // carries several rows for a key, and driving an UPDATE from all of them writes the same
                    // target row once per staging row. The value that survives is then whichever row the plan
                    // happened to apply last (undefined, and free to change between runs), and the reported
                    // update count is the number of staging matches rather than the number of rows changed.
                    Sql =
                        $"UPDATE trg SET {setList} " +
                        $"FROM {OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns)} AS src " +
                        $"INNER JOIN {trg} AS trg ON {keyEquality} " +
                        $"WHERE src._rn = 1" +
                        (changePredicate is null ? ";" : $" AND {changePredicate};"),
                });
        }

        if (!options.SkipInsert)
        {
            var insertColumns = new List<string>(options.DataColumns);
            var selectColumns = options.DataColumns.Select(c => $"src.[{Escape(c)}]").ToList();
            if (!string.IsNullOrEmpty(options.InsertedDateColumn))
            {
                insertColumns.Add(options.InsertedDateColumn);
                selectColumns.Add("SYSUTCDATETIME()");
            }

            // Legacy parity: an INSERTED row carries the merge timestamp in BOTH audit columns, which is what the
            // old engine wrote (old production's arc tables have no NULL UpdatedDate_DW anywhere). Stamping it
            // only on UPDATE looks harmless and is not: UpdatedDate_DW is read as "when did this row last change",
            // so on an insert-only table MAX(UpdatedDate_DW) never advances and a `UpdatedDate_DW >= @since`
            // predicate is UNKNOWN for precisely the rows that just landed, silently starving every downstream
            // watermark and ported procedure built on it.
            if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
            {
                insertColumns.Add(options.UpdatedDateColumn);
                selectColumns.Add("SYSUTCDATETIME()");
            }

            if (!string.IsNullOrEmpty(options.RowStatusColumn))
            {
                insertColumns.Add(options.RowStatusColumn);
                selectColumns.Add("'I'");
            }

            var insertColumnList = string.Join(", ", insertColumns.Select(c => $"[{Escape(c)}]"));
            var selectList = string.Join(", ", selectColumns);

            statements.Add(options.BatchToAvoidLockEscalation
                ? new UpsertStatement
                {
                    Kind = UpsertStatementKind.Insert,
                    Sql = BatchedInsertScript(trg, stg, options, keyEquality, insertColumnList, selectList),
                    CountFromScalar = true,
                }
                : new UpsertStatement
                {
                    Kind = UpsertStatementKind.Insert,
                    Sql =
                        $"INSERT INTO {trg} ({insertColumnList}) " +
                        $"SELECT {selectList} FROM {OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns)} AS src " +
                        $"WHERE src._rn = 1 AND NOT EXISTS (SELECT 1 FROM {trg} AS trg WHERE {keyEquality});",
                });
        }

        return statements;
    }

    /// <summary>
    /// The per-file (per-dataset) full replace (<see cref="UpsertOptions.ReloadColumn"/>): a purge that deletes
    /// every target row whose dataset key is present in the staged batch, then an insert of the batch. The purge
    /// join is a plain equality (<c>=</c>), so NULL keys never match: a target or staging row with no file
    /// identity is never purged, and other files' rows (absent from this batch) are untouched. When key columns
    /// are declared the insert collapses staging to one row per key (the same anti-duplication the keyed upsert
    /// uses), so the target's unique key is not violated by a key that recurs across the batch's files; without
    /// keys every staged row is inserted. The two statements are meant to run in one transaction (the caller
    /// forces it) so a resend is atomic - old rows gone and new rows in, or neither.
    /// </summary>
    private static IReadOnlyList<UpsertStatement> GenerateReloadStatements(string trg, string stg, UpsertOptions options)
    {
        var rc = Escape(options.ReloadColumn!);

        var statements = new List<UpsertStatement>
        {
            new()
            {
                Kind = UpsertStatementKind.Purge,
                Sql =
                    $"DELETE trg FROM {trg} AS trg " +
                    $"WHERE EXISTS (SELECT 1 FROM {stg} AS src WHERE src.[{rc}] = trg.[{rc}]);",
            },
        };

        if (!options.SkipInsert)
        {
            var insertColumns = new List<string>(options.DataColumns);
            var selectColumns = options.DataColumns.Select(c => $"src.[{Escape(c)}]").ToList();
            if (!string.IsNullOrEmpty(options.InsertedDateColumn))
            {
                insertColumns.Add(options.InsertedDateColumn);
                selectColumns.Add("SYSUTCDATETIME()");
            }

            // Legacy parity: an INSERTED row carries the merge timestamp in BOTH audit columns, which is what the
            // old engine wrote (old production's arc tables have no NULL UpdatedDate_DW anywhere). Stamping it
            // only on UPDATE looks harmless and is not: UpdatedDate_DW is read as "when did this row last change",
            // so on an insert-only table MAX(UpdatedDate_DW) never advances and a `UpdatedDate_DW >= @since`
            // predicate is UNKNOWN for precisely the rows that just landed, silently starving every downstream
            // watermark and ported procedure built on it.
            if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
            {
                insertColumns.Add(options.UpdatedDateColumn);
                selectColumns.Add("SYSUTCDATETIME()");
            }

            if (!string.IsNullOrEmpty(options.RowStatusColumn))
            {
                insertColumns.Add(options.RowStatusColumn);
                selectColumns.Add("'I'");
            }

            var insertColumnList = string.Join(", ", insertColumns.Select(c => $"[{Escape(c)}]"));
            var selectList = string.Join(", ", selectColumns);

            // With keys, one row per key (last-wins within the batch) so a key recurring across the batch's files
            // cannot violate the target's unique key; without keys, every staged row is inserted.
            var fromClause = options.KeyColumns.Count > 0
                ? $"{OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns)} AS src WHERE src._rn = 1"
                : $"{stg} AS src";

            statements.Add(new UpsertStatement
            {
                Kind = UpsertStatementKind.Insert,
                Sql = $"INSERT INTO {trg} ({insertColumnList}) SELECT {selectList} FROM {fromClause};",
            });
        }

        return statements;
    }

    // The open-ended end of the current version: the max datetime2(3) value, so every point-in-time query is
    // a uniform half-open [ValidFrom, ValidTo) range with no NULL special case.
    private const string Scd2OpenSentinel = "9999-12-31 23:59:59.999";

    // The effective-from stamped on rows that predate SCD2 (no real history is known for them).
    private const string Scd2BackfillEpoch = "1900-01-01 00:00:00.000";

    /// <summary>
    /// The SCD Type 2 load (three ordered statements, all using the same inlined as-of instant):
    /// (1) backfill any row missing a period (a row that predates enabling SCD2) as the current version, so
    /// turning SCD2 on for an existing populated table is correct; (2) close the current rows whose tracked
    /// attributes changed (stamp ValidTo, clear the current flag); (3) insert a fresh current version for every
    /// changed-or-new key (one row per key). Unchanged keys keep their existing current row untouched.
    /// </summary>
    private static IReadOnlyList<UpsertStatement> GenerateScd2Statements(
        string trg, string stg, UpsertOptions options, string keyEquality,
        IReadOnlyList<string> nonKey, IReadOnlyList<string> checksumColumns)
    {
        var asOf = options.Scd2AsOfLiteral
                   ?? throw new SqlFlowException("SCD2 requires an as-of instant (Scd2AsOfLiteral).");
        var vf = Escape(options.Scd2ValidFromColumn ?? throw new SqlFlowException("SCD2 requires a ValidFrom column."));
        var vt = Escape(options.Scd2ValidToColumn ?? throw new SqlFlowException("SCD2 requires a ValidTo column."));
        var cf = Escape(options.Scd2CurrentFlagColumn ?? throw new SqlFlowException("SCD2 requires a current-flag column."));

        // Tracked attributes: the requested set (restricted to comparable non-key columns) or, by default,
        // every comparable non-key column. If nothing is comparable, no change can be detected, so no row is
        // ever versioned: only brand-new keys are inserted.
        var tracked = (options.Scd2TrackedColumns.Count > 0
                ? nonKey.Where(c => options.Scd2TrackedColumns.Contains(c, StringComparer.OrdinalIgnoreCase))
                : checksumColumns)
            .Where(c => !options.ExcludeFromChecksum.Contains(c))
            .ToList();

        var statements = new List<UpsertStatement>();

        // (1) Backfill pre-existing rows (period columns just added, still NULL) as the current version.
        var backfillFrom = string.IsNullOrEmpty(options.InsertedDateColumn)
            ? $"COALESCE(trg.[{vf}], '{Scd2BackfillEpoch}')"
            : $"COALESCE(trg.[{vf}], trg.[{Escape(options.InsertedDateColumn)}], '{Scd2BackfillEpoch}')";
        statements.Add(new UpsertStatement
        {
            Kind = UpsertStatementKind.Update,
            Sql =
                $"UPDATE trg SET trg.[{vf}] = {backfillFrom}, trg.[{vt}] = '{Scd2OpenSentinel}', trg.[{cf}] = 1 " +
                $"FROM {trg} AS trg WHERE trg.[{vt}] IS NULL;",
        });

        // (2) Close the changed current rows. Skipped when there is no comparable attribute to compare.
        if (tracked.Count > 0)
        {
            var closeSets = $"trg.[{vt}] = '{asOf}', trg.[{cf}] = 0";
            if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
            {
                closeSets += $", trg.[{Escape(options.UpdatedDateColumn)}] = '{asOf}'";
            }

            // Closing a version is an update to that row; stamp the row-status the same way the plain UPDATE
            // branch does, so an expired version is auditable as touched-this-run.
            if (!string.IsNullOrEmpty(options.RowStatusColumn))
            {
                closeSets += $", trg.[{Escape(options.RowStatusColumn)}] = 'U'";
            }

            statements.Add(new UpsertStatement
            {
                Kind = UpsertStatementKind.Update,
                Sql =
                    $"UPDATE trg SET {closeSets} FROM {stg} AS src INNER JOIN {trg} AS trg ON {keyEquality} " +
                    $"WHERE trg.[{cf}] = 1 AND " +
                    $"{Checksum("src", tracked, options, staging: true)} <> {Checksum("trg", tracked, options, staging: false)};",
            });
        }

        // (3) Insert a new current version for every key that has no current row (the just-closed changed keys
        // and the brand-new keys). One row per key, so a key appearing several times in staging versions once.
        var insertColumns = new List<string>(options.DataColumns) { options.Scd2ValidFromColumn!, options.Scd2ValidToColumn!, options.Scd2CurrentFlagColumn! };
        var selectColumns = options.DataColumns.Select(c => $"src.[{Escape(c)}]").ToList();
        selectColumns.Add($"'{asOf}'");
        selectColumns.Add($"'{Scd2OpenSentinel}'");
        selectColumns.Add("1");
        if (!string.IsNullOrEmpty(options.InsertedDateColumn))
        {
            insertColumns.Add(options.InsertedDateColumn);
            selectColumns.Add($"'{asOf}'");
        }

        // Same legacy parity as the plain insert: a newly opened version is stamped in both audit columns, using
        // this batch's as-of instant so every row of one SCD2 apply shares it.
        if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
        {
            insertColumns.Add(options.UpdatedDateColumn);
            selectColumns.Add($"'{asOf}'");
        }

        if (!string.IsNullOrEmpty(options.RowStatusColumn))
        {
            insertColumns.Add(options.RowStatusColumn);
            selectColumns.Add("'I'");
        }

        var insertColumnList = string.Join(", ", insertColumns.Select(c => $"[{Escape(c)}]"));
        var selectList = string.Join(", ", selectColumns);

        statements.Add(new UpsertStatement
        {
            Kind = UpsertStatementKind.Insert,
            Sql =
                $"INSERT INTO {trg} ({insertColumnList}) " +
                $"SELECT {selectList} FROM {OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns)} AS src " +
                $"WHERE src._rn = 1 AND NOT EXISTS (" +
                $"SELECT 1 FROM {trg} AS trg WHERE {keyEquality} AND trg.[{cf}] = 1);",
        });

        return statements;
    }

    /// <summary>
    /// The dataset-partitioned load (legacy DataSetColumn loop), emitted as one script that reports its totals
    /// as a single <c>Inserts</c>/<c>Updates</c> row. Staging is first collapsed to one row per (dataset, key)
    /// (<c>#dsstg</c>). The distinct dataset values are numbered in ascending order (<c>#DataSets</c>) and walked
    /// one at a time: each iteration updates the changed matched rows of that dataset, then inserts its new rows
    /// with a live anti-join against the target. Because the iterations run in order and each sees the target as
    /// the prior datasets left it, a key that recurs across datasets ends at the value from the last dataset that
    /// carries it (the ordered file/partition semantics a set-based upsert cannot express). With
    /// <see cref="UpsertOptions.BatchToAvoidLockEscalation"/> on, each dataset's update and insert are windowed by
    /// key in <see cref="UpsertOptions.BatchRowCount"/> batches.
    /// </summary>
    private static UpsertStatement GenerateDataSetLoopStatement(
        string trg, string stg, UpsertOptions options, string keyEquality,
        IReadOnlyList<string> nonKey, IReadOnlyList<string> checksumColumns)
    {
        var dsCol = Escape(options.DataSetColumn!);
        var dataColList = string.Join(", ", options.DataColumns.Select(c => $"[{Escape(c)}]"));

        // Dedup partition: the dataset column plus the keys (the dataset column is not repeated if it is itself a key).
        var partitionCols = new List<string> { $"[{dsCol}]" };
        partitionCols.AddRange(options.KeyColumns
            .Where(k => !string.Equals(k, options.DataSetColumn, StringComparison.OrdinalIgnoreCase))
            .Select(k => $"[{Escape(k)}]"));
        var partitionList = string.Join(", ", partitionCols);

        var dsMatch = $"(src.[{dsCol}] = ds._ds OR (src.[{dsCol}] IS NULL AND ds._ds IS NULL))";
        var dsCurrent = $"INNER JOIN #DataSets AS ds ON {dsMatch} AND ds.RN = @Counter";

        var doUpdate = !options.SkipUpdate && nonKey.Count > 0;
        var doInsert = !options.SkipInsert;

        var changePredicate = checksumColumns.Count > 0
            ? $"{Checksum("src", checksumColumns, options, staging: true)} <> {Checksum("trg", checksumColumns, options, staging: false)}"
            : null;

        var setList = doUpdate ? string.Join(", ", UpdateSetClauses(nonKey, options)) : string.Empty;

        // Insert column/select lists (data columns plus the stamped system columns), identical to the plain path.
        var insertColumns = new List<string>(options.DataColumns);
        var selectColumns = options.DataColumns.Select(c => $"src.[{Escape(c)}]").ToList();
        if (!string.IsNullOrEmpty(options.InsertedDateColumn))
        {
            insertColumns.Add(options.InsertedDateColumn);
            selectColumns.Add("SYSUTCDATETIME()");
        }

        // Same legacy parity as the plain insert: an inserted row carries the merge timestamp in BOTH audit
        // columns. This path is the one nearly every ported flow takes, because DataSetColumn is the legacy
        // standard, so leaving it out here left UpdatedDate_DW NULL on exactly the loads that are supposed to
        // reproduce old production row for row.
        if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
        {
            insertColumns.Add(options.UpdatedDateColumn);
            selectColumns.Add("SYSUTCDATETIME()");
        }

        if (!string.IsNullOrEmpty(options.RowStatusColumn))
        {
            insertColumns.Add(options.RowStatusColumn);
            selectColumns.Add("'I'");
        }

        var insertColumnList = string.Join(", ", insertColumns.Select(c => $"[{Escape(c)}]"));
        var selectList = string.Join(", ", selectColumns);

        var srcKeySelect = string.Join(", ", options.KeyColumns.Select(k => $"src.[{Escape(k)}]"));

        // #dsstg is already one row per (dataset, key) and each window key table is filtered to a single
        // dataset, so the keys it holds are distinct without a DISTINCT. The join back to #dsstg is NULL-safe
        // for the same reason the plain batched path's is: a nullable key reaches the key table and must find
        // its staging row again.
        var keyEqualitySrcK = NullSafeKeyEquality(options.KeyColumns, "src", "k");

        string updateBlock;
        string insertBlock;
        if (options.BatchToAvoidLockEscalation)
        {
            updateBlock = doUpdate
                ? $"""
                    IF OBJECT_ID('tempdb..#curUpd') IS NOT NULL DROP TABLE #curUpd;
                    SELECT {srcKeySelect}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
                    INTO #curUpd
                    FROM #dsstg AS src
                    {dsCurrent}
                    INNER JOIN {trg} AS trg ON {keyEquality};
                    CREATE UNIQUE CLUSTERED INDEX [IX_curUpd] ON #curUpd (RowNum);
                    SET @u = 0; SET @s = 1; SET @e = @Batch; SET @t = (SELECT COUNT(*) FROM #curUpd);
                    WHILE @s <= @t
                    BEGIN
                        UPDATE trg SET {setList}
                        FROM #dsstg AS src
                        {dsCurrent}
                        INNER JOIN #curUpd AS k ON {keyEqualitySrcK}
                        INNER JOIN {trg} AS trg ON {keyEquality}
                        WHERE k.RowNum BETWEEN @s AND @e{(changePredicate is null ? string.Empty : $" AND {changePredicate}")};
                        SET @u = @u + @@ROWCOUNT;
                        SET @s = @e + 1; SET @e = @e + @Batch;
                    END;
                    INSERT INTO #InsertUpdates (Inserts, Updates) SELECT 0, @u;
                    DROP TABLE #curUpd;
                    """
                : string.Empty;

            insertBlock = doInsert
                ? $"""
                    IF OBJECT_ID('tempdb..#curIns') IS NOT NULL DROP TABLE #curIns;
                    SELECT {srcKeySelect}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
                    INTO #curIns
                    FROM #dsstg AS src
                    {dsCurrent}
                    WHERE NOT EXISTS (SELECT 1 FROM {trg} AS trg WHERE {keyEquality});
                    CREATE UNIQUE CLUSTERED INDEX [IX_curIns] ON #curIns (RowNum);
                    SET @i = 0; SET @s = 1; SET @e = @Batch; SET @t = (SELECT COUNT(*) FROM #curIns);
                    WHILE @s <= @t
                    BEGIN
                        INSERT INTO {trg} ({insertColumnList})
                        SELECT {selectList}
                        FROM #dsstg AS src
                        {dsCurrent}
                        INNER JOIN #curIns AS k ON {keyEqualitySrcK}
                        WHERE k.RowNum BETWEEN @s AND @e;
                        SET @i = @i + @@ROWCOUNT;
                        SET @s = @e + 1; SET @e = @e + @Batch;
                    END;
                    INSERT INTO #InsertUpdates (Inserts, Updates) SELECT @i, 0;
                    DROP TABLE #curIns;
                    """
                : string.Empty;
        }
        else
        {
            updateBlock = doUpdate
                ? $"""
                    UPDATE trg SET {setList}
                    FROM #dsstg AS src
                    {dsCurrent}
                    INNER JOIN {trg} AS trg ON {keyEquality}
                    {(changePredicate is null ? string.Empty : $"WHERE {changePredicate}")};
                    INSERT INTO #InsertUpdates (Inserts, Updates) SELECT 0, @@ROWCOUNT;
                    """
                : string.Empty;

            insertBlock = doInsert
                ? $"""
                    INSERT INTO {trg} ({insertColumnList})
                    SELECT {selectList}
                    FROM #dsstg AS src
                    {dsCurrent}
                    WHERE NOT EXISTS (SELECT 1 FROM {trg} AS trg WHERE {keyEquality});
                    INSERT INTO #InsertUpdates (Inserts, Updates) SELECT @@ROWCOUNT, 0;
                    """
                : string.Empty;
        }

        var declares = options.BatchToAvoidLockEscalation
            ? "DECLARE @Counter INT, @dsCount INT, @Batch INT, @s INT, @e INT, @t INT, @u BIGINT, @i BIGINT;"
            : "DECLARE @Counter INT, @dsCount INT;";
        var setBatch = options.BatchToAvoidLockEscalation ? $"SET @Batch = {options.BatchRowCount};" : string.Empty;

        var sql =
            $"""
            SET NOCOUNT ON;

            IF OBJECT_ID('tempdb..#dsstg') IS NOT NULL DROP TABLE #dsstg;
            SELECT {dataColList}
            INTO #dsstg
            FROM (
                SELECT {dataColList}, ROW_NUMBER() OVER (PARTITION BY {partitionList} ORDER BY (SELECT NULL)) AS _dsrn
                FROM {stg}
            ) AS d
            WHERE d._dsrn = 1;

            IF OBJECT_ID('tempdb..#DataSets') IS NOT NULL DROP TABLE #DataSets;
            SELECT _ds, ROW_NUMBER() OVER (ORDER BY _ds) AS RN
            INTO #DataSets
            FROM (SELECT DISTINCT [{dsCol}] AS _ds FROM #dsstg) AS b;

            IF OBJECT_ID('tempdb..#InsertUpdates') IS NOT NULL DROP TABLE #InsertUpdates;
            CREATE TABLE #InsertUpdates (Inserts BIGINT NULL, Updates BIGINT NULL);

            {declares}
            SET @Counter = 1; SET @dsCount = (SELECT COUNT(*) FROM #DataSets);
            {setBatch}
            WHILE @Counter <= @dsCount
            BEGIN
                {updateBlock}
                {insertBlock}
                SET @Counter = @Counter + 1;
            END;

            DROP TABLE #dsstg;
            DROP TABLE #DataSets;
            SELECT ISNULL(SUM(Inserts), 0) AS Inserts, ISNULL(SUM(Updates), 0) AS Updates FROM #InsertUpdates;
            """;

        return new UpsertStatement
        {
            Kind = UpsertStatementKind.Combined,
            Sql = sql,
            CountFromResultSet = true,
        };
    }

    private static IEnumerable<string> UpdateSetClauses(IReadOnlyList<string> nonKey, UpsertOptions options)
    {
        foreach (var column in nonKey)
        {
            yield return $"trg.[{Escape(column)}] = src.[{Escape(column)}]";
        }

        if (!string.IsNullOrEmpty(options.UpdatedDateColumn))
        {
            yield return $"trg.[{Escape(options.UpdatedDateColumn)}] = SYSUTCDATETIME()";
        }

        if (!string.IsNullOrEmpty(options.InsertedDateColumn))
        {
            // Legacy parity: a matched row whose InsertedDate is NULL (it predates the column) gets stamped
            // the first time it is touched; an existing value is preserved.
            var inserted = Escape(options.InsertedDateColumn);
            yield return $"trg.[{inserted}] = CASE WHEN trg.[{inserted}] IS NULL THEN SYSUTCDATETIME() ELSE trg.[{inserted}] END";
        }

        if (!string.IsNullOrEmpty(options.RowStatusColumn))
        {
            yield return $"trg.[{Escape(options.RowStatusColumn)}] = 'U'";
        }
    }

    // The batched apply (legacy #UpdateKeys pattern, corrected): snapshot the matched keys with a ROW_NUMBER,
    // index the windows, then update window by window so each DML stays under the lock-escalation threshold.
    // The change predicate (when present) is evaluated inside each window exactly as in the unbatched form.
    //
    // The key table holds DISTINCT keys, and the windowed DML reads the per-key-deduped staging source, so a
    // key that staging carries more than once produces exactly one row in the key table and one write. The
    // numbering has to happen OUTSIDE that DISTINCT: ROW_NUMBER() is computed before DISTINCT is applied and
    // is unique on every row, so `SELECT DISTINCT <keys>, ROW_NUMBER() OVER (...)` de-duplicates nothing at
    // all, and the window join then multiplies the deduped staging row back out once per key-table copy.
    private static string BatchedUpdateScript(
        string trg, string stg, UpsertOptions options, string keyEquality, string setList, string? changePredicate)
    {
        var keySelect = string.Join(", ", options.KeyColumns.Select(k => $"src.[{Escape(k)}]"));
        var keySelectK = string.Join(", ", options.KeyColumns.Select(k => $"k.[{Escape(k)}]"));
        var keyList = string.Join(", ", options.KeyColumns.Select(k => $"[{Escape(k)}]"));
        var windowJoin = NullSafeKeyEquality(options.KeyColumns, "src", "k");
        var dedupedSource = OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns);

        return $"""
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#UpsertKeysU') IS NOT NULL DROP TABLE #UpsertKeysU;
            SELECT {keySelectK}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
            INTO #UpsertKeysU
            FROM (SELECT DISTINCT {keySelect} FROM {stg} AS src INNER JOIN {trg} AS trg ON {keyEquality}) AS k;
            CREATE UNIQUE CLUSTERED INDEX [IX_UpsertKeysU_RowNum] ON #UpsertKeysU (RowNum);
            CREATE STATISTICS [ST_UpsertKeysU] ON #UpsertKeysU ({keyList});
            DECLARE @Affected bigint = 0, @Start bigint = 1, @End bigint = {options.BatchRowCount}, @Total bigint;
            SELECT @Total = COUNT_BIG(*) FROM #UpsertKeysU;
            WHILE @Start <= @Total
            BEGIN
                UPDATE trg SET {setList}
                FROM {dedupedSource} AS src
                INNER JOIN #UpsertKeysU AS k ON {windowJoin}
                INNER JOIN {trg} AS trg ON {keyEquality}
                WHERE src._rn = 1 AND k.RowNum BETWEEN @Start AND @End{(changePredicate is null ? string.Empty : $" AND {changePredicate}")};
                SET @Affected = @Affected + @@ROWCOUNT;
                SET @Start = @End + 1;
                SET @End = @End + {options.BatchRowCount};
            END;
            DROP TABLE #UpsertKeysU;
            SELECT @Affected;
            """;
    }

    private static string BatchedInsertScript(
        string trg, string stg, UpsertOptions options, string keyEquality, string insertColumnList, string selectList)
    {
        var keySelect = string.Join(", ", options.KeyColumns.Select(k => $"src.[{Escape(k)}]"));
        var keySelectK = string.Join(", ", options.KeyColumns.Select(k => $"k.[{Escape(k)}]"));
        var keyList = string.Join(", ", options.KeyColumns.Select(k => $"[{Escape(k)}]"));
        var windowJoin = NullSafeKeyEquality(options.KeyColumns, "src", "k");
        var dedupedSource = OneRowPerKeySource(stg, options.DataColumns, options.KeyColumns);

        return $"""
            SET NOCOUNT ON;
            IF OBJECT_ID('tempdb..#UpsertKeysI') IS NOT NULL DROP TABLE #UpsertKeysI;
            SELECT {keySelectK}, ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum
            INTO #UpsertKeysI
            FROM (
                SELECT DISTINCT {keySelect}
                FROM {stg} AS src
                WHERE NOT EXISTS (SELECT 1 FROM {trg} AS trg WHERE {keyEquality})
            ) AS k;
            CREATE UNIQUE CLUSTERED INDEX [IX_UpsertKeysI_RowNum] ON #UpsertKeysI (RowNum);
            CREATE STATISTICS [ST_UpsertKeysI] ON #UpsertKeysI ({keyList});
            DECLARE @Affected bigint = 0, @Start bigint = 1, @End bigint = {options.BatchRowCount}, @Total bigint;
            SELECT @Total = COUNT_BIG(*) FROM #UpsertKeysI;
            WHILE @Start <= @Total
            BEGIN
                INSERT INTO {trg} ({insertColumnList})
                SELECT {selectList}
                FROM {dedupedSource} AS src
                INNER JOIN #UpsertKeysI AS k ON {windowJoin}
                WHERE src._rn = 1 AND k.RowNum BETWEEN @Start AND @End;
                SET @Affected = @Affected + @@ROWCOUNT;
                SET @Start = @End + 1;
                SET @End = @End + {options.BatchRowCount};
            END;
            DROP TABLE #UpsertKeysI;
            SELECT @Affected;
            """;
    }

    // Staging reduced to one row per business key, as a derived table exposing _rn (keep the row where _rn = 1).
    // An incremental read over an append-mode landing source legitimately carries several rows with the same key
    // (the same key landed by more than one file or window), but the target enforces one row per key, so the
    // keyed INSERT must add exactly one or it violates the target's unique key. A plain SELECT DISTINCT is not
    // enough: rows that share the key but differ in any non-key or provenance column (FileDate_DW and kin) survive
    // it and then collide. Partitioning on the (always comparable) key columns collapses them, and carries any
    // non-comparable data column along unpartitioned, so it holds even for xml/geography/image staging.
    private static string OneRowPerKeySource(string stg, IReadOnlyList<string> dataColumns, IReadOnlyList<string> keyColumns)
    {
        var columns = string.Join(", ", dataColumns.Select(c => $"[{Escape(c)}]"));
        var partition = string.Join(", ", keyColumns.Select(k => $"[{Escape(k)}]"));
        return $"(SELECT {columns}, ROW_NUMBER() OVER (PARTITION BY {partition} ORDER BY (SELECT NULL)) AS _rn FROM {stg})";
    }

    private static string KeyEquality(IReadOnlyList<string> keys, string left, string right)
        => string.Join(" AND ", keys.Select(k => $"{left}.[{Escape(k)}] = {right}.[{Escape(k)}]"));

    // Equality for joining staging back to a numbered key table extracted FROM it (the batch windows). It has
    // to treat NULL as equal to NULL, unlike the staging-to-target match, where a plain `=` is the intended
    // semantics: a NULL business key deliberately matches no existing target row and so becomes an insert.
    // That very row then reaches the key table, and with a plain `=` it would fail to join back to the staging
    // row it came from: the batched path would silently write fewer rows than the unbatched path and under-
    // report its own count, with nothing in the log to show for it.
    private static string NullSafeKeyEquality(IReadOnlyList<string> keys, string left, string right)
        => string.Join(" AND ", keys.Select(k =>
        {
            var column = Escape(k);
            return $"({left}.[{column}] = {right}.[{column}] OR ({left}.[{column}] IS NULL AND {right}.[{column}] IS NULL))";
        }));

    // CONCAT requires at least two arguments, so a leading N'' guards the single-column case; non-key values
    // are interleaved with a separator so two distinct rows cannot collide on concatenation.
    private static string Checksum(string alias, IReadOnlyList<string> columns, UpsertOptions options, bool staging)
        => $"HASHBYTES('{options.HashAlgorithm}', CONCAT(N'', " +
           string.Join(", N'|', ", columns.Select(c => ChecksumTerm(alias, c, options, staging))) + "))";

    // What a column contributes to its side's checksum: the value the TARGET stores, rendered so that equal
    // values always produce equal text and different values never collide. Two things are needed for that, and
    // both are corrections of a comparison that otherwise silently misfires:
    //   - The staging value is converted to the target's type first when the two differ, or the same value
    //     renders as two different strings and every matched row hashes as changed on every run (an nchar(36)
    //     '2828ca9b-...' against a uniqueidentifier '2828CA9B-...').
    //   - A family whose default string form is LOSSY gets an explicit lossless style, or a real change hashes
    //     as no change: CONCAT prints a datetime to the minute (style 0, 'May  6 2024  7:08AM'), a float to six
    //     significant digits, and money to two decimals of its four.
    private static string ChecksumTerm(string alias, string column, UpsertOptions options, bool staging)
    {
        var term = $"{alias}.[{Escape(column)}]";
        if (!options.ChecksumColumnTypes.TryGetValue(column, out var types))
        {
            return term;
        }

        if (staging && !string.Equals(types.Staging.Render(), types.Target.Render(), StringComparison.OrdinalIgnoreCase))
        {
            term = $"CONVERT({types.Target.Render()}, {term})";
        }

        return types.Target.Family switch
        {
            SqlTypeFamily.DateTime => $"CONVERT(nvarchar(40), {term}, 126)",  // ISO 8601, every fractional digit
            SqlTypeFamily.Approximate => $"CONVERT(nvarchar(40), {term}, 3)", // all 17 significant digits
            SqlTypeFamily.Money => $"CONVERT(nvarchar(40), {term}, 2)",       // all four decimals
            _ => term,
        };
    }

    private static string Qualify(RelationalObject relationalObject) => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);
}
