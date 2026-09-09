---
id: concept-healthcheck-engine
title: "Health-check detection engine internals"
type: concept
summary: "How the hc detection stack works under the hood: SQL generation, imputation, Theil-Sen, AutoML, generalized ESD, and PELT level shifts."
keywords:
  - esd test
  - rosner
  - pelt
  - level shift
  - theil-sen
  - robust statistics
  - median absolute deviation
  - student-t
  - weekday-median baseline
  - imputation
related:
  - flow-hc
  - cli-healthcheck
  - concept-run-artifacts
sourceRefs:
  - src/SqlFlow.HealthCheck/HealthCheckFlowRunner.cs
  - src/SqlFlow.HealthCheck/HealthCheckSqlBuilder.cs
  - src/SqlFlow.HealthCheck/HealthCheckEngine.cs
  - src/SqlFlow.HealthCheck/EsdDetector.cs
  - src/SqlFlow.HealthCheck/PeltDetector.cs
  - src/SqlFlow.HealthCheck/TrendEstimator.cs
  - src/SqlFlow.HealthCheck/RobustStatistics.cs
  - src/SqlFlow.HealthCheck/StudentT.cs
  - src/SqlFlow.HealthCheck/BaselineModel.cs
  - src/SqlFlow.HealthCheck/HealthCheckModelStore.cs
  - src/SqlFlow.HealthCheck/DateColumnSelector.cs
  - src/SqlFlow.HealthCheck/SeriesRow.cs
  - src/SqlFlow.Core/HealthChecks/HealthCheckFlow.cs
---

# Health-check detection engine internals

[flowType: hc](../flow/hc.md) and [`sqlflow healthcheck`](../cli/healthcheck.md) document the YAML surface and the CLI flags: which metrics run, which `ml.*` knobs exist, what the report contains. This page documents the engine underneath both entry points: the two SQL statements that read the target, the pure (no I/O, no `MLContext`) pipeline that completes and scores a series, and the four numeric building blocks, Theil-Sen, generalized ESD, PELT, and the robust statistics and Student-t math they all lean on, that turn a raw per-date series into a scored, tagged report.

Everything below lives in `SqlFlow.HealthCheck` (namespace `SqlFlow.HealthCheck`) except `HealthCheckFlow` itself, which is a `SqlFlow.Core.HealthChecks` model record. `HealthCheckEngine` is ported faithfully from the legacy `ExecMLHealthCheck` procedure (frequency detection, missing-date completion, calendar featurization, weekday and seasonal imputation, adaptive anomaly tagging, and fit metrics), so scored output stays comparable run for run with the legacy engine's. A handful of pieces are deliberate departures from legacy behavior, called out in their own sections below: the AutoML feature set, the robust (median and MAD) statistics in place of mean and standard deviation, and the generalized ESD test in place of a fixed z-score threshold.

## Where this fits: the run pipeline in code order

`HealthCheckFlowRunner.RunAsync` (src/SqlFlow.HealthCheck/HealthCheckFlowRunner.cs:69) is the entry point for a `flowType: hc` document; the ad-hoc `sqlflow healthcheck` command builds and drives the same runner and engine over a synthesized flow, so everything on this page applies to both. One run proceeds as:

1. Build `HealthCheckSqlBuilder.SeriesSelect` and `HealthCheckSqlBuilder.DataQualitySelect` for the flow and the run's as-of date; both are captured into the run's SQL trace as steps `series.select` and `quality.select` and echoed to the run log at `Trace` level before they execute.
2. Run the series query. Fewer than two distinct observed dates fails the whole run before anything else runs: `The series query returned <n> date(s); a health check needs at least two to detect the cadence. Check the date column, the filter, and whether the table has been loaded.`
3. Run the data-quality query; a nonzero future-dated, sentinel-dated, or NULL-dated count is logged as `quality.probe`.
4. `HealthCheckEngine.DetectFrequency` on the observed dates, once, for the whole flow (the cadence is shared by every metric because they share one date axis).
5. `HealthCheckEngine.DetectMissingDates` against that frequency and the run's as-of date, again once for the flow.
6. Per metric, independently (a broken metric fails only its own section; see below): build one `SeriesRow` per observed date, then `HealthCheckEngine.AddMissingDates`, `HealthCheckEngine.MarkImmaturePoints`, `HealthCheckEngine.ApplyDateFeatures`, `HealthCheckEngine.Impute`, in that order.
7. Resolve the expectation function: reuse a stored model, train a fresh AutoML model behind a Theil-Sen detrend, or fall back to the weekday-median baseline (see the next section for the exact decision order).
8. Score every row (`PredictedValue`) with that expectation function.
9. `HealthCheckEngine.TagAnomalies`, which internally calls `EsdDetector.Detect` and `RobustStatistics`.
10. `PeltDetector.Detect` over the residuals of the mature, observed rows, reported as level shifts separate from point anomalies.
11. `HealthCheckEngine.ComputeMetrics` for the fit summary (MAE, RMSE, MAPE, R-squared), withheld below a minimum point count.

