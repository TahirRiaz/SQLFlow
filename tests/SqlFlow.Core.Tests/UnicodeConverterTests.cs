using SqlFlow.SqlServer.Schema;
using Xunit;

namespace SqlFlow.Tests;

public sealed class UnicodeConverterTests
{
    [Theory]
    [InlineData("nvarchar(50)", "varchar(50)")]
    [InlineData("nvarchar(max)", "varchar(max)")]
    [InlineData("nchar(10)", "char(10)")]
    [InlineData("ntext", "text")]
    [InlineData("sysname", "varchar(128)")]
    [InlineData("varchar(50)", "varchar(50)")] // already non-unicode, unchanged
    [InlineData("int", "int")]                 // non-text, unchanged
    public void ToNonUnicode_ConvertsTextTypes(string input, string expected)
        => Assert.Equal(expected, UnicodeConverter.ToNonUnicode(SqlDataType.Parse(input)).Render());
}
