using System.IO.Compression;
using SqlFlow.Core.Lineage;
using SqlFlow.Lineage.Collection;
using SqlFlow.Lineage.Graph;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The collector side of Power BI extraction: a subscriber that declares <c>pbix:</c> gets the questions its
/// VISUALS ask turned into queries, and those queries are parsed on exactly the same path a hand-transcribed
/// subscriber query takes, so a chart's question becomes a real consumption edge on the same node the loading
/// flows write. The report's own structure (pages, visuals, and each field's ROLE) rides along beside the
/// queries, because the role is the one thing parsed SQL cannot recover: a column list cannot say which field
/// was the axis and which was the value.
/// </summary>
[Collection(PbixExtractorEnvironment.Name)]
public sealed class LineagePowerBiSubscriberTests : IDisposable
{
    private static readonly DateTime Utc = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sqlflow-pbisub-" + Guid.NewGuid().ToString("N")[..8]);

    public LineagePowerBiSubscriberTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private const string Ods = "${env:SQLFLOW_CONN_ODS}";

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    /// <summary>
    /// Extraction runs in the standalone <c>pbix-extract</c> binary, which is built outside the solution
    /// (<c>make -C tools/pbix-extract</c>) so that parsing an untrusted report never happens inside the control
    /// plane. A machine without it cannot exercise these paths, so they skip LOUDLY rather than passing
    /// vacuously: a silent pass here would hide the loss of every assertion below.
    /// </summary>
    private static void RequireExtractor()
        => Skip.If(
            Environment.GetEnvironmentVariable("SQLFLOW_PBIX_EXTRACT") is null
            && !File.Exists(Path.Combine(AppContext.BaseDirectory, "pbix-extract"))
            && (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .All(d => !File.Exists(Path.Combine(d, "pbix-extract"))),
            "The 'pbix-extract' tool was not found. Build it with 'make -C tools/pbix-extract' and put it on "
            + "PATH, or set SQLFLOW_PBIX_EXTRACT to its location.");

    private LineageReport Build()
    {
        RequireExtractor();
        return LineageGraphBuilder.Build(
            new FlowSetCollector().Collect(_root), _root, [LineageTier.Declared], Utc);
    }

    /// <summary>An ingestion writing the table the report's visual reads: the producing half of the graph.</summary>
    private static string Ingestion => string.Join('\n',
        "flowType: ing",
        "name: sales_02_ing",
        "connections:",
        "  pre: ${env:SQLFLOW_CONN_PRE}",
        $"  ods: {Ods}",
        "source:",
        "  server: pre",
        "  object: \"[PreDb].[pre].[v_Sales]\"",
        "target:",
        "  server: ods",
        "  object: \"[OdsDb].[arc].[Sales]\"",
        "load:",
        "  keyColumns: [id]") + '\n';

    /// <summary>Writes the shared fixture report (one pivot table over the model entity <c>Sales</c>, plus a textbox).
    /// A visual names MODEL entities, never warehouse objects, which is what the resolution assertions below pin
    /// down.</summary>
    private void WritePbix(string relative) => PbixFixture.Write(Path.Combine(_root, relative));

    private static string Subscribers(string pbix) => string.Join('\n',
        "connections:",
        $"  dwh: {Ods}",
        "subscribers:",
        "  Revenue_Report:",
        "    type: PowerBI",
        "    owner: analyse@kolumbus.no",
        "    description: Revenue dashboard",
        "    server: dwh",
        $"    pbix: {pbix}") + '\n';

    [SkippableFact]
    public void DeclaredPbix_TurnsItsVisualsIntoConsumptionEdges()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var report = Build();

        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Revenue_Report");

        // The visual's question becomes a real consumption edge, attributed to the subscriber and resolved
        // against its declared connection, on the same ScriptFactBuilder path a transcribed query takes.
        var read = Assert.Single(report.Edges, e =>
            e.Flow is null && e.ViaModule == subscriberKey && e.Relation == LineageRelation.Reads);

        // A visual names the MODEL entity ('Sales'), not the warehouse object, so the edge lands on a
        // name-only node with NO database or schema unless the model's Power Query source resolved to a
        // physical table. This fixture carries only a Report/Layout part and no DataModel (building a real
        // one means XPress9-compressing an Analysis Services image, which a test cannot reasonably do), so
        // there is no M expression to resolve and the unresolved outcome is what is asserted here.
        // The resolution path itself is covered by the tool's own suite (tests/test_msource.c), which drives
        // every recognized and refused M shape directly.
        Assert.Equal(NodeKey.For(Ods, null, null, "Sales"), read.ObjectKey);
        Assert.NotEqual(NodeKey.For(Ods, "OdsDb", "arc", "Sales"), read.ObjectKey);

        // The producing side is unaffected and still writes the fully-qualified node.
        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes
            && e.ObjectKey == NodeKey.For(Ods, "OdsDb", "arc", "Sales"));
    }

    [SkippableFact]
    public void DeclaredPbix_RecordsOneQueryPerVisual_NamedForIt()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var subscriber = Assert.Single(Build().Subscribers);

        // One query, from the one visual that asks something; the textbox contributed nothing.
        var query = Assert.Single(subscriber.Queries);
        Assert.Equal("Revenue / Revenue by Region", query.Name);
        Assert.Contains("SUM([s].[amount])", query.Sql, StringComparison.Ordinal);
        Assert.Contains("FROM [Sales] AS [s]", query.Sql, StringComparison.Ordinal);
        // The model entity resolves to a name-only node (see the edge test for why this is not the
        // database-qualified warehouse node).
        Assert.Equal(NodeKey.For(Ods, null, null, "Sales"), Assert.Single(query.ObjectKeys));
    }

    [SkippableFact]
    public void DeclaredPbix_CarriesThePagesVisualsAndFieldRoles()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var subscriber = Assert.Single(Build().Subscribers);

        var page = Assert.Single(subscriber.Pages);
        Assert.Equal("Revenue", page.DisplayName);
        Assert.Equal("ReportSection1", page.Name);
        Assert.Equal(1, page.Ordinal);

        var visual = Assert.Single(page.Visuals);
        Assert.Equal("pivotTable", visual.VisualType);
        Assert.Equal("Revenue by Region", visual.Title);
        // The structure points back at the SQL that carries its lineage, so the two halves stay joined.
        Assert.Equal("Revenue / Revenue by Region", visual.QueryName);

        // The ROLES: region is what the table is broken down BY, amount is what it reports. The same two
        // columns in swapped roles would be a different question, and the parsed SQL cannot tell them apart.
        Assert.Collection(
            visual.Fields,
            f =>
            {
                Assert.Equal("Rows", f.Role);
                Assert.Equal("Sales", f.TableName);
                Assert.Equal("region", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            },
            f =>
            {
                Assert.Equal("Values", f.Role);
                Assert.Equal("Sales", f.TableName);
                Assert.Equal("amount", f.ColumnOrMeasure);
                Assert.False(f.IsMeasure);
            });
    }

    [SkippableFact]
    public void DeclaredPbix_WithNoHandWrittenQueries_IsNotWarnedAsUnlinked()
    {
        // A pbix-only subscriber (no hand-written 'queries:' block at all) genuinely gets its queries from
        // the report's visuals, extracted AFTER the YAML is parsed. The loader that emits "has no usable
        // queries" runs before that extraction and so cannot see it; declaring 'pbix:' must suppress that
        // warning rather than raise a false alarm on every report-only subscriber.
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", Subscribers("reports/revenue.pbix"));

        var report = Build();

        Assert.NotEmpty(Assert.Single(report.Subscribers).Queries);
        Assert.DoesNotContain(report.Warnings, w => w.Contains("has no usable queries", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void SubscriberWithNeitherQueriesNorPbix_IsWarnedAsUnlinked()
    {
        // The genuine case the warning exists for: nothing hand-written and no report declared, so the
        // subscriber really is a consumer of nothing.
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Empty_Subscriber:",
            "    type: PowerBI",
            "    server: dwh") + '\n');

        var report = Build();

        Assert.Contains(report.Warnings, w => w.Contains("has no usable queries", StringComparison.Ordinal));
    }

    [SkippableFact]
    public void HandAuthoredSubscriber_KeepsWorking_AndCarriesNoPages()
    {
        // The declaration is additive: a subscriber with no 'pbix:' behaves exactly as before.
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Legacy_Report:",
            "    type: PowerBI",
            "    server: dwh",
            "    queries:",
            "      - name: Totals",
            "        sql: SELECT * FROM [OdsDb].[arc].[Sales];") + '\n');

        var subscriber = Assert.Single(Build().Subscribers);

        Assert.Equal("Totals", Assert.Single(subscriber.Queries).Name);
        Assert.Empty(subscriber.Pages);
    }

    [SkippableFact]
    public void DeclaredPbix_AlongsideHandAuthoredQueries_KeepsBoth()
    {
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/revenue.pbix");
        Write("subscribers.yaml", string.Join('\n',
            "connections:",
            $"  dwh: {Ods}",
            "subscribers:",
            "  Revenue_Report:",
            "    type: PowerBI",
            "    server: dwh",
            "    pbix: reports/revenue.pbix",
            "    queries:",
            "      - name: Hand written",
            "        sql: SELECT * FROM [OdsDb].[arc].[Sales];") + '\n');

        var subscriber = Assert.Single(Build().Subscribers);

        // A transcribed query someone already verified is not discarded because the file can now be read.
        Assert.Equal(
            ["Hand written", "Revenue / Revenue by Region"],
            subscriber.Queries.Select(q => q.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [SkippableFact]
    public void MissingPbix_IsWarned_AndLeavesTheRestOfLineageIntact()
    {
        Write("10_ing.yaml", Ingestion);
        Write("subscribers.yaml", Subscribers("reports/absent.pbix"));

        var report = Build();

        // The subscriber still exists as a consumer node; only its extraction failed, and it said so.
        var subscriber = Assert.Single(report.Subscribers);
        Assert.Empty(subscriber.Pages);
        Assert.Contains(report.Warnings, w =>
            w.Contains("reports/absent.pbix", StringComparison.Ordinal)
            && w.Contains("does not exist as either a file or a directory", StringComparison.Ordinal));

        // The producing side of the graph is unaffected by one unreadable report.
        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes);
    }

    [Fact]
    public void MissingExtractor_IsWarned_AndLeavesTheRestOfLineageIntact()
    {
        // Extraction lives in a standalone binary that is deliberately NOT shipped inside the control plane,
        // so a machine legitimately may not have it. That must degrade like any other unreadable report: the
        // subscriber stays a consumer node, the warning says how to fix it, and the rest of the estate
        // resolves. This test does not need the real tool, so it never skips.
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/sales.pbix");
        Write("subscribers.yaml", Subscribers("reports/sales.pbix"));

        var previous = Environment.GetEnvironmentVariable("SQLFLOW_PBIX_EXTRACT");
        LineageReport report;
        try
        {
            Environment.SetEnvironmentVariable(
                "SQLFLOW_PBIX_EXTRACT", Path.Combine(_root, "no-such-pbix-extract"));
            report = LineageGraphBuilder.Build(
                new FlowSetCollector().Collect(_root), _root, [LineageTier.Declared], Utc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SQLFLOW_PBIX_EXTRACT", previous);
        }

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Empty(subscriber.Pages);

        // The warning has to be actionable: a person reading it should know what to build and what to set.
        Assert.Contains(report.Warnings, w =>
            w.Contains("pbix-extract", StringComparison.Ordinal)
            && w.Contains("SQLFLOW_PBIX_EXTRACT", StringComparison.Ordinal));

        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes);
    }

    [SkippableFact]
    public void DirectoryPbix_ExtractsEveryReportUnderOneSubscriber()
    {
        // A workspace of two published reports, both from the same template (so both a page named
        // "ReportSection1" and a visual titled "Revenue by Region" appear TWICE): the collision case a
        // directory of reports routinely produces in practice.
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/north.pbix");
        WritePbix("reports/south.pbix");
        Write("subscribers.yaml", Subscribers("reports"));

        var subscriber = Assert.Single(Build().Subscribers);

        // Both files' pages are kept, each correctly tagged with which report it came from rather than one
        // silently overwriting the other.
        Assert.Equal(2, subscriber.Pages.Count);
        Assert.Equal(
            ["north.pbix", "south.pbix"],
            subscriber.Pages.Select(p => p.ReportFile).OrderBy(f => f, StringComparer.Ordinal));
        Assert.All(subscriber.Pages, p => Assert.Equal("Revenue", p.DisplayName));

        // Once more than one file is in play, the report file joins the query name, so the two files'
        // identically titled visuals do not collide into one synthesized query.
        Assert.Equal(
            ["north.pbix / Revenue / Revenue by Region", "south.pbix / Revenue / Revenue by Region"],
            subscriber.Queries.Select(q => q.Name).OrderBy(n => n, StringComparer.Ordinal));

        // Each query independently resolved its object: neither was silently dropped by the collision, even
        // though both name the same model entity.
        Assert.All(subscriber.Queries, q => Assert.Equal(
            NodeKey.For(Ods, null, null, "Sales"), Assert.Single(q.ObjectKeys)));

        // Both files reading the same entity is genuinely one fact about the graph ("this subscriber reads
        // Sales"), not two, so the edges collapse to one exactly as a subscriber reading the same table twice
        // through two hand-written queries would.
        var subscriberKey = NodeKey.For(ServerIdentity.Subscriber, null, null, "Revenue_Report");
        var report = Build();
        Assert.Single(report.Edges, e =>
            e.Flow is null && e.ViaModule == subscriberKey && e.Relation == LineageRelation.Reads);
    }

    [SkippableFact]
    public void DirectoryPbix_WithOneReport_KeepsSingleFileQueryNaming()
    {
        // With only one file in the directory there is no collision to guard against, so the query name stays
        // exactly what a subscriber declaring that file directly would produce.
        Write("10_ing.yaml", Ingestion);
        WritePbix("reports/only.pbix");
        Write("subscribers.yaml", Subscribers("reports"));

        var subscriber = Assert.Single(Build().Subscribers);

        Assert.Equal("only.pbix", Assert.Single(subscriber.Pages).ReportFile);
        Assert.Equal("Revenue / Revenue by Region", Assert.Single(subscriber.Queries).Name);
    }

    [SkippableFact]
    public void EmptyDirectory_IsWarned_AndLeavesTheRestOfLineageIntact()
    {
        Write("10_ing.yaml", Ingestion);
        Directory.CreateDirectory(Path.Combine(_root, "reports"));
        Write("subscribers.yaml", Subscribers("reports"));

        var report = Build();

        var subscriber = Assert.Single(report.Subscribers);
        Assert.Empty(subscriber.Pages);
        Assert.Contains(report.Warnings, w =>
            w.Contains("reports", StringComparison.Ordinal)
            && w.Contains("containing no .pbix files", StringComparison.Ordinal));

        Assert.Contains(report.Edges, e =>
            e.Flow == "sales_02_ing" && e.Relation == LineageRelation.Writes);
    }

    [SkippableFact]
    public void UnreadablePbix_IsWarned_RatherThanThrowing()
    {
        // A file that is not a report at all (no Report/Layout part) must degrade to a warning, exactly as an
        // unparseable flow document does, rather than aborting the estate's collection.
        Write("10_ing.yaml", Ingestion);
        var path = Path.Combine(_root, "reports", "broken.pbix");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            archive.CreateEntry("Version");
        }

        Write("subscribers.yaml", Subscribers("reports/broken.pbix"));

        var report = Build();

        Assert.Empty(Assert.Single(report.Subscribers).Pages);
        Assert.Contains(report.Warnings, w =>
            w.Contains("could not be read", StringComparison.Ordinal)
            && w.Contains("broken.pbix", StringComparison.Ordinal));
    }
}
