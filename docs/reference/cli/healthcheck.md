---
id: cli-healthcheck
title: sqlflow healthcheck (ad-hoc)
type: cli-command
summary: Zero-configuration ML anomaly check against any table, no pipeline file; auto-detects the date column and runs the full hc detection stack.
keywords:
  - healthcheck
  - anomaly detection
  - ml
  - ad-hoc
  - fail-on-anomaly
  - state-dir
  - date column
  - esd
  - pelt
cliCommand: healthcheck
related:
  - flow-hc
  - concept-run-artifacts
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.HealthCheck/DateColumnSelector.cs
  - src/SqlFlow.HealthCheck/WithoutDatabaseHealthCheck.cs
  - src/SqlFlow.Core/HealthChecks/HealthCheckFlow.cs
---

# sqlflow healthcheck

## Synopsis

```bash
sqlflow healthcheck --object [db.]schema.table [--source <ref>] [--provider mssql|azdb]
                    [--date-column <name>] [--base-value <expr>] [--filter <bool expr>]
                    [--threshold <sigma>] [--alpha <p>] [--budget <seconds>]
                    [--maturity <days>] [--state-dir <dir>] [--retrain]
                    [--fail-on-anomaly] [--json] [--out|-o <file>] [--show-sql]
                    [--log-level <info|debug|trace>]
```

## Description

Runs the ML health check against any table with zero configuration: no pipeline file, no registration, no positional argument. The command auto-detects the date column from the catalog (pin `--date-column` to override), defaults the monitored metric to `COUNT(*)`, and keeps trained models in memory unless `--state-dir` persists them between runs.

Execution goes through the exact same runner and detection stack as `flowType: hc` documents (built by `WithoutDatabaseHealthCheck.BuildRunner`, one code path): Theil-Sen robust trend, AutoML calendar model, robust generalized ESD point anomalies, PELT level shifts, maturity handling, and data-quality probes (future-dated, sentinel-dated, NULL-dated rows). The run executes with `ExecMode` `cli-adhoc` and the flow name `<database>.<schema>.<table>`.

The monitored SQL is T-SQL, so the connection must be SQL Server or Azure SQL. Passing `--provider mysql`, `postgres`, or `oracle` prints `ERROR  healthcheck runs T-SQL on the monitored table; --provider must be mssql or azdb.` and exits 1.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `--object <name>` | yes | The monitored table as `schema.table` or `database.schema.table`. Bracketed parts (`[My Schema].[My Table]`) are accepted. Missing `--object` fails with `This command requires --object <schema.object \| database.schema.object>.` |

With a two-part `--object`, the database is resolved from the connection's default catalog via `SELECT DB_NAME()`. When the connection has no default database, the command prints `ERROR  the connection has no default database; use a three-part --object database.schema.table.` and exits 1.

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--source <ref>` | string | `${env:SQLFLOW_SOURCE}` | The connection to read through: a `${env:NAME}` or `${keyvault:vault/secret}` reference, or a literal connection string. A value that looks like an embedded credential triggers a stderr warning (it lands in shell history). |
| `--provider <p>` | enum | `mssql` | The connection's provider. Only `mssql` (alias `sqlserver`) and `azdb` are accepted; `mysql`, `postgres`, and `oracle` are rejected because the monitored SQL is T-SQL. Any other value fails with `Unknown --provider '<p>'. Allowed: mssql, azdb, mysql, postgres, oracle.` |
| `--date-column <name>` | string | auto-detected | The date column the metric is grouped by. When omitted, it is auto-detected from the catalog and the reasoning is printed (see below). |
| `--base-value <expr>` | T-SQL aggregate | `COUNT(*)` | The aggregate expression to monitor per date, e.g. `SUM(Amount)` or `COUNT(DISTINCT CustomerID)`. The metric name is derived: `rowCount` for a plain `COUNT(*)`, `value` otherwise. |
| `--filter <expr>` | T-SQL boolean | none | ANDed into the series query. |
| `--threshold <sigma>` | double | `2.0` | Severity floor in robust sigmas: the minimum deviation a statistically significant point must still reach to be reported. |
| `--alpha <p>` | double | `0.025` | Significance level of the generalized ESD test. |
| `--budget <seconds>` | int | `30` (`120` with `--state-dir`) | AutoML experiment budget. Ephemeral runs retrain every time, so the default stays snappy; a persisted state dir gets the full document-mode budget because the model is reused afterwards. |
| `--maturity <days>` | int | `1` | Trailing days whose data may still be arriving: scored and shown, never counted as anomalies. `0` disables the window. |
| `--state-dir <dir>` | path | none | Persist trained models and the run history here. Without it, models live in an in-memory ephemeral store and the run leaves nothing behind. |
| `--retrain` | flag | off | Force fresh models this run (training `Always` instead of the default `Auto`). |
| `--fail-on-anomaly` | flag | off | Exit 2 when the run succeeds and finds one or more mature anomalies (CI gating). |
| `--json` | flag | off | Print the full outcome (result plus scored report) as one JSON document on stdout; notes such as the date-column reasoning move to stderr so stdout stays clean JSON. |
| `--out <file>`, `-o <file>` | path | none | Write just the scored report JSON to a file. |
| `--show-sql` | flag | off | Print the rendered SQL trace after the run. |
| `--log-level <l>` | enum | `info` | Run-log detail: `info`, `debug`, or `trace`. Gates both the progress echoed to the console (suppressed by `--json`) and the `run.log` written with `--state-dir`. |

## Behavior

### Date-column auto-detection

When `--date-column` is omitted, the object is introspected and `DateColumnSelector` picks the column:

- Only date-typed columns qualify: `date`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`.
- Each candidate gets one tier, lower wins. A name ending in `_dw` is tier 5 (warehouse system columns; this check runs first, so it wins even when the name also contains `date`). Otherwise: 1 for names ending in `date` (business dates like `OrderDate`), 2 for other names containing `date`, 3 for event stamps (names containing `created`, `modified`, `updated`, `inserted`, `timestamp`, or ending in `at` or `time`), 4 for any other date-typed column.
- Ties break deterministically: shortest name first, then by name (case-insensitive ordinal).

