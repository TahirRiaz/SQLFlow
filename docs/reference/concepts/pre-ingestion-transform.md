---
id: concept-pre-ingestion-transform
title: Pre-ingestion transform views and chained-flow topology
type: concept
summary: How the typed v<Table> view is generated over the pre table, and how a downstream ingestion flow reads it to carry types into raw and target.
keywords:
  - v<table> view
  - typed view
  - chained flows
  - create or alter view
  - transform resolution
  - precedence
  - pre table
  - pass-through
related:
  - flow-transform
  - concept-type-inference
  - guide-lineage-demo
  - concept-shadow-catalog
sourceRefs:
  - src/SqlFlow.Core/Model/TypeInference.cs
  - src/SqlFlow.Core/Model/Results.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Core/Engine/ColumnTransformResolver.cs
  - src/SqlFlow.Core/Engine/TransformViewBuilder.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Catalog/CatalogProjection.cs
---

# Pre-ingestion transform views and chained-flow topology

A pre-ingestion transform is the typed view a landing flow refreshes over its just-loaded raw table. Files land as strings in a pre table; the view named `v<Table>` projects those strings through casts, expressions, and renames so every downstream consumer sees correctly typed data without a second physical copy. The composition is chained flows: the landing flow ends at the pre table plus the view, and a separate downstream ingestion flow reads the view (not the table) as its source into raw/target through the existing staging and keyed-upsert machinery. No new load path exists for the typed hop; it reuses the standard ingestion flow.

## Topology

```text
file --> pre table (raw strings)          landing flow (file), or external-DB ing flow
             |
             v
         [schema].[v<Table>]              refreshed by the landing flow as a post-process
             |
             v
         raw / target table               downstream ing flow reads THE VIEW as its source
```

The two flows are separate documents. In samples/lineage-demo, `10-land-orders.flow.yaml` lands `data/orders.csv` into `demo.Orders_Pre` and refreshes `demo.vOrders_Pre`; `20-ing-orders.flow.yaml` is an `ing` flow whose source object is `SqlFlowCatalogTests.demo.vOrders_Pre` and whose target is `demo.Orders`. Reading the view is also what connects the two flows in lineage (see below).

## When the view is generated

`TypeInferencePolicy.GeneratesView` (src/SqlFlow.Core/Model/TypeInference.cs) gates the post-process:

```text
GeneratesView = GenerateView && (Enabled || Columns.Count > 0)
```

That is: `transform.generateView` is on (the default) AND there is something to project, either type inference (`transform.inferTypes: true`) or at least one authored entry under `transform.columns`. A flow with no `transform` block generates nothing.

Two runners share the post-process:

- File flows (src/SqlFlow.Core/Engine/FlowRunner.cs): trace stage `transform.view`, running after `source.complete`. The ordering is deliberate: a view failure never leaves files un-finalized. The load stands, the files are marked ingested, and a re-run regenerates the view without re-reading anything. The failure still fails the run, because downstream flows read the view and a stale one must be loud.
- Ingestion flows (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs, step 8b): the same post-process runs after staging cleanup when `flow.Transform.GeneratesView`. `IngestionFlow.Transform` gives external-database landings the same policy: the flow's target is the pre/staging table and the downstream chained flow reads the view. A native SQL-to-SQL flow leaves `Transform` at its default (inference off, no columns) and generates nothing. If `transform.inferTypes` is on but no inference service is wired into the runner, the run fails with: `transform.inferTypes is on, but no inference service is wired into this runner. Register one (the engine host does by default), or declare the transforms explicitly under transform.columns.`

In both runners the success log line is `transformation view [schema].[vTable] refreshed (N column(s), M typed)`.

## Naming, refresh, and schema evolution

The view is named `v` plus the flow's target table (`Orders_Pre` gets `vOrders_Pre`), created in the same database and schema as the target, via `CREATE OR ALTER VIEW`. The statement is idempotent and executed on every run, and the runner introspects the just-loaded table fresh before building it, so evolved columns are included. That refresh is the mechanism by which dynamic schema evolution propagates pre -> view -> raw -> target: a new column appears in the pre table, the next run's view projects it, and the downstream ingestion flow's schema sync carries it into raw/target.

## How the projection is resolved

`ColumnTransformResolver.Resolve` (src/SqlFlow.Core/Engine/ColumnTransformResolver.cs) merges three sources with fixed precedence:

1. An authored `transform.columns` entry wins over inference for the same column (matched case-insensitively).
2. Inference fills the columns no author named (when `transform.inferTypes` is on).
3. Every remaining column passes through untyped: `[col]` selected as-is, no cast.

Ordering: raw columns are emitted first in source order; virtual columns and authored transforms whose name is not a raw column append after them in declaration order; the final order is by `order` (SortOrder) when set, then natural position, with a stable tie-break by natural position. Columns marked `excludeFromView` are recorded as emitted (so inference or pass-through cannot re-add them) but dropped from the projection.

Expression construction for an authored entry:

- Type only, no `expr`: `CAST([source] AS type)`.
- `expr` present: the `@ColName` token (case-insensitive) is substituted with the quoted source column reference.
- `virtual: true`: the expression is used verbatim; YAML validation already rejected `@ColName` there because a virtual column has no source column.
- The output alias defaults to the source name when `as` is not set.

The resolver's output type is `InferredColumn { ColumnName, DataType, SelectExpression, Converted, Style, NumericFormat }` (src/SqlFlow.Core/Model/TypeInference.cs); `Style` is the CONVERT style code for date formats and `NumericFormat` records the normalization convention for numeric columns.

## The emitted DDL

