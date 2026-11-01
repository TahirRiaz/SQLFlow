using SqlFlow.Catalog;
using SqlFlow.ControlPlane.Background;
using SqlFlow.Core.Runs;
using SqlFlow.Yaml;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// What each member of a backfilled group runs with. The case that matters is the production shape copy, then file,
/// then ing, where the file flow dates its files from their name: re-landing does not change that date, so at defaults
/// the file flow reads only what is newer than its watermark and the backfill stops after the copy.
/// </summary>
public sealed class BackfillRoutingTests
{
    private const string NameDatedFlow = """
        name: orders_01_csv
        source:
          type: csv
          location: https://lake.dfs.core.windows.net/data/orders/
          options:
            srcFile: "orders*.csv"
            fileDate.from: "name"
            fileDate.pattern: '(?<year>\d{4})(?<month>\d{2})(?<day>\d{2})'
        target:
          connection: Server=.;Database=dw
          schema: pre
          table: orders
        incremental:
          dateColumn: FileDate_DW
        """;

    private static readonly YamlDocumentLoader Documents = YamlDocumentLoader.CreateDefault();

    private static readonly RunParameters Window = new()
    {
        BackfillFrom = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc),
        BackfillTo = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
    };

    private static readonly RunScopeMember[] Source =
    [
        new("copy", "cpy", 0, "copy"),
        new("named", "file", 1, "orders"),
        new("modified", "file", 1, "orders"),
        new("silver", "ing", 2, "orders"),
        new("proc", "sp", 3, "orders"),
    ];

    private static readonly HashSet<string> NamedOnly = new(StringComparer.Ordinal) { "named" };

    [Fact]
    public void ReadsDateFromFile_NameDatedFileFlow_IsTrue()
        => Assert.True(BackfillRouting.ReadsDateFromFile(Documents, NameDatedFlow));

    [Fact]
    public void ReadsDateFromFile_ModifiedDatedFileFlow_IsFalse()
    {
        var modified = NameDatedFlow
            .Replace("fileDate.from: \"name\"", "fileDate.from: \"modified\"", StringComparison.Ordinal);
        Assert.False(BackfillRouting.ReadsDateFromFile(Documents, modified));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not: [a flow")]
    public void ReadsDateFromFile_NoUsableDefinition_IsFalse(string? yaml)
        => Assert.False(BackfillRouting.ReadsDateFromFile(Documents, yaml));

    [Fact]
    public void ForSchedule_NameDatedFileFlow_TakesTheWindow_BelowTheRoot()
    {
        var map = BackfillRouting.ForSchedule(Source, Window, NamedOnly);

        Assert.Equal(Window, map["copy"]);
        Assert.Equal(Window, map["named"]);
        Assert.True(map["silver"].ReprocessFromSourceMin);
        Assert.DoesNotContain("modified", map.Keys);
        Assert.DoesNotContain("proc", map.Keys);
    }

    [Fact]
    public void ForNode_NameDatedFileFlow_TakesTheWindow_BelowTheAnchor()
    {
        var map = BackfillRouting.ForNode(Source, "copy", Window, NamedOnly);

        Assert.Equal(Window, map["copy"]);
        Assert.Equal(Window, map["named"]);
        Assert.True(map["silver"].ReprocessFromSourceMin);
        Assert.DoesNotContain("modified", map.Keys);
    }

    [Fact]
    public void ForNode_AnchoredOnTheFileFlow_LeavesItsSilverChildOnSourceMin()
    {
        RunScopeMember[] members = [new("modified", "file", 0, "orders"), new("silver", "ing", 1, "orders")];

        var map = BackfillRouting.ForNode(members, "modified", Window, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(Window, map["modified"]);
        Assert.True(map["silver"].ReprocessFromSourceMin);
    }

    [Fact]
    public void ForSchedule_FileFlowInTheRootWave_TakesTheWindow_HoweverItDatesItsFiles()
    {
        RunScopeMember[] members = [new("modified", "file", 0, "orders"), new("silver", "ing", 1, "orders")];

        var map = BackfillRouting.ForSchedule(members, Window, new HashSet<string>(StringComparer.Ordinal));

        Assert.Equal(Window, map["modified"]);
    }
}
