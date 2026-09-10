using SqlFlow.Core;
using SqlFlow.SqlServer.Query;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The gate that decides whether an ad-hoc business query is genuinely read-only. It PARSES the statement
/// rather than matching text, and these tests exist to prove that distinction is real: every refusal below is
/// a way a string-matching denylist could be walked past with casing, comments, whitespace, or nesting.
///
/// The bias is deliberate and one-directional. A refused query that was safe costs someone a rewrite; an
/// accepted query that was not costs data.
/// </summary>
public sealed class ReadOnlyQueryGuardTests
{
    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM edw.FerryPassengers_PerDeparture")]
    [InlineData("SELECT TOP 10 * FROM edw.Trips ORDER BY Dato DESC")]
    [InlineData("SELECT RouteNumber, SUM(Boarding) AS Total FROM edw.F GROUP BY RouteNumber HAVING SUM(Boarding) > 0")]
    [InlineData("SELECT COUNT(DISTINCT TripKey) FROM edw.F WHERE RouteNumber = '5200'")]
    [InlineData("WITH d AS (SELECT MAX(Dato) AS m FROM edw.F) SELECT * FROM edw.F JOIN d ON edw.F.Dato = d.m")]
    [InlineData("SELECT f.*, r.Name FROM edw.F f LEFT JOIN edw.Dim_Route r ON r.RouteId = f.RouteId")]
    [InlineData("SELECT * FROM edw.F WHERE Dato BETWEEN '2025-01-01' AND '2025-12-31'")]
    [InlineData("SELECT * FROM (SELECT * FROM edw.F) x")]
    [InlineData("SELECT * FROM edw.F WHERE Id IN (SELECT Id FROM edw.G)")]
    [InlineData("SELECT * FROM edw.F ORDER BY Dato OFFSET 10 ROWS FETCH NEXT 20 ROWS ONLY")]
    [InlineData("SELECT * FROM edw.F UNION ALL SELECT * FROM edw.G")]
    [InlineData("SELECT * FROM edw.F; -- a trailing comment is still one statement")]
    public void Accepts_TheShapesARealBusinessQuestionTakes(string sql)
    {
        Assert.Equal(sql.Trim(), ReadOnlyQueryGuard.Validate(sql));
    }

    [Theory]
    // Anything that writes, at the top level.
    [InlineData("DELETE FROM edw.F")]
    [InlineData("UPDATE edw.F SET Boarding = 0")]
    [InlineData("INSERT INTO edw.F (Id) VALUES (1)")]
    [InlineData("TRUNCATE TABLE edw.F")]
    [InlineData("DROP TABLE edw.F")]
    [InlineData("CREATE TABLE x (a int)")]
    [InlineData("ALTER TABLE edw.F ADD b int")]
    [InlineData("MERGE edw.F AS t USING edw.G AS s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET t.A = s.A;")]
    [InlineData("EXEC sp_who")]
    [InlineData("EXECUTE dbo.SomeProc")]
    // Batch and statement smuggling: the classic way past a naive "starts with SELECT" check.
    [InlineData("SELECT 1; DROP TABLE edw.F")]
    [InlineData("SELECT 1\nGO\nDROP TABLE edw.F")]
    // A SELECT that writes.
    [InlineData("SELECT * INTO edw.Copy FROM edw.F")]
    // Reaching outside the connection from inside a SELECT.
    [InlineData("SELECT * FROM OPENQUERY([OLDPROD], 'DELETE FROM arc.X')")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=x;', 'SELECT 1')")]
    // Session and transaction control.
    [InlineData("USE master")]
    [InlineData("DECLARE @x int")]
    [InlineData("SET NOCOUNT ON")]
    [InlineData("BEGIN TRANSACTION")]
    [InlineData("WAITFOR DELAY '00:00:10'")]
    [InlineData("DBCC CHECKDB")]
    public void Refuses_EverythingThatIsNotASingleReadOnlySelect(string sql)
    {
        Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
    }

    [Theory]
    // Casing and whitespace are exactly what a text denylist loses to. The parser does not care.
    [InlineData("select 1; dRoP tAbLe edw.F")]
    [InlineData("SELECT 1;\r\n\t  DELETE   FROM   edw.F")]
    [InlineData("SELECT 1; /* a comment */ DELETE FROM edw.F")]
    [InlineData("SELECT 1 /* inline */ ; DELETE FROM edw.F")]
    public void Refuses_SmuggledStatements_WhateverTheCasingOrSpacing(string sql)
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
        Assert.Contains("ONE statement", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_NamesTheStatementKind_SoTheCallerCanRewrite()
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate("DELETE FROM edw.F"));
        Assert.Contains("DELETE", ex.Message, StringComparison.Ordinal);
        Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refusal_ExplainsSelectInto_BecauseItLooksLikeASelect()
    {
        var ex = Assert.Throws<SqlFlowException>(
            () => ReadOnlyQueryGuard.Validate("SELECT * INTO edw.Copy FROM edw.F"));
        Assert.Contains("INTO", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_AStatementThatDoesNotParse_RatherThanSendingItOn()
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate("SELECT FROM WHERE"));
        Assert.Contains("does not parse", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Refuses_AnEmptyQuery()
    {
        Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate("   "));
    }

    [Fact]
    public void Refuses_AQueryOverTheLengthLimit()
    {
        var sql = "SELECT '" + new string('a', ReadOnlyQueryGuard.MaxLength) + "'";
        Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
    }

    [Fact]
    public void IsReadOnly_ReportsTheReason_WithoutThrowing()
    {
        Assert.True(ReadOnlyQueryGuard.IsReadOnly("SELECT 1", out var none));
        Assert.Null(none);

        Assert.False(ReadOnlyQueryGuard.IsReadOnly("DELETE FROM edw.F", out var refusal));
        Assert.NotNull(refusal);
        Assert.Contains("DELETE", refusal, StringComparison.Ordinal);
    }
}
