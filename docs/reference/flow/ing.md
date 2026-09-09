---
id: flow-ing
title: "Ingestion flow (flowType: ing): document, source, target"
type: flow-reference
summary: "The flowType: ing YAML document for table-to-table ingestion: top-level keys, source and target endpoints, three-part names, and cross-field rules."
keywords:
  - "flowtype ing"
  - "source.object"
  - "target.object"
  - "three-part name"
  - "canonical indexes"
  - "identitycolumn"
  - "ingestion"
  - "upsert"
  - "sysalias"
yamlPath: "(root, flowType: ing)"
related:
  - flow-ing-load
  - flow-ing-schema-incremental
  - guide-table-to-table-ingestion
  - concept-ingestion-run-pipeline
sourceRefs:
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Core/Ingestion/RelationalObject.cs
  - src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
---

# Ingestion flow (flowType: ing): document, source, target

A `flowType: ing` document defines a relational (table to table) ingestion pipeline: a live relational source table is staged into the flow's canonical staging table (`[raw].[<targetSchema>_<targetTable>_<flowId>]`, rebuilt per run) on the SQL Server target, the target schema is evolved to match, and rows are applied with a keyed two-step upsert (UPDATE changed, then INSERT new; never a T-SQL MERGE). The YAML file is the whole pipeline: parsing yields an `IngestionDocument` carrying the flow plus in-memory stores for the document's connections, assertion definitions, invokes, and service principals. No control database is involved (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs).

Loading and validation live in src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs over the binding DTOs in src/SqlFlow.Yaml/IngestionYaml.cs; the validated model is src/SqlFlow.Core/Ingestion/IngestionFlow.cs. Unknown YAML keys are ignored (the deserializer runs with `IgnoreUnmatchedProperties`).

## Minimal working example

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

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | none | Must be `ing` for this document type. |
| `name` | string | no | unset | Becomes `SysAlias`; also seeds the stable `FlowId`. |
| `batch` | string | no | unset | Batch label carried into the run record (`IngestionFlow.Batch`). |
| `description` | string | no | unset | Free-text description. |
| `connections` | map | no | empty | Named connections referenced by `server:` on source, target, and surrogate keys. |
| `source` | map | yes | none | The source endpoint. Error when missing: `'source' is required.` |
| `target` | map | yes | none | The target endpoint (must resolve to SQL Server). Error when missing: `'target' is required.` |
| `load` | map | no | defaults | Upsert behavior: `keyColumns`, `skipUpdateExisting`, `skipInsertNew`, `matchKeysInSourceAndTarget`, `batchUpsert`, `batchUpsertRowCount` (default 2000), `dataSetColumn`, `reloadColumn` (per-file replace), `streamData` (default true), `threads`, `keepStagingTable`, `truncateStagingOnCompletion`, `truncateSourceWhenConsolidated`. See [ing-load.md](ing-load.md). |
| `matchKeys` | map | no | defaults | Deleted-row detection pass: `action` (`tag` or `delete`), `keyColumns`, `thresholdPercent` (default 20), `ignoreDeletedRowsAfterMonths`, `dateColumn`, `sourceFilter`, `targetFilter`. See [ing-load.md](ing-load.md). |
| `change` | map | no | defaults | Hash-based change detection: `hashColumns`, `hashType`, `ignoreColumnsInHash`. |
| `systemColumns` | map | no | defaults | Audit columns: `insertedDate` (default true), `updatedDate` (default true), `deletedDate` (default false), `rowStatus` (default false). |
| `schema` | map | no | defaults | Schema sync: `sync` (default true), `cleanColumnNames`, `cleanColumnNameRegex`, `replaceInvalidCharsWith`, `convertUnicodeToNonUnicode`, `allowTableRewrite`. See [ing-schema-incremental.md](ing-schema-incremental.md). |
| `incremental` | map | no | defaults | Watermark capture: `columns`, `dateColumn`, `overlapDays` (default 7, date marks only), `lookback` (default 0, numeric marks only), `fullLoad`, `fetchMinValuesFromSource`. See [ing-schema-incremental.md](ing-schema-incremental.md). |
| `initLoad` | map | no | defaults | One-time backfill: `enabled`, `fromDate`, `toDate`, `batchBy` (single character: `M` month, `D` day, `K` key ranges), `batchSize`, `keyColumn`, `keyMaxValue`. |
| `versioning` | map | no | defaults | `temporal` (SQL Server system-versioned history, also reachable as the `temporalHistory` shorthand), `scd2` (engine-managed dimension history), `tokenVersioning`, `tokenRetentionDays`; `insertUnknownDimensionRow` is rejected (see below). |
| `transform` | map | no | defaults | Pre-ingestion transform block, same shape as the file flow's `transform:`. |
| `preProcess` | string | no | unset | Raw T-SQL run verbatim on the target before the load. |
| `postProcess` | string | no | unset | Raw T-SQL run verbatim on the target after the load commits. |
| `preInvoke` | string | no | unset | Name of a block under `invokes:` to run before the load. |
| `postInvoke` | string | no | unset | Name of a block under `invokes:` to run after the load. |
| `invokes` | map | no | empty | Named invoke blocks referenced by `preInvoke`/`postInvoke`. |
| `servicePrincipals` | map | no | empty | Named Azure service principals used by invoke blocks. |
| `virtualColumns` | list | no | empty | Computed columns: each entry has `name`, `dataType`, `dataTypeExpression`, `expression` (required per entry). |
| `assertions` | list | no | empty | Inline data-quality assertions: each entry has `name` and `expression` (both required; duplicate names rejected) and an optional `mode` (`auto` default / `manual` for on-demand assertions-only runs). |
| `surrogateKeys` | list | no | empty | Surrogate key generation: each entry has `server`, `table` (required), `column` (required), `keyColumns` (required), `sKeyColumns`, `preProcess`, `postProcess`. |
| `healthCheck` | map | no | unset | An embedded ML health check over this flow's target table: the standalone hc document's declaration body (`dateColumn` required, `baseValue`/`metrics`, `filter`, `ml`, `maturityDays`, `sentinelDateFloor`, `holidays`) minus target/connections, plus `name` (default `<name>_hc`) and `mode` (default `manual`: runs only on demand). Expands into a derived sibling hc pipeline sharing this file; see the hc reference. Requires the flow to declare `name:`. |

