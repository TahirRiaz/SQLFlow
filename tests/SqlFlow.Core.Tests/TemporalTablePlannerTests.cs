using SqlFlow.Core;
using SqlFlow.Core.Catalog;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The system-versioned temporal planner: every transition it will make, and every state it refuses to make
/// one from. These are the SQL Server rules the engine has to respect, each of which was verified against a
/// live engine before being encoded here:
/// a versioned table needs a PRIMARY KEY; the history table is two-part (same database) and is never silently
/// re-pointed; the period columns are GENERATED ALWAYS and can neither collide with a data column nor be
/// renamed in place; and the enable is idempotent so a re-run, or a run interrupted between the two
/// statements, converges instead of failing.
/// </summary>
public sealed class TemporalTablePlannerTests
{
    private static readonly RelationalObject Target = new() { Database = "dw", Schema = "arc", Name = "Bilteller" };

    private static CatalogObject Live(
        bool temporal = false,
        bool hasPeriod = false,
        bool primaryKey = true,
        string? historySchema = null,
        string? historyTable = null,
        int? retentionDays = null,
        string periodStart = TemporalPolicy.DefaultValidFromColumn,
        string periodEnd = TemporalPolicy.DefaultValidToColumn,
        ObjectType type = ObjectType.Table,
        params string[] dataColumns)
    {
        var columns = new List<CatalogColumn>
        {
            new() { Name = "Id", Ordinal = 1, NativeType = "int", IsNullable = false, IsIdentity = true, IsPrimaryKeyMember = primaryKey },
        };

        var ordinal = 2;
        foreach (var name in dataColumns.Length > 0 ? dataColumns : ["Volum"])
        {
            columns.Add(new CatalogColumn { Name = name, Ordinal = ordinal++, NativeType = "varchar(50)" });
        }

        if (hasPeriod || temporal)
        {
            columns.Add(new CatalogColumn
            {
                Name = periodStart, Ordinal = ordinal++, NativeType = "datetime2(7)", IsNullable = false,
                GeneratedAlways = GeneratedAlwaysKind.RowStart, IsHidden = true,
            });
            columns.Add(new CatalogColumn
            {
                Name = periodEnd, Ordinal = ordinal, NativeType = "datetime2(7)", IsNullable = false,
                GeneratedAlways = GeneratedAlwaysKind.RowEnd, IsHidden = true,
            });
        }

        return new CatalogObject
        {
            Name = new ThreePartName { Database = null, Schema = Target.Schema, Name = Target.Name },
            Type = type,
            Columns = columns,
            IsTemporal = temporal,
            HasSystemTimePeriod = hasPeriod || temporal,
            HistorySchema = temporal ? historySchema ?? TemporalPolicy.DefaultHistorySchema : null,
            HistoryTable = temporal ? historyTable ?? Target.Name : null,
            HistoryRetentionDays = retentionDays,
        };
    }

    private static IReadOnlyList<SqlColumn> Desired(params string[] names)
        => names.Select(n => new SqlColumn { Name = n, DataType = SqlDataType.Parse("varchar(50)") }).ToList();

    private static string AllSql(TemporalPlan plan) => string.Join("\n", plan.Statements.Select(s => s.Text));

    // ---------------------------------------------------------------- fresh enable

    [Fact]
    public void FreshTable_AddsPeriodThenEnables()
    {
        var plan = TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(), Desired("Id", "Volum"));

        Assert.Equal(TemporalAction.AddPeriodAndEnable, plan.Action);
        Assert.Equal("[ver].[Bilteller]", plan.HistoryName);
        Assert.Equal(3, plan.Statements.Count);

        // The history schema is ensured before anything references it.
        Assert.Contains("CREATE SCHEMA [ver]", plan.Statements[0].Text, StringComparison.Ordinal);

