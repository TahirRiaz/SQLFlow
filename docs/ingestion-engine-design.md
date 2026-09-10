# The Ingestion Engine in V3 (flw.Ingestion + flw.IngestionVirtual)

> Pre-implementation design document reasoning from the legacy `flw.Ingestion` schema; it has not
> been re-verified against the current codebase. For the current, code-verified behavior see
> [docs/reference/flow/ing.md](reference/flow/ing.md) and its companion pages
> ([ing-load](reference/flow/ing-load.md), [ing-quality](reference/flow/ing-quality.md),
> [ing-schema-incremental](reference/flow/ing-schema-incremental.md), [ing-versioning](reference/flow/ing-versioning.md)),
> plus [docs/reference/concepts/ingestion-run-pipeline.md](reference/concepts/ingestion-run-pipeline.md).

Status: design. Target tree: `C:\Projects\SQLFlowV3`. Builds on the connection registry (see `connection-registry-design.md`, slice 1 implemented). This document covers the legacy `flw.Ingestion` and `flw.IngestionVirtual` tables, which the project owner identifies as the main engine of SQLFlow.

---

## 1. What the Ingestion engine is, and how it sits on the connection registry

`flw.Ingestion` holds one row per ingestion flow: the canonical relational-to-relational pipeline (`FlowType = 'ing'`). Each row reads from a source SQL object and loads a target SQL object, with a large policy surface controlling incremental capture, merge/upsert, change detection, schema evolution, system columns, versioning, assertions, and orchestration.

The two columns that anchor it to the registry, confirmed by their own `MS_Description`:

- `srcServer` and `trgServer` are the `Alias` of a `flw.SysDataSource` row. In V3 these are resolved by the slice-1 `IConnectionResolver` (an `@alias` in full mode).
- `srcDBSchTbl` and `trgDBSchTbl` are 3-part `[Database].[Schema].[Object]` names (a `CHECK` enforces `parsename(...,3) IS NOT NULL`).

So one Ingestion row is, in V3 terms: `(@srcServer, srcDBSchTbl)` resolved through the registry, read and shaped, then loaded into `(@trgServer, trgDBSchTbl)` resolved through the registry, under a rich load policy.

`flw.IngestionVirtual` is a 1-to-many child (`FlowID` to `flw.Ingestion`) that injects computed columns into the projection: `ColumnName`, `DataType`, `DataTypeExp` (a T-SQL expression that yields the type, for example `CAST('2022-01-01' AS DATE)`), and `SelectExp` (the T-SQL expression that produces the column). It exists so a target column that is not present in the source can be synthesized from an expression.

## 2. The framing decision: Ingestion is not a separate engine

The most important design choice. In V3 the main engine is the shared, stateless `FlowRunner` plus the target/load machinery, already used by the file flows (CSV, JSON, XML, Parquet, Excel). Reaching `flw.Ingestion` fidelity does NOT mean a parallel engine. It means two additions, both of which keep a single code path (the Single Code Path Principle):

1. A relational source: `SourceSpec.Type = "ado"` over an `@alias` location, which is the `AdoSourceReader` already scoped in the connection-registry design (section 4.4 there). This is the only relational-specific piece.
2. An expanded load policy: merge/upsert by keys, hashkey change detection, schema sync, system columns, temporal versioning, init-load backfill, assertions, and invoke. These are target-side concerns and apply to ANY source, not only relational, so a CSV can be upserted by key exactly as a relational source can.

The split matters: it means `flw.Ingestion` does not introduce a new pipeline, it widens the policy surface of the one pipeline and plugs in one more source reader. The file flows immediately gain merge/CDC/system-columns by sharing the same machinery.

## 3. The V3 model (extends FlowDefinition, does not fork it)

`FlowDefinition` already carries `Source` (`SourceSpec`), `Target` (`TargetSpec`), `Schema` (`SchemaPolicy`), `Load` (`LoadPolicy`), `Inference`, `PreProcess`, `PostProcess`, `DesiredIndexes`, and `Incremental`. The Ingestion surface is absorbed by extending the policy records and adding a few new ones, plus the virtual-column list. New and changed types live in `SqlFlow.Core.Model`.

