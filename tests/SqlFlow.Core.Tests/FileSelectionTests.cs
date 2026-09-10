using SqlFlow.Core.Files;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The shared file-selection semantics: the default pattern per type, glob-vs-glob overlap (so two wildcard
/// patterns link without either naming a literal file), and the path-first-then-file <c>Feeds</c> decision that
/// connects a file producer (an invoke that lands files) to a file consumer (a file ingestion) with engine parity.
/// </summary>
public sealed class FileSelectionTests
{
    [Theory]
    [InlineData("csv", "*.csv")]
    [InlineData("json", "*.json")]
    [InlineData("parquet", "*.parquet")]
    [InlineData("xlsx", "*.xlsx")]
    [InlineData("", "*")]
    [InlineData(null, "*")]
    public void DefaultPattern_MirrorsReaderDefaults(string? type, string expected)
        => Assert.Equal(expected, FileSelection.DefaultPattern(type));

    [Theory]
    [InlineData("*.csv", "orders.csv", true)]
    [InlineData("orders_*.csv", "*.csv", true)]
    [InlineData("orders_*.csv", "*.parquet", false)]
    [InlineData("a?b", "axb", true)]
    [InlineData("a?b", "ab", false)]        // '?' requires exactly one character
    [InlineData("*", "anything.txt", true)]
    [InlineData("data-*.json", "*-2026.json", true)]   // common: data-2026.json
    [InlineData("foo*.csv", "bar*.csv", false)]        // disjoint literal prefixes
    [InlineData("*.csv", "*.json", false)]
    [InlineData("ORDERS_*.CSV", "orders_2026.csv", true)]  // case-insensitive
    [InlineData("", "", true)]
    [InlineData("", "*", true)]
    [InlineData("", "x", false)]
    public void GlobsOverlap_DecidesSharedMatch(string a, string b, bool expected)
        => Assert.Equal(expected, FileSelection.GlobsOverlap(a, b));

    [Fact]
    public void GlobsOverlap_IsSymmetric()
    {
        Assert.Equal(FileSelection.GlobsOverlap("a*z", "*mz"), FileSelection.GlobsOverlap("*mz", "a*z"));
        Assert.True(FileSelection.GlobsOverlap("a*z", "*mz"));   // amz
    }

    private static FileSelectionSpec Consumer(string location, string? type = "csv", string? glob = null, string? mask = null)
        => new() { Location = location, Type = type, Glob = glob, Mask = mask };

    private static FileSelectionSpec Producer(string location, string? glob = null, string? mask = null)
        => new() { Location = location, Glob = glob, Mask = mask };

    [Fact]
    public void Feeds_SameFolder_GlobOverlaps()
        => Assert.True(FileSelection.Feeds(
            Producer("raw/orders", glob: "orders_*.csv"),
            Consumer("raw/orders")));   // consumer default *.csv

    [Fact]
    public void Feeds_ProducerInSubfolder_OfWatchedRoot()
        => Assert.True(FileSelection.Feeds(
            Producer("raw/orders/orders_20260714.csv"),
            Consumer("raw")));

    [Fact]
    public void Feeds_DifferentFolder_DoesNotLink()
        => Assert.False(FileSelection.Feeds(
            Producer("raw/invoices", glob: "orders_*.csv"),
            Consumer("raw/orders")));

    [Fact]
    public void Feeds_SiblingFolderPrefix_DoesNotLink()   // raw/orders must not match raw/orders2
        => Assert.False(FileSelection.Feeds(
            Producer("raw/orders2/x.csv"),
            Consumer("raw/orders")));

    [Fact]
    public void Feeds_FolderAligned_ButFileTypeMismatch_DoesNotLink()
        => Assert.False(FileSelection.Feeds(
            Producer("raw/orders/data.parquet"),
            Consumer("raw/orders")));   // consumer reads *.csv

    [Fact]
    public void Feeds_ConsumerGlob_ConstrainsFileNames()
    {
        Assert.True(FileSelection.Feeds(
            Producer("raw/orders/orders_1.csv"),
            Consumer("raw/orders", glob: "orders_*.csv")));
        Assert.False(FileSelection.Feeds(
            Producer("raw/orders/invoices_1.csv"),
            Consumer("raw/orders", glob: "orders_*.csv")));
    }

    [Fact]
    public void Feeds_PathMask_TakesPriorityOverFolder()
    {
        var consumer = Consumer("raw", mask: ".*/orders/.*");
        Assert.True(FileSelection.Feeds(Producer("raw/orders", glob: "*.csv"), consumer));
        Assert.False(FileSelection.Feeds(Producer("raw/invoices", glob: "*.csv"), consumer));
    }

    [Fact]
    public void Feeds_CloudUrls_MatchByPathThenName()
        => Assert.True(FileSelection.Feeds(
            Producer("abfss://raw@acct.dfs.core.windows.net/orders/orders_20260714.csv"),
            Consumer("abfss://raw@acct.dfs.core.windows.net/orders")));

    [Fact]
    public void Feeds_AzureSubfolder_UnderParentWatch_AcrossUriShapes()   // abfss drop beneath an https-watched folder
        => Assert.True(FileSelection.Feeds(
            Producer("abfss://datalakev2@acct.dfs.core.windows.net/raw/src/history/orders"),
            Consumer("https://acct.dfs.core.windows.net/datalakev2/raw/src/history/", glob: "*.csv")));

    [Fact]
    public void Feeds_AzureSiblingContainerPath_AcrossUriShapes_DoesNotLink()   // history2 must not match history
        => Assert.False(FileSelection.Feeds(
            Producer("abfss://datalakev2@acct.dfs.core.windows.net/raw/src/history2/orders"),
            Consumer("https://acct.dfs.core.windows.net/datalakev2/raw/src/history/", glob: "*.csv")));

    [Fact]
    public void Feeds_UnresolvedReference_ComparedVerbatim()
    {
        Assert.True(FileSelection.Feeds(
            Producer("${env:RAW}/orders", glob: "*.csv"),
            Consumer("${env:RAW}/orders")));
        Assert.False(FileSelection.Feeds(
            Producer("${env:RAW}/orders", glob: "*.csv"),
            Consumer("${env:OTHER}/orders")));
    }
}
