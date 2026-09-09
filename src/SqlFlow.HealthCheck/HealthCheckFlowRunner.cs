using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.ML;
using Microsoft.ML.AutoML;
using Microsoft.ML.Data;
using SqlFlow.Core.Connections;
using SqlFlow.Core.HealthChecks;
using SqlFlow.Core.Ingestion;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.HealthCheck;

/// <summary>
/// Executes a health-check flow: query every monitored metric's per-date series in one scan of the target
/// (plus the table-level data-quality probes), then per metric: complete and featurize the series, fit the
/// Theil-Sen robust trend, train (or reuse) the AutoML calendar model over the detrended values, score every
/// date as trend plus calendar pattern, tag the statistically significant deviations with the generalized ESD
/// test, and report PELT level shifts separately from point anomalies. Series too short to train fall back to
/// the weekday-median baseline, so a brand-new table still gets missing-data detection. Trained models
/// persist per metric through <see cref="IHealthCheckModelStore"/>; the scored series come back as the
/// canonical <see cref="HealthCheckReport"/>. One failing metric is reported in its own section and fails the
/// run without silencing the other metrics. It shares the run-log seam with the other runners (FlowType
/// 'hc'). The catch keeps the original error and never throws.
///
/// One legacy deviation, on purpose: the legacy engine declared seven calendar features but never wired them
/// into AutoML, which then auto-featurized every column of the row, including the observed value itself, so
/// the model partly memorized its own target. V3 trains on exactly the calendar features the legacy intended.
/// </summary>
public sealed class HealthCheckFlowRunner
{
    /// <summary>Mature series points below which AutoML training is skipped in favor of the weekday-median
    /// baseline: the experiment needs something to generalize from, and the 80/20 split needs rows on both
    /// sides.</summary>
    public const int MinimumPointsForTraining = 10;

    /// <summary>The seed every MLContext uses, so two runs over the same series train the same model.</summary>
    private const int MlSeed = 42;

    private static readonly string[] FeatureColumns =
    [
        nameof(SeriesRow.Year),
        nameof(SeriesRow.Quarter),
        nameof(SeriesRow.WeekOfYear),
        nameof(SeriesRow.MonthNumber),
        nameof(SeriesRow.DayOfWeekNumber),
        nameof(SeriesRow.IsWeekend),
        nameof(SeriesRow.IsHoliday),
    ];

    private readonly IConnectionResolver _resolver;
    private readonly IHealthCheckModelStore _store;
    private readonly IIngestionRunLog _runLog;

