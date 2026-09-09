---
id: guide-incremental-and-backfill
title: Incremental loads and per-run backfill parameters
type: guide
summary: How incremental watermarks work per flow kind, and how to backfill with --full, --from/--to, and --file-pattern without editing YAML.
keywords:
  - backfill
  - watermark
  - incremental
  - full load
  - runparameters
  - initload
  - file pattern
  - window
related:
  - flow-incremental
  - flow-ing-schema-incremental
  - cli-run
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.Core/Runs/RunParameters.cs
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - samples/csv/csv-incremental.flow.yaml
  - samples/ingestion/orders-ingestion.flow.yaml
---

# Incremental loads and per-run backfill parameters

SQLFlow flows load incrementally by probing the target for a watermark; there is no control database, the target table is the state. When you need to reload history, you do not edit the flow's YAML. You pass typed per-run parameters at trigger time: `--full`, `--from`/`--to`, and `--file-pattern` on `sqlflow run`, or the same fields on the control-plane trigger. The parameters apply to that run only, are validated identically at both entry points, and never touch the definition in git. That is what makes a backfill an audited operational act instead of a temporary YAML edit (src/SqlFlow.Core/Runs/RunParameters.cs).

## The per-run parameter contract

`RunParameters` is a typed, closed record: `{ FullLoad, BackfillFrom, BackfillTo, FilePattern }`. It is deliberately never free-form SQL or a YAML patch, so it can be validated at the trust boundary and can never smuggle an injection into generated statements.

CLI surface on `sqlflow run <pipeline.yaml>`:

| Flag | Type | Meaning |
| --- | --- | --- |
| `--full` | bool | Ignore the watermark; read everything the flow's definition selects. |
| `--from <date>` | date | Low bound of an externally bounded window (inclusive), UTC. |
| `--to <date>` | date | High bound of the window. Requires `--from`. |
| `--file-pattern <glob>` | string | Narrow a file flow to one glob for this run. File flows only. |

Dates parse invariant-culture and are treated as UTC, for example `2023-01-15` or `'2023-01-15 06:00:00'`. An unparseable value fails fast with:

```text
--from '<value>' is not a date; use e.g. 2023-01-15 or '2023-01-15 06:00:00'.
```

Validation rules (`RunParameters.Validate()`, enforced by both the CLI and the control-plane trigger):

- `--full` and a window are mutually exclusive: "fullLoad and a backfill window are mutually exclusive: full load ignores every bound; a window IS the bound."
- `--to` must be strictly after `--from` ("backfillTo must be after backfillFrom."); equal bounds are rejected.
- `--to` without `--from` is rejected: "backfillTo requires backfillFrom (an upper bound alone is not a window)." An open-ended window (`--from` with no `--to`) is valid.
- A file pattern must be 1 to 200 characters (`MaxFilePatternLength = 200`) and must not contain control characters.

Window semantics: `BackfillFrom` is inclusive; `BackfillTo` is exclusive for date columns (ingestion flows bound `>= from AND < to`) but inclusive for file dates, matching each mechanism's native window semantics.

The run log records what was applied via `RunParameters.Describe()`: `full load`, `window 2023-01-01 00:00:00 .. 2023-02-01 00:00:00`, `from 2023-01-01 00:00:00`, and `files '<glob>'`.

Running a batch document with `sqlflow run` passes the same parameters to every member: a batch backfill is one command, not N YAML edits (src/SqlFlow.Cli/Program.cs, `RunBatchAsync`).

## File flows: file-date watermark

A file flow (CSV, JSON, XML, Excel, Parquet) declares incremental loading with an `incremental` block. On each run the engine probes the target for `MAX(dateColumn)` and reads only files newer than that watermark; a run that finds nothing new is a clean no-op.

```yaml
name: Csv_Incremental
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_Incremental
incremental:
  dateColumn: FileDate_DW
  overlapDays: 0
```

Keys on the file-flow `incremental` block (src/SqlFlow.Core/Model/FlowDefinition.cs, `IncrementalSpec`):

