---
id: wiki-recipe-quality-and-monitoring
title: "Recipe: noticing when a pipeline succeeds and is still wrong"
type: narrative
summary: "Anomaly checks on what a load produces, the derived health check any ing flow can carry, and the assertions a model will not write."
keywords:
  - health check
  - hc
  - anomaly
  - monitoring
  - data quality
  - maturityDays
  - assertions
  - silent failure
sourceRefs:
  - src/SqlFlow.HealthCheck/HealthCheckEngine.cs
  - src/SqlFlow.Core/HealthChecks/HealthCheckRunResult.cs
referenceRefs:
  - flow-hc
  - cli-healthcheck
  - flow-ing-quality
  - concept-healthcheck-engine
related:
  - wiki-recipe-fan-in-shared-target
  - wiki-recipe-inspect-and-debug
  - wiki-recipe-export-and-deliver
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: noticing when a pipeline succeeds and is still wrong

Almost every failure worth catching in this system is a **successful run**. Zero rows is a legitimate
outcome, so a starved flow, a watermark past its data, a filter that changed meaning, and a source
that quietly stopped all report success. Monitoring the exit code catches none of them.

## Zero-configuration check

No pipeline file needed. Point it at a table and it auto-detects the date column and runs the full
detection stack:

```bash
sqlflow healthcheck --source ${env:SQLFLOW_CONN_ODS} --object WarehouseDb.arc.Vendor_Orders
```

Use it to sanity-check a table you just built, or to sweep something you inherited.

## A watched metric

```yaml
flowType: hc
name: vendor_orders_watch
batch: quality
schedule: vendor_daily
description: Watches order volume and value for missing or abnormal loads.

connections:
  dwh: ${env:SQLFLOW_CONN_ODS}

target:
  server: dwh
  object: WarehouseDb.arc.Vendor_Orders

dateColumn: OrderedAt
baseValue: COUNT(*)

metrics:
  - name: order_count
    expression: COUNT(*)
  - name: total_value
    expression: SUM(TotalAmount)
  - name: distinct_customers
    expression: COUNT(DISTINCT CustomerRef)

maturityDays: 2
```

**`maturityDays` is the setting that decides whether this is useful or noise.** A feed that keeps
amending the last two days is not anomalous on day zero; it is incomplete. Set it to the number of
days after which the number should be final, and the check stops alarming on data that has not
finished arriving.

Pick metrics that fail differently from each other. `COUNT(*)` catches a missing load;
`SUM(TotalAmount)` catches a load that arrived with the values wrong; `COUNT(DISTINCT ...)` catches
one partition going silent while the total stays plausible.

## Attaching a check to an existing flow

Any `ing` flow can carry one, which creates a derived health-check pipeline over its own target
without a second file:

```yaml
flowType: ing
name: vendor_orders_02_ing

healthCheck:
  dateColumn: OrderedAt
  baseValue: COUNT(*)
  maturityDays: 2
```

The derived pipeline is named `<flow>_hc`. This is the low-friction option: prefer it over a separate
`hc` file unless the check needs metrics the load itself does not imply.

## Running one

```bash
sqlflow run vendor/vendor_orders_watch.yaml
sqlflow run vendor/vendor_orders_watch.yaml --assertions-only    # skip the ML stack, evaluate assertions
sqlflow run vendor/vendor_orders_watch.yaml --fail-on-anomaly    # non-zero exit, so a scheduler notices
sqlflow run vendor/vendor_orders_watch.yaml --retrain            # rebuild the model after a known step change
```

`--fail-on-anomaly` is what turns a check into an alert. Without it the anomaly is recorded and the
run still succeeds, which is right for a dashboard and wrong for a gate.

`--retrain` after a deliberate step change (a new region onboarded, a pricing change) stops the model
alarming on the new normal for weeks.

## The checks a model will not write for you

Three failure modes in this system are structural, and worth a plain SQL assertion because they are
invisible to a total row count.

**A shared target starving its writers.** The total keeps growing while individual partitions freeze.

```sql
SELECT SourceSystemId, MAX(FileDate_DW) AS Frontier, COUNT(*) AS Rows
FROM arc.Sales_Fact
GROUP BY SourceSystemId;
```

Alert when any partition's frontier falls behind the newest by more than a day. See
[recipe-fan-in-shared-target](recipe-fan-in-shared-target.md).

**A column that is present, correctly typed, and entirely NULL.** The signature of a CONVERT style
that never matched.

```sql
SELECT SUM(CASE WHEN OrderedAt IS NULL THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS PctNull
FROM arc.Vendor_Orders;
```

**A rolling-window source whose row count tracks files rather than facts.** Growth looks healthy and
means nothing.

```sql
SELECT FileDate_DW, COUNT(*) AS Rows, COUNT(DISTINCT OrderId) AS DistinctOrders
FROM arc.Vendor_Orders
GROUP BY FileDate_DW ORDER BY FileDate_DW DESC;
```

`Rows` far above `DistinctOrders`, run after run, means the merge key includes file provenance. See
[upsert-and-history](../patterns/upsert-and-history.md).

## What the run itself already tells you

Every run writes `run.json` with the events, the row counts, the watermark before and after, and the
SQL trace. Two fields answer most questions before any monitoring is involved:

- **`watermarkAfter`** explains a successful run that loaded nothing, almost every time.
- **files written versus files unchanged** distinguishes "the source produced new data" from "the
  source produced the same data again". A summary quoting only the landed count makes a run that
  wrote nothing read as new data arriving.

```bash
sqlflow run vendor/vendor_orders_02_ing.yaml --json
cat .sqlflow/runs/vendor_orders_02_ing/*/run.json | head -60
```

## Where to put the alarm

A check is only worth writing if someone sees it fire. Wire `--fail-on-anomaly` into the scheduled
run so a failure surfaces the way any other failed flow does, rather than adding a second channel
that needs its own attention.
