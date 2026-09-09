---
id: flow-ing-schema-incremental
title: "Ingestion flow: schema sync, incremental, initLoad"
type: flow-reference
summary: "Reference for the schema, incremental, and initLoad sections of an ing flow: schema sync, column cleanup, watermark windows, chunked backfill."
keywords:
  - schema.sync
  - cleancolumnnames
  - allowtablerewrite
  - incremental
  - overlapdays
  - lookback
  - initload
  - backfill
yamlPath: "schema / incremental / initLoad (flowType: ing)"
related:
  - concept-schema-evolution
  - concept-ingestion-run-pipeline
  - flow-incremental
  - guide-incremental-and-backfill
sourceRefs:
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Core/Ingestion/DefaultColumnNameCleaner.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs
  - src/SqlFlow.SqlServer/Ingestion/ChunkRanges.cs
---

# Ingestion flow: schema sync, incremental, initLoad

Three optional sections of a `flowType: ing` document control how the target table keeps up with the source. `schema` governs schema synchronization and column-name cleanup on each run, `incremental` bounds each read with a watermark probed from the target, and `initLoad` plans a one-time chunked backfill of a large historical table. All three are mapped in src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs onto the policy records in src/SqlFlow.Core/Ingestion/IngestionPolicies.cs; every section is optional, and an omitted section takes all defaults.

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
  columns: [ModifiedDate]
  overlapDays: 7