```csharp
namespace SqlFlow.Core.Model;

// LoadPolicy gains the merge surface. Today it is { Append, TruncateLoad }; add Merge.
public enum LoadMode { Append, TruncateLoad, Merge }

public sealed record LoadPolicy
{
    public LoadMode Mode { get; init; } = LoadMode.Append;
    public int BatchSize { get; init; } = 50_000;
    public bool TableLock { get; init; } = true;
    public bool ManageIndexes { get; init; }

    // --- Ingestion additions ---

    /// <summary>Business keys that identify a row for Merge (legacy KeyColumns). Required when Mode=Merge
    /// unless a Change policy supplies a hash key instead.</summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>Skip the UPDATE branch of a merge (legacy SkipUpdateExsisting): insert-only.</summary>
    public bool SkipUpdateExisting { get; init; }

    /// <summary>Skip the INSERT branch of a merge (legacy SkipInsertNew): update-only.</summary>
    public bool SkipInsertNew { get; init; }

    /// <summary>Confirm the keys exist on both sides before merging (legacy MatchKeysInSrcTrg).</summary>
    public bool MatchKeysInSourceAndTarget { get; init; }

    /// <summary>Batch the upsert to avoid lock escalation (legacy UseBatchUpsertToAvoideLockEscalation).</summary>
    public bool BatchUpsertToAvoidLockEscalation { get; init; }

    /// <summary>Rows per upsert batch when batching (legacy BatchUpsertRowCount, default 2000).</summary>
    public int BatchUpsertRowCount { get; init; } = 2000;

    /// <summary>Stream rows through (legacy StreamData, default true). When false the engine buffers and
    /// loads on NoOfThreads parallel writers.</summary>
    public bool StreamData { get; init; } = true;

    /// <summary>Parallel writer count when not streaming (legacy NoOfThreads).</summary>
    public int Threads { get; init; } = 1;

    /// <summary>Build a clustered columnstore on the target at create time (legacy ColumnStoreIndexOnTrg).</summary>
    public bool ColumnStoreIndexOnTarget { get; init; }

    /// <summary>Inject and populate an IDENTITY column on the target (legacy IdentityColumn); often the
    /// clustered key on archive tables.</summary>
    public string? IdentityColumn { get; init; }

    /// <summary>Continue the batch if this flow fails (legacy OnErrorResume, default true). A batch-level
    /// concern surfaced on the flow.</summary>
    public bool OnErrorResume { get; init; } = true;
}

/// <summary>Schema-synchronization policy (legacy SyncSchema and the column-cleanup family). Extends the
/// existing SchemaPolicy concern; the existing SchemaEvolution { Create, Widen, Strict } maps to SyncSchema
/// on/off plus the strict/widen behavior.</summary>
public sealed record SchemaSyncPolicy
{
    /// <summary>Propagate new source columns to the target (legacy SyncSchema, default true).</summary>
    public bool Sync { get; init; } = true;

    /// <summary>Clean target column names on sync (legacy OnSyncCleanColumnName); designed for SAP sources.</summary>
    public bool CleanColumnNames { get; init; }

    /// <summary>Regex used to clean column names (legacy CleanColumnNameSQLRegExp); overrides the config default.</summary>
    public string? CleanColumnNameRegex { get; init; }

    /// <summary>Replacement for invalid characters in a cleaned name (legacy ReplaceInvalidCharsWith).</summary>
    public string? ReplaceInvalidCharsWith { get; init; }

    /// <summary>Convert Unicode source types to non-Unicode on the target (legacy OnSyncConvertUnicodeDataType);
    /// designed for SAP Open Hub, which uses NVARCHAR for all text.</summary>
    public bool ConvertUnicodeToNonUnicode { get; init; }
}

/// <summary>Change-detection policy when there is no usable business key (legacy HashKey family). A fixed
/// length binary hash over HashColumns identifies and compares rows for insert/update.</summary>
public sealed record ChangePolicy
{
    public IReadOnlyList<string> HashColumns { get; init; } = [];
    public string HashType { get; init; } = "SHA2_512";           // legacy HashKeyType
    public IReadOnlyList<string> IgnoreColumnsInHash { get; init; } = []; // legacy IgnoreColumnsInHashkey
}

/// <summary>System (audit) columns to inject and maintain (legacy SysColumns). The legacy default is
/// InsertedDate_DW,UpdatedDate_DW; the full set from flw.SysColumn is below.</summary>
public sealed record SystemColumnsPolicy
{
    public bool InsertedDate { get; init; } = true;   // InsertedDate_DW
    public bool UpdatedDate { get; init; } = true;    // UpdatedDate_DW
    public bool DeletedDate { get; init; }            // DeletedDate_DW (soft-delete marker)
    public bool RowStatus { get; init; }              // RowStatus_DW
}

/// <summary>Target history/versioning (legacy trgVersioning via temporal tables, plus dimension helpers).</summary>
public sealed record VersioningPolicy
{
    /// <summary>Maintain a temporal history table for the target (legacy trgVersioning). Established on the
    /// first run; cannot be enabled on a preexisting flow.</summary>
    public bool TemporalHistory { get; init; }
    // SHIPPED DIFFERENTLY: this became VersioningPolicy.Temporal, a TemporalPolicy record, and the
    // "cannot be enabled on a preexisting flow" limitation was removed: the engine plans the transition
    // from the live target state, so versioning can be turned on for an existing, populated table.
    // See docs/reference/flow/ing-versioning.md.

    /// <summary>Insert an unknown-member row for dimension handling (legacy InsertUnknownDimRow).</summary>
    public bool InsertUnknownDimensionRow { get; init; }
}

/// <summary>One-time backfill plan (legacy InitLoad family): bound a large historical load and chunk it.</summary>
public sealed record InitLoadPolicy
{
    public bool Enabled { get; init; }                    // InitLoad
    public DateOnly? FromDate { get; init; }              // InitLoadFromDate
    public DateOnly? ToDate { get; init; }                // InitLoadToDate
    public string? BatchBy { get; init; }                 // InitLoadBatchBy (a single-char unit)
    public int? BatchSize { get; init; }                  // InitLoadBatchSize
    public string? KeyColumn { get; init; }               // InitLoadKeyColumn
    public int? KeyMaxValue { get; init; }                // InitLoadKeyMaxValue
}

/// <summary>The relational source descriptor inside SourceSpec.Options for type "ado", or a typed record.
/// srcServer is the @alias (Location), srcDBSchTbl the 3-part object, plus the read-shaping filters.</summary>
public sealed record RelationalSourceShape
{
    public required string Object { get; init; }          // srcDBSchTbl, 3-part name
    public string? Filter { get; init; }                  // srcFilter
    public string? IncrementalClause { get; init; }       // IncrementalClauseExp (a hard-coded predicate)
    public bool IncrementalClauseIsAppend { get; init; } = true; // srcFilterIsAppend
    public IReadOnlyList<string> IgnoreColumns { get; init; } = []; // IgnoreColumns
    public string? DataSetColumn { get; init; }           // DataSetColumn (per-file/-set ordering)
}

/// <summary>A computed projection column (one flw.IngestionVirtual row).</summary>
public sealed record VirtualColumn
{
    public string? Name { get; init; }          // ColumnName
    public string? DataType { get; init; }      // DataType
    public string? DataTypeExpression { get; init; } // DataTypeExp (a T-SQL expression yielding the type)
    public required string SelectExpression { get; init; } // SelectExp
}
```

