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
}
