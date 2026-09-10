using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;

namespace SqlFlow.SqlServer.Schema;

/// <summary>What the temporal planner decided to do with the target, for logging and for the run record.</summary>
public enum TemporalAction
{
    /// <summary>The target is already in the declared state; no DDL.</summary>
    AlreadyCurrent,

    /// <summary>The table has no SYSTEM_TIME period: add the period columns, then turn versioning on.</summary>
    AddPeriodAndEnable,

    /// <summary>The period columns survive from an earlier enablement (SET SYSTEM_VERSIONING = OFF leaves them
    /// in place), so only the enable statement is needed. This is also the resume path after a run that was
    /// interrupted between the two statements.</summary>
    EnableOnExistingPeriod,

    /// <summary>Versioning is already on and correctly linked; only HISTORY_RETENTION_PERIOD differs.</summary>
    ChangeRetention,
}

/// <summary>The DDL that brings one target to its declared system-versioning state, plus what it will do.</summary>
public sealed record TemporalPlan
{
    public required TemporalAction Action { get; init; }

    public required IReadOnlyList<DdlStatement> Statements { get; init; }

    /// <summary>The two-part history table the target is (or will be) versioned into.</summary>
    public required string HistoryName { get; init; }

    public bool HasChanges => Statements.Count > 0;
}

/// <summary>
/// Plans SQL Server system-versioned temporal history for an ingestion target (legacy <c>trgVersioning</c>,
/// legacy <c>flw.GetVersioningScript</c>). Pure: it takes the declared policy plus the live introspected
/// shape and returns idempotent DDL, so every rule below is decided before a statement runs.
///
/// The SQL Server limitations this encodes, each verified against a live engine rather than assumed:
/// <list type="bullet">
/// <item><b>A PRIMARY KEY is mandatory.</b> <c>SET (SYSTEM_VERSIONING = ON)</c> fails with "must have primary
/// key defined", so a target without one is rejected here with an actionable message instead of at the DDL.</item>
/// <item><b>The history table must be in the same database</b> (SQL Server only accepts a two-part history
/// name), so the plan never emits a three-part name and the policy's schema is resolved in the target's database.</item>
/// <item><b>The history table's column names and ordinals must match the current table exactly.</b> When the
/// history table already exists (a migrated history seeded from an older estate), SQL Server validates that
/// on enable; the plan uses <c>DATA_CONSISTENCY_CHECK = ON</c> so the mismatch surfaces as a precise engine
/// error rather than a silently mislinked table.</item>
/// <item><b>Period columns are GENERATED ALWAYS and cannot collide.</b> Adding a period whose column name is
/// already taken fails with "specified more than once", so the plan checks the desired schema first.</item>
/// <item><b>Re-pointing history is never silent.</b> A target already versioned into a different history table
/// is an error: switching the link would orphan the existing history, which is data loss by side effect.</item>
/// <item><b>Turning the flag off never un-versions the table.</b> There is no "disable" transition here at
/// all: dropping system-versioning discards the ability to recover past rows and is an explicit operator
/// action, not something an edited YAML performs.</item>
/// </list>
///
/// Two limitations are deliberately handled elsewhere, because they are not schema transitions:
/// <c>TRUNCATE TABLE</c> is not a supported operation on a versioned table (rejected by flow validation
/// against <c>target.truncateBeforeLoad</c>), and ordinary schema evolution (ADD, ALTER and DROP COLUMN) is
/// fully supported while versioning is on and propagates to the history table, so the evolution path runs
/// unchanged and this planner never toggles versioning off to let a column change through.
/// </summary>
public static class TemporalTablePlanner
{
    /// <summary>
    /// Plans the transition from the live target state to the declared policy. <paramref name="live"/> is the
    /// introspected target (never null: the target must exist before it can be versioned).
    /// <paramref name="desired"/> is the schema the evolution step just applied, used only to detect a period
    /// column name that collides with a real data column.
    /// </summary>
    public static TemporalPlan Plan(
        RelationalObject target,
        TemporalPolicy policy,
        CatalogObject live,
        IReadOnlyList<SqlColumn> desired)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(desired);

