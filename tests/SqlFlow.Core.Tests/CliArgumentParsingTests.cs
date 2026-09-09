using SqlFlow.Cli;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Guards the CLI argument parser: a value-taking option must consume its value so the value never leaks into
/// the positional-argument list (the bug that had --join-separator and --xml missing from the value-taking set),
/// while a plain flag must not swallow the token after it.
/// </summary>
public sealed class CliArgumentParsingTests
{
    [Theory]
    [InlineData(new[] { "flatten", "folder", "--xml", "//a/b" }, new[] { "flatten", "folder" })]
    [InlineData(new[] { "flatten", "folder", "--join-separator", "|" }, new[] { "flatten", "folder" })]
    [InlineData(new[] { "run", "pipe.yaml", "--out", "dir" }, new[] { "run", "pipe.yaml" })]
    [InlineData(new[] { "flatten", "folder", "--xml", "//a/b", "--join-separator", ";" }, new[] { "flatten", "folder" })]
    public void ValueTakingOption_DoesNotLeakItsValueIntoPositionals(string[] args, string[] expected)
        => Assert.Equal(expected, Program.PositionalArguments(args));

    [Fact]
    public void PlainFlag_DoesNotSwallowTheNextToken()
    {
        // --up is a boolean flag, not value-taking: the token after it is a real positional.
        Assert.Equal(["of", "value"], Program.PositionalArguments(["of", "--up", "value"]));
    }

    [Fact]
    public void JoinSeparatorAndXml_AreRegisteredValueTaking()
    {
        Assert.Contains("--join-separator", Program.ValueTakingOptions);
        Assert.Contains("--xml", Program.ValueTakingOptions);
    }

    [Fact]
    public void MapOption_BindsTheValueFollowingTheFlag()
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Program.MapOption(options, ["--xml", "//root/item"], "--xml", "xmlPaths");
        Assert.Equal("//root/item", options["xmlPaths"]);
    }

    [Fact]
    public void MapOption_AbsentFlag_LeavesOptionUnset()
    {
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        Program.MapOption(options, ["run", "pipe.yaml"], "--xml", "xmlPaths");
        Assert.DoesNotContain("xmlPaths", options.Keys);
    }
}