`IncrementalSpec` already exists in `FlowDefinition` (Table, DateColumn, OverlapDays, FullLoad). It is extended to carry the legacy incremental columns: `IncrementalColumns` (the high-water columns whose MAX is read from the target, or MIN from the source when `FetchMinValuesFromSrc` is set) and `FetchMinValuesFromSource`. The existing `OverlapDays` is `NoOfOverlapDays`; the existing `FullLoad` is the legacy `FullLoad`.

`FlowDefinition` itself gains: `IReadOnlyList<VirtualColumn> VirtualColumns`, `SchemaSyncPolicy SchemaSync`, `ChangePolicy? Change`, `SystemColumnsPolicy SystemColumns`, `VersioningPolicy Versioning`, `InitLoadPolicy? InitLoad`, `AssertionsPolicy Assertions`, `InvokePolicy? Invoke`, and the grouping fields `Batch` and `SysAlias`. `PreProcess`/`PostProcess` already exist (inline SQL); the legacy `PreProcessOnTrg`/`PostProcessOnTrg` (stored-proc names) map to a single PreProcess/PostProcess entry of the form `EXEC <proc>`. `DesiredIndexes` already exists (`trgDesiredIndex`).

## 4. The engine pipeline and its interfaces (the main engine)

Execution order for an ingestion run, with the abstraction that owns each stage. Stages that already exist in V3 are marked. The connection for source and target is resolved once each via the slice-1 `IConnectionResolver` (roles Source and Target) and opened via `IConnectionFactory`.