        var historySchema = string.IsNullOrWhiteSpace(policy.HistorySchema)
            ? TemporalPolicy.DefaultHistorySchema
            : policy.HistorySchema.Trim();
        var historyTable = policy.ResolveHistoryTable(target.Name);
        var historyName = $"[{Escape(historySchema)}].[{Escape(historyTable)}]";
        var qualified = $"[{Escape(target.Schema)}].[{Escape(target.Name)}]";
        var literal = Literal($"{target.Schema}.{target.Name}");

        if (live.Type != ObjectType.Table)
        {
            throw new SqlFlowException(
                $"versioning.temporal cannot be enabled on '{target.QualifiedName}': system-versioning applies to tables, " +
                $"and the target is a {live.Type.ToString().ToLowerInvariant()}.");
        }

        // Already versioned: the only remaining difference the engine will act on is the retention period.
        // Re-pointing the history table or renaming the period is refused, never silently applied.
        if (live.IsTemporal)
        {
            EnsureHistoryMatches(target, live, historySchema, historyTable);
            EnsurePeriodColumnsMatch(target, live, policy);

            if (live.HistoryRetentionDays == policy.RetentionDays)
            {
                return new TemporalPlan
                {
                    Action = TemporalAction.AlreadyCurrent,
                    Statements = [],
                    HistoryName = historyName,
                };
            }

            return new TemporalPlan
            {
                Action = TemporalAction.ChangeRetention,
                Statements = [EnableStatement(qualified, literal, historyName, policy, alreadyOn: true)],
                HistoryName = historyName,
            };
        }

        EnsurePrimaryKey(target, live);

        var statements = new List<DdlStatement> { EnsureHistorySchemaStatement(historySchema) };

        // SET (SYSTEM_VERSIONING = OFF) leaves the period columns and the PERIOD definition behind, so a
        // target in that state (an operator's manual disable, or a run interrupted between the two
        // statements) needs only the enable. Re-adding the period there would fail on the column names.
        if (live.HasSystemTimePeriod)
        {
            EnsurePeriodColumnsMatch(target, live, policy);
            statements.Add(EnableStatement(qualified, literal, historyName, policy, alreadyOn: false));

            return new TemporalPlan
            {
                Action = TemporalAction.EnableOnExistingPeriod,
                Statements = statements,
                HistoryName = historyName,
            };
        }

        EnsurePeriodNamesAreFree(target, policy, live, desired);
        statements.Add(AddPeriodStatement(qualified, literal, target, policy));
        statements.Add(EnableStatement(qualified, literal, historyName, policy, alreadyOn: false));

