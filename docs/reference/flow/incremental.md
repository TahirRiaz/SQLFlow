---
id: flow-incremental
title: "File flow: incremental section and watermark mechanics"
type: flow-reference
summary: The incremental block for file flows, file-date and row-level watermark modes, overlap windows, fullLoad, and loader validation rules.
keywords:
  - incremental
  - watermark
  - filedate_dw
  - overlapdays
  - watermarkcolumn
  - watermarkoverlap
  - fullload
  - incrementalafterdate
yamlPath: incremental
related:
  - concept-file-discovery-and-lifecycle
  - guide-incremental-and-backfill
  - flow-ing-schema-incremental
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Core/Model/Watermark.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Engine/WatermarkFilteringDataReader.cs
  - src/SqlFlow.SqlServer/SqlServerIncrementalProbe.cs
  - src/SqlFlow.Sources/FileDateFilter.cs
  - src/SqlFlow.Sources/ParquetSourceReader.cs
  - samples/csv/csv-incremental.flow.yaml
---

# File flow: `incremental` section

The `incremental` block turns a file flow into a stateless incremental load: on each run the engine probes the target table for a high-water mark and reads only source data past it. The target table itself is the state, so no control database or bookmark store is involved. When the block is omitted the flow loads everything it reads. Two mutually exclusive modes exist: file-date (the default) bounds which source files are read; row-level (activated by `watermarkColumn`) bounds which source rows are kept.

Minimal working example (adapted from samples/csv/csv-incremental.flow.yaml):

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

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `table` | string | no | the flow's own target | Fully qualified table to probe for the watermark, e.g. `[db].[dbo].[Silver]`. |
| `dateColumn` | string | no | `FileDate_DW` | Date/datetime column whose `MAX` is the file-date watermark. File-date mode only. |
| `overlapDays` | int | no | `0` | Days subtracted from the file-date watermark to re-read a safety window. File-date mode only. |
| `watermarkColumn` | string | no | (unset) | When set, switches to row-level mode: the column whose target `MAX` bounds source rows. |
| `watermarkOverlap` | double | no | `0` | Overlap subtracted from the row-level watermark: days for date/datetime columns, raw units for numeric columns, ignored for text. |
| `fullLoad` | bool | no | `false` | Ignore the watermark and reload everything, in either mode. |

The block is parsed by `MapIncremental` in src/SqlFlow.Yaml/YamlFlowLoader.cs into the `IncrementalSpec` record in src/SqlFlow.Core/Model/FlowDefinition.cs. Keys are camelCase; unknown keys are ignored by the YAML deserializer.

### `table`

Overrides which table is probed for the watermark. Defaults to the flow's own target (`[schema].[table]` of this flow). A fully qualified name such as `[db].[dbo].[Silver]` is accepted, which lets a raw-layer flow watermark against a downstream silver table. If the probe table does not exist (`OBJECT_ID` returns null), the probe yields no watermark and the run performs a full load.

Setting `table` explicitly is rarely needed: by default the control-plane worker automatically anchors the watermark to the next durable table downstream in the lineage graph (the silver table this flow feeds), so deleting rows there re-opens the read window and the source is re-pulled. See "Downstream watermark anchoring" below. An explicit `table` here is a deliberate operator choice and wins over the lineage-derived table.

### Downstream watermark anchoring (automatic)

When lineage resolves a single downstream table for this flow, the watermark is probed from that silver table instead of the flow's own target, with no YAML key to enable it: bronze is driven by what silver holds, so a downstream delete self-heals by re-reading the source on the next run. The downstream table is resolved by the control-plane worker (`ResolveDownstreamWatermarkTableAsync` in src/SqlFlow.Node/RunWorker.cs, walking the flow dependencies to their `Writes`/`Creates` edges) and only applied when exactly one such table resolves. It is column-safe and reachability-safe: a silver table missing/renaming the `dateColumn`/`watermarkColumn`, or unreachable/empty, falls back to the flow's own target rather than failing or forcing a full reload. While anchored, the silver `MAX` is authoritative over the on-disk run-history floor (the floor normally prevents the watermark from regressing; anchoring deliberately lets it regress with silver). The run detail's watermark source reads `downstream MAX [db].[schema].[table]` when applied, `target MAX ...` when it fell back. A direct `sqlflow run` has no lineage graph, so it probes the flow's own target.

