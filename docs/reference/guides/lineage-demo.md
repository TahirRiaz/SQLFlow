---
id: guide-lineage-demo
title: "Lineage demo walkthrough: a seven-flow chained estate"
type: guide
summary: Run the samples/lineage-demo estate end to end and land a five-wave execution plan with declared and derived lineage in the catalog.
keywords:
  - lineage demo
  - waves
  - chained flows
  - samples
  - walkthrough
  - db sync
  - connect
related:
  - cli-lineage
  - concept-lineage-tiers
  - concept-pre-ingestion-transform
  - cli-db
sourceRefs:
  - samples/lineage-demo/README.md
  - samples/lineage-demo/10-land-orders.flow.yaml
  - samples/lineage-demo/11-land-customers.flow.yaml
  - samples/lineage-demo/20-ing-orders.flow.yaml
  - samples/lineage-demo/21-ing-customers.flow.yaml
  - samples/lineage-demo/30-build-order-fact.flow.yaml
  - samples/lineage-demo/40-build-country-rollup.flow.yaml
  - samples/lineage-demo/50-build-exec-kpi.flow.yaml
  - samples/lineage-demo/sql/usp_BuildOrderFact.sql
  - samples/lineage-demo/sql/usp_BuildRollups.sql
  - src/SqlFlow.Cli/Program.cs
---

# Lineage demo walkthrough: a seven-flow chained estate

`samples/lineage-demo` is a seven-flow estate that exercises the full chained topology and lands a five-wave execution plan in the catalog. Two file landings feed typed transformation views, two ingestion flows read those views into keyed targets, and three stored-procedure flows chain a fact table through a country rollup into an executive KPI.

## Topology

```text
orders.csv    -> demo.Orders_Pre    -> demo.vOrders_Pre    -> demo.Orders    \
                                                                              -> demo.Fact_OrderSummary
customers.csv -> demo.Customers_Pre -> demo.vCustomers_Pre -> demo.Customers /       |
                                                                                     v
                       demo.Fact_OrderSummary -> demo.Fact_CountryRollup -> demo.Kpi_Executive
                                     (usp_BuildCountryRollup)      (usp_BuildExecKpi)
```

| Flow | Kind | Wave | What it does |
| --- | --- | --- | --- |
| `demo-land-orders` | file | 1 | CSV into a raw pre table; refreshes the typed view `demo.vOrders_Pre` (inference plus a declared `vehicle_type_clean` transform) |
| `demo-land-customers` | file | 1 | CSV into a raw pre table; refreshes `demo.vCustomers_Pre` (inference only) |
| `demo-ing-orders` | ing | 2 | Reads the view `demo.vOrders_Pre` into `demo.Orders` via keyed upsert on `order_id` |
| `demo-ing-customers` | ing | 2 | Reads the view `demo.vCustomers_Pre` into `demo.Customers` via keyed upsert on `customer_id` |
| `demo-build-order-fact` | sp | 3 | `demo.usp_BuildOrderFact` joins both targets into `demo.Fact_OrderSummary` |
| `demo-build-country-rollup` | sp | 4 | `demo.usp_BuildCountryRollup` reads `demo.Fact_OrderSummary` and writes `demo.Fact_CountryRollup` |
| `demo-build-exec-kpi` | sp | 5 | `demo.usp_BuildExecKpi` reads `demo.Fact_CountryRollup` and writes `demo.Kpi_Executive` |

The two rollup procedures make the chain three procedures deep: `demo.Fact_OrderSummary` is not a terminal output but a consumed intermediate, and `demo.Kpi_Executive` is the estate's final product.

## How the dependencies resolve

The waves come from two different lineage tiers:

