# Dynamic Schema Evolution in V3

> Pre-implementation design document (legacy-behavior study plus an early implementation slice); it
> has not been re-verified against the current codebase. For the current, code-verified behavior see
> [docs/reference/concepts/schema-evolution.md](reference/concepts/schema-evolution.md) and
> [docs/reference/flow/schema.md](reference/flow/schema.md).

Status: design plus first implemented slice. This is the core of the ingestion engine: detect the schema of the incoming stream and adjust the target schema on the fly. It builds on the connection registry (slice 1) and the ingestion model (`SqlFlow.Core.Ingestion`). Grounded in a detailed study of the legacy `SyncSchema` path; legacy file:line citations are in the study notes, summarized here.

## 1. How legacy does it (the mechanics that matter)

- Two passes per flow, through a per-run staging table. `ProcessIngestion` calls `SyncSchema` twice: first to evolve a fresh staging table to match the shaped source, then to evolve the persistent target to match the staging table.
- The staging table (`[Schema01Raw].[{target}_{flowId}]`) is dropped and recreated every run, so it always takes a clean CREATE. Only the target ever takes the ADD/ALTER evolution path. The target evolves to match staging, and staging carries everything: source columns, virtual columns, the system `_DW` columns, the hash key column, name cleanup, and unicode conversion.
- Detection is SMO object-column introspection of a SQL Server table or view (not result-set description), because a legacy `ing` source is always SQL Server. Cross-provider sources go through other flow types in legacy.
- Evolution is additive plus altering, never dropping. A new source column becomes `ALTER TABLE ... ADD`; a changed column becomes `ALTER TABLE ... ALTER COLUMN`. Target-only columns are retained (this is what protects the injected `_DW`, identity, and key columns).
- Three behaviors in legacy are defects we will not reproduce: it has no widen guard, so it will narrow a column to match a shrunken source (truncation risk); its `DataTypeExp` parser swaps decimal precision and scale; and its DDL diff matches column names case-sensitively while the rest of the engine is case-insensitive.
- Injected columns: the `_DW` system columns and the hash key arrive as virtual columns in staging; the identity column is added to the target only, with a clustered PK. The hash key type is `binary(N)` sized by the algorithm (`SHA2_256` to `binary(32)`), not a fixed width.
- Column order is normalized: `PK*` first, `*PK` next, ordinary columns, then `_DW` columns last.
- A separate INFORMATION_SCHEMA comparison gates the load: if a hash-key or key column's type diverges between staging and target, it is a critical mismatch that blocks the upsert.
- The cleanup regex and replacement default values live in runtime `flw.SysCFG`, not in the repo. They must be read from a live database during migration.

## 2. V3 architecture

V3 keeps the two-pass staging architecture, because it is what makes cross-provider ingestion and the explicit two-step upsert work: you cannot join a MySQL source to a SQL Server target, so the source is bulk-copied into a staging table in the target database, then upserted within that database. Schema evolution therefore runs against two tables: the staging table (a fresh CREATE each run, shaped to the incoming stream) and the persistent target (ADD or ALTER to match staging).

The per-run pipeline (before load):

```
1. Detect the incoming stream schema   ISourceSchemaReader.GetResultSchemaAsync(resolvedSrc, sourceSelect)
                                        schema-only execution of the shaped source SELECT (object + virtual
                                        columns + filters, with ignored columns already projected out), so the
                                        detected columns ARE the incoming stream, cross-provider included.
2. Transform the detected columns       IColumnNameCleaner (regex remove + replace + dedupe)  [when CleanColumnNames]
                                        IUnicodeConverter (nvarchar -> varchar, etc.)         [when ConvertUnicodeToNonUnicode]
                                        -> shaped columns + a source-to-target name map for the bulk copy.
3. Build the desired schema             IngestionSchemaBuilder: shaped columns + injected system / hash / identity
                                        columns, ordered (PK*, *PK, normal, _DW last).
4. Introspect the live target           ISchemaProvider.GetTableSchemaAsync (exists or null).
5. Plan the evolution                   SchemaEvolutionPlanner.Plan(desired, actual): CreateTable | ColumnsToAdd |
                                        ColumnsToAlter | CriticalMismatches, using monotonic widening (section 3).
6. Generate DDL                         CREATE TABLE (+ PK / indexes / identity / columnstore) or ADD / ALTER COLUMN.
7. Execute the DDL                       ISchemaProvider.ExecuteDdlAsync, wrapped in one transaction (an improvement
                                        over legacy's per-statement auto-commit, so a partial failure rolls back).
8. Temporal versioning                   when Versioning.Temporal.Enabled is set. (SHIPPED DIFFERENTLY: not
                                        limited to first create; the transition is planned from the live
                                        target state, so it also turns an existing populated table into a
                                        system-versioned one. See reference/flow/ing-versioning.md.)
```

The staging build runs steps 1 to 7 against the staging table (always CreateTable, since it is dropped first); the target build runs steps 4 to 8 against the persistent target, with the staging table as its "desired" input.

## 3. Structured types and monotonic widening (the core)

The detail that everything rests on: V3 must reason about SQL types structurally, not as opaque strings. Today `ColumnDefinition.SqlType` is a string like `NVARCHAR(50)`, which cannot answer "is the incoming column wider than the target?". The first implemented slice introduces:

- `SqlDataType` (parsed: a lowercased base type, plus optional length, precision, scale, and a `max` flag), with `Parse` and `Render` that are bracket and whitespace tolerant and round-trip. Decimal is parsed correctly as `(precision, scale)` (fixing the legacy swap).
- `SqlTypeResolution.Resolve(existing, desired)` returns one of: `Keep` (the target already accommodates the incoming type), `Alter(merged)` (the target must grow to a widened type), or `Incompatible` (a cross-family change that cannot be applied safely and must be surfaced, not silently forced).