### `dateColumn`

The date/datetime column whose `MAX` on the probe table is the file-date watermark. Defaults to `FileDate_DW`, the file-date provenance column the file readers inject by default (source option `includeFileDate`, default `true`), which is why file flows normally leave this key alone. The column must exist on the probe table and be one of `date`, `datetime`, `datetime2`, `smalldatetime`, or `datetimeoffset` (src/SqlFlow.SqlServer/SqlServerIncrementalProbe.cs). Failures:

- Column missing: `Incremental dateColumn '<col>' was not found on <table>.`
- Wrong type: `Incremental dateColumn '<col>' on <table> is not a date/time type; the file-date watermark requires one.`
- Combined with `watermarkColumn`: `incremental: 'dateColumn' is the file-date watermark; it cannot be combined with a row-level 'watermarkColumn'.` (raised at YAML load).

### `overlapDays`

Subtracted from the file-date watermark inside the probe query, so the last N days of files are re-read as a safety window. `0` means exact (only files strictly newer than the watermark, compared to the whole second; see "File-date mode mechanics"). Validation:

- Negative value: `Incremental overlapDays must be zero or positive, was <v>.` (raised by the probe).
- Combined with `watermarkColumn`: `incremental: 'overlapDays' applies to the file-date watermark; with a row-level 'watermarkColumn' use 'watermarkOverlap'.` (raised at YAML load).

### `watermarkColumn`

Setting this key switches the flow to row-level mode. The column must be present on both the probe table and in the source result; use a name that survives column-name cleanup unchanged (an id, a load timestamp, a commit version) so the source predicate and the target probe address the same column. The target column's runtime type must resolve to a `WatermarkKind` (see below); otherwise the probe fails with:

```text
Incremental watermarkColumn '<col>' on <table> is type '<type>', which is not orderable as a watermark. Use an integer, decimal, float, date/time, or text column.
```

A missing column on the probe table fails with `Incremental watermarkColumn '<col>' was not found on <table>.` A watermark column absent from the source result fails the run with `Incremental watermark column '<col>' is not in the source result; available: ...` (src/SqlFlow.Core/Engine/WatermarkFilteringDataReader.cs).

### `watermarkOverlap`

The overlap window subtracted from the row-level watermark before source rows are compared against it. Units depend on the resolved kind (src/SqlFlow.SqlServer/SqlServerIncrementalProbe.cs, `ApplyOverlap`): days for `DateTime` and `DateTimeOffset` kinds, raw units for `Whole`, `Fixed`, and `Floating` kinds, ignored for `Text`. Default `0` means strictly greater than `MAX`. Validation at YAML load:

- Without `watermarkColumn`: `incremental: 'watermarkOverlap' requires a 'watermarkColumn'.`
- Negative: `incremental: 'watermarkOverlap' must be zero or positive, was <v>.`

### `fullLoad`

`fullLoad: true` skips the watermark probe entirely and reloads everything, in either mode (src/SqlFlow.Core/Engine/FlowRunner.cs, `ApplyIncrementalAsync` returns the flow unchanged). Use it for a one-off forced reload without editing the rest of the block.

## File-date mode mechanics

The default mode. Before the load the engine runs the probe (src/SqlFlow.SqlServer/SqlServerIncrementalProbe.cs):

```sql
SELECT DATEADD(DAY, -@overlap, MAX([dateColumn])) FROM <table>;
```

A missing probe table or a `NULL` `MAX` yields no watermark and the run loads all available files, emitting `incremental: no prior watermark on the target; loading all available files`. Otherwise the resolved bound is injected into the source options as `incrementalAfterDate` in ISO 8601 round-trip (`"o"`) format (src/SqlFlow.Core/Engine/FlowRunner.cs). The option is injected by the engine, not authored directly in YAML (src/SqlFlow.Core/Model/PreIngestionCsv.cs documents it as such).

File discovery applies `incrementalAfterDate` as an exclusive lower bound through `FileDateFilter` (src/SqlFlow.Sources/FileDateFilter.cs): a file is included only when its date interval ends strictly after the bound. The same filter also carries the flow's `initFromFileDate`/`initToFileDate` window, so init-load windows and the incremental bound share one selection path; when a `fileDate` spec is configured the date is read from the path or name, otherwise the file's modified timestamp is used, and path-derived dates let whole partition folders be pruned before they are walked.