```
resolve @srcServer / @trgServer            IConnectionResolver (slice 1, done)
  -> pre-invoke (ADF/runbook)              IInvokeRunner            (PreInvokeAlias; deferred slice)
  -> pre-process on target                 ISchemaProvider.ExecuteDdl (exists; PreProcessOnTrg)
  -> build source SELECT                   ISourceQueryBuilder      (object + virtual cols + filter + incremental + ignore)
  -> introspect + sync target schema       ISchemaSync              (SyncSchema family; extends existing schema diff)
  -> plan incremental window               IIncrementalPlanner      (extends existing IIncrementalProbe)
  -> plan init-load chunks (if enabled)    IInitLoadPlanner         (InitLoad family)
  -> read source rows                      AdoSourceReader          (type "ado"; connection-registry design 4.4)
  -> inject system + hash + identity cols  ISystemColumnInjector / IHashKeyStrategy
  -> load: full / truncate / merge         ILoadStrategy            (Append=exists; Merge=new MERGE generator)
  -> maintain temporal history             ITemporalVersioning      (trgVersioning)
  -> post-process on target                ISchemaProvider.ExecuteDdl (exists; PostProcessOnTrg)
  -> run assertions                        IAssertionRunner         (CheckEmptyTable, CheckFreshnessDaily)
  -> post-invoke                           IInvokeRunner            (PostInvokeAlias; deferred slice)
```

New Core abstractions (interfaces in `SqlFlow.Core.Abstractions`, SQL Server implementations in `SqlFlow.SqlServer`, all taking the slice-1 `ResolvedConnection` per the provider-signature decision):

- `ISourceQueryBuilder` builds the source `SELECT`: the base object, the `VirtualColumn` projections, `srcFilter`, the incremental predicate (calculated window or `IncrementalClauseExp`, combined per `srcFilterIsAppend`), and `IgnoreColumns` exclusion. Pure string generation plus an introspection of source columns; no open of its own.
- `ISchemaSync` reconciles the target schema to the shaped source schema: add new columns (the existing diff), and apply the cleanup family (`CleanColumnNames` via the regex, `ReplaceInvalidCharsWith`, `ConvertUnicodeToNonUnicode`). It supersedes the file-flow schema step and keeps `SchemaEvolution` semantics.
- `ILoadStrategy` is the heart. `Append` and `TruncateLoad` already exist via `IBulkLoader`. `Merge` is new: a generated SQL Server `MERGE` (or a batched upsert when `BatchUpsertToAvoidLockEscalation` is set) keyed on `KeyColumns` or the hash, honoring `SkipInsertNew`/`SkipUpdateExisting` and maintaining the system columns on each branch.
- `IHashKeyStrategy` computes the fixed-length binary hash over `HashColumns` (minus `IgnoreColumnsInHash`) with `HashType` (default `SHA2_512`) so change detection works without a business key.
- `ISystemColumnInjector` maintains `InsertedDate_DW` / `UpdatedDate_DW` / `DeletedDate_DW` / `RowStatus_DW` and the injected `IdentityColumn`.
- `IIncrementalPlanner` extends the existing `IIncrementalProbe`: it reads MAX of `IncrementalColumns` from the target (or MIN from the source when `FetchMinValuesFromSource`), applies `NoOfOverlapDays` against `DateColumn`, and orders by `DataSetColumn` when set.
- `IInitLoadPlanner` turns the `InitLoadPolicy` into a sequence of bounded reads (by date window or key range and `BatchSize`) for the first backfill.
- `ITemporalVersioning` enables and maintains the target temporal history table (`trgVersioning`) and the unknown-dimension row.
- `IAssertionRunner` runs the named assertions (`CheckEmptyTable`, `CheckFreshnessDaily`) after load and fails or warns per policy.
- `IInvokeRunner` triggers a pre-registered ADF pipeline or Automation runbook by alias (`PreInvokeAlias`/`PostInvokeAlias` from a future `flw.Invoke` analogue). Deferred to its own slice; until then a flow that references an invoke alias fails fast with a precise not-yet-supported error rather than silently skipping (no-stub rule).