## name, SysAlias, and FlowId

`name` is optional for a `flowType: ing` document. When set, the trimmed value becomes the flow's `SysAlias`, and `FlowId` is a stable positive int31 derived from the name-based GUID (`StableFlowId` masks the GUID's first four bytes with `0x7FFFFFFF`), so logs and staging tables key consistently across runs without a database to assign ids. A blank or missing name yields `FlowId` 0 and no `SysAlias`.

## connections and endpoint resolution

Each value under `connections:` is one of: a plain string (a SQL Server connection reference, the back-compatible form), a map with `provider:` and `connection:` keys, or nothing at all (a bare alias resolves `${env:SQLFLOW_CONN_<NAME>}` by convention; the map form without a `connection:` keeps the provider and takes the same convention). Secrets should not rest in the file; a whole `${env:NAME}` or `${keyvault:vault/secret}` reference is expanded at run time.

Both `source` and `target` take exactly one of:

- `server:` referencing a name declared under `connections:`. An undeclared name fails: `'<section>.server' references '<name>', which is not declared under 'connections:'.`
- `connection:` an inline connection string (or `${...}` reference) with optional `provider:` (`mssql` or `sqlserver`, `azdb`, `mysql`, `postgres` or `postgresql`, `oracle`; SQL Server when omitted). The inline form synthesizes a connection named `source` or `target`; if `connections:` already declares that name the document is rejected with a message telling you to rename it or use `server:`.

Setting both fails: `'<section>' sets both 'server' and 'connection'; use exactly one.` Setting neither fails: `'<section>' needs a connection. Set '<section>.connection' to a connection string or a ${...} reference, or '<section>.server' to a name declared under 'connections:'.`

In the model, the endpoint's connection reference is `@` plus the resolved server alias (`IngestionSource.ConnectionReference` and `IngestionTarget.ConnectionReference` in src/SqlFlow.Core/Ingestion/IngestionFlow.cs).

## source

```yaml
source:
  server: erp
  object: AdventureWorks.Sales.Orders
  filter: "Status = 'open'"
  ignoreColumns: [InternalNote]
  dataSetColumn: Region
```

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `server` / `connection` | string | exactly one | none | Connection resolution as above; `provider` applies to the inline form. |
| `object` | string | yes | none | Three-part `Database.Schema.Table` name; `table` is an accepted alias for this key. |
| `filter` | string | no | unset | Raw WHERE fragment shaping the source read. |
| `filterIsAppend` | bool | no | `true` | Whether the filter is appended to the engine-built predicate. |
| `incrementalClause` | string | no | unset | Extra incremental predicate expression. |
| `ignoreColumns` | string list | no | empty | Source columns to drop from the read. |
| `dataSetColumn` | string | no | unset | Dataset/partition column; it also gets a canonical index on target create. |

