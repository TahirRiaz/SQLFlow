using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// End-to-end incremental load against the physical sink: the engine probes the target for the
/// MAX(FileDate_DW) watermark and only re-reads files newer than it. Proves that a second run with an
/// older file loads nothing, a newer file loads only itself, and a run with no new files is a clean
/// no-op (not a failure).
/// </summary>
[Trait("Category", "Integration")]
public sealed class IncrementalIntegrationTests : IDisposable
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_inc_" + Guid.NewGuid().ToString("N"));

    public IncrementalIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec DatedCsv(string name, string content, DateTime modifiedUtc)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
        return new SourceSpec { Type = "csv", Location = path, Options = new Dictionary<string, string?>() };
    }

    private static FlowDefinition Flow(string cs, SourceSpec source, string table)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = cs, Schema = IntegrationDb.Schema, Table = table },
            Schema = new SchemaPolicy { Evolve = SchemaEvolution.Widen },
            Load = new LoadPolicy(),
            Incremental = new IncrementalSpec { DateColumn = "FileDate_DW" },
        };

    [SkippableFact]
    public async Task IncrementalLoad_OnlyIngestsFilesNewerThanWatermark()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Incr_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // First run: an "old" file establishes the watermark.
            var first = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day1.csv", "OrderId\n1\n2\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // Second run sees only an older file -> nothing new -> clean no-op, row count unchanged.
            var noop = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day0.csv", "OrderId\n9\n", new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, noop.Status);
            Assert.Equal(0, noop.RowsLoaded);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // A run that correctly finds nothing new must not report a failed stage. The source read reaches a
            // definite answer ("no file is newer than the watermark") and the engine acts on it, so the trace of
            // this healthy no-op carries no error for an operator to chase down.
            Assert.DoesNotContain(noop.Trace, t => !t.Succeeded);

            // Third run with a newer file ingests only that file.
            var third = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day2.csv", "OrderId\n3\n4\n5\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), table));
            Assert.Equal(FlowStatus.Success, third.Status);
            Assert.Equal(3, third.RowsLoaded);
            Assert.Equal(5, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task DownstreamWatermark_FileFlow_RepullsWhatSilverIsBehindOn()
    {
        // The default downstream anchor for a file (bronze/pre) flow: its watermark is read from the next durable
        // table in the lineage chain (the ods/silver table it feeds), not its own target. A file that the pre
        // target and run history already know about is skipped by the normal path, but re-pulled once the silver
        // table is behind on it - so deleting rows from silver self-heals by re-reading the source.
        var cs = IntegrationDb.Require();
        var db = new SqlConnectionStringBuilder(cs).InitialCatalog;
        var pre = "IT_DwnPre_" + Guid.NewGuid().ToString("N")[..8];
        var ods = "IT_DwnOds_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, pre);
        await IntegrationDb.DropTableAsync(cs, ods);

        try
        {
            // Establish the pre watermark at 2024-02 by landing an old file then a newer one.
            var r1 = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("d1.csv", "OrderId\n1\n2\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), pre));
            Assert.Equal(FlowStatus.Success, r1.Status);
            var r2 = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("d2.csv", "OrderId\n3\n4\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), pre));
            Assert.Equal(FlowStatus.Success, r2.Status);
            Assert.Equal(4, await IntegrationDb.RowCountAsync(cs, pre));

            // The downstream silver table carries the same FileDate_DW provenance column but is behind: it only
            // knows about the 2024-01 file (its 2024-02 rows were "deleted").
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [{IntegrationDb.Schema}].[{ods}] ([OrderId] int NULL, [FileDate_DW] nvarchar(32) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [{IntegrationDb.Schema}].[{ods}] ([OrderId],[FileDate_DW]) VALUES (1,'20240101000000'),(2,'20240101000000');");
            var silver = new RelationalObject { Database = db, Schema = IntegrationDb.Schema, Name = ods };

            // A file dated between silver's mark (2024-01) and pre's mark (2024-02): the normal path skips it,
            // because pre already knows about 2024-02.
            var mid = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
            var skipped = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("mid.csv", "OrderId\n7\n", mid), pre));
            Assert.Equal(FlowStatus.Success, skipped.Status);
            Assert.Equal(0, skipped.RowsLoaded);

            // Anchored to the (behind) silver table, the window regresses to 2024-01 and the same file is re-pulled.
            var repulled = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("mid2.csv", "OrderId\n7\n", mid), pre), runId: null, statementSink: null,
                runHistoryDirectory: null, watermarkTable: silver);
            Assert.Equal(FlowStatus.Success, repulled.Status);
            Assert.Equal(1, repulled.RowsLoaded);
            Assert.NotNull(repulled.Incremental);
            Assert.StartsWith("downstream MAX", repulled.Incremental!.WatermarkSource);
            Assert.Contains(ods, repulled.Incremental!.WatermarkSource!, StringComparison.Ordinal);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, pre);
            await IntegrationDb.DropTableAsync(cs, ods);
        }
    }

    [SkippableFact]
    public async Task DownstreamWatermark_FileFlow_FallsBackWhenSilverLacksTheColumn()
    {
        // Column-safety on the file path: a silver table that does not carry the date column must fall back to the
        // flow's own target probe rather than failing the run.
        var cs = IntegrationDb.Require();
        var db = new SqlConnectionStringBuilder(cs).InitialCatalog;
        var pre = "IT_DwnFbPre_" + Guid.NewGuid().ToString("N")[..8];
        var ods = "IT_DwnFbOds_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, pre);
        await IntegrationDb.DropTableAsync(cs, ods);

        try
        {
            var r1 = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("d1.csv", "OrderId\n1\n2\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), pre));
            Assert.Equal(FlowStatus.Success, r1.Status);

            // Silver exists but has no FileDate_DW column (renamed downstream).
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [{IntegrationDb.Schema}].[{ods}] ([OrderId] int NULL, [LoadedAt] datetime2 NULL);");
            var silver = new RelationalObject { Database = db, Schema = IntegrationDb.Schema, Name = ods };

            // An older file, anchored to the column-less silver table: it must fall back to the pre target (which
            // knows 2024-02) and skip the file, NOT fail the run.
            var older = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var result = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("old.csv", "OrderId\n9\n", older), pre), runId: null, statementSink: null,
                runHistoryDirectory: null, watermarkTable: silver);
            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(0, result.RowsLoaded);
            Assert.StartsWith("target MAX", result.Incremental!.WatermarkSource);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, pre);
            await IntegrationDb.DropTableAsync(cs, ods);
        }
    }

    [SkippableFact]
    public async Task DownstreamWatermark_FileFlow_AcceptsNumericProvenanceEncoding()
    {
        // Silver/ods tables carried over from legacy layers store FileDate_DW as a NUMERIC yyyyMMddHHmmss stamp
        // (decimal), not a string. The file-date probe must accept it, else downstream anchoring silently falls
        // back on every real legacy silver table. Mirrors the Baatbooking arc.* case.
        var cs = IntegrationDb.Require();
        var db = new SqlConnectionStringBuilder(cs).InitialCatalog;
        var pre = "IT_DwnNum_" + Guid.NewGuid().ToString("N")[..8];
        var ods = "IT_DwnNumOds_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, pre);
        await IntegrationDb.DropTableAsync(cs, ods);

        try
        {
            // pre knows about the 2024-02 file.
            Assert.Equal(FlowStatus.Success, (await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("n1.csv", "OrderId\n1\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), pre))).Status);
            Assert.Equal(FlowStatus.Success, (await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("n2.csv", "OrderId\n2\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), pre))).Status);

            // silver carries FileDate_DW as a decimal yyyyMMddHHmmss stamp, behind at 2024-01.
            await IntegrationDb.ExecuteAsync(cs,
                $"CREATE TABLE [{IntegrationDb.Schema}].[{ods}] ([OrderId] int NULL, [FileDate_DW] decimal(14,0) NULL);");
            await IntegrationDb.ExecuteAsync(cs,
                $"INSERT INTO [{IntegrationDb.Schema}].[{ods}] ([OrderId],[FileDate_DW]) VALUES (1, 20240101000000);");
            var silver = new RelationalObject { Database = db, Schema = IntegrationDb.Schema, Name = ods };

            // A 2024-01-15 file: pre would skip it, but anchored to the numeric-stamped silver (2024-01) it re-pulls.
            var mid = new DateTime(2024, 1, 15, 0, 0, 0, DateTimeKind.Utc);
            var repulled = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("nmid.csv", "OrderId\n7\n", mid), pre), runId: null, statementSink: null,
                runHistoryDirectory: null, watermarkTable: silver);
            Assert.Equal(FlowStatus.Success, repulled.Status);
            Assert.Equal(1, repulled.RowsLoaded);
            Assert.StartsWith("downstream MAX", repulled.Incremental!.WatermarkSource);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, pre);
            await IntegrationDb.DropTableAsync(cs, ods);
        }
    }

    [SkippableFact]
    public async Task IncrementalLoad_EmptyTarget_LoadsEverything()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_IncrFull_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            // No target yet -> no watermark -> full load of the available file.
            var result = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("seed.csv", "OrderId\n1\n2\n3\n", new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)), table));

            Assert.Equal(FlowStatus.Success, result.Status);
            Assert.Equal(3, result.RowsLoaded);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task Incremental_FloorComesFromRunLog_WhenTargetCannotProvideWatermark()
    {
        // The bronze/pre pattern: the target is a transient landing table, so probing IT for MAX(FileDate_DW)
        // yields no watermark and a naive re-run reloads every file. The durable floor is the on-disk run log,
        // which survives the target being cleared. Passing the run-history anchor makes the runner read it.
        var cs = IntegrationDb.Require();
        var table = "IT_IncrLog_" + Guid.NewGuid().ToString("N")[..8];
        await IntegrationDb.DropTableAsync(cs, table);

        try
        {
            var day1 = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            // First run loads day1; then record it in the run log the way the executor would.
            var first = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day1.csv", "OrderId\n1\n2\n", day1), table), null, null, _dir);
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(2, first.RowsLoaded);
            WriteRunLog(table, "day1.csv", day1);

            // Drop the target so its own probe can offer no watermark (the transient-landing case).
            await IntegrationDb.DropTableAsync(cs, table);

            // An OLDER file: the target gives nothing, but the run-log floor (day1) excludes it -> no-op.
            var older = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day0.csv", "OrderId\n9\n", new DateTime(2023, 12, 1, 0, 0, 0, DateTimeKind.Utc)), table), null, null, _dir);
            Assert.Equal(FlowStatus.Success, older.Status);
            Assert.Equal(0, older.RowsLoaded);

            // A NEWER file clears the floor and loads.
            var newer = await IntegrationDb.RealRunner().RunAsync(
                Flow(cs, DatedCsv("day2.csv", "OrderId\n3\n4\n5\n", new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc)), table), null, null, _dir);
            Assert.Equal(FlowStatus.Success, newer.Status);
            Assert.Equal(3, newer.RowsLoaded);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, table);
        }
    }

    // Records one successful run in the on-disk run history the reader consumes: RunAsync itself does not write
    // history (the executor does), so a runner-level test stamps the processed-file manifest here.
    private void WriteRunLog(string flowName, string fileName, DateTimeOffset modified)
    {
        var runId = Guid.NewGuid();
        var artifact = new
        {
            schemaVersion = 1,
            flowKind = "file",
            flowName,
            runId,
            success = true,
            writtenUtc = DateTime.UtcNow,
            result = new { processedFiles = new[] { new { name = fileName, modified = (DateTimeOffset?)modified } } },
        };

        var json = JsonSerializer.Serialize(artifact, CamelCaseJson);
        RunHistoryWriter.Write(_dir, flowName, runId, DateTime.UtcNow, new Dictionary<string, string> { ["run.json"] = json });
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