`IDdlGenerator` stays untouched as the text generator; the `MERGE` text is produced by the new merge generator the same way table DDL is produced today.

## 5. Lightweight and full modes (one engine, two stores)

Mirrors the connection-registry split exactly.

- Lightweight: a YAML file expresses one ingestion flow (relational source by `@alias`, target by `@alias`, the load policy, and the `virtualColumns` list). No control database.
- Full: a new `IIngestionFlowStore` (the sibling of `IDataSourceStore`) reads one `flw.Ingestion` row joined to its `flw.IngestionVirtual` children and maps to the same `FlowDefinition`. The control DB is authoritative.

Both produce the same `FlowDefinition`, so `FlowRunner` and every stage above run identically. `Batch` and `SysAlias` are grouping and scheduling metadata that the control plane uses to select and order flows; they live on `FlowDefinition` but do not change a single flow's execution.

## 6. Column-complete migration mapping

Every `flw.Ingestion` and `flw.IngestionVirtual` column, with its V3 disposition. None is dropped silently.

| Legacy column | V3 destination | Disposition |
|---|---|---|
| `Ingestion.FlowID` | `FlowDefinition` full-mode identity (int), alongside the name-derived `FlowId` GUID | keep; sequence-backed surrogate retained for lineage/log joins |
| `Ingestion.Batch` | `FlowDefinition.Batch` | keep; batch grouping (links to a future SysBatch analogue) |
| `Ingestion.SysAlias` | `FlowDefinition.SysAlias` | keep; source-system grouping (links to a future SysAlias analogue) |
| `Ingestion.srcServer` | `SourceSpec.Location` (`@alias`) | keep; resolved by the connection registry |
| `Ingestion.srcDBSchTbl` | `RelationalSourceShape.Object` | keep; 3-part name, validated |
| `Ingestion.trgServer` | `TargetSpec.Connection` (`@alias`) | keep; resolved by the connection registry |
| `Ingestion.trgDBSchTbl` | `TargetSpec.Schema` + `TargetSpec.Table` (+ database) | keep; split from the 3-part name |
| `Ingestion.trgDesiredIndex` | `FlowDefinition.DesiredIndexes` | keep (exists) |
| `Ingestion.DeactivateFromBatch` | control-plane scheduling flag | relocate to the scheduler/control plane |
| `Ingestion.StreamData` | `LoadPolicy.StreamData` | keep |
| `Ingestion.NoOfThreads` | `LoadPolicy.Threads` | keep |
| `Ingestion.KeyColumns` | `LoadPolicy.KeyColumns` | keep |
| `Ingestion.IncrementalColumns` | `IncrementalSpec.IncrementalColumns` | keep |
| `Ingestion.IncrementalClauseExp` | `RelationalSourceShape.IncrementalClause` | keep |
| `Ingestion.DateColumn` | `IncrementalSpec.DateColumn` | keep (exists) |
| `Ingestion.DataSetColumn` | `RelationalSourceShape.DataSetColumn` | keep |
| `Ingestion.NoOfOverlapDays` | `IncrementalSpec.OverlapDays` | keep (exists) |
| `Ingestion.FetchMinValuesFromSrc` | `IncrementalSpec.FetchMinValuesFromSource` | keep |
| `Ingestion.SkipUpdateExsisting` | `LoadPolicy.SkipUpdateExisting` | keep (typo corrected) |
| `Ingestion.SkipInsertNew` | `LoadPolicy.SkipInsertNew` | keep |
| `Ingestion.FullLoad` | `IncrementalSpec.FullLoad` | keep (exists) |
| `Ingestion.TruncateTrg` | `LoadMode.TruncateLoad` | keep (exists) |
| `Ingestion.TruncatePreTableOnCompletion` | `LoadPolicy` flag (pre/staging table cleanup) | keep |
| `Ingestion.srcFilter` | `RelationalSourceShape.Filter` | keep |
| `Ingestion.srcFilterIsAppend` | `RelationalSourceShape.IncrementalClauseIsAppend` | keep |
| `Ingestion.IdentityColumn` | `LoadPolicy.IdentityColumn` | keep |
| `Ingestion.HashKeyColumns` | `ChangePolicy.HashColumns` | keep |
| `Ingestion.HashKeyType` | `ChangePolicy.HashType` | keep |
| `Ingestion.IgnoreColumns` | `RelationalSourceShape.IgnoreColumns` | keep |
| `Ingestion.IgnoreColumnsInHashkey` | `ChangePolicy.IgnoreColumnsInHash` | keep |
| `Ingestion.SysColumns` | `SystemColumnsPolicy` | keep (parsed from the CSV list) |
| `Ingestion.ColumnStoreIndexOnTrg` | `LoadPolicy.ColumnStoreIndexOnTarget` | keep |
| `Ingestion.SyncSchema` | `SchemaSyncPolicy.Sync` | keep |
| `Ingestion.OnErrorResume` | `LoadPolicy.OnErrorResume` | keep |
| `Ingestion.OnSyncCleanColumnName` | `SchemaSyncPolicy.CleanColumnNames` | keep |
| `Ingestion.ReplaceInvalidCharsWith` | `SchemaSyncPolicy.ReplaceInvalidCharsWith` | keep |
| `Ingestion.OnSyncConvertUnicodeDataType` | `SchemaSyncPolicy.ConvertUnicodeToNonUnicode` | keep |
| `Ingestion.CleanColumnNameSQLRegExp` | `SchemaSyncPolicy.CleanColumnNameRegex` | keep |
| `Ingestion.trgVersioning` | `VersioningPolicy.Temporal` (shipped as a `TemporalPolicy` record) | keep |
| `Ingestion.InsertUnknownDimRow` | `VersioningPolicy.InsertUnknownDimensionRow` | keep |
| `Ingestion.TokenVersioning` | `VersioningPolicy` (token field) | keep but deferred (legacy marks it under development) |
| `Ingestion.TokenRetentionDays` | `VersioningPolicy` (token field) | keep but deferred (same) |
| `Ingestion.PreProcessOnTrg` | `FlowDefinition.PreProcess` entry (`EXEC <proc>`) | keep (exists) |
| `Ingestion.PostProcessOnTrg` | `FlowDefinition.PostProcess` entry (`EXEC <proc>`) | keep (exists) |
| `Ingestion.PreInvokeAlias` | `InvokePolicy.PreAlias` | keep; deferred Invoke slice |
| `Ingestion.PostInvokeAlias` | `InvokePolicy.PostAlias` | keep; deferred Invoke slice |
| `Ingestion.Assertions` | `AssertionsPolicy` (named checks) | keep |
| `Ingestion.MatchKeysInSrcTrg` | `LoadPolicy.MatchKeysInSourceAndTarget` | keep |
| `Ingestion.UseBatchUpsertToAvoideLockEscalation` | `LoadPolicy.BatchUpsertToAvoidLockEscalation` | keep (typo corrected) |
| `Ingestion.BatchUpsertRowCount` | `LoadPolicy.BatchUpsertRowCount` | keep |
| `Ingestion.InitLoad` | `InitLoadPolicy.Enabled` | keep |
| `Ingestion.InitLoadFromDate` | `InitLoadPolicy.FromDate` | keep |
| `Ingestion.InitLoadToDate` | `InitLoadPolicy.ToDate` | keep |
| `Ingestion.InitLoadBatchBy` | `InitLoadPolicy.BatchBy` | keep |
| `Ingestion.InitLoadBatchSize` | `InitLoadPolicy.BatchSize` | keep |
| `Ingestion.InitLoadKeyColumn` | `InitLoadPolicy.KeyColumn` | keep |
| `Ingestion.InitLoadKeyMaxValue` | `InitLoadPolicy.KeyMaxValue` | keep |
| `Ingestion.BatchOrderBy` | control-plane ordering within a batch | relocate to the scheduler (legacy marks it under development) |
| `Ingestion.FlowType` | the flow-type discriminator (`ing`) | keep; selects the relational source path and load surface |
| `Ingestion.Description` | `FlowDefinition` metadata | keep |
| `Ingestion.FromObjectMK` | lineage master key (source) | keep; lineage layer (a later slice) |
| `Ingestion.ToObjectMK` | lineage master key (target) | keep; lineage layer (a later slice) |
| `Ingestion.CreatedBy` | full-mode audit | keep; control-DB row metadata |
| `Ingestion.CreatedDate` | full-mode audit | keep; control-DB row metadata |
| `IngestionVirtual.VirtualID` | full-mode surrogate | keep; control-DB row identity |
| `IngestionVirtual.FlowID` | the parent link | keep; foreign key to the flow |
| `IngestionVirtual.ColumnName` | `VirtualColumn.Name` | keep |
| `IngestionVirtual.DataType` | `VirtualColumn.DataType` | keep |
| `IngestionVirtual.DataTypeExp` | `VirtualColumn.DataTypeExpression` | keep |
| `IngestionVirtual.SelectExp` | `VirtualColumn.SelectExpression` | keep |

