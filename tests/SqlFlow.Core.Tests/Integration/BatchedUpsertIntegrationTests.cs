using System.Globalization;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Proves the key-windowed apply (BatchToAvoidLockEscalation) against the real sink on the input that broke it:
/// staging that carries a business key more than once. The target enforces one row per key with a UNIQUE index,
/// exactly as the ported arc tables do, so a windowed INSERT that adds a key twice fails loudly here instead of
/// in production. The batched and unbatched applies are also run side by side against identical targets and must
/// reach the identical final state, including for a NULL business key, which the batched path used to drop on
/// the way back from its key table. These tests skip when the sink is unreachable.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BatchedUpsertIntegrationTests
{
    private static RelationalObject Obj(string name) => new() { Database = "db", Schema = "dbo", Name = name };

    private static UpsertOptions KeyedOptions(bool batch, IReadOnlyList<string> data, IReadOnlyList<string> keys)
        => new()
        {
            DataColumns = data,
            KeyColumns = keys,
            BatchToAvoidLockEscalation = batch,

            // One key per window: every window boundary is exercised, and a key that reaches the key table
            // twice lands in two different windows, which is how the duplicate first escaped the dedup.
            BatchRowCount = 1,
            InsertedDateColumn = "InsertedDate_DW",
            UpdatedDateColumn = "UpdatedDate_DW",
        };

    private static async Task<(long Inserted, long Updated)> ApplyAsync(string cs, IReadOnlyList<UpsertStatement> statements)
    {
        long inserted = 0, updated = 0;
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new SqlCommand(statement.Sql, connection) { CommandTimeout = 0 };
            long affected;
            if (statement.CountFromScalar)
            {
                var scalar = await command.ExecuteScalarAsync();
                affected = scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
            }
            else
            {
                affected = await command.ExecuteNonQueryAsync();
            }

            if (statement.Kind == UpsertStatementKind.Update)
            {
                updated += affected;
            }
            else
            {
                inserted += affected;
            }
        }

        return (inserted, updated);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateStagingKeys_WriteOneRowPerKey_UnderAUniqueIndex(bool batch)
    {
        var cs = IntegrationDb.Require();
        var trg = $"_SfDupKey_Trg_{(batch ? "B" : "N")}";
        var stg = $"_SfDupKey_Stg_{(batch ? "B" : "N")}";
        await IntegrationDb.DropTableAsync(cs, trg);
        await IntegrationDb.DropTableAsync(cs, stg);

        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{trg}] ([Pk] int IDENTITY(1,1) NOT NULL PRIMARY KEY, [Id] int NOT NULL, [Name] nvarchar(50) NULL, " +
            "[Amount] int NULL, [InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON [dbo].[{trg}] ([Id]);");
        await IntegrationDb.ExecuteAsync(cs,
            $"CREATE TABLE [dbo].[{stg}] ([Id] int NOT NULL, [Name] nvarchar(50) NULL, [Amount] int NULL);");

        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Id],[Name],[Amount]) VALUES (1, 'Ann', 100);");

            // Key 1 matches the target twice over (the UPDATE branch), keys 2 and 3 are new and key 2 is staged
            // three times (the INSERT branch). Every duplicate is a byte-identical row, which is what a source
            // table with no unique constraint of its own actually produces.
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([Id],[Name],[Amount]) VALUES " +
                "(1, 'Ann', 200), (1, 'Ann', 200), (2, 'Bob', 300), (2, 'Bob', 300), (2, 'Bob', 300), (3, 'Cid', 400);");

            var statements = UpsertGenerator.GenerateStatements(
                Obj(trg), Obj(stg), KeyedOptions(batch, ["Id", "Name", "Amount"], ["Id"]));

            var (inserted, updated) = await ApplyAsync(cs, statements);

            // One row per key, so the unique index holds and the counts report rows written, not staging matches.
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
            Assert.Equal(2, inserted);
            Assert.Equal(1, updated);
            Assert.Equal(200, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 1"));
            Assert.Equal(300, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 2"));
            Assert.Equal(400, await IntegrationDb.ScalarAsync<int?>(cs, $"SELECT [Amount] FROM [dbo].[{trg}] WHERE [Id] = 3"));

            // Re-running the same staging is a no-op: every key now matches and nothing changed.
            var (insertedAgain, updatedAgain) = await ApplyAsync(cs, statements);
            Assert.Equal(0, insertedAgain);
            Assert.Equal(0, updatedAgain);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, trg);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }

    [SkippableFact]
    public async Task BatchedAndUnbatched_ReachTheSameState_WithANullBusinessKey()
    {
        var cs = IntegrationDb.Require();
        const string batched = "_SfNullKey_Trg_B";
        const string unbatched = "_SfNullKey_Trg_N";
        const string stg = "_SfNullKey_Stg";
        await IntegrationDb.DropTableAsync(cs, batched);
        await IntegrationDb.DropTableAsync(cs, unbatched);
        await IntegrationDb.DropTableAsync(cs, stg);

        foreach (var trg in new[] { batched, unbatched })
        {
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [dbo].[{trg}] ([Code] nvarchar(20) NULL, [Name] nvarchar(50) NULL, " +
                "[InsertedDate_DW] datetime2(3) NULL, [UpdatedDate_DW] datetime2(3) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{trg}] ([Code],[Name]) VALUES ('A', 'old');");
        }

        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{stg}] ([Code] nvarchar(20) NULL, [Name] nvarchar(50) NULL);");

        try
        {
            // The NULL-keyed row matches nothing in the target (a plain `=` never matches NULL), so it is an
            // insert on both paths. The batched path has to find it again in staging through its key table.
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [dbo].[{stg}] ([Code],[Name]) VALUES (NULL, 'nil'), ('A', 'new'), ('B', 'bee');");

            var options = KeyedOptions(batch: false, ["Code", "Name"], ["Code"]);
            var (insertedN, updatedN) = await ApplyAsync(cs, UpsertGenerator.GenerateStatements(Obj(unbatched), Obj(stg), options));
            var (insertedB, updatedB) = await ApplyAsync(cs,
                UpsertGenerator.GenerateStatements(Obj(batched), Obj(stg), options with { BatchToAvoidLockEscalation = true }));

            Assert.Equal(insertedN, insertedB);
            Assert.Equal(updatedN, updatedB);
            Assert.Equal(2, insertedB);
            Assert.Equal(1, updatedB);

            foreach (var trg in new[] { batched, unbatched })
            {
                Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, trg));
                Assert.Equal(1, await IntegrationDb.ScalarAsync<int?>(cs,
                    $"SELECT COUNT(*) FROM [dbo].[{trg}] WHERE [Code] IS NULL AND [Name] = 'nil'"));
                Assert.Equal("new", await IntegrationDb.ScalarAsync<string?>(cs,
                    $"SELECT [Name] FROM [dbo].[{trg}] WHERE [Code] = 'A'"));
            }
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, batched);
            await IntegrationDb.DropTableAsync(cs, unbatched);
            await IntegrationDb.DropTableAsync(cs, stg);
        }
    }
}
