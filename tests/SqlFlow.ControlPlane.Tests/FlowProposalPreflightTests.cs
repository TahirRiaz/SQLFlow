using SqlFlow.ControlPlane.Api;
using SqlFlow.SourceControl.Proposals;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// The proposal preflight's pure core: the same loaders the managed sync parses with, applied before any git
/// push. Errors are the silent-failure modes (a file the sync would ignore, a valid flow under an undiscoverable
/// extension); warnings are the design signals a reviewer must see (an endpoint change on a revised flow, a
/// duplicate flow name). No database or git is involved: the catalog side is handed in as plain records.
/// </summary>
public sealed class FlowProposalPreflightTests
{
    private const string IngFlow = """
        flowType: ing
        name: orders-ingestion
        connections:
          erp: ${env:SQLFLOW_SRC}
          dwh: ${env:SQLFLOW_DW}
        source:
          server: erp
          object: AdventureWorks.Sales.Orders
        target:
          server: dwh
          object: DW.raw.Orders
        load:
          keyColumns: [OrderID]
        """;

    private static ProposalPreflightResult Run(
        IReadOnlyList<ProposalFile> files, params FlowProposalPreflight.ExistingPipeline[] existing)
        => FlowProposalPreflight.Run(files, existing);

    [Fact]
    public void ValidFlow_PassesClean()
    {
        var result = Run([new ProposalFile("flows/orders.flow.yaml", IngFlow)]);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void UnparseableFlow_IsAnError_NamingTheLoaderCause()
    {
        // A missing source block: the sync would treat this as a non-flow yaml and silently import nothing.
        var yaml = "flowType: ing\nname: broken\nconnections:\n  dwh: x\ntarget:\n  server: dwh\n  object: D.raw.T\n";
        var result = Run([new ProposalFile("flows/broken.flow.yaml", yaml)]);

        var error = Assert.Single(result.Errors);
        Assert.Equal("flows/broken.flow.yaml", error.Path);
        Assert.Contains("would never land", error.Message, StringComparison.Ordinal);
        Assert.Contains("source", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidFlowUnderYmlExtension_IsAnError_BecauseTheSyncNeverDiscoversIt()
    {
        var result = Run([new ProposalFile("flows/orders.flow.yml", IngFlow)]);

        var error = Assert.Single(result.Errors);
        Assert.Contains(".yaml", error.Message, StringComparison.Ordinal);
        Assert.Contains("never import", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompanionFiles_AreNotPreflighted()
    {
        var result = Run(
        [
            new ProposalFile("flows/compat_views.sql", "CREATE OR ALTER VIEW arc.V AS SELECT 1 AS x;"),
            new ProposalFile("flows/README.md", "# notes"),
            new ProposalFile("flows/mapping.json", "{ \"not\": \"a flow\" }"),
        ]);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RevisionThatRepointsTheTarget_WarnsWithTheExactEndpointDiff()
    {
        var revised = IngFlow.Replace("object: DW.raw.Orders", "object: DW.raw.Orders_v2", StringComparison.Ordinal);
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline("orders-ingestion", "flows/orders.flow.yaml", IngFlow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("CHANGES its declared endpoints", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[Orders]", warning.Message, StringComparison.Ordinal);
        Assert.Contains("[Orders_v2]", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionThatOnlyTunesBehavior_DoesNotWarn()
    {
        // Adding an incremental block and a schedule changes no endpoint, so the revision is silent.
        var revised = IngFlow + "\nincremental:\n  columns: [ModifiedDate]\n  overlapDays: 7\nschedule: nightly\n";
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline("orders-ingestion", "flows/orders.flow.yaml", IngFlow));

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void NewFileReusingAnExistingFlowName_Warns()
    {
        var result = Run(
            [new ProposalFile("flows/orders-copy.flow.yaml", IngFlow)],
            new FlowProposalPreflight.ExistingPipeline("orders-ingestion", "flows/orders.flow.yaml", IngFlow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("already declared by 'flows/orders.flow.yaml'", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoProposalFilesDeclaringOneFlowName_Warn()
    {
        var result = Run(
        [
            new ProposalFile("flows/a.flow.yaml", IngFlow),
            new ProposalFile("flows/b.flow.yaml", IngFlow),
        ]);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("same proposal", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduleLibrary_IsCheckedWithItsOwnLoader_AndSurfacesWarningsWithoutBlocking()
    {
        // A schedule with neither cron, interval, nor after is silently dropped by the sync; the preflight
        // surfaces the library loader's own warning instead of misreading the file as a broken flow.
        var library = "schedules:\n  cyclehire_daily:\n    enabled: false\n";
        var result = Run([new ProposalFile("flows/schedules.yaml", library)]);

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("cyclehire_daily", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FileFlowRevisionThatRepointsTheSourceLocation_Warns()
    {
        const string fileFlow = """
            name: cyclehire-bikes-pre
            source:
              type: json
              location: https://lake.example/raw/cyclehire/history/bikes/
            target:
              connection: ${env:SQLFLOW_DW}
              schema: pre
              table: Cyclehire_Bikes
            """;
        var revised = fileFlow.Replace("raw/cyclehire", "raw/voi", StringComparison.Ordinal);
        var result = Run(
            [new ProposalFile("Cyclehire/cyclehire_bikes_01_jsn.yaml", revised)],
            new FlowProposalPreflight.ExistingPipeline(
                "cyclehire-bikes-pre", "Cyclehire/cyclehire_bikes_01_jsn.yaml", fileFlow));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("raw/cyclehire", warning.Message, StringComparison.Ordinal);
        Assert.Contains("raw/voi", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RevisionWhoseStoredCopyNoLongerParses_WarnsAndDoesNotBlock()
    {
        var result = Run(
            [new ProposalFile("flows/orders.flow.yaml", IngFlow)],
            new FlowProposalPreflight.ExistingPipeline(
                "orders-ingestion", "flows/orders.flow.yaml", "flowType: ing\nname: orders-ingestion\n"));

        Assert.Empty(result.Errors);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("could not be compared", warning.Message, StringComparison.Ordinal);
    }
}
