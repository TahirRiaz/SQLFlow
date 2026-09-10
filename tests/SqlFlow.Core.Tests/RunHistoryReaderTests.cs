using System.Text.Json;
using System.Text.Json.Serialization;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests;

/// <summary>
/// The read side of the on-disk run history, which supplies the database-free incremental file watermark: the
/// maximum processed-file modified date across a file flow's retained, successful run.json artifacts. Covers the
/// first-run (no history) case, the max-across-runs rule (so a backfill of older files cannot regress the mark),
/// the failed-run and missing-date exclusions, and graceful skipping of a corrupt artifact.
/// </summary>
public sealed class RunHistoryReaderTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_runhistory_" + Guid.NewGuid().ToString("N"));

    public RunHistoryReaderTests() => Directory.CreateDirectory(_dir);

    private const string Flow = "BB_Baatbooking_sess_pre";

    private void WriteRun(bool success, params DateTimeOffset?[] modified)
    {
        var runId = Guid.NewGuid();
        var artifact = new RunArtifact
        {
            FlowKind = "file",
            FlowName = Flow,
            RunId = runId,
            Success = success,
            WrittenUtc = DateTime.UtcNow,
            Result = new { processedFiles = modified.Select(m => new { name = "sess.csv", modified = m }).ToArray() },
        };

        var json = JsonSerializer.Serialize(artifact, JsonOptions);
        RunHistoryWriter.Write(_dir, Flow, runId, DateTime.UtcNow, new Dictionary<string, string> { ["run.json"] = json });
    }

    private static DateTimeOffset Utc(int year, int month, int day)
        => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NoHistory_ReturnsNull()
        => Assert.Null(RunHistoryReader.LastProcessedFileDate(_dir, Flow));

    [Fact]
    public void SingleRun_ReturnsMaxModifiedOfThatRun()
    {
        WriteRun(success: true, Utc(2022, 12, 20), Utc(2022, 12, 27), Utc(2022, 12, 24));

        Assert.Equal(Utc(2022, 12, 27), RunHistoryReader.LastProcessedFileDate(_dir, Flow));
    }

    [Fact]
    public void MaxIsTakenAcrossRuns_SoABackfillOfOlderFilesDoesNotRegressTheMark()
    {
        WriteRun(success: true, Utc(2022, 12, 27));   // the real high-water mark
        WriteRun(success: true, Utc(2022, 1, 5));      // a later backfill run loading an OLDER file

        Assert.Equal(Utc(2022, 12, 27), RunHistoryReader.LastProcessedFileDate(_dir, Flow));
    }

    [Fact]
    public void FailedRun_IsIgnored()
    {
        WriteRun(success: true, Utc(2022, 6, 1));
        WriteRun(success: false, Utc(2022, 12, 27));   // a higher mark, but its load never committed

        Assert.Equal(Utc(2022, 6, 1), RunHistoryReader.LastProcessedFileDate(_dir, Flow));
    }

    [Fact]
    public void ProcessedFilesWithoutModifiedDate_YieldNoMark()
    {
        WriteRun(success: true, new DateTimeOffset?[] { null });

        Assert.Null(RunHistoryReader.LastProcessedFileDate(_dir, Flow));
    }

    [Fact]
    public void CorruptArtifact_IsSkipped_AndOtherRunsStillYieldTheMark()
    {
        WriteRun(success: true, Utc(2022, 12, 27));

        // A second run folder whose run.json is not valid JSON: it degrades to no-mark and is skipped.
        var corruptRunId = Guid.NewGuid();
        RunHistoryWriter.Write(_dir, Flow, corruptRunId, DateTime.UtcNow.AddSeconds(1),
            new Dictionary<string, string> { ["run.json"] = "{ this is not valid json" });

        Assert.Equal(Utc(2022, 12, 27), RunHistoryReader.LastProcessedFileDate(_dir, Flow));
    }

    [Fact]
    public void DifferentFlowName_HasItsOwnHistory()
    {
        WriteRun(success: true, Utc(2022, 12, 27));

        Assert.Null(RunHistoryReader.LastProcessedFileDate(_dir, "BB_Some_Other_pre"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; a held-open file must not fail the test run.
        }
    }
}