A file dated by its modified time is compared at the precision `FileDate_DW` stores it, the whole second. The watermark is read back from that column, so the file that set it (modified at, say, `05:51:27.139`, stored as `20261004055127`) is covered by the watermark `05:51:27` and is not read again; compared at full precision it would be newer than its own watermark, reload on every run, and land rows the next flow already holds. Whole seconds need one rule more: every run of a file-date incremental flow, a `fullLoad` or backfill run included, is a settled-only read (the engine injects the source option `settledFilesOnly`, src/SqlFlow.Core/Engine/FlowRunner.cs `SettleFileDateReads`), which never loads a file whose modified second has not ended, because that would fix the watermark at that second and a file written later in the same second would never be newer than it. When the listing meets a file modified in the second the listing started in, the read waits for that second to end (at most one second) and lists again, so a file landed just before the run (an acquisition flow and the landing flow in one fire) is read by this run. Only a file modified after the run began listing, or dated ahead of the clock, waits for the next run, so a skewed clock cannot push the watermark past files still to arrive. A listing that meets no such file costs no wait. Files dated from their name or path are never held back, and a flow with a row-level `watermarkColumn` or no `incremental` block reads every listed file. When a run reads some files and leaves others, its trace says `N file(s) modified after this run began listing the source, or dated ahead of its clock, wait for the next run, ...`.

A run that selects no files is a clean success, not a failure, and it is reported as one throughout (src/SqlFlow.Core/Engine/FlowRunner.cs). The `source.columns` stage reaches a definite answer rather than failing, so it is traced as a succeeded stage at info level, not as an error: a no-op leaves no error row in the trace for an operator to chase down. This holds for every run of a file flow, not only an incremental one: a full load, a run with a backfill window, and a flow with no `incremental` block all end as a success that loaded zero rows. The selection is decided before the target is touched, so nothing is created, truncated, or reset by such a run.

What the run line says, and how loudly, depends on why the read came up empty (the `Reason` on `NoSourceFilesException`, see [file-source-pipeline](../concepts/file-source-pipeline.md)) and on the kind of run:

- An incremental run where files exist but none are newer than the watermark (`NoneAfterWatermark`, the normal resting state): info, `Flow '<name>': No new files under '<path>': nothing matching pattern '<glob>' is newer than <watermark> (examined N file(s)).` An incremental run whose declared init window or path mask excluded the candidates (`NoneSelected`) is reported at info for the same reason.
- An incremental run where the only new files were modified after it began listing the store, or are dated ahead of its clock (`NoneSettled`): info, `Flow '<name>': No files under '<path>' are ready to read yet (examined N file(s)): M file(s) matching pattern '<glob>' were modified after this run began listing the store, or carry a modified time ahead of its clock, so they wait for the next run (...)`. The next run reads them.
- The location holds no candidate file at all (`NoCandidates`), on any run: **warning**, `Flow '<name>': No files under '<path>' match pattern '<glob>'.` The run still succeeds with zero rows, but this is what a wrong path or pattern looks like, and a flow that quietly loads nothing forever is the failure mode the warning exists to catch.
- A full load or a backfill window that selects nothing (the watermark is suppressed for such a run), or a flow with no `incremental` block: **warning**, with the same message the selection produced. For a window that excluded every candidate the message names the dates the files carry and where they were read from, for example `... date window [2025-10-01 00:00:00 .. 2025-10-24 23:59:00]); examined 1 file(s). 1 file(s) outside the date bounds are dated 2026-10-02 18:42:47 UTC (the file's modified time).`

This is proven end to end by tests/SqlFlow.Core.Tests/Integration/IncrementalIntegrationTests.cs (first run seeds, an older file is a no-op that records no failed stage, a newer file loads only itself) and tests/SqlFlow.Core.Tests/Integration/IncrementalWindowIntegrationTests.cs; the reason classification is pinned by tests/SqlFlow.Core.Tests/CsvSourceReaderTests.cs, and the full-load, backfill-window and no-incremental no-ops by tests/SqlFlow.Core.Tests/EmptySourceRunTests.cs.

## Row-level mode mechanics

