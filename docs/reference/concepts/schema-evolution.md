---
id: concept-schema-evolution
title: "Schema evolution: diffing, monotonic widening, and DDL apply"
type: concept
summary: How SQLFlow diffs desired vs live schemas, widens types monotonically, classifies rewrite cost, and applies DDL under app locks.
keywords:
  - schema drift
  - widen
  - sqldatatype
  - type families
  - table rewrite
  - allowtablerewrite
  - app locks
  - type mapping
  - monotonic widening
  - ddl
related:
  - flow-schema
  - flow-ing-schema-incremental
  - cli-plan
  - concept-ingestion-run-pipeline
sourceRefs:
  - src/SqlFlow.Core/Engine/SchemaDiffer.cs
  - src/SqlFlow.Core/Engine/DesiredSchemaBuilder.cs
  - src/SqlFlow.Core/Model/Schema.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Core/Model/DdlBatch.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs
  - src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - src/SqlFlow.SqlServer/Schema/SchemaEvolutionPlanner.cs
  - src/SqlFlow.SqlServer/Schema/EvolutionPlan.cs
  - src/SqlFlow.SqlServer/Schema/EvolutionDdlGenerator.cs
  - src/SqlFlow.SqlServer/Schema/SchemaSyncService.cs
  - src/SqlFlow.SqlServer/Schema/SqlColumn.cs
  - src/SqlFlow.SqlServer/Schema/SqlDataType.cs
  - src/SqlFlow.SqlServer/Schema/SqlTypeResolution.cs
  - src/SqlFlow.SqlServer/Schema/ChangeFootprintClassifier.cs
  - src/SqlFlow.SqlServer/Schema/RewritePolicy.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.SqlServer/Schema/HashKey.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
---

# Schema evolution: diffing, monotonic widening, and DDL apply

Schema evolution keeps a live SQL Server target table in step with what a flow wants to load, without manual DDL. Two separate engines exist, one per flow family. Both introspect the live target and diff it against the desired schema; only the ingestion engine's generated DDL is idempotent and applies under the non-blocking app-lock model described later on this page, while the file-flow engine runs its `CREATE TABLE` / `ALTER TABLE ADD` statements directly inside one transaction:

- **File flows** (csv, parquet, json, xml, xls) use `SchemaDiffer` in src/SqlFlow.Core/Engine/SchemaDiffer.cs plus `SqlServerDdlGenerator`. Missing columns are added, and existing columns are widened monotonically to fit the incoming data through `IColumnTypeReconciler` (the SQL Server implementation reuses the same `SqlDataType` + `SqlTypeResolution` the ingestion path uses), so a column a narrower earlier run created (for example `varchar(255)` when the flow now lands `varchar(4000)`) grows via `ALTER COLUMN` instead of overflowing the bulk load. An incompatible cross-family difference is left alone (never narrowed or force-changed).
- **Ingestion flows** (`flowType: ing`, table to table) use the full structured engine: `SchemaEvolutionPlanner`, `SqlTypeResolution`, `ChangeFootprintClassifier`, and `EvolutionDdlGenerator` under src/SqlFlow.SqlServer/Schema/. Types are parsed into structured `SqlDataType` values and widened monotonically: the target only ever grows and is never narrowed, target-only columns are never dropped, and a nullable column is never tightened to `NOT NULL`.

## File flows: diff and generated DDL

`DesiredSchemaBuilder.Build` (src/SqlFlow.Core/Engine/DesiredSchemaBuilder.cs) maps the inferred source columns through the type mapper, applying per-column `overrides` and the flow's `defaultColumnType`. `SchemaDiffer.Diff(desired, actual, evolve, reconciler)` then returns a `SchemaDelta { CreateTable, ColumnsToAdd, ColumnsToAlter }`:

- A missing target always yields `CreateTable = true` with the full desired column list, regardless of the `schema.evolve` mode.
- For an existing target, columns are matched case-insensitively by name. The `evolve` mode (enum `SchemaEvolution` in src/SqlFlow.Core/Model/FlowDefinition.cs) decides what happens:
  - `create`: never alter an existing table; the delta is empty.
  - `widen` (default): add the missing columns and widen existing columns whose live type is too narrow for the desired one. Each widening `ALTER COLUMN` renders the merged type and keeps the live column's nullability, so it never tightens a `NULL` column to `NOT NULL`.
  - `strict`: fail with `SchemaDriftException` if the source has columns the target lacks.

