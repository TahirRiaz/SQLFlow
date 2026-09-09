---
id: wiki-recipe-dimension-and-surrogate-keys
title: "Recipe: building a dimension and its surrogate keys"
type: narrative
summary: "Minting surrogate keys in a key table, projecting arc into a dimension without reshaping it, and the snapshot trap that truncates history."
keywords:
  - dimension
  - surrogate key
  - skey
  - edw
  - star schema
  - scd
  - date dimension
  - cal
  - conformed
sourceRefs:
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
referenceRefs:
  - flow-ing-quality
  - flow-ing-versioning
  - flow-cal
  - concept-upsert-and-change-detection
related:
  - wiki-recipe-staging-to-silver
  - wiki-upsert-and-history
  - wiki-recipe-fan-in-shared-target
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: building a dimension and its surrogate keys

The third hop. `arc` holds the curated source-shaped table; `edw` holds the model consumers query.

```
arc.Vendor_Customer  --ing (03)-->  edw.Dim_Customer
        │
        └── skey.Customer   the surrogate key table, minted during the arc load
```

## 1. Mint the surrogate key where the business key lands

`surrogateKeys` is declared on the flow that loads `arc`, not on the dimension flow. Each entry
maintains a key table: one row per distinct business key, with a stable generated surrogate.

```yaml
# vendor/vendor_customer_02_ing.yaml   (arc load)
flowType: ing
name: vendor_customer_02_ing
batch: customer

source: { server: pre, object: "[StagingDb].[pre].[v_Vendor_Customer]" }
target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Customer]"
  identityColumn: VendorCustomerPK

load:
  keyColumns: [CustomerRef]

surrogateKeys:
  - table: "[WarehouseDb].[skey].[Customer]"
    column: CustomerId
    keyColumns: [CustomerRef]
```

`keyColumns` is the **business** key (natural, from the source). `column` is the surrogate the model
joins on. `table` is where the mapping lives, so the surrogate survives a reload of `arc`: rebuilding
the fact table does not renumber anything, because the numbers are not in the fact table.

`identityColumn` is a different thing: a per-table physical PK for the loaded row. Do not use it as a
dimension key.

A composite business key is just more columns:

```yaml
surrogateKeys:
  - table: "[WarehouseDb].[skey].[Product]"
    column: ProductId
    keyColumns: [SourceSystemId, ProductCode]
```

## 2. Project arc into the dimension

```yaml
# vendor/vendor_customer_03_ing.yaml   (dimension load)
flowType: ing
name: vendor_customer_03_ing
batch: customer
schedule: vendor_daily

connections:
  arc: ${env:SQLFLOW_CONN_ODS}
  edw: ${env:SQLFLOW_CONN_ODS}

source:
  server: arc
  object: "[WarehouseDb].[arc].[Vendor_Customer]"
  ignoreColumns: [UpdatedDate_DW]     # audit columns are stamped on the dimension, never copied

target:
  server: edw
  object: "[WarehouseDb].[edw].[Dim_Customer]"

load:
  keyColumns: [CustomerId]

schema:
  sync: false                          # the dimension's shape is a contract; never let a load reshape it

systemColumns:
  insertedDate: true
  updatedDate: true
```

Three deliberate choices here:

**`schema.sync: false`.** A dimension is a published contract. Let the arc table evolve with the
source; keep the dimension's shape a decision someone makes on purpose. The `ing` -> `ing` hop binds
automatically one wave later, so it needs no extra wiring.

**`ignoreColumns: [UpdatedDate_DW]`.** Copying the source's audit column over the dimension's own
would make "when did this dimension row change" mean "when did the arc row change". Let the engine
stamp its own.

**`keyColumns: [CustomerId]`** is the surrogate, because the dimension's identity is the surrogate.

A key table can itself be the source of a dimension when the mapping is all the model needs:

```yaml
source: { server: skey, object: "[WarehouseDb].[skey].[Customer]" }
target: { server: edw,  object: "[WarehouseDb].[edw].[Dim_Customer]" }
load:   { keyColumns: [CustomerId] }
```

## 3. Keeping history in the dimension

Type 1 (overwrite) is the default: the merge updates the row in place.

For full history, put system-versioning on the dimension rather than hand-rolling validity columns:

```yaml
versioning:
  temporal:
    enabled: true
    historySchema: edw
    historyTable: Dim_Customer_History
    validFromColumn: ValidFrom
    validToColumn: ValidTo
    periodPrecision: 2
    hiddenPeriodColumns: true
```

Mutually exclusive with `truncateBeforeLoad` and with `scd2`; both combinations are rejected up
front rather than half-applied.

## 4. A date dimension needs no source

```yaml
# calendar/calendar_dim_00_cal.yaml
flowType: cal
name: calendar_dim_00_cal
calendar:
  server: edw
  object: "[WarehouseDb].[edw].[Dim_Date]"
  from: "2015-01-01"
  to: "2035-12-31"
  timezone: Europe/Oslo
  observances: true
```

Generated from rules alone, then merged so the target holds exactly the declared range. Regenerate by
widening `from`/`to` and re-running.

## The trap: a snapshot source truncates your dimension

A source that only exposes **current state** returns today's members and nothing about members that
have gone away. Load that straight into a dimension with a truncate-reload, or with a merge whose
source is the whole snapshot, and every retired member disappears from the model along with every
fact that referenced it.

This does not announce itself. The load succeeds, the row count is plausible, and only a query for a
retired member reveals the hole.

```yaml
# the dimension keeps everything it has ever seen; the snapshot only ever adds or updates
load:
  keyColumns: [CustomerId]        # merge, never truncateBeforeLoad, on a snapshot source
```

Closing the gap needs a backfill from whatever still remembers the retired members, then a re-run of
the dimension load **windowed to the range you backfilled**, because its own watermark is already
past those rows. See [recipe-backfill-and-replay](recipe-backfill-and-replay.md).

The same blindness applies upstream: a live id-discovery call (`iterate.ids_from`) only sees the
current fleet, so history for departed entities is never fetched at all. See
[api-fanout](../patterns/api-fanout.md).

## Verify

```sql
-- every business key has exactly one surrogate
SELECT CustomerRef, COUNT(DISTINCT CustomerId) AS Surrogates
FROM skey.Customer GROUP BY CustomerRef HAVING COUNT(DISTINCT CustomerId) > 1;

-- no fact points at a surrogate the dimension does not have
SELECT COUNT(*) AS Orphans
FROM arc.Vendor_Order f
LEFT JOIN edw.Dim_Customer d ON d.CustomerId = f.CustomerId
WHERE d.CustomerId IS NULL;
```

```bash
sqlflow run     vendor/vendor_customer_03_ing.yaml --show-sql
sqlflow lineage vendor/ --of edw.Dim_Customer --up
```

The `--up` walk should show `pre` -> `arc` -> `edw` and nothing surprising in between.