| Key | Default | Meaning |
| --- | --- | --- |
| `table` | the flow's own target | Fully qualified table to probe for the watermark. |
| `dateColumn` | `FileDate_DW` | Date/datetime column whose MAX is the file-date watermark. |
| `overlapDays` | `0` | Days subtracted from the watermark to re-read a safety window. |
| `watermarkColumn` | unset | Switches to row-level mode: `MAX(column)` on the target bounds source rows. |
| `watermarkOverlap` | `0` | Overlap subtracted from the row-level watermark. |
| `fullLoad` | `false` | Ignore the watermark and reload everything, for either mode. |

`watermarkColumn` and the file-date keys are mutually exclusive: combining `watermarkColumn` with `dateColumn` or `overlapDays` fails validation when the flow loads, and `watermarkOverlap` requires `watermarkColumn` to be set and must not be negative.

When you pass run parameters, `DocumentExecutor.ApplyFileRunParameters` rewrites the same knobs the definition itself uses, so the engine needs no second code path:

- `--from`/`--to` become the source options `initFromFileDate`/`initToFileDate` (format `yyyy-MM-dd HH:mm:ss`), the engine's native externally bounded file-date selection. Both bounds are inclusive for file dates.
- `--file-pattern` becomes the source option `srcFile`, narrowing file discovery to the requested glob.
- `--full`, or any explicit window, forces `Incremental.FullLoad = true` so the watermark probe cannot narrow the explicit bound.

```bash
# Reload every file the definition selects (keyed targets upsert, so this is idempotent)
sqlflow run csv-incremental.flow.yaml --full

# Reprocess one month of files
sqlflow run csv-incremental.flow.yaml --from 2023-01-01 --to 2023-02-01

# Reprocess a single file or prefix
sqlflow run csv-incremental.flow.yaml --file-pattern 'orders_2023-01*.csv'
```

## Ingestion flows (flowType: ing): watermark columns and overlap

Table-to-table ingestion declares incremental loading with high-water mark columns probed from the target (src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs):

```yaml
flowType: ing
name: orders-ingestion
connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}
source:
  server: erp
  object: AdventureWorks.Sales.Orders
target:
  server: dwh
  object: DW.raw.Orders
load:
  keyColumns: [OrderID]
incremental:
  columns: [OrderID]
  lookback: 250
  dateColumn: ModifiedDate
  overlapDays: 7
```

Keys on the ingestion `incremental` block (src/SqlFlow.Yaml/IngestionYaml.cs):

| Key | Meaning |
| --- | --- |
| `columns` | Non-date high-water mark columns; the target is probed with `MAX(column)` and the source read is bounded `column > MAX`. |
| `lookback` | Safety re-read window subtracted from a NUMERIC watermark's `MAX` (and its source `MIN`), in key units rather than days. Default `0`. The counterpart of `overlapDays`, which never applies to `columns`. |
| `dateColumn` | Date watermark column; probed with `DATEADD(day, -overlapDays, MAX(column))` on the target. |
| `overlapDays` | Safety re-read window subtracted from the DATE watermark on both the MAX and MIN probes. Default `7`. Never applies to `columns`. |
| `fullLoad` | Ignore the watermark and read the whole (optionally filtered) source. |
| `fetchMinValuesFromSource` | Also probe `MIN` on the source; when the source minimum is below the target maximum, the window widens back to the source minimum with `>=` (reprocess re-loaded history). |

The MIN probe runs on the source, so identifier quoting and date arithmetic come from the source's own SQL dialect; a foreign source (non SQL Server) uses its own `DateSubtractDays` for the overlap. The numeric `lookback` is plain subtraction and needs no dialect support.

Set a `lookback` whenever the watermark is an id or counter the source allocates before the row commits: without it the bare `MAX` can advance past a row that was still in flight, and the strict `>` of every later run skips it permanently. Size it above the number of ids that can be in flight at once. The keyed upsert reconciles the re-read, so a keyed flow inserts nothing extra; a keyless flow appends the window again. It is skipped for a watermark type arithmetic does not apply to (string, binary, rowversion, `float`/`real`).

An absent or empty target is a full load by definition; the filter precedence is: a replace `filter` wins, then the `fullLoad` flag, then an empty target, then the incremental-column predicate, then the date predicate, with an append filter concatenated last.

Per-run parameters replace the probed watermark entirely; an explicit operator bound is authoritative, so no probe runs and the target's state never narrows it:

```bash
# Ignore the watermark; keyed flows still upsert (idempotent), a keyless flow appends (duplicates)
sqlflow run orders-ingestion.flow.yaml --full

# Bound the incremental date column: ModifiedDate >= '2023-01-01' AND ModifiedDate < '2023-02-01'
sqlflow run orders-ingestion.flow.yaml --from 2023-01-01 --to 2023-02-01
```

A window on an ingestion flow requires `incremental.dateColumn`, and that column's source type must be a date type (`date`, `datetime`, `datetime2`, `smalldatetime`, `datetimeoffset`). Otherwise the run fails with:

```text
A backfill window needs incremental.dateColumn on the flow, so the engine knows which column to bound.
A backfill window bounds incremental.dateColumn '<col>', but its source type is '<type>', not a date type.
```

## Ingestion flows: one-time chunked backfill with initLoad

A large historical load is declared once on an ingestion flow with `initLoad`; the planner chunks the source read so no single query has to move years of data at once (src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs):

```yaml
initLoad:
  enabled: true
  fromDate: 2020-01-01
  toDate: 2024-12-31
  batchBy: M          # M month | D day | K integer key ranges
  batchSize: 1
```

| Key | Default | Meaning |
| --- | --- | --- |
| `enabled` | `false` | Turn the chunked backfill on. |
| `fromDate` | today minus 3 years | Window start for date chunking. |
| `toDate` | today | Window end for date chunking. |
| `batchBy` | `M` | `M` month chunks, `D` day chunks, `K` integer key ranges. |
| `batchSize` | `1` | Units per chunk. |
| `keyColumn` | unset | Integer key column; required when `batchBy: K`. |
| `keyMaxValue` | `10000000` | Upper key bound for `K` chunking. |

Date chunks are half-open `[start, end + 1 day)`, key chunks are inclusive `[lo, hi]`, and a trailing `IS NULL` segment is always appended so rows with a NULL date or key are not dropped. Date chunking (`M`/`D`) uses the flow's `incremental.dateColumn` and requires it; `K` requires `initLoad.keyColumn`.

A trigger-time window re-windows the chunk plan for that run, so one `initLoad` definition serves any historical slice:

```bash
# Replay just 2022 through the existing chunk plan
sqlflow run orders-ingestion.flow.yaml --from 2022-01-01 --to 2023-01-01
```

The run log records `init-load window overridden by run parameters: window 2022-01-01 00:00:00 .. 2023-01-01 00:00:00`.

## Export flows (flowType: exp)

An export flow honors only the backfill window: `--from`/`--to` override the flow's `FromDate`/`ToDate` chunk-plan bounds for that run, logged as `export window overridden by run parameters: ...`. Full load has no export meaning (the planner's window is the selection) and a file pattern is a source concern; supplying them without a window logs:

```text
run parameters '<describe>' do not apply to an export flow (only a backfill window does); running as defined.
```

## Flow kinds with no window surface (sp, hc, inv)

Stored-procedure, health-check, and invoke flows have nothing to window or select. Supplied parameters are visibly acknowledged in the run log rather than silently dropped, so the run detail answers "did my backfill apply?" definitively:

```text
run parameters 'full load' do not apply to flowType 'sp'; the flow runs as defined.
```

A source-control snapshot flow (`flowType: scm`) also has no window surface, but it does not get this acknowledgment: its run options carry no `RunParameters` field at all, so `--full`, `--from`/`--to`, and `--file-pattern` are silently ignored with no run-log entry (src/SqlFlow.Execution/DocumentExecutor.cs, `ExecuteSourceControlAsync`).

## Idempotency notes

- A keyed target (`load.keyColumns` set) still upserts under `--full`, so a keyed full reload is idempotent.
- A keyless flow appends everything under `--full` and will duplicate rows; the engine takes the insert-all apply path exactly as it does for an empty target.
- The same `RunParameters` contract is carried on control-plane triggered runs, so a GUI or API backfill validates and behaves exactly like the CLI flags.

## See also

- [Incremental loading for file flows](../flow/incremental.md)
- [Ingestion schema evolution and incremental settings](../flow/ing-schema-incremental.md)
- [sqlflow run](../cli/run.md)
- [Control plane and run triggering](../concepts/control-plane.md)
