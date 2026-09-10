---
id: flow-hc
title: "Health-check flow (flowType: hc): metrics and ML detection"
type: flow-reference
summary: "flowType: hc reference: monitored metrics, dateColumn, maturityDays, holidays, and the ml block (thresholds, ESD alpha, training policy)."
keywords:
  - healthcheck
  - flowtype hc
  - metrics
  - datecolumn
  - anomaly detection
  - theil-sen
  - esd
  - pelt
  - maturitydays
yamlPath: "(root, flowType: hc)"
related:
  - cli-healthcheck
  - concept-run-artifacts
  - concept-healthcheck-engine
  - flow-overview
sourceRefs:
  - src/SqlFlow.Yaml/YamlHealthCheckFlowLoader.cs
  - src/SqlFlow.Yaml/HealthCheckYaml.cs
  - src/SqlFlow.Core/HealthChecks/HealthCheckFlow.cs
  - src/SqlFlow.HealthCheck/HealthCheckFlowRunner.cs
  - src/SqlFlow.HealthCheck/HealthCheckModelStore.cs
  - src/SqlFlow.HealthCheck/WithoutDatabaseHealthCheck.cs
  - src/SqlFlow.Cli/Program.cs
  - samples/healthcheck/orders-healthcheck.flow.yaml
---

# Health-check flow (flowType: hc)

A health-check flow learns the expected per-date behavior of one or more aggregate metrics on a monitored SQL Server table and reports the dates whose actual values deviate significantly from the prediction. The detection stack per metric: a Theil-Sen robust trend, an AutoML calendar model over the detrended series, generalized ESD point anomalies with median/MAD studentization, PELT level-shift detection, and a weekday-median baseline for short histories, plus table-level data-quality probes (future-dated, sentinel-dated, and NULL-dated rows). All metrics are computed in one scan of the target, grouped by `dateColumn`. Trained models persist per metric under `.sqlflow/state/<flow>/<metric>/` next to the flow document; the scored series is the run's `healthcheck.json` artifact.

## Minimal example

```yaml
flowType: hc
name: orders-watch

connections:
  dwh: ${env:SQLFLOW_DW}

target:
  server: dwh
  object: DW.dbo.Orders

dateColumn: OrderDate
baseValue: COUNT(*)
```

## Embedded in an ingestion flow (the usual form)

