using SqlFlow.Core.Model;
using SqlFlow.Core.Runs;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// The engine half of the landing reset (<c>load.resetWhenConsolidated</c>): given the control plane's verdict,
/// the file flow truncates its chained landing target after the source read finds files and before the load, so
/// an authorized run starts the table with only its own rows; a blocked verdict, the per-flow opt-out, and a
/// quiet incremental day (no new files) must all preserve the accumulated rows. Runs against the physical sink
/// database through the real reader, schema, and bulk-load path.
/// </summary>
[Trait("Category", "Integration")]
public sealed class LandingResetIntegrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sqlflow_it_" + Guid.NewGuid().ToString("N"));

    public LandingResetIntegrationTests() => Directory.CreateDirectory(_dir);

    private SourceSpec Csv(string fileName, string content, DateTime? modifiedUtc = null)
    {
        var path = Path.Combine(_dir, fileName);
        File.WriteAllText(path, content);
        if (modifiedUtc is { } stamp)
        {
            // The file-date watermark stamp is second-precision (yyyyMMddHHmmss), so a sub-second mtime reads as
            // "newer than" its own stamp; pin a whole-second mtime so the quiet-day test is about the reset, not
            // about mtime precision.
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return new SourceSpec { Type = "csv", Location = path, Options = new Dictionary<string, string?>() };
    }

    // The chained landing shape: an append-mode flow that generates the typed view (inference on), which is
    // what makes it participate in the reset at all.
    private static FlowDefinition Flow(string connectionString, SourceSpec source, string table, bool resetWhenConsolidated = true, IncrementalSpec? incremental = null)
        => new()
        {
            Name = table,
            Source = source,
            Target = new TargetSpec { Connection = connectionString, Schema = IntegrationDb.Schema, Table = table },
            Load = new LoadPolicy { Mode = LoadMode.Append, ResetWhenConsolidated = resetWhenConsolidated },
            Inference = new TypeInferencePolicy { Enabled = true },
            Incremental = incremental,
        };

    private static LandingReset Verdict(bool authorized) => new()
    {
        Authorized = authorized,
        Reason = authorized ? "every consumer succeeded after the last load" : "consumer 'ods' has not caught up",
    };

    private static Task<FlowResult> RunAsync(FlowDefinition flow, LandingReset? verdict)
        => IntegrationDb.RealRunner().RunAsync(flow, null, null, null, null, null, verdict);

    private static async Task DropAsync(string cs, string table)
    {
        await IntegrationDb.ExecuteAsync(cs, $"DROP VIEW IF EXISTS [{IntegrationDb.Schema}].[v_{table}];");
        await IntegrationDb.DropTableAsync(cs, table);
    }

    [SkippableFact]
    public async Task AuthorizedVerdict_ResetsTheLandingBeforeTheLoad()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_Reset_" + Guid.NewGuid().ToString("N")[..8];
        await DropAsync(cs, table);
        try
        {
            var first = await RunAsync(Flow(cs, Csv("day1.csv", "Id,Name\n1,Alpha\n2,Beta\n"), table), verdict: null);
            Assert.Equal(FlowStatus.Success, first.Status);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // The consolidated verdict: the next run frees the landing table, so it ends with only its own rows.
            var second = await RunAsync(Flow(cs, Csv("day2.csv", "Id,Name\n3,Gamma\n"), table), Verdict(authorized: true));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(1, await IntegrationDb.RowCountAsync(cs, table));
            Assert.Equal(1, await IntegrationDb.ScalarAsync<int>(cs, $"SELECT COUNT(*) FROM [{IntegrationDb.Schema}].[{table}] WHERE [Id] = '3'"));
        }
        finally
        {
            await DropAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task BlockedVerdict_Appends_AndKeepsTheStagedRows()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_ResetBlk_" + Guid.NewGuid().ToString("N")[..8];
        await DropAsync(cs, table);
        try
        {
            await RunAsync(Flow(cs, Csv("day1.csv", "Id,Name\n1,Alpha\n2,Beta\n"), table), verdict: null);

            var second = await RunAsync(Flow(cs, Csv("day2.csv", "Id,Name\n3,Gamma\n"), table), Verdict(authorized: false));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await DropAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task OptOut_ResetWhenConsolidatedFalse_AppendsEvenWhenAuthorized()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_ResetOpt_" + Guid.NewGuid().ToString("N")[..8];
        await DropAsync(cs, table);
        try
        {
            await RunAsync(Flow(cs, Csv("day1.csv", "Id,Name\n1,Alpha\n2,Beta\n"), table, resetWhenConsolidated: false), verdict: null);

            var second = await RunAsync(
                Flow(cs, Csv("day2.csv", "Id,Name\n3,Gamma\n"), table, resetWhenConsolidated: false),
                Verdict(authorized: true));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(3, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await DropAsync(cs, table);
        }
    }

    [SkippableFact]
    public async Task QuietIncrementalDay_NoNewFiles_NeverResets()
    {
        var cs = IntegrationDb.Require();
        var table = "IT_ResetNoop_" + Guid.NewGuid().ToString("N")[..8];
        await DropAsync(cs, table);
        try
        {
            var incremental = new IncrementalSpec { DateColumn = "FileDate_DW" };
            var now = DateTime.UtcNow;
            var wholeSecond = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, DateTimeKind.Utc).AddHours(-1);
            var source = Csv("day1.csv", "Id,Name\n1,Alpha\n2,Beta\n", wholeSecond);
            await RunAsync(Flow(cs, source, table, incremental: incremental), verdict: null);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));

            // Same file, nothing newer than the watermark: the run is a clean no-op, and because the reset runs
            // only after the source read finds files, an authorized verdict on a quiet day must not touch the
            // table (a downstream full-reload consumer would otherwise reload from an emptied landing).
            var second = await RunAsync(Flow(cs, source, table, incremental: incremental), Verdict(authorized: true));

            Assert.Equal(FlowStatus.Success, second.Status);
            Assert.Equal(0, second.RowsLoaded);
            Assert.Equal(2, await IntegrationDb.RowCountAsync(cs, table));
        }
        finally
        {
            await DropAsync(cs, table);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort scratch cleanup: a straggling handle on a temp file must not fail the test run.
        }
    }
}
