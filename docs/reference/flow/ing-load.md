---
id: flow-ing-load
title: "Ingestion flow: load, matchKeys, change, systemColumns"
type: flow-reference
summary: "Keyed upsert (load), deleted-row detection (matchKeys), hash change detection (change), and the _DW audit columns (systemColumns) of an ing flow."
keywords:
  - keycolumns
  - upsert
  - batchupsert
  - matchkeys
  - hashcolumns
  - system columns
  - soft delete
  - datasetcolumn
yamlPath: "load / matchKeys / change / systemColumns (flowType: ing)"
related:
  - concept-upsert-and-change-detection
  - flow-ing
  - flow-ing-versioning
sourceRefs:
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs
  - src/SqlFlow.SqlServer/Schema/MatchKeyGenerator.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Schema/HashKey.cs
---

# Ingestion flow: load, matchKeys, change, systemColumns

These four sections of a `flowType: ing` document control how staged rows are applied to the target. `load` drives the keyed upsert (the engine stages the source, then runs an explicit UPDATE followed by an INSERT; it never emits a T-SQL MERGE). `matchKeys` detects target rows whose keys vanished from the source and tags or deletes them. `change` adds a fixed-length binary hash for row identity and change comparison. `systemColumns` toggles the injected `_DW` audit columns.

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
```

With `load.keyColumns` set, matched rows whose data changed are updated and new rows are inserted. Without keys, the flow appends everything.

## Keys reference

### load

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `keyColumns` | string list | no | `[]` | Business keys that match staged rows to target rows for the update/insert. Empty means append-only. |
| `skipUpdateExisting` | bool | no | `false` | Omit the UPDATE branch: insert-only. |
| `skipInsertNew` | bool | no | `false` | Omit the INSERT branch: update-only. |
| `matchKeysInSourceAndTarget` | bool | no | `false` | Run the key-match (deleted-row detection) pass after the load; configured by `matchKeys`. |
| `batchUpsert` | bool | no | `false` | Apply the upsert in key windows to avoid lock escalation. |
| `batchUpsertRowCount` | int | no | `2000` | Rows per window when `batchUpsert` is on. |
| `dataSetColumn` | string | no | none | Apply staging one dataset at a time, partitioned by this column. |
| `reloadColumn` | string | no | none | Per-file (per-dataset) full replace keyed on this column (typically `FileName_DW`): purge the target rows for the datasets in the incoming batch, then insert the batch. Supersedes the keyed upsert. |
| `streamData` | bool | no | `true` | Parsed and stored for legacy fidelity. The current engine always streams a live reader into the bulk copy and does not vary behavior on this flag. |
| `threads` | int | no | none | Concurrency cap for `initLoad` backfill segments only (default 1 when unset); a normal run always uses a single reader/writer pair. Values `<= 0` collapse to null. |
| `keepStagingTable` | bool | no | `false` | Keep the flow's canonical staging table after a successful run. |
| `truncateStagingOnCompletion` | bool | no | `false` | Truncate a kept staging table after a successful run. |
| `truncateSourceWhenConsolidated` | bool | no | `false` | After a successful load, truncate the upstream landing (`pre`) table feeding this flow's source, but only once the target's `MAX(watermark)` has caught up to the landing table's. Requires an incremental watermark and a SQL Server source. |

### matchKeys

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `action` | string | no | `tag` | `tag` (soft delete) or `delete` (hard delete). Case-insensitive. |
| `keyColumns` | string list | no | inherit `load.keyColumns` | Override of `load.keyColumns` when the pass is active; the whole apply (key index, upsert match, delete detection) then keys on these columns. |
| `thresholdPercent` | int | no | `20` | Skip the action when more than this percentage of the target would be affected. `0` to `100`. |
| `ignoreDeletedRowsAfterMonths` | int | no | none | Tag only rows whose `dateColumn` is within this many months; older rows are left alone. |
| `dateColumn` | string | no | falls back to `incremental.dateColumn` | Date column for the ignore window. |
| `sourceFilter` | string | no | the flow's `source.filter` | Predicate bounding the source key read; raw-append, carries its own leading `AND`. |
| `targetFilter` | string | no | none | Predicate bounding which target rows the pass may touch; raw-append, carries its own leading `AND`. |

### change

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `hashColumns` | string list | no | `[]` | Columns hashed into the row key. Non-empty enables the hash key. |
| `hashType` | string | no | engine default (`SHA2_256`) | Hash algorithm. Valid values: `SHA2_512`, `SHA2_256`, `SHA1`, `MD5` (also `MD2`, `MD4`, `SHA`). |
| `ignoreColumnsInHash` | string list | no | `[]` | Columns excluded from the hash. |

### systemColumns

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `insertedDate` | bool | no | `true` | Inject and maintain `InsertedDate_DW` (datetime2(3)). |
| `updatedDate` | bool | no | `true` | Inject and maintain `UpdatedDate_DW` (datetime2(3)). |
| `deletedDate` | bool | no | `false` | Inject `DeletedDate_DW` (datetime2(3)), the soft-delete marker. |
| `rowStatus` | bool | no | `false` | Inject and maintain `RowStatus_DW` (char(1)). |

## load details

### load.keyColumns

The business key the upsert matches on. The apply is an explicit staged UPDATE then INSERT, never a T-SQL MERGE (src/SqlFlow.Core/Ingestion/IngestionPolicies.cs). Without `keyColumns` the flow appends every staged row. SCD2 versioning requires `keyColumns`; the loader rejects `versioning.scd2` without them: `'versioning.scd2' requires 'load.keyColumns' (the business key the dimension versions by).`

### load.skipUpdateExisting and load.skipInsertNew

`skipUpdateExisting: true` drops the UPDATE branch (insert-only). `skipInsertNew: true` drops the INSERT branch (update-only). Both default false.

### load.matchKeysInSourceAndTarget

Enables the deleted-row detection pass described under [matchKeys details](#matchkeys-details). When this is `true` and `matchKeys.action` is `tag`, the loader auto-enables the `deletedDate` system column, so tag mode works without setting both flags.

### load.batchUpsert and load.batchUpsertRowCount

`batchUpsert: true` (model property `BatchUpsertToAvoidLockEscalation`) snapshots the matched keys with a `ROW_NUMBER`, indexes the windows, then updates and inserts window by window so each DML statement stays under the lock-escalation threshold. `batchUpsertRowCount` (default 2000) sets the window size. Within a dataset-partitioned load, batching is honored per dataset.

### load.dataSetColumn

When set, staging is applied to the target one dataset at a time, partitioned by this column's distinct values in ascending order; each dataset's UPDATE-then-INSERT runs against the target as the prior datasets left it. This preserves file/partition load order in a shared staging area: a business key that recurs across datasets collapses to the last dataset that carries it, which a single set-based upsert cannot express. Rows are deduplicated to one row per (dataset, key) before the loop. Not combinable with SCD2 versioning; the generator fails with `A dataset-column load (DataSetColumn) cannot be combined with SCD2 versioning.` The column must be among the data columns, otherwise: `DataSetColumn '<name>' is not among the data columns.`

Note that `source.dataSetColumn` is a separate key on the `source` section; the `load.dataSetColumn` key is the one that drives the dataset-partitioned apply.

### load.reloadColumn

Per-file (per-dataset) full replace, for the chained file-landing pattern where a file is the unit of data and a resent file must fully replace its prior version. When set, the apply is not a keyed upsert but a purge-then-insert scoped to the incoming batch:

1. Purge: `DELETE trg FROM [target] AS trg WHERE EXISTS (SELECT 1 FROM <staging> AS src WHERE src.[reloadColumn] = trg.[reloadColumn])`. The join is a plain equality, so it is NULL-safe: a target or staging row with no file identity is never matched, and files absent from this run's batch are untouched.
2. Insert the staged rows. When `load.keyColumns` are declared, the insert first collapses staging to one row per key so a key recurring across the batch's files cannot violate the target's unique key; without keys, every staged row is inserted.

The two statements always run in one transaction, so a resend is atomic (the old rows are gone and the new ones in, or neither). Purged rows are reported as `RowsDeleted`.

Use `FileName_DW` as the reload column, and set the upstream pre flow's `showPathWithFileName` so `FileName_DW` carries the full path, the collision-free identity (two files with the same name in different folders stay distinct). The provenance columns ride through the `[pre].[v<Table>]` view onto this ods target, so the reload column is a real target column here. Combined with an incremental watermark on `FileDate_DW`, only the resent file (its rows carry a newer file date) is read into staging, so only that file is purged and reloaded; the other landed files are never touched. This is the crucial difference from a keyed upsert, which would leave behind records that the new version of the file dropped.

On the run that creates the target, an `NCI_ReloadColumn` nonclustered index is added so the purge seeks (skipped when the column is already the leading column of the key, date, or dataset index).

`reloadColumn` supersedes the keyed upsert and is therefore rejected in combination with `load.dataSetColumn` (the ordered dataset-upsert loop), `versioning.scd2`, `load.matchKeysInSourceAndTarget`, and `target.truncateBeforeLoad`, each with a specific validation message. It does not require `load.keyColumns` (the file is the unit of replacement). At run time, a `reloadColumn` that is not a bulk-copied target column fails with `ReloadColumn '<name>' is not among the data columns.`

```yaml
source:
  server: dwpre
  object: dw-pre-prod.pre.vBoatbooking_sess   # the typed view; carries FileName_DW (full path)
