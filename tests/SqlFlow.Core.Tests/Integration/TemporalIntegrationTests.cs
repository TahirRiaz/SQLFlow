using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// System-versioned temporal history end to end against the real sink. The unit tests prove what DDL the
/// planner emits; these prove that SQL Server accepts it and behaves as the feature promises, and they
/// deliberately exercise every limitation a versioned table imposes rather than only the happy path:
/// versioning is turned on over an already-created, already-populated target; the period columns stay HIDDEN
/// so consumers see no shape change; ordinary schema evolution (add and widen) keeps working while versioning
/// is on and propagates to the history table; a re-run with no source change writes no history; a target with
/// no primary key is refused; and re-pointing the history table is refused. Skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TemporalIntegrationTests
{
    // Unique per test class: staging tables are named [raw].[<schema>_<table>_<flowId>] and harness cleanup
    // drops by the flow-id suffix, so classes sharing a flow id drop each other's live staging under xunit's
    // parallel run.
    private const int FlowId = 9311;

    private static IngestionFlow BuildFlow(string src, string trg, TemporalPolicy? temporal, bool identity = true) => new()
    {
        FlowId = FlowId,
        SysAlias = trg,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget
        {
            Server = "sink",
            Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg },
            // SQL Server requires a system-versioned table to have a PRIMARY KEY; in V3 the identity column is
            // what makes the engine create one, so it is part of the normal temporal setup.
            IdentityColumn = identity ? "RowPK" : null,
        },
        Load = new IngestionLoadPolicy { KeyColumns = ["CustomerId"] },
        SystemColumns = new SystemColumnsPolicy { InsertedDate = true, UpdatedDate = true },
        Versioning = new VersioningPolicy { Temporal = temporal ?? new TemporalPolicy() },
    };

    // ---------------------------------------------------------------- the headline scenario

    /// <summary>
    /// The scenario legacy could only do at table-creation time: a target that already exists and already
    /// holds rows is turned into a system-versioned table, and from then on every change is recoverable.
    /// Also proves the non-breaking claim: the period columns are HIDDEN, so the column list a consumer sees
    /// through SELECT * is byte-for-byte what it was before versioning was enabled.
    /// </summary>
    [SkippableFact]
    public async Task Temporal_CanBeEnabledOnAnExistingPopulatedTable_AndCapturesEveryChange()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmp_Src";
        const string trg = "_SfTmp_Trg";

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [Name] nvarchar(50) NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann','Oslo'),(2,'Bob','Bergen');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();

            // Pass 1: load WITHOUT temporal. The target is created and populated; it is an ordinary table.
            var first = await runner.RunAsync(BuildFlow(src, trg, temporal: null));
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.False(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.False(await TemporalDb.HasPeriodAsync(cs, trg));

            var columnsBefore = await ColumnListAsync(cs, trg);

            // Pass 2: turn temporal ON over the populated table.
            var second = await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true }));
            Assert.True(second.Success, second.Error);

            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.Equal($"ver.{trg}", await TemporalDb.HistoryNameAsync(cs, trg));

            // The period is a hidden datetime2(7) pair, exactly as declared.
            Assert.Equal(
                ["ValidFrom_DW:datetime2(7):hidden", "ValidTo_DW:datetime2(7):hidden"],
                await TemporalDb.PeriodColumnsAsync(cs, trg));

            // Non-breaking: SELECT * still returns the original column list, so no consumer sees a shape change.
            Assert.Equal(columnsBefore, await ColumnListAsync(cs, trg));

            // Enabling versioning does not disturb the rows already there, and history starts empty.
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(0, await TemporalDb.HistoryRowCountAsync(cs, trg));

            // A change: customer 1 moves city, customer 3 is new.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [City] = 'Stockholm' WHERE CustomerId = 1;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (3,'Cy','Tromso');");

            var third = await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true }));
            Assert.True(third.Success, third.Error);

            // The current table holds the new truth...
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal("Stockholm", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 1"));

            // ...and SQL Server kept the superseded version, which is the whole point of the feature.
            Assert.Equal(1, await TemporalDb.HistoryRowCountAsync(cs, trg));
            Assert.Equal("Oslo", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [ver].[{trg}] WHERE [CustomerId] = 1"));

            // FOR SYSTEM_TIME ALL sees both eras: 3 current rows plus the 1 superseded version.
            Assert.Equal(4, await TemporalDb.AllVersionsCountAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    /// <summary>
    /// A re-run with no source change must write NO history. This is the regression guard for the subtle way
    /// this feature could break change detection: the period columns are GENERATED ALWAYS and live in
    /// sys.columns like any other column, so if they leaked into the schema diff or the change-detection type
    /// map, every run would rewrite every matched row and manufacture a new history version for it.
    /// </summary>
    [SkippableFact]
    public async Task Temporal_ReRunWithNoSourceChange_WritesNoHistory()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpIdem_Src";
        const string trg = "_SfTmpIdem_Trg";

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] decimal(18,2) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann',10.50),(2,'Bob',20.25);");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var policy = new TemporalPolicy { Enabled = true };

            Assert.True((await runner.RunAsync(BuildFlow(src, trg, policy))).Success);
            Assert.Equal(0, await TemporalDb.HistoryRowCountAsync(cs, trg));

            // Two further runs, no source change at all.
            var second = await runner.RunAsync(BuildFlow(src, trg, policy));
            Assert.True(second.Success, second.Error);
            var third = await runner.RunAsync(BuildFlow(src, trg, policy));
            Assert.True(third.Success, third.Error);

            Assert.Equal(0, second.RowsUpdated);
            Assert.Equal(0, third.RowsUpdated);
            Assert.Equal(0, await TemporalDb.HistoryRowCountAsync(cs, trg));
            Assert.Equal(2, await TemporalDb.AllVersionsCountAsync(cs, trg));
            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    // ---------------------------------------------------------------- schema evolution under versioning

    /// <summary>
    /// The limitation everyone assumes exists but does not: ADD COLUMN and a widening ALTER COLUMN are both
    /// supported while SYSTEM_VERSIONING is ON, and SQL Server propagates each to the history table. The
    /// engine therefore never has to take versioning off to fit a schema change through, which would leave a
    /// window where changes are not recorded. This test fails the moment that assumption stops holding.
    /// </summary>
    [SkippableFact]
    public async Task Temporal_SchemaEvolutionKeepsWorking_AndPropagatesToTheHistoryTable()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpEvo_Src";
        const string trg = "_SfTmpEvo_Trg";

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] varchar(20) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var policy = new TemporalPolicy { Enabled = true };

            Assert.True((await runner.RunAsync(BuildFlow(src, trg, policy))).Success);
            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));

            // The source grows a column and widens another, both while the target is versioned.
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ADD [Segment] nvarchar(30) NULL;");
            await IntegrationDb.ExecuteAsync(cs, $"ALTER TABLE [dbo].[{src}] ALTER COLUMN [City] varchar(80) NULL;");
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [Segment] = 'Retail' WHERE CustomerId = 1;");

            var second = await runner.RunAsync(BuildFlow(src, trg, policy));
            Assert.True(second.Success, second.Error);

            // The new column landed on the current table AND on the history table (SQL Server's propagation).
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "Segment"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('[ver].[{trg}]') AND name = 'Segment'"));

            // The widened column is wider on both halves too.
            Assert.Equal("varchar(80)", await IntegrationDb.ColumnTypeAsync(cs, trg, "City"));

            // Versioning was never switched off to make room for the change.
            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.Equal($"ver.{trg}", await TemporalDb.HistoryNameAsync(cs, trg));

            // And the attribute change was captured as history, not lost during the evolution.
            Assert.Equal(1, await TemporalDb.HistoryRowCountAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    // ---------------------------------------------------------------- configuration

    /// <summary>Every knob applied at once: a custom history schema and table, renamed non-hidden period
    /// columns, and the legacy datetime2(0) precision that a history table migrated from an older estate
    /// requires the current table to match.</summary>
    [SkippableFact]
    public async Task Temporal_HonorsCustomHistoryNamesPeriodNamesPrecisionAndVisibility()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpCfg_Src";
        const string trg = "_SfTmpCfg_Trg";
        const string historySchema = "verx";
        const string historyTable = "_SfTmpCfg_Versions";

        await CleanAsync(cs, src, trg, historySchema, historyTable);
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            var policy = new TemporalPolicy
            {
                Enabled = true,
                HistorySchema = historySchema,
                HistoryTable = historyTable,
                ValidFromColumn = "SysStart",
                ValidToColumn = "SysEnd",
                HiddenPeriodColumns = false,
                PeriodPrecision = 0,
            };

            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(src, trg, policy));
            Assert.True(result.Success, result.Error);

            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.Equal($"{historySchema}.{historyTable}", await TemporalDb.HistoryNameAsync(cs, trg));
            Assert.Equal(
                ["SysStart:datetime2(0):visible", "SysEnd:datetime2(0):visible"],
                await TemporalDb.PeriodColumnsAsync(cs, trg));

            // Not hidden, so the period columns ARE part of SELECT * here; that is the documented trade-off.
            Assert.Contains("SysStart", await ColumnListAsync(cs, trg), StringComparison.Ordinal);
        }
        finally
        {
            await CleanAsync(cs, src, trg, historySchema, historyTable);
        }
    }

    /// <summary>Retention is set on enable and can be changed (and cleared back to INFINITE) on a later run,
    /// which is the one difference the planner will still act on once a table is already versioned.</summary>
    [SkippableFact]
    public async Task Temporal_SetsAndChangesHistoryRetention()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpRet_Src";
        const string trg = "_SfTmpRet_Trg";

        Skip.IfNot(
            await IntegrationDb.ScalarAsync<int?>(cs, "SELECT 1 WHERE COL_LENGTH('sys.tables','history_retention_period') IS NOT NULL") == 1,
            "HISTORY_RETENTION_PERIOD needs Azure SQL Database, Azure SQL Managed Instance, or SQL Server 2025+.");

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();

            var first = await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true, RetentionDays = 30 }));
            Assert.True(first.Success, first.Error);
            Assert.Equal("30 DAY", await TemporalDb.RetentionAsync(cs, trg));

            var second = await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true, RetentionDays = 3650 }));
            Assert.True(second.Success, second.Error);
            Assert.Equal("3650 DAY", await TemporalDb.RetentionAsync(cs, trg));

            // Dropping the setting returns the table to keeping history forever (-1 is SQL Server's INFINITE).
            var third = await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true }));
            Assert.True(third.Success, third.Error);
            Assert.Equal("-1 INFINITE", await TemporalDb.RetentionAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    // ---------------------------------------------------------------- resume and refusals

    /// <summary>
    /// SET (SYSTEM_VERSIONING = OFF) leaves the period columns behind, so an operator's manual disable (or a
    /// run interrupted between the two statements) must converge on the next run rather than fail trying to
    /// add a period that is already there.
    /// </summary>
    [SkippableFact]
    public async Task Temporal_ReEnablesOverASurvivingPeriod_WithoutReAddingIt()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpRes_Src";
        const string trg = "_SfTmpRes_Trg";

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            var policy = new TemporalPolicy { Enabled = true };

            Assert.True((await runner.RunAsync(BuildFlow(src, trg, policy))).Success);

            // An operator unlinks versioning; the period survives, exactly as SQL Server documents.
            await TemporalDb.DisableVersioningAsync(cs, trg);
            Assert.False(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.True(await TemporalDb.HasPeriodAsync(cs, trg));

            var second = await runner.RunAsync(BuildFlow(src, trg, policy));
            Assert.True(second.Success, second.Error);
            Assert.True(await TemporalDb.IsVersionedAsync(cs, trg));
            Assert.Equal($"ver.{trg}", await TemporalDb.HistoryNameAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    /// <summary>SQL Server refuses to version a table without a PRIMARY KEY. The engine catches it first and
    /// names the flow setting that fixes it, instead of surfacing the raw engine error.</summary>
    [SkippableFact]
    public async Task Temporal_WithoutAPrimaryKey_FailsWithAnActionableMessage()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpNoPk_Src";
        const string trg = "_SfTmpNoPk_Trg";

        await CleanAsync(cs, src, trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            // identity: false means the engine creates the target with no primary key at all.
            var result = await RelationalIngestionHarness.BuildRunner()
                .RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true }, identity: false));

            Assert.False(result.Success);
            Assert.Contains("PRIMARY KEY", result.Error!, StringComparison.Ordinal);
            Assert.Contains("target.identityColumn", result.Error!, StringComparison.Ordinal);

            // The failure is a refusal, not a half-applied state: no period was added.
            Assert.False(await TemporalDb.HasPeriodAsync(cs, trg));
            Assert.False(await TemporalDb.IsVersionedAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg);
        }
    }

    /// <summary>Re-pointing history would orphan every version already recorded, so an edited history name on
    /// an already-versioned table fails the run rather than silently relinking it.</summary>
    [SkippableFact]
    public async Task Temporal_RePointingTheHistoryTable_FailsTheRun()
    {
        var cs = IntegrationDb.Require();
        const string src = "_SfTmpRep_Src";
        const string trg = "_SfTmpRep_Trg";

        await CleanAsync(cs, src, trg);
        await CleanAsync(cs, src, trg, "verx", trg);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Oslo');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();
            Assert.True((await runner.RunAsync(BuildFlow(src, trg, new TemporalPolicy { Enabled = true }))).Success);

            var result = await runner.RunAsync(
                BuildFlow(src, trg, new TemporalPolicy { Enabled = true, HistorySchema = "verx" }));

            Assert.False(result.Success);
            Assert.Contains("orphan", result.Error!, StringComparison.OrdinalIgnoreCase);

            // The original link is untouched: the refusal changed nothing.
            Assert.Equal($"ver.{trg}", await TemporalDb.HistoryNameAsync(cs, trg));
        }
        finally
        {
            await CleanAsync(cs, src, trg, "verx", trg);
            await CleanAsync(cs, src, trg);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static async Task CleanAsync(
        string cs,
        string src,
        string trg,
        string historySchema = TemporalDb.HistorySchema,
        string? historyTable = null)
    {
        await IntegrationDb.DropTableAsync(cs, src);
        await TemporalDb.DropVersionedTableAsync(cs, trg, historySchema, historyTable);
        await RelationalIngestionHarness.DropStagingAsync(cs, FlowId);
    }

    /// <summary>The table's column list as a consumer sees it: what SELECT * would return, in order, which
    /// excludes HIDDEN period columns.</summary>
    private static async Task<string> ColumnListAsync(string cs, string table)
        => await IntegrationDb.ScalarAsync<string?>(cs,
            $"""
             SELECT STRING_AGG(CONVERT(nvarchar(max), c.[name]), ',') WITHIN GROUP (ORDER BY c.column_id)
             FROM sys.columns AS c
             WHERE c.object_id = OBJECT_ID('[dbo].[{table.Replace("]", "]]", StringComparison.Ordinal)}]') AND c.is_hidden = 0
             """) ?? string.Empty;
}