```

## Keys reference

### schema

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `sync` | bool | no | `true` | Propagate new source columns to the target on each run. |
| `cleanColumnNames` | bool | no | `false` | Clean target column names on sync (designed for SAP sources). |
| `cleanColumnNameRegex` | string | no | null | Regex whose matches are removed or replaced in column names; overrides the built-in default pattern. |
| `replaceInvalidCharsWith` | string | no | null | Replacement text for regex matches; blank means matches are removed. |
| `convertUnicodeToNonUnicode` | bool | no | `false` | Convert Unicode source types to non-Unicode on the target (designed for SAP Open Hub, which uses NVARCHAR for all text). |
| `allowTableRewrite` | bool | no | `false` | Permit an expensive table-rewrite ALTER (for example int to bigint) to run inline. |

### incremental

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `columns` | list of string | no | `[]` | High-water columns whose MAX is probed from the target to bound the next read. |
| `dateColumn` | string | no | null | Date column used with `overlapDays` to build an overlapping window. |
| `overlapDays` | int | no | `7` | Days subtracted from the date watermark to re-read a safety window. |
| `lookback` | int | no | `0` | Value subtracted from a non-date (numeric) watermark to re-read a safety window, in key units rather than days. |
| `fullLoad` | bool | no | `false` | Force a full load regardless of the incremental settings. |
| `fetchMinValuesFromSource` | bool | no | `false` | Also probe MIN from the source; when the source MIN is below the target MAX, widen the window back to the source minimum to reprocess history. |

### initLoad

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `enabled` | bool | no | `false` | Turn the chunked backfill on. |
| `fromDate` | date string | no | null (planner falls back to three years back) | Low bound of the backfill window, `2024-01-31` style. |
| `toDate` | date string | no | null (planner falls back to today) | High bound of the backfill window. |
| `batchBy` | string | no | null (planner treats null as `M`) | Chunk unit: `M` calendar month, `D` day, `K` integer key ranges. |
| `batchSize` | int | no | null (planner treats null as 1) | Chunk width: months or days per chunk, or rows per key bucket. |
| `keyColumn` | string | no | null | Integer column to bucket by when `batchBy: K`. |
| `keyMaxValue` | int | no | null (planner falls back to 10000000) | Upper bound of the key range when `batchBy: K`. |

## schema section

### schema.sync

Default `true`. When on, new columns seen on the source are added to the target on each run (the legacy `SyncSchema` behavior). Turning it off freezes the target's column set; the flow still runs but new source columns are not propagated. Integration coverage lives in tests/SqlFlow.Core.Tests/Integration/SchemaSyncIntegrationTests.cs.

### schema.cleanColumnNames, cleanColumnNameRegex, replaceInvalidCharsWith

Default `false`. When enabled, src/SqlFlow.Core/Ingestion/DefaultColumnNameCleaner.cs applies a "remove invalid characters" regex to every column name during sync:

- The pattern is `cleanColumnNameRegex` when set, otherwise the built-in default (the canonical legacy `flw.SysCFG.ColCleanupSQLRegExp` value, which keeps ASCII letters, digits, underscore, and the Norwegian letters æøåÆØÅ, and matches everything else).
- Matches are replaced with `replaceInvalidCharsWith`, or removed when it is unset.
- A name cleaned down to nothing becomes `EmptyColumnName`.
- Case-insensitive collisions are de-duplicated with a numeric suffix.

An invalid pattern fails the run with:

```text
Invalid CleanColumnNameRegex '<pattern>': <regex error>
```

The feature is opt-in and SAP-oriented; ordinary relational sources rarely need it.

### schema.convertUnicodeToNonUnicode

Default `false`. Converts Unicode source data types to their non-Unicode counterparts on the target during sync. Designed for SAP Open Hub, which emits NVARCHAR for all text.

### schema.allowTableRewrite

Default `false`. Some type changes (for example widening `int` to `bigint`) require SQL Server to rewrite every row of the table while holding a table lock. By default the engine refuses such a plan and fails the run with a `SchemaRewriteNotPermittedException` (src/SqlFlow.Core/SchemaRewriteNotPermittedException.cs):

```text
Schema evolution on '<target>' requires a table rewrite on column(s) <columns> (for example int to bigint). This holds a table lock for the full rewrite and is refused by default. Set AllowTableRewrite to run it, ideally in a maintenance window.
```

Set `allowTableRewrite: true` explicitly, ideally for a maintenance-window run, to let the rewrite proceed. The legacy engine had no such guard.

## incremental section

A flow is incremental when `columns` is non-empty or `dateColumn` is set (`IncrementalPolicy.IsIncremental` in src/SqlFlow.Core/Ingestion/IngestionPolicies.cs). Without either, every run reads the whole source (optionally filtered); a keyed flow still upserts, a keyless flow appends.

The window is resolved by src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs. It probes `MAX` of every watermark column over the target, then appends predicates to the source read. An incremental column referenced here must exist among the source columns; otherwise the run fails with:

```text
Incremental column '<name>' is not among the source columns.
```

When the target table does not exist yet or all probed watermarks are NULL, the run is a full load: there is nothing to bound by.

### incremental.columns

High-water columns (typically an identity, a rowversion-like counter, or a modified timestamp). Each run reads only rows where the column is strictly greater than the target's MAX. Blank and duplicate entries are ignored. `overlapDays` never applies to these; their safety window is `lookback`.

### incremental.dateColumn and overlapDays

`dateColumn` is a date-typed watermark with a safety window: the probed MAX is shifted back `overlapDays` days (`DATEADD(day, -N, MAX(...))`), so recent rows are re-read and late-arriving updates are captured by the keyed upsert. The default is 7.

Caution: the ing-flow default for `overlapDays` is 7, but the file flow's incremental section defaults to 0 (compare `OverlapDays ?? 7` in src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs with `OverlapDays ?? 0` in src/SqlFlow.Yaml/YamlFlowLoader.cs). Do not assume the same default across flow types.

`dateColumn` also names the chunking column for date-based `initLoad` plans and for a `--from`/`--to` backfill window at run time.

### incremental.lookback

The numeric counterpart of `overlapDays`, and the only safety window a non-date watermark has. The value is subtracted from the probed `MAX` of every `incremental.columns` mark, so the target probe reads `MAX([Id]) - 250` rather than a bare `MAX([Id])`, and with `fetchMinValuesFromSource` the source `MIN` probe is shifted by the same amount so the two stay symmetric. Like the `DATEADD` it mirrors, the shift happens inside the probe, so the watermark a run reports is the one the read was actually bounded by. The default is `0`, which reproduces the bare `MAX`.

Why a monotonic key needs one: an id is allocated at `INSERT` but the row only becomes readable at `COMMIT`, so a reader can see id N+k while N is still in flight. A watermark taken as the bare `MAX` of what was visible advances past N, and the next run's strict `>` can never reach back down to it. The row is skipped permanently, and nothing reports it; the signature in the target is contiguous blocks of missing ids, each starting at exactly the previous run's watermark plus one.

```yaml
incremental:
  columns: [Id]
  lookback: 250      # key units, not days
