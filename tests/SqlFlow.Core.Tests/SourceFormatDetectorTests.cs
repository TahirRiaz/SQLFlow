using System.Text;
using SqlFlow.Sources;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// Tests for <see cref="SourceFormatDetector"/>, focused on delimiter sniffing. A tabular file resolved from an
/// explicit format or a <c>.csv</c> extension never reaches the content detector, so the delimiter must be profiled
/// from the data itself; without it a semicolon/tab/pipe file reads as one comma-delimited column (the exact defect
/// this covers). Comma stays the reader's default (a null option), and a single-column file reports no delimiter.
/// </summary>
public sealed class SourceFormatDetectorTests
{
    private static (string? Option, string Evidence) Sniff(string text)
        => SourceFormatDetector.DetectCsvDelimiter(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void DetectsSemicolonDelimiter()
    {
        var (option, evidence) = Sniff("A;B;C\r\n1;2;3\r\n4;5;6\r\n");

        Assert.Equal(";", option);
        Assert.Contains("';'", evidence);
    }

    [Fact]
    public void DetectsTabDelimiter()
    {
        var (option, evidence) = Sniff("A\tB\tC\n1\t2\t3\n4\t5\t6\n");

        Assert.Equal("\t", option);
        Assert.Contains("tab", evidence);
    }

    [Fact]
    public void DetectsPipeDelimiter()
    {
        var (option, _) = Sniff("A|B|C\n1|2|3\n");

        Assert.Equal("|", option);
    }

    [Fact]
    public void CommaMapsToTheReaderDefault()
    {
        // Comma is the reader's own default, so a comma file yields a null option (nothing to pin) yet still
        // reports the delimiter it found in the evidence.
        var (option, evidence) = Sniff("A,B,C\n1,2,3\n");

        Assert.Null(option);
        Assert.Contains("field", evidence);
    }

    [Fact]
    public void SingleColumnFileReportsNoDelimiter()
    {
        var (option, evidence) = Sniff("HEADER\r\nvalue1\r\nvalue2\r\n");

        Assert.Null(option);
        Assert.Contains("single column", evidence);
    }

    [Fact]
    public void SemicolonWinsOverIncidentalCommas()
    {
        // A semicolon-delimited file that also carries a stray comma inside one field must still resolve to the
        // consistent semicolon, not the incidental comma, so the columns are not collapsed.
        var (option, _) = Sniff("NAME;NOTE;CODE\r\nAcme;a, b;10\r\nBeta;none;20\r\n");

        Assert.Equal(";", option);
    }
}
