---
id: concept-upsert-and-change-detection
title: "The two-step upsert, batching, hash change detection, and deleted-row matching"
type: concept
summary: "How ing flows apply staging to the target: UPDATE then INSERT (never MERGE), HASHBYTES change detection, lock-friendly batching, and matchKeys deletes."
keywords:
  - upsert
  - no merge
  - hashbytes
  - batchupsert
  - lock escalation
  - datasetcolumn
  - matchkeys
  - soft delete
related:
  - flow-ing-load
  - flow-ing-versioning
  - flow-ing
  - concept-ingestion-run-pipeline
sourceRefs:
  - src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs
  - src/SqlFlow.SqlServer/Schema/MatchKeyGenerator.cs
  - src/SqlFlow.SqlServer/Schema/HashKey.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
---

# The two-step upsert, batching, hash change detection, and deleted-row matching

An ingestion flow (`flowType: ing`) never writes source rows straight into the target. It stages them in the flow's canonical staging table (`[raw].[<targetSchema>_<targetTable>_<flowId>]`, rebuilt per run), then applies staging to the target with an explicit two-step upsert: an UPDATE of matched rows whose data changed, followed by an INSERT of rows that do not yet exist. The engine deliberately never emits a T-SQL `MERGE` (`src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs` documents the choice: MERGE has known concurrency and trigger hazards, and the legacy engine avoided it too). Everything on this page is pure text generation in `UpsertGenerator` and `MatchKeyGenerator`, orchestrated by `src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs`.

A keyed flow (non-empty `load.keyColumns`, or `matchKeys.keyColumns` when the key-match pass is on and declares its own keys) always goes through the upsert. A keyless flow appends every staged row with a single INSERT (append-only by nature). The upsert's INSERT is an anti-join:

```sql
INSERT INTO [dbo].[Orders] (...)
SELECT src.[OrderID], src.[Status], ...
FROM (
  SELECT [OrderID], [Status], ...,
         ROW_NUMBER() OVER (PARTITION BY [OrderID] ORDER BY (SELECT NULL)) AS _rn
  FROM [raw].[dbo_Orders_279975153]
) AS src
WHERE src._rn = 1
  AND NOT EXISTS (SELECT 1 FROM [dbo].[Orders] AS trg WHERE src.[OrderID] = trg.[OrderID]);
```

Because new rows are detected against the live target rather than a watermark, a full reload over a NULL watermark is idempotent for keyed targets. This deliberately fixes the legacy full-load branch, which did a blind insert and duplicated rows.

## The UPDATE branch and change detection

The UPDATE joins staging to the target on key equality and only rewrites rows whose non-key data actually changed. Change is detected by comparing a `HASHBYTES` checksum of the comparable non-key columns on each side:

```sql
HASHBYTES('SHA2_256', CONCAT(N'', src.[Status], N'|', src.[Amount])) <>
HASHBYTES('SHA2_256', CONCAT(N'', trg.[Status], N'|', trg.[Amount]))
```

Two details of the `CONCAT` shape are load-bearing (`UpsertGenerator.Checksum`):

- A leading `N''` guards the single-column case, since `CONCAT` requires at least two arguments.
- Values are interleaved with `N'|'` separators so two distinct rows cannot collide on concatenation (for example `('ab','c')` vs `('a','bc')`).

Columns are excluded from the checksum in two ways (`IngestionFlowRunner.BuildLoadStatements`):

- Automatically, when the column's type cannot participate in `CONCAT`: `xml`, `geography`, `geometry`, `hierarchyid`, `image`, `text`, `ntext`, `varbinary`, `binary`, `rowversion`, `timestamp`, `sql_variant` (the `NonChecksumTypes` set). Such a column would raise "Argument data type ... is invalid" at run time.
- Explicitly, via `change.ignoreColumnsInHash` (source column names, mapped to target names).

Excluded columns are still copied by the UPDATE's SET list; they just do not count as "changed". When every comparable column is excluded, the UPDATE drops its change predicate entirely and rewrites all matched rows (legacy semantics: better to over-update than never update). The exclusion list is logged at debug level as `upsert.plan`. A column the TARGET stores under a non-concatenable type is excluded the same way, since `CONCAT` reads both sides.

### The checksum compares the value the target STORES

The two sides of the comparison are two different tables, and a checksum of raw column references silently misreads that in two ways. Both are corrected by reading the target's actual column types after the schema evolve (`IngestionFlowRunner.ReadColumnTypesAsync`) and rendering each term accordingly:

