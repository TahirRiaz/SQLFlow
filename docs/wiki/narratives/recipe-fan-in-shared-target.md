---
id: wiki-recipe-fan-in-shared-target
title: "Recipe: pushing several regions or tenants into one table"
type: narrative
summary: "N flows merging into one table: the discriminator in the merge key, the scoped watermark probe that stops them starving each other, and serial scheduling."
keywords:
  - fan-in
  - multi-region
  - shared target
  - discriminator
  - incrementalClause
  - tenant
  - partition
  - starvation
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
referenceRefs:
  - concept-shared-target-watermarks
  - flow-ing
  - flow-schedule
  - concept-upsert-and-change-detection
related:
  - wiki-recipe-incremental-load
  - wiki-upsert-and-history
  - wiki-orchestration-and-scheduling
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: pushing several regions or tenants into one table

N flows, one per region / source system / tenant, all merging into one table that separates them
with a discriminator column.

```
region_a_00_cpy -> pre.v_Sales_RegionA ─┐
region_b_00_cpy -> pre.v_Sales_RegionB ─┼──> arc.Sales_Fact
region_c_00_cpy -> pre.v_Sales_RegionC ─┘     (discriminated by SourceSystemId)
```

Each region keeps its own acquisition, its own staging table and its own typed view. Only the final
target is shared.

## The three things that must be true

```yaml
# region_a/region_a_sales_02_ing.yaml
flowType: ing
name: region_a_sales_02_ing
batch: sales
schedule: region_a_daily

connections:
  pre: ${env:SQLFLOW_CONN_PRE}
  ods: ${env:SQLFLOW_CONN_ODS}

source:
  server: pre
  object: "[StagingDb].[pre].[v_Sales_RegionA]"
  # 2. SCOPE THE PROBE. Three flows share this target; an unscoped MAX() returns
  #    whatever region loaded last, and the other two read nothing.
  incrementalClause: "AND [SourceSystemId] = 10"

target:
  server: ods
  object: "[WarehouseDb].[arc].[Sales_Fact]"
  identityColumn: SalesFactPK

load:
  # 1. DISCRIMINATOR IN THE MERGE KEY, so two regions cannot collide on the same business id.
  keyColumns: [SourceSystemId, BusinessDate, RecordId]

incremental:
  columns: [FileDate_DW]
  lookback: 1

schema:
  sync: true
```

**1. The discriminator is part of `load.keyColumns`.** Without it, region 10 and region 20 sharing a
`RecordId` overwrite each other on every run. The upsert has no other notion of which region a row
came from.

**2. `source.incrementalClause` scopes the watermark probe.** It is appended to the probe **on the
target**, not to the source read:

```sql
SELECT MAX(FileDate_DW) FROM arc.Sales_Fact WHERE 1=1 AND [SourceSystemId] = 10
```

Each flow now tracks its own frontier. The discriminator column must exist on the target; check that
before rolling the change out, not after.

**3. Only one flow may own the schema.** `schema.sync: true` on three flows writing one table means
three writers can each evolve it. Either keep the shape agreed and frozen, or let one flow own
evolution and set the others to `sync: false`.

## Why the unscoped version is so hard to spot

Without the `incrementalClause`, the flows run in sequence and:

1. Region A probes the shared max, reads its new rows, merges. The shared max is now A's latest.
2. Region B probes the **same table**, gets A's value, finds nothing beyond it in its own source.
3. B loads zero rows. So does C.

- **Every run succeeds.** Zero rows is a legitimate outcome, not an error.
- **Stage 1 looks healthy.** The staging tables keep filling normally. The data stops one stage short.
- **It never self-corrects.** The leading flow re-advances the shared mark on every run.
- **Total row count keeps growing,** because the leading writer still works. Only counts **per
  discriminator value** reveal the gap.

```sql
-- the query that actually finds it
SELECT SourceSystemId, MAX(FileDate_DW) AS Frontier, COUNT(*) AS Rows
FROM arc.Sales_Fact
GROUP BY SourceSystemId
ORDER BY SourceSystemId;
```

Regions frozen at an old frontier while one is current is the signature.

## Recovery needs no backfill

The data is not lost. It is sitting in staging above each starved flow's own frontier, waiting for a
predicate that selects it.

```bash
# after adding the incrementalClause, an ordinary run picks it all up
sqlflow run region_b/region_b_sales_02_ing.yaml
```

Use `--full` only if the upstream staging data has actually been discarded.

## Schedule them serially

Several regions hitting one target concurrently is lock contention and a merge fighting itself.
Chain the region schedules with `after` so one fire walks them in order:

```yaml
# region_a/schedules.yaml
schedules:
  region_a_daily:
    cron: "0 3 * * *"
    timezone: Europe/Oslo
    enabled: true
  region_b_daily:
    after: region_a_daily
    enabled: true
  region_c_daily:
    after: region_b_daily
    enabled: true
```

Each region's own `cpy` -> `csv` -> `ing` chain still wave-orders inside its slot, because that comes
from lineage rather than from the schedule.

**A schedule with `enabled: false` and no cron, no intervalSeconds and no `after` is dropped by the
catalog sync entirely,** so it does not exist to re-enable later. Give a disabled schedule a cron.

## The same shape, other names

This is not only regions. The identical recipe covers one table fed by several tenants, by several
source systems, or by a historical era and a live feed running side by side. When two eras collide on
a name rather than on a discriminator, keep `_hist` and `_live` tables and union them behind a view
under the original name; see [upsert-and-history](../patterns/upsert-and-history.md).

In the estate this shape carries five regions across eight datasets each, all merging into shared
`arc` tables, scheduled serially by `after`.