`SqlServerDdlGenerator` (src/SqlFlow.SqlServer/SqlServerDdlGenerator.cs) renders the delta:

```sql
CREATE TABLE [dbo].[Csv_FolderUnion] (
    [OrderID] varchar(255) NULL,
    [Amount] varchar(255) NOT NULL
);
```

Each added column is a separate statement, and columns added to an existing (potentially populated) table are always forced `NULL`able, because existing rows have no default value to backfill:

```sql
ALTER TABLE [dbo].[Csv_FolderUnion] ADD [Region] varchar(255) NULL;
```

An existing column that must grow is widened in place, after the adds, keeping its current nullability:

```sql
ALTER TABLE [dbo].[Csv_FolderUnion] ALTER COLUMN [Remarks] varchar(4000) NULL;
```

All file-flow DDL statements execute inside a single transaction via `SqlServerSchemaProvider.ExecuteDdlAsync`; any failure rolls back everything. Target introspection (`GetTableSchemaAsync`) reads `INFORMATION_SCHEMA.COLUMNS` ordered by `ORDINAL_POSITION` and renders char/binary lengths as `(n)` or `(MAX)` and decimal/numeric as `(precision, scale)`.

### Schema union across files in one load

When a file flow's discovery selects several files, the source schema is the union of every file's columns in first-seen order, compared case-insensitively (`FileSourceReaderBase.GetColumnsAsync` in src/SqlFlow.Sources/FileSourceReaderBase.cs). Rules:

- Columns with empty names are dropped from the union.
- Each file's cells are mapped positionally through a per-file name-to-index map; a file that lacks a union column contributes NULL for it.
- The same column seen with an identical resolved type in two files merges with nullability widened (nullable if either side is).
- Any type disagreement (only possible with a typed format like Parquet; string formats always agree) widens the column to string with an explicit `nvarchar(max)` SQL type, the lossless union.
- A source column whose name collides with an enabled generated column (provenance, `HashKey_DW`, concat key, file line number) is rejected with a `SqlFlowException` instead of being silently overwritten.

samples/csv/csv-folder-union.flow.yaml is the canonical example:

```yaml
name: Csv_FolderUnion
source:
  type: csv
  location: ./data/union
  options:
    srcFile: "*.csv"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_FolderUnion
schema:
  evolve: widen
```

### CLR to SQL Server type mapping

`SqlServerTypeMapper` (src/SqlFlow.SqlServer/SqlServerTypeMapper.cs) turns inferred CLR columns into SQL types. Precedence: an authored override (`schema.overrides.<name>.type`) wins, then an explicit `SourceColumn.SqlType` the source declared (for example an injected `varbinary(64)` hash key), then CLR inference. `Nullable<T>` unwraps to its underlying type first. Nullability comes from the override when present, otherwise the source column's `IsNullable` (default true).

| CLR type | SQL Server type |
| --- | --- |
| `string`, `MaxLength` 1..4000 | `NVARCHAR(n)` |
| `string`, `MaxLength` > 4000 | `NVARCHAR(MAX)` |
| `string`, no `MaxLength` | the flow's `defaultColumnType` (default `varchar(255)`) |
| `bool` | `BIT` |
| `byte` | `TINYINT` |
| `short` | `SMALLINT` |
| `int` | `INT` |
| `long` | `BIGINT` |
| `decimal` | `DECIMAL(precision, scale)`, precision fallback 38, scale fallback 6 |
| `double` | `FLOAT` |
| `float` | `REAL` |
| `DateTime` | `DATETIME2` |
| `DateTimeOffset` | `DATETIMEOFFSET` |
| `TimeSpan` | `TIME` |
| `Guid` | `UNIQUEIDENTIFIER` |
| `byte[]` | `VARBINARY(MAX)` |
| anything else | `NVARCHAR(MAX)` |

## Ingestion flows: the monotonic widening plan

`SchemaEvolutionPlanner.Plan(desired, actual, keyColumns)` (src/SqlFlow.SqlServer/Schema/SchemaEvolutionPlanner.cs) produces an `EvolutionPlan`:

| Field | Meaning |
| --- | --- |
| `CreateTable` / `CreateColumns` | The target is missing; create it with the full column list. |
| `ColumnsToAdd` | Desired columns absent from the target (case-insensitive match). |
| `ColumnsToAlter` | Same-family type widenings (`ALTER COLUMN`), each classified with a `ChangeFootprint`. |
| `CriticalMismatches` | Incompatible type changes on a key, hash-key, identity, or primary-key column. `IsBlocked` is true when any exist; the load must not proceed. |
| `Drift` | Non-blocking findings, surfaced for visibility only. |

The plan is monotonic:

- Same-family types merge to the wider of the two (`SqlTypeResolution.Resolve`); the target is never narrowed.
- Target-only columns are never dropped; they become `DriftKind.ExtraTargetColumn` findings.
- A nullable target column is never tightened to `NOT NULL`; a desired `NOT NULL` on a nullable target becomes `DriftKind.NullabilityNotTightened`.
- A cross-family (incompatible) type change on an ordinary column is `DriftKind.IncompatibleOrdinaryColumn` and the target column is left unchanged; on a key, hash-key, identity, or primary-key column it is a `CriticalMismatch` that blocks the load.

`EvolutionDdlGenerator.Generate(target, plan, allowTableRewrite)` (src/SqlFlow.SqlServer/Schema/EvolutionDdlGenerator.cs) turns the plan into a classified `DdlBatch`. A blocked plan throws before any DDL runs:

```text
Schema evolution on '<target>' is blocked by N critical type mismatch(es): [Col] cannot evolve [int] to [nvarchar(50)]: incompatible type families (Integer vs Text)
```

Generated statements are idempotent:

- The CREATE is guarded by `IF OBJECT_ID(N'[schema].[table]', N'U') IS NULL`. It adds `IDENTITY(1, 1)` for `IsIdentity` columns and `CONSTRAINT [PK_<table>] PRIMARY KEY CLUSTERED (...)` over the `IsPrimaryKey` columns.
- Each column ADD is guarded by `IF COL_LENGTH(...) IS NULL` and is always `NULL`able on an existing table.
- Each `ALTER COLUMN` widening renders the merged type and preserves the existing column's nullability.

`SchemaSyncService` (src/SqlFlow.SqlServer/Schema/SchemaSyncService.cs) wraps the pipeline end to end: `PlanAsync` introspects the live target and returns the plan without applying anything (the dry run); `EvolveAsync` introspects, plans, generates, applies, and returns an `EvolutionOutcome` carrying the plan plus the exact `DdlStatement` list that ran.

### How the ingestion run uses it

`IngestionFlowRunner` (src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs) evolves schema in two passes through the flow's canonical staging table:

1. **Staging**: the flow's canonical table `[raw].[<targetSchema>_<targetTable>_<flowId>]` is rebuilt via `EvolveAsync` with `allowTableRewrite: false` and no key columns. Any prior incarnation is dropped first, so this is always a clean CREATE of the bulk-copied data columns only. On success the staging table is dropped (`DROP TABLE IF EXISTS`) unless `load.keepStagingTable: true`; on failure it is always kept for debugging, and the next run's rebuild resets it.
2. **Target**: when `schema.sync` is true (the default), the persistent target takes the ADD/ALTER evolution path with the flow's `schema.allowTableRewrite` and effective key columns. Target-only columns are always retained.

`IngestionSchemaBuilder` (src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs) builds the desired schema from the shaped source columns and injects the engine-maintained columns:

- `InsertedDate_DW`, `UpdatedDate_DW`, `DeletedDate_DW` as plain `datetime` (not `datetime2(3)`, matching the original SQLFlow arc/ods tables) and `RowStatus_DW` as `char(1)`, per the `systemColumns` policy. When SCD2 is enabled, the period columns (`ValidFrom`/`ValidTo`) are added as `datetime2(3)` (with the current-flag column as `bit`).
- `HashKey_DW` as `binary(N)` sized by algorithm (`HashKey.BinaryTypeFor`): SHA2_512 = 64, SHA2_256 = 32, SHA1/SHA = 20, MD5/MD4/MD2 = 16. An unknown algorithm fails fast.
- The identity column (target only, from `target.identityColumn`) as `int IDENTITY(1, 1) NOT NULL` with a clustered primary key.

