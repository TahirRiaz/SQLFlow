---
id: flow-transform
title: "transform section: type inference and column transforms"
type: flow-reference
summary: "transform block shared by file and ing flows: inferTypes, onConvertError, threshold, sample, generateView, and authored per-column transforms with @ColName."
keywords:
  - transform
  - infertypes
  - columns
  - generateview
  - onconverterror
  - threshold
  - "@colname"
  - virtual
  - excludefromview
yamlPath: transform
related:
  - concept-type-inference
  - concept-pre-ingestion-transform
  - cli-infer
sourceRefs:
  - src/SqlFlow.Core/Model/TypeInference.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Core/Engine/ColumnTransformResolver.cs
  - src/SqlFlow.Core/Engine/TransformViewBuilder.cs
  - src/SqlFlow.Core/Engine/ColumnTransformExpression.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - schemas/sqlflow.flow.schema.json
---

# `transform` section: type inference and column transforms

The `transform` block is the pre-ingestion transform layer: it turns the raw (string-typed) landing table into a typed projection by combining datatype inference (`inferTypes`) with explicitly authored per-column transforms (`columns`). The resolved projection is materialized as a transformation view named `v<Table>` in the target's schema, refreshed as a post-process of every load with `CREATE OR ALTER VIEW`. Downstream chained flows read the view, not the raw table, which is how correctly typed data and dynamic schema evolution propagate down the chain. The exact same block shape and validation are shared by file flows and relational ingestion (`flowType: ing`) documents; both loaders map it through `YamlFlowLoader.MapInference` (src/SqlFlow.Yaml/YamlFlowLoader.cs, src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs).

Minimal working example (adapted from samples/lineage-demo/11-land-customers.flow.yaml):

```yaml
name: demo-land-customers
source:
  type: csv
  location: data/customers.csv
target:
  connection: ${env:SQLFLOW_DEMO_DB}
  schema: demo
  table: Customers_Pre
transform:
  inferTypes: true
```

This lands `customers.csv` into `demo.Customers_Pre` as raw strings, profiles the loaded table, and refreshes `demo.vCustomers_Pre` with each column cast to its inferred type.

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `transform.inferTypes` | bool | no | `false` | Enable datatype inference over the loaded raw columns. |
| `transform.onConvertError` | string | no | `silent-null` (see below) | What to do with values that do not convert: `silent-null`, `fail`, or `keep-string`. |
| `transform.threshold` | double | no | `1.0` | Fraction of non-null sampled values that must convert before a type is chosen (`1.0` = all). |
| `transform.sample` | int | no | `0` | Rows to sample when profiling; `0` = full scan. |
| `transform.preserveLeadingZeros` | bool | no | `true` | Keep integer-looking values with significant leading zeros (zip codes, IDs) as string. |
| `transform.generateView` | bool | no | `true` | Generate the typed transformation view `[schema].[v<Table>]` as a post-process of the load. |
| `transform.columns` | list | no | `[]` | Authored per-column transforms; each overrides inference for its column. |

Per entry under `transform.columns`:

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `name` | string | yes | none | The raw source column the transform applies to; for a `virtual` column, the computed column's own name. Unique case-insensitively. |
| `expr` | string | no (yes when `virtual`) | none | SQL expression producing the value. The token `@ColName` is substituted with the bracket-quoted source column. |
| `as` | string | no | `name` | Output column name in the view. The raw table keeps `name`; the view exposes the alias. |
| `type` | string | no | none | Declared target SQL type, e.g. `varchar(50)`, `decimal(18,2)`. With no `expr`, the transform is `CAST(@ColName AS type)`. |
| `order` | int | no | none | Position in the generated view; lower first. Unset entries keep their natural position. |
| `virtual` | bool | no | `false` | A computed column with no raw source counterpart. Requires `expr`; `@ColName` is rejected. |
| `excludeFromView` | bool | no | `false` | Drop the column from the view's final projection. |

## `transform.inferTypes`

Default `false`. When `true`, the runner profiles the just-loaded table (after the load, so evolved columns are included) and picks a SQL type per raw column; the chosen types are, by construction, convertible by SQL Server because the profiler counts what `TRY_CONVERT` accepts. Inference fills only the columns that no `transform.columns` entry names; an authored transform always wins for its column (`ColumnTransformResolver` in src/SqlFlow.Core/Engine/ColumnTransformResolver.cs). Columns that are neither authored nor inferred pass through untyped.