        return new TemporalPlan
        {
            Action = TemporalAction.AddPeriodAndEnable,
            Statements = statements,
            HistoryName = historyName,
        };
    }

    /// <summary>
    /// Adds the period columns and the PERIOD definition in one ALTER, which is the only way SQL Server
    /// accepts them on an existing table. Both columns need a DEFAULT because the table may already hold
    /// rows: ROW START is stamped one second in the past so it is strictly less than any subsequent update's
    /// timestamp (a row whose period is empty is invisible to <c>FOR SYSTEM_TIME AS OF</c>), and ROW END
    /// takes the open-ended sentinel. This carries the legacy <c>flw.GetVersioningScript</c> semantics
    /// forward: history begins at the moment versioning was enabled.
    ///
    /// Classified as a rewrite: the ROW START default is a runtime expression, not a constant, so SQL Server
    /// stamps every existing row rather than recording a metadata-only default. On a large target that is a
    /// one-time full pass, and the applier gives it its own transaction and lock window instead of burying
    /// it in the additive batch.
    /// </summary>
    private static DdlStatement AddPeriodStatement(string qualified, string literal, RelationalObject target, TemporalPolicy policy)
    {
        var precision = policy.PeriodPrecision;
        // datetime2 renders its own maximum for the open-ended sentinel: at scale 0 that is 23:59:59, at
        // scale 7 it is 23:59:59.9999999. Rendering the full-precision literal and converting down would
        // round up out of range, so the sentinel is built to the declared scale.
        var openEnded = precision == 0 ? "9999-12-31 23:59:59" : "9999-12-31 23:59:59." + new string('9', precision);
        var hidden = policy.HiddenPeriodColumns ? " HIDDEN" : string.Empty;
        var from = policy.ValidFromColumn;
        var to = policy.ValidToColumn;
        var fromDefault = ConstraintName(target, from);
        var toDefault = ConstraintName(target, to);

        var text =
            $"IF NOT EXISTS (SELECT 1 FROM sys.periods WHERE object_id = OBJECT_ID(N'{literal}') AND period_type = 1)\n" +
            $"ALTER TABLE {qualified} ADD\n" +
            $"    [{Escape(from)}] datetime2({precision}) GENERATED ALWAYS AS ROW START{hidden} NOT NULL\n" +
            $"        CONSTRAINT [{Escape(fromDefault)}] DEFAULT DATEADD(SECOND, -1, SYSUTCDATETIME()),\n" +
            $"    [{Escape(to)}] datetime2({precision}) GENERATED ALWAYS AS ROW END{hidden} NOT NULL\n" +
            $"        CONSTRAINT [{Escape(toDefault)}] DEFAULT CONVERT(datetime2({precision}), '{openEnded}'),\n" +
            $"    PERIOD FOR SYSTEM_TIME ([{Escape(from)}], [{Escape(to)}]);";

        return new DdlStatement { Text = text, Cost = DdlCost.Rewrite };
    }

    /// <summary>
    /// The enable statement. <c>DATA_CONSISTENCY_CHECK = ON</c> is always requested: when the history table
    /// is auto-created it costs nothing, and when it already exists (a history migrated from an older estate)
    /// it is the check that stops a mismatched table from being linked. When versioning is already on, the
    /// same statement is the supported way to change HISTORY_RETENTION_PERIOD, so it is re-issued unguarded;
    /// otherwise it is guarded on <c>temporal_type</c> so a concurrent run cannot apply it twice.
    /// </summary>
    private static DdlStatement EnableStatement(string qualified, string literal, string historyName, TemporalPolicy policy, bool alreadyOn)
    {
        var retention = policy.RetentionDays is { } days
            ? $", HISTORY_RETENTION_PERIOD = {days} DAYS"
            : ", HISTORY_RETENTION_PERIOD = INFINITE";

        var alter = $"ALTER TABLE {qualified} SET " +
                    $"(SYSTEM_VERSIONING = ON (HISTORY_TABLE = {historyName}, DATA_CONSISTENCY_CHECK = ON{retention}));";

        var text = alreadyOn
            ? alter
            : $"IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE object_id = OBJECT_ID(N'{literal}') AND temporal_type = 2)\n{alter}";

        // Linking history is a metadata operation; the row-stamping cost, when there is one, was paid by the
        // ADD PERIOD that precedes it.
        return new DdlStatement { Text = text, Cost = DdlCost.MetadataOnly };
    }

    private static DdlStatement EnsureHistorySchemaStatement(string schema)
        => new()
        {
            // The identifier is escaped for the bracket quoting and then literal-escaped again, because it
            // sits inside the EXEC string: CREATE SCHEMA must begin its own batch and cannot be run inline.
            Text = $"IF SCHEMA_ID(N'{Literal(schema)}') IS NULL EXEC(N'CREATE SCHEMA [{Literal(Escape(schema))}]');",
            Cost = DdlCost.MetadataOnly,
        };

    private static void EnsurePrimaryKey(RelationalObject target, CatalogObject live)
    {
        if (live.Columns.Any(c => c.IsPrimaryKeyMember))
        {
            return;
        }

        throw new SqlFlowException(
            $"versioning.temporal cannot be enabled on '{target.QualifiedName}': SQL Server requires a system-versioned " +
            "table to have a PRIMARY KEY, and the target has none. Set 'target.identityColumn' so the engine creates the " +
            "table with a clustered primary key, or add a primary key to the existing table before enabling temporal history.");
    }

    private static void EnsureHistoryMatches(RelationalObject target, CatalogObject live, string historySchema, string historyTable)
    {
        if (string.Equals(live.HistorySchema, historySchema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(live.HistoryTable, historyTable, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new SqlFlowException(
            $"'{target.QualifiedName}' is already system-versioned into [{live.HistorySchema}].[{live.HistoryTable}], but the " +
            $"flow declares [{historySchema}].[{historyTable}]. Re-pointing the history table would orphan the existing " +
            "history, so the engine will not do it: either declare the current history table under 'versioning.temporal', " +
            "or migrate the history and re-link it as a deliberate operator step.");
    }

    private static void EnsurePeriodColumnsMatch(RelationalObject target, CatalogObject live, TemporalPolicy policy)
    {
        var start = live.Columns.FirstOrDefault(c => c.GeneratedAlways == GeneratedAlwaysKind.RowStart)?.Name;
        var end = live.Columns.FirstOrDefault(c => c.GeneratedAlways == GeneratedAlwaysKind.RowEnd)?.Name;

        if (string.Equals(start, policy.ValidFromColumn, StringComparison.OrdinalIgnoreCase)
            && string.Equals(end, policy.ValidToColumn, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new SqlFlowException(
            $"'{target.QualifiedName}' already carries a SYSTEM_TIME period on [{start}]/[{end}], but the flow declares " +
            $"[{policy.ValidFromColumn}]/[{policy.ValidToColumn}]. A period cannot be renamed in place; either declare the " +
            "existing column names under 'versioning.temporal', or drop the period as a deliberate operator step.");
    }

    /// <summary>
    /// Refuses a period column name that a real column already uses, on the live table or in the schema the
    /// flow is about to keep evolving. SQL Server's own error for this ("specified more than once") does not
    /// say which side owns the name, and a period that silently swallowed a data column would be worse.
    /// </summary>
    private static void EnsurePeriodNamesAreFree(
        RelationalObject target,
        TemporalPolicy policy,
        CatalogObject live,
        IReadOnlyList<SqlColumn> desired)
    {
        if (string.Equals(policy.ValidFromColumn, policy.ValidToColumn, StringComparison.OrdinalIgnoreCase))
        {
            throw new SqlFlowException(
                $"versioning.temporal on '{target.QualifiedName}' declares the same column '{policy.ValidFromColumn}' as both " +
                "validFromColumn and validToColumn; a SYSTEM_TIME period needs two distinct columns.");
        }

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in live.Columns.Where(c => c.GeneratedAlways == GeneratedAlwaysKind.None))
        {
            taken.Add(column.Name);
        }

        foreach (var column in desired)
        {
            taken.Add(column.Name);
        }

        foreach (var name in new[] { policy.ValidFromColumn, policy.ValidToColumn })
        {
            if (taken.Contains(name))
            {
                throw new SqlFlowException(
                    $"versioning.temporal on '{target.QualifiedName}' cannot use '{name}' as a period column: the table already " +
                    "has a data column of that name, and SQL Server cannot add a period column that collides with one. Choose " +
                    "different 'validFromColumn'/'validToColumn' names.");
            }
        }
    }

    /// <summary>The default constraint name, matching legacy's <c>DF_&lt;schema&gt;&lt;table&gt;&lt;column&gt;</c>
    /// shape so a ported target's constraint names are recognizable. Kept inside the 128-character identifier
    /// limit, which the concatenation of three user-chosen names can otherwise exceed.</summary>
    private static string ConstraintName(RelationalObject target, string column)
    {
        var name = $"DF_{target.Schema}{target.Name}{column}";
        return name.Length <= 128 ? name : name[..128];
    }

    private static string Escape(string identifier) => identifier.Replace("]", "]]", StringComparison.Ordinal);

    private static string Literal(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
