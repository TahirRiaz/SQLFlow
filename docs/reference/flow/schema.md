---
id: flow-schema
title: "File flow: schema section (evolve, defaultColumnType, overrides)"
type: flow-reference
summary: "schema block of a flow YAML: evolution policy (create, widen, strict), default SQL type for raw columns, and per-column type/nullability overrides."
keywords:
  - schema.evolve
  - create
  - widen
  - strict
  - defaultcolumntype
  - overrides
  - schema evolution
  - schema drift
yamlPath: schema
related:
  - concept-schema-evolution
  - flow-load
  - cli-plan
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Core/Engine/SchemaDiffer.cs
  - src/SqlFlow.Core/SqlFlowException.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - schemas/sqlflow.flow.schema.json
---

# schema

The `schema` section controls how the target table's shape is created and kept in sync with the source: the evolution policy (`evolve`), the SQL type used for raw untyped columns at create time (`defaultColumnType`), and per-column type or nullability pins (`overrides`). The whole section is optional; omitting it gives you `evolve: widen`, no overrides, and `varchar(255)` as the default column type unless `source.options.defaultColDataType` overrides it (record `SchemaPolicy` in src/SqlFlow.Core/Model/FlowDefinition.cs).

```yaml
name: Csv_CustomType
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_CustomType
schema:
  evolve: widen
  defaultColumnType: nvarchar(4000)
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `evolve` | enum: `create`, `widen`, `strict` | no | `widen` | How to reconcile the desired schema with the live target table. |
| `defaultColumnType` | string (SQL type) | no | `varchar(255)` | SQL type for raw untyped string columns when creating or widening the target. |
| `overrides` | map of column name to `{type, nullable}` | no | empty | Per-column type and nullability pins; keys match column names case-insensitively. |

## schema.evolve

Allowed values are `create`, `widen`, and `strict`, matched case-insensitively (`Enum.TryParse` with `ignoreCase: true` in src/SqlFlow.Yaml/YamlFlowLoader.cs). An unrecognized value fails validation at load time with:

```text
<file>: 'schema.evolve' has invalid value '<value>'. Allowed: Create, Widen, Strict.
```

The policy is applied by `SchemaDiffer.Diff` (src/SqlFlow.Core/Engine/SchemaDiffer.cs). When the target table does not exist, every policy creates it with all desired columns. When the table exists, the engine computes the set of desired columns missing from the target (compared case-insensitively by name) and then:

- `create`: never alters the existing table; missing columns are ignored.
- `widen`: adds the missing columns to the existing table (`ColumnsToAdd`).
- `strict`: succeeds only if no columns are missing; otherwise it throws `SchemaDriftException` (src/SqlFlow.Core/SqlFlowException.cs) with the message:

```text
Schema drift on [<schema>].[<table>]: target is missing <n> column(s) (<names>) and the evolve policy is 'strict'.
```

No policy ever drops or retypes existing target columns; the diff only adds.

`widen` pairs naturally with folder loads: when `source.location` is a directory, the columns of all matched files are unioned and rows are NULL-filled for columns their file lacks (samples/csv/csv-folder-union.flow.yaml), so different versions of a feed land in one table and newly-seen columns are added on the fly.

## schema.defaultColumnType

The SQL type assigned to raw untyped string columns when the engine creates target columns. Resolution order (`ResolveDefaultColumnType` in src/SqlFlow.Yaml/YamlFlowLoader.cs):

1. `schema.defaultColumnType`, when set and non-blank.
2. `source.options.defaultColDataType`, when the schema key is absent (the legacy SQLFlow `DefaultColDataType` convention carried in source metadata).
3. The built-in default `varchar(255)`.

The resolved value is only used for string columns with no usable inferred length. In `SqlServerTypeMapper.MapClrType` (src/SqlFlow.SqlServer/SqlServerTypeMapper.cs), a string column with a known `MaxLength` between 1 and 4000 becomes `NVARCHAR(<len>)`, above 4000 becomes `NVARCHAR(MAX)`, and only a column with no length information falls back to `defaultColumnType`. Non-string CLR types map to fixed SQL types (`INT`, `BIGINT`, `DATETIME2`, and so on) regardless of this setting.

Practical values seen in the samples: `nvarchar(4000)` for JSON payloads (samples/json/json-schema-evolution.flow.yaml) and `nvarchar(max)` for keep-subtree flows that store whole JSON or XML fragments in one column (samples/json/json-keep-subtree.flow.yaml, samples/xml/xml-keep-subtree.flow.yaml).

Note that reading a non-UTF-8 file is separate from storing its characters: `source.options.srcEncoding` controls decoding, but preserving Cyrillic and other non-Latin text end to end requires an `nvarchar` default column type, since the built-in `varchar(255)` loses those characters (samples/csv/csv-encoding-utf16.flow.yaml).

## schema.overrides

A map keyed by column name; keys are matched case-insensitively (the loader builds the dictionary with `StringComparer.OrdinalIgnoreCase`). Each value has two optional fields (record `ColumnOverride`):

| Field | Type | Description |
| --- | --- | --- |
| `type` | string | Explicit SQL type for this column, used verbatim (trimmed). |
| `nullable` | boolean | Whether the column is nullable; when omitted, the source column's nullability is used. |

Type precedence in `SqlServerTypeMapper.Map` (src/SqlFlow.SqlServer/SqlServerTypeMapper.cs):

1. The authored override `type` wins.
2. Otherwise a `SqlType` the source declared explicitly for the column (for example an injected hash-key column).
3. Otherwise CLR-to-SQL inference, with `defaultColumnType` as the string fallback described above.

```yaml
schema:
  evolve: widen
  overrides:
    OrderId:
      type: BIGINT
      nullable: false
```

## Full example

Adapted from samples/quickstart/orders.flow.yaml:

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
  options:
    delimiter: ","
    header: true
target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders
schema:
  evolve: widen                    # create | widen | strict
  defaultColumnType: varchar(255)  # SQL type for raw untyped columns
  overrides:
    OrderId:
      type: BIGINT
      nullable: false
load:
  mode: append
```

Preview the schema delta and the generated DDL the policy produces without changing anything:

```bash
sqlflow plan orders.flow.yaml
```

## See also

- [Schema evolution concept](../concepts/schema-evolution.md)
- [load section](load.md)
- [plan command](../cli/plan.md)