target:
  server: dwh
  object: dw-dwh-prod.arc.Boatbooking_sess
load:
  reloadColumn: FileName_DW
  keyColumns: [SESS_ID]        # optional: dedups within a file
incremental:
  columns: [FileDate_DW]       # only the resent file is read into staging
  overlapDays: 0
```

### load.streamData and load.threads

`streamData` is parsed and stored (default `true`), but the current engine does not branch on it: staging is always populated by streaming a live reader straight into the bulk copy (`SqlBulkCopy` with `EnableStreaming = true`), regardless of the flag's value (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs). `threads` has one effect: when `initLoad.enabled: true`, the chunked backfill segments fan out concurrently, capped at `threads` (default 1 when unset), each segment opening its own source and target connection and streaming into the same staging table. A normal run without `initLoad` always uses a single reader/writer pair, so `threads` has no effect there. `threads` values `<= 0` are treated the same as unset.

### load.keepStagingTable and load.truncateStagingOnCompletion

By default the flow's canonical staging table (and the match-keys key table, when present) is dropped after a successful run. A failed run always keeps the staging table for debugging, regardless of this flag. `keepStagingTable: true` keeps it on success too; `truncateStagingOnCompletion: true` (model property `TruncatePreTableOnCompletion`) then empties the kept table after a successful load so it carries structure without the run's data. When the table is not kept, the flag has no effect: dropping already discards the data. A kept table (or one left by a failure) is reset by the next run's rebuild, so a flow never owns more than one staging table.

This governs the flow's staging table (`[raw].[<targetSchema>_<targetTable>_<flowId>]`) only. It is unrelated to `truncateSourceWhenConsolidated` below, which governs the upstream landing table.

### load.truncateSourceWhenConsolidated

For the chained landing pattern `file -> [pre].[<Table>] -> view [pre].[v<Table>] -> target`, the landing (`pre`) table is written by a file flow and read by this ingestion flow through the typed view. The default cleanup lives on the WRITER: the file flow's `load.resetWhenConsolidated` (on by default, see docs/reference/flow/load.md) truncates the landing table at the start of its next run once every direct consumer has consolidated it. This ingestion-side flag is the explicit consumer-side alternative for the same table. Setting `truncateSourceWhenConsolidated: true` reclaims it safely: after a successful load, the engine compares `MAX(watermark)` in the landing table against `MAX(watermark)` in the target and truncates the landing table only when the target has caught up (target mark `>=` landing mark). The target-side probe is scoped by `source.incrementalClause` when the flow declares one, so on a shared target (several operators merging into one arc table) another operator's fresher load can never fake the catch-up. The watermark is the first `incremental.columns` entry, else `incremental.dateColumn`, and must exist under the same name on both sides (the clean `FileDate_DW` system column and its siblings do). The landing table is the source object with a leading `v_` stripped; a source that is already a base table is truncated as-is.

This is the safe alternative to `target.truncateBeforeLoad` on the landing flow: it never removes un-consolidated data. A failed run never truncates (the step is on the success path, after the load commits); an empty landing table is a no-op; and a target that has not caught up leaves the landing rows in place, so the next run re-consolidates them rather than losing them. The flag requires an incremental watermark and a SQL Server source; both are checked at run start, so a misconfigured flow fails immediately rather than after a committed load:

```text
load.truncateSourceWhenConsolidated requires an incremental watermark (incremental.columns or incremental.dateColumn) ...
load.truncateSourceWhenConsolidated is supported only for SQL Server sources ...
```

## matchKeys details

Active only when `load.matchKeysInSourceAndTarget: true`. After the load, the engine lands the full distinct source key set in the flow's canonical `mkey_` table (in the `raw` schema, rebuilt per run) and anti-joins the target against it: a target row whose key no longer exists in the source is tagged or deleted. The key read is bounded only by the static source filter, never the incremental window, so a row deleted outside the window is still detected. The key comparison is NULL-safe: a NULL key matches a NULL key. One script runs and reports one row of counters (total, candidates, affected, resurrected, threshold breached).

### matchKeys.action

`tag` (default) soft-deletes by stamping `DeletedDate_DW = SYSUTCDATETIME()`, and `RowStatus_DW = 'D'` when the row-status column is on. `delete` hard-deletes the rows. Any other value fails validation: `'matchKeys.action' must be 'tag' or 'delete', got '<value>'.` Tag mode requires the `DeletedDate_DW` column; the loader auto-enables it (see `systemColumns.deletedDate`).

Tag mode also reconciles resurrections: a tagged row whose key reappears in the source is un-tagged (`DeletedDate_DW` set back to NULL, `RowStatus_DW` reset to `'U'` when present), independent of the threshold, since un-deleting is always safe.

### matchKeys.keyColumns

Overrides `load.keyColumns` for the match comparison; empty inherits them. When overridden, the target's unique index, the upsert match, and the delete detection all key on the override columns.

### matchKeys.thresholdPercent

A value outside 0 to 100 fails validation: `'matchKeys.thresholdPercent' must be 0 to 100, got <n>.` At run time, when more than this percentage of the target would be affected, the action is skipped and a warning is logged (`WARNING: action SKIPPED ...` with the candidate count and percentage); a mass disappearance is more often a broken source read than a real mass delete. Resurrection still runs.

### matchKeys.ignoreDeletedRowsAfterMonths and matchKeys.dateColumn

Tag mode only: tag a row only when its `dateColumn` value is within the last N months (`>= DATEADD(MONTH, -N, SYSUTCDATETIME())`); older rows are left alone. Setting `ignoreDeletedRowsAfterMonths` without `dateColumn` fails at load: `'matchKeys.ignoreDeletedRowsAfterMonths' requires 'matchKeys.dateColumn'.` At run time `dateColumn` falls back to `incremental.dateColumn`; with neither set the run fails: `matchKeys.ignoreDeletedRowsAfterMonths requires matchKeys.dateColumn (or an incremental dateColumn).`

### matchKeys.sourceFilter and matchKeys.targetFilter

Raw-append predicates: each carries its own leading `AND` (for example `"AND Status = 'open'"`). `sourceFilter` bounds the source key read and defaults to the flow's `source.filter`, so rows the load never reads are not treated as deleted. `targetFilter` bounds which target rows the pass (including resurrection) may touch.

## change details

A fixed-length binary hash over `hashColumns` identifies and compares rows when there is no usable business key. The hash key is active when `hashColumns` is non-empty (model property `HasHashKey`). `hashType: null` (unset) means the engine default, `SHA2_256`; the column type is sized to the algorithm (`SHA2_512` binary(64), `SHA2_256` binary(32), `SHA1` binary(20), `MD5` binary(16)). An unknown algorithm fails fast: `Unknown hash algorithm '<v>'. Valid values: SHA2_512, SHA2_256, SHA1, MD5 (also MD2, MD4, SHA).` `ignoreColumnsInHash` lists columns excluded from the hash input.

## systemColumns details

The four `_DW` audit columns are injected into the desired schema when enabled:

| YAML key | Column | Type | Default |
| --- | --- | --- | --- |
| `insertedDate` | `InsertedDate_DW` | datetime2(3) | on |
| `updatedDate` | `UpdatedDate_DW` | datetime2(3) | on |
| `deletedDate` | `DeletedDate_DW` | datetime2(3) | off |
| `rowStatus` | `RowStatus_DW` | char(1) | off |

Behavior, from src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs and src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs:

- All system columns are created nullable, so an `ALTER TABLE ... ADD` onto an already-populated target succeeds. They are computed by the engine (never bulk-copied) and always sort last in the column order.
- An existing column with the same name is not injected twice (case-insensitive existence check).
- `InsertedDate_DW` is stamped `SYSUTCDATETIME()` on insert. On update, a matched row whose `InsertedDate_DW` is NULL (it predates the column) is stamped the first time it is touched; an existing value is preserved.
- `UpdatedDate_DW` is stamped `SYSUTCDATETIME()` on update. The SCD2 close of a current row also stamps it, using the run's fixed as-of instant so every row closed in the run carries the same timestamp.
- `RowStatus_DW` values written by the engine: `'I'` on insert, `'U'` on update (including SCD2 close and match-keys resurrection), `'D'` on a match-keys tag.
- `deletedDate` is auto-enabled when `load.matchKeysInSourceAndTarget: true` with `matchKeys.action: tag`.

## Full example

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "AND Status <> 'draft'"          # raw-append: carries its own leading AND

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]
  matchKeysInSourceAndTarget: true
  batchUpsert: true
  batchUpsertRowCount: 5000
  keepStagingTable: true
  truncateStagingOnCompletion: true

matchKeys:
  action: tag                          # soft delete; auto-enables DeletedDate_DW
  thresholdPercent: 20                 # skip the action if more than 20% would be affected
  ignoreDeletedRowsAfterMonths: 6
  dateColumn: OrderDate
  targetFilter: "AND Region = 'NA'"

change:
  hashColumns: [OrderID, CustomerID, Amount]
  ignoreColumnsInHash: [LoadComment]

systemColumns:
  insertedDate: true
  updatedDate: true
  rowStatus: true
```

Validate and run:

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

## See also

- [Ingestion flow overview](ing.md)
- [Ingestion flow: versioning (SCD2)](ing-versioning.md)
- [Upsert and change detection](../concepts/upsert-and-change-detection.md)
