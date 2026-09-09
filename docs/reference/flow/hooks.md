---
id: flow-hooks
title: "Process and invoke hooks: preProcess, postProcess, desiredIndexes, preInvoke, postInvoke"
type: flow-reference
summary: SQL hooks run on the target before and after the load, declared index scripts, and Azure invoke hooks, for file flows and ingestion flows.
keywords:
  - preprocess
  - postprocess
  - desiredindexes
  - preinvoke
  - postinvoke
  - hooks
  - create index
  - invokes
yamlPath: preProcess / postProcess / desiredIndexes / preInvoke / postInvoke
related:
  - flow-inv
  - flow-load
  - flow-ing
  - flow-sp
  - flow-exp
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/YamlInvokeParts.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.Core/Invoke/IInvokeRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/TargetProcessHooks.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs
  - src/SqlFlow.SqlServer/SqlServerIndexManager.cs
  - src/SqlFlow.SqlServer/SqlServerDesiredIndexManager.cs
---

# Process and invoke hooks: preProcess, postProcess, desiredIndexes, preInvoke, postInvoke

Hooks let a flow run your own SQL on the target around the load, declare the indexes a freshly created target table should have, and trigger Azure Data Factory pipelines or Automation runbooks before the data work and after the load commits. The two flow families take different shapes for the same key names, so keep them apart:

- **File flows** (csv, json, parquet, and the other file readers; a document with no `flowType:` key): `preProcess:` and `postProcess:` are **YAML lists of SQL statements**, and `desiredIndexes:` is a top-level string of `CREATE INDEX` statements.
- **Ingestion flows** (`flowType: ing`): `preProcess:` and `postProcess:` are each a **single raw T-SQL string**, `preInvoke:`/`postInvoke:` name an invoke declared under `invokes:`, and declared indexes live under `target.desiredIndexes`.

Minimal examples, one per family:

```yaml
# File flow: lists of SQL statements, top-level desiredIndexes.
name: orders_csv
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders
preProcess:
  - "EXEC dbo.usp_BeforeOrders"
postProcess:
  - "UPDATE STATISTICS dbo.Orders"
desiredIndexes: |
  CREATE NONCLUSTERED INDEX IX_Orders_OrderId ON dbo.Orders (OrderId);
```

```yaml
# Ingestion flow: single raw T-SQL strings, plus invoke hooks by name.
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
preProcess: "EXEC dbo.usp_BeforeOrders"
postProcess: "UPDATE STATISTICS DW.raw.Orders"
```

## Keys reference

### File flow (top-level keys)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `preProcess` | list of strings (SQL) | no | `[]` | SQL run on the target before the load: inline statements or `EXEC` of a procedure. |
| `postProcess` | list of strings (SQL) | no | `[]` | SQL run on the target after the load. |
| `desiredIndexes` | string (SQL) | no | none | One or more `CREATE INDEX` statements; applied only on the run that creates the target table. |

### Ingestion flow (`flowType: ing`)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `preProcess` | string (raw T-SQL) | no | none | One raw T-SQL command (typically `EXEC [schema].[proc]`) run verbatim on the target before the load. |
| `postProcess` | string (raw T-SQL) | no | none | One raw T-SQL command run verbatim on the target after the load commits. |
| `preInvoke` | string (invoke name) | no | none | Name of an invoke declared under `invokes:`, run before any data work. |
| `postInvoke` | string (invoke name) | no | none | Name of an invoke declared under `invokes:`, run after the load commits. |
| `target.desiredIndexes` | string (SQL) | no | none | Declared indexes for the target, applied only on the run that creates it. |

## preProcess and postProcess (file flows)

Parsed as `List<string>` in src/SqlFlow.Yaml/FlowYaml.cs and mapped in src/SqlFlow.Yaml/YamlFlowLoader.cs; an omitted key becomes an empty list and the stage is skipped entirely.

Execution (src/SqlFlow.Core/Engine/FlowRunner.cs):

- `preProcess` runs as stage `target.preprocess`, after the schema DDL is applied (so on a first run the target table already exists when the hook fires) and before index management, truncation, and the load.
- `postProcess` runs as stage `target.postprocess`, after the load, after index rebuild and desired-index application, and before the source files are finalized (`source.complete`) and the transform view is refreshed.

Each hook list executes on the target connection through the schema provider's DDL executor (src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs): all statements in the list run inside one transaction, committed at the end; any failure rolls back the whole list and fails the run. Statements run in list order.

```yaml
preProcess:
  - "EXEC dbo.usp_PrepareStaging"
  - "DELETE FROM dbo.Orders WHERE LoadTag = 'retry'"
postProcess:
  - "UPDATE STATISTICS dbo.Orders"
  - "EXEC dbo.usp_PublishOrders"
```

## desiredIndexes (file flows)

A single string (usually a YAML block scalar) carrying one or more `CREATE INDEX` statements. A whitespace-only value is treated as absent (src/SqlFlow.Yaml/YamlFlowLoader.cs).

Index handling is split by whether the run created the table (src/SqlFlow.Core/Engine/FlowRunner.cs):

- **Table created during this run**: after the load, the desired-index manager applies the script as stage `indexes.desired`. Each statement is reported as an action; a created index logs `created index [name] on table`, and a statement that could not be applied logs a warning `index [name] not created: <detail>` without failing the run.
- **Table already existed**: `desiredIndexes` is not applied. Instead, when `load.manageIndexes: true`, the load-time index manager disables the non-clustered indexes already present before the load (`indexes.disable`) and rebuilds them after (`indexes.rebuild`). The two paths are mutually exclusive.

Adapted from samples/csv/csv-desired-index.flow.yaml:

```yaml
name: Csv_DesiredIndex
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_DesiredIndex
desiredIndexes: |
  CREATE NONCLUSTERED INDEX IX_Csv_DesiredIndex_OrderId ON dbo.Csv_DesiredIndex (OrderId);
  CREATE NONCLUSTERED INDEX IX_Csv_DesiredIndex_Customer ON dbo.Csv_DesiredIndex (Customer) INCLUDE (Amount);
```

For the existing-table path, see samples/quickstart/orders.indexed.flow.yaml, which sets `load.manageIndexes: true` to disable non-clustered indexes before the load and rebuild them after. The manager leaves primary-key, unique-constraint, and clustered indexes intact (src/SqlFlow.SqlServer/SqlServerIndexManager.cs).

## preProcess and postProcess (ingestion flows)

Parsed as single nullable strings (src/SqlFlow.Yaml/IngestionYaml.cs) and mapped to the flow's process policy, `PreProcessOnTarget`/`PostProcessOnTarget` (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs, src/SqlFlow.Core/Ingestion/IngestionPolicies.cs). These carry the legacy `PreProcessOnTrg`/`PostProcessOnTrg` semantics.

Execution (src/SqlFlow.SqlServer/Ingestion/TargetProcessHooks.cs, src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs):

- The value is a raw T-SQL command, typically `EXEC [schema].[proc]`, executed verbatim as `CommandType.Text` with `CommandTimeout = 0` on the **target** connection, no parameters bound, outside the load transaction.
- **Activation gate**: a hook whose trimmed value is 2 characters or fewer is skipped (`TargetProcessHooks.ShouldRun` requires trimmed length greater than 2). This preserves the legacy activation contract.
- `preProcess` runs before the staging table is created, so before any new data exists anywhere. On a first-ever run the target table does not exist yet either; a pre-hook that touches the target must guard itself with `IF OBJECT_ID(...) IS NOT NULL`.
- `postProcess` runs after the load commits and before the staging table is dropped. A failure here does not roll back the committed load (the hook runs outside the load transaction), but it fails the run and keeps staging for debugging.

```sql
-- A safe ingestion pre-process hook body: guard for the first run.
IF OBJECT_ID('DW.raw.Orders') IS NOT NULL
    EXEC DW.dbo.usp_ArchiveOrders;
```

Integration coverage: tests/SqlFlow.Core.Tests/Integration/IngestionProcessHookIntegrationTests.cs.

## preInvoke and postInvoke (ingestion flows)

Each names an invoke declared in the document's `invokes:` block (an Azure Data Factory pipeline or an Azure Automation runbook; see the invoke reference for the block's fields). Resolution happens at parse time (src/SqlFlow.Yaml/YamlInvokeParts.cs, `ResolveHookAlias`): a blank value is treated as absent, and an unresolved name fails validation with

```text
<file>: 'preInvoke' references '<name>', which is not declared under 'invokes:'.
```

(same wording for `postInvoke`; verified in tests/SqlFlow.Core.Tests/YamlInvokeHooksTests.cs).

Execution (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs):

- `preInvoke` runs before any data work, as a prerequisite hook. A failure the invoke does not tolerate fails the flow before anything is staged or loaded.
- `postInvoke` runs after the load commits and before staging is dropped. A failure fails the run, but the committed load stands (and staging is kept for debugging).
- **Without-database (YAML) mode with no invoke runner wired**: a set alias is a clear error, never a silent skip. `NullInvokeRunner` (src/SqlFlow.Core/Invoke/IInvokeRunner.cs) throws: `PreInvokeAlias/PostInvokeAlias '<name>' is set, but the invoke subsystem (the flw.Invoke registry and the ADF/Automation dispatcher) is not available in this build.`

The `invokes:` block and the hook-alias validation are shared across the ing, exp, and sp document kinds through the same mapping code (src/SqlFlow.Yaml/YamlInvokeParts.cs), so the block fields and the error wording are identical in each. `preInvoke` exists only on `flowType: ing`; exp and sp documents accept `postInvoke` only.

## desiredIndexes (ingestion flows, under target)

In `flowType: ing` documents the declared-index script lives at `target.desiredIndexes` (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs). It is applied only on the run that created the target table (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs); on later runs the create-only manager is not invoked. A malformed script or a manager failure after the load commits is surfaced as a failed index action, never a rollback of the committed load.

## Fuller example

An ingestion flow using all the hook kinds together (adapted from samples/ingestion/orders-ingestion.flow.yaml):

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
  desiredIndexes: "CREATE NONCLUSTERED INDEX IX_Orders_OrderID ON DW.raw.Orders (OrderID);"

load:
  keyColumns: [OrderID]

preProcess: "IF OBJECT_ID('DW.raw.Orders') IS NOT NULL EXEC DW.dbo.usp_BeforeOrders"
postProcess: "UPDATE STATISTICS DW.raw.Orders"

preInvoke: gate-pipeline
postInvoke: notify-pipeline

invokes:
  gate-pipeline:
    type: adf
    pipeline: pl_gate
    servicePrincipal: deploy
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

Run order for this flow: `gate-pipeline` (pre-invoke), then the pre-process hook, then staging, schema sync, and the keyed load; after the commit, the post-process hook, then declared indexes (first run only), then `notify-pipeline` (post-invoke), then staging cleanup.

## See also

- [Invoke flows and the invokes block](inv.md)
- [Ingestion flows (flowType: ing)](ing.md)
- [Stored-procedure flows (flowType: sp)](sp.md)
- [Export flows (flowType: exp)](exp.md)
- [Load policy: mode, batch size, manageIndexes](load.md)