Column order is normalized: names starting with `PK` first, names ending with `PK` next, ordinary columns in source order, then `_DW` columns last (a `_DW` suffix always sorts last, even if the name would match a PK rule). Optional shaping transforms apply before the diff: column-name cleanup (`schema.cleanColumnNames`, regex and invalid-char replacement, from `SchemaSyncPolicy`) and unicode-to-non-unicode conversion (`schema.convertUnicodeToNonUnicode`).

## SqlDataType families and widening rules

`SqlDataType.Parse` (src/SqlFlow.SqlServer/Schema/SqlDataType.cs) parses a type string into `BaseType` (lowercased canonical), `Length` (-1 means `(max)`), `Precision`, and `Scale`. It is bracket and whitespace tolerant and round-trips through `Render()`. Canonicalization: `numeric` and `dec` become `decimal`, `integer` becomes `int`, `rowversion` becomes `timestamp`.

`SqlTypeFamily` values: `Text`, `Integer`, `Decimal`, `Approximate`, `Money`, `DateTime`, `Bit`, `Binary`, `Guid`, `Other`. Any cross-family change resolves to `Incompatible`. Within a family, `SqlTypeResolution` (src/SqlFlow.SqlServer/Schema/SqlTypeResolution.cs) widens:

| Family | Widening rule |
| --- | --- |
| Text | Unicode wins (`nchar`/`nvarchar`/`ntext`/`sysname` make the merge `n`-prefixed); variable wins (`varchar`/`nvarchar`/`text`/`ntext`/`sysname` make it `varchar`-based); `text`/`ntext` or length -1 forces `(max)`; otherwise the longer length. `sysname` counts as length 128. |
| Integer | Rank order `tinyint` (1) < `smallint` (2) < `int` (3) < `bigint` (4); the higher rank wins. |
| Decimal | Max integer digits plus max scale, precision clamped to 38 (unspecified precision counts as 18). |
| Approximate | `float` if either side is `float`, else `real`. |
| Money | `money` if either side is `money`, else `smallmoney`. |
| DateTime | Only the fractional-second scale of an identical base type is widened; a differing base (for example `date` vs `datetime2`) is `Incompatible` (conservative, no cross promotion). An unspecified `datetime2`/`time`/`datetimeoffset` scale counts as 7, so a bare `datetime2` already covers any incoming scale. |
| Binary | `timestamp`/`rowversion` cannot be widened at all (`Incompatible`); `image` or length -1 forces `varbinary(max)`; variable wins; otherwise the longer length. |
| Bit, Guid, Other | No widening: any difference is `Incompatible`. |

## ALTER COLUMN footprint and the allowTableRewrite gate

`ChangeFootprintClassifier.Classify(from, to)` (src/SqlFlow.SqlServer/Schema/ChangeFootprintClassifier.cs) classifies each widening ALTER as `MetadataOnly` or `TableRewrite` from the structured type pair. A rewrite forces a full in-place row rewrite under a schema-modification lock held for the whole operation, blocking every reader and writer on that table.

| Family | Rewrite when |
| --- | --- |
| Integer | Any rank increase (fixed storage width changes). |
| Decimal | Crossing a 5/9/13/17-byte storage class: precision <= 9, <= 19, <= 28, > 28. Growth within one class is metadata-only. |
| DateTime (`datetime2`/`time`/`datetimeoffset`) | Crossing a fractional-scale byte class: scale <= 2, <= 4, > 4. |
| Text | `(n)` to `(max)` (moves in-row data to LOB storage), or widening fixed `char`/`nchar`. `varchar(n)` to `varchar(m)` is metadata-only. |
| Binary | To `(max)`, or widening fixed `binary(n)`. |
| everything else | Metadata-only. |

`RewritePolicy.Decide(allowTableRewrite)` returns `Inline` when the flow opted in and `RequireOptIn` otherwise. A plan containing a `TableRewrite` alter is refused by `EvolutionDdlGenerator` with `SchemaRewriteNotPermittedException` unless the ingestion flow set `schema.allowTableRewrite: true` (`SchemaSyncPolicy.AllowTableRewrite`, default false; parsed from `IngestionSchemaYaml.AllowTableRewrite` in src/SqlFlow.Yaml/IngestionYaml.cs). Staging tables always evolve with `allowTableRewrite: false`. The refusal message:

```text
Schema evolution on '<target>' requires a table rewrite on column(s) <names> (for example int to bigint). This holds a table lock for the full rewrite and is refused by default. Set AllowTableRewrite to run it, ideally in a maintenance window.
```

## DDL apply: app locks, lock timeouts, and transaction boundaries

`SqlServerSchemaProvider.ApplyDdlAsync` (src/SqlFlow.SqlServer/SqlServerSchemaProvider.cs) applies a classified `DdlBatch` under an object-scoped session app lock:

- The lock resource is `SqlFlow.Schema:<schema>.<table>`, lowercased (case-canonical), acquired with `sys.sp_getapplock` in `Exclusive` mode with `Session` ownership. Two different objects get distinct resources and never contend.
- `SET LOCK_TIMEOUT` is applied first, so a blocked ALTER aborts with SQL error 1222 instead of queuing in front of readers.
- All metadata-only statements commit in one short `READ COMMITTED` transaction; each `TableRewrite` statement commits in its own transaction, so a rewrite failure never rolls back the additive work.
- A benign create race (error 2714 on the guarded `CREATE TABLE`) is swallowed: another run created the table first.
- SQL errors 1222 (lock-request timeout), 1204 (lock resources), and 1205 (deadlock victim) are wrapped in `SchemaLockTimeoutException`; `sp_getapplock` return codes below zero (-1 timeout, -2 cancelled, -3 deadlock) also throw `SchemaLockTimeoutException`.
- The app lock is released in a `finally` block; a release failure never masks the original DDL exception.

`DdlBatch` (src/SqlFlow.Core/Model/DdlBatch.cs) carries the raw schema/table names (the lock key) plus `DdlStatement { Text, Cost, IsCreateTable }` entries; `DdlCost` is `MetadataOnly | Rewrite`. `SchemaApplyOptions` defaults: `AppLockTimeoutMs` 30000, `DdlLockTimeoutMs` 5000, `RewriteMaxDurationMinutes` 1.

## Configuration touchpoints

| Surface | Key or command | Effect |
| --- | --- | --- |
| File flow YAML | `schema.evolve` (`create` \| `widen` \| `strict`, default `widen`) | Diff mode for an existing target. |
| File flow YAML | `schema.defaultColumnType` (default `varchar(255)`) | Type for untyped (string) columns on create. |
| File flow YAML | `schema.overrides.<column>` (`type`, `nullable`) | Per-column type and nullability overrides. |
| Ingestion flow YAML | `schema.sync` (default true) | Propagate new source columns to the target on each run. |
| Ingestion flow YAML | `schema.allowTableRewrite` (default false) | Permit a table-rewrite ALTER to run inline. |
| Ingestion flow YAML | `schema.cleanColumnNames`, `schema.cleanColumnNameRegex`, `schema.replaceInvalidCharsWith` | Column-name cleanup applied while building the desired schema; renames staging columns as well as the target, regardless of `schema.sync`. |
| Ingestion flow YAML | `schema.convertUnicodeToNonUnicode` | Convert Unicode source types to non-Unicode on the target. |
| CLI | `sqlflow plan <file>` | Dry run for file flows: prints whether the target exists, the columns to add, and the generated DDL without executing it. Ingestion, export, and stored-procedure work is determined at run time against the live source, so `plan` rejects those documents. |

## Example: opting in to a table rewrite

Adapted from samples/ingestion/orders-ingestion.flow.yaml. Suppose the source's `OrderID` grew from `int` to `bigint`. The planner classifies the `ALTER COLUMN` as a rewrite (integer rank increase), so the run fails with `SchemaRewriteNotPermittedException` until the flow opts in:

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

schema:
  sync: true
  allowTableRewrite: true   # permit the int -> bigint rewrite, ideally in a maintenance window
```

```bash
sqlflow validate orders-ingestion.flow.yaml
sqlflow run orders-ingestion.flow.yaml
```

## See also

- [schema (file flows)](../flow/schema.md)
- [ingestion schema and incremental settings](../flow/ing-schema-incremental.md)
- [plan (CLI)](../cli/plan.md)
- [the ingestion run pipeline](./ingestion-run-pipeline.md)
