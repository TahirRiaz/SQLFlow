---
id: flow-ing-quality
title: "Ingestion flow: assertions, surrogateKeys, virtualColumns"
type: flow-reference
summary: "Inline data-quality assertions, IDENTITY-backed surrogate-key generation, and computed virtual columns on an ing flow document."
keywords:
  - assertions
  - surrogate keys
  - virtual columns
  - data quality
  - "@tablename"
  - computed columns
  - identity lookup
  - keycolumns
yamlPath: "assertions / surrogateKeys / virtualColumns (flowType: ing)"
related:
  - flow-ing
  - concept-shadow-catalog
  - concept-ingestion-run-pipeline
sourceRefs:
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Core/Ingestion/AssertionDefinition.cs
  - src/SqlFlow.Core/Ingestion/InMemoryAssertionDefinitionStore.cs
  - src/SqlFlow.Core/Ingestion/SurrogateKey.cs
  - src/SqlFlow.Core/Ingestion/VirtualColumn.cs
  - src/SqlFlow.SqlServer/Ingestion/AssertionRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/SurrogateKeyExecutor.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
---

# Ingestion flow: assertions, surrogateKeys, virtualColumns

Three optional top-level sections of an ingestion flow (`flowType: ing`) that run around the core load. `assertions` declares named, log-only data-quality checks evaluated against the loaded target after the upsert. `surrogateKeys` generates IDENTITY-backed surrogate keys in a lookup table (auto-created) and stamps them back onto the target, post-load and post-commit. `virtualColumns` marks named source columns as computed projections instead of bulk-copied source data. All three are loaded by src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs and executed by the same engine components as full (control-database) mode.

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

# Log-only assertions, evaluated after the load (@TableName expands to the target).
assertions:
  - name: NotEmpty
    expression: SELECT COUNT(*) FROM @TableName
  - name: NoNullOrderId
    expression: SELECT COUNT(*) FROM @TableName WHERE OrderId IS NULL

# An IDENTITY-backed lookup that assigns OrderKey per OrderId and writes it back.
surrogateKeys:
  - table: TestDB.dbo.OrderKeyMap
    column: OrderKey
    keyColumns: [OrderId]
