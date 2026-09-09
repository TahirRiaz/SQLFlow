using SqlFlow.Core.Ingestion;

namespace SqlFlow.SqlServer.Ingestion;

/// <summary>
/// Plans the canonical (automatic) indexes a relational target gets when it is first created, porting the
/// legacy SyncSchema create-time index set: a UNIQUE NONCLUSTERED index on the key columns
/// (<c>NCI_KeyColumn</c>), and plain NONCLUSTERED indexes on the date column (<c>NCI_DateColumn</c>), the
/// dataset column (<c>NCI_DataSetColumn</c>), and <c>UpdatedDate_DW</c> (<c>NCI_UpdatedDate_DW</c>); optionally
/// a clustered columnstore when requested and there is no identity primary key (the two are mutually
/// exclusive). The legacy <c>{NameHash}</c> suffix is dropped in favor of stable, recognizable names; each
/// statement is guarded by a <c>sys.indexes</c> existence check so re-application is a no-op. Column names
/// must already be the cleaned TARGET names (the caller maps them). Pure text generation.
/// </summary>
public static class CanonicalIndexPlanner
{
    public static IReadOnlyList<string> Plan(
        RelationalObject target,
        IReadOnlyList<string> keyColumns,
        string? dateColumn,
        string? dataSetColumn,
        bool hasUpdatedDateColumn,
        bool columnStore,
        bool hasIdentityPrimaryKey,
        bool scd2Enabled = false,
        string? reloadColumn = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyColumns);

        var qualified = SchemaQualified(target);
        var objectId = $"OBJECT_ID(N'{QuoteLiteral(qualified)}')";
        var statements = new List<string>();

        // Under SCD2 the business key is NOT unique across the table (it has one row per version); the
        // canonical key index becomes a filtered unique index (one CURRENT row per key), created by
        // Scd2KeyIndexStatements instead, so the plain unique index is suppressed here.
        if (keyColumns.Count > 0 && !scd2Enabled)
        {
            statements.Add(CreateIndex("NCI_KeyColumn", qualified, objectId, keyColumns, unique: true));
        }

        if (!string.IsNullOrWhiteSpace(dateColumn))
        {
            statements.Add(CreateIndex("NCI_DateColumn", qualified, objectId, [dateColumn], unique: false));
        }

        if (!string.IsNullOrWhiteSpace(dataSetColumn))
        {
            statements.Add(CreateIndex("NCI_DataSetColumn", qualified, objectId, [dataSetColumn], unique: false));
        }

        if (hasUpdatedDateColumn)
        {
            statements.Add(CreateIndex("NCI_UpdatedDate_DW", qualified, objectId, ["UpdatedDate_DW"], unique: false));
        }

        // Per-file replace (load.reloadColumn) purges the target by this column on every run; index it so the
        // DELETE seeks instead of scans. Skipped when the column is already the leading column of the key, date,
        // or dataset index (that index already serves the seek), so no redundant duplicate is created.
        if (!string.IsNullOrWhiteSpace(reloadColumn)
            && !string.Equals(reloadColumn, dateColumn, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(reloadColumn, dataSetColumn, StringComparison.OrdinalIgnoreCase)
            && (keyColumns.Count == 0 || !string.Equals(reloadColumn, keyColumns[0], StringComparison.OrdinalIgnoreCase)))
        {
            statements.Add(CreateIndex("NCI_ReloadColumn", qualified, objectId, [reloadColumn], unique: false));
        }

        // A clustered columnstore index cannot coexist with a clustered rowstore primary key; the identity PK
        // wins (legacy precedence), so columnstore is only emitted when there is no identity PK.
        if (columnStore && !hasIdentityPrimaryKey)
        {
            var name = "CCSI_" + Sanitize(target.Name);
            statements.Add(
                $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = {objectId} AND name = N'{QuoteLiteral(name)}') " +
                $"CREATE CLUSTERED COLUMNSTORE INDEX [{Escape(name)}] ON {qualified};");
        }

