using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// A subscriber's report reaches the catalog as a SPECIFICATION, from whichever place holds one: a file committed
/// beside the subscriber, an upload the semantic layer keeps, or the copy an earlier sync kept of a <c>.pbix</c> this
/// machine cannot extract. These run against the checked-in sample specification (the real tool's output for the
/// AdventureWorks report), so none of them needs the extractor binary; the ones that must prove its ABSENCE point
/// <c>SQLFLOW_PBIX_EXTRACT</c> at nothing, and share a collection with every other test that depends on it.
/// </summary>
[Collection(PbixExtractorEnvironment.Name)]
public sealed class LineageReportSpecTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc);

    private const string Dwh = "${env:SQLFLOW_CONN_DWH}";

    private static readonly string SubscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Sales_Report");

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sqlflow-reportspec-" + Guid.NewGuid().ToString("N")[..8]);

    public LineageReportSpecTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal static string SampleSpec()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "samples", "powerbi", "AdventureWorks_Sales.spec.yaml");
        Assert.True(File.Exists(path), $"Expected the sample specification at {Path.GetFullPath(path)}.");
        return File.ReadAllText(path);
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void WriteSubscribers(string? pbix)
        => Write("subscribers.yaml", string.Join('\n', new[]
        {
            "connections:",
            $"  dwh: {Dwh}",
            "  other: ${env:SQLFLOW_CONN_OTHER}",
            "subscribers:",
            "  Sales_Report:",
            "    type: PowerBI",
            "    server: dwh",
            pbix is null ? string.Empty : $"    pbix: {pbix}",
        }.Where(l => l.Length > 0)) + '\n');

    private CollectionResult Collect(params StoredReportSpec[] stored) => new FlowSetCollector().Collect(_root, stored);

    private static LineageReport Build(CollectionResult collected, string root)
        => LineageGraphBuilder.Build(collected, root, [LineageTier.Declared], Utc);

    private static CollectionResult WithoutExtractor(Func<CollectionResult> collect)
    {
        var previous = Environment.GetEnvironmentVariable(ReportSpecs.ExtractorPathVariable);
        try
        {
            Environment.SetEnvironmentVariable(
                ReportSpecs.ExtractorPathVariable, Path.Combine(Path.GetTempPath(), "no-such-pbix-extract-" + Guid.NewGuid()));
            return collect();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ReportSpecs.ExtractorPathVariable, previous);
        }
    }

    [Fact]
    public void CommittedSpecification_IsReadLikeAnExtractedReport_UnderTheReportsOwnName()
    {
        Write("reports/Sales.pbix.yaml", SampleSpec());
        WriteSubscribers("reports/Sales.pbix.yaml");

        var collected = Collect();
        var subscriber = Assert.Single(collected.Subscribers);

        // The label is the report's own file name, the same one extracting Sales.pbix directly would produce, so
        // switching a subscriber from the .pbix to its committed specification keeps every stored key stable.
        Assert.Equal(3, subscriber.Pages.Count);
        Assert.All(subscriber.Pages, p => Assert.Equal("Sales.pbix", p.ReportFile));
        Assert.Equal("Sales.pbix", Assert.Single(subscriber.Models).ReportFile);
        Assert.Equal(5, subscriber.Queries.Count);

        // A committed specification is not an extraction to keep, and any kept one for the subscriber is now stale.
        Assert.Empty(subscriber.ExtractedSpecs);
        Assert.NotNull(subscriber.RetainedExtractedReports);
        Assert.Empty(subscriber.RetainedExtractedReports);
    }

    [Fact]
    public void CommittedSpecification_ResolvesItsModelTables_ToWarehouseObjects()
    {
        Write("reports/Sales.pbix.yaml", SampleSpec());
        WriteSubscribers("reports/Sales.pbix.yaml");

        var report = Build(Collect(), _root);

        // The visuals name model entities; the specification's Power Query sources turn them into the warehouse
        // tables they load from, through the same synonym pass a .pbix extracted on the spot goes through.
        var subscriber = Assert.Single(report.Subscribers);
        var customer = Assert.Single(subscriber.Models.Single().Tables, t => t.Name == "Customer");
        Assert.Equal(NodeKey.For(Dwh, "AdventureWorks", "dbo", "DimCustomer"), customer.ObjectKey);
        Assert.Contains(report.Edges, e =>
            e.ViaModule == SubscriberKey && e.Relation == LineageRelation.Reads
            && e.ObjectKey == NodeKey.For(Dwh, "AdventureWorks", "dbo", "FactResellerSales"));
    }

    [Fact]
    public void DirectoryOfSpecifications_LabelsEachByItsPath()
    {
        Write("reports/north/Sales.pbix.yaml", SampleSpec());
        Write("reports/south/Sales.pbix.yaml", SampleSpec());
        Write("reports/notes.yaml", "not: a specification\n");
        WriteSubscribers("reports");

        var subscriber = Assert.Single(Collect().Subscribers);

        Assert.Equal(
            ["north/Sales.pbix", "south/Sales.pbix"],
            subscriber.Pages.Select(p => p.ReportFile).Distinct().Order(StringComparer.Ordinal));
        // Two reports from one template: the report joins every query name so the two do not collide.
        Assert.All(subscriber.Queries, q => Assert.True(
            q.Name.StartsWith("north/Sales.pbix / ", StringComparison.Ordinal)
            || q.Name.StartsWith("south/Sales.pbix / ", StringComparison.Ordinal),
            q.Name));
        Assert.Equal(10, subscriber.Queries.Select(q => q.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SpecificationFiles_AreNotParsedAsFlows()
    {
        Write("reports/Sales.pbix.yaml", SampleSpec());
        WriteSubscribers("reports/Sales.pbix.yaml");

        var collected = Collect();

        Assert.Empty(collected.Flows);
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("Sales.pbix.yaml: skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadedSpecification_LinksASubscriberItsLibraryLeavesBare()
    {
        WriteSubscribers(pbix: null);

        var collected = Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Upload, SampleSpec()));
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.Equal(3, subscriber.Pages.Count);
        Assert.Equal(5, subscriber.Queries.Count);
        // Linked by the upload, so the "consumer of nothing" warning the file alone would earn is not raised.
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("has no usable queries", StringComparison.Ordinal));
        // Nothing is declared, so no kept extraction is wanted.
        Assert.NotNull(subscriber.RetainedExtractedReports);
        Assert.Empty(subscriber.RetainedExtractedReports);
    }

    [Fact]
    public void SubscriberWithNothing_IsStillWarnedAsUnlinked()
    {
        WriteSubscribers(pbix: null);

        var collected = Collect();

        Assert.Contains(collected.Warnings, w => w.Contains("has no usable queries", StringComparison.Ordinal));
    }

    [Fact]
    public void UploadForAnotherSubscriber_IsIgnored()
    {
        WriteSubscribers(pbix: null);
        var elsewhere = NodeKey.For(ServerIdentity.Subscriber, null, null, "Someone_Else");

        var subscriber = Assert.Single(
            Collect(new StoredReportSpec(elsewhere, "Sales.pbix", ReportSpecOrigin.Upload, SampleSpec())).Subscribers);

        Assert.Empty(subscriber.Pages);
    }

    [Fact]
    public void RepositoryReport_WinsOverAnUploadOfTheSameName()
    {
        Write("reports/Sales.pbix.yaml", SampleSpec());
        WriteSubscribers("reports/Sales.pbix.yaml");
        var upload = SampleSpec().Replace("Page 1", "Uploaded page", StringComparison.Ordinal);

        var collected = Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Upload, upload));
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.DoesNotContain(subscriber.Pages, p => p.DisplayName == "Uploaded page");
        Assert.Contains(collected.Warnings, w =>
            w.Contains("uploaded report 'Sales.pbix'", StringComparison.Ordinal)
            && w.Contains("used instead", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredReportMissingHere_IsServedFromTheKeptExtraction()
    {
        // The control plane's clone: subscribers.yaml names the .pbix, but the file is git-ignored.
        WriteSubscribers("reports/Sales.pbix");

        var collected = Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Extracted, SampleSpec()));
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.Equal(3, subscriber.Pages.Count);
        Assert.Contains(collected.Warnings, w =>
            w.Contains("does not exist here", StringComparison.Ordinal)
            && w.Contains("last extracted", StringComparison.Ordinal));
        // This pass cannot tell whether the report still exists, so the kept copy must not be dropped.
        Assert.Null(subscriber.RetainedExtractedReports);
        Assert.Empty(subscriber.ExtractedSpecs);
    }

    [Fact]
    public void DeclaredReportMissingHere_WithNoKeptExtraction_IsWarnedAsBefore()
    {
        WriteSubscribers("reports/Sales.pbix");

        var collected = Collect();

        Assert.Empty(Assert.Single(collected.Subscribers).Pages);
        Assert.Contains(collected.Warnings, w => w.Contains(
            "does not exist as either a file or a directory", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyReportDirectory_ServesTheKeptExtractions_WithoutDroppingThem()
    {
        // A folder whose .pbix files are git-ignored still exists in a clone when anything else in it is tracked.
        Write("reports/README.txt", "reports live on the team share");
        WriteSubscribers("reports");

        var collected = Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Extracted, SampleSpec()));
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.Equal(3, subscriber.Pages.Count);
        Assert.Null(subscriber.RetainedExtractedReports);
    }

    [Fact]
    public void DeclaredPbix_WithoutTheExtractor_IsServedFromTheKeptExtraction()
    {
        Write("reports/Sales.pbix", "not read: the extractor is absent");
        WriteSubscribers("reports/Sales.pbix");

        var collected = WithoutExtractor(() =>
            Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Extracted, SampleSpec())));
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.Equal(3, subscriber.Pages.Count);
        Assert.Null(subscriber.RetainedExtractedReports);
        // The only report was served, so there is nothing to warn about the missing tool.
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("tool was not found", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredPbix_WithoutTheExtractorOrAKeptExtraction_SaysHowToFixIt()
    {
        Write("reports/Sales.pbix", "not read: the extractor is absent");
        WriteSubscribers("reports/Sales.pbix");

        var collected = WithoutExtractor(() => Collect());

        Assert.Empty(Assert.Single(collected.Subscribers).Pages);
        Assert.Contains(collected.Warnings, w =>
            w.Contains("tool was not found", StringComparison.Ordinal)
            && w.Contains(ReportSpecs.ExtractorPathVariable, StringComparison.Ordinal)
            && w.Contains("sqlflow powerbi extract", StringComparison.Ordinal));
    }

    [Fact]
    public void CommittedSpecification_WinsOverThePbixBesideIt()
    {
        Write("reports/Sales.pbix", "never extracted: its specification is committed");
        Write("reports/Sales.pbix.yaml", SampleSpec());
        WriteSubscribers("reports");

        var collected = WithoutExtractor(() => Collect());
        var subscriber = Assert.Single(collected.Subscribers);

        Assert.Equal(3, subscriber.Pages.Count);
        Assert.Contains(collected.Warnings, w => w.Contains("has both a .pbix and a committed specification", StringComparison.Ordinal));
        Assert.DoesNotContain(collected.Warnings, w => w.Contains("tool was not found", StringComparison.Ordinal));
        // Served from the specification, so a kept extraction of it is no longer wanted.
        Assert.NotNull(subscriber.RetainedExtractedReports);
        Assert.Empty(subscriber.RetainedExtractedReports);
    }

    [Fact]
    public void BrokenSpecification_IsWarned_AndTheRestResolves()
    {
        Write("reports/Sales.pbix.yaml", "subscribers:\n  a: {}\n  b: {}\n");
        WriteSubscribers("reports/Sales.pbix.yaml");

        var collected = Collect();

        Assert.Empty(Assert.Single(collected.Subscribers).Pages);
        Assert.Contains(collected.Warnings, w =>
            w.Contains("report 'Sales.pbix' could not be read", StringComparison.Ordinal)
            && w.Contains("reports/Sales.pbix.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void SubscriberFingerprint_IsStable_AndMovesWithAnyReportInput()
    {
        WriteSubscribers(pbix: null);
        var upload = new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Upload, SampleSpec());

        var bare = Collect().SubscriberInputHash;
        Assert.Equal(bare, Collect().SubscriberInputHash);

        var withUpload = Collect(upload).SubscriberInputHash;
        Assert.NotEqual(bare, withUpload);
        Assert.Equal(withUpload, Collect(upload).SubscriberInputHash);

        var changedUpload = Collect(upload with { Yaml = upload.Yaml.Replace("Page 1", "Page One", StringComparison.Ordinal) });
        Assert.NotEqual(withUpload, changedUpload.SubscriberInputHash);

        // An edited subscriber library moves it too, even though no flow changed.
        Write("subscribers.yaml", File.ReadAllText(Path.Combine(_root, "subscribers.yaml")) + "# edited\n");
        Assert.NotEqual(bare, Collect().SubscriberInputHash);
    }

    [Fact]
    public void TwoReportsOnOneModel_ResolveTheirSharedTablesOnce()
    {
        // Two reports under one subscriber built on the same model each declare that 'Customer' loads from
        // dbo.DimCustomer. That is one fact, not a collision.
        WriteSubscribers(pbix: null);

        var report = Build(Collect(
            new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Upload, SampleSpec()),
            new StoredReportSpec(SubscriberKey, "Sales copy.pbix", ReportSpecOrigin.Upload, SampleSpec())), _root);

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Equal(2, subscriber.Models.Count);
        Assert.All(subscriber.Models, m => Assert.Equal(
            NodeKey.For(Dwh, "AdventureWorks", "dbo", "DimCustomer"),
            Assert.Single(m.Tables, t => t.Name == "Customer").ObjectKey));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("synonym of two different objects", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoReportsDisagreeingOnATable_KeepTheFirst_AndSaySo()
    {
        WriteSubscribers(pbix: null);
        var other = SampleSpec().Replace("sourceName: \"DimCustomer\"", "sourceName: \"DimClient\"", StringComparison.Ordinal);

        var report = Build(Collect(
            new StoredReportSpec(SubscriberKey, "A.pbix", ReportSpecOrigin.Upload, SampleSpec()),
            new StoredReportSpec(SubscriberKey, "B.pbix", ReportSpecOrigin.Upload, other)), _root);

        Assert.Contains(report.Warnings, w =>
            w.Contains("'Customer' is declared as a synonym of two different objects", StringComparison.Ordinal));
    }

    [Fact]
    public void DeclaredDefaultServer_IsWhatReportsResolveAgainst()
    {
        // Two connections and no hand-written query: only the subscriber's own 'server:' can say which one a report
        // reads through.
        WriteSubscribers(pbix: null);

        var subscriber = Assert.Single(
            Collect(new StoredReportSpec(SubscriberKey, "Sales.pbix", ReportSpecOrigin.Upload, SampleSpec())).Subscribers);

        Assert.All(subscriber.Queries, q => Assert.Equal(Dwh, q.ServerRef));
    }
}

/// <summary>The tests that depend on whether the <c>pbix-extract</c> tool can be found, run one at a time because some
/// of them change <c>SQLFLOW_PBIX_EXTRACT</c> for the whole process.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PbixExtractorEnvironment
{
    public const string Name = "pbix-extract environment";
}
