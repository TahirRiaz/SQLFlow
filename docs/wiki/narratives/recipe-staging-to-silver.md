---
id: wiki-recipe-staging-to-silver
title: "Recipe: staging to silver, where the typed contract is actually written"
type: narrative
summary: "The typed view is the contract: writing it, keeping a rebuilt table's old shape, and running two eras behind one name."
keywords:
  - staging
  - silver
  - pre
  - arc
  - typed view
  - generateView
  - compatibility view
  - era split
  - contract
sourceRefs:
  - src/SqlFlow.Core/Engine/TypeInferencer.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Core/Ingestion/IngestionFlow.cs
referenceRefs:
  - concept-pre-ingestion-transform
  - flow-transform
  - concept-type-inference
  - concept-upsert-and-change-detection
related:
  - wiki-chaining-flows-through-the-lake
  - wiki-recipe-dimension-and-surrogate-keys
  - wiki-string-first-landing
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: staging to silver

Three zones, two moves:

```
raw (lake files)  --file flow-->  pre.<Table>      every column a string
                                  pre.v_<Table>    the TYPED CONTRACT
                  --ing flow-->   arc.<Table>      curated, keyed, incremental
```

**The typed view is where the contract lives.** The staging table is deliberately dumb; the curated
table is built from the view. So every type decision, every rename, every derived column and every
legacy compatibility concession is written in one place you can read and re-run.

## Writing the view

`transform.generateView: true` builds `pre.v_<Table>` from the `columns` list. `@ColName` is the
placeholder for the column named by `name`.

```yaml
transform:
  generateView: true
  columns:
    # 1. plain types
    - { name: OrderId,     expr: "CAST(@ColName AS bigint)" }
    - { name: CustomerRef, expr: "CAST(@ColName AS varchar(64))" }

    # 2. a number that may arrive blank: guard the CAST, do not let it fail the view
    - { name: Quantity,    expr: "CASE WHEN LEN(@ColName) > 0 THEN CAST(@ColName AS int) ELSE NULL END" }

    # 3. a timestamp whose format the vendor is not consistent about
    - { name: OrderedAt,   expr: "COALESCE(TRY_CONVERT(datetime,@ColName,127),TRY_CONVERT(datetime,@ColName,21))" }

    # 4. a column the target contract requires but the source never sends
    - { name: LegacyFlag,  expr: "CAST(NULL AS bit)" }

    # 5. a derived column: the view is the right place for it
    - { name: OrderMonth,  expr: "CONVERT(char(7), TRY_CONVERT(date,@ColName,23), 126)" }

    # 6. provenance the merge key and the watermark depend on
    - { name: FileDate_DW, expr: "CAST(@ColName AS decimal(14,0))" }
    - { name: FileName_DW, expr: "CAST(@ColName AS varchar(255))" }
    - { name: DataSet_DW,  expr: "CAST(@ColName AS decimal(14,0))" }
```

Two more knobs worth knowing:

```yaml
    - { name: InternalNote, expr: "CAST(@ColName AS varchar(max))", excludeFromView: true }
    - { name: RowHash,      expr: "HASHBYTES('SHA2_256', @ColName)", virtual: true }
    - { name: SourceRef,    expr: "CAST(@ColName AS varchar(50))", as: BusinessKey, order: 1 }
```

`excludeFromView` keeps a landed column out of the contract, `virtual` adds a column the source does
not have, `as` renames, and `order` pins position.

## Three rules for writing type expressions

**Guard every numeric and date CAST.** A bare `CAST(@ColName AS int)` fails the whole view the first
time the source sends a blank. `CASE WHEN LEN(@ColName) > 0 THEN ... ELSE NULL END` or `TRY_CONVERT`
degrades one value instead of the run.

**Pick the CONVERT style explicitly, and hard-code it.** Style inference is not stable across
locales, and a style that silently returns NULL turns a whole column into NULLs without failing.
Prefer an explicit style code, and `COALESCE` two of them where the vendor is inconsistent.

**Remember empty is NULL, not `''`.** A landed blank cell is `NULL`, so a ported predicate written
against a system that stored `''` changes meaning here. See
[string-first-landing](../decisions/string-first-landing.md).

## The move to silver

```yaml
flowType: ing
name: vendor_orders_02_ing
batch: orders
schedule: vendor_daily

connections:
  pre: ${env:SQLFLOW_CONN_PRE}
  ods: ${env:SQLFLOW_CONN_ODS}

source:
  server: pre
  object: "[StagingDb].[pre].[v_Vendor_Orders]"     # the view, never the table

target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Orders]"
  identityColumn: VendorOrdersPK

load:
  keyColumns: [OrderId]

incremental:
  columns: [FileDate_DW]
  lookback: 1

schema:
  sync: true

systemColumns:
  insertedDate: false
  updatedDate: true
```

## Keeping a rebuilt table's old shape

A framework-built table almost never matches a hand-built predecessor column for column: the
surrogate key and audit columns are appended, so **positions shift even when the column set is
identical**. Anything doing `SELECT *` or positional access breaks.

Do not reshape the new table. Interpose a view under the old name:

```sql
-- compat_views.sql, kept beside the flows; a one-time DDL operation, not part of the run
CREATE OR ALTER VIEW arc.LegacyOrderName AS
SELECT
    CAST(OrderId      AS bigint)       AS OrderId,
    CAST(CustomerRef  AS varchar(50))  AS CustomerRef,   -- old prod was varchar(50), not 64
    CAST(Quantity     AS int)          AS Quantity,
    CAST(OrderedAt    AS datetime)     AS OrderedAt
FROM arc.Vendor_Orders;
```

Cast every column to its old type, in the old order. Consumers keep querying the old name and see no
change. Skip the view only when the new table is already byte-for-byte identical.

## Two eras behind one name

A frozen historical era and a live feed that collide on a table name are the same problem. Keep them
apart physically and union them behind the original name:

```sql
CREATE OR ALTER VIEW arc.Vendor_Orders AS
SELECT * FROM arc.Vendor_Orders_Hist
UNION ALL
SELECT * FROM arc.Vendor_Orders_Live;
```

**Split the eras by a key anti-join, not by a cutover date.** A date boundary and the actual data
rarely agree: rows straddle it, and the overlap either duplicates or disappears. Point the live
`ing` flow at a guard view that excludes anything already in the historical table.

## Verify before you trust it

```bash
sqlflow run vendor/vendor_orders_01_csv.yaml
sqlflow run vendor/vendor_orders_02_ing.yaml --show-sql
```

```sql
-- a column can match name, type and position and still be 100% NULL
SELECT COUNT(*) AS Rows,
       SUM(CASE WHEN OrderedAt IS NULL THEN 1 ELSE 0 END) AS NullOrderedAt,
       SUM(CASE WHEN Quantity  IS NULL THEN 1 ELSE 0 END) AS NullQuantity
FROM arc.Vendor_Orders;
```

Diff NULL counts per column against the system being replaced, and spot-check values. A column that
is entirely NULL is the signature of a CONVERT style that never matched, and row counts will not
show it.

For a byte-sensitive comparison use a real checksum over values rather than a length sum, which
passes happily on a codepage-mangled table.