- **land to ing (declared tier).** The landing flows declare the views they write (the `transform` section produces `demo.vOrders_Pre` and `demo.vCustomers_Pre`), and the ingestion flows name those views as their source objects. These edges resolve offline from the YAML alone.
- **ing to sp and sp to sp (derived tier).** The `sp` documents only declare which procedure they execute. The reads and writes inside each procedure body (`demo.usp_BuildOrderFact` reads `demo.Orders` and `demo.Customers`; `demo.usp_BuildCountryRollup` reads `demo.Fact_OrderSummary`; `demo.usp_BuildExecKpi` reads `demo.Fact_CountryRollup`) are derived by fetching the module text from the live database's `sys.sql_modules` and parsing it. That is why the catalog sync below passes `--connect`: without it these edges, and therefore waves 3 through 5, are not computed.

## Prerequisites

- A reachable SQL Server database (the sample uses a local `SqlFlowCatalogTests`).
- `sqlcmd` on the PATH for the one-time procedure install.
- Two environment variables. `SQLFLOW_DEMO_DB` is referenced by every flow document via `${env:SQLFLOW_DEMO_DB}`; `SQLFLOW_CATALOG_DB` is the default connection reference for `sqlflow db` (the `--db` option defaults to `${env:SQLFLOW_CATALOG_DB}`). The demo points both at the same database.

```bash
export SQLFLOW_DEMO_DB="Server=localhost;Database=SqlFlowCatalogTests;Integrated Security=True;TrustServerCertificate=True"
export SQLFLOW_CATALOG_DB="$SQLFLOW_DEMO_DB"
```

## Step 1: install the stored procedures

Apply the two SQL scripts once. Each script creates the `demo` schema if it is missing and uses `CREATE OR ALTER`, so re-running is safe.

```bash
cd samples/lineage-demo
cat sql/usp_BuildOrderFact.sql | sqlcmd -S localhost -d SqlFlowCatalogTests -E
cat sql/usp_BuildRollups.sql   | sqlcmd -S localhost -d SqlFlowCatalogTests -E
```

`sql/usp_BuildOrderFact.sql` defines `demo.usp_BuildOrderFact`, which drops and rebuilds `demo.Fact_OrderSummary` from a join of `demo.Orders` and `demo.Customers`. `sql/usp_BuildRollups.sql` defines the two downstream procedures, `demo.usp_BuildCountryRollup` and `demo.usp_BuildExecKpi`. Deferred name resolution means the target tables need not exist when the procedures are created; each `SELECT ... INTO` creates its table at run time.

## Step 2: run the seven flows in file-number order

```bash
sqlflow run 10-land-orders.flow.yaml
sqlflow run 11-land-customers.flow.yaml
sqlflow run 20-ing-orders.flow.yaml
sqlflow run 21-ing-customers.flow.yaml
sqlflow run 30-build-order-fact.flow.yaml
sqlflow run 40-build-country-rollup.flow.yaml
sqlflow run 50-build-exec-kpi.flow.yaml
```

Run artifacts land under `.sqlflow/runs/<flow-name>/` next to the flow files and feed the observed lineage tier.

### The landing flows

`10-land-orders.flow.yaml` demonstrates type inference combined with one declared column transform. `@ColName` is the placeholder for the transform's source column (`vehicle_type` here), so the expression reads the raw string column and the view exposes it as `vehicle_type_clean`:

```yaml
name: demo-land-orders
source:
  type: csv
  location: data/orders.csv
target:
  connection: ${env:SQLFLOW_DEMO_DB}
  schema: demo
  table: Orders_Pre
transform:
  inferTypes: true
  columns:
    - name: vehicle_type
      expr: "UPPER(CAST(@ColName AS varchar(50)))"
      as: vehicle_type_clean
      type: varchar(50)
```

`11-land-customers.flow.yaml` is the inference-only variant: the same shape with `transform.inferTypes: true` and no `columns` list. Each landing flow loads the CSV into a raw pre table (`demo.Orders_Pre`, `demo.Customers_Pre`) and refreshes a typed transformation view over it (`demo.vOrders_Pre`, `demo.vCustomers_Pre`).

