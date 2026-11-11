using SqlFlow.Core;
using SqlFlow.Core.Query;
using SqlFlow.SqlServer.Query;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The read-only query runner against the physical sink. Its row-by-row read: a parameter is bound as a value and never
/// written into the statement, the rows come with the provider's own values (a long text untrimmed, a number as a number),
/// the caller stops the read when it has what it needs, and a statement that is not one SELECT is refused before anything
/// connects. Its run, which shares the read's execution: the row cap, the text each cell is rendered as, and the database.
/// </summary>
[Trait("Category", "Integration")]
public sealed class QueryRunnerReadIntegrationTests
{
    [SkippableFact]
    public async Task A_parameter_is_bound_as_a_value_and_the_rows_carry_the_providers_values()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_ReadOnlyRead_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);
        var longText = new string('x', 6000);
        try
        {
            await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{table}] (Name nvarchar(100) NOT NULL, Depth decimal(18, 4) NULL, Note nvarchar(max) NULL);");
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{table}] VALUES (N'it''s', 1250.4000, N'{longText}'), (N'other', NULL, NULL), (N'third', 7, NULL);");

            var rows = new List<object?[]>();
            IReadOnlyList<string> names = [];
            var handed = await SqlServerQueryRunner.ReadAsync(
                cs,
                new ReadOnlyRead
                {
                    Sql = $"SELECT Name, Depth, Note FROM [dbo].[{table}] WHERE Name = @name OR Name = @other ORDER BY Name",
                    Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { ["name"] = "it's", ["other"] = "other" },
                },
                (columns, record) =>
                {
                    names = columns.Select(c => c.Name).ToList();
                    var values = new object?[record.FieldCount];
                    for (var i = 0; i < record.FieldCount; i++)
                    {
                        values[i] = record.IsDBNull(i) ? null : record.GetValue(i);
                    }

                    rows.Add(values);
                    return true;
                },
                CancellationToken.None);

            Assert.Equal(2, handed);
            Assert.Equal(["Name", "Depth", "Note"], names);
            Assert.Equal("other", rows[1][0]);
            Assert.Null(rows[1][1]);
            Assert.Equal("it's", rows[0][0]);
            Assert.Equal(1250.4000m, Assert.IsType<decimal>(rows[0][1]));
            Assert.Equal(longText, rows[0][2]);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task The_caller_stops_the_read_when_it_has_what_it_needs()
    {
        var cs = IntegrationDb.Require();
        var seen = 0;
        var handed = await SqlServerQueryRunner.ReadAsync(
            cs,
            new ReadOnlyRead { Sql = "SELECT v FROM (VALUES (1), (2), (3), (4), (5), (6)) AS t(v) ORDER BY v" },
            (_, _) => ++seen < 3,
            CancellationToken.None);

        Assert.Equal(3, handed);
        Assert.Equal(3, seen);
    }

    [SkippableFact]
    public async Task A_run_keeps_its_cap_its_text_and_the_database_it_ran_in()
    {
        var cs = IntegrationDb.Require();
        var result = await SqlServerQueryRunner.RunAsync(
            cs,
            new QueryRunRequest
            {
                Sql = "SELECT v, CAST(v AS decimal(9, 2)) AS d, CAST('2026-01-02T03:04:05' AS datetime2) AS t FROM (VALUES (1), (2), (3)) AS x(v) ORDER BY v",
                MaxRows = 2,
            },
            CancellationToken.None);

        Assert.True(result.Truncated);
        Assert.Equal(2, result.RowCount);
        Assert.Equal(["v", "d", "t"], result.Columns.Select(c => c.Name));
        Assert.Equal(["1", "1.00", "2026-01-02T03:04:05.0000000"], result.Rows[0]);
        Assert.False(string.IsNullOrWhiteSpace(result.Database));
    }

    [Theory]
    [InlineData("DELETE FROM dbo.Anything")]
    [InlineData("SELECT 1; DROP TABLE dbo.Anything")]
    [InlineData("SELECT * INTO dbo.Copy FROM dbo.Anything")]
    public async Task A_statement_that_is_not_one_select_is_refused_before_anything_connects(string sql)
    {
        // The connection string names a server nothing answers on: a refusal that connected first would fail with a
        // SqlException instead.
        await Assert.ThrowsAsync<SqlFlowException>(() => SqlServerQueryRunner.ReadAsync(
            "Server=tcp:192.0.2.1,1;Database=None;Connect Timeout=1;TrustServerCertificate=True",
            new ReadOnlyRead { Sql = sql },
            (_, _) => true,
            CancellationToken.None));
    }

    [Theory]
    [InlineData("1st", 120)]
    [InlineData("a-b", 120)]
    [InlineData("@name", 120)]
    [InlineData("name", 0)]
    [InlineData("name", 3601)]
    public void A_parameter_name_or_a_timeout_out_of_bounds_is_refused(string name, int timeout)
    {
        var read = new ReadOnlyRead
        {
            Sql = "SELECT @name",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = "x" },
            TimeoutSeconds = timeout,
        };

        Assert.Throws<SqlFlowException>(() => read.Validate());
    }
}
