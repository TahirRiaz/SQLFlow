---
id: flow-sch
title: "Schema registration flow (flowType: sch)"
type: flow-reference
summary: "flowType: sch registers an external database's tables and views in the catalog so subscribers link to them and questions know where to run."
keywords:
  - schema registration
  - flowtype sch
  - external database
  - registered source
  - metadata only
  - registers relation
  - subscriber linking
  - datasource inference
  - powerai
  - chat with data
yamlPath: "(root, flowType: sch)"
related:
  - flow-overview
  - flow-schedule
  - flow-subscribers
  - concept-shadow-catalog
  - concept-lineage-graph-and-plan
  - concept-data-operations
sourceRefs:
  - src/SqlFlow.Yaml/YamlSchemaRegistrationFlowLoader.cs
  - src/SqlFlow.Core/SchemaRegistration/SchemaRegistrationFlow.cs
  - src/SqlFlow.Core/SchemaRegistration/SchemaRegistrationResult.cs
  - src/SqlFlow.SqlServer/Catalog/SqlServerObjectHarvester.cs
  - src/SqlFlow.SqlServer/Catalog/SchemaRegistrationReader.cs
  - src/SqlFlow.SqlServer/SchemaRegistration/SchemaRegistrationRunner.cs
  - src/SqlFlow.Lineage/Collection/FlowDocumentHeaders.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/SchemaRegistrationCollector.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.Catalog/RunQueueStore.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceInference.cs
  - src/SqlFlow.Execution/DocumentExecutor.cs
---

# Schema registration flow (flowType: sch)

