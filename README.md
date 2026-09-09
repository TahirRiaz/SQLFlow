# SQLFlow

[![CI](https://github.com/TahirRiaz/SQLFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/TahirRiaz/SQLFlow/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4.svg)](https://dotnet.microsoft.com/)

**Metadata-driven, self-evolving incremental ETL for SQL Server - pipelines as YAML, no hand-written T-SQL.**

SQLFlow reads your source, looks at the live target schema, and **generates the ETL on the fly** -
the `CREATE`/`ALTER` to evolve the schema and the bulk load to move the data. You describe *what*
you want in a small YAML file; SQLFlow figures out the SQL.

> This is the v3 rebuild: a clean, modern .NET 9 engine. A single flow file runs standalone with
> no setup (`sqlflow plan` / `sqlflow run`, no control database required). The same repository of
> flows can also be synced into a shadow catalog (`sqlflow db`, `sqlflow catalog sync`) to unlock
> lineage (`sqlflow lineage`), scheduling, and a queued-run control plane with worker nodes
> (`sqlflow worker`).

## Quick start

```bash
# 1. point at your SQL Server (secrets never live in the YAML)
export SQLFLOW_DW="Server=localhost;Database=DW;Trusted_Connection=True;TrustServerCertificate=True"

# 2. preview exactly what SQL it would run - changes nothing
dotnet run --project src/SqlFlow.Cli -- plan samples/quickstart/orders.flow.yaml

# 3. run it
dotnet run --project src/SqlFlow.Cli -- run samples/quickstart/orders.flow.yaml
```

A pipeline is a YAML file:

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_DW}   # a reference, never a secret
  schema: dbo
  table: Orders
schema:
  evolve: widen                   # create | widen | strict
load:
  mode: append                    # append | truncate-load
```

## Why SQLFlow

- **It writes the SQL, you don't.** Schema-sync DDL and bulk load are generated from your metadata
  and the live schema - not maintained by hand.
- **Self-evolving schema.** New columns in the source are added to the target automatically
  (`widen`), or rejected (`strict`), your choice.
- **`plan` before you `run`.** See the exact generated T-SQL before anything touches the database.
- **Config as code.** Pipelines are YAML in git - diffable, reviewable, versioned.
- **SQL Server native.** Built directly on `Microsoft.Data.SqlClient` + `SqlBulkCopy`.

## How it works

```text
YAML ──▶ [ model ] ──▶ infer source schema ──▶ introspect target
                                                      │
                                                      ▼
                                  diff ──▶ generate DDL ──▶ execute ──▶ bulk load
```

A standalone flow is stateless between runs: the target table (via `incremental`) is the only
state, so a re-run reasons from the live schema and watermark rather than from stored history. A
flow synced into the shadow catalog additionally gets run history, lineage, and scheduling backed
by the catalog database. See [docs/architecture.md](docs/architecture.md).

## Repository layout

| Project | Responsibility |
| --- | --- |
| `src/SqlFlow.Core` | Domain model, abstractions, the engine |
| `src/SqlFlow.Yaml` | YAML to model mapping and validation, every flow document kind |
| `src/SqlFlow.Execution` | Document loading and execution shared across flow kinds |
| `src/SqlFlow.SqlServer` | SQL Server introspection, DDL generation, bulk load, type mapping |
| `src/SqlFlow.Sources` | File source readers (CSV, XLS, JSON, XML, Parquet) and type inference |
| `src/SqlFlow.DuckDb` | DuckDB-backed source reader (Parquet, CSV, JSON, Delta) |
| `src/SqlFlow.Providers` | Foreign database catalog discovery (MySQL, PostgreSQL, Oracle) |
| `src/SqlFlow.Catalog` | The shadow catalog: pipelines, schedules, run history, sync |
| `src/SqlFlow.Lineage` | Lineage graph computation across the flow estate |
| `src/SqlFlow.ControlPlane` | Control-plane API: run queue, catalog, and schedule endpoints |
| `src/SqlFlow.Orchestration` | Batch flow orchestration: dependency waves, parallel execution |
| `src/SqlFlow.Node` | Worker node: polls the control plane and executes queued runs |
| `src/SqlFlow.Azure` | Azure auth, ADF/Automation invoke, service principal resolution |
| `src/SqlFlow.HealthCheck` | ML anomaly-detection engine for health-check flows |
| `src/SqlFlow.SourceControl` | SMO database scripting to git for source-control flows |
| `src/SqlFlow.Cli` | The `sqlflow` command-line tool |
| `tests/SqlFlow.Core.Tests` | Unit and integration tests |

## Build & test

```bash
dotnet build
dotnet test
```

Requires the [.NET 9 SDK](https://dotnet.microsoft.com/download).

### Integration tests against a real SQL Server

Most tests are pure unit tests with no dependencies. The tests under `tests/SqlFlow.Core.Tests/Integration`
exercise the real SQL Server path (table create, bulk load, schema evolution, index disable/rebuild,
desired indexes) against a live **sink database**. They read the connection from the
`SQLFlowSinkConStr` environment variable and **skip automatically when it is not set or not reachable**,
so the default `dotnet test` run stays self-contained.

To run them, point `SQLFlowSinkConStr` at a throwaway database you have `db_owner` on:

```bash
# the value is a normal connection string; use a database you don't mind tables being created/dropped in
export SQLFlowSinkConStr="Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet test --filter Category=Integration
```

Each integration test creates uniquely named tables and drops them before and after itself, so the
sink starts fresh every run. The connection must use a reachable endpoint: SQL Server's TCP/IP protocol
has to be enabled (SQL Server Configuration Manager) and the port in the connection string must match
the port the instance listens on.

## Reference documentation

[docs/reference/](docs/reference/) has the full generated reference corpus: every CLI command, every `.flow.yaml` section and source type, cross-cutting concepts, and task guides, each fact traced to its source file and line. [docs/reference/flow/keys.json](docs/reference/flow/keys.json) is a machine-readable census of every YAML attribute (type, default, allowed values, validation), and [docs/reference/manifest.json](docs/reference/manifest.json) indexes every page. See [docs/reference/README.md](docs/reference/README.md) for how these are meant to be consumed.

## Talks and demos

Recorded at the annual **BouvetOne** developer conference, in Norwegian. These present the v2
engine; the v3 rebuild in this repository keeps the same ideas behind a YAML-first surface.

| | |
| --- | --- |
| [<img src="https://img.youtube.com/vi/ybiEdjSAvcI/mqdefault.jpg" width="220" alt="Hvordan SQLFlow loste komplekse datautfordringer hos Kolumbus AS">](https://www.youtube.com/watch?v=ybiEdjSAvcI) | **[Hvordan SQLFlow løste komplekse datautfordringer hos Kolumbus AS](https://www.youtube.com/watch?v=ybiEdjSAvcI)**<br>How SQLFlow solved complex data challenges at Kolumbus AS. |
| [<img src="https://img.youtube.com/vi/EJZiZii7cmU/mqdefault.jpg" width="220" alt="SQLFlow i praksis">](https://www.youtube.com/watch?v=EJZiZii7cmU) | **[SQLFlow i praksis: Bygg automatisert dataflyt uten kode og vedlikehold](https://www.youtube.com/watch?v=EJZiZii7cmU)**<br>SQLFlow in practice: building automated data flow with no code and no maintenance. |
| [<img src="https://img.youtube.com/vi/xa-NkFe6Rrw/mqdefault.jpg" width="220" alt="Dynamic schema evolution">](https://www.youtube.com/watch?v=xa-NkFe6Rrw) | **[SQLFlow: Dynamic schema evolution](https://www.youtube.com/watch?v=xa-NkFe6Rrw)**<br>The schema-evolution engine, demonstrated live. |

## Who uses it

- **Kolumbus AS**, the public transport authority for Rogaland, Norway, running ingestion across
  its bus, boat and mobility estate.
- **Nortura SA**, Norway's largest food supplier, where SQLFlow supported critical data operations.

## Community and support

Questions, issues, and success stories are all welcome.

- **Slack:** [Join the SQLFlow community workspace](https://join.slack.com/t/sqlflow/shared_invite/zt-31tob1pxv-t4uVIjgucYRakm~W5Oik8A)
- **Email:** [tahir@sqlflow.io](mailto:tahir@sqlflow.io)
- **LinkedIn:** [Contact](https://www.linkedin.com/in/businessiq/)

## SQLFlow v2

This repository previously held **SQLFlow v2**: a Blazor UI over a SQL Server metadata database
that drove the pipelines, with a PowerShell/Docker sandbox installer. That tree is preserved in
full and stays available at the [`v2`](https://github.com/TahirRiaz/SQLFlow/tree/v2) tag:

```bash
git clone https://github.com/TahirRiaz/SQLFlow.git
git checkout v2
```

v2 is licensed under the **GNU General Public License v3.0**. v3, the contents of this branch, is
licensed under the MIT License.

## Contributing

Contributions are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md) and our
[Code of Conduct](CODE_OF_CONDUCT.md).

## License

[MIT](LICENSE). The v2 tree, at the `v2` tag, remains under GPL-3.0.
