using SqlFlow.Core;
using SqlFlow.Core.Comparison;
using Xunit;

namespace SqlFlow.Tests.Catalog;

/// <summary>
/// The trust boundary for the SQL fragments a baseline comparison accepts. A logical-key expression and a row
/// filter cannot be parameters (they are projected and grouped, not compared to a value), so they are
/// interpolated into generated SQL, and this guard is the only thing standing between an operator's text and
/// the engine. Every refusal proved here is a way a fragment could otherwise have stopped being an expression
/// and become a statement.
/// </summary>
public sealed class SqlFragmentGuardTests
{
    [Theory]
    [InlineData("Dato")]
    [InlineData("[Order Date]")]
    [InlineData("CAST(Klokkeslett AS time)")]
    [InlineData("TRY_CONVERT(datetime, Dato, 126)")]
    [InlineData("ISNULL(Sted, '(ukjent)')")]
    [InlineData("CONVERT(varchar(10), Dato, 126)")]
    [InlineData("LEFT(RegNr, 7)")]
    [InlineData("CASE WHEN Type = 1 THEN 'inn' ELSE 'ut' END")]
    [InlineData("DATEADD(hour, -1, PassingTime)")]
    [InlineData("CAST(Belop AS decimal(18,2))")]
    [InlineData("dbo.Something")]
    [InlineData("Antall >= 1e6")]
    [InlineData("Navn LIKE 'A%' ESCAPE '\\'")]
    [InlineData("Dato >= '2025-01-01' AND Dato < '2026-01-01'")]
    public void Validate_AcceptsTheExpressionsARealKeyOrFilterIsMadeOf(string fragment)
    {
        Assert.Equal(fragment, SqlFragmentGuard.Validate(fragment, "key"));
    }

    [Theory]
    // Ending the expression and starting a statement is the whole attack; every one of these is refused for a
    // different reason, so no single missed check opens the door.
    [InlineData("Dato; DROP TABLE arc.Sales")]
    [InlineData("Dato -- ignore the rest")]
    [InlineData("Dato /* ignore the rest */")]
    [InlineData("(SELECT TOP 1 password FROM sys.sql_logins)")]
    [InlineData("Dato FROM arc.Other")]
    [InlineData("Dato UNION ALL SELECT 1")]
    [InlineData("@variable")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("OPENQUERY(X, 'SELECT 1')")]
    [InlineData("OPENROWSET('SQLNCLI', 'x', 'SELECT 1')")]
    [InlineData("HOST_NAME()")]
    [InlineData("DB_NAME()")]
    [InlineData("SUSER_SNAME()")]
    [InlineData("DECLARE x int")]
    [InlineData("WAITFOR DELAY '00:00:10'")]
    [InlineData("\"Dato\"")]
    public void Validate_RefusesAnythingThatCouldStopBeingAnExpression(string fragment)
    {
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate(fragment, "key"));
    }

    [Fact]
    public void Validate_NamesTheRefusedFunction_SoAnOperatorLearnsWhatIsAllowed()
    {
        var ex = Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate("SUSER_SNAME()", "keyExpressions"));
        Assert.Contains("SUSER_SNAME", ex.Message, StringComparison.Ordinal);
        Assert.Contains("keyExpressions", ex.Message, StringComparison.Ordinal);
        Assert.Contains("CAST", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RefusesAnUnterminatedStringLiteral_RatherThanSwallowingTheRestOfTheStatement()
    {
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate("Sted = 'oslo", "where"));
    }

    [Fact]
    public void Validate_AcceptsAnEscapedQuoteInsideALiteral()
    {
        const string fragment = "Sted = 'O''Brien'";
        Assert.Equal(fragment, SqlFragmentGuard.Validate(fragment, "where"));
    }

    [Fact]
    public void Validate_RefusesUnbalancedParentheses()
    {
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate("CAST(Dato AS date", "key"));
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate("Dato)", "key"));
    }

    [Fact]
    public void Validate_RefusesAFragmentLongerThanTheLimit()
    {
        var fragment = new string('a', SqlFragmentGuard.MaxLength + 1);
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.Validate(fragment, "key"));
    }

    [Fact]
    public void ValidateList_SplitsOnTopLevelCommasOnly_SoACastSurvives()
    {
        var parts = SqlFragmentGuard.ValidateList("Dato, Sted, CAST(Klokkeslett AS time)", "keyExpressions", 8);
        Assert.Equal(["Dato", "Sted", "CAST(Klokkeslett AS time)"], parts);
    }

    [Fact]
    public void ValidateList_DoesNotSplitOnACommaInsideALiteral()
    {
        var parts = SqlFragmentGuard.ValidateList("ISNULL(Sted, 'a,b'), Dato", "keyExpressions", 8);
        Assert.Equal(["ISNULL(Sted, 'a,b')", "Dato"], parts);
    }

    [Fact]
    public void ValidateList_RefusesMoreItemsThanTheCallerAllows()
    {
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.ValidateList("a, b, c", "keyExpressions", 2));
    }

    [Theory]
    [InlineData("OLDPROD")]
    [InlineData("old-dwh-prod")]
    [InlineData("dw-dwh-prod")]
    [InlineData("Order Details")]
    public void ValidateIdentifier_AcceptsTheNamesARealEstateUses(string identifier)
    {
        Assert.Equal(identifier, SqlFragmentGuard.ValidateIdentifier(identifier, "linkedServer"));
    }

    [Theory]
    // The caller passes the BARE name and the builder adds the quoting, so a name carrying its own bracket or
    // quote (which could close the quoting early) is refused rather than escaped.
    [InlineData("[OLDPROD]")]
    [InlineData("OLD]PROD")]
    [InlineData("OLD'PROD")]
    [InlineData("OLD;PROD")]
    [InlineData("OLD.PROD")]
    public void ValidateIdentifier_RefusesANameThatCarriesItsOwnQuoting(string identifier)
    {
        Assert.Throws<SqlFlowException>(() => SqlFragmentGuard.ValidateIdentifier(identifier, "linkedServer"));
    }

    [Fact]
    public void ValidateIdentifier_RefusesANameOverTheSqlServerLimit()
    {
        Assert.Throws<SqlFlowException>(
            () => SqlFragmentGuard.ValidateIdentifier(new string('a', 129), "objectName"));
    }
}