    public HealthCheckFlowRunner(IConnectionResolver resolver, IHealthCheckModelStore store, IIngestionRunLog? runLog = null)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(store);
        _resolver = resolver;
        _store = store;
        _runLog = runLog ?? NullIngestionRunLog.Instance;
    }

    /// <param name="flow">The validated flow.</param>
    /// <param name="options">Per-execution options (run log sink, exec mode).</param>
    /// <param name="asOfUtc">The instant the series is judged against (trailing-gap and maturity anchor);
    /// now when null. Injectable so tests pin the calendar.</param>
    /// <param name="ct">Cancels the SQL reads and the AutoML experiments.</param>
    public async Task<HealthCheckRunOutcome> RunAsync(
        HealthCheckFlow flow, IngestionRunOptions? options = null, DateTime? asOfUtc = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(flow);
        options ??= new IngestionRunOptions();

        var runId = options.RunId ?? Guid.NewGuid();
        var startUtc = DateTime.UtcNow;
        var asOfDate = (asOfUtc ?? startUtc).Date;
        var events = options.Events ?? NullRunEventSink.Instance;
        var statements = options.StatementSink ?? NullRunStatementSink.Instance;

        // The executed SQL is captured unconditionally (the result carries it on success AND failure) and
        // woven into the event log at Trace level, the same dual feed as every other runner.
        var trace = new List<SqlTraceEntry>();
        void Trace(string step, string sql)
        {
            var entry = new SqlTraceEntry { Sequence = trace.Count + 1, Step = step, Sql = sql };
            trace.Add(entry);
            events.Log(RunLogLevel.Trace, step, sql);
            statements.Report(entry);
        }

        var seriesSql = HealthCheckSqlBuilder.SeriesSelect(flow, asOfDate);
        var qualitySql = HealthCheckSqlBuilder.DataQualitySelect(flow, asOfDate);
        try
        {
            events.Log(RunLogLevel.Info, "run.start",
                $"health check '{flow.SysAlias}' (flow {flow.FlowId}): {flow.Metrics.Count} metric(s) per {flow.DateColumn} on {flow.Target.QualifiedName} via '{flow.Server}'");

            var resolved = await _resolver.ResolveAsync(flow.ConnectionReference, ConnectionRole.Target, ct: ct).ConfigureAwait(false);

            Trace("series.select", seriesSql);
            var loaded = await LoadSeriesAsync(resolved.CanonicalString, seriesSql, flow, ct).ConfigureAwait(false);
            events.Log(RunLogLevel.Info, "series.load", $"{loaded.Dates.Count} observed date(s) loaded for {flow.Metrics.Count} metric(s)");

            Trace("quality.select", qualitySql);
            var quality = await LoadDataQualityAsync(resolved.CanonicalString, qualitySql, ct).ConfigureAwait(false);
            if (quality.FutureDatedRows > 0 || quality.SentinelDatedRows > 0 || quality.NullDatedRows > 0)
            {
                events.Log(RunLogLevel.Info, "quality.probe",
                    $"data quality: {quality.FutureDatedRows} future-dated, {quality.SentinelDatedRows} sentinel-dated, {quality.NullDatedRows} NULL-dated row(s)");
            }

            if (loaded.Dates.Count < 2)
            {
                throw new InvalidOperationException(
                    $"The series query returned {loaded.Dates.Count} date(s); a health check needs at least two to detect the cadence. " +
                    "Check the date column, the filter, and whether the table has been loaded.");
            }

            var frequency = HealthCheckEngine.DetectFrequency(loaded.Dates);
            var missing = HealthCheckEngine.DetectMissingDates(loaded.Dates, frequency, expectThrough: asOfDate);
            var trailingGap = missing.Count(d => d > loaded.Dates[^1]);
            events.Log(RunLogLevel.Debug, "series.frequency",
                $"cadence {frequency}; {missing.Count} expected date(s) missing" +
                (trailingGap > 0 ? $" ({trailingGap} trailing: the newest data is older than the cadence expects)" : string.Empty));

            // Score each metric independently; one broken metric reports in its own section instead of
            // silencing the others.
            var metricReports = new List<HealthCheckMetricReport>(flow.Metrics.Count);
            var metricResults = new List<HealthCheckMetricResult>(flow.Metrics.Count);
            foreach (var (metric, values) in flow.Metrics.Zip(loaded.MetricValues))
            {
                try
                {
                    var (metricReport, metricResult) = ScoreMetric(flow, metric, loaded.Dates, values, missing, frequency, asOfDate, events, ct);
                    metricReports.Add(metricReport);
                    metricResults.Add(metricResult);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    events.Log(RunLogLevel.Info, "metric.failed", $"[{metric.Name}] {ex.Message}");
                    metricResults.Add(new HealthCheckMetricResult
                    {
                        Name = metric.Name,
                        SeriesPoints = 0,
                        ImputedPoints = 0,
                        ImmaturePoints = 0,
                        Anomalies = 0,
                        LevelShifts = 0,
                        ModelTrained = false,
                        ModelTrainer = string.Empty,
                        Error = SecretHygiene.RedactedMessage(ex),
                    });
                }
            }

            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            var failed = metricResults.Where(m => m.Error is not null).ToList();
            var success = failed.Count == 0;
            var totalAnomalies = metricResults.Sum(m => m.Anomalies);

            var report = new HealthCheckReport
            {
                FlowName = flow.SysAlias,
                RunId = runId,
                WrittenUtc = endUtc,
                Target = flow.Target.QualifiedName,
                DateColumn = flow.DateColumn,
                FilterCriteria = flow.FilterCriteria,
                Frequency = frequency.ToString(),
                MaturityDays = flow.MaturityDays,
                DataQuality = quality,
                Metrics = metricReports,
            };

            var result = new HealthCheckRunResult
            {
                RunId = runId,
                FlowId = flow.FlowId,
                Success = success,
                StartTimeUtc = startUtc,
                EndTimeUtc = endUtc,
                DurationSeconds = duration,
                SqlTrace = trace,
                Error = success ? null : $"{failed.Count} of {flow.Metrics.Count} metric(s) failed: " +
                    string.Join("; ", failed.Select(m => $"[{m.Name}] {m.Error}")),
                Frequency = frequency.ToString(),
                DataQuality = quality,
                MetricResults = metricResults,
                TotalAnomalies = totalAnomalies,
            };

            events.Log(RunLogLevel.Info, "run.end",
                success
                    ? $"SUCCESS in {duration}s: {totalAnomalies} anomalies across {flow.Metrics.Count} metric(s)"
                    : $"FAILED after {duration}s: {result.Error}");
            await _runLog.WriteAsync(
                BuildRecord(flow, options, runId, startUtc, endUtc, duration, loaded.Dates.Count, success, result.Error, seriesSql, trace),
                ct).ConfigureAwait(false);

            return new HealthCheckRunOutcome { Result = result, Report = report };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SqlTrace.MarkLastFailed(trace, statements, ex.Message);
            var endUtc = DateTime.UtcNow;
            var duration = DurationSeconds(startUtc, endUtc);
            events.Log(RunLogLevel.Info, "run.end", $"FAILED after {duration}s: {ex.Message}");
            try
            {
                await _runLog.WriteAsync(
                    BuildRecord(flow, options, runId, startUtc, endUtc, duration, fetched: 0, success: false, error: ex.Message, seriesSql, trace),
                    ct).ConfigureAwait(false);
            }
            catch (Exception logEx) when (logEx is not OperationCanceledException)
            {
                // Best-effort: a run-log write failure must not mask the real error, which is returned below.
            }

            return new HealthCheckRunOutcome
            {
                Result = new HealthCheckRunResult
                {
                    RunId = runId,
                    FlowId = flow.FlowId,
                    Success = false,
                    StartTimeUtc = startUtc,
                    EndTimeUtc = endUtc,
                    DurationSeconds = duration,
                    SqlTrace = trace,
                    Error = SecretHygiene.RedactedMessage(ex),
                },
            };
        }
    }

    /// <summary>The full per-metric pipeline: complete, featurize, impute, detrend, model, score, tag, shift.</summary>
    private (HealthCheckMetricReport Report, HealthCheckMetricResult Result) ScoreMetric(
        HealthCheckFlow flow, HealthCheckMetric metric, IReadOnlyList<DateTime> dates, IReadOnlyList<double?> values,
        IReadOnlyList<DateTime> missing, DataFrequency frequency, DateTime asOfDate, IRunEventSink events, CancellationToken ct)
    {
        var series = new List<SeriesRow>(dates.Count + missing.Count);
        var nullValues = 0;
        for (var i = 0; i < dates.Count; i++)
        {
            if (values[i] is null)
            {
                nullValues++;
            }

            series.Add(new SeriesRow { Date = dates[i], BaseValue = (float)(values[i] ?? 0) });
        }

        if (nullValues > 0)
        {
            events.Log(RunLogLevel.Debug, "series.load", $"[{metric.Name}] {nullValues} date(s) had a NULL value and count as missing data");
        }

        HealthCheckEngine.AddMissingDates(series, missing);
        HealthCheckEngine.MarkImmaturePoints(series, asOfDate, flow.MaturityDays);
        HealthCheckEngine.ApplyDateFeatures(series, flow.Holidays);
        HealthCheckEngine.Impute(series);
        var imputedPoints = series.Count(r => r.IsNoData == 1);
        var immaturePoints = series.Count(r => r.IsImmature);
        events.Log(RunLogLevel.Debug, "series.impute",
            $"[{metric.Name}] {series.Count} point(s), {imputedPoints} imputed, {immaturePoints} immature (window {HealthCheckEngine.MovingAverageWindow})");

        var mature = series.Where(r => !r.IsImmature).ToList();
        var mlContext = new MLContext(seed: MlSeed);
        var (predict, modelInfo, trendInfo) = ResolveModel(mlContext, flow, metric, series, mature, events, ct);

        foreach (var row in series)
        {
            row.PredictedValue = predict(row);
        }

        events.Log(RunLogLevel.Debug, "score.predict", $"[{metric.Name}] {series.Count} point(s) scored by {modelInfo.Trainer}");

        if (HealthCheckEngine.TagAnomalies(series, flow.AnomalyThreshold, flow.EsdAlpha, flow.MaxAnomalyFraction))
        {
            events.Log(RunLogLevel.Info, "anomaly.tag",
                $"[{metric.Name}] {series.Count(r => r.AnomalyDetected)} anomalies " +
                $"(ESD alpha {flow.EsdAlpha.ToString("0.###", CultureInfo.InvariantCulture)}, " +
                $"severity floor {flow.AnomalyThreshold.ToString("0.##", CultureInfo.InvariantCulture)} sigma)");
        }
        else
        {
            events.Log(RunLogLevel.Info, "anomaly.tag",
                $"[{metric.Name}] statistical tagging skipped: fewer than {HealthCheckEngine.MinimumPointsForTagging} mature observed point(s); missing-data detection still applies");
        }

        // Level shifts live in the residuals of the mature observed points: a regime change is a finding of
        // its own, not a wall of daily anomalies.
        var observedMature = mature.Where(r => r.IsNoData == 0).ToList();
        var shifts = PeltDetector
            .Detect(observedMature.Select(r => (double)(r.BaseValue - r.PredictedValue)).ToList())
            .Select(s => new HealthCheckLevelShift
            {
                Date = observedMature[s.Index].Date,
                MedianBefore = Math.Round(s.MedianBefore, 4),
                MedianAfter = Math.Round(s.MedianAfter, 4),
                MagnitudeSigma = Math.Round(s.MagnitudeSigma, 4),
            })
            .ToList();
        foreach (var shift in shifts)
        {
            events.Log(RunLogLevel.Info, "levelshift",
                $"[{metric.Name}] regime change on {shift.Date:yyyy-MM-dd}: residual median {shift.MedianBefore:0.##} -> {shift.MedianAfter:0.##} ({shift.MagnitudeSigma:0.#} sigma). " +
                "A backfill, purge, or new source; retrain (--retrain) to adopt the new level as normal.");
        }

        var fit = HealthCheckEngine.ComputeMetrics(series);
        if (fit is not null)
        {
            events.Log(RunLogLevel.Debug, "metrics",
                $"[{metric.Name}] MAE {fit.MeanAbsoluteError:0.###}, RMSE {fit.RootMeanSquaredError:0.###}, " +
                $"MAPE {fit.MeanAbsolutePercentageError:0.##}%, R2 {fit.RSquared:0.####}");
        }

        var points = series
            .Select(r => new HealthCheckSeriesPoint
            {
                Date = r.Date,
                Actual = r.BaseValue,
                Adjusted = r.BaseValueAdjusted,
                Predicted = r.PredictedValue,
                Severity = r.Severity,
                Imputed = r.IsNoData == 1,
                Immature = r.IsImmature,
                Anomaly = r.AnomalyDetected,
                AnomalyReason = r.AnomalyReason,
            })
            .ToList();
        var anomalies = points.Where(p => p.Anomaly).ToList();

        var report = new HealthCheckMetricReport
        {
            Name = metric.Name,
            Expression = metric.Expression,
            Model = modelInfo,
            Fit = fit,
            Trend = trendInfo,
            LevelShifts = shifts,
            AnomalySummary = new HealthCheckAnomalySummary
            {
                Total = anomalies.Count,
                MissingData = anomalies.Count(p => p.AnomalyReason == "Missing Data"),
                AbsoluteDifference = anomalies.Count(p => p.AnomalyReason == "Absolute Difference"),
                RelativeDifference = anomalies.Count(p => p.AnomalyReason == "Relative Difference"),
                ImmaturePoints = immaturePoints,
                FirstAnomalyDate = anomalies.Count > 0 ? anomalies.Min(p => p.Date) : null,
                LastAnomalyDate = anomalies.Count > 0 ? anomalies.Max(p => p.Date) : null,
            },
            Series = points,
        };

        var result = new HealthCheckMetricResult
        {
            Name = metric.Name,
            SeriesPoints = series.Count,
            ImputedPoints = imputedPoints,
            ImmaturePoints = immaturePoints,
            Anomalies = anomalies.Count,
            LevelShifts = shifts.Count,
            ModelTrained = modelInfo.TrainedThisRun,
            ModelTrainer = modelInfo.Trainer,
            Fit = fit,
        };

        return (report, result);
    }

    /// <summary>Resolves the metric's expectation function per the training policy: the stored model, a
    /// freshly trained one, or the weekday-median baseline for short histories.</summary>
    private (Func<SeriesRow, float> Predict, HealthCheckModelInfo Info, HealthCheckTrendInfo Trend) ResolveModel(
        MLContext mlContext, HealthCheckFlow flow, HealthCheckMetric metric,
        List<SeriesRow> series, List<SeriesRow> mature, IRunEventSink events, CancellationToken ct)
    {
        var stored = _store.Load(flow.SysAlias, metric.Name);
        var trainReason = HealthCheckTrainingDecision.Resolve(
            flow.Training, flow.RetrainAfterDays, stored?.Metadata, DateTime.UtcNow, _store.ModelPath(flow.SysAlias, metric.Name));

        if (trainReason is null)
        {
            // stored is non-null on every reuse path: 'never' throws above when it is missing, and 'auto'
            // only clears the reason once a current, fresh-enough stored model exists.
            var metadata = stored!.Metadata;
            events.Log(RunLogLevel.Info, "model.decision",
                $"[{metric.Name}] reusing stored model ({metadata.Trainer}, trained {metadata.TrainedAtUtc:yyyy-MM-dd HH:mm}Z)");

            using var memory = new MemoryStream(stored.ModelBytes);
            var model = mlContext.Model.Load(memory, out _);
            var engine = mlContext.Model.CreatePredictionEngine<SeriesRow, SeriesPrediction>(model);
            var trend = new TrendFit
            {
                AnchorDate = metadata.TrendAnchorDate,
                SlopePerDay = metadata.TrendSlopePerDay,
                Intercept = metadata.TrendIntercept,
                WindowPoints = metadata.TrendWindowPoints,
            };

            return (
                row => (float)(trend.ValueAt(row.Date) + engine.Predict(row).PredictedValue),
                new HealthCheckModelInfo
                {
                    Trainer = metadata.Trainer,
                    TrainedThisRun = false,
                    TrainedAtUtc = metadata.TrainedAtUtc,
                    ValidationRSquared = metadata.ValidationRSquared,
                },
                new HealthCheckTrendInfo { SlopePerDay = Math.Round(trend.SlopePerDay, 6), WindowPoints = trend.WindowPoints });
        }

        events.Log(RunLogLevel.Info, "model.decision", $"[{metric.Name}] training a fresh model: {trainReason}");

        if (mature.Count < MinimumPointsForTraining)
        {
            // Too short for AutoML, not too short to be useful: the weekday-median baseline still catches
            // missing data and gross deviations, and the flow graduates to a trained model as history grows.
            events.Log(RunLogLevel.Info, "model.baseline",
                $"[{metric.Name}] {mature.Count} mature point(s) is below the training minimum of {MinimumPointsForTraining}; " +
                $"scoring with {BaselineModel.TrainerName} until more history accumulates");
            var baseline = BaselineModel.Fit(mature.Count > 0 ? mature : series);

            return (
                row => (float)baseline.Predict(row.Date),
                new HealthCheckModelInfo
                {
                    Trainer = BaselineModel.TrainerName,
                    TrainedThisRun = true,
                    TrainedAtUtc = DateTime.UtcNow,
                },
                new HealthCheckTrendInfo { SlopePerDay = 0, WindowPoints = 0 });
        }

        return Train(mlContext, flow, metric, mature, events, ct);
    }

    /// <summary>Fits the robust trend, runs the AutoML regression over the calendar features of the
    /// detrended series, evaluates the holdout, persists the winner, and describes it.</summary>
    private (Func<SeriesRow, float> Predict, HealthCheckModelInfo Info, HealthCheckTrendInfo Trend) Train(
        MLContext mlContext, HealthCheckFlow flow, HealthCheckMetric metric,
        List<SeriesRow> mature, IRunEventSink events, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        // The trend carries the level and growth; the ML learns the calendar pattern AROUND the trend.
        var trend = TrendEstimator.Fit(mature.Select(r => (r.Date, (double)r.BaseValueAdjusted)).ToList());
        foreach (var row in mature)
        {
            row.DetrendedLabel = (float)(row.BaseValueAdjusted - trend.ValueAt(row.Date));
        }

        events.Log(RunLogLevel.Debug, "model.trend",
            $"[{metric.Name}] Theil-Sen trend {trend.SlopePerDay.ToString("0.####", CultureInfo.InvariantCulture)}/day over the last {trend.WindowPoints} point(s)");

        var trainingData = mlContext.Data.LoadFromEnumerable(mature);
        var split = mlContext.Data.TrainTestSplit(trainingData, testFraction: 0.2, seed: MlSeed);

        // The model must learn the expectation from the calendar alone: the label and every bookkeeping
        // column (above all the observed value itself) are excluded from featurization.
        var columns = new ColumnInformation { LabelColumnName = nameof(SeriesRow.DetrendedLabel) };
        foreach (var feature in FeatureColumns)
        {
            columns.NumericColumnNames.Add(feature);
        }

        columns.IgnoredColumnNames.Add(nameof(SeriesRow.Date));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.BaseValue));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.BaseValueAdjusted));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.PredictedValue));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.IsNoData));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.AnomalyDetected));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.AnomalyReason));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.Severity));
        columns.IgnoredColumnNames.Add(nameof(SeriesRow.IsImmature));

        var settings = new RegressionExperimentSettings
        {
            MaxExperimentTimeInSeconds = (uint)flow.MaxExperimentSeconds,
            OptimizingMetric = RegressionMetric.RSquared,
            CancellationToken = ct,
            CacheDirectoryName = Path.Combine(Path.GetTempPath(), "SqlFlowMlCache"),
        };

        events.Log(RunLogLevel.Info, "model.train",
            $"[{metric.Name}] AutoML regression over {FeatureColumns.Length} calendar feature(s), budget {flow.MaxExperimentSeconds}s");

        var trials = new List<HealthCheckTrialResult>();
        var progress = new TrialProgressHandler(metric.Name, events, trials);
        var experiment = mlContext.Auto().CreateRegressionExperiment(settings);
        var experimentResult = experiment.Execute(split.TrainSet, columnInformation: columns, progressHandler: progress);
        ct.ThrowIfCancellationRequested();

        var bestRun = experimentResult.BestRun;
        if (bestRun?.Model is null)
        {
            throw new InvalidOperationException(
                $"AutoML produced no model within {flow.MaxExperimentSeconds}s; raise ml.maxExperimentSeconds.");
        }

        stopwatch.Stop();
        var validationRSquared = bestRun.ValidationMetrics?.RSquared;
        events.Log(RunLogLevel.Info, "model.train",
            $"[{metric.Name}] best of {trials.Count} trial(s): {bestRun.TrainerName}" +
            (validationRSquared is { } r2 ? $" (validation R2 {r2:0.####})" : string.Empty) +
            $" in {stopwatch.Elapsed.TotalSeconds:0.#}s");

        EvaluateHoldout(mlContext, metric.Name, bestRun.Model, split.TestSet, events);

        byte[] modelBytes;
        using (var memory = new MemoryStream())
        {
            mlContext.Model.Save(bestRun.Model, null, memory);
            modelBytes = memory.ToArray();
        }

        var trainedAtUtc = DateTime.UtcNow;
        var modelPath = _store.Save(flow.SysAlias, metric.Name, modelBytes, new HealthCheckModelMetadata
        {
            EngineVersion = HealthCheckModelMetadata.CurrentEngineVersion,
            FlowName = flow.SysAlias,
            Trainer = bestRun.TrainerName,
            TrainedAtUtc = trainedAtUtc,
            TrainingSeconds = stopwatch.Elapsed.TotalSeconds,
            Trials = trials.Count,
            ValidationRSquared = validationRSquared,
            TrendAnchorDate = trend.AnchorDate,
            TrendSlopePerDay = trend.SlopePerDay,
            TrendIntercept = trend.Intercept,
            TrendWindowPoints = trend.WindowPoints,
            TrialResults = trials,
        });
        events.Log(RunLogLevel.Debug, "model.save", $"[{metric.Name}] model persisted to {modelPath}");

        var engine = mlContext.Model.CreatePredictionEngine<SeriesRow, SeriesPrediction>(bestRun.Model);
        return (
            row => (float)(trend.ValueAt(row.Date) + engine.Predict(row).PredictedValue),
            new HealthCheckModelInfo
            {
                Trainer = bestRun.TrainerName,
                TrainedThisRun = true,
                TrainedAtUtc = trainedAtUtc,
                TrainingSeconds = stopwatch.Elapsed.TotalSeconds,
                Trials = trials.Count,
                ValidationRSquared = validationRSquared,
            },
            new HealthCheckTrendInfo { SlopePerDay = Math.Round(trend.SlopePerDay, 6), WindowPoints = trend.WindowPoints });
    }

    /// <summary>Logs the best model's fit on the holdout split. The 80/20 split is random, so at the minimum
    /// series size the holdout can come up empty; that is reported, not crashed on.</summary>
    private static void EvaluateHoldout(MLContext mlContext, string metricName, ITransformer model, IDataView testSet, IRunEventSink events)
    {
        var testRows = mlContext.Data.CreateEnumerable<SeriesRow>(testSet, reuseRowObject: false).Count();
        if (testRows == 0)
        {
            events.Log(RunLogLevel.Debug, "model.evaluate", $"[{metricName}] holdout split is empty; skipping the holdout evaluation");
            return;
        }

        var metrics = mlContext.Regression.Evaluate(model.Transform(testSet), labelColumnName: nameof(SeriesRow.DetrendedLabel));
        events.Log(RunLogLevel.Debug, "model.evaluate",
            $"[{metricName}] holdout ({testRows} row(s)): R2 {metrics.RSquared:0.####}, MAE {metrics.MeanAbsoluteError:0.###}");
    }

    private sealed record LoadedSeries(IReadOnlyList<DateTime> Dates, IReadOnlyList<IReadOnlyList<double?>> MetricValues);

    /// <summary>Reads the multi-metric series query into a shared date axis plus one value column per metric.
    /// A NULL aggregate stays null here (per-metric missing data); a non-date first column is a clear error.</summary>
    private static async Task<LoadedSeries> LoadSeriesAsync(string connectionString, string seriesSql, HealthCheckFlow flow, CancellationToken ct)
    {
        var dates = new List<DateTime>();
        var metricValues = new List<List<double?>>(flow.Metrics.Count);
        for (var m = 0; m < flow.Metrics.Count; m++)
        {
            metricValues.Add([]);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(seriesSql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            DateTime date;
            try
            {
                date = reader.GetDateTime(0);
            }
            catch (InvalidCastException ex)
            {
                throw new InvalidOperationException(
                    $"The date column '{flow.DateColumn}' of {flow.Target.QualifiedName} is not a date/datetime type; " +
                    "point dateColumn at a column SQL Server can return as a date.", ex);
            }

            dates.Add(date);
            for (var m = 0; m < flow.Metrics.Count; m++)
            {
                metricValues[m].Add(await reader.IsDBNullAsync(m + 1, ct).ConfigureAwait(false)
                    ? null
                    : Convert.ToDouble(reader.GetValue(m + 1), CultureInfo.InvariantCulture));
            }
        }

        // The query orders by the date column, but a datetime axis can carry same-day timestamps; the engine
        // sorts the per-metric rows again anyway. The shared axis keeps query order.
        return new LoadedSeries(dates, metricValues.Select(v => (IReadOnlyList<double?>)v).ToList());
    }

    private static async Task<HealthCheckDataQuality> LoadDataQualityAsync(string connectionString, string qualitySql, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(qualitySql, connection) { CommandTimeout = 0 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The data-quality query returned no row.");
        }

        return new HealthCheckDataQuality
        {
            FutureDatedRows = reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            SentinelDatedRows = reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            NullDatedRows = reader.IsDBNull(2) ? 0 : reader.GetInt64(2),
        };
    }

    private static IngestionRunRecord BuildRecord(
        HealthCheckFlow flow, IngestionRunOptions options, Guid runId, DateTime startUtc, DateTime endUtc, int durationSeconds,
        long fetched, bool success, string? error, string seriesSql, IReadOnlyList<SqlTraceEntry> trace)
        => new()
        {
            RunId = runId,
            FlowId = flow.FlowId,
            FlowType = flow.FlowType,
            Process = $"-->{flow.Server}.{flow.Target.QualifiedName}",
            Batch = flow.Batch,
            SysAlias = flow.SysAlias,
            ExecMode = options.ExecMode,
            StartTimeUtc = startUtc,
            EndTimeUtc = endUtc,
            DurationSeconds = durationSeconds,
            RowsFetched = fetched,
            Success = success,
            SelectCmd = seriesSql,
            TraceLog = trace.Count > 0 ? SqlTrace.Render(trace) : null,
            Error = error,
        };

    private static int DurationSeconds(DateTime startUtc, DateTime endUtc) => (int)Math.Max(0, (endUtc - startUtc).TotalSeconds);

    /// <summary>Feeds AutoML trial progress into the run log (Debug) and collects the per-trial results that
    /// become the model metadata (the legacy MLModelSelection column, kept lossless).</summary>
    private sealed class TrialProgressHandler : IProgress<RunDetail<RegressionMetrics>>
    {
        private readonly string _metricName;
        private readonly IRunEventSink _events;
        private readonly List<HealthCheckTrialResult> _trials;

        public TrialProgressHandler(string metricName, IRunEventSink events, List<HealthCheckTrialResult> trials)
        {
            _metricName = metricName;
            _events = events;
            _trials = trials;
        }

        public void Report(RunDetail<RegressionMetrics> value)
        {
            var trial = new HealthCheckTrialResult
            {
                Trainer = value.TrainerName,
                RSquared = value.ValidationMetrics?.RSquared,
                MeanAbsoluteError = value.ValidationMetrics?.MeanAbsoluteError,
                RootMeanSquaredError = value.ValidationMetrics?.RootMeanSquaredError,
                RuntimeSeconds = value.RuntimeInSeconds,
            };

            lock (_trials)
            {
                _trials.Add(trial);
            }

            _events.Log(RunLogLevel.Debug, "model.trial",
                trial.RSquared is { } r2
                    ? $"[{_metricName}] {trial.Trainer}: R2 {r2:0.####} ({trial.RuntimeSeconds:0.#}s)"
                    : $"[{_metricName}] {trial.Trainer}: failed ({value.Exception?.Message ?? "no metrics"})");
        }
    }
}