        return statements;
    }

    /// <summary>
    /// The SCD2 key-index reconciliation, run whenever SCD2 is on (not only at create time): the business key
    /// is unique only among CURRENT rows, so the canonical key index is a FILTERED unique index
    /// (<c>WHERE [flag] = 1</c>). This drops a pre-existing non-filtered <c>NCI_KeyColumn</c> (the plain unique
    /// index a table got before SCD2 was enabled) and creates the filtered one when missing, so turning SCD2
    /// on for an already-created table migrates its key index. Both statements are guarded, so re-running is a
    /// no-op. The filter creates safely even before the flag column is backfilled (NULL is excluded by the
    /// filter, so the index starts empty).
    /// </summary>
    public static IReadOnlyList<string> Scd2KeyIndexStatements(
        RelationalObject target, IReadOnlyList<string> keyColumns, string currentFlagColumn)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(keyColumns);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentFlagColumn);
        if (keyColumns.Count == 0)
        {
            return [];
        }

        var qualified = SchemaQualified(target);
        var objectId = $"OBJECT_ID(N'{QuoteLiteral(qualified)}')";
        var columnList = string.Join(", ", keyColumns.Select(c => $"[{Escape(c)}]"));
        var flag = Escape(currentFlagColumn);
        var keyLiterals = string.Join(", ", keyColumns.Select(c => $"N'{QuoteLiteral(c)}'"));

        return
        [
            // (a) A same-named index that is not the correct filtered-unique form (the plain unique key index a
            //     table got before SCD2, or a hand-made wrong one) is dropped so the create below can replace it.
            $"IF EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = {objectId} AND name = N'NCI_KeyColumn' " +
            $"AND NOT (is_unique = 1 AND has_filter = 1)) DROP INDEX [NCI_KeyColumn] ON {qualified};",

            // (b) ANY non-filtered unique nonclustered index whose key is exactly the business key (whatever its
            //     name) enforces whole-table uniqueness and would block a second version, so it is dropped too.
            //     Primary keys and unique CONSTRAINTS are deliberately left alone (dropping them is unsafe and a
            //     natural-key PK is a modeling conflict the operator must resolve).
            $"""
            DECLARE @scd2Drop nvarchar(max) = N'';
            SELECT @scd2Drop = @scd2Drop + N'DROP INDEX ' + QUOTENAME(i.name) + N' ON {qualified};'
            FROM sys.indexes AS i
            WHERE i.object_id = {objectId} AND i.type = 2 AND i.is_unique = 1 AND i.has_filter = 0
              AND i.is_primary_key = 0 AND i.is_unique_constraint = 0
              AND (SELECT COUNT(*) FROM sys.index_columns AS ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = {keyColumns.Count}
              AND NOT EXISTS (
                  SELECT 1 FROM sys.index_columns AS ic
                  JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                  WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                    AND c.name NOT IN ({keyLiterals}));
            IF LEN(@scd2Drop) > 0 EXEC sys.sp_executesql @scd2Drop;
            """,

            // (c) Create the canonical filtered-unique key index (one CURRENT row per business key).
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = {objectId} AND name = N'NCI_KeyColumn') " +
            $"CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON {qualified} ({columnList}) WHERE [{flag}] = 1;",
        ];
    }

    private static string CreateIndex(string name, string qualified, string objectId, IReadOnlyList<string> columns, bool unique)
    {
        var columnList = string.Join(", ", columns.Select(c => $"[{Escape(c)}]"));
        var uniqueKeyword = unique ? "UNIQUE " : string.Empty;
        return
            $"IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = {objectId} AND name = N'{name}') " +
            $"CREATE {uniqueKeyword}NONCLUSTERED INDEX [{name}] ON {qualified} ({columnList});";
    }

    private static string Sanitize(string name)
        => new string(name.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());

    private static string SchemaQualified(RelationalObject relationalObject)
        => $"[{Escape(relationalObject.Schema)}].[{Escape(relationalObject.Name)}]";

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string QuoteLiteral(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
