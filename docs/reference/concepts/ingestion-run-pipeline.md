---
id: concept-ingestion-run-pipeline
title: "Ingestion run pipeline internals: staging, source SELECT, windows, InitLoad"
type: concept
summary: How IngestionFlowRunner executes a flowType ing run, from source SELECT and staging through incremental windows, InitLoad chunks, and the SQL trace.
keywords:
  - staging table
  - run pipeline
  - where 1=1
  - incremental window
  - initload chunks
  - schema builder
  - dialects
  - sql trace
related:
  - flow-ing
  - flow-ing-schema-incremental
  - concept-upsert-and-change-detection
  - concept-schema-evolution
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs
  - src/SqlFlow.SqlServer/Ingestion/ChunkRanges.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.SqlServer/Schema/UnicodeConverter.cs
  - src/SqlFlow.Core/Ingestion/DefaultColumnNameCleaner.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
  - src/SqlFlow.Core/Ingestion/SqlTrace.cs
---

# Ingestion run pipeline internals: staging, source SELECT, windows, InitLoad

`IngestionFlowRunner` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs) executes one relational ingestion flow (`flowType: ing`) end to end: it resolves the source and target connections through the registry, introspects and shapes the source columns, rebuilds the flow's canonical staging table on the target (in the `raw` schema), streams the source into it with `SqlBulkCopy`, evolves the target schema, and applies staging to the target with the keyed two-step upsert (or an insert-all when there is no key). There is a single execution path: file flows use `FlowRunner`, relational flows use this runner, and both share the schema-evolution and upsert machinery.

This page covers the run's internal order of operations, how the source SELECT is built, how the incremental window and InitLoad chunk plans shape that SELECT, and the observability contract (the SQL trace and the run result).

## Order of operations

One run of `IngestionFlowRunner.RunAsync` proceeds in this order. Every step that generates SQL captures it into the run's trace (see below).

