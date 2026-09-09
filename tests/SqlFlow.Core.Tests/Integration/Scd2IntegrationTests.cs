using SqlFlow.Core.Ingestion;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The headline SCD Type 2 scenario end to end against the real sink: a dimension is first loaded WITHOUT
/// versioning (so the target exists and is populated with no period columns), then SCD2 is turned ON and the
/// flow re-run with a changed attribute and a brand-new key. This proves the requirement legacy could not meet:
/// enabling dimension history on an already-created, already-populated table. Dynamic schema evolution adds the
/// period columns; the backfill stamps the pre-existing rows as the current version; the change opens a new
/// version while closing the old one; the unchanged row is left alone. Skips when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class Scd2IntegrationTests
{
    private static IngestionFlow BuildFlow(string src, string trg, bool scd2) => new()
    {
        // Unique per test class: staging tables are named [raw].[<schema>_<table>_<flowId>] and harness cleanup
        // drops by the flow-id suffix, so classes sharing a flow id drop each other's live staging when xunit
        // runs them in parallel.
        FlowId = 9102,
        SysAlias = trg,
        Source = new IngestionSource { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = src } },
        Target = new IngestionTarget { Server = "sink", Table = new RelationalObject { Database = "db", Schema = "dbo", Name = trg } },
        Load = new IngestionLoadPolicy { KeyColumns = ["CustomerId"] },
        SystemColumns = new SystemColumnsPolicy { InsertedDate = true, UpdatedDate = true },
        Versioning = new VersioningPolicy { Scd2 = new Scd2Policy { Enabled = scd2 } },
    };

    [SkippableFact]
    public async Task Scd2_CanBeEnabledOnAnExistingPopulatedTable_AndVersionsChanges()
    {
        var cs = IntegrationDb.Require();
        var src = "_SfScd2_Src";
        var trg = "_SfScd2_Trg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [Name] nvarchar(50) NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann','Oslo'),(2,'Bob','Bergen');");

        try
        {
            var runner = RelationalIngestionHarness.BuildRunner();

            // Pass 1: load WITHOUT SCD2. The target is created and populated; it has NO period columns.
            var first = await runner.RunAsync(BuildFlow(src, trg, scd2: false));
            Assert.True(first.Success, first.Error);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.False(await IntegrationDb.ColumnExistsAsync(cs, trg, "ValidTo_DW"));

            // The source changes: customer 1 moves city (a tracked-attribute change), customer 3 is new.
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [City] = 'Stockholm' WHERE CustomerId = 1;");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (3,'Cy','Tromso');");

            // Pass 2: turn SCD2 ON and re-run. Schema evolution must add the period columns to the existing
            // populated table, the pre-existing rows must be backfilled current, and the change must version.
            var second = await runner.RunAsync(BuildFlow(src, trg, scd2: true));
            Assert.True(second.Success, second.Error);

            // The period columns were added by dynamic schema evolution (the legacy limitation, fixed).
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "ValidFrom_DW"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "ValidTo_DW"));
            Assert.True(await IntegrationDb.ColumnExistsAsync(cs, trg, "IsCurrent_DW"));

            // Four rows total: customer 2 (unchanged, 1), customer 1 (old + new, 2), customer 3 (new, 1).
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));

            // Exactly one current row per business key.
            Assert.Equal(3, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [IsCurrent_DW] = 1"));

            // Customer 1: two versions, the current one is Stockholm, the closed one is Oslo with a real ValidTo.
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustomerId] = 1"));
            Assert.Equal("Stockholm", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 1 AND [IsCurrent_DW] = 1"));
            Assert.Equal("Oslo", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 1 AND [IsCurrent_DW] = 0"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustomerId] = 1 AND [IsCurrent_DW] = 0 AND [ValidTo_DW] < '9999-12-31'"));

            // Customer 2: unchanged, so a single current row backfilled from the pre-SCD2 load (no new version).
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustomerId] = 2"));
            Assert.Equal("Bergen", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 2 AND [IsCurrent_DW] = 1"));

            // Customer 3: the new key is a single current row.
            Assert.Equal("Tromso", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 3 AND [IsCurrent_DW] = 1"));

            // Re-running with no source change must be a no-op: no new versions, still one current per key.
            var third = await runner.RunAsync(BuildFlow(src, trg, scd2: true));
            Assert.True(third.Success, third.Error);
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(3, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [IsCurrent_DW] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }

    [SkippableFact]
    public async Task Scd2_MigratesANonCanonicalUniqueKeyIndex_OnAnExistingTable()
    {
        var cs = IntegrationDb.Require();
        var src = "_SfScd2_MigSrc";
        var trg = "_SfScd2_MigTrg";

        await IntegrationDb.DropTableAsync(cs, src);
        await IntegrationDb.DropTableAsync(cs, trg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{src}] ([CustomerId] int NOT NULL, [Name] nvarchar(50) NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{src}] VALUES (1,'Ann','Oslo');");

        // A hand-built dimension that predates SCD2, carrying a UNIQUE index on the natural key under a
        // non-canonical name. Enabling SCD2 must migrate (drop) it, or the second version would be blocked.
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([CustomerId] int NOT NULL, [Name] nvarchar(50) NULL, [City] nvarchar(50) NULL);");
        await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] VALUES (1,'Ann','Oslo');");
        await IntegrationDb.ExecuteAsync(cs, $"CREATE UNIQUE NONCLUSTERED INDEX [UX_NaturalKey] ON [dbo].[{trg}] ([CustomerId]);");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"UPDATE [dbo].[{src}] SET [City] = 'Stockholm' WHERE CustomerId = 1;");

            var result = await RelationalIngestionHarness.BuildRunner().RunAsync(BuildFlow(src, trg, scd2: true));
            Assert.True(result.Success, result.Error);

            // The non-canonical unique index was dropped; the filtered canonical one replaced it.
            Assert.False(await IntegrationDb.IndexExistsAsync(cs, trg, "UX_NaturalKey"));
            Assert.True(await IntegrationDb.IndexExistsAsync(cs, trg, "NCI_KeyColumn"));

            // The change versioned: two rows for the key, current is Stockholm.
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [CustomerId] = 1"));
            Assert.Equal("Stockholm", await IntegrationDb.ScalarAsync<string?>(cs,
                $"SELECT [City] FROM [dbo].[{trg}] WHERE [CustomerId] = 1 AND [IsCurrent_DW] = 1"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, src);
            await IntegrationDb.DropTableAsync(cs, trg);
        }
    }
}
