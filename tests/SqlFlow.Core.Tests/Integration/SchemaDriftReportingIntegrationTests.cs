using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Covers what a relational run says when the source's shape and the target's shape have parted company: the
/// preflight that refuses a <c>schema.sync: false</c> run whose target cannot accept the source's columns, and
/// the warnings a synced run raises for the changes that are visible to everything downstream.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SchemaDriftReportingIntegrationTests
{
    [SkippableFact]
    public async Task SyncOff_TargetMissingASourceColumn_FailsBeforeAnyDataMoves()
    {
        const int flowId = 91;
        var cs = IntegrationDb.Require();
        const string src = "_SfDrift_Src";
        const string trg = "_SfDrift_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        // The source has grown a column the hand-built target never had, which is exactly what an upstream
        // release does to a pinned target.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, [current_phone_number_id] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([Id],[Name],[current_phone_number_id]) VALUES (1, 'Ann', 7);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, " +
            "[InsertedDate_DW] datetime NULL, [UpdatedDate_DW] datetime NULL);");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(Flow(flowId, src, trg) with
            {
                SchemaSync = new SchemaSyncPolicy { Sync = false },
            });

            Assert.False(result.Success);
            Assert.Contains("current_phone_number_id", result.Error, StringComparison.Ordinal);
            Assert.Contains("schema.sync is off", result.Error, StringComparison.Ordinal);
            Assert.Contains("source.ignoreColumns", result.Error, StringComparison.Ordinal);

            // The point of preflighting: it fails before the source is read, so no staging table was built and
            // no row was copied. Staging is deliberately KEPT on failure, so its absence proves the run stopped
            // ahead of the staging rebuild rather than after it.
            Assert.Equal(0, result.RowsStaged);
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
            Assert.Equal(0, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task SyncOff_TargetDoesNotExist_SaysWhyInsteadOfFailingOnTheLoad()
    {
        const int flowId = 92;
        var cs = IntegrationDb.Require();
        const string src = "_SfDriftMissing_Src";
        const string trg = "_SfDriftMissing_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL);");

        try
        {
            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(Flow(flowId, src, trg) with
            {
                SchemaSync = new SchemaSyncPolicy { Sync = false },
            });

            Assert.False(result.Success);
            Assert.Contains("does not exist and schema.sync is off", result.Error, StringComparison.Ordinal);
            Assert.Equal(0, await RelationalIngestionHarness.StagingCountAsync(cs, flowId));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    [SkippableFact]
    public async Task SyncOn_WarnsAboutRetypedColumnsAndReportsDriftItDidNotActedOn()
    {
        const int flowId = 93;
        var cs = IntegrationDb.Require();
        const string src = "_SfDriftWarn_Src";
        const string trg = "_SfDriftWarn_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);
        await RelationalIngestionHarness.DropStagingAsync(cs, flowId);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([Id] int NOT NULL, [Name] varchar(100) NULL, [Amount] int NULL, [Added] int NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] ([Id],[Name],[Amount],[Added]) VALUES (1, 'Ann', 100, 5);");

        // [Name] is narrower than the source (a widening ALTER), [Amount] is a different type family (drift the
        // engine records but never acts on), [Added] is absent (a plain addition), and [Legacy] exists only here.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL, [Name] varchar(50) NULL, [Amount] nvarchar(50) NULL, [Legacy] int NULL);");

        try
        {
            var log = new RunLogger(RunLogLevel.Info);
            var result = await RelationalIngestionHarness.BuildRunner()
                .RunAsync(Flow(flowId, src, trg), new IngestionRunOptions { Events = log });

            Assert.True(result.Success, result.Error);

            var warnings = log.Entries.Where(e => e.Level == RunLogLevel.Warning).ToList();

            // The widening was applied, and it changed the type every consumer of [Name] reads, so it is a
            // warning rather than another line in a change count.
            var retyped = Assert.Single(warnings, w => w.Step == "target.evolve");
            Assert.Contains("re-typed", retyped.Message, StringComparison.Ordinal);
            Assert.Contains("[Name] varchar(50) -> varchar(100)", retyped.Message, StringComparison.Ordinal);

            // The type the engine deliberately did NOT change is reported too, instead of being decided in
            // silence on every run forever.
            var drift = Assert.Single(warnings, w => w.Step == "target.drift");
            Assert.Contains("[Amount]", drift.Message, StringComparison.Ordinal);

            // A plain addition stays informational, and a target-only column is the benign case: neither is a
            // warning, or the warnings would be noise nobody reads.
            Assert.Contains(log.Entries, e => e.Level == RunLogLevel.Info
                && e.Step == "target.evolve" && e.Message.Contains("[Added]", StringComparison.Ordinal));
            Assert.DoesNotContain(warnings, w => w.Message.Contains("Legacy", StringComparison.Ordinal));
            Assert.Equal("varchar(100)", await IntegrationDb.ColumnTypeAsync(cs, trg, "Name"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
            await RelationalIngestionHarness.DropStagingAsync(cs, flowId);
        }
    }

    private static IngestionFlow Flow(int flowId, string source, string target) => new()
    {
        FlowId = flowId,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = source } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = target } },
        Load = new IngestionLoadPolicy { KeyColumns = ["Id"] },
    };
}
