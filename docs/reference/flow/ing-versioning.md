---
id: flow-ing-versioning
title: "Ingestion flow: versioning, temporal history, and SCD2"
type: flow-reference
summary: "Target history for an ingestion flow: SQL Server system-versioned temporal tables, and application-managed SCD Type 2 dimension history."
keywords:
  - scd2
  - versioning
  - validfrom_dw
  - validto_dw
  - iscurrent_dw
  - dimension history
  - temporalhistory
  - temporal
  - system-versioned
  - system_time
  - history table
  - for system_time
  - retentiondays
  - trackedcolumns
yamlPath: "versioning (flowType: ing)"
related:
  - flow-ing-load
  - concept-upsert-and-change-detection
sourceRefs:
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/IngestionYaml.cs
  - src/SqlFlow.Core/Ingestion/IngestionPolicies.cs
  - src/SqlFlow.SqlServer/Schema/TemporalTablePlanner.cs
  - src/SqlFlow.SqlServer/Schema/SchemaSyncService.cs
  - src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs
  - src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs
  - src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
---

# Ingestion flow: versioning and SCD2

The `versioning` section of an ingestion flow (`flowType: ing`) controls target history. It offers two independent, mutually exclusive mechanisms:

- **`versioning.temporal`** (legacy `trgVersioning`): SQL Server **system-versioned temporal history**. The database itself keeps every superseded version of a row in a paired history table, so an UPDATE or DELETE on the target is never lossy and the table is queryable as of any past instant with `FOR SYSTEM_TIME`. The target keeps its exact column list, because the period columns are `HIDDEN`.
- **`versioning.scd2`**: **application-managed** slowly-changing-dimension Type 2 history, where the engine keeps a validity period per row inside the target itself. When a tracked attribute of a keyed row changes, the current row is closed (its ValidTo stamped, its current flag cleared) and a new current version is inserted.

Enabling both on one flow is rejected: they would record every change twice. The remaining keys are either rejected as not yet implemented (`insertUnknownDimensionRow`) or reserved and inert (`tokenVersioning`, `tokenRetentionDays`).

Which to choose: `temporal` when you want a complete, database-guaranteed audit trail of a fact or reference table with no change to what consumers query; `scd2` when you want a dimension whose versions are first-class rows that ordinary SQL (and a BI tool) can join to a date.

```yaml
flowType: ing
name: customer-dim

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Customer

target:
  server: dwh
  object: DW.dim.Customer

load:
  keyColumns: [CustomerID]      # required by scd2: the business key the dimension versions by

versioning:
  scd2:
    enabled: true
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `versioning.temporalHistory` | bool | no | `false` | Shorthand for `versioning.temporal.enabled` (this is the key a ported legacy `trgVersioning` flow carries). |
| `versioning.temporal` | block | no | disabled | SQL Server system-versioned temporal history (sub-keys below). |
| `versioning.temporal.enabled` | bool | no | `false` | Turn on system-versioned history for the target. |
| `versioning.temporal.historySchema` | string | no | `ver` | Schema holding the history table, in the target's own database. Created if missing. Legacy read this from `flw.SysCFG.Schema06Version`, which was `ver`. |
| `versioning.temporal.historyTable` | string | no | target's name | History table name. Set it only when two targets in different schemas would collide on one history name. |
| `versioning.temporal.validFromColumn` | string | no | `ValidFrom_DW` | The period's ROW START column (`GENERATED ALWAYS`, engine-added). |
| `versioning.temporal.validToColumn` | string | no | `ValidTo_DW` | The period's ROW END column (`GENERATED ALWAYS`, engine-added). |
| `versioning.temporal.hiddenPeriodColumns` | bool | no | `true` | Declare the period columns `HIDDEN`, so `SELECT *` returns the table's original column list and enabling history breaks no consumer. |
| `versioning.temporal.periodPrecision` | int (0-7) | no | `7` | `datetime2` fractional-second scale of the period columns. Legacy emitted `0`; set `0` when linking a history table migrated from a legacy estate, whose period columns must match. |
| `versioning.temporal.retentionDays` | int | no | none (INFINITE) | `HISTORY_RETENTION_PERIOD` in days. Needs Azure SQL Database, Azure SQL Managed Instance, or SQL Server 2025+. |
| `versioning.insertUnknownDimensionRow` | bool | no | `false` | Insert an unknown-member dimension row. Not yet implemented: a flow that enables it is rejected at validation. |
| `versioning.tokenVersioning` | bool | no | `false` | Reserved (legacy TokenVersioning), carried for fidelity; no engine behavior. |
| `versioning.tokenRetentionDays` | int | no | none | Reserved (legacy TokenRetentionDays); no engine behavior. |
| `versioning.scd2` | block | no | disabled | Application-managed SCD Type 2 history (sub-keys below). |
| `versioning.scd2.enabled` | bool | no | `false` | Turn on the SCD2 load path. |
| `versioning.scd2.validFromColumn` | string | no | `ValidFrom_DW` | Period-start column (`datetime2(3)`, engine-added). |
| `versioning.scd2.validToColumn` | string | no | `ValidTo_DW` | Period-end column (`datetime2(3)`, engine-added); the current row holds the open sentinel. |
| `versioning.scd2.currentFlagColumn` | string | no | `IsCurrent_DW` | Current-row indicator (`bit`, engine-added): 1 for the live version, 0 for expired versions. |
| `versioning.scd2.trackedColumns` | string list | no | `[]` | Source column names whose change opens a new version; empty means every comparable non-key data column. |

## versioning.temporal

### Enabling

The shorthand is enough for the common case, and is what a ported legacy `trgVersioning` flow carries:

```yaml
versioning:
  temporalHistory: true      # identical to: temporal: { enabled: true }