        var addPeriod = plan.Statements[1].Text;
        Assert.Contains("GENERATED ALWAYS AS ROW START HIDDEN NOT NULL", addPeriod, StringComparison.Ordinal);
        Assert.Contains("GENERATED ALWAYS AS ROW END HIDDEN NOT NULL", addPeriod, StringComparison.Ordinal);
        Assert.Contains("PERIOD FOR SYSTEM_TIME ([ValidFrom_DW], [ValidTo_DW])", addPeriod, StringComparison.Ordinal);

        // The ROW START default is one second in the past, so an existing row's period is never empty and the
        // row stays visible to FOR SYSTEM_TIME AS OF (the legacy flw.GetVersioningScript semantic).
        Assert.Contains("DATEADD(SECOND, -1, SYSUTCDATETIME())", addPeriod, StringComparison.Ordinal);

        var enable = plan.Statements[2].Text;
        Assert.Contains("SYSTEM_VERSIONING = ON (HISTORY_TABLE = [ver].[Bilteller]", enable, StringComparison.Ordinal);
        Assert.Contains("DATA_CONSISTENCY_CHECK = ON", enable, StringComparison.Ordinal);
        Assert.Contains("HISTORY_RETENTION_PERIOD = INFINITE", enable, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ADD PERIOD stamps every existing row (its ROW START default is a runtime expression, not a
    /// constant), so it is classified as a rewrite and the applier gives it its own transaction and lock
    /// window instead of burying it in the additive metadata batch. Linking history is metadata only.
    /// </summary>
    [Fact]
    public void AddPeriod_IsClassifiedAsARewrite_AndTheEnableIsMetadataOnly()
    {
        var plan = TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(), Desired("Id"));

        Assert.Equal(DdlCost.MetadataOnly, plan.Statements[0].Cost);
        Assert.Equal(DdlCost.Rewrite, plan.Statements[1].Cost);
        Assert.Equal(DdlCost.MetadataOnly, plan.Statements[2].Cost);
    }

    /// <summary>Both statements are guarded on live catalog state, so a concurrent run (or a retry after a
    /// partial apply) converges rather than failing on "period already exists".</summary>
    [Fact]
    public void FreshEnable_IsIdempotent()
    {
        var sql = AllSql(TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(), Desired("Id")));

        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.periods", sql, StringComparison.Ordinal);
        Assert.Contains("IF NOT EXISTS (SELECT 1 FROM sys.tables", sql, StringComparison.Ordinal);
        Assert.Contains("AND temporal_type = 2", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "datetime2(0)", "'9999-12-31 23:59:59'")]
    [InlineData(3, "datetime2(3)", "'9999-12-31 23:59:59.999'")]
    [InlineData(7, "datetime2(7)", "'9999-12-31 23:59:59.9999999'")]
    public void PeriodPrecision_DrivesTheColumnTypeAndTheOpenEndedSentinel(int precision, string type, string sentinel)
    {
        var policy = new TemporalPolicy { Enabled = true, PeriodPrecision = precision };
        var sql = AllSql(TemporalTablePlanner.Plan(Target, policy, Live(), Desired("Id")));

        Assert.Contains(type, sql, StringComparison.Ordinal);
        Assert.Contains(sentinel, sql, StringComparison.Ordinal);
    }

