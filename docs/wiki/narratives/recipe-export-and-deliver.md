---
id: wiki-recipe-export-and-deliver
title: "Recipe: getting data back out (files, documents, and other systems)"
type: narrative
summary: "The three ways data leaves: exp writes files, trl reshapes rows into documents, and inv hands off to a pipeline SQLFlow does not own."
keywords:
  - export
  - exp
  - trl
  - parquet
  - documents
  - json template
  - delivery
  - inv
  - adf
sourceRefs:
  - src/SqlFlow.Translate/TranslateFlowRunner.cs
  - src/SqlFlow.Translate/JsonTemplateRenderer.cs
  - src/SqlFlow.Translate/TranslateOutputWriter.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
referenceRefs:
  - flow-exp
  - flow-trl
  - flow-inv
  - flow-service-principals
related:
  - wiki-recipe-database-source
  - wiki-orchestration-and-scheduling
  - wiki-recipe-quality-and-monitoring
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: getting data back out

Ingestion is most of the estate, but a warehouse that cannot publish is half a system. Three flow
kinds handle the outbound direction, and they are not interchangeable.

| Need | Flow kind |
| --- | --- |
| A table or view as CSV / Parquet files | `exp` |
| Rows reshaped into documents (JSON) and delivered | `trl` |
| Hand off to a pipeline SQLFlow does not own | `inv` |

## exp: a table to files

```yaml
flowType: exp
name: vendor_orders_export
batch: export
schedule: vendor_daily

connections:
  dwh: ${env:SQLFLOW_CONN_ODS}

source:
  server: dwh
  object: WarehouseDb.arc.Vendor_Orders
  filter: "Status = 'SETTLED'"

target:
  path: abfss://fs@account.dfs.core.windows.net/export/vendor/orders
```

Chunk a large export rather than writing one enormous file. The chunking key is the same decision as
a partitioning key: choose what the consumer filters on.

```yaml
target:
  path: abfss://fs@account.dfs.core.windows.net/export/vendor/orders
  format: parquet
  chunkBy: month          # or day, or an integer key
  chunkColumn: OrderedAt
```

Two things worth knowing:

**`source.filter` accepts a bare predicate or one already prefixed with `AND`.** Both parse to the
same thing, so a predicate copied out of a larger WHERE clause does not need editing.

**`source.withHint` passes a query hint through** when the export needs a specific index and the
optimizer will not choose it:

```yaml
source:
  object: WarehouseDb.edw.Fact_Order
  withHint: "WITH (INDEX([NCI_OrderDate]))"
```

An export writing to a lake path is a **producer** in the lineage graph, exactly like an acquisition.
Anything reading that path binds to it, so an export feeding another team's ingestion is a visible
edge rather than an out-of-band arrangement.

## trl: rows to documents

`exp` writes the shape the table already has. `trl` writes a shape the consumer specified: it maps a
query result through a declared JSON template, producing arbitrarily nested documents.

```yaml
flowType: trl
name: vendor_orders_translate
batch: translate

connections:
  dwh: ${env:SQLFLOW_CONN_ODS}

source:
  server: dwh
  query: SELECT OrderId, CustomerRef, OrderedAt, TotalAmount FROM WarehouseDb.arc.Vendor_Orders

template:
  id: "vendor:order:{OrderId}"
  kind: "com.example:order:1.0.0"
  data:
    Reference: "{CustomerRef}"
    PlacedAt:  "{OrderedAt}"
    Amount:    { $column: TotalAmount, $type: string }

output:
  path: abfss://fs@account.dfs.core.windows.net/export/vendor/orders-json
  mode: filePerDocument
  fileName: "order_{OrderId}"
```

`{Column}` interpolates into a string. `{ $column: X, $type: string }` places the value with an
explicit type, which is how a numeric column reaches a consumer that demands a string without a cast
in the query.

Nested collections come from named `datasets` bound to the parent row:

```yaml
datasets:
  - name: lines
    query: SELECT OrderId, Sku, Quantity FROM WarehouseDb.arc.Vendor_OrderLine
    bind: [OrderId]
```

Each parent document then carries its matching child rows, so a one-to-many becomes one nested
document rather than a join the consumer has to re-assemble.

**Documents are always saved to the destination first, then delivered.** The files on disk are the
record of what was sent, which is what makes a rejected delivery diagnosable and a redelivery
possible without re-querying.

## inv: hand off to another system

When the next step belongs to a pipeline SQLFlow does not own, `inv` triggers it and polls, so the
external step stays a node in the same dependency graph rather than a gap in it.

```yaml
flowType: inv
name: vendor_publish_00_inv
batch: invoke
schedule: vendor_daily

servicePrincipals:
  datafactory:
    subscriptionId: ${env:AZ_SUBSCRIPTION_ID}
    resourceGroup: my-resource-group
    dataFactoryName: my-data-factory

invoke:
  type: adf
  servicePrincipal: datafactory
  pipeline: PublishVendorOrders
  onErrorResume: false
```

`onErrorResume: false` makes the external failure stop the wave. Set it true only when the downstream
work is genuinely optional, because a silent skip is worse than a failed run.

## Who consumes this, and what breaks if it changes

Declaring subscribers turns the impact question into a query instead of a conversation. Each
subscriber query is parsed into read edges, so lineage can answer which report depends on which
column.

```yaml
# subscribers/analytics.subscribers.yaml
subscribers:
  Orders_Dashboard:
    type: powerbi
    owner: analytics-team
    server: ${env:SQLFLOW_CONN_ODS}
    description: Daily order volume and revenue.
    url: https://app.example/reports/orders
    queries:
      - name: volume_by_day
        sql: SELECT OrderedAt, COUNT(*) FROM edw.Fact_Order GROUP BY OrderedAt
```

```bash
sqlflow lineage flows/ --of edw.Fact_Order --down
```

This changes no behaviour. It exists so the answer lives in the repository rather than in someone's
memory, which is the difference between a safe schema change and a hopeful one.
