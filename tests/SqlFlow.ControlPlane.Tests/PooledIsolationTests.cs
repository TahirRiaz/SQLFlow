using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A unit at a raised isolation level hands its connection back to the pool at READ COMMITTED, whether it commits, fails,
/// or is rolled back by the server as a deadlock victim's is. SQL Server keeps a session's level when the pool lends the
/// connection again, so without this the next borrower's plain reads ran at the unit's level. Each test owns a pool of
/// one session (its own application name, at most one connection), so the next borrower is the unit's own session.
/// </summary>
public sealed class PooledIsolationTests
{
    // sys.dm_exec_sessions.transaction_isolation_level.
    private const short ReadCommitted = 2;
    private const short RepeatableRead = 3;
    private const short Serializable = 4;

    [SkippableTheory]
    [Trait("Category", "Integration")]
    [InlineData(IsolationLevel.Serializable, Serializable)]
    [InlineData(IsolationLevel.RepeatableRead, RepeatableRead)]
    public async Task A_unit_that_commits_hands_its_connection_back_at_read_committed(IsolationLevel level, short raised)
    {
        var cs = OneSessionPool();
        try
        {
            (short Session, short Level) inside;
            await using (var db = CatalogDatabase.Create(cs))
            {
                inside = await PooledIsolation.InTransactionAsync(db, level, () => SessionOfAsync(db), CancellationToken.None);
            }

            Assert.Equal(raised, inside.Level);
            Assert.Equal((inside.Session, ReadCommitted), await NextBorrowerAsync(cs));
        }
        finally
        {
            ClearPool(cs);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_unit_that_fails_hands_its_connection_back_at_read_committed()
    {
        var cs = OneSessionPool();
        try
        {
            short session = 0;
            await using (var db = CatalogDatabase.Create(cs))
            {
                var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => PooledIsolation.InTransactionAsync<int>(
                    db, IsolationLevel.Serializable, async () =>
                    {
                        (session, var level) = await SessionOfAsync(db);
                        Assert.Equal(Serializable, level);
                        throw new InvalidOperationException("the unit's own failure");
                    }, CancellationToken.None));
                Assert.Equal("the unit's own failure", failure.Message);
            }

            Assert.Equal((session, ReadCommitted), await NextBorrowerAsync(cs));
        }
        finally
        {
            ClearPool(cs);
        }
    }

    [SkippableFact]
    [Trait("Category", "Integration")]
    public async Task A_unit_the_server_rolled_back_hands_its_connection_back_at_read_committed()
    {
        var cs = OneSessionPool();
        try
        {
            short session = 0;
            await using (var db = CatalogDatabase.Create(cs))
            {
                // The server ends the transaction under the unit, as it ends a deadlock victim's, and the statement fails.
                await Assert.ThrowsAsync<SqlException>(() => PooledIsolation.InTransactionAsync<int>(
                    db, IsolationLevel.Serializable, async () =>
                    {
                        (session, _) = await SessionOfAsync(db);
                        return await db.Database.ExecuteSqlRawAsync("ROLLBACK; RAISERROR(N'the server rolled the unit back', 16, 1);");
                    }, CancellationToken.None));
            }

            Assert.Equal((session, ReadCommitted), await NextBorrowerAsync(cs));
        }
        finally
        {
            ClearPool(cs);
        }
    }

    [Theory]
    [InlineData(IsolationLevel.ReadCommitted)]
    [InlineData(IsolationLevel.Unspecified)]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadUncommitted)]
    public async Task A_level_that_cannot_be_lowered_inside_its_unit_is_refused(IsolationLevel level)
    {
        // Refused before anything connects, so the server named here is never reached.
        await using var db = CatalogDatabase.Create("Server=tcp:unreachable.invalid,1433;Database=catalog;User Id=u;Password=p;");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PooledIsolation.InTransactionAsync(db, level, () => Task.FromResult(0), CancellationToken.None));
    }

    private static string OneSessionPool()
        => new SqlConnectionStringBuilder(CatalogTestDb.Require())
        {
            ApplicationName = "pooled-isolation-" + Guid.NewGuid().ToString("N")[..12],
            Pooling = true,
            MaxPoolSize = 1,
        }.ConnectionString;

    private static async Task<(short Session, short Level)> SessionOfAsync(DbContext db)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = SessionQuery;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt16(0), reader.GetInt16(1));
    }

    private static async Task<(short Session, short Level)> NextBorrowerAsync(string cs)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(SessionQuery, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt16(0), reader.GetInt16(1));
    }

    private const string SessionQuery =
        "SELECT CAST(@@SPID AS smallint), transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID;";

    private static void ClearPool(string cs)
    {
        using var connection = new SqlConnection(cs);
        SqlConnection.ClearPool(connection);
    }
}