    /// <summary>HIDDEN is what makes enabling history non-breaking for consumers: SELECT * keeps returning
    /// the table's original column list. Turning it off is possible but must be explicit.</summary>
    [Fact]
    public void HiddenPeriodColumns_CanBeTurnedOff()
    {
        var policy = new TemporalPolicy { Enabled = true, HiddenPeriodColumns = false };
        var sql = AllSql(TemporalTablePlanner.Plan(Target, policy, Live(), Desired("Id")));

        Assert.Contains("GENERATED ALWAYS AS ROW START NOT NULL", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("HIDDEN", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void HistorySchemaAndTable_CanBeOverridden()
    {
        var policy = new TemporalPolicy { Enabled = true, HistorySchema = "history", HistoryTable = "Bilteller_Versions" };
        var plan = TemporalTablePlanner.Plan(Target, policy, Live(), Desired("Id"));

        Assert.Equal("[history].[Bilteller_Versions]", plan.HistoryName);
        Assert.Contains("CREATE SCHEMA [history]", AllSql(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void RetentionDays_RenderAsADayPeriod()
    {
        var policy = new TemporalPolicy { Enabled = true, RetentionDays = 3650 };
        Assert.Contains(
            "HISTORY_RETENTION_PERIOD = 3650 DAYS",
            AllSql(TemporalTablePlanner.Plan(Target, policy, Live(), Desired("Id"))),
            StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- resume and no-op paths

    /// <summary>
    /// SET (SYSTEM_VERSIONING = OFF) leaves the period columns and the PERIOD behind, so a target in that
    /// state needs only the enable. Re-adding the period there would fail on the column names, which is also
    /// the state a run interrupted between the two statements leaves behind.
    /// </summary>
    [Fact]
    public void PeriodWithoutVersioning_OnlyEnables()
    {
        var plan = TemporalTablePlanner.Plan(
            Target, new TemporalPolicy { Enabled = true }, Live(temporal: false, hasPeriod: true), Desired("Id"));

        Assert.Equal(TemporalAction.EnableOnExistingPeriod, plan.Action);
        Assert.Equal(2, plan.Statements.Count);
        Assert.DoesNotContain("PERIOD FOR SYSTEM_TIME", AllSql(plan), StringComparison.Ordinal);
        Assert.Contains("SYSTEM_VERSIONING = ON", AllSql(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void AlreadyVersionedIntoTheDeclaredHistory_IsANoOp()
    {
        var plan = TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(temporal: true), Desired("Id"));

        Assert.Equal(TemporalAction.AlreadyCurrent, plan.Action);
        Assert.False(plan.HasChanges);
        Assert.Empty(plan.Statements);
    }

    /// <summary>Re-issuing SET SYSTEM_VERSIONING = ON is the supported way to change the retention period, so
    /// that one statement is emitted unguarded (a temporal_type guard would make it a no-op).</summary>
    [Fact]
    public void RetentionDrift_ReIssuesTheEnableUnguarded()
    {
        var policy = new TemporalPolicy { Enabled = true, RetentionDays = 90 };
        var plan = TemporalTablePlanner.Plan(Target, policy, Live(temporal: true, retentionDays: 30), Desired("Id"));

        Assert.Equal(TemporalAction.ChangeRetention, plan.Action);
        var sql = Assert.Single(plan.Statements).Text;
        Assert.Contains("HISTORY_RETENTION_PERIOD = 90 DAYS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IF NOT EXISTS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearingRetention_SetsItBackToInfinite()
    {
        var plan = TemporalTablePlanner.Plan(
            Target, new TemporalPolicy { Enabled = true }, Live(temporal: true, retentionDays: 30), Desired("Id"));

        Assert.Equal(TemporalAction.ChangeRetention, plan.Action);
        Assert.Contains("HISTORY_RETENTION_PERIOD = INFINITE", AllSql(plan), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- refusals

    /// <summary>SQL Server: "System versioned temporal table ... must have primary key defined". Caught here
    /// so the message names the flow setting that fixes it rather than the raw engine error.</summary>
    [Fact]
    public void NoPrimaryKey_IsRefusedWithAnActionableMessage()
    {
        var ex = Assert.Throws<SqlFlowException>(() =>
            TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(primaryKey: false), Desired("Id")));

        Assert.Contains("PRIMARY KEY", ex.Message, StringComparison.Ordinal);
        Assert.Contains("target.identityColumn", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Re-pointing history would orphan every version already recorded, so it is refused rather than
    /// applied. The message names both the live and the declared history table.</summary>
    [Fact]
    public void RePointingTheHistoryTable_IsRefused()
    {
        var policy = new TemporalPolicy { Enabled = true, HistorySchema = "archive" };
        var ex = Assert.Throws<SqlFlowException>(() =>
            TemporalTablePlanner.Plan(Target, policy, Live(temporal: true, historySchema: "ver"), Desired("Id")));

        Assert.Contains("[ver].[Bilteller]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("[archive].[Bilteller]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("orphan", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A period cannot be renamed in place, so a declared name that differs from the live one is an
    /// error rather than a second period (which SQL Server would reject anyway).</summary>
    [Fact]
    public void RenamingAnExistingPeriod_IsRefused()
    {
        var policy = new TemporalPolicy { Enabled = true, ValidFromColumn = "SysStart", ValidToColumn = "SysEnd" };
        var ex = Assert.Throws<SqlFlowException>(() =>
            TemporalTablePlanner.Plan(Target, policy, Live(temporal: true), Desired("Id")));

        Assert.Contains("cannot be renamed", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ValidFrom_DW", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>SQL Server's own error ("specified more than once") does not say which side owns the name.</summary>
    [Fact]
    public void PeriodNameCollidingWithALiveDataColumn_IsRefused()
    {
        var ex = Assert.Throws<SqlFlowException>(() => TemporalTablePlanner.Plan(
            Target,
            new TemporalPolicy { Enabled = true },
            Live(dataColumns: ["Volum", "ValidFrom_DW"]),
            Desired("Id", "Volum")));

        Assert.Contains("ValidFrom_DW", ex.Message, StringComparison.Ordinal);
        Assert.Contains("already has a data column", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>The collision check covers the schema the flow is about to keep evolving, not just the live
    /// table: a column that arrives on the next run would otherwise break the enable after the fact.</summary>
    [Fact]
    public void PeriodNameCollidingWithADesiredColumn_IsRefused()
    {
        var ex = Assert.Throws<SqlFlowException>(() => TemporalTablePlanner.Plan(
            Target,
            new TemporalPolicy { Enabled = true },
            Live(),
            Desired("Id", "Volum", "ValidTo_DW")));

        Assert.Contains("ValidTo_DW", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalPeriodColumnNames_AreRefused()
    {
        var policy = new TemporalPolicy { Enabled = true, ValidFromColumn = "Period", ValidToColumn = "Period" };
        var ex = Assert.Throws<SqlFlowException>(() =>
            TemporalTablePlanner.Plan(Target, policy, Live(), Desired("Id")));

        Assert.Contains("two distinct columns", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AViewTarget_IsRefused()
    {
        var ex = Assert.Throws<SqlFlowException>(() =>
            TemporalTablePlanner.Plan(Target, new TemporalPolicy { Enabled = true }, Live(type: ObjectType.View), Desired("Id")));

        Assert.Contains("applies to tables", ex.Message, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- identifier safety

    /// <summary>Every identifier reaches the DDL through bracket escaping, and every one that also lands
    /// inside a string literal (the OBJECT_ID guards, the EXEC'd CREATE SCHEMA) is literal-escaped on top.</summary>
    [Fact]
    public void HostileIdentifiers_AreEscapedInBothBracketAndLiteralPositions()
    {
        var target = new RelationalObject { Database = "dw", Schema = "ar]c", Name = "Bil'teller" };
        var policy = new TemporalPolicy { Enabled = true, HistorySchema = "ve'r]" };
        var live = new CatalogObject
        {
            Name = new ThreePartName { Database = null, Schema = target.Schema, Name = target.Name },
            Type = ObjectType.Table,
            Columns = [new CatalogColumn { Name = "Id", Ordinal = 1, NativeType = "int", IsPrimaryKeyMember = true }],
        };

        var sql = AllSql(TemporalTablePlanner.Plan(target, policy, live, Desired("Id")));

        Assert.Contains("[ar]]c].[Bil'teller]", sql, StringComparison.Ordinal);          // bracket position
        Assert.Contains("OBJECT_ID(N'ar]c.Bil''teller')", sql, StringComparison.Ordinal); // literal position
        Assert.Contains("CREATE SCHEMA [ve''r]]]", sql, StringComparison.Ordinal);        // both, nested in EXEC
    }
}