```

Size it above the number of ids that can be in flight at once, and keep in mind:

- The re-read window is reconciled by the keyed upsert, so a keyed flow re-reads rows it already holds and inserts nothing. A **keyless** flow (no `load.keyColumns`) appends them again; that is the one configuration where a lookback duplicates rows.
- The shift is skipped silently for a watermark column arithmetic does not apply to. Only `tinyint`, `smallint`, `int`, `bigint`, `decimal`, `numeric`, `money` and `smallmoney` receive it; a string, binary, rowversion, or `float`/`real` high-water column keeps its bare `MAX` rather than being fed a subtraction the source would reject.
- It is measured in the watermark's own units, and the subtraction is plain decimal arithmetic on the stored value. On a digit-packed stamp such as a `decimal` shaped `yyyyMMddHHmmss` that is not time arithmetic: subtracting 100 from `20260825120000` yields `20260825119900`, a value no row can hold, so the window rewinds to the start of that hour and no further. Size such a stamp's lookback generously, or put a real date column under `dateColumn` and use `overlapDays` when a true time window is what you want.

### incremental.fullLoad

Forces a full read regardless of the watermark configuration. In the legacy filter precedence a replace-style `source.filter` wins first, then `fullLoad`, then an empty or absent target, then the incremental-column predicate, then the date predicate; an append-style filter is concatenated last.

The same effect is available per run without a YAML edit:

```bash
sqlflow run orders-ingestion.flow.yaml --full
```

### incremental.fetchMinValuesFromSource

Default `false`. When on, the resolver additionally probes `MIN` of the watermark columns on the source (the date mark shifted back by the same `overlapDays`, so the comparison is symmetric). If the source MIN is below the target MAX, the window widens back to the source minimum with a `>=` comparison, reprocessing the whole source through the upsert. Use it when history in the source can be restated.

### Downstream watermark anchoring (automatic)

In a chained `pre -> ods` topology the landing (pre) flow's durable record of "what has already been ingested end to end" effectively lives in the ods (silver) table it feeds, not in the pre table it writes. So by default, whenever lineage resolves a single downstream table for an incremental flow, the high-water `MAX` is probed from that downstream (silver) table instead of the flow's own target. Deleting rows from the ods table lowers the watermark and the corresponding source rows are re-pulled on the next run: a self-healing backfill triggered by a downstream delete rather than a manual reload. There is no YAML key to enable this; bronze is driven by what silver holds. It applies to both `ing` flows (`IncrementalWindowResolver`) and file flows (`FlowRunner`).

The downstream table is resolved from the shadow catalog's lineage graph by the control plane and handed to the run (the execution engine has no catalog of its own): it is the single unambiguous table that the flow's downstream consumers write, found by walking the flow-level dependencies and their `Writes`/`Creates` edges (`ResolveDownstreamWatermarkTableAsync` in src/SqlFlow.Node/RunWorker.cs). The anchor is applied only when exactly one such table resolves; a fan-out to several downstream tables, or a chain lineage has not computed yet, leaves the probe on the flow's own target.

Two safety properties hold, so the anchor can never make a run worse than the flow's own-target default:

- Column-safe: if the downstream table renamed or dropped the watermark column (a typed view's output alias differs from the source column name), the resolver falls back to the flow's own target rather than probing a `MAX` over a column that is not there.
- Reachability-safe: if the downstream table is not reachable on the target connection (a different server, or not created yet) or is empty, the resolver falls back to the flow's own target rather than mistaking an absent object for an empty target and forcing a full reload.

For a file flow the anchor is also authoritative over the on-disk run-history floor: the whole point is that the watermark tracks the silver table and regresses with it, so a downstream delete re-opens the window (the run-history floor, which normally prevents regression, is bypassed while anchored). An explicit `incremental.table` override on the flow (a deliberate operator choice) still wins over the lineage-derived downstream table.

A direct CLI run has no lineage graph to resolve the downstream table from, so `sqlflow run` simply probes the flow's own target. The feature is exercised end to end by the control-plane worker. The run detail's watermark source reads `downstream MAX [schema].[table]` when the anchor applied, and `target MAX [schema].[table]` when it fell back.

## initLoad section

A one-time chunked backfill (the legacy InitLoad family): it bounds a large historical load and splits the source read into segments so no single read has to move the entire table. When `enabled: true`, the run ignores the incremental watermark and instead executes the segment plan built by src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs; all segments feed the same staging table, so the rest of the run (schema sync, upsert, assertions) is unchanged.

### initLoad.fromDate and toDate

Invariant-culture dates. A malformed value fails validation at load time with:

```text
<file>: 'initLoad.fromDate' must be a date like 2024-01-31, got '<value>'.
```

(and the same for `initLoad.toDate`). When unset, the planner applies the legacy metadata defaults: `fromDate` is three years back from today and `toDate` is today.

### initLoad.batchBy and batchSize

A single-character unit, uppercased and trimmed by the planner; null is treated as `M`:

- `M`: calendar-aligned month chunks (snapped to month end, first and last chunk clamped to the window), `batchSize` months per chunk.
- `D`: rolling fixed-width day windows, `batchSize` days per chunk. A day batch size below 1 fails with `Day batch size must be at least 1.`
- `K`: inclusive integer key buckets `[lo, hi]` from 0 up to `keyMaxValue`, `batchSize` rows per bucket.

Date segments are half-open (`>= start AND < end + 1 day`); key segments are inclusive. A trailing `IS NULL` segment is always appended so rows with a NULL date or key are not dropped. Any other unit produces an empty plan and stages nothing (matching legacy, no throw).

Date-based plans chunk on `incremental.dateColumn`; there is no separate initLoad date column. Missing prerequisites fail fast:

```text
InitLoad by date requires Incremental.DateColumn to be set.
InitLoad by key requires InitLoad.KeyColumn to be set.
```

### initLoad.keyColumn and keyMaxValue

For `batchBy: K`: the integer column to bucket by, and the top of the key range. `keyMaxValue` defaults to 10000000 in the planner when unset.

### Per-run window override

A trigger-time `--from`/`--to` re-windows the chunk plan for that run only, so one initLoad definition serves any historical slice without a YAML edit (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs):

```bash
sqlflow run orders-ingestion.flow.yaml --from 2022-01-01 --to 2022-12-31
```

Planner behavior is specified in tests/SqlFlow.Core.Tests/InitLoadPlannerTests.cs.

## Fuller example

Adapted from samples/ingestion/orders-ingestion.flow.yaml:

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

schema:
  sync: true
  allowTableRewrite: false

incremental:
  columns: [ModifiedDate]
  dateColumn: OrderDate
  overlapDays: 7
  lookback: 0
  fullLoad: false
  fetchMinValuesFromSource: false

# One-time historical load: enable, run once, then disable again.
initLoad:
  enabled: true
  fromDate: 2020-01-01
  toDate: 2024-12-31
  batchBy: M          # M month | D day | K integer key ranges
  batchSize: 1
```

Validate and run:

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

## See also

- [Schema evolution](../concepts/schema-evolution.md)
- [Ingestion run pipeline](../concepts/ingestion-run-pipeline.md)
- [File flow: incremental section](./incremental.md)
- [Guide: incremental loads and backfill](../guides/incremental-and-backfill.md)