A `flowType: sch` document registers the tables and views of one SQL Server database in the shadow catalog. Each object is recorded exactly as an object a flow manages would be: its columns, its primary key, and its script (the reconstructed `CREATE TABLE`, or the view's definition). Nothing is ever loaded from that database, and the flow never takes part in an execution wave. It is a metadata fetch.

It exists so a database SQLFlow does not load can still be queried. The two halves of that are split on purpose:

- **The registration knows where the data is.** Every registered object is linked to the flow by a `Registers` lineage edge, and the flow's connection is a declared datasource. A question whose tables were registered runs on that connection, in that database, with no one naming either.
- **The subscriber knows how the data fits together.** Joins, measures, and computed values come from the dashboard that reads the tables (a Power BI report's relationships, DAX measures, and calculated columns; see [subscribers.yaml](subscribers.md)). A registration therefore reads no foreign keys, no procedures or functions, no synonyms, and never parses a view's body.

## Minimal example

```yaml
flowType: sch
name: adventureworks_00_sch
connections:
  aw: ${env:SQLFLOW_ADVENTUREWORKS_DB}
source:
  server: aw
```

This registers every table and view in the connection's default database.

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `flowType` | string | yes | | Must be `sch` (case-insensitive, trimmed). |
| `name` | string | yes | | The flow name: the pipeline's identity, its run-history folder, and the flow every `Registers` edge is attributed to. By convention `<source>_00_sch`. |
| `description` | string | no | null | Free-text description. |
| `batch` | string | no | null | Grouping label; by convention `sch`. No effect on scheduling. |
| `lifecycle` | string | no | production | `production` or `development`. |
| `mode` | string | no | auto | `auto`, `manual`, or `disabled`, as on every flow. |
| `schedule` | map/string/list | no | none | The document-envelope schedule. A scheduled registration keeps the catalog current as the database changes. |
| `connections` | map | no | | Document-local named connections. |
| `source` | map | yes | | The database to register: exactly one of `server` or `connection`, plus optional `provider` and `database`. The resolved connection must be SQL Server (`mssql` or `azdb`). |
| `source.server` | string | one of server/connection | | Name of a connection declared under `connections:`. |
| `source.connection` | string | one of server/connection | | An inline connection string or `${...}` reference; synthesizes a connection named `source`. |
| `source.provider` | string | no | mssql | Provider of an inline `source.connection`. |
| `source.database` | string | no | null | The database to register. Set, the run switches the connection to it and verifies `DB_NAME()` before reading. Absent, the connection's own database is registered. |
| `objects.includeSchemas` | list | no | every schema | Only tables and views in these schemas are registered. |
| `objects.excludeSchemas` | list | no | none | Schemas skipped, applied after `includeSchemas`. |

Schema names are trimmed, unbracketed (`[sales]` is `sales`), de-duplicated, and compared case-insensitively, in the SQL too, so a case-sensitive database collation does not change the result. A schema named in both lists is refused at parse time.

## What is registered

For each table (`sys.objects` type `U`) and view (type `V`) in scope that is not `is_ms_shipped`:

| Recorded | Catalog column | Notes |
| --- | --- | --- |
| Schema, name, kind | `catalog.Object` | Kind `Table` or `View`. |
| Columns | `catalog.ObjectColumn` | Ordinal, rendered type (`nvarchar(100)`, `decimal(18,2)`), nullability; tier `Derived`. |
| Primary key | `catalog.Object.KeyColumns` | Key order; `KeyOrigin` `Constraint`. A view has none. |
| Script | `catalog.Object.Script` / `Definition` | A table's reconstructed `CREATE TABLE` (columns with identity, computed expressions, nullability, and the primary key) as `Script`; a view's stored text as `Definition`. An encrypted view is registered with its columns and without its text, with a warning. |
| Registration | `catalog.LineageEdge` | One `Registers` edge from the flow to each object, tier `Derived`. |

The same reader, `SqlServerObjectHarvester`, serves the derived lineage tier, so a table is described identically whether a registration or a connected sync recorded it. Registered objects are not added to the semantic layer: the column allow-list stays default-deny, and an admin allows the columns a question may read.

## Run pipeline

1. Resolve the connection and read the tables and views in scope, set-based (one query per kind of fact, never one per object).
2. Write `run.json` with the registered objects under `result`. A result larger than the 64 MB run-artifact limit fails the run and says to narrow `objects.includeSchemas`; it is never truncated.
3. Record the run. Every write-back path (a worker completing a queued run, `sqlflow run` with a catalog, a repo sync reading run history) upserts the objects and columns through the same object write a lineage sync uses. A worker or CLI write-back also replaces the flow's `Registers` edges with the objects it read now. A repo sync leaves the edges to its own lineage pass.
4. When the registered set changed, a worker completion queues the repo's managed sync, so subscribers re-link to new tables without a manual sync.

A failed run records nothing, and a dropped table keeps its `catalog.Object` row but loses its `Registers` edge, so it no longer resolves or runs.

## Catalog, scheduling, and lineage

- **Pipeline.** Kind `sch`; source server is the connection reference; target server is the file system. That source server is what makes the connection a declared datasource (see [Data operations](../concepts/data-operations.md)).
- **Waves.** A registration moves no data, so it never joins an execution wave and nothing depends on it. `Registers` edges are ignored by run-order computation, traversal, and object levels.
- **Connected sync.** `sqlflow db sync --connect` (and the managed sync) reads each declared registration with the same reader and emits its `Registers` edges. A registration whose database is unreachable is reported, and the edges an earlier pass recorded for it are kept. Edges of a registration the repository no longer declares are dropped.
- **Subscribers.** A subscriber library that declares no `connections:` is a registered-source library: its queries and its report's model tables are matched to registered objects by database, schema, and name (schema and name when the reference names no database), and re-keyed under the registering flow's connection. Registrations made by any repository count. No match, or the same name registered in two databases, leaves the read unlinked with a warning. See [subscribers.yaml](subscribers.md).
- **Where a question runs.** Datasource inference looks at `Registers` edges first: objects an active registration registered run on its source server, in the database they were registered in. See [Data operations](../concepts/data-operations.md).

## CLI

```
sqlflow validate adventureworks_00_sch.yaml       # OK  'adventureworks_00_sch' is valid (schema registration: ...)
sqlflow run adventureworks_00_sch.yaml            # OK  registered 6 table(s) and 3 view(s) (...) from 'AdventureWorks'
sqlflow db sync . --repo aw --connect             # registers through the connected pass as well
```

## Full example

```yaml
flowType: sch
name: adventureworks_00_sch
batch: sch
description: AdventureWorksDW2022 tables and views, registered for Power BI subscribers and PowerAI
connections:
  aw: ${env:SQLFLOW_ADVENTUREWORKS_DB}
source:
  server: aw
  database: AdventureWorks
objects:
  includeSchemas: [dbo]
schedule:
  name: adventureworks_daily
  cron: "0 4 * * *"
  timezone: Europe/Oslo
```

## See also

- [subscribers.yaml](subscribers.md): registered-source subscribers.
- [Data operations](../concepts/data-operations.md): how a question's datasource is decided.
- [Source-control flow](scm.md): the other metadata-only flow, which snapshots DDL into git instead.