### The ingestion flows

The ingestion flows read the views, not the pre tables. Reading the view is what carries the inferred and declared types into the target, and what orders each ingestion flow after its landing flow in lineage. From `20-ing-orders.flow.yaml`:

```yaml
flowType: ing
name: demo-ing-orders
connections:
  src: ${env:SQLFLOW_DEMO_DB}
  dwh: ${env:SQLFLOW_DEMO_DB}
source:
  server: src
  object: SqlFlowCatalogTests.demo.vOrders_Pre
target:
  server: dwh
  object: SqlFlowCatalogTests.demo.Orders
load:
  keyColumns: [order_id]
```

Note the three-part object names; see the gotchas below.

### The stored-procedure flows

Each `sp` flow declares only the procedure it executes. From `30-build-order-fact.flow.yaml`:

```yaml
flowType: sp
name: demo-build-order-fact
connections:
  dwh: ${env:SQLFLOW_DEMO_DB}
procedure:
  server: dwh
  object: SqlFlowCatalogTests.demo.usp_BuildOrderFact
```

`40-build-country-rollup.flow.yaml` and `50-build-exec-kpi.flow.yaml` are identical in shape, pointing at `SqlFlowCatalogTests.demo.usp_BuildCountryRollup` and `SqlFlowCatalogTests.demo.usp_BuildExecKpi`.

## Step 3: sync the estate into the catalog

```bash
sqlflow db sync . --repo lineage-demo --connect
```

`db sync` projects the estate, the `run.json` artifacts, and lineage under the given path into the EF-managed shadow catalog, migrating the schema first so a sync against a fresh server just works. `--repo` attributes the rows (default: the folder name). `--db` (default `${env:SQLFLOW_CATALOG_DB}`) is only the catalog connection, the database the sync writes into. `--connect` adds the derived lineage tier through a separate connection: it reaches the servers the flow documents themselves declare (here, `${env:SQLFLOW_DEMO_DB}`) to read object metadata and `sys.sql_modules`, which is what expands the procedure bodies and links flows across repos through shared objects.

## Step 4: inspect the result in the GUI

After the sync:

- The Lineage page shows the object graph: the pre tables, the views, the targets, the three procedures, the fact table, the country rollup, and the executive KPI.
- The pipelines carry waves 1 through 5.
- Each landing pipeline's Transforms tab lists its declared and detected view columns (for `demo-land-orders`, the declared `vehicle_type_clean` transform alongside the inferred columns).

You can also compute the same lineage from the CLI without a catalog database; `sqlflow lineage <folder>` is offline by default (declared documents plus observed run artifacts) and `--connect` adds the derived tier.

```bash
sqlflow lineage . --connect
```

## Gotchas

- **Ingestion object names must be three-part on SQL Server.** A two-part name is read as `database.table` (the MySQL convention), so SQL Server objects must carry the database explicitly: `SqlFlowCatalogTests.demo.vOrders_Pre`, not `demo.vOrders_Pre`.
- **The `sp` document also requires a three-part procedure name**, and its database segment must match the connection's Initial Catalog (`SqlFlowCatalogTests` for the local demo).
- **The editor may flag the `ing` and `sp` documents** against the file-flow JSON schema; the CLI loaders accept them (the schema does not yet describe the non-file flow kinds).
- **`--db` values should stay references.** The CLI warns when `--db` embeds a credential on the command line (it lands in shell history); prefer `${env:SQLFLOW_CATALOG_DB}` or another `${env:NAME}` / `${keyvault:vault/secret}` reference, with local values in the git-ignored `.sqlflow/env` file.

## See also

- [sqlflow lineage](../cli/lineage.md)
- [Lineage tiers](../concepts/lineage-tiers.md)
- [Pre-ingestion transforms](../concepts/pre-ingestion-transform.md)
- [sqlflow db](../cli/db.md)
