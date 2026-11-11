using SqlFlow.Yaml;
using Xunit;
using YamlDotNet.Serialization;

namespace SqlFlow.Tests;

/// <summary>
/// An unquoted decimal in a value of any shape keeps what the document wrote: the unquoted-scalar option read it as a
/// single-precision float, which moved a value past about seven significant digits, and widened 0.1 to 0.100000001.
/// </summary>
public sealed class ExactDecimalTests
{
    private static readonly IDeserializer Reader = new DeserializerBuilder()
        .WithAttemptingUnquotedStringTypeDeserialization()
        .WithExactDecimals()
        .Build();

    private static object? Read(string scalar) => Reader.Deserialize<Dictionary<string, object?>>($"value: {scalar}\n")["value"];

    [Theory]
    [InlineData("2128.7232", 2128.7232)]
    [InlineData("0.1", 0.1)]
    [InlineData("-0.5", -0.5)]
    [InlineData("+1.5", 1.5)]
    [InlineData(".5", 0.5)]
    [InlineData("3.", 3.0)]
    [InlineData("1.5e3", 1500.0)]
    [InlineData("12345678.901234", 12345678.901234)]
    public void AnUnquotedDecimal_ReadsInDoublePrecision(string scalar, double expected)
        => Assert.Equal(expected, Assert.IsType<double>(Read(scalar)));

    [Fact]
    public void AnUnquotedDecimal_WritesBackAsTheDocumentWroteIt()
        => Assert.Equal("2128.7232", Assert.IsType<double>(Read("2128.7232")).ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("\"2128.7232\"", "2128.7232")]
    [InlineData("'0.1'", "0.1")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v1.5", "v1.5")]
    public void WhatIsNotAnUnquotedDecimal_StaysText(string scalar, string expected)
        => Assert.Equal(expected, Read(scalar));

    [Fact]
    public void AnIntegerAndABoolean_KeepTheirTypes()
    {
        Assert.IsNotType<double>(Read("42"));
        Assert.Equal(42L, Convert.ToInt64(Read("42"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(true, Read("true"));
    }

    [Fact]
    public void ATypedProperty_ReadsAsItsTypeSays()
        => Assert.Equal(1.5f, Reader.Deserialize<Dictionary<string, float>>("value: 1.5\n")["value"]);

    [Fact]
    public void AnEmptyValue_IsNull()
        => Assert.Null(Read("~"));

    [Fact]
    public void AValueNestedInALibraryOfAnyShape_ReadsTheSameWay()
    {
        var read = Reader.Deserialize<Dictionary<string, object?>>("a:\n  b: [ 2128.7232, { c: 0.1 } ]\n");

        var list = Assert.IsType<List<object>>(Assert.IsType<Dictionary<object, object>>(read["a"])["b"]);
        Assert.Equal(2128.7232, Assert.IsType<double>(list[0]));
        Assert.Equal(0.1, Assert.IsType<double>(Assert.IsType<Dictionary<object, object>>(list[1])["c"]));
    }
}
