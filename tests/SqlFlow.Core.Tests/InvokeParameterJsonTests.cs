using SqlFlow.Azure.Invoke;
using SqlFlow.Core;
using Xunit;

namespace SqlFlow.Tests;

public sealed class InvokeParameterJsonTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_Yields_EmptyDictionaries(string? json)
    {
        Assert.Empty(InvokeParameterJson.ToDataFactoryParameters(json));
        Assert.Empty(InvokeParameterJson.ToAutomationParameters(json));
    }

    [Fact]
    public void DataFactory_KeepsRawJsonValues()
    {
        var result = InvokeParameterJson.ToDataFactoryParameters("""{"s":"hello","n":42,"b":true,"o":{"a":1}}""");

        Assert.Equal("\"hello\"", result["s"].ToString());   // a string stays JSON-quoted
        Assert.Equal("42", result["n"].ToString());
        Assert.Equal("true", result["b"].ToString());
        Assert.Equal("""{"a":1}""", result["o"].ToString());
    }

    [Fact]
    public void Automation_UnwrapsStrings_AndJsonEncodesTheRest()
    {
        var result = InvokeParameterJson.ToAutomationParameters("""{"s":"hello","n":42,"o":{"a":1}}""");

        Assert.Equal("hello", result["s"]);                  // a string is unwrapped (no quotes)
        Assert.Equal("42", result["n"]);
        Assert.Equal("""{"a":1}""", result["o"]);            // structured value passed as JSON text
    }

    [Fact]
    public void NonObjectJson_Throws()
    {
        Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToDataFactoryParameters("[1,2,3]"));
        Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToAutomationParameters("\"scalar\""));
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToDataFactoryParameters("{not json"));
        Assert.Throws<SqlFlowException>(() => InvokeParameterJson.ToAutomationParameters("{not json"));
    }
}
