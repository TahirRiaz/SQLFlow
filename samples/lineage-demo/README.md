# Lineage demo: files -> pre -> views -> targets -> fact -> sp chain

A seven-flow estate that exercises the full chained topology and lands a five-wave execution
plan in the catalog:

```
orders.csv    -> demo.Orders_Pre    -> demo.vOrders_Pre    -> demo.Orders    \
                                                                              -> demo.Fact_OrderSummary
customers.csv -> demo.Customers_Pre -> demo.vCustomers_Pre -> demo.Customers /       |
                                                                                     v
                                              demo.Fact_OrderSummary -> demo.Fact_CountryRollup -> demo.Kpi_Executive
                                                            (usp_BuildCountryRollup)      (usp_BuildExecKpi)
```

| Flow | Kind | Wave | What it does |
| --- | --- | --- | --- |
| demo-land-orders | file | 1 | CSV -> raw pre table; refreshes the typed view `demo.vOrders_Pre` (inference + a declared `vehicle_type_clean` transform) |
| demo-land-customers | file | 1 | CSV -> raw pre table; refreshes `demo.vCustomers_Pre` (inference only) |
| demo-ing-orders | ing | 2 | Reads THE VIEW -> keyed upsert into `demo.Orders` |
| demo-ing-customers | ing | 2 | Reads THE VIEW -> keyed upsert into `demo.Customers` |
| demo-build-order-fact | sp | 3 | `demo.usp_BuildOrderFact` joins both targets into `demo.Fact_OrderSummary` |
| demo-build-country-rollup | sp | 4 | `demo.usp_BuildCountryRollup` reads `demo.Fact_OrderSummary` -> `demo.Fact_CountryRollup` |
| demo-build-exec-kpi | sp | 5 | `demo.usp_BuildExecKpi` reads `demo.Fact_CountryRollup` -> `demo.Kpi_Executive` |

The landing flows declare the views they write, so `land -> ing` dependencies resolve at the
declared tier; the `ing -> sp` and `sp -> sp` dependencies come from the connected (derived) tier
reading the procedure bodies from `sys.sql_modules`, which is why the sync uses `--connect`. The
two rollup procedures make the chain three procedures deep: `Fact_OrderSummary` is no longer a
terminal output but a consumed intermediate; `Kpi_Executive` is the estate's final product.

## Run it

```bash
export SQLFLOW_DEMO_DB="Server=localhost;Database=SqlFlowCatalogTests;Integrated Security=True;TrustServerCertificate=True"
export SQLFLOW_CATALOG_DB="$SQLFLOW_DEMO_DB"

# One-time: the demo schema, the fact procedure, and the two rollup procedures.
cat sql/usp_BuildOrderFact.sql | sqlcmd -S localhost -d SqlFlowCatalogTests -E
cat sql/usp_BuildRollups.sql   | sqlcmd -S localhost -d SqlFlowCatalogTests -E

# The flows, in dependency order.
sqlflow run 10-land-orders.flow.yaml
sqlflow run 11-land-customers.flow.yaml
sqlflow run 20-ing-orders.flow.yaml
sqlflow run 21-ing-customers.flow.yaml
sqlflow run 30-build-order-fact.flow.yaml
sqlflow run 40-build-country-rollup.flow.yaml
sqlflow run 50-build-exec-kpi.flow.yaml

# Sync the estate + derived lineage into the catalog.
sqlflow db sync . --repo lineage-demo --connect
```

Then open the GUI: the Lineage page shows the object graph (pre tables, views, targets, the three
procedures, the fact table, the country rollup, and the executive KPI), the pipelines carry waves
1-5, and each landing pipeline's Transforms tab lists its declared and detected view columns.

## Notes

- Ingestion (`ing`) object names are three-part; a two-part name is read as `database.table`
  (the MySQL convention), so SQL Server objects must carry the database explicitly.
- The `sp` document also requires a three-part procedure name whose database matches the
  connection's Initial Catalog.
- The editor may flag the `ing`/`sp` documents against the file-flow JSON schema; the CLI
  loaders accept them (the schema does not yet describe the non-file flow kinds).
