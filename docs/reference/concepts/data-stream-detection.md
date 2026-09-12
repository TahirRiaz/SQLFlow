---
id: concept-data-stream-detection
title: "Data stream detection: which tables stopped receiving data, and why a verdict says what it says"
type: concept
summary: "The estate-wide detector behind the Data streams board and the detect_stream_anomalies tool: what it reads, the six detectors and their size floors, how verdicts are ranked, and how to read one stream's evidence."
keywords:
  - data streams
  - stream anomaly
  - stalled table
  - missing data
  - less data than usual
  - level shift
  - silence detector
  - null days
  - reprocessing trim
  - delivery cycle
  - detect_stream_anomalies
  - false positive
related:
  - concept-healthcheck-engine
  - concept-control-plane
  - flow-hc
sourceRefs:
  - src/SqlFlow.HealthCheck/StreamAnomaly.cs
  - src/SqlFlow.HealthCheck/StreamAnomalyDetector.cs
  - src/SqlFlow.HealthCheck/CycleEstimator.cs
  - src/SqlFlow.HealthCheck/PeltDetector.cs
  - src/SqlFlow.HealthCheck/EsdDetector.cs
  - src/SqlFlow.HealthCheck/HealthCheckEngine.cs
  - src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/datastreams/DataStreamsPage.tsx
---

# Data stream detection

Every run the platform records carries how many rows it inserted, updated, and deleted. That is a heartbeat for every table the platform writes, collected with no configuration at all, and the data stream detector turns it into one question per table: is data still arriving the way this table's own history says it should? The [health-check engine](healthcheck-engine.md) answers a richer version of that question for the few tables that have a `hc` flow; this detector covers every scheduled flow in the estate with the same numeric building blocks.

It is exposed in three places, all computing the same analysis on each request (nothing is stored):

- The control plane's `GET /api/v1/datastreams` (the board, ranked most urgent first) and `GET /api/v1/datastreams/{pipelineId}` (one stream with its full day-by-day series), in `src/SqlFlow.ControlPlane/Api/DataStreamEndpoints.cs`.
- The GUI's **Data streams** page, which renders the board with a two-week sparkline per row and a detail sheet per stream.
- The MCP tool `detect_stream_anomalies`, which returns the board or, given `pipelineId` or `flowName`, the single-stream drill-down with every detector's reasoning.

## What is analysed, and what is not

**Population.** Only flows that join an ENABLED schedule are analysed by default (`includeUnscheduled` adds the rest). A flow nothing schedules has no say in whether data is delivered, so holding it to a delivery cadence would manufacture findings about a promise nobody made. The response reports how many streams were left out for this reason.

**Scope and stage.** Each stream is classified as `source` (it brings data in from outside the estate: a vendor delivery) or `internal` (it derives one of our tables from another), by walking lineage upstream and, failing that, by the target schema and flow kind. The control plane's `DataStreams` options carry the schemas and flow kinds that decide it: `LandingSchemas` (`raw`, `arc`, `stg`, `staging`, `landing`, `src`, `ext`, `pre`), `DownstreamSchemas` (`edw`, `dwh`, `dw`, `mart`, `rpt`, `skey`) and `SourceFlowKinds` (`api`, `sftp`, `cpy`, `file`, `ing`). The board shows one side at a time because one quiet upstream would otherwise light up its whole downstream chain as separate findings. The stage sharpens the question: `integration` fetches from the vendor (nothing there usually means the vendor sent nothing), `file-ingestion` and `archive` mean their data arrived and we did not take it in, `derived` is entirely our own processing.

**Reprocessing is removed before anything is measured, in two passes.** First, runs the log flags as reprocessing are dropped: a forced full load, a backfill window, an ad-hoc source filter or file pattern, a reprocess from the source minimum, an assertions-only run, or an engine-derived scope of backfill or init-load. They are still counted per day (`excludedBackfillRuns`) so the chart can show something happened. Second, the loading days that remain are trimmed to a Tukey fence, `Q3 + 3 * IQR` with a floor of three times the median load (`ReprocessTrimIqrMultiplier`, `ReprocessTrimMedianMultiplier`), because the flags only catch what an operator declared and a catch-up after an outage carries no flag. The reported series keeps the true numbers; only the fitting sees the trimmed ones. `includeBackfills=true` switches the first pass off.

**Per-request, from the catalog.** The board reads the run table for the window, groups by pipeline and UTC day, and hands each stream's daily buckets to `StreamAnomalyDetector.Analyze`. For a stream that loaded nothing inside the window, its last load before the window is read from the full history, so a table dead for six months is reported as stalled rather than as "never loaded".

## The analysis, in order