Two legacy side effects are noted, not lost: the `AddFlowToLogTable` insert trigger becomes the V3 run-log write in full mode (the engine writes the log, not a DB trigger; logic in code, not the database); the `Chk_*DBSchTbl` 3-part-name CHECKs become validation at the trust boundary when a flow is authored or loaded.

## 7. Build order (slices on top of the connection registry)

In dependency order, each slice buildable and testable on its own:

1. The relational source: `AdoSourceReader` (`type: ado`) over the slice-1 connection registry, plus the `ISourceReader` signature change to accept a `ResolvedConnection`. This is the prerequisite for any relational ingestion and is already scoped in the connection-registry design (section 4.4).
2. `ISourceQueryBuilder` plus `VirtualColumn` projection, `srcFilter`, `IgnoreColumns`. Pure generation, unit-testable.
3. `ISchemaSync` (the cleanup family on top of the existing diff).
4. `ILoadStrategy.Merge` plus `ISystemColumnInjector` plus `IHashKeyStrategy`: the upsert heart. The largest and highest-value slice.
5. `IIncrementalPlanner` extensions (incremental columns, fetch-min-from-source) on the existing probe.
6. `IInitLoadPlanner` (backfill chunks).
7. `ITemporalVersioning`, `IAssertionRunner`.
8. `IInvokeRunner` (ADF/runbook) and the lineage master keys, each a later slice.
9. Full-mode `IIngestionFlowStore` over `flw.Ingestion` + `flw.IngestionVirtual`, and the YAML mapping for lightweight mode.