Monotonic widening is the production-grade rule that replaces legacy's narrow-happy behavior: the target only ever grows. For two types in the same family, the merged type is the wider of the two (the longer length, `max` wins; the larger decimal precision and scale; the wider integer; the larger datetime scale). If the merged type equals the existing target, it is a `Keep` (no DDL); if the source is wider, it is an `Alter` to the merged type; the target is never narrowed, so data already present always still fits. Family rules:

- Text: char, varchar, nchar, nvarchar, text, ntext are one family; widen to the longer length (or `max`), and to unicode if either side is unicode (unicode is a superset). This makes the legacy unicode-conversion direction safe by construction.
- Integer: tinyint, smallint, int, bigint widen upward.
- Decimal and numeric: widen precision and scale independently, clamped to the SQL Server maximum precision of 38.
- Approximate (real, float) and the money types widen within family.
- Date and time: same base widens on scale; differing bases are reported `Incompatible` for now (a conservative choice; cross promotion such as date to datetime2 is a later refinement) rather than risked.
- Anything cross-family (for example int to nvarchar, or datetime to int) is `Incompatible`.

`Incompatible` on an ordinary column is a warning and the target is left as is; `Incompatible` (or any change) on a key or hash-key column is a critical mismatch that blocks the load, mirroring the legacy gate but with a clear, structured reason.

## 4. Transforms

- Column-name cleanup (`IColumnNameCleaner`): a regex identifies the characters to remove or replace (the legacy regex is a "remove invalid characters" pattern); apply `ReplaceInvalidCharsWith` (or remove when empty); an empty result becomes `EmptyColumnName`; collisions are de-duplicated with a numeric suffix. It produces a source-to-target name map so the bulk-copy column mapping follows the rename. The regex and replacement come from the flow (`SchemaSyncPolicy.CleanColumnNameRegex`, `ReplaceInvalidCharsWith`), which a migration fills from live `flw.SysCFG` when not set per flow.
- Unicode conversion (`IUnicodeConverter`): `nvarchar` to `varchar`, `nchar` to `char`, `ntext` to `text`, preserving the declared length; `nvarchar(max)` to `varchar(max)`. Applied to both staging and target builds when `ConvertUnicodeToNonUnicode` is set.

## 5. Injected columns and ordering

The `IngestionSchemaBuilder` augments the shaped source columns with:

- System columns per `SystemColumnsPolicy`: `InsertedDate_DW` and `UpdatedDate_DW` as `datetime2(3)` (legacy uses `datetime`; `datetime2(3)` is the modern equivalent and a documented improvement), `DeletedDate_DW` as `datetime2(3)`, `RowStatus_DW` as `char(1)`.
- The hash key column `HashKey_DW` as `binary(N)` where N is sized from `ChangePolicy.HashType`: SHA2_512 to 64, SHA2_256 to 32, SHA1 to 20, MD5 to 16. Present only when a hash key is configured.
- The identity column (target only) as `int identity(1,1) not null`, with a clustered PK, when `Target.IdentityColumn` is set.

Order is normalized exactly as legacy: names starting with `PK` first, names ending with `PK` next, then ordinary columns in source order, then the `_DW` columns last. This keeps CREATE output stable and the system columns grouped.

## 6. Abstractions and placement

- `SqlFlow.Core` keeps the model and the pure, dialect-agnostic contracts. `ColumnDefinition` stays string-typed at the model boundary; the structured `SqlDataType` is a SQL Server concern.
- `SqlFlow.SqlServer` owns the SQL Server type knowledge and the evolution engine: `SqlDataType`, `SqlTypeResolution`, `IngestionSchemaBuilder`, `SchemaEvolutionPlanner`, the ALTER-aware DDL generation, and `ISourceSchemaReader` (the live schema-only detection, which needs SqlClient). `IUnicodeConverter` and the hash sizing are SQL Server type transforms and live here too.
- `IColumnNameCleaner` is dialect-agnostic (a regex over names) and can live in Core.

## 7. Legacy behaviors: fix versus preserve

- Fix: narrow-happy evolution becomes monotonic widening; the decimal precision/scale swap is parsed correctly; case-insensitive column-name matching throughout; DDL wrapped in a transaction.
- Preserve: never drop target columns; the two-pass staging model; the column ordering; the critical-mismatch gate on key and hash columns; `binary(N)` hash sizing by algorithm; the cleanup and unicode transforms.
- Decide later: `ColumnStoreIndexOnTrg` is dead in the legacy ingestion path (read but never applied); V3 will wire it at create time deliberately. Cross-datetime promotion. Computed columns (legacy effectively does not propagate them).

## 8. Build slices

1. Done in this slice: `SqlDataType` (structured parse and render) and `SqlTypeResolution` (monotonic widening: Keep, Alter, Incompatible), plus the hash-key sizing, with unit tests. This is the detail-critical core that the planner and DDL depend on.
2. `IColumnNameCleaner` and `IUnicodeConverter` (pure transforms) with tests.
3. `IngestionSchemaBuilder` (inject system, hash, identity columns; ordering) with tests.
4. `SchemaEvolutionPlanner` (desired versus actual to CreateTable, ColumnsToAdd, ColumnsToAlter, CriticalMismatches) with tests, and the ALTER-aware DDL generation.
5. `ISourceSchemaReader` (live schema-only detection over the connection registry) plus `ISchemaProvider` introspection, and the two-pass staging orchestration in the ingestion executor.