- **The target may store a column under a different type than staging carries it.** A pre-created target loaded with `schema.sync: false` (the migrated-source pattern) is the usual case: a MySQL `char(36)` staged as `nchar(36)` into a `uniqueidentifier` column, or a `datetime2` staged into a `datetime`. `CONCAT` then renders the same value two ways (`2828ca9b-...` against `2828CA9B-...`), so every matched row hashes as changed and the flow rewrites its whole matched set on every run. The staging side is converted to the target's type first.
- **Some types have a lossy default string form,** so a real change can hash as no change. `CONCAT` prints a `datetime` to the minute (style 0, `May  6 2024  7:08AM`), a `float` to six significant digits, and `money` to two of its four decimals. Those families are rendered with an explicit lossless style: `126` (ISO 8601) for the date/time family, `3` (all 17 digits) for `float`/`real`, `2` (all four decimals) for `money`/`smallmoney`.

```sql
-- staged datetime2(0) against a stored datetime, and a uniqueidentifier target
HASHBYTES('SHA2_256', CONCAT(N'', CONVERT(uniqueidentifier, src.[Uuid]), N'|', CONVERT(nvarchar(40), CONVERT(datetime, src.[Stamp]), 126))) <>
HASHBYTES('SHA2_256', CONCAT(N'', trg.[Uuid], N'|', CONVERT(nvarchar(40), trg.[Stamp], 126)))
```

Columns whose two types agree and render exactly (`int`, `decimal`, the character types, `uniqueidentifier`, `bit`) keep the bare column reference on both sides. Types SqlFlow does not model are left alone. The reconciled columns are logged at debug level as `upsert.plan`.

Staging is collapsed to one row per business key on insert with `ROW_NUMBER() OVER (PARTITION BY <keys>)`, keeping `_rn = 1`. This matters because an incremental read over an append-mode landing source legitimately returns several rows with the same key (the same key landed by more than one file or window); the target enforces one row per key, so the anti-join INSERT must add exactly one or it fails with a duplicate-key violation. Partitioning on the key columns (always comparable) collapses them and carries any non-comparable data column (`xml`, `geography`, `image` and kin) along unpartitioned, so it holds for every staging shape. The SCD2 and dataset-loop inserts use the same collapse.

### System-column stamping

When the corresponding system columns are enabled (see `systemColumns` in [ing-load](../flow/ing-load.md)):

- Insert branch: `InsertedDate_DW = SYSUTCDATETIME()` and `RowStatus_DW = 'I'`.
- Update branch: `UpdatedDate_DW = SYSUTCDATETIME()`, `RowStatus_DW = 'U'`, and a NULL-only backfill of `InsertedDate_DW` (a matched row that predates the column gets stamped the first time it is touched; an existing value is preserved).

### Skips and validation

- `load.skipUpdateExisting: true` omits the UPDATE branch (insert-only). The UPDATE is also skipped when there are no non-key columns to update.
- `load.skipInsertNew: true` omits the INSERT branch (update-only).
- A keyed apply with zero key columns fails: `An upsert requires at least one key column.`
- Every key column must be among the data columns, else: `Key column '<key>' is not among the data columns.`

By default the UPDATE and INSERT run in one transaction (all-or-nothing). The batched apply below is the exception.

## Batched upsert (lock-escalation avoidance)

`load.batchUpsert: true` (model: `IngestionLoadPolicy.BatchUpsertToAvoidLockEscalation`) applies the same logic through ROW_NUMBER key windows of `load.batchUpsertRowCount` rows (default 2000), so each DML statement stays under SQL Server's lock-escalation threshold.

The generated scripts (`BatchedUpdateScript` / `BatchedInsertScript`):

1. Snapshot the matched keys into `#UpsertKeysU` (update) or the new keys into `#UpsertKeysI` (insert) with a `ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS RowNum`.
2. Create a clustered index on `RowNum` and `CREATE STATISTICS` on the key columns.
3. Loop `WHERE k.RowNum BETWEEN @Start AND @End`, advancing by the batch size and accumulating `@@ROWCOUNT`.
4. Report the accumulated total as a single scalar result set (`UpsertStatement.CountFromScalar`), not via a rows-affected count.

The change predicate, when present, is evaluated inside each window exactly as in the unbatched form.

The batched apply runs WITHOUT an enclosing transaction by design (`IngestionFlowRunner.ApplyLoadAsync`): each key window commits and releases its locks on its own, trading the single-transaction atomicity of the default apply for lock friendliness on large deltas. The run log announces this: `batched apply: key windows of N row(s), per-window commits (no enclosing transaction)`.

A batch size below 1 fails: `BatchRowCount must be at least 1, got N.` (checked only when batching is on).

## Dataset-partitioned loading (load.dataSetColumn)

When `load.dataSetColumn` is set, staging is applied to the target one dataset at a time, partitioned by that column's distinct values in ascending order. Each dataset's UPDATE-then-INSERT sees the target as the prior datasets left it, so a business key that recurs across datasets ends at the value from the LAST dataset carrying it. This preserves file/partition load order in a shared staging area, which a single set-based upsert cannot express.