A health check over a table that an ingestion flow loads does not need its own file: declare it as the
`healthCheck:` block of the ing document, so everything about the table lives in one place. The block is the
same declaration body as this document minus `target`/`connections` (both come from the flow's own target),
and it expands into a full sibling hc pipeline: its own runs, its own trained models, its own catalog row,
ordered after the load by lineage.

```yaml
flowType: ing
name: orders_dw
# ... source / target / load ...

healthCheck:            # derived pipeline 'orders_dw_hc' over the flow's target
  dateColumn: OrderDate
  baseValue: COUNT(*)
  # mode: auto          # opt into schedules/batch runs; embedded checks default to manual (on demand)
```

Two deliberate differences from the standalone document:

- **`mode` defaults to `manual`**: an embedded check is an on-demand instrument. Run it from the GUI (the
  "Run health check" button on the flow's pipeline page or a run's Health tab), trigger the derived flow name
  via `POST /api/v1/runs`, or locally with `sqlflow run <file> --health-check`. Declare `mode: auto` to have
  batch/node group runs and schedules execute it like any flow (lineage orders it after the load).
- **`name` is derived** (`<flowName>_hc`) unless overridden, and the flow must declare `name:`.

Keep the standalone document for tables no single flow owns (a mart table built by an sp flow, a legacy table
with no V3 pipeline); its `mode` defaults to `auto`.

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | | Must be `hc`. |
| `name` | string | yes | | The flow's identity: becomes `SysAlias` and seeds the stable flow id. Keys the state and run folders. |
| `description` | string | no | none | Free-text description. |
| `batch` | string | no | none | Batch label carried into the run record. |
| `mode` | string | no | `auto` | `auto` lets schedules and batch/node group runs execute the check like any flow. `manual` excludes it from every automatic dispatch (the scheduler skips its schedules with a warning; group expansion and local batch membership omit it), so it runs only when triggered directly: the GUI's "Run health check" button, a single-flow `POST /api/v1/runs`, or a direct CLI run. Case-insensitive; anything else fails with `'mode' has unknown value '<v>'. Allowed: auto, manual.` |
| `connections` | map | no | empty | Named connections. Each value is a plain string (a SQL Server connection reference), a map with `provider` and `connection`, or bare (resolves `${env:SQLFLOW_CONN_<NAME>}` by convention). |
| `target` | map | yes | | The monitored endpoint: `server` (or inline `connection` plus optional `provider`) and `object`. |
| `target.server` | string | conditional | | Name of a declared connection in `connections`. |
| `target.connection` | string | conditional | | A direct connection instead of `server`; registered under a synthesized name. |
| `target.provider` | string | no | `mssql` | Provider of a direct `connection`. Must resolve to SQL Server: `mssql` or `azdb`. |
| `target.object` | string | yes | | Strict three-part name `Database.Schema.Table`. |
| `dateColumn` | string | yes | | The date column the metrics are grouped by. |
| `baseValue` | string | conditional | | Single-metric shorthand: one aggregate T-SQL expression. Mutually exclusive with `metrics`. |
| `metrics` | list | conditional | | Multi-metric form: entries of `{name, baseValue}`. Mutually exclusive with `baseValue`. |
| `metrics[].name` | string | no | derived | Metric name; letters, digits, `_`, `-`; unique within the flow. |
| `metrics[].baseValue` | string | yes | | The aggregate expression producing the value per date. |
| `filter` | string | no | none | Boolean T-SQL expression ANDed into the series query; shared by every metric. |
| `maturityDays` | int | no | `1` | Trailing days whose data may still be arriving: scored, never counted as anomalies. 0 to 30. |
| `sentinelDateFloor` | date | no | `1990-01-01` | Rows dated before this count as sentinel-dated in the data-quality probe. |
| `ml` | map | no | all defaults | The model and detection settings (see below). |
| `ml.maxExperimentSeconds` | int | no | `120` | AutoML experiment budget in seconds, per metric. 1 to 86400. |
| `ml.anomalyThreshold` | number | no | `2.0` | Severity floor in robust sigmas a significant point must reach to be reported. > 0 and <= 100. |
| `ml.esdAlpha` | number | no | `0.025` | Significance level of the generalized ESD test. Strictly between 0 and 0.5. |
| `ml.maxAnomalyFraction` | number | no | `0.10` | ESD search bound: at most this share of the series can be flagged. In (0, 0.49]. |
| `ml.training` | enum | no | `auto` | `auto`, `always`, or `never`. |
| `ml.retrainAfterDays` | int | no | null | With `training: auto`, retrain a stored model older than this many days. >= 1. |
| `holidays` | list of dates | no | empty | Dates the calendar features treat as holidays (`IsHoliday`). Deduplicated. |

## Key details

### name

Required. Validation and mapping live in src/SqlFlow.Yaml/YamlHealthCheckFlowLoader.cs. The name becomes `SysAlias`, seeds the stable numeric flow id, and keys both the model state folder (`.sqlflow/state/<flow>/`) and the run history folder (`.sqlflow/runs/<flow>/`).

### connections and target

`connections` declares named data sources; `target.server` references one by name. Alternatively `target.connection` carries a direct connection string reference, optionally with `target.provider`. The series and quality queries are T-SQL, so the target connection must be SQL Server; a foreign provider fails at parse time with:

```text
<source>: the target connection '<name>' is '<kind>'; a health-check flow's target must be SQL Server (mssql or azdb).
```

A missing `target` fails with `'target' is required.` A missing `target.object` fails with `'target.object' is required (a three-part name like Database.Schema.Table).` The object must parse as a three-part name; fewer parts fail with `Object name '<name>' must be a three-part [Database].[Schema].[Object] name; found <n> part(s).` Bracketed parts (`[DW].[dbo].[Orders]`) are accepted.

### dateColumn

Required; the error is `'dateColumn' is required (the date column the metrics are grouped by).` At run time the first result column must come back as a date/datetime; a non-date column fails the run with a message telling you to point `dateColumn` at a column SQL Server can return as a date.

### baseValue and metrics

Exactly one form must be present:

- `baseValue: <aggregate>` declares a single metric. Its name is derived by `HealthCheckMetric.DefaultName`: `rowCount` when the expression is `COUNT(*)` (spaces and case are ignored), `value` otherwise.
- `metrics:` declares a list of `{name, baseValue}` entries. A missing `name` gets the same derived default. Each `baseValue` is any aggregate T-SQL expression: `COUNT(*)`, `SUM(Amount)`, `COUNT(DISTINCT CustomerID)`, `AVG(UnitPrice)`. A metric-specific condition goes inside the aggregate itself, for example `SUM(CASE WHEN Status = 'OK' THEN 1 END)`; all metrics share the flow-level `filter` and one table scan.

Setting both fails with `set either 'baseValue' (one metric) or 'metrics' (a list), not both.` Setting neither fails with `a health check needs a metric. Set 'baseValue' (an aggregate like COUNT(*) or SUM(Amount)) or a 'metrics' list of {name, baseValue} entries.` A metric entry without `baseValue` fails with `'metrics[i].baseValue' is required (an aggregate expression).`

Metric names may contain ASCII letters, digits, `_`, and `-` (error: `'metrics[i].name' '<name>' is invalid. Use letters, digits, '_', or '-'.`) and must be unique case-insensitively (error: `metric name '<name>' is declared more than once.`). Names key the trained model's state folder and the report section, so renaming a metric orphans its stored model.

### filter

An optional boolean T-SQL expression ANDed into the series query and shared by every metric. Blank values are treated as absent.

### maturityDays

Trailing days (relative to the run's as-of date) whose data may still be arriving. These points are scored and shown in the report but never counted as anomalies, so today's partial load does not page anyone. Range 0 to 30, default 1; 0 disables the window. Out of range fails with `'maturityDays' must be between 0 and 30, got <n>.`

### sentinelDateFloor

A date; rows dated before it are counted as sentinel-dated rows by the data-quality probe (the 1900-01-01 placeholders that creep into warehouse date columns). Default `1990-01-01`. The series query itself is bounded to the window from `sentinelDateFloor` through the run's as-of date, so sentinel-dated and future-dated rows are counted by the probe instead of poisoning the series.

### ml.maxExperimentSeconds

The AutoML regression experiment budget per metric, in seconds. Range 1 to 86400, default 120. Out of range fails with `'ml.maxExperimentSeconds' must be between 1 and 86400, got <n>.` If AutoML produces no model within the budget the run fails with `AutoML produced no model within <n>s; raise ml.maxExperimentSeconds.`

### ml.anomalyThreshold

The minimum severity, in robust sigmas of prediction error, a statistically significant point must still reach to be reported: the operational guard on top of the ESD significance test. Must be finite, > 0, and <= 100; default 2.0. Invalid values fail with `'ml.anomalyThreshold' must be a positive number of standard deviations (at most 100), got <v>.`

### ml.esdAlpha

The significance level of the generalized ESD test: the probability of a clean series producing a deviation this extreme. Lower means fewer false alarms. Must be strictly between 0 and 0.5; default 0.025. Invalid values fail with `'ml.esdAlpha' must be a significance level strictly between 0 and 0.5, got <v>.`

### ml.maxAnomalyFraction

The ESD search bound: at most this share of the series can be flagged as point anomalies. Must be in (0, 0.49]; default 0.10. Invalid values fail with `'ml.maxAnomalyFraction' must be in (0, 0.49], got <v>.`

### ml.training

When the check trains a fresh model versus reusing the persisted one (per metric):

- `auto` (default): trains when no stored model exists, the stored model is older than `ml.retrainAfterDays`, or it was trained by an older detection engine version; otherwise reuses it.
- `always`: trains a fresh model on every run.
- `never`: never trains; scores with the stored model and fails clearly when none exists: `training is 'never' but no stored model exists at '<path>'. Train once with 'training: auto' (or --retrain) before pinning the model.`

Unknown values fail with `'ml.training' has unknown value '<v>'. Allowed: auto, always, never.`

### ml.retrainAfterDays

With `training: auto`, a stored model older than this many days is retrained. Null (omitted) keeps the stored model until it is deleted, outdated by an engine upgrade, or training is forced. Must be >= 1 (error: `'ml.retrainAfterDays' must be at least 1, got <n>.`). Setting it with any other training mode fails with `'ml.retrainAfterDays' only applies with 'ml.training: auto' ('<mode>' ignores model age).`

### holidays

A list of dates the calendar features treat as holidays (the `IsHoliday` feature), so expected-low days are not anomalous. Duplicates are silently deduplicated; a blank entry fails with `'holidays[i]' must not be blank.`

## Run behavior

Implemented in src/SqlFlow.HealthCheck/HealthCheckFlowRunner.cs.

- One series query and one data-quality query run against the target; both are captured in the SQL trace as steps `series.select` and `quality.select` and woven into the run log at Trace level.
- Fewer than 2 observed dates fails the run: `The series query returned <n> date(s); a health check needs at least two to detect the cadence.`
- The AutoML calendar model trains on exactly seven calendar features: `Year`, `Quarter`, `WeekOfYear`, `MonthNumber`, `DayOfWeekNumber`, `IsWeekend`, `IsHoliday`. The observed value and every bookkeeping column are excluded from featurization (a deliberate fix of the legacy engine, which leaked the observed value into its own features). The `MLContext` seed is fixed at 42, so two runs over the same series train the same model.
- Series with fewer than 10 mature points (`HealthCheckFlowRunner.MinimumPointsForTraining`) skip AutoML in favor of the weekday-median baseline, which still catches missing data and gross deviations.
- Level shifts (PELT over the residuals of mature observed points) are reported separately from point anomalies: a regime change is one finding, not a wall of daily anomalies.
- One failing metric gets its own report section and fails the run without silencing the other metrics.
- Models persist through `IHealthCheckModelStore` (src/SqlFlow.HealthCheck/HealthCheckModelStore.cs): the file store writes `healthcheck.model` plus a `healthcheck.model.json` metadata sidecar under `.sqlflow/state/<flow>/<metric>/` next to the flow document, atomically (write to temp, then replace); ad-hoc `sqlflow healthcheck` runs use an ephemeral in-memory store unless `--state-dir` is given. The sidecar's current schema version and engine version are both 2; in `auto` mode a model trained by an older engine version is retrained. Deleting a metric's state folder forces retraining under `auto` (`always` retrains regardless of the folder; `never` fails instead of retraining until a model exists again).
- Every run writes the canonical artifacts to `.sqlflow/runs/<flow>/` next to the pipeline file: `run.json`, `run.log`, `trace.sql`, and `healthcheck.json` (the full scored series).

### CLI flags and exit codes

`sqlflow run <file>` accepts `--retrain` (train fresh models this run) and `--fail-on-anomaly`. Exit codes (src/SqlFlow.Cli/Program.cs, `HealthCheckExitCode`): 0 on success, 1 on failure, 2 when `--fail-on-anomaly` was set and the run found anomalies (the CI gate, distinguishable from a broken run). `--log-level info|debug|trace` sets the run.log detail.

```bash
sqlflow validate orders-healthcheck.flow.yaml
sqlflow run orders-healthcheck.flow.yaml
sqlflow run orders-healthcheck.flow.yaml --retrain
sqlflow run orders-healthcheck.flow.yaml --fail-on-anomaly
```

The same engine also runs without any pipeline file via `sqlflow healthcheck --object [db.]schema.table [--source <ref>]`, with date-column auto-detection (only date-typed columns qualify; business-named date columns outrank generic event stamps, which outrank warehouse `_DW` system columns; ties break deterministically by tier, then shortest name, then case-insensitive name order). See the CLI page for the ad-hoc flags.

## Full example

Adapted from samples/healthcheck/orders-healthcheck.flow.yaml.

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

## See also

- [Ad-hoc health check command](../cli/healthcheck.md)
- [Health-check detection engine internals](../concepts/healthcheck-engine.md)
- [Run artifacts](../concepts/run-artifacts.md)
- [Flow types overview](./overview.md)