1. **Feature guard**: `versioning.insertUnknownDimensionRow` is rejected up front with an explicit "not yet implemented" error instead of being silently ignored, as are two contradictory combinations: `versioning.temporal` with `target.truncateBeforeLoad` (SQL Server does not allow TRUNCATE on a system-versioned table) and `versioning.temporal` with `versioning.scd2` (two history mechanisms would record every change twice).
2. **Pre-invoke**: when `preInvoke` names an invoke flow, it runs before any data work; a failure fails the run.
3. **Source introspection and shaping**: the source object's columns are read through the source catalog reader; `source.ignoreColumns` are removed; the source's identity and primary-key facts are cleared so staging and the target never inherit them. Zero remaining columns fails with `Source object <name> exposes no columns to ingest.`
4. **Desired-schema build**: `IngestionSchemaBuilder` produces the desired target schema and the bulk-copy name map (see below).
5. **Target pre-process hook**: `preProcess` raw T-SQL runs on the target before any new data is staged. On a first-ever run the target does not exist yet, so a pre-hook that touches it must guard itself with `IF OBJECT_ID(...) IS NOT NULL`.
6. **Rebuild canonical staging**: the `raw` schema is ensured, the flow's canonical staging table is dropped if a prior incarnation exists, and a fresh table holding only the bulk-copied data columns is created, so the run always stages into an empty table with its exact source shape.
7. **Fill staging**: either one incremental-windowed source read, or (when `initLoad.enabled`) a fan-out of chunked segment reads. Both paths feed the same staging table, so everything after this step is identical.
8. **Evolve the target** (when `schema.sync` is on, the default): the target is created or altered to the desired schema.
9. **Canonical indexes**: only on the run that created the target, the canonical indexes (key, date, dataset, `UpdatedDate_DW`, optional clustered columnstore) are created on the still-empty table.
10. **SCD2 key-index reconciliation**: whenever SCD2 is enabled (any table state), the business-key index is migrated to its filtered-unique form; the statements are guarded, so this is a no-op once in place.
11. **Temporal history** (when `versioning.temporal` is on): the target is brought to its declared system-versioning state. It runs after the evolve and the index steps, because SQL Server refuses to version a table with no PRIMARY KEY (which the create step supplies) and because adding the `SYSTEM_TIME` period last keeps the period columns out of the schema the evolution planner diffs. Depending on the live state this adds the period and turns versioning on, only turns it on (over a period that survived a manual disable), re-applies a changed retention period, or does nothing. The statements are ordered and each commits on its own, so an interrupted transition resumes on the next run. Ordinary schema evolution keeps working on a versioned target: SQL Server supports ADD, ALTER and DROP COLUMN while versioning is on and propagates each to the history table, so the engine never toggles versioning off.
13. **Optional truncate**: `target.truncateBeforeLoad` issues `TRUNCATE TABLE` on the target.
12. **Apply**: the keyed two-step upsert (UPDATE then anti-join INSERT), the SCD2 close-and-insert, the dataset-partitioned loop, or the keyless insert-all. The default apply wraps the statements in one transaction; the batched apply (`load.batchUpsert: true`) deliberately runs without an enclosing transaction so each key window commits and releases its locks on its own.
14. **Post-process hook**: `postProcess` raw T-SQL runs on the target after the load commits, outside the load transaction, and before staging is dropped.
15. **Surrogate keys**: declared `surrogateKeys` are generated and stamped back; a per-spec failure is surfaced on the result, never a rollback of the committed load.
16. **Match-keys pass**: when `load.matchKeysInSourceAndTarget` is on, the full distinct source key set (never bounded by the incremental window) is landed in the flow's canonical `mkey_` key table (in the `raw` schema, rebuilt per run like staging) and target rows whose keys vanished are tagged or deleted, subject to the threshold guard.
17. **Desired indexes**: only on the run that created the target, the declared `target.desiredIndexes` script is applied; a failure is surfaced as a failed `IndexAction`, never a rollback.
18. **Assertions**: declared data-quality assertions run against the loaded target; they are log-only and never affect `Success`.
19. **Post-invoke**: `postInvoke` runs after the load commits and before staging is dropped; a failure reports the run as failed but the committed load stands.
20. **Drop or keep staging**: see the staging lifecycle below.
21. **Transform view**: when the transform policy generates a view, `[schema].[v_<Table>]` is refreshed over the target with `CREATE OR ALTER`; a failure here fails the run (a stale view must be loud) but the committed load stands.
22. **Consolidation-gated landing truncate** (`load.truncateSourceWhenConsolidated`): empties the upstream `[pre]` landing table that feeds the source, but only once the target has caught up. The prerequisites are validated up front at run start (an incremental watermark, `incremental.columns` or `incremental.dateColumn`, and a SQL Server source), so a misconfigured flow fails before any data work. On the success path it compares `MAX(watermark)` on both sides (the target-side probe scoped by `source.incrementalClause` when declared, so a shared target's other writers cannot fake the catch-up) and issues `TRUNCATE TABLE` on the landing table only when the target's mark is at least the landing table's; a target that has not consolidated the landed rows keeps them (nothing is lost). It runs after the load commits and after the transform-view refresh. The default cleanup for the same landing table lives on its WRITER: the file flow's `load.resetWhenConsolidated` (on by default) resets it at the start of that flow's next run once every direct consumer has consolidated, making this ingestion-side flag the explicit consumer-side alternative.
23. **Run record**: the run record (rows, durations, the generated SELECT/INSERT/UPDATE/CREATE statements, the rendered trace) is written to the run log. In without-database (pure YAML) mode the log is a no-op.

A failure at any point keeps the staging table, writes a failure run record on a best-effort basis, and returns a failed result carrying the trace captured up to the failure point.

## The staging table

The staging table is canonical per flow, not per execution: it lives in the `raw` schema (which by itself marks it as staging, so the name carries no prefix) and is named `{targetSchema}_{targetTable}_{flowId}`, e.g. `[raw].[arc_Orders_279975153]`. The name is traceable at a glance to the target it feeds, and because every run rebuilds the same object, executions never accumulate per-run copies in the database. Characters outside `A-Z a-z 0-9 _` in the target identifiers fold to `_`, and the traceable middle is trimmed if the composed name would exceed SQL Server's 128-character identifier cap (the flow-id suffix, which guarantees uniqueness, is never trimmed). The run queue serializes executions of the same flow (see the claim's pipeline gate in `RunQueueStore`), so two runs never contend for the table.

Lifecycle:

- The `raw` schema is ensured and any prior incarnation is dropped at run start, then the table is created fresh with the run's exact source shape.
- Dropped on success by default (`DROP TABLE IF EXISTS`).
- `load.keepStagingTable: true` keeps it after a successful run; the next run's rebuild resets it.
- A FAILED run always keeps it for debugging, regardless of the flag; the next run's rebuild resets it.
- `load.truncateStagingOnCompletion: true` empties a KEPT staging table after success, so it carries structure without the run's data. It has no effect when staging is dropped (the drop is the stronger cleanup).

