using System.Text.Json;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// How a run.json artifact becomes a run row, specifically the row-count fields the boards read. Each flow kind
/// names its "rows read from the source" field for its own domain, and the projection has to understand all of
/// them: reading only the file-flow names left RowsLoaded NULL on every ingestion run ever recorded, so a flow
/// staging and merging millions of rows was indistinguishable in the GUI from one that did nothing. Pure: the
/// projection is a function of the document.
/// </summary>
public sealed class RunArtifactProjectionTests
{
    private static readonly Guid RepoId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static CatalogRun Project(string json)
    {
        using var document = JsonDocument.Parse(json);
        var run = CatalogProjection.RunFromJson(document.RootElement, RepoId);
        Assert.NotNull(run);
        return run;
    }

    private static string Artifact(string flowKind, string resultFields) => $$"""
        {
          "schemaVersion": 1,
          "runId": "3f2504e0-4f89-41d3-9a0c-0305e82c3301",
          "flowName": "demo_flow",
          "flowKind": "{{flowKind}}",
          "success": true,
          "writtenUtc": "2026-08-30T09:00:00Z",
          "result": { "startTimeUtc": "2026-08-30T08:59:58Z", "endTimeUtc": "2026-08-30T09:00:00Z", {{resultFields}} }
        }
        """;

    [Fact]
    public void IngestionRun_ReportsItsStagedCount_AsRowsLoaded()
    {
        // An ingestion artifact carries rowsStaged: the rows it read from the source into staging, which is that
        // kind's "rows loaded". Without this the board showed nothing loaded for a run that inserted 127 rows.
        var run = Project(Artifact("ing", """ "rowsStaged": 127, "rowsInserted": 120, "rowsUpdated": 7 """));

        Assert.Equal(127, run.RowsLoaded);
        Assert.Equal(120, run.RowsInserted);
        Assert.Equal(7, run.RowsUpdated);
    }

    [Fact]
    public void FileRun_KeepsReportingRowsLoaded()
    {
        var run = Project(Artifact("file", """ "rowsLoaded": 500, "rowsInserted": 500 """));

        Assert.Equal(500, run.RowsLoaded);
    }

    [Fact]
    public void TotalRows_StillFillsRowsLoaded_ForAnArtifactThatNamesItThatWay()
    {
        var run = Project(Artifact("file", """ "totalRows": 42 """));

        Assert.Equal(42, run.RowsLoaded);
    }

    [Fact]
    public void TheFieldsAreOrdered_NotMerged()
    {
        // An artifact carrying both must report the kind's own primary field, never a sum or the later fallback:
        // the fallback exists to fill a gap, not to override a value the writer stated.
        var run = Project(Artifact("ing", """ "rowsLoaded": 10, "totalRows": 20, "rowsStaged": 30 """));

        Assert.Equal(10, run.RowsLoaded);
    }

    [Fact]
    public void ARunThatLoadedNothing_ReportsZero_NotNull()
    {
        // Zero and "not reported" must stay distinguishable: a genuinely empty run has to be readable as empty.
        var run = Project(Artifact("ing", """ "rowsStaged": 0, "rowsInserted": 0 """));

        Assert.Equal(0, run.RowsLoaded);
    }

    [Fact]
    public void AnArtifactWithNoRowFields_LeavesRowsLoadedNull()
    {
        var run = Project(Artifact("api", """ "filesWritten": 2 """));

        Assert.Null(run.RowsLoaded);
    }
}