1. **Calendar and reliability.** Every calendar day of the window gets what the stream did on it. From the days through the last load (never from the drought itself), the stream learns which weekdays it loads on at all: a weekday whose loaded share clears `ExpectedLoadRateThreshold` (0.5, with `MinWeekdaySamples` = 3 observations before a weekday's own rate is trusted over the overall rate) is a day the stream is EXPECTED on. How often it delivers on those days is the median week's delivery rate, not the mean day's, so a bad fortnight moves two of nine weekly rates and leaves the middle one where it was. An expected day that wrote nothing is an "unexpected null day", split into ran-empty (the flow ran and wrote nothing: upstream) and no-run (scheduling or worker).
2. **Cadence.** The expected gap between loads is the schedule's cron when the stream has one (`cadenceSource: schedule`), otherwise the median gap between loading days (`observed`). A declared cadence is strictly better evidence: a stream silent for three weeks teaches an inferred detector that three-week gaps are normal.
3. **Scoring the volumes.** On the trimmed loading days: a Theil-Sen robust trend, then the weekday-median baseline of what the trend leaves, then (when `DetectDeliveryCycles` is on) a sparse recurring delivery cycle the weekday model cannot express (a bigger refill every fortnight, a month-end file), fitted jointly with the weekday model by backfitting both orders and keeping the decomposition with the fewer departing parts. `expected` for a day is trend plus weekday plus cycle. Generalized ESD then tags point anomalies over the residuals, exactly as in the health-check engine, and the trailing `MaturityDays` (1) are scored but never flagged.
4. **The size floor on points.** The shared tagging waives its 10% relative floor once a point is overwhelmingly significant. On a very steady stream everything is: with thirty rows of noise a day 800 rows (0.75%) short of its 107,000 scores 12.7 sigma. The stream detector therefore untags any flagged day whose deviation is under `VolumeOutlierMinPercent` (10) of the larger of actual and expected. The sigma severity stays on the point; only the verdict is withdrawn.
5. **Level shifts.** PELT over the residuals of the mature observed days, as in the health-check engine.
6. **Readiness.** The volume tests need a sample: `MinObservedDays` (7) loading days, below which they report themselves held back and only whether data is still ARRIVING is tested. The presence tests need only a rhythm that accounts for the sample: the loads made plus the loads the current drought swallowed must reach half of what the cadence implies for the window, so a monthly feed is judged on two loads while a daily feed that has only ever loaded twice is not. A stream failing both bars is reported `insufficient-history` ("Too new" on the board), charted and never flagged.

## The six detectors

Three are PRIMARY and may raise a finding alone; three measure volume and corroborate. Each returns whether it fired, a score in [0, 1] (0 at the firing boundary, 1 where the evidence is unambiguous), a direction, and a `detail` sentence carrying its numbers whether or not it fired, so a healthy verdict is auditable.

| Detector | Primary | Fires when | Floors and knobs |
|---|---|---|---|
| `silence` (No data now) | yes | Days since the last load exceed the expected gap times `SilenceTolerance` (2); with an inferred cadence it must also exceed the longest gap the stream has survived, plus one day. | A declared cron ignores past gaps: a ten-day gap in the past was an incident, not a licence. |
| `nullDays` (Missing days) | yes | Unexpected null days exceed what the learned reliability predicts by `NullDayZ` (3) standard deviations of that count. | Judged per weekday, so a feed that never loads at weekends is not reported every Saturday. |
| `cadence` (Not running) | yes | The flow itself has not RUN for longer than its cadence tolerates, whatever it would have loaded. | Kept apart from `silence`: a flow that stopped running is a scheduling or worker problem, a flow that runs and writes nothing is an upstream one. |
| `rateChange` (Volume rate) | no | The last `RecentWindowDays` (7) moved against the baseline rate by `RateCollapseZ` (4) quasi-Poisson sigma AND by `RateCollapseDropPercent` (50%) of expected volume, with the baseline expecting at least `RateCollapseMinExpected` (30) rows. | Both directions; a rise is information. |
| `levelShift` (Level shift) | no | PELT found a regime change in the recent third (at least 14 points) of the series of `LevelShiftSigma` (3) marginal-noise sigma AND of `LevelShiftMinPercent` (10%) of the level the stream was expected to hold over the new regime. | The size floor is what keeps a very steady feed from reporting a hundred-row move on a hundred thousand as "4 sigma". |
| `volumeOutlier` (Outlying day) | no | A day in the recent slice is ESD-flagged, statistically rather than as "Missing Data", after the `VolumeOutlierMinPercent` floor. | Empty days are the null-day model's business, never this one's. |

`FlagIncreases` (true) lets the volume tests fire upward; `PromoteVolumeFindings` (true) lets them raise a finding rather than only corroborate. Sigma is always the stream's OWN residual noise, which is why the two size floors exist: the steadier a stream, the more a trivial wobble looks like a finding, and significance without size is not a fault.

## The ensemble: categories, statuses, severities

The fired detectors are turned into one finding, ranked by what an operator must do about it.

| Category | Meaning | Status (board label) | Severity |
|---|---|---|---|
| `failing` | Silent, and every run since the last load failed: fix the flow, the upstream is not the suspect. | `stalled` (Stopped) | critical when two detectors agree or the drought is unambiguous, else warning |
| `stalled` | No data for longer than the cadence tolerates, whether or not the flow still runs. | `stalled` (Stopped) | as above |
| `gap-days` | More expected days empty than the history predicts, at least one of them a day the flow did not run. | `degraded` (Missing data) with two detectors, else `watch` | as above |
| `not-running` | The flow stopped executing. | as above | as above |
| `idle-days` | Every empty day had a run that succeeded and wrote nothing: the flow reporting there was nothing new, not data going missing. | `watch` (Worth a look) | info |
| `less-than-normal` | Data still arrives on schedule, in less volume. | `degraded` with two detectors, else `watch` | warning with two detectors, else info |
| `more-than-normal` | More volume than usual. | `watch` | info |
| `healthy` | Every detector quiet. | `healthy` (OK) | info |
| `insufficient-history` | Too few loads to judge. | `insufficient-history` (Too new) | info |

The confirmation rule: `ConfirmationThreshold` = 2 independent detectors must agree before a finding can be critical; a lone detector describes something worth a look, never something worth a page. Only a zero-data category can be critical at all; a shortfall caps at warning because a quieter upstream and a broken one look identical from here; a surplus never rises above information. `confidence` is the weight-averaged score of the detectors that fired (the presence tests weigh more than the volume tests), and `agreeingDetectors` is their count. The `summary` sentence is the category's own sentence plus "Also:" every other fired detector's detail.

## Reading one stream's evidence

The single-stream response (`pipelineId` or `flowName` on the tool, the detail sheet in the GUI) carries everything the verdict rests on:

- `signals`: all six detectors with `fired`, `score`, `direction`, `primary`, and the `detail` sentence. The quiet ones say what they measured, which is the case FOR the stream.
- `profile`: the learned pattern (`shape`, `loadDays`, `typicalRows` with its `lowRows` to `highRows` band, `reliability`, any `cycle` with its period, size and next due date, and a `description` sentence), the cadence and where it came from, `expectedDays`, `unexpectedNullDays` split into `emptyRunDays` and `noRunDays`, `predictedNullDays`, `trimmedLoadDays` and the `trimFence`, the trend, and the averages per run and per loading day.
- `series`: one point per calendar day with `rowsWritten` (and the insert/update/delete split), `expected`, `severity` (the robust sigma of the residual), `anomaly` and `reason`, `unexpectedNull`, `imputed`, `immature`, `trimmed`, and the excluded backfill runs.
- `sparkline` (board rows too): the last 14 days as parallel arrays, with the positions of flagged and missed days.

A worked example. A vendor feed wrote 107,309 to 107,394 rows every Tuesday to Friday for weeks, then 107,203 to 107,317 from one Tuesday on, with about thirty rows of variation inside a weekday. Before the size floors, `levelShift` fired at 4.2 sigma ("shifted down to a new level on 2026-09-01") and two ordinary days were flagged at 12 sigma; nothing else fired, every expected day had loaded, and the stream was `watch` / `less-than-normal`. The right reading was already in the payload: one non-primary detector, `nullDays` quiet on all 29 expected days, `rateChange` within normal variation, and a shift of 85 rows on a 107,000 level. With the floors the shift's detail now reads "moved down by 85 row(s) (0.1% of its 107,253 row(s) level): 4.2 sigma against this stream's very steady history, but far too small to be a change in what it delivers", no day is flagged, and the stream is healthy. When a finding rests on one volume detector and the stream still loads on every expected day, that is the shape to expect, and the answer to "why is this flagged" is the `detail` sentence with its numbers.

## Configuration touchpoints

`StreamAnomalyOptions` in `src/SqlFlow.HealthCheck/StreamAnomaly.cs` holds every knob above with its default and the reasoning for it; the control plane constructs it per stream with the schedule's cadence (`ExpectedGapDaysOverride`) and the last load before the window (`LastKnownLoadUtc`). The scope classification lives in the control plane's `DataStreams` options (`ControlPlane__DataStreams__LandingSchemas` and siblings). The window is 1 to 180 days, 60 by default; the board analyses at most 1,000 streams per request, newest activity first, and reports both the analysed and the total count so a capped sweep is visible.

## See also

- [Health-check detection engine internals](healthcheck-engine.md): the trend, imputation, ESD, PELT and robust statistics this detector reuses.
- [Health-check flow (flowType: hc)](../flow/hc.md): the per-table health check for the tables that warrant one.
- [Control plane](control-plane.md): the API surface the board and the MCP tool read.
