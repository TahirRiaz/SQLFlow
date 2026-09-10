---
id: wiki-recipe-database-source
title: "Recipe: another database into the warehouse (scaffold, then ing)"
type: narrative
summary: "Browsing a foreign source, scaffolding runnable ing YAML from its tables, and treating the first load and the daily delta as two problems."
keywords:
  - recipe
  - ing
  - foreign database
  - scaffold
  - mysql
  - postgres
  - initLoad
  - incremental
  - detect-unique-key
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.SqlServer/Ingestion/InitLoadPlanner.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
referenceRefs:
  - flow-ing
  - cli-catalog
  - cli-catalog-scaffold
  - cli-detect-unique-key
  - guide-foreign-db-ingestion
  - guide-table-to-table-ingestion
related:
  - wiki-chaining-flows-through-the-lake
  - wiki-relational-incremental
  - wiki-recipe-backfill-and-replay
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: another database into the warehouse

No lake, no files: `ing` reads a source table and writes a target table. This is 512 of the estate's
751 flows, so it is the shape you will write most.

## 1. Look before you author

```bash
# what is in there
sqlflow catalog databases --source ${env:SQLFLOW_CONN_VENDORDB} --provider mysql
sqlflow catalog tables    --source ${env:SQLFLOW_CONN_VENDORDB} --provider mysql --database vendor --like "trip%"
sqlflow catalog columns   --source ${env:SQLFLOW_CONN_VENDORDB} --provider mysql --object vendor.trips

# find the real key rather than guessing one
sqlflow detect-unique-key --source ${env:SQLFLOW_CONN_VENDORDB} --object vendor.trips
```

Providers: `mssql`, `azdb`, `mysql`, `postgres`, `oracle`.

## 2. Scaffold rather than hand-write

```bash
# one table
sqlflow catalog scaffold \
  --source ${env:SQLFLOW_CONN_VENDORDB} --provider mysql \
  --object vendor.trips \
  --target ${env:SQLFLOW_CONN_ODS} --target-object arc.Vendor_Trips \
  --detect-keys \
  --name vendor_trips_01_ing \
  -o vendor/vendor_trips_01_ing.yaml

# the whole schema, one file per object
sqlflow catalog scaffold-all \
  --source ${env:SQLFLOW_CONN_VENDORDB} --provider mysql \
  --database vendor --schema public --like "trip%" \
  --target ${env:SQLFLOW_CONN_ODS} --target-schema arc \
  --no-views \
  --out vendor/
```

Scaffolding gets the column set and types right. You still decide the key, the window, and the
history policy.

## 3. The daily delta

```yaml
flowType: ing
name: vendor_trips_01_ing
batch: trips
schedule: vendor_daily

connections:
  src: ${env:SQLFLOW_CONN_VENDORDB}
  ods: ${env:SQLFLOW_CONN_ODS}

source:
  server: src
  object: "[vendor].[public].[trips]"
  ignoreColumns: [internal_blob, debug_payload]

target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Trips]"
  identityColumn: VendorTripsPK

load:
  keyColumns: [trip_id]
  batchUpsert: true
  batchUpsertRowCount: 500000

incremental:
  dateColumn: updated_at
  overlapDays: 3

schema:
  sync: true

systemColumns:
  insertedDate: true
  updatedDate: true
```

`overlapDays: 3` re-presents three days of rows every run so a row committed after the timestamp it
carries is still caught. It only works with a keyed upsert; paired with `load.mode: append` it
duplicates instead.

## 4. The first load is a different problem

```yaml
initLoad:
  enabled: true
  keyColumn: trip_id
  batchBy: 500000
  batchSize: 500000
  keyMaxValue: 1800000000

load:
  threads: 4
```

Run it once, then turn `initLoad.enabled` off and let the incremental window take over.

## 5. Variations worth knowing

**Push the predicate into the source SELECT.**

```yaml
source:
  filter: "status = 'SETTLED'"
  filterIsAppend: true       # the filtered set is a new slice, not the valid set to reconcile against
```

Without `filterIsAppend`, a row that later stops matching the filter stays in the target forever,
because the upsert INSERT is an anti-join and never deletes.

**A source with no usable date column.**

```yaml
incremental:
  columns: [trip_id]
  lookback: 5000            # ids committed late are contiguous blocks at watermark+1
```

**Current-state dimension, no history wanted.**

```yaml
target:
  object: "[WarehouseDb].[arc].[Vendor_Stations]"
  truncateBeforeLoad: true   # NOTE: under target:, not load:
```

`truncateBeforeLoad` under `load:` binds to nothing and is silently ignored. And a truncate-reload
flow whose schedule is paused loses whatever was current while it was off, permanently: there is no
window to re-read.

**Keep every version of a row.**

```yaml
versioning:
  temporal:
    enabled: true
    historySchema: arc
    historyTable: Vendor_Trips_History
    validFromColumn: ValidFrom
    validToColumn: ValidTo
    hiddenPeriodColumns: true
```

Mutually exclusive with `truncateBeforeLoad` and with `scd2`; both combinations are rejected up
front.

**Chain arc into a dimension.** An `ing` flow whose source is another flow's target binds
automatically, one wave later.

```yaml
source: { server: ods, object: "[WarehouseDb].[arc].[Vendor_Trips]" }
target: { server: ods, object: "[WarehouseDb].[edw].[Dim_Vendor_Trip]" }
surrogateKeys:
  - { table: "[skey].[VendorTrip]", column: VendorTripSK, keyColumns: [trip_id] }
```

## 6. Run and verify

```bash
sqlflow validate vendor/vendor_trips_01_ing.yaml
sqlflow run      vendor/vendor_trips_01_ing.yaml --show-sql
sqlflow lineage  vendor/ --explain vendor_trips_01_ing
```

`--show-sql` prints the generated DDL and merge, which is the fastest way to confirm the key and the
window are what you meant.