A metric's failure is caught, logged as `metric.failed`, and turned into a `HealthCheckMetricResult` carrying only an `Error`; the loop continues to the remaining metrics, and the run fails overall (`Success: false`, listing every failed metric) without losing the metrics that scored cleanly. The outer `RunAsync` has the same shape at the run level: any exception (other than cancellation) is caught, a best-effort run-log write is attempted, and a failed `HealthCheckRunOutcome` is returned. The catch keeps the original error and never rethrows; both an early failure (bad date column, too few dates) and a late one (a metric's `Impute` throwing on an all-zero series) come back as a normal failed result, not an unhandled exception.

## Picking the date column for ad-hoc runs

`DateColumnSelector.Choose` (src/SqlFlow.HealthCheck/DateColumnSelector.cs:30) is what lets `sqlflow healthcheck --object ...` run with no `dateColumn` at all; a pipeline file always states `dateColumn` explicitly and never calls this. Only `date`, `datetime`, `datetime2`, `smalldatetime`, and `datetimeoffset` columns qualify (case-insensitively, parameterized length like `datetime2(7)` stripped before the type-name comparison); everything else is invisible to the chooser. Each candidate gets a tier, lower wins:

| Tier | Rule |
| --- | --- |
| 1 | Name is exactly `date` or ends with `date` (business dates: `OrderDate`) |
| 2 | Name contains `date` elsewhere (`DateOfBirth`) |
| 3 | Name contains `created`, `modified`, `updated`, `inserted`, or `timestamp`, or ends with `at` or `time` (event stamps: `CreatedAt`, `ModifiedTimestamp`) |
| 4 | Any other date-typed column (`ValidFrom`, an unnamed `Snapshot`) |
| 5 | Name ends with `_dw` (warehouse system columns) |

The `_dw` check runs first in `Tier`, before any of the `date`-name checks, so a name like `LoadDate_DW` lands in tier 5 even though it also ends in `date`. Ties within a tier resolve deterministically: shortest name first, then case-insensitive ordinal name order (`OrderDate` beats `ShipmentDate` on length; two equal-length names fall to alphabetical order). The chosen column, every candidate, and the tie-break reasoning are always returned together so an operator can see why the heuristic picked what it picked and override it with `--date-column`. Full CLI behavior (the printed reasoning line, the no-qualifying-column error) is in [the ad-hoc command reference](../cli/healthcheck.md).

## HealthCheckSqlBuilder: one scan, two statements

`HealthCheckSqlBuilder` (src/SqlFlow.HealthCheck/HealthCheckSqlBuilder.cs) renders exactly two T-SQL statements per run, both pure string building with no parameters (dates are formatted `yyyy-MM-dd` and inlined; identifiers are bracket-quoted with `]` doubled).

**`SeriesSelect(flow, asOfDate)`** computes every metric's per-date aggregate in one scan (the legacy `flw.GetRVHealthCheck` shape, widened to N value columns), `GROUP BY` the date column, bounded to `[flow.SentinelDateFloor .. asOfDate]`:

```sql
SELECT [OrderDate] AS [Date], CAST(COUNT(*) AS float) AS [orders], CAST(SUM(Amount) AS float) AS [revenue], CAST(COUNT(DISTINCT CustomerID) AS float) AS [buyers]
FROM [DW].[dbo].[Orders]
WHERE 1 = 1
  AND [OrderDate] >= '1990-01-01'
  AND [OrderDate] <= '2026-06-12'
  AND (OrderStatus <> 'Cancelled')
GROUP BY [OrderDate]
ORDER BY [OrderDate];
```

Each metric's expression is trimmed and `CAST(... AS float)`, so an integer `COUNT(*)` and a `decimal` `SUM(Amount)` come back through the same reader column type. The `(OrderStatus <> 'Cancelled')` line only appears when `flow.FilterCriteria` is non-blank; a blank or whitespace-only filter is dropped entirely rather than emitting a dangling `AND ()`. There is always exactly one `FROM`, regardless of metric count: this is the "one scan for every metric" guarantee, not one query per metric.

The window bound is the point of the sentinel floor. Without it, a single 1900-01-01 placeholder row or a future-dated row would become the first or last observed date, and `DetectMissingDates` would then walk the cadence across decades of invented gaps, drowning the real series in imputed points. Rows outside `[SentinelDateFloor .. asOfDate]` are not silently dropped: they are exactly what the second query counts.

**`DataQualitySelect(flow, asOfDate)`** runs three `COUNT_BIG` probes under the same shared filter:

```sql
SELECT
  COUNT_BIG(CASE WHEN [OrderDate] > '2026-06-12' THEN 1 END) AS [FutureDatedRows],
  COUNT_BIG(CASE WHEN [OrderDate] < '1990-01-01' THEN 1 END) AS [SentinelDatedRows],
  COUNT_BIG(CASE WHEN [OrderDate] IS NULL THEN 1 END) AS [NullDatedRows]
FROM [DW].[dbo].[Orders]
WHERE 1 = 1
  AND (OrderStatus <> 'Cancelled');
```