The generated script (`UpsertGenerator.GenerateDataSetLoopStatement`) is ONE statement:

1. Staging is collapsed to one row per (dataset, key) via `ROW_NUMBER()` into `#dsstg`.
2. The distinct dataset values are numbered in ascending order into `#DataSets`.
3. A `WHILE` loop walks the datasets by number; a NULL dataset value matches via `(src.[col] = ds._ds OR (src.[col] IS NULL AND ds._ds IS NULL))`.
4. Per-branch counts accumulate in `#InsertUpdates`; the script's final `SELECT` reports the totals as a single result-set row with `Inserts` and `Updates` columns (`UpsertStatement.CountFromResultSet`, kind `Combined`).

Constraints and interactions, all enforced in code:

- `dataSetColumn` must be one of the data columns: `DataSetColumn '<col>' is not among the data columns.`
- Not combinable with SCD2 versioning: `A dataset-column load (DataSetColumn) cannot be combined with SCD2 versioning.`
- Honors `load.batchUpsert` within each dataset: each dataset's update and insert are windowed by key into `#curUpd` / `#curIns` temp tables in `load.batchUpsertRowCount` batches.

Note that `load.dataSetColumn` (the apply loop, documented here) is distinct from `source.dataSetColumn` (a source-side dataset column declaration); both parse, but only the load key drives the partitioned apply.

## The hash-key column (change: block)

`change.hashColumns` configures an injected `HashKey_DW` column (`ColumnRole.HashKey`, nullable) on the target, typed `binary(N)` sized by the algorithm (`src/SqlFlow.SqlServer/Schema/HashKey.cs`):

| Algorithm | Column type |
|---|---|
| `SHA2_512` | `binary(64)` |
| `SHA2_256` | `binary(32)` |
| `SHA`, `SHA1` | `binary(20)` |
| `MD2`, `MD4`, `MD5` | `binary(16)` |

`change.hashType` selects the algorithm for both the injected column's size and the upsert's change-detection checksum; unset means `SHA2_256` (`HashKey.DefaultAlgorithm`). The name is case-insensitive and trimmed. An unknown algorithm fails fast:

```text
Unknown hash algorithm '<x>'. Valid values: SHA2_512, SHA2_256, SHA1, MD5 (also MD2, MD4, SHA).
```

The upsert validates the algorithm through `HashKey.BinaryTypeFor` before interpolating it into the `HASHBYTES` call, so the algorithm name is injection-safe by construction.

## The key-match pass: deleted-row detection (matchKeys)

An upsert never removes anything, so a row deleted at the source lives on in the target forever. `load.matchKeysInSourceAndTarget: true` closes that gap. After every load, including incremental ones, the runner:

1. Rebuilds the flow's canonical key table `[raw].[mkey_{targetSchema}_{targetTable}_{flowId}]` (dropping any prior incarnation first) by cloning the target key columns' exact types and collations (`SELECT TOP (0) ... INTO`), with a clustered index on the keys.
2. Lands the full distinct SOURCE key set in it. The key fetch is bounded only by the static filter, never the incremental window, so a row deleted outside the window is still detected.
3. Runs one script (`MatchKeyGenerator.Generate`) that anti-joins the target against the key set with NULL-safe key equality (a NULL key matches a NULL key) and tags or deletes target rows whose keys vanished.
4. Reads back one counter row: `TotalRows`, `CandidateRows`, `AffectedRows`, `ResurrectedRows`, `ThresholdBreached`.

The key table is dropped on success unless `load.keepStagingTable` keeps the flow's work tables; a failed run keeps it for debugging, and the next run's rebuild resets it.

### Action, threshold, resurrection

- `matchKeys.action: tag` (the default) soft-deletes: it stamps `DeletedDate_DW = SYSUTCDATETIME()` and, when the row-status column is on, `RowStatus_DW = 'D'`. The row stays.
- `matchKeys.action: delete` hard-deletes the row. Any other value fails validation: `'matchKeys.action' must be 'tag' or 'delete', got '<x>'.`
- Tag mode requires the `DeletedDate_DW` system column. The YAML loader auto-enables it when the pass is on in tag mode; the runner guards the non-YAML paths with: `MatchKeys Tag mode soft-deletes by stamping DeletedDate_DW; enable the DeletedDate_DW system column.`
- `matchKeys.thresholdPercent` (default 20, valid 0 to 100) is enforced: when more than N% of the target rows would be affected, the action is SKIPPED and a loud warning is logged (`WARNING: action SKIPPED ... A mass key disappearance is usually a broken source read; raise matchKeys.thresholdPercent to proceed.`). The legacy engine carried this column but never enforced it.
- In tag mode, a previously tagged row whose key REAPPEARS in the source is un-tagged (resurrected): `DeletedDate_DW` is cleared and `RowStatus_DW` reset to `'U'`. Resurrection runs independently of the threshold, since un-deleting is always safe. Legacy never cleared `DeletedDate_DW`.