```

The full block configures the rest:

```yaml
versioning:
  temporal:
    enabled: true
    historySchema: ver              # history lives in [ver].[<target>], same database
    retentionDays: 3650             # optional; omit to keep history forever
```

The target becomes system-versioned, and from then on SQL Server records the previous image of every row an UPDATE or DELETE touches:

```sql
-- what the row looked like at a past instant
SELECT * FROM arc.SVV_Bilteller FOR SYSTEM_TIME AS OF '2026-01-15T00:00:00' WHERE Felt = 1;

-- every version, current and historical
SELECT * FROM arc.SVV_Bilteller FOR SYSTEM_TIME ALL WHERE Felt = 1;
```

### It can be turned on for a table that already exists and already holds rows

This is the limitation legacy could not clear: legacy applied versioning only in its create-a-new-table branch, so an existing target could never gain history. V3 introspects the live target and plans the transition from whatever state it is in, so enabling the flag on a populated production table is an ordinary run. The engine adds the `SYSTEM_TIME` period, stamps the existing rows' ROW START one second in the past (so no row's period is empty and every row stays visible to `FOR SYSTEM_TIME AS OF`), and links the history table.

### Enabling history does not change what consumers see

The period columns are declared `HIDDEN` by default, so `SELECT *`, result-set metadata, and every downstream consumer keep seeing the target's original column list. That is what makes turning this on a non-breaking change. Set `hiddenPeriodColumns: false` if you want the period columns to be part of the ordinary projection.

### What the engine will and will not do

The transition is planned from the live state (src/SqlFlow.SqlServer/Schema/TemporalTablePlanner.cs), which gives four outcomes:

| Live state | What happens |
| --- | --- |
| No `SYSTEM_TIME` period | The period is added, then versioning is turned on. |
| Period present, versioning off | Only the enable runs. This is the resume path after a manual `SET (SYSTEM_VERSIONING = OFF)` (which leaves the period behind) or a run interrupted between the two statements. |
| Already versioned into the declared history table | Nothing, unless the retention period differs, which is re-applied. |
| Already versioned into a **different** history table | The run fails. Re-pointing would orphan the history already recorded. |

Two things the engine deliberately never does:

- **Turning the flag back off does not un-version the table** and does not drop history. History is data; discarding it is an explicit operator action, not a side effect of an edited YAML.
- **It never takes versioning off to push a schema change through**, which would open a window where changes go unrecorded. It does not need to (see below).

### Schema evolution keeps working while versioning is on

`ALTER TABLE ... ADD`, `ALTER COLUMN` (including widenings that rewrite the table), and `DROP COLUMN` are all supported by SQL Server on a system-versioned table, and it propagates each change to the history table automatically. So dynamic schema evolution runs unchanged on a versioned target: a new source column lands on both halves, a widened column widens on both.

### Limitations and how they surface

SQL Server imposes real rules on a versioned table. Each is enforced up front with an actionable message rather than surfacing as a raw engine error mid-run:

| Rule | How the engine handles it |
| --- | --- |
| The table must have a PRIMARY KEY | Rejected before any DDL: *"SQL Server requires a system-versioned table to have a PRIMARY KEY, and the target has none. Set 'target.identityColumn' ..."*. In V3 `target.identityColumn` is what makes the engine create a clustered primary key. |
| `TRUNCATE TABLE` is not allowed | `versioning.temporal` combined with `target.truncateBeforeLoad` is rejected at parse time and again at run time. Legacy silently skipped the truncate, which left the flow believing it had done a full reload when it had appended. |
| `DROP TABLE` is not allowed | Unlink versioning first (`ALTER TABLE ... SET (SYSTEM_VERSIONING = OFF)`), then drop both halves. |
| The history table must be in the same database | `historySchema` / `historyTable` must be plain undotted names; a dotted one is rejected at parse time. |
| The history table's columns must match the current table exactly | The enable uses `DATA_CONSISTENCY_CHECK = ON`, so linking a pre-existing (for example migrated) history table with a different shape fails with SQL Server's precise column-and-ordinal error instead of mislinking. |
| Period columns are `GENERATED ALWAYS` | They can never be written, so the engine keeps them out of the schema diff, the upsert column list, and change detection entirely. A period column name that collides with a data column is rejected. |
| A period cannot be renamed in place | Changing `validFromColumn`/`validToColumn` on an already-versioned table is rejected. |
| `HISTORY_RETENTION_PERIOD` is not on every engine | It needs Azure SQL Database, Azure SQL Managed Instance, or SQL Server 2025+. Omit `retentionDays` elsewhere. |

### Mutual exclusion with SCD2

```text
'versioning.temporal' and 'versioning.scd2' cannot both be enabled. System-versioned history keeps every row version in a separate history table maintained by SQL Server, while SCD2 keeps versions in the target itself; enabling both records each change twice. Choose one.
```

### Porting a legacy trgVersioning flow

Legacy stored the flag as `flw.Ingestion.trgVersioning` and generated the DDL through `flw.GetVersioningScript`, taking the history schema from `flw.SysCFG.Schema06Version` and hardcoding the `ValidFrom_DW`/`ValidTo_DW` names at `datetime2(0)`. Those are the V3 defaults, so `versioning.temporalHistory: true` reproduces the legacy setup, with one difference: V3 defaults the period to `datetime2(7)`. Add `periodPrecision: 0` when the flow has to match tables the legacy engine created (a control-DB-sourced flow does this automatically).

## versioning.insertUnknownDimensionRow

Also rejected at parse time when enabled:

```text
'versioning.insertUnknownDimensionRow' is not yet implemented; seed the unknown-member row explicitly for now.
```

## versioning.tokenVersioning and versioning.tokenRetentionDays

Accepted by the schema and carried on the parsed model for fidelity with the legacy engine, but no engine code acts on them.

## versioning.scd2

### Enabling and validation

`versioning.scd2.enabled: true` switches the keyed load from the plain two-step upsert to the SCD2 close-and-insert load. Validation at parse time (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs):

- `load.keyColumns` is required (SCD2 maintains one version chain per business key):

  ```text
  'versioning.scd2' requires 'load.keyColumns' (the business key the dimension versions by).
  ```

- `target.truncateBeforeLoad: true` is forbidden (truncating would erase the history):

  ```text
  'versioning.scd2' cannot be combined with 'target.truncateBeforeLoad'; truncating would erase the dimension history.
  ```

- The three period columns must be three distinct names (compared case-insensitively). This check runs for any `scd2` block, even one with `enabled: false`:

  ```text
  'versioning.scd2' validFromColumn, validToColumn, and currentFlagColumn must be three distinct names (got '<validFrom>', '<validTo>', '<currentFlag>').
  ```

Additionally, the SQL generator (src/SqlFlow.SqlServer/Schema/UpsertGenerator.cs) refuses to combine SCD2 with a dataset-partitioned load: `A dataset-column load (DataSetColumn) cannot be combined with SCD2 versioning.` Under SCD2, `load.skipUpdateExisting` and `load.skipInsertNew` do not apply; the SCD2 statement set replaces the plain UPDATE/INSERT branches entirely.

### Period columns and schema evolution

The three period columns are ordinary columns added by src/SqlFlow.SqlServer/Schema/IngestionSchemaBuilder.cs through the same path as the `_DW` system columns: `validFromColumn` and `validToColumn` are `datetime2(3)`, `currentFlagColumn` is `bit`, and all three are nullable so an `ALTER TABLE ... ADD` onto an existing populated table succeeds. SCD2 can therefore be enabled on an already-created, already-populated target: schema evolution adds the columns, and the first SCD2 run backfills every pre-existing row as the current version.

The current row of a key is the one whose current flag is 1. Its ValidTo holds the open-ended sentinel `9999-12-31 23:59:59.999` (the max `datetime2(3)` value), so every point-in-time query is a uniform half-open `[ValidFrom, ValidTo)` range with no NULL special case.

### The SCD2 load

The load is three ordered statements sharing one inlined as-of instant (UTC, captured once per run in src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs, so a closed row's ValidTo equals the new row's ValidFrom exactly):

1. Backfill: every target row whose ValidTo is NULL (a row that predates SCD2) is stamped as the current version. ValidFrom comes from the existing value, else `InsertedDate_DW` when that system column is present, else the epoch `1900-01-01 00:00:00.000`.
2. Close: current rows (`flag = 1`) whose tracked attributes differ from the staged row (compared with a HASHBYTES checksum) get ValidTo stamped with the as-of instant and the flag cleared to 0. When `systemColumns.updatedDate` is on, `UpdatedDate_DW` is stamped; when `systemColumns.rowStatus` is on, `RowStatus_DW` is stamped `'U'`.
3. Insert: one new current version per key that has no current row (the just-closed changed keys plus brand-new keys), with an anti-join on `flag = 1`. Staging is deduplicated to one row per key first, so a key appearing several times in staging versions once. New rows get ValidFrom = as-of, ValidTo = the open sentinel, flag = 1, plus `InsertedDate_DW` and `RowStatus_DW 'I'` when those system columns are on.

Unchanged keys keep their existing current row untouched.

### trackedColumns

`trackedColumns` lists SOURCE column names (the runner maps them to cleaned target names, the same way `change.ignoreColumnsInHash` is mapped). Only changes in these attributes open a new version; other column changes leave the current row as is. An empty list (the default) tracks every comparable non-key data column. Columns whose data type cannot participate in the checksum, and columns in `change.ignoreColumnsInHash`, are excluded from comparison. If nothing is comparable, the close step is skipped and only brand-new keys are inserted.

### Key index migration

Under SCD2 the business key is unique only among current rows (each key has one row per version), so the canonical unique key index becomes a filtered unique index (src/SqlFlow.SqlServer/Ingestion/CanonicalIndexPlanner.cs):

```sql
CREATE UNIQUE NONCLUSTERED INDEX [NCI_KeyColumn] ON [dim].[Customer] ([CustomerID]) WHERE [IsCurrent_DW] = 1;
```

This reconciliation runs on every SCD2 run, not only at target creation, so enabling SCD2 on a pre-existing table migrates its key index before the load inserts a second version for a key:

- A same-named `NCI_KeyColumn` that is not the filtered-unique form is dropped.
- Any non-filtered unique nonclustered index whose key columns are exactly the business key (whatever its name) is dropped, because it would block a second version.
- Primary keys and unique constraints are deliberately left alone; a natural-key PK is a modeling conflict the operator must resolve.
- All statements are guarded by `sys.indexes` checks, so re-running is a no-op.

## Full example

```yaml
flowType: ing
name: customer-dim

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Customer