`FutureDatedRows` counts strictly after `asOfDate`; `SentinelDatedRows` counts strictly before `flow.SentinelDateFloor`; `NullDatedRows` counts `IS NULL` (deliberately not `IS NOT NULL`: NULL dates are exactly what this probe exists to surface). `HealthCheckFlow.SentinelDateFloor` defaults to `1990-01-01`; the "1900-01-01" figure that motivates the floor's existence (in the source's own reasoning) is a common warehouse placeholder value, not the default floor itself. Both statements bracket-escape identifiers, so a date column literally named `Order]Date` renders as `[Order]]Date]` and still parses.

## HealthCheckEngine: the pure completion and scoring pipeline

`HealthCheckEngine` (src/SqlFlow.HealthCheck/HealthCheckEngine.cs) is a static class of pipeline stages over `List<SeriesRow>` (src/SqlFlow.HealthCheck/SeriesRow.cs). Every stage mutates or reads the same mutable row list the legacy engine's `DailyData` passes did; nothing here touches SQL, the network, or ML.NET.

### DetectFrequency: cadence from the most common gap

`DetectFrequency(dates)` orders the dates, takes the day-gap between every consecutive pair, and picks the most common gap value (ties resolve toward the smaller gap: with two 1-day gaps and two 7-day gaps in the same series, `Daily` wins over `Weekly`). The winning gap buckets into a `DataFrequency`:

| Most common gap (days) | Frequency |
| --- | --- |
| < 1.5 | `Daily` |
| 1.5 to < 2.5 | `BiDaily` |
| 2.5 to < 8 | `Weekly` |
| 8 to < 15 | `BiWeekly` |
| 15 to < 32 | `Monthly` |
| 32 to < 100 | `Quarterly` |
| 100 to < 200 | `HalfYearly` |
| 200 to < 400 | `Yearly` |
| everything else | `Irregular` |

Requires at least two dates (`ArgumentException` otherwise); this is the check `HealthCheckFlowRunner` performs before calling it, worded for the operator rather than the caller.

### DetectMissingDates: internal gaps plus the trailing dead-pipeline walk

`DetectMissingDates(dates, frequency, expectThrough)` walks from the first observed date to `max(last observed date, expectThrough)` in cadence-sized steps (`NextDate`), collecting every stepped-to date absent from the observed set. Two distinct kinds of gap fall out of one walk:

- **Internal gaps**: a date between the first and last observed date that the cadence expects but the series lacks.
- **Trailing gaps**: dates after the last observed date, up through `expectThrough` (the run's as-of date). This is what catches the pipeline that simply stopped: a table whose last load was four days ago has no gap inside its series, only a gap after it, and only the trailing walk surfaces that.

An `expectThrough` earlier than the last observed date cannot shrink the walk (only internal gaps remain; no trailing dates are invented). `Irregular` cadence steps one day at a time via `NextDate`'s default arm; `Monthly`/`Quarterly`/`HalfYearly`/`Yearly` step by calendar months or years (`AddMonths`/`AddYears`, so month-length and leap years are honored, not a fixed day count).

### AddMissingDates and ApplyDateFeatures

`AddMissingDates(series, missingDates)` appends one `SeriesRow` per missing date with `BaseValue = 0` and `IsNoData = 1`, then re-sorts the series by date.

`ApplyDateFeatures(series, holidays)` fills the seven calendar features the AutoML model trains on, computed in-engine (the without-database replacement for the legacy `flw.SysDateFeatures` lookup table): `Year`, `Quarter` (`ceiling(month / 3.0)`), `WeekOfYear` (`System.Globalization.ISOWeek.GetWeekOfYear`), `MonthNumber`, `DayOfWeekNumber` (`(float)DayOfWeek + 1`, so Sunday is 1 and Saturday is 7, the legacy `DATEPART(weekday)` convention), `IsWeekend` (Saturday or Sunday), and `IsHoliday` (membership in the flow's `holidays` list, matched by calendar date).

### Impute: weekday average, seasonal ratio, moving average, overall average

`Impute(series, movingAverageWindow = 7)` sets `BaseValueAdjusted` (the training label input) and `IsNoData` for every row, in a fallback chain for any row whose `BaseValue` is exactly `0`:

1. The weekday average of every row with `BaseValue > 0` (a genuine arithmetic mean, not a median: this is a different fallback from the short-history baseline model described later, which does use medians).
2. Once at least 60 rows have `BaseValue > 0`, that weekday average is further scaled by a monthly seasonal ratio: `monthlyAverage / averageOfAllMonthlyAverages` for the row's month, guarding against a zero denominator.
3. If the row's weekday was never observed with a positive value (no entry in the weekday-average table), the centered moving average at that row's position, from `MovingAverages(series, window)`: the mean of the nonzero values in a `window`-wide span centered on the row (half-width `window / 2`), or the mean of every value in the span (including zeros) when the whole span is zero. `MovingAverages` returns exactly one entry per row of the series it is given, so this tier always has a value ready.

`Impute` also carries a defensive last-resort branch, the overall average of every `BaseValue > 0` row, guarding an index that would run past the end of the moving-average list. Because that list is always as long as the series (tier 3's guarantee above), the guard never trips in the current code: every zero row resolves through tier 1, 2, or 3.

A row whose `BaseValue` is nonzero, including a negative value, is left as observed (`BaseValueAdjusted = BaseValue`, `IsNoData = 0`): the "is this missing" test is `BaseValue == 0`, not sign, so a SUM-of-adjustments metric that legitimately sits below zero is never treated as missing data by this stage. A series with no `BaseValue > 0` row anywhere fails clearly: `The series has no nonzero values to learn from; a health check needs at least one observed value.` `MovingAverageWindow` is a public constant equal to `7` (one week, centered).

### MarkImmaturePoints: the trailing scored-but-never-anomalous window

`MarkImmaturePoints(series, asOfDate, maturityDays)` flags every row with `Date >= asOfDate - (maturityDays - 1)` as `IsImmature`. With the default `maturityDays: 1` this is exactly the as-of date itself; `maturityDays: 0` flags nothing (the window is disabled outright, not shrunk to zero width). Immature rows are still scored and still appear in the report, they are simply excluded from every anomaly count, fit metric, and the AutoML-versus-baseline training gate below: today's partial load does not page anyone, and does not silently poison tomorrow's fit statistics either.

### TagAnomalies: missing-data rule plus generalized ESD plus business floors

`TagAnomalies(series, thresholdStdDev, alpha, maxAnomalyFraction)` is where the residual-based detection happens. Its inputs partition the series into `mature` (every non-immature row) and, within that, `observed` (mature rows with `IsNoData == 0`):

1. **Severity for every row.** When at least two mature observed residuals (`BaseValue - PredictedValue`) exist, their median and `RobustStatistics.Scale` become the center and scale for a `RobustStatistics.Z` computed against *every* row's residual, immature rows included (`Severity`, rounded to 4 places, capped display-wise at `999` for an infinite Z). This means a row can carry a nonzero `Severity` in the report even when it was never eligible to be tagged an anomaly.
2. **Missing data, unconditionally.** Every mature row with `IsNoData == 1` or `BaseValue == 0` is tagged `AnomalyDetected = true`, `AnomalyReason = "Missing Data"`, before any point-count gate is checked. A mature date with no data is treated as an incident, not a statistic, regardless of how few points exist.
3. **The statistical gate.** If `observed.Count` (mature and actually observed, not imputed) is below `MinimumPointsForTagging` (`3`), the method returns `false` and stops here: missing-data tagging from step 2 still stands, but no ESD pass runs. `HealthCheckFlowRunner` logs this exact tradeoff as `anomaly.tag`: "statistical tagging skipped: fewer than 3 mature observed point(s); missing-data detection still applies".
4. **Generalized ESD.** `EsdDetector.Detect` runs over the mature observed residuals with the flow's `alpha` and `maxAnomalyFraction`. Each flagged candidate must then clear three independent conditions before it is actually tagged:
   - `Severity >= thresholdStdDev` (the `ml.anomalyThreshold` operational floor on top of ESD's own significance test).
   - Not suppressed by the absolute floor: `|predicted - actual| > AbsoluteFloor` (a public constant, `1.0`).
   - Not suppressed by the relative floor, unless severity overrides it: a relative deviation (`|predicted - actual| / actual * 100`, undefined and treated as infinite when `actual <= 0`) at or under `RelativeFloorPercent` (a public constant, `10.0`) is still suppressed *unless* `Severity >= 3 * thresholdStdDev`, the override for a deviation that is technically under 10% but still enormous in sigma terms.

   `AbsoluteFloor` and `RelativeFloorPercent` are fixed engine constants (the legacy floors, carried forward for tiny-variance series); neither is exposed as a YAML key. A tagged point's reason is `"Relative Difference"` when `relativePercent / RelativeFloorPercent` exceeds `absolute / AbsoluteFloor`, else `"Absolute Difference"`: whichever view clears its own floor by the larger multiple names the reason (a 3-row miss on a 6-row Sunday reads relative; a 400-row miss on a 10,000-row Monday reads absolute). A negative-valued metric's flagged point always reads `"Absolute Difference"`, because the relative view is undefined below zero.

   Tagging also overwrites the point's `Severity` with the ESD test's own value at the outward test where it was removed (the median and scale of whichever residuals were still remaining at that step, not the single fixed median and scale step 1 used for every row). An untagged row keeps its step-1 `Severity`.

### ComputeMetrics: MAE, RMSE, MAPE, R-squared

`ComputeMetrics(series)` restricts to `actual` rows (`IsNoData == 0 && !IsImmature`, the same mature-and-observed definition as `TagAnomalies`'s `observed`), and returns `null` below `MinimumPointsForMetrics` (`10`) of them, so a short or mostly-imputed series never reports fit numbers built on noise:

- **MAE**: mean of `|actual - predicted|`.
- **RMSE**: root of the mean of `(actual - predicted)^2`.
- **MAPE**: mean of `|(actual - predicted) / actual|` over the nonzero-actual subset only, times 100; `0` when no actual is nonzero.
- **R-squared**: `1 - ssRes / ssTot` where `ssTot` is the total sum of squares around the mean actual and `ssRes` the residual sum of squares; when `ssTot` is `0` (a perfectly flat actual series), R-squared is `1` if the fit is also flat and exact (`ssRes == 0`) or `0` otherwise, avoiding a division by zero on a constant series.

`MinimumPointsForMetrics` (`10`) is numerically the same value as `HealthCheckFlowRunner.MinimumPointsForTraining`, but the two gate different things and count differently: `MinimumPointsForTraining` counts every mature row (real and imputed) before scoring even starts, to decide whether AutoML gets a fair shot; `MinimumPointsForMetrics` counts mature, actually-observed rows after scoring, to decide whether the fit numbers are trustworthy. A series can clear one and not the other.

## Choosing the expectation function: stored model, AutoML, or the weekday baseline

`HealthCheckFlowRunner.ResolveModel` (src/SqlFlow.HealthCheck/HealthCheckFlowRunner.cs:376) decides how each metric's `PredictedValue` gets computed, in this order:

1. Load any stored model and metadata for `(flow.SysAlias, metric.Name)` through `IHealthCheckModelStore.Load`: the file-backed store keeps the serialized ML.NET transformer as `healthcheck.model` next to a `healthcheck.model.json` metadata sidecar (schema version `HealthCheckModelMetadata.CurrentSchemaVersion`, `2`; engine version `HealthCheckModelMetadata.CurrentEngineVersion`, `2`), written together through a temp-file-then-atomic-replace so a concurrent reader never observes a half-written pair. A model file with no matching, readable sidecar fails loudly (naming the folder to delete) rather than silently retraining over it.
2. `HealthCheckTrainingDecision.Resolve` (src/SqlFlow.HealthCheck/HealthCheckModelStore.cs) turns the flow's `ml.training` policy, `ml.retrainAfterDays`, and the stored metadata's engine version and training timestamp into either `null` (reuse what is stored) or a human-readable training reason. The policy table itself (`auto`/`always`/`never`, retrain-after-days, engine-version staleness) is documented in [flowType: hc](../flow/hc.md); this page is only concerned with what happens on each branch.
3. **Reuse** (`trainReason` is `null`): the stored ML.NET transformer is deserialized into a `PredictionEngine`, and the stored `TrendAnchorDate`, `TrendSlopePerDay`, `TrendIntercept`, and `TrendWindowPoints` are rebuilt into a `TrendFit` exactly as they were at the last training. Neither `TrendEstimator` nor AutoML runs again on this path; every row scores as `trend.ValueAt(date) + engine.Predict(row).PredictedValue` against that frozen trend.
4. **Train** (`trainReason` is non-null, logged as `model.decision`): `mature.Count`, the number of non-immature rows in the gap-completed series (real observations and `AddMissingDates`-imputed rows together, immature rows excluded), is compared against `HealthCheckFlowRunner.MinimumPointsForTraining` (`10`):
   - **Below 10**: `BaselineModel.Fit` on the mature rows becomes the expectation function (falling back to the full series if there happen to be zero mature rows). Scoring is `baseline.Predict(date)`, with no trend term at all. This result is never persisted: `IHealthCheckModelStore.Save` is only ever called from the AutoML branch, so a baseline-scored metric recomputes its weekday medians fresh on every run.
   - **At or above 10**: `TrendEstimator.Fit` runs over `(Date, BaseValueAdjusted)` for the mature rows; every mature row's `DetrendedLabel` becomes `BaseValueAdjusted - trend.ValueAt(Date)`. An ML.NET `RegressionExperimentSettings` experiment (`OptimizingMetric: RSquared`, budget `ml.maxExperimentSeconds`, `MLContext` seed `42`) trains against `DetrendedLabel` on an 80/20 `TrainTestSplit` (seed `42`) of the mature rows, using only the seven calendar feature columns (`Year`, `Quarter`, `WeekOfYear`, `MonthNumber`, `DayOfWeekNumber`, `IsWeekend`, `IsHoliday`); `Date`, `BaseValue`, `BaseValueAdjusted`, `PredictedValue`, `IsNoData`, `AnomalyDetected`, `AnomalyReason`, `Severity`, and `IsImmature` are all explicitly excluded from featurization. The winning trial's model, the fitted trend, and the full trial roster are saved together through `IHealthCheckModelStore.Save`; every future run reuses that pairing until a retrain is triggered. Scoring is the same formula as the reuse path: `trend.ValueAt(date) + engine.Predict(row).PredictedValue`.

The holdout evaluation (`EvaluateHoldout`) only runs on the fresh-AutoML branch: it logs the holdout split's R-squared and MAE at `Debug` level, or logs and skips cleanly when the 80/20 split rounds down to zero test rows (possible right at the 10-point minimum).

This is the one deliberate, explicitly documented departure from the legacy engine: the legacy engine declared these same seven calendar features but never actually wired them into its AutoML column set, so AutoML auto-featurized every column of the row instead, including the observed value itself, and the model partly memorized its own target. V3's `ColumnInformation.IgnoredColumnNames` list exists specifically to prevent that.

## TrendEstimator: Theil-Sen robust trend

`TrendEstimator.Fit(points, windowPoints = 180)` (src/SqlFlow.HealthCheck/TrendEstimator.cs) fits `expected = Intercept + SlopePerDay * (date - AnchorDate).TotalDays` over the trailing `windowPoints` of the input (`DefaultWindowPoints` is `180`, about two calendar quarters, enough for a stable slope without dragging in last year's regime). The estimator is the classic Theil-Sen construction: the slope is the median of every pairwise slope `(y_j - y_i) / (x_j - x_i)` across all point pairs with distinct x in the window, and the intercept is the median residual `y_i - slope * x_i`. With fewer than two points, or fewer than two distinct dates, in the window, the fit is flat: `SlopePerDay = 0`, intercept the window's plain median.

Because the slope and intercept are both medians rather than least-squares estimates, the fit tolerates roughly 29% of the window being contaminated by outliers without being pulled off course. This is also, per the source's own framing, the piece that keeps a steadily growing table honest: a least-squares fit or a flat baseline over a table gaining rows every day either chases yesterday's growth as if it were an anomaly, or flags today's healthy growth as anomalous relative to a stale flat expectation; Theil-Sen's median slope tracks the growth itself as the new normal.

Computing every pairwise slope is `O(n^2)`; above `MaxSlopePairs` (`50,000`) pairs, the estimator switches to deterministic stride sampling over the pair index (`stride = ceil(totalPairs / MaxSlopePairs)`, keep every `stride`-th pair) rather than random sampling, so the same input always produces the same fit. The anchor date and every date comparison are truncated to the date component, so two timestamps on the same calendar day score identically.

## EsdDetector: generalized ESD (Rosner 1983) with S-H-ESD studentization

`EsdDetector.Detect(values, alpha, maxAnomalyFraction)` (src/SqlFlow.HealthCheck/EsdDetector.cs) implements the generalized extreme Studentized deviate test. Classic fixed z-score thresholds fail real warehouse series two ways: **masking**, where one huge spike inflates the measured spread and hides every smaller one under it, and **multiplicity**, where testing every point at one fixed threshold mass-produces false positives as the series grows. ESD addresses both by testing iteratively and by comparing each test against a critical value that accounts for how many tests have already run; studentizing with median and MAD instead of mean and standard deviation (the S-H-ESD refinement) keeps the spread estimate itself from being contaminated by the very points under test.

The algorithm, over `n = values.Count`:

1. `maxAnomalies = min(ceil(n * maxAnomalyFraction), n - 3)`. Below `n = 4`, or when `maxAnomalies < 1`, the result is empty: there is no degrees-of-freedom room left for a Student-t verdict.
2. For each of `maxAnomalies` outward tests: recompute the median and `RobustStatistics.Scale` of whatever residuals remain, find the single most extreme remaining residual by absolute distance from that median, record its `RobustStatistics.Z` as a candidate severity, and remove it from the remaining pool before the next test.
3. Walk the candidates in test order (`i = 1..maxAnomalies`); at each step compute Rosner's critical value `lambda_i` from `t = StudentT.InverseCdf(1 - alpha / (2 * nRemaining), df)` with `nRemaining = n - i + 1` and `df = nRemaining - 2`, then `lambda_i = (nRemaining - 1) * t / sqrt((df + t^2) * nRemaining)`.
4. The **last** test index `i` whose candidate severity exceeds its own `lambda_i` fixes the final anomaly count; every candidate up to and including that index is returned. This last-exceeding rule, not the first, is what defeats masking: an early test that happens not to clear its critical value does not stop the search from finding a later, still-significant one.

`alpha` must be strictly between 0 and 1 (the per-test significance level; lower means stricter); `maxAnomalyFraction` must be in `(0, 0.49]` (a robust test refuses, by construction, to call half or more of a series anomalous). Both are `ArgumentOutOfRangeException` otherwise. The whole test is deterministic (no RNG) and runs in `O(k * n)` for `k` outward tests.

## PeltDetector: PELT level shifts

`PeltDetector.Detect(values, penaltyFactor = 3.0)` (src/SqlFlow.HealthCheck/PeltDetector.cs) implements PELT, Pruned Exact Linear Time (Killick et al. 2012), exact change-point detection over a robust L1 segment cost. It exists to separate a genuine regime change, a backfill, a retention purge, a new source doubling volume, from a wall of individually-flagged point anomalies: a level shift is one finding about a permanent change in level, not thirty consecutive daily anomalies that both bury the real signal and exhaust the ESD anomaly-fraction budget.

Mechanics:

- **Segment cost** is L1: the sum of `|value - segment median|` over the segment, not squared-error. A single extreme spike barely moves a segment's median, so one bad day cannot make a one-point segment look cheap enough to justify a split; genuine shifts, which move the median of an entire segment, still pay off.
- **`MinSegmentLength`** is a public constant, `7`, one full weekly cycle on each side of any detected change, so the ordinary weekday/weekend rhythm is never itself mistaken for a regime change. Series shorter than `2 * MinSegmentLength` (14 points) return no shifts.
- **The noise scale (`sigma`)** must be immune to the very shifts being searched for: the plain robust scale of the whole series is inflated by any real regime change, which would both dull the penalty and understate every magnitude. `RobustNoiseScale` instead takes the MAD-sigma of each point's deviation from a one-cycle (`MinSegmentLength`-wide) rolling median, so a shift only pollutes the handful of boundary points near it, not the whole estimate. If the series is exactly constant (`globalScale == 0`), detection stops immediately: there is nothing to find. If the series has structure but zero marginal noise (a perfectly clean, noise-free step), `sigma` would otherwise be zero; the detector floors it at `globalScale * 1e-3` so the penalty and every reported magnitude stay finite instead of exploding.
- **Penalty**: `penaltyFactor * sigma * ln(n)`, a BIC-style cost every additional change point must clear to be worth adding; `penaltyFactor` defaults to `3.0` (conservative, few false regimes) and must be strictly positive. A lower factor makes the detector strictly more willing to split, never less.
- **Search**: exact dynamic programming over `F[t]`, the minimal cost of segmenting `values[0..t)`, with PELT's pruning rule dropping any candidate start point whose path cost already exceeds the current optimum (the L1 cost is subadditive, so a losing start can never win later). The optimal path is walked back from `F[n]` to recover the change points.

Each returned `LevelShift` carries `Index` (the first point of the new regime), `MedianBefore` and `MedianAfter` (the robust medians on each side), and `MagnitudeSigma`, `|MedianAfter - MedianBefore| / sigma`, how many marginal-noise sigmas the jump represents. `HealthCheckFlowRunner` runs this over the residuals (`BaseValue - PredictedValue`) of the mature, observed rows only, after scoring and after `TagAnomalies`, and reports shifts in their own section of the metric report, separate from the anomaly list.

## RobustStatistics: median and MAD

`RobustStatistics` (src/SqlFlow.HealthCheck/RobustStatistics.cs) is the location-and-scale foundation every detector above shares. The legacy engine measured anomalies against the mean and standard deviation of the very sample the anomalies sat inside, so one large spike inflated the threshold and masked every smaller deviation around it; median and MAD (median absolute deviation) ignore that contamination instead of being distorted by it.

- **`Median(values)`**: the sorted middle value, or the average of the two middle values for an even count. Throws on an empty input.
- **`Scale(values)`**: `MAD * MadToSigma`, where `MadToSigma` is the public constant `1.4826`, the consistency correction that makes MAD comparable to a normal distribution's standard deviation. When the majority of values sit exactly on the median (MAD collapses to `0`, which a near-constant series with one wiggle produces), `Scale` falls back to the plain mean absolute deviation times `1.2533` (the analogous normal-consistency constant for mean absolute deviation), so the minority of differing values are still seen. A genuinely constant sample returns `0`; that zero is a deliberate signal, not a bug, and callers (`EsdDetector`, `TagAnomalies`'s business floors) are built to treat it as "nothing to measure here" rather than dividing by it.
- **`Z(value, median, scale)`**: `|value - median| / scale` when `scale > 0`; when `scale <= 0`, `0` if `value` equals `median` exactly and positive infinity for any deviation at all off a truly constant sample.

## StudentT: the dependency-free numeric core

`StudentT` (src/SqlFlow.HealthCheck/StudentT.cs) supplies the Student-t CDF and inverse CDF that `EsdDetector`'s critical values need, self-contained with no external statistics package: a regularized incomplete beta function (continued-fraction form) built on a Lanczos log-gamma approximation, and an inverse CDF via Newton iteration on that CDF, seeded from Acklam's rational approximation of the inverse normal CDF (`InverseNormalCdf`) and widened for low degrees of freedom before iterating (heavier tails than normal need a larger first step). Bisection bounds guard the Newton iteration so it cannot diverge in the tails. Per the source's own description this is reproducible to `1e-8`, and the test suite pins several `InverseCdf` results against published Student-t critical-value tables to three decimal places (for example `InverseCdf(0.975, 10) = 2.2281`) and round-trips `Cdf(InverseCdf(p, df), df) == p` to nine decimal places. This is why the detector carries no numeric-library dependency: every distribution value ESD needs is computed from first principles in this one file.

## BaselineModel: the weekday-median fallback

`BaselineModel` (src/SqlFlow.HealthCheck/BaselineModel.cs) is the expectation function for a metric with fewer than `HealthCheckFlowRunner.MinimumPointsForTraining` mature points; see the [decision order above](#choosing-the-expectation-function-stored-model-automl-or-the-weekday-baseline) for exactly when this fires, and [flowType: hc](../flow/hc.md) for the YAML-facing framing of that threshold. `BaselineModel.Fit(series)` groups every observed (`IsNoData == 0`) row by `DayOfWeek` and takes the `RobustStatistics.Median` of `BaseValue` per weekday, plus the overall median across every observed row as the fallback for a weekday that was never observed at all. `Predict(date)` returns the matching weekday median, or the overall median. Medians rather than means mean one bad day in a five-day history does not drag the expectation for that weekday off course; a table created last week still gets usable missing-data and gross-deviation detection from its second day onward, and graduates to the trained model as history accumulates. Fitting requires at least one observed point (`InvalidOperationException` otherwise); imputed rows never influence the medians.

## Configuration touchpoints

| Surface | Feeds into |
| --- | --- |
| `dateColumn` (YAML) / `--date-column` (CLI, or `DateColumnSelector` when omitted) | The `GROUP BY` column in both `HealthCheckSqlBuilder` statements and the date axis every stage below operates on |
| `sentinelDateFloor` (YAML) / fixed default in ad-hoc runs | The lower bound of `SeriesSelect`'s window and the `SentinelDatedRows` probe in `DataQualitySelect` |
| `maturityDays` (YAML) / `--maturity` (CLI) | `MarkImmaturePoints`'s trailing window; excludes rows from anomaly tagging, level-shift residuals, and `ComputeMetrics` |
| `ml.anomalyThreshold` (YAML) / `--threshold` (CLI) | `TagAnomalies`'s `thresholdStdDev` severity floor and its `3x` override on the relative business floor |
| `ml.esdAlpha` (YAML) / `--alpha` (CLI) | `EsdDetector.Detect`'s `alpha`, the per-test significance level behind Rosner's critical values |
| `ml.maxAnomalyFraction` (YAML only; no ad-hoc CLI flag) | `EsdDetector.Detect`'s `maxAnomalyFraction`, the upper bound on `maxAnomalies` |
| `ml.maxExperimentSeconds` (YAML) / `--budget` (CLI) | The AutoML `RegressionExperimentSettings.MaxExperimentTimeInSeconds` budget in the training branch of `ResolveModel` |
| `ml.training`, `ml.retrainAfterDays` (YAML) / `--retrain` (CLI) | `HealthCheckTrainingDecision.Resolve`, which decides reuse versus the AutoML-or-baseline training branch |
| `holidays` (YAML) | `ApplyDateFeatures`'s `IsHoliday` feature, consumed by the AutoML model when one is trained |
| `--state-dir` (CLI) / the flow document's location (YAML) | Which `IHealthCheckModelStore` implementation backs persistence: file-based under `.sqlflow/state/`, or the ephemeral in-memory store |

`AbsoluteFloor` (`1.0`) and `RelativeFloorPercent` (`10.0`), the business-noise floors inside `TagAnomalies`, and every constant named in this page (`MinimumPointsForTraining`, `MinimumPointsForTagging`, `MinimumPointsForMetrics`, `MovingAverageWindow`, `TrendEstimator.DefaultWindowPoints`, `TrendEstimator.MaxSlopePairs`, `PeltDetector.MinSegmentLength`, the PELT `penaltyFactor` default, `RobustStatistics.MadToSigma`) are fixed in code, not exposed through YAML or CLI flags.

## Example

Adapted from samples/healthcheck/orders-healthcheck.flow.yaml. This flow drives every stage on this page: three metrics scored in one scan, a sentinel floor tightened from the `1990-01-01` default, a 30-day retrain policy, and two fixed holidays feeding `ApplyDateFeatures`.

```yaml
flowType: hc
name: orders-watch
description: Watches order volume and revenue for missing or abnormal loads.

connections:
  dwh: ${env:SQLFLOW_DW}

target:
  server: dwh
  object: DW.dbo.Orders

dateColumn: OrderDate
metrics:
  - name: orders
    baseValue: COUNT(*)
  - name: revenue
    baseValue: SUM(Amount)
  - name: buyers
    baseValue: COUNT(DISTINCT CustomerID)
filter: OrderStatus <> 'Cancelled'

maturityDays: 1
sentinelDateFloor: 2000-01-01

ml:
  maxExperimentSeconds: 120
  anomalyThreshold: 2.0
  esdAlpha: 0.025
  maxAnomalyFraction: 0.10
  training: auto
  retrainAfterDays: 30

holidays:
  - 2026-01-01
  - 2026-12-25
  - 2026-12-26
```

```bash
sqlflow validate orders-healthcheck.flow.yaml
sqlflow run orders-healthcheck.flow.yaml --show-sql
```

With `--show-sql`, the printed trace shows the exact `series.select` and `quality.select` statements this page's SQL section describes, generated for the `orders`, `revenue`, and `buyers` metrics in one scan, bounded to `[2000-01-01 .. today]`.

## See also

- [Health-check flow (flowType: hc)](../flow/hc.md): the YAML surface, keys reference, and run behavior this engine implements.
- [sqlflow healthcheck (ad-hoc)](../cli/healthcheck.md): the zero-configuration CLI entry point, date-column auto-detection output, and exit codes.
- [Run artifacts](../concepts/run-artifacts.md): `run.json`, `run.log`, `trace.sql`, and `healthcheck.json`.