In a `flowType: ing` document, `inferTypes: true` requires an inference service wired into the runner. Without one the run fails with: `transform.inferTypes is on, but no inference service is wired into this runner. Register one (the engine host does by default), or declare the transforms explicitly under transform.columns.` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs).

## `transform.onConvertError`

Maps to the `ConvertErrorMode` enum (src/SqlFlow.Core/Model/TypeInference.cs):

| Value | Behavior |
| --- | --- |
| `silent-null` | Bad values become `NULL` (uses `TRY_CONVERT`). Lenient. |
| `fail` | Bad values raise an error (uses `CONVERT`). Strict, fail loudly. |
| `keep-string` | Only convert columns that are 100% convertible; otherwise keep the raw string. |

Tokens are normalized by stripping `-` and `_` before a case-insensitive enum parse, so `silent-null`, `silentNull`, `keep_string`, and `keepstring` all parse. An invalid token fails validation with: `'transform.onConvertError' has invalid value '<value>'. Allowed: SilentNull, Fail, KeepString.` The message is prefixed with the source file, and `<value>` is the normalized token (hyphens and underscores already stripped).

Default divergence, verified in code: when the flow document has no `transform:` block at all, the model default applies and `OnConvertError` is `Fail` (`TypeInferencePolicy` in src/SqlFlow.Core/Model/TypeInference.cs). When the `transform:` block is present but `onConvertError` is omitted, the loader falls back to `SilentNull` (src/SqlFlow.Yaml/YamlFlowLoader.cs, `MapInference`). Set the key explicitly if the difference matters. Note also that the editor schema (schemas/sqlflow.flow.schema.json) currently enumerates only `silent-null` and `fail`; the loader additionally accepts `keep-string`.

`onConvertError` governs the expressions inference emits. An authored type-only column transform always emits a plain `CAST(@ColName AS type)` regardless of this setting.

## `transform.threshold`

Default `1.0`. The fraction of non-null sampled values that must convert to a candidate type before that type is chosen; `1.0` means every sampled value must convert. Lowering it lets inference pick a type for a mostly-clean column, with `onConvertError` deciding what happens to the stragglers.

## `transform.sample`

Default `0`, meaning full scan. A positive value limits profiling to that many rows.

## `transform.preserveLeadingZeros`

Default `true`. Integer-looking values with significant leading zeros (zip codes, account numbers) stay string instead of being inferred as a numeric type.

## `transform.generateView`

Default `true`. The view is generated only when `generateView` is true AND there is something to project: inference is enabled or `columns` is non-empty (`TypeInferencePolicy.GeneratesView` in src/SqlFlow.Core/Model/TypeInference.cs). A flow with `generateView: true` but neither `inferTypes` nor authored columns generates nothing. Set `generateView: false` to compute nothing and skip the post-process entirely.

When it runs, the view refresh is a post-process of the load in both runners: stage `transform.view` in the file-flow `FlowRunner` (src/SqlFlow.Core/Engine/FlowRunner.cs) and step 8b of the relational `IngestionFlowRunner` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs). A failure here fails the run, because downstream chained flows read the view and a stale view must be loud; the committed load itself stands, and since the DDL is `CREATE OR ALTER VIEW`, a re-run regenerates the view idempotently without re-reading source data. The resolved projection rides back on the result (`TransformViewResult { ViewName, Ddl, Columns }`) for the run log and the catalog.

## `transform.columns`

Authored per-column transforms, the source of truth. A faithful port of the legacy `flw.PreIngestionTransform` rows: `name` = ColumnName, `expr` = SelectExp, `as` = ColumnAlias, `type` = DataType, `order` = ColumnSortOrder, `virtual` = Virtual, `excludeFromView` = ExcludeFromView. The declared transforms are also projected into catalog `PipelineColumn` rows with kind `declared` on every pipeline sync, so the estate is queryable for which transformations are set on a pipeline (src/SqlFlow.Catalog/CatalogEntities.cs).

### `name`

Required, trimmed, unique case-insensitively across the list. For a non-virtual entry it names the raw source column; for a `virtual` entry it names the computed output column itself.

Validation errors (src/SqlFlow.Yaml/YamlFlowLoader.cs, exact messages, prefixed with the source file):

- Missing name: `transform.columns[<i>] is missing 'name'.`
- Duplicate: `transform.columns has a duplicate column '<name>'.`

### `expr` and the `@ColName` token

