using SqlFlow.Core;
using SqlFlow.Core.Connections;
using Xunit;

namespace SqlFlow.Tests;

public sealed class ConnectionRefParseTests
{
    [Fact]
    public void Alias_StripsSigil()
    {
        var reference = ConnectionRef.Parse("@AdventureWorksDW");
        Assert.Equal(ConnectionRefKind.Alias, reference.Kind);
        Assert.Equal("AdventureWorksDW", reference.Value);
    }

    [Theory]
    [InlineData("dwh")]
    [InlineData("a.b-c_1")]
    [InlineData("DW.Prod-01")]
    public void Alias_AcceptsIdentifierCharset(string alias)
    {
        var reference = ConnectionRef.Parse("@" + alias);
        Assert.Equal(ConnectionRefKind.Alias, reference.Kind);
        Assert.Equal(alias, reference.Value);
    }

    [Fact]
    public void Alias_TrimsSurroundingWhitespace()
    {
        var reference = ConnectionRef.Parse("   @dwh  ");
        Assert.Equal(ConnectionRefKind.Alias, reference.Kind);
        Assert.Equal("dwh", reference.Value);
    }

    [Fact]
    public void InlineConnectionString_IsInline()
    {
        const string raw = "Server=tcp:dw;Initial Catalog=Silver;Authentication=Active Directory Default";
        var reference = ConnectionRef.Parse(raw);
        Assert.Equal(ConnectionRefKind.Inline, reference.Kind);
        Assert.Equal(raw, reference.Value);
    }

    [Fact]
    public void SecretReference_IsInline()
        => Assert.Equal(ConnectionRefKind.Inline, ConnectionRef.Parse("${keyvault:kv-prod/dw-conn}").Kind);

    [Fact]
    public void AadUserInString_IsNotMistakenForAlias()
    {
        // '@' appears inside the value but not as the first character, so it is an inline string.
        const string raw = "Server=tcp:dw;User ID=svc@contoso.onmicrosoft.com;Authentication=Active Directory Password";
        var reference = ConnectionRef.Parse(raw);
        Assert.Equal(ConnectionRefKind.Inline, reference.Kind);
        Assert.Equal(raw, reference.Value);
    }

    [Theory]
    [InlineData("@bad alias")]   // whitespace
    [InlineData("@bad;drop")]    // semicolon
    [InlineData("@a=b")]         // equals
    [InlineData("@with/slash")]  // slash
    [InlineData("@")]            // empty alias after the sigil
    public void MalformedAlias_Throws(string raw)
        => Assert.Throws<SqlFlowException>(() => ConnectionRef.Parse(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Throws(string raw)
        => Assert.Throws<SqlFlowException>(() => ConnectionRef.Parse(raw));

    [Fact]
    public void Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => ConnectionRef.Parse(null!));
}
