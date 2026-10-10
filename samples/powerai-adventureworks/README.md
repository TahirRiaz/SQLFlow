# PowerAI AdventureWorks sample

A one-report estate for exercising PowerAI end to end against the local compose stack
(`deploy/compose`): report extraction, model-entity resolution, the semantic layer, question
retrieval, confirmation, and auto-run.

It moves no data. It holds:

- `adventureworks_00_sch.yaml`, a schema registration flow (`flowType: sch`) that registers the tables and
  views of the `AdventureWorks` database (AdventureWorksDW2022, restored by the stack's
  `adventureworks-init` service). Its connection is where AdventureWorks questions run.
- `subscribers/`, the `AdventureWorks_Sales` Power BI subscriber. Its library declares no connection, so the
  report's model tables are linked onto the registered tables by database, schema, and name.
- `reports/`, the committed specification of `samples/powerbi/AdventureWorks Sales.pbix`.

## Syncing it into the local catalog

From the repository root, with the stack up (SQL Server published on the host port from
`deploy/compose`, 14333 by default):

```bash
export SQLFLOW_CATALOG_DB="Server=localhost,14333;Database=SqlFlowCatalog;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True"
export SQLFLOW_ADVENTUREWORKS_DB="Server=localhost,14333;Database=AdventureWorks;User ID=sa;Password=<MSSQL_SA_PASSWORD>;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- db sync samples/powerai-adventureworks --repo powerai-adventureworks --connect
```

`--connect` makes the sync read the registered database, so the catalog holds its tables, views, columns,
and scripts and the report links onto them. Running the registration flow refreshes the same records
without a sync:

```bash
dotnet run --project src/SqlFlow.Cli -- run samples/powerai-adventureworks/adventureworks_00_sch.yaml --db '${env:SQLFLOW_CATALOG_DB}'
```

The semantic layer needs the registered tables before any of their columns can be allowed. PowerAI's
column policy is default-deny, so allow the report's tables afterwards (the Semantic layer page's Allow
all, or `PUT /api/v1/powerai/column-policies/objects` per object).

The worker resolves `${env:SQLFLOW_ADVENTUREWORKS_DB}` from the compose file, so queries against the
registered database run inside the stack.

## Test plan

[TESTING.md](TESTING.md) is the manual test suite for driving PowerAI from Claude Code through the
`sqlflow` MCP server.
