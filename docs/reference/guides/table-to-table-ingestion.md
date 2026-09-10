---
id: guide-table-to-table-ingestion
title: Table-to-table ingestion end to end
type: guide
summary: Build, validate, and run a flowType ing pipeline that copies a source table into SQL Server through staging, schema sync, and a keyed upsert.
keywords:
  - ingestion
  - flowtype ing
  - upsert
  - staging
  - schema sync
  - keycolumns
  - matchkeys
  - walkthrough
related:
  - flow-ing
  - flow-ing-load
  - cli-catalog-scaffold
  - concept-ingestion-run-pipeline
sourceRefs:
  - samples/ingestion/orders-ingestion.flow.yaml
  - samples/seed/orders-dw.flow.yaml
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs
  - src/SqlFlow.Cli/Program.cs
---

# Table-to-table ingestion end to end

This guide builds a `flowType: ing` pipeline from scratch: a relational source table copied into a SQL Server target through the flow's canonical staging table in the `raw` schema. With the default `schema.sync: true`, the target is created on the first run and new source columns are added automatically on later runs; rows are applied with a keyed two-step upsert: matched rows whose data changed are UPDATEd, new rows are INSERTed. The engine never issues a T-SQL MERGE and never blindly reloads a keyed target. A single YAML file is the whole pipeline; no control database or registration is required.

The target of an ingestion flow must be SQL Server (`mssql` or `azdb`); a foreign target is rejected at parse time with: `the target connection '<name>' is '<kind>'; an ingestion flow's target must be SQL Server (mssql or azdb).`

## 1. Start with a minimal flow

The smallest working document names the flow, declares two connections, points at a source and a target object, and picks the business key:

```yaml
flowType: ing
name: orders-dw

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: TestDB.dbo.Orders

target:
  server: dwh
  object: TestDB.dbo.Orders_DW

load:
  keyColumns: [OrderId]
```

This is samples/seed/orders-dw.flow.yaml, trimmed to its core. `source` and `target` are both required; omitting either fails validation with `'source' is required.` or `'target' is required.`

## 2. Declare connections

`connections:` is a map of named references. `source.server` and `target.server` must reference a declared name; an unknown name fails with `'source.server' references '<name>', which is not declared under 'connections:'.`

Each value is one of:

- A plain string: a connection string or a whole `${env:NAME}` or `${keyvault:vault/secret}` reference. Treated as SQL Server.
- Empty or omitted: the alias resolves its own well-known environment variable by convention.
- A map with `provider` and `connection` keys. `provider` is one of `mssql` (default), `azdb`, `mysql`, `postgres`, or `oracle`.

Instead of `server:`, an endpoint may carry an inline `connection:` (optionally with `provider:` on the source). Setting both on one endpoint fails with `'source' sets both 'server' and 'connection'; use exactly one.` Setting neither fails with a message telling you to set one of them.

```yaml
source:
  connection: ${env:SQLFLOW_SRC}   # inline; synthesizes a connection named 'source'
  object: AdventureWorks.Sales.Orders
```

Secrets never rest in the file: use a whole `${env:...}` or `${keyvault:...}` reference, or a passwordless connection string (Integrated Security or AD Default).

## 3. Name the objects

`object:` (alias: `table:`) is a three-part name, `Database.Schema.Table`, and may point at a table or a view. A two-part name without brackets is read as `database.table` (the MySQL convention, where the schema part is the database), so `erpdb.orders` becomes `erpdb.erpdb.orders`. SQL Server objects must therefore carry the database explicitly; `dbo.Orders` would be misread as a database named `dbo`.

A missing object fails with: `'source.object' is required (a three-part name like Database.Schema.Table, or Database.Table for MySQL, where the database is the schema).`

## 4. Choose keys and load behavior

`load.keyColumns` names the business key that drives the upsert. Without keys the flow takes the insert-all path and appends everything. Other `load` keys, with their defaults from src/SqlFlow.Core/Ingestion/IngestionPolicies.cs:

