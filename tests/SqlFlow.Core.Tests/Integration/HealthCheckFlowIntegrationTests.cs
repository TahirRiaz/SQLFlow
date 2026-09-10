using System.Text;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.HealthCheck;
using Xunit;

namespace SqlFlow.Tests.Integration;

/// <summary>
/// Exercises the health-check flow end to end against the real sink: seventeen weeks of GROWING synthetic
/// orders with a weekly rhythm, one silent day, one collapsed day, and seeded date pathologies (future,
/// sentinel, NULL dates), watched by two metrics (row count and revenue) in one scan. The first run trains
/// per-metric models and persists them under .sqlflow/state; the second run reuses them without retraining.
/// The injected days must come back tagged with the right reasons regardless of AutoML's exact fit, and the
/// quality probes must count the seeded pathologies exactly.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HealthCheckFlowIntegrationTests : IDisposable
{
    private const string Table = "_SfHc_Orders";
    private readonly string _anchor = Path.Combine(Path.GetTempPath(), "sqlflow-hc-it-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        if (Directory.Exists(_anchor))
        {
            Directory.Delete(_anchor, recursive: true);
        }
    }

    private static DataSource SinkConnection() => new()
    {
        Alias = "sink",
        Kind = DataSourceKind.MSSQL,
        ConnectionRef = "${env:SQLFlowSinkConStr}",
        Credential = new CredentialProfile { Mode = CredentialMode.InlineConnectionString },
    };

    private static HealthCheckFlow Flow(string dbName, int flowId, string alias) => new()
    {
        FlowId = flowId,
        SysAlias = alias,
        Server = "sink",
        Target = RelationalObject.Parse($"{dbName}.dbo.{Table}"),
        DateColumn = "OrderDate",
        Metrics =
        [
            new HealthCheckMetric { Name = "rowCount", Expression = "COUNT(*)" },
            new HealthCheckMetric { Name = "revenue", Expression = "SUM(Amount)" },
        ],
        MaxExperimentSeconds = 8,
    };

    [SkippableFact]
    public async Task Runner_TrainsScoresPersists_ThenReusesTheStoredModels()
    {
        var cs = IntegrationDb.Require();
        var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        var (silentDay, collapsedDay) = await SeedOrders(cs);

        try
        {
            var runner = WithoutDatabaseHealthCheck.BuildRunner([SinkConnection()], _anchor);
            var flow = Flow(dbName!, 60, "orders-hc");

            // First run: no stored models exist, so 'auto' trains both metrics, persists, and scores.
            var first = await runner.RunAsync(flow, new IngestionRunOptions { ExecMode = "test", Events = new RunLogger(RunLogLevel.Trace) });
            Assert.True(first.Result.Success, first.Result.Error);
            Assert.Equal("Daily", first.Result.Frequency);
            Assert.Equal(2, first.Result.SqlTrace.Count); // series + quality, one scan each

            // The seeded date pathologies, counted exactly.
            Assert.NotNull(first.Result.DataQuality);
            Assert.Equal(3, first.Result.DataQuality.FutureDatedRows);
            Assert.Equal(2, first.Result.DataQuality.SentinelDatedRows);
            Assert.Equal(2, first.Result.DataQuality.NullDatedRows);

            Assert.Equal(2, first.Result.MetricResults.Count);
            foreach (var metric in first.Result.MetricResults)
            {
                Assert.Null(metric.Error);
                Assert.True(metric.ModelTrained);
                Assert.False(string.IsNullOrWhiteSpace(metric.ModelTrainer));

                // 119 seeded days minus the silent one observed; the walk restores it and adds today
                // (data may still arrive there: immature, never counted).
                Assert.Equal(120, metric.SeriesPoints);
                Assert.Equal(2, metric.ImputedPoints);
                Assert.Equal(1, metric.ImmaturePoints);
                Assert.NotNull(metric.Fit);
            }

            Assert.NotNull(first.Report);
            Assert.Equal(2, first.Report.Metrics.Count);
            foreach (var metricReport in first.Report.Metrics)
            {
                // The two injected incidents are found with the right reasons, on both metrics.
                var bySeverity = metricReport.Series.Where(p => p.Anomaly).OrderByDescending(p => p.Severity).ToList();
                Assert.Contains(bySeverity, p => p.Date == silentDay && p.AnomalyReason == "Missing Data");
                Assert.Contains(bySeverity, p => p.Date == collapsedDay && p.AnomalyReason != "Missing Data");

                // Today (UTC, the runner's anchor) is scored, marked immature, and never counted.
                var today = metricReport.Series.Single(p => p.Date == DateTime.UtcNow.Date);
                Assert.True(today.Immature);
                Assert.False(today.Anomaly);

                // The ESD fraction cap bounds the list even if the 8s model fits loosely.
                Assert.True(metricReport.AnomalySummary.Total <= 14, $"{metricReport.Name}: {metricReport.AnomalySummary.Total} anomalies");

                // Healthy growth must not read as a regime change.
                Assert.True(metricReport.Trend.SlopePerDay > 0, $"{metricReport.Name}: slope {metricReport.Trend.SlopePerDay}");
            }

            // Both models and their sidecars landed in per-metric state folders.
            var store = new HealthCheckModelStore(_anchor);
            foreach (var metricName in new[] { "rowCount", "revenue" })
            {
                Assert.True(File.Exists(store.ModelPath("orders-hc", metricName)));
                var stored = store.Load("orders-hc", metricName);
                Assert.NotNull(stored);
                Assert.Equal(HealthCheckModelMetadata.CurrentEngineVersion, stored.Metadata.EngineVersion);
                Assert.True(stored.Metadata.Trials >= 1);
                Assert.True(stored.Metadata.TrendSlopePerDay > 0);
            }

            // Second run: 'auto' finds the fresh stored models and scores without retraining.
            var second = await runner.RunAsync(flow);
            Assert.True(second.Result.Success, second.Result.Error);
            Assert.All(second.Result.MetricResults, m => Assert.False(m.ModelTrained));
            Assert.Equal(
                first.Result.MetricResults.Select(m => m.ModelTrainer),
                second.Result.MetricResults.Select(m => m.ModelTrainer));
            Assert.NotNull(second.Report);
            Assert.All(second.Report.Metrics, m => Assert.False(m.Model.TrainedThisRun));
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, Table);
        }
    }

    [SkippableFact]
    public async Task TrainingNever_WithoutStoredModels_FailsTheMetricsClearly()
    {
        var cs = IntegrationDb.Require();
        var dbName = await IntegrationDb.ScalarAsync<string?>(cs, "SELECT DB_NAME();");
        await SeedOrders(cs);

        try
        {
            var runner = WithoutDatabaseHealthCheck.BuildRunner([SinkConnection()], _anchor);
            var flow = Flow(dbName!, 61, "orders-hc-pinned") with { Training = HealthCheckTraining.Never };

            var outcome = await runner.RunAsync(flow);

            Assert.False(outcome.Result.Success);
            Assert.NotNull(outcome.Result.Error);
            Assert.Contains("training is 'never' but no stored model exists", outcome.Result.Error, StringComparison.Ordinal);

            // Both metrics report their own failure; the envelope (with the quality probes) still exists.
            Assert.Equal(2, outcome.Result.MetricResults.Count);
            Assert.All(outcome.Result.MetricResults, m => Assert.NotNull(m.Error));
            Assert.NotNull(outcome.Report);
            Assert.Empty(outcome.Report.Metrics);
            Assert.NotNull(outcome.Result.DataQuality);
        }
        finally
        {
            await IntegrationDb.DropTableAsync(cs, Table);
        }
    }

    /// <summary>
    /// 119 days ending yesterday with weekly rhythm AND growth: weekday rows grow one per week from 20,
    /// weekends stay 6. One day is deleted entirely (silent load), a later one collapses to a single row
    /// (partial load). Both injected incidents are pinned to WEEKDAYS so the collapse is always a large,
    /// unambiguous drop (a weekday baseline of ~20-30, not a weekend's 6); otherwise whether AutoML ranks it as
    /// an anomaly would depend on which day of week the calendar happens to put the offset on, making the test
    /// flaky across date rollovers. Returns the resolved incident dates so the assertions stay in lockstep with
    /// the seed. Date pathologies on top: 3 future-dated rows, 2 sentinel-dated rows (1900-01-01), 2 NULL-dated
    /// rows.
    /// </summary>
    private static async Task<(DateTime SilentDay, DateTime CollapsedDay)> SeedOrders(string cs)
    {
        await IntegrationDb.DropTableAsync(cs, Table);
        await IntegrationDb.ExecuteAsync(cs, $"CREATE TABLE [dbo].[{Table}] ([OrderDate] date NULL, [Amount] int NOT NULL);");

        // Anchor the seeded calendar on the UTC date, exactly as the runner does (asOfDate = DateTime.UtcNow.Date).
        // Using DateTime.Today (local) here instead would put the seed and the runner on different dates whenever
        // local and UTC fall on opposite sides of midnight, shifting the series length by one and making the exact
        // SeriesPoints/today assertions fail for part of each day.
        var start = DateTime.UtcNow.Date.AddDays(-119);

        // The first weekday at or after a day offset, so the injected incidents never land on a low-baseline weekend.
        int Weekday(int index)
        {
            while (start.AddDays(index).DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            {
                index++;
            }

            return index;
        }

        var silentIndex = Weekday(40);
        var collapsedIndex = Weekday(80);

        var batch = new StringBuilder();
        var valuesInBatch = 0;

        async Task Add(string date, int amount)
        {
            if (valuesInBatch > 0)
            {
                batch.Append(',');
            }

            batch.Append('(').Append(date).Append(',').Append(amount).Append(')');
            if (++valuesInBatch == 900)
            {
                await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{Table}] ([OrderDate],[Amount]) VALUES {batch};");
                batch.Clear();
                valuesInBatch = 0;
            }
        }

        for (var day = 0; day < 119; day++)
        {
            if (day == silentIndex)
            {
                continue;
            }

            var date = start.AddDays(day);
            var isWeekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var rows = day == collapsedIndex ? 1 : (isWeekend ? 6 : 20 + day / 7);
            for (var row = 0; row < rows; row++)
            {
                await Add($"'{date:yyyy-MM-dd}'", row + 1);
            }
        }

        for (var i = 0; i < 3; i++)
        {
            await Add($"'{DateTime.UtcNow.Date.AddDays(5 + i):yyyy-MM-dd}'", 1);
        }

        for (var i = 0; i < 2; i++)
        {
            await Add("'1900-01-01'", 1);
            await Add("NULL", 1);
        }

        if (valuesInBatch > 0)
        {
            await IntegrationDb.ExecuteAsync(cs, $"INSERT INTO [dbo].[{Table}] ([OrderDate],[Amount]) VALUES {batch};");
        }

        return (start.AddDays(silentIndex), start.AddDays(collapsedIndex));
    }
}