target:
  server: dwh
  object: DW.dim.Customer

load:
  keyColumns: [CustomerID]

versioning:
  scd2:
    enabled: true
    validFromColumn: EffectiveFrom      # override the ValidFrom_DW default
    validToColumn: EffectiveTo          # override the ValidTo_DW default
    currentFlagColumn: IsActive         # override the IsCurrent_DW default
    trackedColumns: [Name, Segment]     # only these attributes open a new version
```

Validate and run with the standard CLI verbs:

```bash
sqlflow validate customer-dim.flow.yaml
sqlflow run customer-dim.flow.yaml
```

### Full temporal example

```yaml
flowType: ing
name: svv_bilteller_02_ing

connections:
  pre: ${env:SQLFLOW_CONN_DWPREPROD}
  ods: ${env:SQLFLOW_CONN_DWDWHPROD}

source:
  server: pre
  object: "[dw-pre-prod].[pre].[v_SVV_Bilteller]"

target:
  server: ods
  object: "[dw-dwh-prod].[arc].[SVV_Bilteller_live]"
  identityColumn: SVVBiltellerPK        # required: it is what gives the table its primary key

load:
  keyColumns: [Trafikkregistreringspunkt, Felt, Dato]

versioning:
  temporal:
    enabled: true
    historySchema: ver                  # history lands in [ver].[SVV_Bilteller_live]
```

## See also

- [Ingestion flow: load, matchKeys, change, systemColumns](./ing-load.md): key columns, skip flags, dataset column, and batching.
- [Upsert and change detection](../concepts/upsert-and-change-detection.md): the plain two-step upsert SCD2 replaces, and the checksum change detection both share.
