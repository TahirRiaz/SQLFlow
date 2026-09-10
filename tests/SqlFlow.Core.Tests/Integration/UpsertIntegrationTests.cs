using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the two-step staging-to-target upsert against the real sink: it updates a changed row, inserts
/// a new row, stamps the system date columns on the right branch, and (on a re-run with unchanged staging)
/// detects no change and rewrites nothing.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UpsertIntegrationTests
{
    [SkippableFact]
    public async Task Upsert_UpdatesChanged_InsertsNew_AndReRunIsNoOp()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfUpsert_Trg";
        const string stg = "_SfUpsert_Stg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL PRIMARY KEY, [Name] nvarchar(50) NULL, [Amount] int NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{stg}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 100);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{stg}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 200), (2, 'Bob', 300);");

            var options = new UpsertOptions
            {
                DataColumns = ["Id", "Name", "Amount"],
                KeyColumns = ["Id"],
                InsertedDateColumn = "InsertedDate_DW",
                UpdatedDateColumn = "UpdatedDate_DW",
            };
            var target = new RelationalObject { Database = "db", Schema = "dbo", Name = trg };
            var staging = new RelationalObject { Database = "db", Schema = "dbo", Name = stg };
            var statements = UpsertGenerator.Generate(target, staging, options);

            foreach (var statement in statements)
            {
                await IntegrationDb.ExecuteAsync(cs, statement);
            }

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(200, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal("Bob", await IntegrationDb.ScalarAsync<string?>(cs, $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 1 AND [UpdatedDate_DW] IS NOT NULL"));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Id] = 2 AND [InsertedDate_DW] IS NOT NULL"));

            // Re-run with unchanged staging: the checksum matches (no UPDATE) and the rows exist (no INSERT).
            var updatedBefore = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");
            foreach (var statement in statements)
            {
                await IntegrationDb.ExecuteAsync(cs, statement);
            }

            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, trg));
            var updatedAfter = await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1");
            Assert.Equal(updatedBefore, updatedAfter);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    /// <summary>
    /// A target pre-created with a shape of its own (the migrated-source pattern: schema sync off, staging
    /// carrying the source's types) must still be compared on the values it STORES. Row 1 is identical and must
    /// not be rewritten even though its three columns render differently on the two sides; row 2 differs by
    /// seconds, which the datetime rendering hides unless the comparison is explicitly lossless; row 3 differs
    /// in the last float digit, which the default six-digit rendering hides.
    /// </summary>
    [SkippableFact]
    public async Task Upsert_ComparesTheValuesTheTargetStores()
    {
        var cs = IntegrationDb.Require();
        const string trg = "_SfUpsertTypes_Trg";
        const string stg = "_SfUpsertTypes_Stg";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Id] int NOT NULL PRIMARY KEY, [Uuid] uniqueidentifier NULL, [Stamp] datetime NULL, [Val] float(53) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{stg}] ([Id] int NOT NULL, [Uuid] nchar(36) NULL, [Stamp] datetime2(0) NULL, [Val] float(53) NULL);");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"""
                INSERT INTO [dbo].[{trg}] ([Id],[Uuid],[Stamp],[Val]) VALUES
                  (1, '2828ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:09', 0.123456789012345),
                  (2, '3928ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:09', 1.5),
                  (3, '4028ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:09', 0.123456789012345);
                """);
            await IntegrationDb.ExecuteAsync(cs, $"""
                INSERT INTO [dbo].[{stg}] ([Id],[Uuid],[Stamp],[Val]) VALUES
                  (1, N'2828ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:09', 0.123456789012345),
                  (2, N'3928ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:11', 1.5),
                  (3, N'4028ca9b-fd72-11ea-80a9-42010a1b3007', '2024-05-06 07:08:09', 0.123456789012346);
                """);

            var options = new UpsertOptions
            {
                DataColumns = ["Id", "Uuid", "Stamp", "Val"],
                KeyColumns = ["Id"],
                UpdatedDateColumn = "UpdatedDate_DW",
                ChecksumColumnTypes = new Dictionary<string, ChecksumColumnType>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Uuid"] = new() { Staging = SqlDataType.Parse("nchar(36)"), Target = SqlDataType.Parse("uniqueidentifier") },
                    ["Stamp"] = new() { Staging = SqlDataType.Parse("datetime2(0)"), Target = SqlDataType.Parse("datetime") },
                    ["Val"] = new() { Staging = SqlDataType.Parse("float(53)"), Target = SqlDataType.Parse("float(53)") },
                },
            };
            var target = new RelationalObject { Database = "db", Schema = "dbo", Name = trg };
            var staging = new RelationalObject { Database = "db", Schema = "dbo", Name = stg };

            foreach (var statement in UpsertGenerator.Generate(target, staging, options))
            {
                await IntegrationDb.ExecuteAsync(cs, statement);
            }

            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(2, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [UpdatedDate_DW] IS NOT NULL"));
            Assert.Null(await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [UpdatedDate_DW] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal(new DateTime(2024, 5, 6, 7, 8, 11), await IntegrationDb.ScalarAsync<DateTime?>(cs, $"SELECT [Stamp] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal(0.123456789012346, await IntegrationDb.ScalarAsync<double?>(cs, $"SELECT [Val] FROM [dbo].[{trg}] WHERE [Id] = 3"));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }
}