### Scoping the pass

- `matchKeys.keyColumns` overrides `load.keyColumns` for the WHOLE apply, not just this pass: the upsert match, the target's unique key index, and the delete detection all key on the same columns (`IngestionFlowRunner.EffectiveKeyColumns`).
- `matchKeys.sourceFilter` bounds the source key read. It defaults to the flow's `source.filter`, so rows the load never reads are not treated as deleted (legacy defaulted to no filter and deleted them). `matchKeys.targetFilter` bounds which target rows the pass may touch. Both are raw-append fragments that carry their own leading `AND`.
- `matchKeys.ignoreDeletedRowsAfterMonths` (tag mode) limits tagging to rows whose date column is within N months (`DATEADD(MONTH, -N, SYSUTCDATETIME())`); older rows are left alone. The date column is `matchKeys.dateColumn`, with a runner-level fallback to `incremental.dateColumn`; when neither is set the run fails with `matchKeys.ignoreDeletedRowsAfterMonths requires matchKeys.dateColumn (or an incremental dateColumn).` The YAML loader is stricter and requires `matchKeys.dateColumn` explicitly at parse time: `'matchKeys.ignoreDeletedRowsAfterMonths' requires 'matchKeys.dateColumn'.`
- A keyless flow with the pass on fails: `MatchKeysInSourceAndTarget is on, but the flow has no key columns (set load.keyColumns or matchKeys.keyColumns).`

## Configuration touchpoints

| YAML key | Model | Effect |
|---|---|---|
| `load.keyColumns` | `IngestionLoadPolicy.KeyColumns` | Business keys for the upsert match; empty means keyless append |
| `load.skipUpdateExisting` | `SkipUpdateExisting` | Insert-only: omit the UPDATE branch |
| `load.skipInsertNew` | `SkipInsertNew` | Update-only: omit the INSERT branch |
| `load.batchUpsert` | `BatchUpsertToAvoidLockEscalation` | Key-windowed apply, per-window commits |
| `load.batchUpsertRowCount` | `BatchUpsertRowCount` | Rows per window (default 2000) |
| `load.dataSetColumn` | `DataSetColumn` | Dataset-partitioned apply loop |
| `load.matchKeysInSourceAndTarget` | `MatchKeysInSourceAndTarget` | Enable the deleted-row detection pass |
| `matchKeys.*` | `MatchKeyPolicy` | Action, threshold, key override, filters, age window |
| `change.hashColumns` | `ChangePolicy.HashColumns` | Inject the `HashKey_DW` column |
| `change.hashType` | `ChangePolicy.HashType` | HASHBYTES algorithm (default `SHA2_256`) |
| `change.ignoreColumnsInHash` | `ChangePolicy.IgnoreColumnsInHash` | Exclude columns from change detection (still copied) |
| `systemColumns.*` | `SystemColumnsPolicy` | Which `_DW` audit columns the branches stamp |

There are no environment variables or CLI flags specific to this machinery; the flow document is the whole configuration, applied by `sqlflow run <file>` and checked by `sqlflow validate <file>`.

## Example

Adapted from `samples/ingestion/orders-ingestion.flow.yaml`: a keyed upsert with batching, hash exclusions, and soft-delete detection.

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "AND Status <> 'draft'"

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]
  batchUpsert: true
  batchUpsertRowCount: 5000
  matchKeysInSourceAndTarget: true

change:
  hashType: SHA2_256
  ignoreColumnsInHash: [LastSyncedAt]

matchKeys:
  action: tag
  thresholdPercent: 20
  ignoreDeletedRowsAfterMonths: 6
  dateColumn: OrderDate

systemColumns:
  rowStatus: true
```

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

Each run stages the source rows, batch-updates the changed matched orders and batch-inserts the new ones (skipping `LastSyncedAt`-only changes), then tags orders that vanished from the source with `DeletedDate_DW` and `RowStatus_DW = 'D'`, skipping the action loudly if more than 20% of the target would be tagged, and un-tagging any order whose key came back.

## See also

- [Ingestion flow: load, matchKeys, change, systemColumns](../flow/ing-load.md): the full key-by-key reference for these blocks.
- [Ingestion flow: versioning](../flow/ing-versioning.md): SCD2 dimension history, which replaces the plain upsert with a close-and-insert apply.
- [Ingestion flow overview](../flow/ing.md): where the apply step sits in the whole run pipeline.
- [Ingestion run pipeline internals](./ingestion-run-pipeline.md): staging, source SELECT, and incremental windows around the apply step described here.