The bulk copy into staging uses `SqlBulkCopy` with `TableLock`, `BatchSize = 0` (one batch, the minimally-logged path into the heap), `BulkCopyTimeout = 0`, and `EnableStreaming = true`. The bulk update lock taken under `TableLock` on a heap is mutually compatible, which is what lets InitLoad's parallel segment streams load the same staging table concurrently.

## Source SELECT construction and dialects

The source read is always:

```sql
SELECT <quoted columns> FROM <qualified object> WHERE 1=1<sourceWhere>
```

The `WHERE 1=1` base exists so that fragments compose by raw append (the legacy contract): the incremental predicate and the user filter each carry their own leading ` AND ` (or, for `source.filter`, their own leading keyword) and are concatenated onto it.

- `source.filter` is raw SQL carrying its own leading keyword, for example `"AND SystemID = 13"`. With the default `filterIsAppend: true` it is concatenated after the computed predicates. With `filterIsAppend: false` it becomes a replace-filter that wins over every computed predicate, including a per-run backfill window.
- Identifier quoting, object qualification, temporal and binary literal formatting, and date arithmetic on the source all come from the `ISourceSqlDialect` matched to the resolved connection's kind. SQL Server is built in; MySQL, PostgreSQL, and Oracle dialects register via the `SqlFlow.Providers` package.
- A source kind with no registered dialect fails with: `No source SQL dialect is registered for data source kind '<kind>'. Register the matching provider (for example from SqlFlow.Providers).` A missing type mapper fails with the analogous type-mapper message.
- Source introspection runs on a connection already opened against the source database, so it uses the current-database `OBJECT_ID` path (no database switch). A missing source object fails with: `Source object <name> was not found.`

## Desired-schema construction

`IngestionSchemaBuilder.Build` (src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs) shapes the introspected source columns into the desired staging and target schemas. One builder serves both passes; `forStaging` toggles only the identity injection.

- **Name cleanup** (`schema.cleanColumnNames`): `DefaultColumnNameCleaner` applies a remove-invalid-characters regex (the per-flow `schema.cleanColumnNameRegex`, or the legacy shipped default when blank), replacing matches with `schema.replaceInvalidCharsWith` (or removing them when blank). An empty result becomes `EmptyColumnName`; collisions are de-duplicated with a numeric suffix. The regex runs with a 2-second timeout, and an invalid pattern fails with `Invalid CleanColumnNameRegex '<pattern>': ...`.
- **Unicode conversion** (`schema.convertUnicodeToNonUnicode`): `UnicodeConverter` maps `nvarchar` to `varchar` and `nchar` to `char` (both preserve the declared length), `ntext` to `text` (the length is dropped), and `sysname` to `varchar(128)` (a fixed length, not the source declaration). Other types pass through.
- **Virtual columns**: a `virtualColumns` entry matching a source column name marks it computed with its declared expression, so it is projected, not bulk-copied.
- **System, SCD2, and hash columns** are injected as computed columns (`InsertedDate_DW`, `UpdatedDate_DW`, `DeletedDate_DW`, `RowStatus_DW`, the SCD2 period columns, the hash key).
- **Identity** (`target.identityColumn`): injected on the target pass only, typed `int`, rendered as `IDENTITY(1, 1)` and `NOT NULL`, and marked as the primary key.
- **Column order** is legacy-normalized: names starting with `PK` first, names ending with `PK` next, ordinary columns in source order, then every `*_DW` column last (the `_DW` rule wins even over a PK-shaped name).
- The builder's `SourceToTargetNames` map drives the `SqlBulkCopy` column mappings. Virtual, system, hash, and identity columns are computed, so they are absent from it. A key column that is not in the map (because it is ignored or virtual) fails fast: `Key column '<name>' is not a bulk-copied target column (is it in IgnoreColumns or a virtual column?).`

## Incremental window resolution

When `initLoad` is off, `IncrementalWindowResolver` (src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs) computes the `sourceWhere` fragment for the single read.

For an incremental flow (`incremental.columns` or `incremental.dateColumn` set) it probes `SELECT MAX([col]) ... FROM <target>` on the TARGET, with `DATEADD(day, -<overlapDays>, MAX([dateColumn]))` for the date column and `MAX([col]) - <lookback>` for a numeric one, then builds the fragment. Predicate precedence matches the legacy engine exactly:

1. A replace-filter (`source.filter` with `filterIsAppend: false`) wins over everything.
2. The `incremental.fullLoad` flag yields an empty fragment (full read).
3. An absent or empty target yields an empty fragment (full load).
4. Otherwise the incremental-columns predicate applies; when it is empty, the date predicate applies.
5. An append-filter is concatenated last in every case.

Details:

- `incremental.columns` build strict `>` predicates against the target MAX values, rewound by `incremental.lookback` (default 0, so by default the bare MAX) when the column's type is one arithmetic applies to; `incremental.dateColumn` has `incremental.overlapDays` (default 7) subtracted from its MAX before comparison. Each shift applies only to its own kind of mark.
- `incremental.fetchMinValuesFromSource: true` additionally probes MIN on the SOURCE (with the same overlap subtraction through the source dialect's date arithmetic, and the same numeric lookback subtraction, so both sides shift equally). When the source minimum is below the target maximum, the window widens back to the source minimum and the operator becomes `>=` (reprocess history).
- `source.incrementalClause` is appended verbatim to both probe queries' `WHERE 1=1` (the legacy hard-coded probe predicate).
- Watermark literals are typed from the introspected source column types: integers and decimals render raw, `bit` renders `1`/`0`, binary types go through the dialect's binary literal, date types through the dialect's temporal literal with `yyyy-MM-dd HH:mm:ss.fff` (date-only as `yyyy-MM-dd`, `datetimeoffset` with a zone offset), and strings are quoted with `''` escaping.
- An incremental column missing from the source columns fails: `Incremental column '<col>' is not among the source columns.`
- An absent or empty target means a full load. A keyless full read takes the insert-all apply path (`RunFullLoad: true`); a keyed full read still goes through the upsert, whose INSERT is an anti-join, so a full reload is idempotent.

### Per-run parameters (backfill)

Trigger-time `RunParameters` override the probe entirely; the CLI surface is `sqlflow run <file> [--full] [--from <date>] [--to <date>]` (src/SqlFlow.Cli/Program.cs):

- `--full` skips the probe and forces a full read; keyed flows still upsert (idempotent reload), and only a keyless full read is the insert-all path.
- `--from`/`--to` become `>= from AND < to` bounds on `incremental.dateColumn`. The flow must declare that column (`A backfill window needs incremental.dateColumn on the flow, so the engine knows which column to bound.`) and its source type must be a date type (`A backfill window bounds incremental.dateColumn '<col>', but its source type is '<type>', not a date type.`). The bounds are rendered through the source dialect's temporal literal.

## InitLoad: chunked one-time backfill

`initLoad.enabled: true` replaces the single windowed read with a chunk plan from `InitLoadPlanner` (src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs), using the shared range math in `ChunkRanges`:

- `batchBy: M` produces calendar-aligned month windows (snap to month-end, first and last clamped), `batchSize` months per chunk.
- `batchBy: D` produces rolling fixed-width day windows of `batchSize` days.
- `batchBy: K` produces inclusive integer key buckets `[lo, hi]` of `batchSize` values from 0 up to `keyMaxValue`, on `initLoad.keyColumn`.
- An unknown unit returns zero segments and streams nothing (the legacy behavior; no error is thrown).

Defaults when bounds are unset: `fromDate` is today minus 3 years, `toDate` is today, `batchSize` is 1, `keyMaxValue` is 10,000,000.

Each date segment is a half-open interval `[start, end + 1 day)` rendered as `(col >= 'yyyy-MM-dd' AND col < 'yyyy-MM-dd')`; key segments are inclusive. A trailing `(col IS NULL)` segment is always appended so rows with a NULL date or key are not dropped. Every segment SELECT shares the same prefix: the quoted column list, the qualified source object, `WHERE 1=1`, and the raw-appended `source.filter`.

The date column is `incremental.dateColumn`; there is no separate InitLoad date column. Missing prerequisites fail fast: `InitLoad by date requires Incremental.DateColumn to be set.` and `InitLoad by key requires InitLoad.KeyColumn to be set.`

Segments fan out concurrently under `load.threads` (default 1), each with its own source and target connection, all streaming with `SqlBulkCopy` `TableLock` into the same run-private heap staging table. The staged count is the exact sum of `SqlBulkCopy.RowsCopied` across segments. InitLoad ignores the incremental watermark entirely (the window is `RunFullLoad: true` with an empty `sourceWhere`), and a trigger-time `--from`/`--to` re-windows the chunk plan for that run only, so one InitLoad definition serves any historical slice without a YAML edit.

## Observability: the SQL trace and the run result

Every SQL statement the run generates is captured as a `SqlTraceEntry` (`Sequence`, `Step`, `Sql`; src/SqlFlow.Core/Ingestion/SqlTrace.cs) in execution order, on success and on failure (the failure case captures everything up to the failure point). Each captured statement is also emitted to the run event sink at Trace level, so one call site feeds both the trace artifact and the run timeline. `SqlTrace.Render` produces the readable `-- [n] step` text stored on the run record.

`IngestionRunResult` carries: `RowsStaged`, `RowsInserted`, `RowsUpdated`, `RowsDeleted`, `FlowRate` (rows per second, 0 for a sub-second run), `StagingTable`, `StagingRetained`, `SourceWhere` (the fragment appended after `WHERE 1=1`), `RunFullLoad`, `IndexActions`, `Assertions`, `SurrogateKeys`, `SqlTrace`, `TransformView`, and `Error` (null on success).

## Configuration touchpoints

| Surface | Controls |
| --- | --- |
| `source.filter`, `source.filterIsAppend`, `source.incrementalClause`, `source.ignoreColumns` | Source SELECT shaping and the probe predicate |
| `incremental.columns`, `incremental.dateColumn`, `incremental.overlapDays`, `incremental.lookback`, `incremental.fullLoad`, `incremental.fetchMinValuesFromSource` | The incremental window |
| `initLoad.enabled`, `initLoad.fromDate`, `initLoad.toDate`, `initLoad.batchBy`, `initLoad.batchSize`, `initLoad.keyColumn`, `initLoad.keyMaxValue` | The chunked backfill plan |
| `load.keyColumns`, `load.batchUpsert`, `load.batchUpsertRowCount`, `load.threads`, `load.keepStagingTable`, `load.truncateStagingOnCompletion` | The apply and the staging lifecycle |
| `load.truncateSourceWhenConsolidated` | The consolidation-gated truncate of the upstream `[pre]` landing table once the target's `MAX(watermark)` has caught up |
| `schema.sync`, `schema.cleanColumnNames`, `schema.cleanColumnNameRegex`, `schema.replaceInvalidCharsWith`, `schema.convertUnicodeToNonUnicode`, `schema.allowTableRewrite` | Desired-schema construction and evolution |
| `target.identityColumn`, `target.truncateBeforeLoad`, `target.desiredIndexes`, `target.columnStoreIndex` | Target shaping and create-run indexes |
| `sqlflow run <file> --full --from <date> --to <date>` | Per-run window and chunk-plan overrides |

## Example

An incremental ingestion with a one-time chunked backfill window, adapted from samples/ingestion/orders-ingestion.flow.yaml:

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "AND Status <> 'draft'"      # raw-append: carries its own leading AND

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]
  keepStagingTable: true
  truncateStagingOnCompletion: true    # kept staging is emptied after success