The choice and any alternatives are printed, for example `date column: chose 'OrderDate' (tier 1); alternatives: InsertedDate_DW. Pin --date-column to override.` When no column qualifies the command prints `ERROR  could not auto-detect a date column: <reason>. Pass --date-column <name>.` and exits 1. When the object itself is not found: `ERROR  object <name> was not found.`

### State and run artifacts

Without `--state-dir` the run is fully ephemeral. With `--state-dir`, models persist there and the run also writes the canonical artifact set via `RunHistory.WriteAt`: `run.json` (with `flowKind` `hc`), `run.log`, `trace.sql`, and `healthcheck.json` (the full scored series). When the write succeeds, the run directory is printed as `run log: <path>` unless `--json` is set; a failed history write surfaces a warning and never fails the run.

### Text output

On success the summary line is `OK  N anomalies across M metric(s) (<frequency>) in Ds`; on failure, `FAILED  <error>`. A data-quality line follows when any probe fired: `data quality: X future-dated, Y sentinel-dated, Z NULL-dated row(s)`. Each metric then prints anomalies, series points, imputed and immature counts, level shifts when present, the model trainer with `trained` or `reused`, and R2 when a fit exists. Findings follow, grouped per metric: `SHIFT` lines for level shifts, then up to 10 `ANOMALY` lines ordered by severity (worst first), with `<metric>: N more anomalies in healthcheck.json` when truncated.

## Examples

Check row counts on a table, letting the command pick the date column (connection from `SQLFLOW_SOURCE`):

```bash
sqlflow healthcheck --object DW.dbo.Orders
```

Monitor daily revenue for non-cancelled orders with an explicit source and date column, gating a CI job:

```bash
sqlflow healthcheck --object DW.dbo.Orders --source '${env:SQLFLOW_DW}' \
  --date-column OrderDate --base-value 'SUM(Amount)' \
  --filter "OrderStatus <> 'Cancelled'" --fail-on-anomaly
```

Persist models and run history between runs (raises the AutoML budget default to 120 seconds) and emit machine-readable output:

```bash
sqlflow healthcheck --object dbo.Orders --source '${env:SQLFLOW_DW}' \
  --state-dir ./hc-state --json > outcome.json
```

Force fresh models and write just the scored report:

```bash
sqlflow healthcheck --object DW.dbo.Orders --retrain --out report.json
```

## Exit behavior

| Code | Meaning |
| --- | --- |
| 0 | The run succeeded and either `--fail-on-anomaly` was not set or no anomalies were found. |
| 1 | The run failed, or a pre-run error occurred (unsupported provider, no default database for a two-part object, object not found, no auto-detectable date column). |
| 2 | `--fail-on-anomaly` was set, the run succeeded, and `TotalAnomalies > 0`. |

## See also

- [flowType: hc](../flow/hc.md): the document form of the same engine, with multiple named metrics, holidays, `maxAnomalyFraction`, `sentinelDateFloor`, and `retrainAfterDays`.
- [Run artifacts](../concepts/run-artifacts.md): `run.json`, `run.log`, `trace.sql`, and `healthcheck.json`.
- [CLI conventions](../concepts/cli-conventions.md): `--json`, `--show-sql`, `--log-level`, secret references, and `${env:SQLFLOW_SOURCE}`.