| Key | Default | Effect |
| --- | --- | --- |
| `keyColumns` | `[]` | Business keys for the update/insert match. Empty appends all rows. |
| `skipUpdateExisting` | `false` | Omit the UPDATE branch: insert-only. |
| `skipInsertNew` | `false` | Omit the INSERT branch: update-only. |
| `matchKeysInSourceAndTarget` | `false` | Enable the deleted-row detection pass (section 7). |
| `batchUpsert` | `false` | Batch the upsert in key windows to avoid lock escalation. |
| `batchUpsertRowCount` | `2000` | Rows per upsert batch when batching. |
| `dataSetColumn` | unset | Apply staging one dataset at a time, partitioned by this column, in ascending order. |
| `streamData` | `true` | Accepted for legacy fidelity; the engine always streams the source directly into staging via SqlBulkCopy, so this flag has no observable effect today. |
| `threads` | unset | Concurrent segment count for an `initLoad` backfill only (each segment streamed on its own connection); unset, `0`, or negative all mean serial. The regular incremental or full read always runs as a single stream, unaffected by this setting. |
| `keepStagingTable` | `false` | Keep the staging table after a successful run. A failed run always keeps it for debugging, regardless of this flag. |
| `truncateStagingOnCompletion` | `false` | Empty a kept staging table after success; only meaningful with `keepStagingTable: true`. |

## 5. Validate and run

```bash
sqlflow validate orders-dw.flow.yaml
sqlflow run      orders-dw.flow.yaml
```

`validate` prints, on success:

```text
OK  'orders-dw' is valid (ingestion: [TestDB].[dbo].[Orders] -> [TestDB].[dbo].[Orders_DW]).
```

Useful `run` flags (from src/SqlFlow.Cli/Program.cs): `--json` emits the execution result as JSON, `--show-sql` prints every SQL statement the run generated in execution order, and `--log-level <level>` adjusts verbosity. The SQL trace is populated on success and failure (captured up to the failure point), so `--show-sql` is the first stop when a run misbehaves.

You can also scaffold the file from live catalog metadata instead of writing it by hand:

```bash
sqlflow catalog scaffold --source '${env:SQLFLOW_SRC}' --object TestDB.dbo.Orders \
  --target-object dbo.Orders_DW --detect-keys --out orders-dw.flow.yaml
```

`--keys OrderId` pins the key columns explicitly; `--detect-keys` fills `keyColumns` from live unique-key detection when `--keys` is not given.

## 6. What one run does

The runner (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs) executes these stages:

1. Resolves the source and target connections through the registry.
2. Introspects and shapes the source columns (applying `ignoreColumns`, virtual columns, and name cleaning).
3. Rebuilds the flow's canonical staging table on the target: `[raw].[<targetSchema>_<targetTable>_<flowId>]`, named after the target it feeds; one table per flow, reset each run.
4. Streams the source into staging with SqlBulkCopy, appending `source.filter` and any incremental window to the read.
5. Evolves the target schema, when `schema.sync` is true (the default): the target is created if absent, and new source columns are added if it already exists. A type-widening change (for example `int` to `bigint`) requires `schema.allowTableRewrite: true`; the default `false` refuses the rewrite because it holds a table lock for the full row rewrite and belongs in a maintenance window. With `schema.sync: false` this step is skipped entirely, so the target must already exist with a compatible shape.
6. Applies staging to the target: a flow with key columns always takes the two-step keyed upsert (UPDATE changed rows, then INSERT new rows), regardless of whether the target is empty, partially loaded, or just created; a keyless flow instead appends every staged row (insert-all).
7. Runs the key-match pass when enabled (section 7), then assertions and surrogate keys.
8. Drops the staging table on success unless `load.keepStagingTable: true`; a failed run always leaves it in place.

`preProcess` / `postProcess` are raw T-SQL commands run verbatim on the target before and after the load. `preInvoke` / `postInvoke` are Azure hooks around the load (section 9).

## 7. Detect deleted rows with matchKeys

Set `load.matchKeysInSourceAndTarget: true` to detect rows that vanished from the source. After each load the engine lands the full distinct source key set in the flow's canonical `mkey_` key table (in the `raw` schema, rebuilt per run) and compares it against the target in SQL on the target side; the key table is dropped in the same step as the staging table (governed by `load.keepStagingTable`). Target rows whose keys are gone from the source are handled per `matchKeys.action`:

- `tag` (the default): soft delete by stamping the `DeletedDate_DW` system column, which this mode auto-enables so you do not need to set `systemColumns.deletedDate` yourself. A tagged row whose key reappears in the source is un-tagged (resurrected).
- `delete`: hard delete the row. Any other value fails validation with `'matchKeys.action' must be 'tag' or 'delete', got '<value>'.`

```yaml
load:
  keyColumns: [OrderID]
  matchKeysInSourceAndTarget: true

matchKeys:
  action: tag
  thresholdPercent: 20
  ignoreDeletedRowsAfterMonths: 6
  dateColumn: OrderDate
  sourceFilter: "AND Status = 'open'"
  targetFilter: "AND Region = 'NA'"
```

Guard rails, all verified in src/SqlFlow.Core/Ingestion/IngestionPolicies.cs and the runner:

- `thresholdPercent` (default 20, must be 0 to 100): when more than this percentage of the target would be affected, the action is skipped and a warning is logged; a mass disappearance is more often a broken source read than a real mass delete.
- `ignoreDeletedRowsAfterMonths` bounds tag mode to rows whose `dateColumn` is within N months; it requires `matchKeys.dateColumn` to be set explicitly, otherwise validation fails with `'matchKeys.ignoreDeletedRowsAfterMonths' requires 'matchKeys.dateColumn'.`
- `sourceFilter` and `targetFilter` are raw-append predicates carrying their own leading `AND`: the first bounds the source key read (it defaults to the flow's `source.filter`, so rows the load never reads are not treated as deleted), the second bounds which target rows the pass may touch.
- `matchKeys.keyColumns` overrides `load.keyColumns` for this comparison; empty inherits the load keys.
- The pass requires keys: with neither `load.keyColumns` nor `matchKeys.keyColumns` set, the run fails with `MatchKeysInSourceAndTarget is on, but the flow has no key columns (set load.keyColumns or matchKeys.keyColumns).`

## 8. Shape the source and target

Source refinements:

```yaml
source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "AND Status = 'open'"  # raw WHERE fragment; carries its own leading AND/OR
  ignoreColumns: [InternalNote]  # source columns to drop
  dataSetColumn: Region          # dataset/partition column
```

`source.dataSetColumn` marks the dataset column; the canonical index planner (src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs) creates a nonclustered index on it (`NCI_DataSetColumn`) on the target.

Target refinements:

```yaml
target:
  server: dwh
  object: DW.raw.Orders
  truncateBeforeLoad: false            # true gives full-reload semantics
  columnStoreIndex: false              # true creates a clustered columnstore on first create
  desiredIndexes: "CREATE INDEX NCI_Orders_Region ON DW.raw.Orders (Region);"
```

`desiredIndexes` holds declared index DDL applied when the target is created.

Computed columns are injected with `virtualColumns`; each entry needs `expression` (missing it fails with `'virtualColumns[i].expression' is required.`) and may carry `name` and `dataType`:

```yaml
virtualColumns:
  - name: LoadTag
    dataType: nvarchar(50)
    expression: "'erp-nightly'"
```

## 9. Hooks around the load

`preInvoke:` and `postInvoke:` name entries declared under `invokes:`; naming an undeclared invoke fails validation. Azure Data Factory invokes reference a service principal declared under `servicePrincipals:`.

```yaml
postInvoke: notify-pipeline

invokes:
  notify-pipeline:
    type: adf
    pipeline: pl_notify
    servicePrincipal: deploy

servicePrincipals:
  deploy:
    subscriptionId: 00000000-0000-0000-0000-000000000000
    resourceGroup: rg-data
    dataFactoryName: adf-prod
```

## 10. Add assertions

Inline data-quality assertions are evaluated after the load and are log-only, never blocking. `@TableName` expands to the flow's two-part target name. Each assertion needs a unique `name` and an `expression`; a duplicate name fails with `assertion '<name>' is declared more than once.`

```yaml
assertions:
  - name: NotEmpty
    expression: SELECT COUNT(*) FROM @TableName
  - name: NoNullOrderId
    expression: SELECT COUNT(*) FROM @TableName WHERE OrderId IS NULL
```

## 11. Full example

Adapted from samples/ingestion/orders-ingestion.flow.yaml; every key below parses against src/SqlFlow.Yaml/IngestionYaml.cs:

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "AND Status = 'open'"

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]
  matchKeysInSourceAndTarget: true

matchKeys:
  action: tag
  thresholdPercent: 20

schema:
  sync: true
  allowTableRewrite: false

incremental:
  dateColumn: ModifiedDate
  overlapDays: 7

virtualColumns:
  - name: LoadTag
    dataType: nvarchar(50)
    expression: "'erp-nightly'"

assertions:
  - name: NotEmpty
    expression: SELECT COUNT(*) FROM @TableName
```

Run it:

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml --show-sql
```

The first run creates `DW.raw.Orders` and inserts every open order. Later runs read only rows past the `ModifiedDate` high-water mark (re-reading a 7 day overlap window), add any new source columns to the target, update changed rows, insert new ones, and tag rows whose `OrderID` disappeared from the source, skipping the tag pass if more than 20 percent of the target would be affected.

## See also

- [flowType ing reference](../flow/ing.md)
- [load block reference](../flow/ing-load.md)
- [sqlflow catalog scaffold](../cli/catalog-scaffold.md)
- [The ingestion run pipeline](../concepts/ingestion-run-pipeline.md)