## 8. Open decisions for the user

1. Merge engine: a single generated `MERGE` statement (concise, atomic) versus an explicit `UPDATE` then `INSERT` pair (avoids known `MERGE` concurrency and trigger pitfalls on SQL Server). Recommendation: the explicit two-step upsert, batched when `BatchUpsertToAvoidLockEscalation` is set; it sidesteps the documented `MERGE` hazards and maps cleanly onto `SkipInsertNew`/`SkipUpdateExisting`.
2. Model unification: extend `FlowDefinition` and `LoadPolicy` in place (recommended, one model and one code path for file and relational flows) versus a separate `IngestionFlow` record. Recommendation: extend in place.
3. Scope of the first ingestion slice: stop at slice 4 (a working relational source plus key-based merge with system columns), or also include incremental (slice 5) in the first cut. Recommendation: slices 1 through 4 first, since merge and system columns are the defining value of the engine, then incremental.
4. `FlowType` breadth: model only `ing` now, or reserve the discriminator for the other legacy flow types (csv, xls, jsn, xml, prc, exp) so the model is forward-compatible. Recommendation: reserve the discriminator now, implement only `ing` plus the existing file readers.

---

## 9. Implementation status

### 9.1 Delivered: the IngestionFlow model and lossless legacy porting

Implemented in `SqlFlow.Core`, namespace `SqlFlow.Core.Ingestion`, pure (no SqlClient or Azure), built and unit-tested:

- The model: `IngestionFlow` with `IngestionSource` / `IngestionTarget` endpoints (a connection-registry alias plus a `RelationalObject` three-part name), `VirtualColumn`, and the policy records `IngestionLoadPolicy`, `ChangePolicy`, `SystemColumnsPolicy`, `SchemaSyncPolicy`, `IncrementalPolicy`, `InitLoadPolicy`, `VersioningPolicy`, `ProcessPolicy`.
- `RelationalObject.Parse`: a bracket-aware, ']]'-escaping three-part-name parser that takes the rightmost three parts (matching the legacy GetValidSrcTrgName) and fails fast on fewer than three parts or an empty part.
- Porting: `Legacy.LegacyIngestionRow` and `Legacy.LegacyIngestionVirtualRow` DTOs that mirror the tables column-for-column (legacy spellings preserved), plus `Legacy.IngestionFlowMapper.FromLegacy(...)`, a pure and lossless mapper. Every legacy NULL resolves to the documented table default; an unrecognized SysColumns token or a malformed name fails fast with a flow-identified error rather than being dropped.

Tests (23 new, 54 in the project total): `RelationalObjectTests`, `IngestionFlowMapperTests`. The full solution builds with 0 warnings and 0 errors.

### 9.2 Decisions taken (these supersede the section 8 recommendations where they differ)

- No `MERGE`, and no merge-mode enum at all. The legacy engine already stages the source then applies an explicit UPDATE then INSERT (verified in `ProcessIngestionToTarget.cs`), so the model carries the raw columns (`KeyColumns`, `SkipUpdateExisting`, `SkipInsertNew`, `TruncateBeforeLoad`) and the executor derives the two-step behavior, exactly as legacy does.
- A dedicated `IngestionFlow` model, NOT an extension of the file-oriented `FlowDefinition`. SQL to SQL Server is fundamentally different (a live relational source addressed through the connection registry, a typed source schema, keyed change application). The two models share the connection-registry value types, but nothing is forced into the file model.
- `FullLoad` maps from the legacy `int` as truthy (non-zero is full). `NoOfThreads` of 0 maps to "engine default" (null). `SysColumns` NULL maps to the default pair; an explicit empty string maps to none.

### 9.3 Next thin step for end-to-end porting

The porting LOGIC is complete and tested. What remains, to pull rows from a live legacy control database, is a small `SqlFlow.SqlServer` reader that runs `SELECT ... FROM flw.Ingestion` (and `flw.IngestionVirtual`) into the DTOs and then calls the mapper. It needs SqlClient and a connection (resolved through the connection registry), so it lands with the full-mode store work. The faithful per-column mapping (the hard part) is already done.

After this, the engine slices in section 7 build on this model: `AdoSourceReader`, `ISourceQueryBuilder`, `ISchemaSync`, the two-step upsert executor, and the rest.