`TransformViewBuilder.Build` (src/SqlFlow.Core/Engine/TransformViewBuilder.cs) is pure string construction (no database access) and emits exactly:

```sql
CREATE OR ALTER VIEW [schema].[vTable]
AS
SELECT
    <expr> AS [col],
    <expr> AS [col2]
FROM [schema].[Table];
```

An empty projection emits `SELECT *` (a pass-through view). The builder accepts an optional `whereClause` parameter appended verbatim as `WHERE <clause>` (the legacy PreFilter); both runners currently call `Build` without one. The `]` character in identifiers is escaped as `]]` everywhere.

## Configuration touchpoints

The `transform` block on a landing flow (mapped by src/SqlFlow.Yaml/YamlFlowLoader.cs) controls everything:

| Key | Default | Effect |
| --- | --- | --- |
| `transform.inferTypes` | `false` | Detect types for columns no authored transform names |
| `transform.generateView` | `true` | `false` skips the view post-process entirely |
| `transform.onConvertError` | `silentNull` | `silentNull`, `fail`, or `keepString` conversion handling |
| `transform.threshold` | `1.0` | Fraction of non-null values that must convert before a type is chosen |
| `transform.sample` | `0` | Rows to sample when profiling; `0` is a full scan |
| `transform.preserveLeadingZeros` | `true` | Keep integer-looking values with significant leading zeros as string |
| `transform.columns` | empty | Authored per-column transforms (see below) |

The `silentNull` default for `transform.onConvertError` is the YAML loader's fallback when a `transform` block is present but the key is omitted (`YamlFlowLoader.MapInference`, src/SqlFlow.Yaml/YamlFlowLoader.cs); `TypeInferencePolicy.OnConvertError` itself defaults to `Fail` (src/SqlFlow.Core/Model/TypeInference.cs) when constructed outside YAML loading.

Each `transform.columns` entry carries: `name` (source column, or the computed column's own name when virtual), `expr` (T-SQL with `@ColName` substituted), `as` (output alias), `type` (declared type; without `expr` this means a plain cast), `order` (position in the view), `virtual`, and `excludeFromView`. Validation fails the load with a clear message for a missing name, a duplicate name, a virtual column without `expr` or with `@ColName` in its expression, and a plain column with neither `expr` nor `type`.

There is no dedicated CLI command for the view: it is a post-process of `sqlflow run <flow.yaml>`. The `sqlflow infer` command runs the same inference service standalone against an already-loaded table and prints the report as JSON (`--out <path>` writes it to a file, `--no-validate` skips validation).

## Failure semantics

A view-generation failure fails the run, but the committed load stands. In file flows the files are already finalized (the stage runs after `source.complete`); in ing flows the upsert has already committed and staging cleanup has run. Because `CREATE OR ALTER` is idempotent, a re-run regenerates the view without re-loading data.

## Result, catalog, and lineage

The resolved projection rides back on the run result as `TransformViewResult { ViewName, Ddl, Columns }` (src/SqlFlow.Core/Model/Results.cs), exposed as `FlowResult.TransformView` on file flows and `IngestionRunResult.TransformView` on ing flows. It is serialized into the run record and read back from `result.transformView.columns` in run.json by `CatalogProjection.PipelineColumnsDetected` (src/SqlFlow.Catalog/CatalogProjection.cs), which turns each view column into a detected pipeline-column row. Authored `transform.columns` entries are projected separately as declared rows, so the catalog records both what the YAML declared and what the engine actually applied.

For lineage, `FlowSetCollector` (src/SqlFlow.Lineage/Collection/FlowSetCollector.cs) declares the view as WRITTEN by the landing flow (node kind View) whenever `GeneratesView` is true. Since the downstream ingestion flow declares a read of the same view, the graph orders `landing -> view -> downstream ingestion` into correct execution waves.

## Example

The landing flow (adapted from samples/lineage-demo/10-land-orders.flow.yaml):

```yaml
name: demo-land-orders
source:
  type: csv
  location: data/orders.csv
target:
  connection: ${env:SQLFLOW_DEMO_DB}
  schema: demo
  table: Orders_Pre
transform:
  inferTypes: true
  columns:
    - name: vehicle_type
      expr: "UPPER(CAST(@ColName AS varchar(50)))"
      as: vehicle_type_clean
      type: varchar(50)
```

Every run refreshes `demo.vOrders_Pre`: `vehicle_type` gets the authored expression aliased to `vehicle_type_clean`, inference types the remaining columns, and anything inference cannot type passes through as-is.

The downstream typed hop (samples/lineage-demo/20-ing-orders.flow.yaml) reads the view:

```yaml
flowType: ing
name: demo-ing-orders
connections:
  src: ${env:SQLFLOW_DEMO_DB}
  dwh: ${env:SQLFLOW_DEMO_DB}
source:
  server: src
  object: SqlFlowCatalogTests.demo.vOrders_Pre
target:
  server: dwh
  object: SqlFlowCatalogTests.demo.Orders
load:
  keyColumns: [order_id]
```

Integration coverage lives in tests/SqlFlow.Core.Tests/Integration/TransformViewIntegrationTests.cs (file flows), tests/SqlFlow.Core.Tests/Integration/IngestionTransformViewIntegrationTests.cs (ing flows), and tests/SqlFlow.Core.Tests/ColumnTransformTests.cs (resolver and builder).

## See also

- [transform block reference](../flow/transform.md)
- [Type inference](../concepts/type-inference.md)
- [Lineage demo walkthrough](../guides/lineage-demo.md)
- [Shadow catalog](../concepts/shadow-catalog.md)