```

## Keys reference

### assertions (list)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | string | yes | none | Unique assertion name (case-insensitive across the list). |
| `expression` | string | yes | none | T-SQL query template run against the loaded target; may use the `@TableName` and `@FilterCriteria` macros. |
| `mode` | string | no | `auto` | `auto` evaluates the assertion as part of every ingestion run; `manual` reserves it for an on-demand assertions-only run (see below). Case-insensitive; anything else fails with `'assertions[i].mode' has unknown value '<v>'. Allowed: auto, manual.` |

### surrogateKeys (list)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `table` | string | yes | none | Three-part `Database.Schema.Table` name of the surrogate lookup table. Auto-created if missing. |
| `column` | string | yes | none | The IDENTITY surrogate column in the lookup table; also added to the target (as `int NULL`) if absent. |
| `keyColumns` | string list | yes (non-empty) | none | Business-key columns on the target that drive the DISTINCT and the joins. |
| `sKeyColumns` | string list | no | `[]` | Lookup-table key-column names the business keys map onto, positionally. Empty means the lookup table uses the `keyColumns` names. |
| `server` | string | no | unset (target connection) | Connection alias where keys are generated. Must be declared under `connections:`. A different resolved connection than the target means remote generation. |
| `preProcess` | string | no | unset | Raw T-SQL run on the surrogate connection before generation (only when the trimmed text is longer than 2 characters). |
| `postProcess` | string | no | unset | Raw T-SQL run on the surrogate connection after generation and push-back (same length gate). |

### virtualColumns (list)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `expression` | string | yes | none | T-SQL select expression that produces the column value (legacy `SelectExp`). |
| `name` | string | no | unset | The output column name (legacy `ColumnName`). |
| `dataType` | string | no | unset | Declared target data type, for example `nvarchar(50)` (legacy `DataType`). |
| `dataTypeExpression` | string | no | unset | A T-SQL expression that yields the type, for example `CAST('2022-01-01' AS DATE)` (legacy `DataTypeExp`); an alternative to `dataType`. |

## assertions

Each entry is a `{name, expression}` pair with an optional `mode`. The loader (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs) validates:

- A missing or blank name fails with `'assertions[i].name' is required.`
- A missing or blank expression fails with `'assertions[i].expression' is required.`
- Names must be unique case-insensitively; a repeat fails with `assertion '<name>' is declared more than once.`

The declarations become `AssertionDefinition` records held in an `InMemoryAssertionDefinitionStore` (src/SqlFlow.Core/Ingestion/InMemoryAssertionDefinitionStore.cs), so a YAML document runs through the exact same `AssertionRunner` as full mode; only the definition storage differs. Lookups in the store are case-insensitive.

### Runtime behavior

`AssertionRunner` (src/SqlFlow.SqlServer/Ingestion/AssertionRunner.cs) runs after the load commits, after the surrogate keys, and materializes each expression by naive string REPLACE, exactly as legacy. A normal ingestion run evaluates only the `mode: auto` assertions; `mode: manual` ones are skipped entirely (no result row) and wait for an assertions-only run:

- `@TableName` is replaced with the two-part, bracket-escaped target name, for example `[dbo].[Orders_DW]`.
- `@FilterCriteria` is replaced with the flow's `source.incrementalClause` (empty string when unset).

Each materialized statement runs against the target connection with a command timeout of 3600 seconds. The first row's first column becomes `Result` and its second column (when present) becomes `AssertedValue`.

Assertions are log-only and non-blocking:

- A failing or erroring assertion never fails or rolls back the run. A per-assertion exception yields `Evaluated = false`, `Result = "0"`, and the error message, and the remaining assertions still run.
- An assertion name with no definition is dropped silently, preserving the list order (the legacy `INNER JOIN` semantics). In YAML mode every declared entry defines itself, so this only surfaces through the legacy control-database path.
- An expression that is blank after macro expansion is skipped.

Each result (name, materialized SQL, result, asserted value, duration, error) lands on the run result and in the run log; the materialized SQL is captured in the SQL trace under `assertion.<name>`. When the run artifact is synced, assertion results are projected into the shadow catalog as `RunAssertion` rows (src/SqlFlow.Catalog/CatalogProjection.cs).

### Auto-mode versus manual-mode assertions

A declared assertion does not necessarily run after every load; its `mode` decides when it fires:

- `mode: auto` (the default) assertions run as step 7c of a normal ingestion run. The runner calls `AssertionRunner.RunAsync` with `includeManual: false` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs:596), so a manual-mode assertion is skipped there and records no result for that run.
- `mode: manual` assertions run only in an assertions-only run, triggered by `sqlflow run --assertions-only`. That path calls `AssertionRunner.RunAsync` with `includeManual: true` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs:794) and evaluates the flow's whole assertion list, auto and manual alike, against the target with no load.

The gate lives in `AssertionRunner` itself (src/SqlFlow.SqlServer/Ingestion/AssertionRunner.cs:51-55): when a definition's `Mode` is `Manual` and `includeManual` is false, that assertion is passed over. So a `manual` assertion never blocks or runs during a normal load; use it for heavier or on-demand checks you do not want on the ingestion hot path.

Without an assertion runner wired, the engine default is `NullAssertionRunner`, which runs nothing; the YAML composition root (src/SqlFlow.SqlServer/Ingestion/WithoutDatabaseIngestion.cs) always wires the real runner over the document's declarations.

### On-demand execution (assertions-only runs)

An assertions-only run evaluates the flow's WHOLE assertion list, `auto` and `manual` alike, against the CURRENT target and does nothing else: no source read, no staging rebuild, no load (the branch lives at the top of src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs, before the source connection even resolves). It is triggered as a per-run substitution parameter, never a YAML edit:

- **GUI**: the "Run assertions" button on an ingestion pipeline's detail page, and on the Assertions tab of any ingestion run's detail page.
- **API**: `POST /api/v1/runs` with `"assertionsOnly": true`. Refused (400) for any flow kind but `ing`, and for a node/batch scope (it is a single-flow concept, like the built-in backfill).
- **CLI**: `sqlflow run <pipeline.yaml> --assertions-only` (local) or `sqlflow trigger --repo <r> --flow <f> --assertions-only` (fleet).

The flag is mutually exclusive with `--full`, a backfill window, and a file pattern (an assertions-only run reads no source data). The run records like any other: assertion results land on the run artifact, project into the shadow catalog, and show in the run detail's Assertions tab; the run header carries `assertionsOnly` for the audit trail. The log-only contract carries over per assertion: a failing assertion never fails the run; only an infrastructure failure (an unreachable target) does.

The typical split: cheap invariants (row counts, NULL keys) stay `auto` and run with every load; expensive or occasional checks (a full reconciliation against the source, a heavy DISTINCT scan) are declared `mode: manual` and executed on demand from the GUI when someone actually wants the answer.

## surrogateKeys

Each entry configures one surrogate-key generation pass (`SurrogateKeySpec`, src/SqlFlow.Core/Ingestion/SurrogateKey.cs). The loader validates:

- `table` is required (`'surrogateKeys[i].table' is required.`) and must parse as a three-part name; a short name fails with `Object name '<t>' must be a three-part [Database].[Schema].[Object] name; found N part(s).`
- `column` is required (`'surrogateKeys[i].column' is required.`).
- `keyColumns` must be present and non-empty (`'surrogateKeys[i].keyColumns' is required.`).
- A set `server` must name a declared connection, or loading fails with `'surrogateKeys[i].server' references '<s>', which is not declared under 'connections:'. Leave it unset to generate keys on the target connection.`

At runtime, `sKeyColumns` (when non-empty) must have the same count as `keyColumns`; a mismatch fails that spec with `sKeyColumns has N columns but KeyColumns has M; they must match (positional pairing).`

### Runtime behavior

`SurrogateKeyExecutor` (src/SqlFlow.SqlServer/Ingestion/SurrogateKeyExecutor.cs) runs post-load and post-commit, before the assertions (step 7b in src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs). Like assertions it is log-only: a per-spec failure is captured as `SurrogateKeyResult.Error` and never rolls back the committed load. Per spec, in order:

1. **Provision.** The lookup table is created if missing, in its own database via that database's `sys.sp_executesql`: the surrogate column as `int IDENTITY(1,1) NOT NULL PRIMARY KEY`, the key columns typed from the target's live column types, plus `InsertedDate_DW datetime NULL` and `SrcDBSchTbl nvarchar(250) NULL`, and a `UNIQUE` constraint on the business key. The surrogate column is added to the target as `int NULL` if absent, with a nonclustered index `NCI_<column>`. A business key that is not a target column fails the spec with `Surrogate-key business-key column '<c>' is not on the target table.`
2. **preProcess** runs on the surrogate connection when its trimmed length is greater than 2 (the legacy gate).
3. **Generate.** An `INSERT ... SELECT DISTINCT` anti-join inserts only the business keys not yet in the lookup table, letting the IDENTITY issue the surrogate. The legacy concurrency hole is closed: generation and push-back run in one transaction and the anti-join takes `HOLDLOCK, UPDLOCK` on the lookup table, backed by the UNIQUE key.
4. **Push back.** An `UPDATE` joins the lookup table to the target on the business key and stamps the surrogate onto target rows where it is still `NULL`. Both statements are idempotent on re-run (anti-join on the key, NULL-only push-back).
5. **postProcess** runs on the surrogate connection, same length gate.

**Local vs remote.** With `server` unset, or set to an alias that resolves to the same canonical connection string as the target, the pass is LOCAL: the generate and push-back run as two statements in one transaction on the target connection (the lookup table is addressed three-part). A different resolved connection means REMOTE: no linked server is used; the distinct business keys are bulk-copied into a run-scoped temp table `[dbo].[_SfSkTmp_<flowId>_<token>]` on the surrogate server, generated and stamped there in a transaction, bulk-copied back into a temp on the target server, then pushed onto the target. Both temps are dropped in a best-effort cleanup.

Each `SurrogateKeyResult` carries `KeysGenerated` (rows inserted into the lookup table), `RowsStamped` (target rows updated), `IsRemote`, `Executed`, `Error`, `Duration`, and the ordered SQL `Statements`, which are surfaced in the run's SQL trace under `surrogate-key.<table>` and summarized in the run log.

## virtualColumns

Each entry is a computed projection column (`VirtualColumn`, src/SqlFlow.Core/Ingestion/VirtualColumn.cs), a faithful port of a legacy `flw.IngestionVirtual` row; the full-mode loader reads the same shape from that table. The loader requires `expression`; a missing or blank value fails with `'virtualColumns[i].expression' is required.` `name`, `dataType`, and `dataTypeExpression` are optional and blank values normalize to unset.

### Runtime behavior

The schema builder (src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs) matches each named virtual column against the introspected source columns case-insensitively. A source column whose name matches a virtual declaration is marked computed (role `Virtual`, origin `Computed`) and carries the declared select expression on the schema model. Computed columns are excluded from the bulk-copy name map, so they are not copied from the source and not part of the upsert's data columns; they still appear in the desired target schema. A declaration whose name matches no introspected source column (or that has no name) does not change the built schema.

Because a virtual column is not bulk-copied, it cannot serve as an upsert key. The upsert generator rejects a `load.keyColumns` entry that is not among the bulk-copied data columns with `Key column '<c>' is not among the data columns.`; on the run that creates the target (and when planning the SCD2 key index or a match-key pass), the key-index mapping fails first with `Key column '<c>' is not a bulk-copied target column (is it in IgnoreColumns or a virtual column?).`

`dataType` and `dataTypeExpression` are parsed onto the flow model for legacy `flw.IngestionVirtual` parity (`DataType` / `DataTypeExp`).

## Full example

Adapted from samples/ingestion/orders-ingestion.flow.yaml and the loader's accepted shape, exercising all three sections:

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}
  keystore: ${env:SQLFLOW_KEYSTORE}

source:
  server: erp
  object: AdventureWorks.Sales.Orders

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]

# Data quality: evaluated after the load; reported, never blocking.
assertions:
  - name: NotEmpty
    expression: SELECT COUNT(*) FROM @TableName
  - name: NoNullOrderId
    expression: SELECT COUNT(*) FROM @TableName WHERE OrderID IS NULL

# Surrogate keys: generated after the load, written back to the target.
surrogateKeys:
  - table: DW.dim.Customer            # IDENTITY-backed lookup table (auto-created)
    column: CustomerKey
    keyColumns: [CustomerID]
    sKeyColumns: [CustId]             # positional rename in the lookup table
    server: keystore                  # a different resolved connection than the target ('dwh'), so this runs remote
    preProcess: "EXEC dbo.skBefore"
    postProcess: "EXEC dbo.skAfter"

# Computed columns: a source column matching the name is marked computed, not bulk-copied.
virtualColumns:
  - name: LoadTag
    dataType: nvarchar(50)
    expression: "'erp-nightly'"
```

Validate and run with the CLI:

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

## See also

- [Ingestion flow overview](./ing.md)
- [Ingestion flow: load](./ing-load.md)
- [Ingestion flow: schema, incremental, initLoad](./ing-schema-incremental.md)
- [Ingestion flow: versioning and SCD2](./ing-versioning.md)
- [Hooks: preProcess, postProcess, invokes](./hooks.md)
- [Shadow catalog](../concepts/shadow-catalog.md)
- [Ingestion run pipeline](../concepts/ingestion-run-pipeline.md)