When `watermarkColumn` is set the engine instead probes:

```sql
SELECT MAX([watermarkColumn]) FROM <table>;
```

The target column's type resolves to a `WatermarkKind` (src/SqlFlow.Core/Model/Watermark.cs, src/SqlFlow.SqlServer/SqlServerIncrementalProbe.cs):

| Kind | SQL Server types | Compared as |
| --- | --- | --- |
| `Whole` | tinyint, smallint, int, bigint | long |
| `Fixed` | decimal, numeric, money, smallmoney | decimal |
| `Floating` | real, float | double |
| `DateTime` | date, datetime, datetime2, smalldatetime | DateTime |
| `DateTimeOffset` | datetimeoffset | DateTimeOffset |
| `Text` | char, varchar, nchar, nvarchar | string (ordinal) |

The overlap-applied bound rides on the source as three engine-injected options (src/SqlFlow.Core/Model/Watermark.cs, `WatermarkPredicate`):

- `incrementalColumn`: the watermark column name.
- `incrementalValue`: the culture-invariant canonical serialization of the bound.
- `incrementalKind`: the `WatermarkKind` name. An unknown name fails with `Unknown incremental watermark kind '<k>'.`

Filtering is layered so every source kind produces identical results:

- The engine wraps the streaming reader in `WatermarkFilteringDataReader` (src/SqlFlow.Core/Engine/WatermarkFilteringDataReader.cs), which drops every row not strictly past the bound. This applies to all readers (CSV, JSON, Parquet, DuckDB, relational).
- DuckDB additionally pushes the same predicate into its scan, rendered as `"col" > <literal>` with a double-quote-escaped column (src/SqlFlow.DuckDb/DuckDbQuery.cs). Date and text bounds render as single-quote-escaped literals (`TIMESTAMP '...'` for `DateTime`, `TIMESTAMPTZ '...'` for `DateTimeOffset`, a quoted string for `Text`); numeric bounds render bare. DuckDB then skips row groups below the bound instead of scanning the whole dataset.
- The Parquet reader prunes whole row groups whose recorded `MAX` statistic for the watermark column is at or below the bound (src/SqlFlow.Sources/ParquetSourceReader.cs). The statistic is decoded into the column's cell value first, and columns whose statistic does not order like their cells (unsigned integers, `TIME`, binary, sub-millisecond timestamps written by Parquet.Net) are never pruned; see [Parquet source](source-types/parquet.md). Pruning is best-effort: a missing, unreadable, or incomparable statistic reads the group and lets the row-level filter drop the old rows, so correctness never depends on statistics.

All comparisons are strictly greater-than the overlap-applied `MAX`. A `null` or `DBNull` source cell is never past the bound, so it is excluded and unknown values are not resurrected on a re-run. Both the pushdown and the client-side filter resolve the same comparison from the same canonical string, so they cannot disagree. See tests/SqlFlow.Core.Tests/Incremental/WatermarkPredicateTests.cs, tests/SqlFlow.Core.Tests/Incremental/WatermarkFilteringDataReaderTests.cs, and tests/SqlFlow.Core.Tests/Integration/RowWatermarkIntegrationTests.cs.

## Fuller examples

File-date mode probing a downstream table, with a two-day safety window:

```yaml
name: Orders_Bronze
source:
  type: parquet
  location: ./landing/orders
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Orders_Bronze
incremental:
  table: "[dw].[dbo].[Orders_Silver]"
  dateColumn: FileDate_DW
  overlapDays: 2
```

Row-level mode on a numeric commit version, re-reading a small overlap:

```yaml
name: Orders_RowWatermark
source:
  type: parquet
  location: ./landing/orders
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Orders
incremental:
  watermarkColumn: CommitVersion
  watermarkOverlap: 100
```

Forced full reload without removing the block:

```yaml
incremental:
  watermarkColumn: CommitVersion
  fullLoad: true
```

## See also

- [File discovery and lifecycle](../concepts/file-discovery-and-lifecycle.md): how `incrementalAfterDate` participates in file selection.
- [Incremental and backfill guide](../guides/incremental-and-backfill.md): task-oriented walkthrough of incremental loads and reloads.
- [Relational ingestion: schema and incremental](../flow/ing-schema-incremental.md): the ingestion flow's own `incremental` block, which has a different shape.
