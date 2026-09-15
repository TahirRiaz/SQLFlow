using SqlFlow.Cli;
using SqlFlow.Cli.Hosting;
using SqlFlow.Core.Hosting;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A host names its product through <see cref="ProductBranding"/>; SQLFlow's own text is the default, and the CLI banner
/// reads the branding's tagline.
/// </summary>
public sealed class ProductBrandingTests
{
    [Fact]
    public void SqlFlowsBranding_IsTheDefaultText()
    {
        var branding = ProductBranding.SqlFlow;

        Assert.Equal("SQLFlow", branding.ProductName);
        Assert.Equal("SQLFlow", branding.Lockup);
        Assert.Equal("metadata-driven ETL for SQL Server", branding.CliTagline);
        Assert.True(branding.IsSqlFlow);
        Assert.False(new ProductBranding("SQLFlow", "SQLFlow", "metadata-driven ETL for SQL Server").IsSqlFlow);
    }

    [Fact]
    public void AHostsBranding_UsesTheLockupAsTheCliTagline_UnlessItNamesOne()
    {
        var withoutTagline = new ProductBranding("Acme Data", "Acme Data, powered by SQLFlow");
        var withTagline = new ProductBranding("Acme Data", "Acme Data, powered by SQLFlow", "the Acme Data command line, powered by SQLFlow");

        Assert.Equal("Acme Data, powered by SQLFlow", withoutTagline.CliTagline);
        Assert.Equal("the Acme Data command line, powered by SQLFlow", withTagline.CliTagline);
    }

    [Theory]
    [InlineData("", "lockup", null, "productName")]
    [InlineData("   ", "lockup", null, "productName")]
    [InlineData(" Acme", "lockup", null, "productName")]
    [InlineData("Acme", "two\nlines", null, "lockup")]
    [InlineData("Acme", "lockup", "tab\there", "cliTagline")]
    public void AnInvalidValue_IsRefused_NamingTheParameter(string productName, string lockup, string? cliTagline, string parameter)
    {
        var error = Assert.Throws<ArgumentException>(() => new ProductBranding(productName, lockup, cliTagline));

        Assert.Equal(parameter, error.ParamName);
    }

    [Fact]
    public void AValueLongerThanTheLimit_IsRefused()
        => Assert.Throws<ArgumentException>(() => new ProductBranding(new string('a', ProductBranding.MaxLength + 1), "lockup"));

    [Fact]
    public void TheCliBanner_ReadsTheBrandingsTagline()
    {
        var branded = Program.UsageText(new CliModuleSet([], new ProductBranding("Acme Data", "Acme Data, powered by SQLFlow")));
        var plain = Program.UsageText(CliModuleSet.Empty);

        Assert.StartsWith("sqlflow - Acme Data, powered by SQLFlow" + Environment.NewLine, branded, StringComparison.Ordinal);
        Assert.StartsWith("sqlflow - metadata-driven ETL for SQL Server" + Environment.NewLine, plain, StringComparison.Ordinal);
        Assert.Equal(
            plain["sqlflow - metadata-driven ETL for SQL Server".Length..],
            branded["sqlflow - Acme Data, powered by SQLFlow".Length..]);
    }
}
