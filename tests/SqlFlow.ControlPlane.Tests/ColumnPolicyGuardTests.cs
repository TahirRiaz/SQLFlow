using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Api;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// <see cref="ColumnPolicyGuard"/> against a real catalog: the statement shapes that could read a column outside the
/// allow-list without naming it the way the guard reads names. Each case seeds two catalogued tables under names of its
/// own (an employee table whose <c>Ssn</c> is not allowed, and a customer table whose <c>Email</c> is not), checks one
/// statement, and removes what it seeded.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ColumnPolicyGuardTests
{
    [SkippableTheory]
    // What the allow-list exists to let through.
    [InlineData("SELECT e.Name, e.Id FROM dbo.{E} e")]
    [InlineData("SELECT Name FROM dbo.{E}")]
    [InlineData("SELECT c.Code FROM dbo.{C} c JOIN dbo.{E} e ON e.Id = c.Id")]
    [InlineData("SELECT COUNT(*) FROM dbo.{E}")]
    // A CTE named as a CTE, read by its outputs.
    [InlineData("WITH x AS (SELECT e.Name FROM dbo.{E} e) SELECT x.Name FROM x")]
    [InlineData("WITH x (Who) AS (SELECT e.Name FROM dbo.{E} e) SELECT x.Who FROM x")]
    [InlineData("WITH x AS (SELECT e.Name, e.Id FROM dbo.{E} e), y AS (SELECT x.* FROM x) SELECT y.Name FROM y")]
    [InlineData("WITH x AS (SELECT e.Name AS N FROM dbo.{E} e UNION SELECT c.Code FROM dbo.{C} c) SELECT x.N FROM x")]
    public async Task Allows_AStatementThatReadsOnlyAllowedColumns(string template)
    {
        await WithEstateAsync(async (db, e, c) =>
            await ColumnPolicyGuard.EnsureAllowedAsync(db, Fill(template, e, c), CancellationToken.None));
    }

    [SkippableTheory]
    // The column itself, directly and through a star.
    [InlineData("SELECT e.Ssn FROM dbo.{E} e")]
    [InlineData("SELECT * FROM dbo.{E}")]
    // A CTE named like the real table: an unqualified CTE name is exempt, a schema-qualified reference never is.
    [InlineData("WITH {E} AS (SELECT 1 AS x) SELECT Ssn FROM dbo.{E}")]
    [InlineData("WITH {E} AS (SELECT e.Ssn FROM dbo.{E} e) SELECT * FROM {E}")]
    // An alias reused in a subquery: the outer e is the customer table, whose Email is not allowed.
    [InlineData("SELECT e.Email FROM dbo.{C} e WHERE EXISTS (SELECT 1 FROM dbo.{E} e WHERE e.Id = 1)")]
    // A call made on a column reads that column.
    [InlineData("SELECT e.Ssn.ToString() FROM dbo.{E} e")]
    // A pseudo-column names a column without writing its name.
    [InlineData("SELECT $IDENTITY FROM dbo.{E}")]
    // A function the allow-list cannot see into: on a schema, with or without a table read, or only over CTEs.
    [InlineData("SELECT dbo.fn(e.Name) FROM dbo.{E} e")]
    [InlineData("SELECT dbo.fn(1)")]
    [InlineData("WITH x AS (SELECT 1 AS v) SELECT dbo.fn(x.v) FROM x")]
    [InlineData("WITH x AS (SELECT 1 AS v) SELECT x.nope FROM x")]
    // An object the catalog has no record of, whether or not a column of it is named.
    [InlineData("SELECT COUNT(*) FROM dbo.NotCatalogued_{E}")]
    [InlineData("SELECT * FROM dbo.ufn_{E}()")]
    // Fail closed: an unqualified star is read against every table the statement reads, whichever scope it is in.
    [InlineData("WITH x AS (SELECT e.Name FROM dbo.{E} e), y AS (SELECT * FROM x) SELECT y.Name FROM y")]
    // A raw filter in the task composition is checked like any statement.
    [InlineData("SELECT e.Name FROM dbo.{E} e WHERE 1 = 1 UNION SELECT e2.Ssn FROM dbo.{E} e2")]
    public async Task Refuses_AStatementThatCouldReadAColumnOutsideTheAllowList(string template)
    {
        await WithEstateAsync(async (db, e, c) =>
            await Assert.ThrowsAsync<SqlFlowException>(
                () => ColumnPolicyGuard.EnsureAllowedAsync(db, Fill(template, e, c), CancellationToken.None)));
    }

    [SkippableFact]
    public async Task ARefusal_NamesWhatWasRefused()
    {
        await WithEstateAsync(async (db, e, _) =>
        {
            var column = await Assert.ThrowsAsync<SqlFlowException>(
                () => ColumnPolicyGuard.EnsureAllowedAsync(db, $"SELECT e.Ssn FROM dbo.{e} e", CancellationToken.None));
            Assert.Contains($"{e}.Ssn", column.Message, StringComparison.Ordinal);

            var unattributed = await Assert.ThrowsAsync<SqlFlowException>(
                () => ColumnPolicyGuard.EnsureAllowedAsync(db, "SELECT dbo.fn(1)", CancellationToken.None));
            Assert.Contains("'dbo'", unattributed.Message, StringComparison.Ordinal);

            var unknown = await Assert.ThrowsAsync<SqlFlowException>(
                () => ColumnPolicyGuard.EnsureAllowedAsync(db, $"SELECT COUNT(*) FROM dbo.Missing_{e}", CancellationToken.None));
            Assert.Contains($"Missing_{e}", unknown.Message, StringComparison.Ordinal);
        });
    }

    [SkippableFact]
    public async Task AStatementThatDoesNotParseAsOneSelect_IsLeftToTheReadOnlyGuard()
    {
        // The read-only guard has already refused anything that is not exactly one SELECT; this check is layered on it.
        await WithEstateAsync(async (db, _, _) =>
            await ColumnPolicyGuard.EnsureAllowedAsync(db, "SELECT FROM WHERE", CancellationToken.None));
    }

    private static string Fill(string template, string employee, string customer)
        => template.Replace("{E}", employee, StringComparison.Ordinal).Replace("{C}", customer, StringComparison.Ordinal);

    /// <summary>Seeds the two tables and their policies under names of their own, runs <paramref name="check"/>, and
    /// removes every row it seeded.</summary>
    private static async Task WithEstateAsync(Func<CatalogDbContext, string, string, Task> check)
    {
        var cs = CatalogTestDb.Require();
        await CatalogDatabase.MigrateAsync(cs);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var serverRef = "${env:SQLFLOW_GUARD_" + suffix + "}";
        var employee = "Employee_" + suffix;
        var customer = "Customer_" + suffix;
        var employeeKey = $"{serverRef}|dw|dbo|{employee.ToLowerInvariant()}";
        var customerKey = $"{serverRef}|dw|dbo|{customer.ToLowerInvariant()}";
        var now = DateTime.UtcNow;

        try
        {
            await using (var seed = CatalogDatabase.Create(cs))
            {
                Table(seed, employeeKey, serverRef, employee, now, ("Id", true), ("Name", true), ("Ssn", false));
                Table(seed, customerKey, serverRef, customer, now, ("Id", true), ("Code", true), ("Email", false));
                await seed.SaveChangesAsync();
            }

            await using var db = CatalogDatabase.Create(cs);
            await check(db, employee, customer);
        }
        finally
        {
            await using var cleanup = CatalogDatabase.Create(cs);
            string[] keys = [employeeKey, customerKey];
            await cleanup.ColumnPolicies.Where(p => keys.Contains(p.ObjectKey)).ExecuteDeleteAsync();
            await cleanup.ObjectColumns.Where(c => keys.Contains(c.ObjectKey)).ExecuteDeleteAsync();
            await cleanup.Objects.Where(o => keys.Contains(o.Key)).ExecuteDeleteAsync();
        }
    }

    private static void Table(
        CatalogDbContext db, string key, string serverRef, string name, DateTime now, params (string Column, bool Allowed)[] columns)
    {
        db.Objects.Add(new CatalogObject
        {
            Key = key, ServerRef = serverRef, Database = "dw", Schema = "dbo", Name = name, Kind = "Table",
            FirstSeenUtc = now, LastSeenUtc = now,
        });
        for (var i = 0; i < columns.Length; i++)
        {
            db.ObjectColumns.Add(new CatalogObjectColumn
            {
                ObjectKey = key, Ordinal = i + 1, Name = columns[i].Column, DataType = "nvarchar(100)", Nullable = false,
                Tier = "Observed",
            });
            if (columns[i].Allowed)
            {
                db.ColumnPolicies.Add(new CatalogColumnPolicy
                {
                    ObjectKey = key, ColumnName = columns[i].Column, IsAllowed = true, UpdatedUtc = now,
                });
            }
        }
    }
}