incremental:
  columns: [ModifiedDate]
  dateColumn: OrderDate
  overlapDays: 7
  lookback: 0                          # numeric counterpart of overlapDays, in key units

initLoad:
  enabled: true                        # one-time chunked backfill (disable after it runs)
  fromDate: 2020-01-01
  toDate: 2024-12-31
  batchBy: M
  batchSize: 1
```

Run it, or re-window the plan for one run without editing the file:

```bash
sqlflow run orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml --from 2022-01-01 --to 2022-07-01
```

With `initLoad.enabled` removed, the same flow reads incrementally. The engine probes both `MAX([ModifiedDate])` and `DATEADD(day, -7, MAX([OrderDate]))` on the target, but per precedence rule 4 above the `incremental.columns` predicate wins outright and the date predicate never surfaces (`OrderDate` is declared as `incremental.dateColumn` only because `initLoad.batchBy: M` requires it). The source read looks like:

```sql
SELECT [OrderID], [OrderDate], [ModifiedDate], ... FROM [Sales].[Orders]
WHERE 1=1 AND [ModifiedDate] > '2026-06-25 08:14:02.123' AND Status <> 'draft'
```

## See also

- [flowType: ing reference](../flow/ing.md)
- [Ingestion schema sync and incremental keys](../flow/ing-schema-incremental.md)
- [Upsert and change detection](./upsert-and-change-detection.md)
- [Schema evolution](./schema-evolution.md)
