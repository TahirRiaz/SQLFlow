using SqlFlow.Lineage.Collection;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The specification boundary: what the semantic layer stores is the canonical, redacted form of a specification,
/// and anything that is not a well-formed one (or is shaped to exhaust the reader) is refused before it is read.
/// </summary>
public sealed class ReportSpecsTests
{
    private const string Source = "the test specification";

    [Fact]
    public void Normalize_KeepsEverythingTheReaderUses()
    {
        var raw = LineageReportSpecTests.SampleSpec();

        var canonical = ReportSpecs.Normalize(raw, Source);

        var before = ReportSpecs.Inspect(raw, Source);
        var after = ReportSpecs.Inspect(canonical, Source);
        Assert.Equal(before.Pages, after.Pages);
        Assert.Equal(before.Visuals, after.Visuals);
        Assert.Equal(before.Tables, after.Tables);
        Assert.Equal(before.Measures, after.Measures);
        Assert.Equal(before.Relationships, after.Relationships);
        Assert.Equal(before.ResolvedTables, after.ResolvedTables);
        Assert.Equal((3, 5, 8, 1, 8, 7), (after.Pages, after.Visuals, after.Tables, after.Measures, after.Relationships, after.ResolvedTables));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = ReportSpecs.Normalize(LineageReportSpecTests.SampleSpec(), Source);

        Assert.Equal(once, ReportSpecs.Normalize(once, Source));
        Assert.Equal(ReportSpecs.Hash(once), ReportSpecs.Hash(ReportSpecs.Normalize(once, Source)));
    }

    [Fact]
    public void Normalize_RedactsACredentialQuotedInAnExpression()
    {
        var raw = LineageReportSpecTests.SampleSpec().Replace(
            "Sql.Database(\\\"<redacted>\\\", \\\"AdventureWorks\\\")",
            "Sql.Database(\\\"Server=x;User ID=a;Password=hunter2;\\\", \\\"AdventureWorks\\\")",
            StringComparison.Ordinal);
        Assert.Contains("hunter2", raw, StringComparison.Ordinal);

        var canonical = ReportSpecs.Normalize(raw, Source);

        Assert.DoesNotContain("hunter2", canonical, StringComparison.Ordinal);
        Assert.Contains("Password=[redacted]", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_QuotesNamesThatWouldOtherwiseBeYamlSyntax()
    {
        var raw = LineageReportSpecTests.SampleSpec().Replace(
            "displayName: \"Page 1\"", "displayName: \"Totals #1: all regions\"", StringComparison.Ordinal);

        var canonical = ReportSpecs.Normalize(raw.Replace("title: \"Order Quantity by Reseller Country\"", "title: \"true\"", StringComparison.Ordinal), Source);

        // Whatever quoting the writer chose, the values read back as the same strings.
        Assert.Contains("Totals #1: all regions", canonical, StringComparison.Ordinal);
        Assert.Equal(canonical, ReportSpecs.Normalize(canonical, Source));
        var parsed = PbixExtractTool.Parse(canonical, Source);
        Assert.Contains(parsed.Pages, p => p.Page == "Totals #1: all regions");
        Assert.Contains(parsed.Pages.SelectMany(p => p.Visuals ?? []), v => v.Title == "true");
    }

    [Theory]
    [InlineData("Sales.pbix", "Sales")]
    [InlineData("team/Sales Q3.pbix", "Sales_Q3")]
    [InlineData("report", "report")]
    [InlineData(".pbix", "report")]
    public void TheSpecificationName_FollowsTheReportLabel(string label, string name)
        => Assert.Equal(name, PbixExtractTool.SpecNameFor(label));

    [Theory]
    [InlineData("not: [valid")]
    [InlineData("subscribers: {}")]
    [InlineData("subscribers:\n  a: {}\n  b: {}\n")]
    [InlineData("just a scalar")]
    public void Malformed_IsRefused(string yaml)
    {
        var ex = Assert.Throws<PbixExtractException>(() => ReportSpecs.Normalize(yaml, Source));
        Assert.Contains(Source, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Aliases_AreRefusedBeforeTheDocumentIsMaterialized()
    {
        const string bomb = """
            subscribers:
              r:
                nodes: &n
                - id: "a"
                  kind: "page"
                edges: *n
            """;

        var ex = Assert.Throws<PbixExtractException>(() => ReportSpecs.Inspect(bomb, Source));
        Assert.Contains("anchors or aliases", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DuplicateNodes_AreRefused()
    {
        const string yaml = """
            subscribers:
              r:
                nodes:
                - id: "r#f#page:1"
                  kind: "page"
                - id: "r#f#page:1"
                  kind: "page"
            """;

        var ex = Assert.Throws<PbixExtractException>(() => ReportSpecs.Normalize(yaml, Source));
        Assert.Contains("duplicate node id", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Oversized_IsRefused()
    {
        var yaml = "subscribers:\n  r:\n    reportWarnings:\n    - \"" + new string('x', ReportSpecs.MaxBytes) + "\"\n";

        var ex = Assert.Throws<PbixExtractException>(() => ReportSpecs.Inspect(yaml, Source));
        Assert.Contains("larger than", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sales.pbix")]
    [InlineData("team/Sales Q3.pbix")]
    [InlineData("Salg og økonomi.pbix")]
    public void ReportFile_AcceptsARelativeName(string label)
        => Assert.Null(ReportSpecs.ReportFileProblem(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" Sales.pbix")]
    [InlineData("Sales#2.pbix")]
    [InlineData("team\\Sales.pbix")]
    [InlineData("/Sales.pbix")]
    [InlineData("../Sales.pbix")]
    [InlineData("team//Sales.pbix")]
    [InlineData("Sales\n.pbix")]
    public void ReportFile_RefusesWhatCannotKeyARow(string? label)
        => Assert.NotNull(ReportSpecs.ReportFileProblem(label));

    [Fact]
    public void ReportFile_RefusesAnOverlongName()
        => Assert.NotNull(ReportSpecs.ReportFileProblem(new string('a', ReportSpecs.MaxReportFileLength + 1)));

    [Theory]
    [InlineData("reports/Sales.pbix.yaml", true, "reports/Sales.pbix")]
    [InlineData("reports/SALES.PBIX.YAML", true, "reports/SALES.PBIX")]
    [InlineData("reports/sales.spec.yaml", false, "reports/sales.spec")]
    [InlineData("reports/sales.yml", false, "reports/sales")]
    public void SpecificationFiles_AreNamedForTheirReport(string path, bool isSpec, string label)
    {
        Assert.Equal(isSpec, ReportSpecs.IsSpecFile(path));
        Assert.Equal(label, ReportSpecs.LabelOf(path));
    }
}
