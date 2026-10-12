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

    [Theory]
    // Every OPENROWSET form: the BULK one reads a file on the database server's own disk.
    [InlineData("SELECT * FROM OPENROWSET(BULK 'C:\\secrets.txt', SINGLE_CLOB) AS x", "BULK")]
    [InlineData("SELECT BulkColumn FROM OPENROWSET(BULK N'/var/opt/mssql/data/x.bak', SINGLE_BLOB) AS b", "BULK")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', 'Data Source=x').db.dbo.t", "ad-hoc")]
    [InlineData("SELECT * FROM OPENXML(@h, '/r')", "OPENXML")]
    // A sequence advanced from inside a SELECT is a write, wherever it appears.
    [InlineData("SELECT NEXT VALUE FOR dbo.OrderNumbers", "sequence")]
    [InlineData("SELECT o.Id FROM dbo.Orders o WHERE o.Id > NEXT VALUE FOR dbo.OrderNumbers", "sequence")]
    // The server's own catalog, by view, by table-valued function, and by scalar function.
    [InlineData("SELECT name FROM sys.columns", "system catalog")]
    [InlineData("SELECT name FROM [SYS].objects", "system catalog")]
    [InlineData("SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS", "system catalog")]
    [InlineData("SELECT name FROM master.sys.columns", "system catalog")]
    [InlineData("SELECT text FROM sys.dm_exec_sql_text(0x01)", "system catalog")]
    [InlineData("SELECT * FROM sys.fn_xe_file_target_read_file('x*.xel', NULL, NULL, NULL)", "system catalog")]
    [InlineData("SELECT sys.fn_varbintohexstr(0x01)", "system catalog")]
    // The metadata functions that hand back a column's name or a module's body.
    [InlineData("SELECT COL_NAME(OBJECT_ID('dbo.Employee'), 3)", "COL_NAME")]
    [InlineData("SELECT col_name(1, 1)", "col_name")]
    [InlineData("SELECT OBJECT_DEFINITION(OBJECT_ID('dbo.v_Employee'))", "OBJECT_DEFINITION")]
    [InlineData("SELECT INDEX_COL('dbo.Employee', 1, 1)", "INDEX_COL")]
    [InlineData("SELECT COLUMNPROPERTY(OBJECT_ID('dbo.Employee'), 'Ssn', 'IsIdentity')", "COLUMNPROPERTY")]
    [InlineData("SELECT COL_LENGTH('dbo.Employee', 'Ssn')", "COL_LENGTH")]
    // Extended-procedure-style names, as a scalar and as a table.
    [InlineData("SELECT * FROM fn_trace_gettable('x.trc', default)", "fn_trace_gettable")]
    [InlineData("SELECT dbo.xp_something(1)", "xp_something")]
    public void Refuses_TheReadsThatReachPastTheEstatesOwnData(string sql, string named)
    {
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
        Assert.Contains(named, ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    // A common table expression in front of a write is still the write.
    [InlineData("WITH c AS (SELECT 1 AS a) DELETE FROM edw.F")]
    [InlineData("WITH c AS (SELECT 1 AS a) UPDATE edw.F SET A = 1")]
    [InlineData("WITH c AS (SELECT 1 AS Id) MERGE edw.F AS t USING c AS s ON t.Id = s.Id WHEN MATCHED THEN DELETE;")]
    // A SELECT ... INTO in the first branch of a union.
    [InlineData("SELECT a INTO edw.Copy FROM edw.F UNION SELECT b FROM edw.G")]
    // A transaction around a SELECT is three statements.
    [InlineData("BEGIN TRAN; SELECT 1; COMMIT")]
    public void Refuses_AWriteDressedAsAQuery(string sql)
    {
        Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(sql));
    }

    [Theory]
    // A comment that reads like a second statement is a comment.
    [InlineData("SELECT 1 -- ; DROP TABLE edw.F")]
    [InlineData("SELECT 1 /* ; DROP TABLE edw.F */")]
    // A linked-server name reads a table like any other: the column guard decides what it may read.
    [InlineData("SELECT o.Id FROM srv.Sales.dbo.Orders o")]
    // The built-in table functions read nothing outside the query.
    [InlineData("SELECT j.[value] FROM OPENJSON('[1,2]') j")]
    [InlineData("SELECT s.value FROM STRING_SPLIT('a,b', ',') s")]
    // Window functions, hints and a user table-valued function are reads.
    [InlineData("SELECT RouteId, ROW_NUMBER() OVER (PARTITION BY RouteId ORDER BY Dato) AS n FROM edw.F")]
    [InlineData("SELECT * FROM edw.F WITH (NOLOCK)")]
    [InlineData("SELECT * FROM dbo.ufn_Orders(2025)")]
    // A name that only starts like a refused one is not refused.
    [InlineData("SELECT * FROM edw.SysLog")]
    [InlineData("SELECT ObjectName FROM edw.ColNames")]
    public void Accepts_ReadsThatOnlyLookUnusual(string sql)
    {
        Assert.Equal(sql.Trim(), ReadOnlyQueryGuard.Validate(sql));
    }

    [Fact]
    public void TheLengthLimit_IsMeasuredOnTheTrimmedStatement_AndIsInclusive()
    {
        var atLimit = "SELECT '" + new string('a', ReadOnlyQueryGuard.MaxLength - 9) + "'";
        Assert.Equal(ReadOnlyQueryGuard.MaxLength, atLimit.Length);
        Assert.Equal(atLimit, ReadOnlyQueryGuard.Validate("  \n" + atLimit + "\n  "));

        var overLimit = "SELECT '" + new string('a', ReadOnlyQueryGuard.MaxLength - 8) + "'";
        var ex = Assert.Throws<SqlFlowException>(() => ReadOnlyQueryGuard.Validate(overLimit));
        Assert.Contains($"{ReadOnlyQueryGuard.MaxLength + 1} characters", ex.Message, StringComparison.Ordinal);
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