### source.object

`source.object` (or its alias `source.table`) is required. Missing it fails with: `'source.object' is required (a three-part name like Database.Schema.Table, or Database.Table for MySQL, where the database is the schema).`

Name parsing rules (src/SqlFlow.Core/Ingestion/RelationalObject.cs, src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs):

- A two-part unbracketed name `X.Y` is expanded to `X.X.Y`: the MySQL convention, where the database part is the schema. The expansion only applies when the raw value contains no `[` and both parts are non-empty.
- `RelationalObject.Parse` is bracket-aware (`.` inside `[...]` does not split; an inner `]` is escaped as `]]`), and takes the rightmost three parts, so a stray leading server part is dropped rather than failing.
- Fewer than three parts fails with: `Object name '<name>' must be a three-part [Database].[Schema].[Object] name; found N part(s).` (surfaced through the YAML layer as `'source.object': <message>`). An empty database, schema, or object part is also rejected.

## target

```yaml
target:
  server: dwh
  object: DW.raw.Orders
  truncateBeforeLoad: false
  columnStoreIndex: false
  identityColumn: OrderSK
  desiredIndexes: "CREATE INDEX IX_Orders_Region ON [raw].[Orders] (Region);"
```

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `server` / `connection` | string | exactly one | none | Connection resolution as above; the resolved connection must be SQL Server. |
| `object` | string | yes | none | Three-part `Database.Schema.Table` name; `table` is an accepted alias. Same parsing rules as `source.object`. |
| `truncateBeforeLoad` | bool | no | `false` | Truncate the target before applying staging (full reload semantics). Not combinable with `versioning.scd2`, nor with `versioning.temporal` (SQL Server does not allow TRUNCATE on a system-versioned table). |
| `columnStoreIndex` | bool | no | `false` | Create a clustered columnstore index when the target is first created. |
| `identityColumn` | string | no | unset | Identity column on the target. An identity primary key suppresses the columnstore index (the identity PK wins). |
| `desiredIndexes` | string | no | unset | CREATE INDEX statements applied when the target is created. |

The target must resolve to a SQL Server connection (`mssql` or `azdb`): staging, schema evolution, and the upsert are T-SQL. A foreign target is rejected at parse time: `the target connection '<name>' is '<kind>'; an ingestion flow's target must be SQL Server (mssql or azdb).`

### Canonical indexes on target create

On the run that creates the target, `CanonicalIndexPlanner` (src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs) emits:

- `NCI_KeyColumn`: a UNIQUE NONCLUSTERED index on the key columns.
- `NCI_DateColumn`, `NCI_DataSetColumn`, `NCI_UpdatedDate_DW`: plain NONCLUSTERED indexes, each only when the respective column exists.
- `CCSI_<table>`: a clustered columnstore index, only when `target.columnStoreIndex` is true AND there is no identity primary key.

Every statement is guarded by an `IF NOT EXISTS` check against `sys.indexes`, so re-application is a no-op; index names are stable (the legacy `{NameHash}` suffix is dropped). Under SCD2 the plain unique key index is suppressed; `Scd2KeyIndexStatements` creates a filtered-unique `NCI_KeyColumn` (`WHERE [flag] = 1`) instead and migrates a pre-existing plain unique index. Index columns are the cleaned TARGET names: the flow declares SOURCE names and the runner maps them through the bulk-copy name map, so a missing optional date or dataset column simply yields no index (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs).

## Cross-field validation rules

The loader enforces these at parse time (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs):

