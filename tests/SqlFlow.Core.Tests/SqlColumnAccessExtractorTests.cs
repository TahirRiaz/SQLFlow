using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlFlow.SqlServer.Query;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The extractor <see cref="SqlColumnAccessExtractor"/> that <c>ColumnPolicyGuard</c> checks every ad-hoc SELECT
/// against before it may run. Its stated policy is fail-closed: a column reference it cannot bind to exactly one
/// table (an unqualified column, or a qualifier it never saw a matching alias for) is attributed to EVERY table
/// the statement reads rather than dropped. These tests pin that both the precise-binding and the fan-out cases
/// come out the way the guard depends on.
/// </summary>
public sealed class SqlColumnAccessExtractorTests
{
    private static SelectStatement Parse(string sql)
    {
        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out var errors);
        Assert.Empty(errors);
        var script = Assert.IsType<TSqlScript>(fragment);
        var statement = Assert.Single(script.Batches.SelectMany(b => b.Statements));
        return Assert.IsType<SelectStatement>(statement);
    }

    [Fact]
    public void QualifiedColumn_ResolvesToItsAliasedTable()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT e.Ssn FROM dbo.Employee e"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Employee", hit.Table.Name);
        Assert.Equal("dbo", hit.Table.Schema);
        Assert.Equal("Ssn", hit.Column);
    }

    [Fact]
    public void UnqualifiedColumn_FansOutToEveryTable()
    {
        // The join predicate's own qualified columns (d.Id, e.DepartmentId) are separately extracted and
        // resolve precisely, since a JOIN ON clause is read to run the query exactly like the select list.
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("SELECT Ssn FROM dbo.Employee e JOIN dbo.Department d ON d.Id = e.DepartmentId"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Department" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Department" && a.Column == "Id");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "DepartmentId");
        Assert.Equal(4, accesses.Count);
    }

    [Fact]
    public void UnresolvedQualifier_FansOutJustLikeNoQualifier()
    {
        // "x.Ssn" where no FROM/JOIN introduces an alias or table literally named "x": the guard must not treat
        // this as safely unrelated to Employee, so it falls back to the same fan-out an unqualified column gets.
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT x.Ssn FROM dbo.Employee e"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Employee", hit.Table.Name);
        Assert.Equal("Ssn", hit.Column);
    }

    [Fact]
    public void QualifiedByBareTableName_ResolvesEvenWithoutAnExplicitAlias()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT Employee.Ssn FROM dbo.Employee"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Employee", hit.Table.Name);
        Assert.Equal("Ssn", hit.Column);
    }

    [Fact]
    public void UnqualifiedStar_IsRecordedAsAllColumnsOfEveryTable()
    {
        // A plain "FROM dbo.Employee e" with no join predicate keeps this fixture free of the extra qualified
        // column accesses a JOIN ON clause would add, so only the star's own fan-out is under test here.
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT * FROM dbo.Employee e, dbo.Department d"));

        Assert.Equal(2, accesses.Count);
        Assert.All(accesses, a => Assert.Null(a.Column));
        Assert.Contains(accesses, a => a.Table.Name == "Employee");
        Assert.Contains(accesses, a => a.Table.Name == "Department");
    }

    [Fact]
    public void QualifiedStar_IsRecordedAsAllColumnsOfOnlyThatTable()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT e.* FROM dbo.Employee e, dbo.Department d"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Employee", hit.Table.Name);
        Assert.Null(hit.Column);
    }

    [Fact]
    public void NoTableReferenced_ExtractsNoAccesses()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT 1 AS Answer"));

        Assert.Empty(accesses);
    }

    [Fact]
    public void AnAliasReusedAtAnotherNestingLevel_IsAttributedToEveryTableItNames()
    {
        // The outer e is Customer and the inner e is Employee; the outer e.Email must reach Customer's policy, so a
        // reused alias is read as either table rather than whichever was seen last.
        var accesses = SqlColumnAccessExtractor.Extract(Parse(
            "SELECT e.Email FROM dbo.Customer e WHERE EXISTS (SELECT 1 FROM dbo.Employee e WHERE e.Id = 1)"));

        Assert.Contains(accesses, a => a.Table.Name == "Customer" && a.Column == "Email");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Email");
        Assert.Contains(accesses, a => a.Table.Name == "Customer" && a.Column == "Id");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Id");
    }

    [Fact]
    public void AMethodCalledOnAColumn_ReadsThatColumn()
    {
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("SELECT e.Doc.value('(/a)[1]', 'int') AS A FROM dbo.Employee e"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Employee", hit.Table.Name);
        Assert.Equal("Doc", hit.Column);
    }

    [Fact]
    public void AnUnqualifiedMethodCall_FansOutLikeAnUnqualifiedColumn()
    {
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("SELECT Doc.query('/a') FROM dbo.Employee e, dbo.Department d"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Doc");
        Assert.Contains(accesses, a => a.Table.Name == "Department" && a.Column == "Doc");
    }

    [Fact]
    public void ASchemaQualifiedFunctionCall_IsReadAsTheColumnItsTargetMayBe()
    {
        // dbo.fn(e.Id) and Doc.value(...) parse alike, so the target is read as a column: no policy allows a column
        // named after a schema, and the call fails closed.
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT dbo.fn(e.Id) FROM dbo.Employee e"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "dbo");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Id");
    }

    [Theory]
    [InlineData("SELECT $IDENTITY FROM dbo.Employee", SqlColumnAccessExtractor.IdentityPseudoColumn)]
    [InlineData("SELECT e.$IDENTITY FROM dbo.Employee e", SqlColumnAccessExtractor.IdentityPseudoColumn)]
    [InlineData("SELECT IDENTITYCOL FROM dbo.Employee", SqlColumnAccessExtractor.IdentityPseudoColumn)]
    [InlineData("SELECT $ROWGUID FROM dbo.Employee", SqlColumnAccessExtractor.RowGuidPseudoColumn)]
    [InlineData("SELECT ROWGUIDCOL FROM dbo.Employee", SqlColumnAccessExtractor.RowGuidPseudoColumn)]
    [InlineData("SELECT $node_id FROM dbo.Employee", "$GRAPHNODEID")]
    public void APseudoColumn_IsReportedUnderItsOwnName_NotDropped(string sql, string column)
    {
        var hit = Assert.Single(SqlColumnAccessExtractor.Extract(Parse(sql)));

        Assert.Equal("Employee", hit.Table.Name);
        Assert.Equal(column, hit.Column);
    }

    [Fact]
    public void ATableValuedFunction_IsATableItReads()
    {
        var parsed = Parse("SELECT o.Total, * FROM dbo.ufn_Orders(2025) o");

        Assert.Equal([new AccessedTable("dbo", "ufn_Orders")], SqlColumnAccessExtractor.ExtractTables(parsed));
        var accesses = SqlColumnAccessExtractor.Extract(parsed);
        Assert.Contains(accesses, a => a.Table.Name == "ufn_Orders" && a.Column == "Total");
        Assert.Contains(accesses, a => a.Table.Name == "ufn_Orders" && a.Column is null);
    }

    [Fact]
    public void ACommonTableExpression_ReportsItsBodysColumns_AndItsOwnName()
    {
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("WITH c AS (SELECT Ssn FROM dbo.Employee) SELECT * FROM c"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column is null);
        Assert.Contains(accesses, a => a.Table == new AccessedTable(null, "c") && a.Column is null);
    }

    [Fact]
    public void ADerivedTablesColumns_ReachTheTableItReads()
    {
        // x is not a named table, so x.Name fans out to Employee as well, where the policy decides; the inner Ssn is
        // read from Employee directly.
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("SELECT x.Name FROM (SELECT Ssn AS Name FROM dbo.Employee) x"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Name");
    }

    [Theory]
    [InlineData("SELECT e.Ssn FROM dbo.Employee e UNION SELECT c.Code FROM dbo.Customer c")]
    [InlineData("SELECT e.Ssn FROM dbo.Employee e EXCEPT SELECT c.Code FROM dbo.Customer c")]
    [InlineData("SELECT e.Ssn FROM dbo.Employee e INTERSECT SELECT c.Code FROM dbo.Customer c")]
    public void EverySideOfASetOperation_IsRead(string sql)
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse(sql));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Customer" && a.Column == "Code");
    }

    [Fact]
    public void AColumnOfAnApplyFunction_FansOutToTheTablesRead()
    {
        var accesses = SqlColumnAccessExtractor.Extract(
            Parse("SELECT j.[value] FROM dbo.Employee e CROSS APPLY OPENJSON(e.Data) j"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Data");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "value");
    }

    [Fact]
    public void AWindowsPartitionAndOrder_AreRead()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse(
            "SELECT ROW_NUMBER() OVER (PARTITION BY e.Ssn ORDER BY e.Hired) AS n FROM dbo.Employee e"));

        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Ssn");
        Assert.Contains(accesses, a => a.Table.Name == "Employee" && a.Column == "Hired");
    }

    [Fact]
    public void AnOrderByOrdinal_ReadsNoColumn()
    {
        var accesses = SqlColumnAccessExtractor.Extract(Parse("SELECT e.Name FROM dbo.Employee e ORDER BY 1"));

        var hit = Assert.Single(accesses);
        Assert.Equal("Name", hit.Column);
    }

    [Theory]
    [InlineData("SELECT [e].[Ssn] FROM [dbo].[Employee] AS [e]")]
    [InlineData("SELECT \"e\".\"Ssn\" FROM \"dbo\".\"Employee\" AS \"e\"")]
    [InlineData("SELECT E.Ssn FROM dbo.Employee e")]
    [InlineData("SELECT dbo.Employee.Ssn FROM dbo.Employee")]
    [InlineData("SELECT e.Ssn FROM LinkedSrv.HrDb.dbo.Employee e")]
    public void QuotingCaseAndMultiPartNames_ResolveToTheSameTable(string sql)
    {
        var hit = Assert.Single(SqlColumnAccessExtractor.Extract(Parse(sql)));

        Assert.Equal(new AccessedTable("dbo", "Employee"), hit.Table);
        Assert.Equal("Ssn", hit.Column);
    }

    [Fact]
    public void ExtractTables_ListsATableReadTwiceOnce()
    {
        var tables = SqlColumnAccessExtractor.ExtractTables(
            Parse("SELECT a.Id FROM dbo.Employee a JOIN dbo.Employee b ON b.ManagerId = a.Id"));

        Assert.Equal([new AccessedTable("dbo", "Employee")], tables);
    }

    [Fact]
    public void ExtractReferences_KeepsEachReferencesCandidates_Together()
    {
        var references = SqlColumnAccessExtractor.ExtractReferences(
            Parse("SELECT e.Ssn, Code FROM dbo.Employee e, dbo.Customer c"));

        var qualified = Assert.Single(references, r => r.Column == "Ssn");
        Assert.Equal([new AccessedTable("dbo", "Employee")], qualified.Tables);
        var unqualified = Assert.Single(references, r => r.Column == "Code");
        Assert.Equal(2, unqualified.Tables.Count);
    }

    [Theory]
    [InlineData("SELECT dbo.fn(1)", new[] { "dbo" })]
    [InlineData("SELECT dbo.fn(1), sales.g(2)", new[] { "dbo", "sales" })]
    [InlineData("SELECT 1", new string[0])]
    [InlineData("SELECT GETDATE(), 'x' AS y", new string[0])]
    [InlineData("SELECT dbo.fn(1) FROM dbo.Employee", new string[0])]
    public void ExtractUnattributed_NamesWhatIsReadWhileNoTableIs(string sql, string[] expected)
    {
        Assert.Equal(expected, SqlColumnAccessExtractor.ExtractUnattributed(Parse(sql)));
    }
}