A SQL expression producing the column's value. The token `@ColName` is substituted with the bracket-quoted reference to `name` (for example `[vehicle_type]`), so renaming the source column only touches `name`. Matching is case-insensitive and token-aware, using the regex `(?<![A-Za-z0-9_@])@ColName(?![A-Za-z0-9_])` (src/SqlFlow.Core/Engine/ColumnTransformExpression.cs): a distinct T-SQL variable such as `@ColNameFoo` is left untouched.

When both `expr` and `type` are set, the expression is used verbatim and the type is recorded as its declared result type. When only `type` is set, the generated expression is `CAST(@ColName AS <type>)`.

### `as`

The output column name in the generated view. Null keeps `name`. This is a rename: the raw table keeps `name`, the typed view (and everything downstream) exposes the alias.

### `type`

The declared target SQL type (for example `varchar(50)`, `decimal(18,2)`). With no `expr` it drives a straight cast; with an `expr` it documents the expression's result type and is carried into the catalog projection.

### `order`

Position in the generated view. The resolver sorts by `order` when set, else by the column's natural position, with natural position as the tiebreaker (src/SqlFlow.Core/Engine/ColumnTransformResolver.cs). An entry with a low `order` value moves ahead of naturally positioned columns whose position index is higher.

### `virtual`

A computed column with no raw counterpart. It must declare an `expr`, and that expression cannot use `@ColName` (there is no source column); reference other columns by name instead. Virtual columns, and any authored transform whose `name` is not a raw column, are appended after the raw columns in declaration order, then the `order` sort applies.

Validation errors:

- Virtual without expr: `transform.columns['<name>'] is virtual and must declare an 'expr'.`
- Virtual using the token: `transform.columns['<name>'] is virtual, so its 'expr' cannot use @ColName (there is no source column); reference other columns by name.`

A non-virtual column with neither `expr` nor `type` is rejected as a no-op: `transform.columns['<name>'] does nothing - declare an 'expr', a 'type' (to cast), or mark it 'virtual'.`

### `excludeFromView`

Drops the column from the view's final projection. The entry is recorded as handled, so inference and pass-through do not re-add it: the raw column stays in the landing table but never appears in the view, and an excluded entry's `expr`/`type` are not emitted in the view DDL at all (they are still projected into the catalog's declared rows). A virtual column's expression can still reference the excluded raw column by name, because the view selects from the raw table. Useful to hide a raw column from downstream consumers, for example one that only exists to feed a virtual column. A non-virtual entry must still declare an `expr` or a `type` to pass the no-op validation, even when excluded.

## Resolution precedence and the generated view

`ColumnTransformResolver.Resolve` merges the three typing sources into one ordered projection:

1. Raw columns first, in source order: the authored transform for the column if one exists, else the inferred column (when `inferTypes` ran), else an untyped pass-through (`[Column]` selected as-is).
2. Virtual and derived authored columns (names not present in the raw table) appended after, in declaration order.
3. `excludeFromView` entries dropped from the projection.
4. Final ordering by `order` when set, else natural position.

`TransformViewBuilder.Build` (src/SqlFlow.Core/Engine/TransformViewBuilder.cs) then emits:

```sql
CREATE OR ALTER VIEW [demo].[vOrders_Pre]
AS
SELECT
    UPPER(CAST([vehicle_type] AS varchar(50))) AS [vehicle_type_clean],
    CAST([unit_price] AS decimal(18,2)) AS [unit_price]
FROM [demo].[Orders_Pre];
```

An empty resolved projection emits `SELECT *` (a pass-through view). All identifiers are bracket-quoted with `]` escaped as `]]`.

## Fuller example

Adapted from samples/lineage-demo/10-land-orders.flow.yaml, extended with a virtual column and a hidden intermediate:

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
  onConvertError: silent-null
  columns:
    - name: vehicle_type
      expr: "UPPER(CAST(@ColName AS varchar(50)))"
      as: vehicle_type_clean
      type: varchar(50)
    - name: unit_price
      type: decimal(18,2)
      order: 1
    - name: internal_batch_code
      type: varchar(20)
      excludeFromView: true
    - name: order_year
      virtual: true
      expr: "YEAR(order_date)"
      type: int
```

Here `vehicle_type` and `unit_price` are authored (inference does not touch them), `internal_batch_code` never appears in the view (its `type` satisfies validation and is recorded in the catalog, but no cast is emitted), `order_year` is a computed column appended after the raw columns, and every remaining column gets its inferred type. The run refreshes `demo.vOrders_Pre`; the downstream chained ingestion flow reads the view.

## See also

- [flow overview](./overview.md)
- [schema section](./schema.md)
- [load section](./load.md)
- [infer CLI command](../cli/infer.md)