- `versioning.temporal.enabled: true` (or the `versioning.temporalHistory: true` shorthand) cannot combine with `versioning.scd2.enabled: true`: `'versioning.temporal' and 'versioning.scd2' cannot both be enabled. ... Choose one.`
- `versioning.temporal.enabled: true` cannot combine with `target.truncateBeforeLoad: true`: `'versioning.temporal' cannot be combined with 'target.truncateBeforeLoad'. SQL Server does not allow TRUNCATE TABLE on a system-versioned table ...`
- `versioning.temporalHistory: false` alongside `versioning.temporal.enabled: true` is rejected as a contradiction; `temporalHistory` is the shorthand for `temporal.enabled`.
- `versioning.temporal.historySchema` / `historyTable` must be plain undotted names (SQL Server requires the history table to live in the target's own database); `periodPrecision` must be 0-7; `retentionDays` must be positive.
- `versioning.insertUnknownDimensionRow: true` is rejected: `'versioning.insertUnknownDimensionRow' is not yet implemented; seed the unknown-member row explicitly for now.`
- `versioning.scd2.enabled: true` requires `load.keyColumns`; missing them fails with: `'versioning.scd2' requires 'load.keyColumns' (the business key the dimension versions by).`
- `versioning.scd2.enabled: true` cannot combine with `target.truncateBeforeLoad: true`: `'versioning.scd2' cannot be combined with 'target.truncateBeforeLoad'; truncating would erase the dimension history.`
- SCD2's `validFromColumn`, `validToColumn`, and `currentFlagColumn` must be three distinct names (case-insensitive); defaults are `ValidFrom_DW`, `ValidTo_DW`, `IsCurrent_DW`.
- When `load.matchKeysInSourceAndTarget: true` and the match-key action is `tag` (the default), the `DeletedDate_DW` system column is auto-enabled, so the soft-delete stamp does not require setting both flags independently.
- `matchKeys.action` must be `tag` or `delete`; `matchKeys.thresholdPercent` must be 0 to 100; `matchKeys.ignoreDeletedRowsAfterMonths` requires `matchKeys.dateColumn`.

A keyless flow (no `load.keyColumns`) appends every staged row (insert-all); key columns drive the two-step upsert.

## transform

The `transform:` block shares the file flow's shape and validation: it binds to the same `TransformYaml` DTO and maps through `YamlFlowLoader.MapInference` (src/SqlFlow.Yaml/FlowYaml.cs, src/SqlFlow.Yaml/YamlFlowLoader.cs). Keys: `inferTypes` (default false), `onConvertError`, `threshold` (default 1.0), `sample` (default 0), `preserveLeadingZeros` (default true), `generateView` (default true), and `columns` (entries with `name`, `expr`, `as`, `type`, `order`, `virtual`, `excludeFromView`). The resulting policy projects the target into a typed transformation view; a native SQL-to-SQL flow leaves it at its default, which generates nothing.

## preProcess and postProcess

Unlike the file flow (where these are string lists), the ingestion document takes a single raw T-SQL string for each. `preProcess` runs verbatim on the target before the load and `postProcess` after the load commits; both execute as `CommandType.Text` with no parameters bound (typically `EXEC [schema].[proc]`).

```yaml
preProcess: "EXEC dbo.usp_BeforeOrders"
postProcess: "UPDATE STATISTICS DW.raw.Orders"
```

## Fuller example

Adapted from samples/ingestion/orders-ingestion.flow.yaml and samples/ingestion/mysql-to-sqlserver.flow.yaml:

```yaml
flowType: ing
name: shop-orders
description: Nightly MySQL shop orders into the warehouse raw layer.

connections:
  shop:
    provider: mysql
    connection: ${env:SHOP_MYSQL}
  dwh: ${env:SQLFLOW_DW}

source:
  server: shop
  object: shop.orders          # MySQL two-part name, expanded to shop.shop.orders
  ignoreColumns: [internal_note]

target:
  server: dwh
  object: DW.raw.ShopOrders
  columnStoreIndex: false
  desiredIndexes: "CREATE INDEX IX_ShopOrders_Status ON [raw].[ShopOrders] (status);"

load:
  keyColumns: [id]
  matchKeysInSourceAndTarget: true   # tag mode: auto-enables DeletedDate_DW

matchKeys:
  action: tag
  thresholdPercent: 20

incremental:
  columns: [updated_at]
  overlapDays: 7

assertions:
  - name: NotEmpty
    expression: SELECT COUNT(*) FROM @TableName

postProcess: "UPDATE STATISTICS DW.raw.ShopOrders"
```

## See also

- [ing-load.md](ing-load.md): the `load`, `matchKeys`, `change`, and `systemColumns` sections in depth.
- [ing-schema-incremental.md](ing-schema-incremental.md): the `schema`, `incremental`, and `initLoad` sections in depth.
- [../guides/table-to-table-ingestion.md](../guides/table-to-table-ingestion.md): end-to-end walkthrough.
- [../concepts/ingestion-run-pipeline.md](../concepts/ingestion-run-pipeline.md): the run pipeline (stage, evolve, index, upsert, match keys).
