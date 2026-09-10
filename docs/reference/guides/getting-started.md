---
id: guide-getting-started
title: "Getting started: first run and authoring your first flow"
type: guide
summary: Run your first pipeline in three commands, then author a .flow.yaml from scratch with editor IntelliSense and inspect the run artifacts.
keywords:
  - quickstart
  - first flow
  - orders.flow.yaml
  - validate plan run
  - editor intellisense
  - json schema
  - environment variables
related:
  - cli-run
  - cli-plan
  - cli-validate
  - flow-overview
  - flow-source
  - flow-schema
  - flow-load
  - concept-connections-and-secrets
  - concept-environment-variables
sourceRefs:
  - README.md
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Secrets/LocalEnvFile.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Core/Model/PreIngestionCsv.cs
  - src/SqlFlow.Core/Runs/RunHistoryWriter.cs
  - schemas/sqlflow.flow.schema.json
  - .vscode/settings.json
  - samples/quickstart/orders.flow.yaml
  - samples/csv/README.md
  - docs/environment-variables.md
---

# Getting started: first run and authoring your first flow

This guide takes you from a clean checkout to a loaded SQL Server table, then walks through authoring a `.flow.yaml` of your own. A pipeline is a small YAML file; SQLFlow reads the source, inspects the live target schema, generates the DDL and bulk load, and executes them. No hand-written T-SQL and no control database are needed.

## Prerequisites

- .NET 9 SDK (the CLI is `src/SqlFlow.Cli`, run via `dotnet run`).
- A SQL Server you can create tables in (local instance, container, or Azure SQL).

All CLI examples below use the source-tree invocation form:

```bash
dotnet run --project src/SqlFlow.Cli -- <command> <file>
```

Everything after `--` is the `sqlflow` command line, so `sqlflow plan orders.flow.yaml` becomes `dotnet run --project src/SqlFlow.Cli -- plan orders.flow.yaml`.

## Step 1: point at your SQL Server

Secrets never live in the YAML. The flow document carries a reference such as `${env:SQLFLOW_DW}`; the value comes from the environment at run time.

```bash
# bash / CI
export SQLFLOW_DW="Server=localhost;Database=DW;Trusted_Connection=True;TrustServerCertificate=True"
```

```powershell
# PowerShell
$env:SQLFLOW_DW = "Server=localhost;Database=DW;Trusted_Connection=True;TrustServerCertificate=True"
```

For local development you can instead put the value in a git-ignored `.sqlflow/env` file (plain `KEY=VALUE` lines, `#` comments allowed) next to the flow document or in any parent directory; the nearest file is used. The process environment always wins over the file, so a CI variable can never be shadowed by a stray local copy. Values from this file are never logged; malformed lines fail loudly with their line number and the content is not echoed. See src/SqlFlow.Core/Secrets/LocalEnvFile.cs.

```text
# .sqlflow/env  (git-ignored; the whole .sqlflow/ folder belongs in .gitignore)
SQLFLOW_DW=Server=localhost;Database=DW;Trusted_Connection=True;TrustServerCertificate=True
```

## Step 2: validate, plan, run

The quickstart pipeline `samples/quickstart/orders.flow.yaml` loads `samples/quickstart/orders.csv` into `dbo.Orders`. Three commands cover the whole loop:

```bash
# check the definition only (no database access needed for the definition itself)
dotnet run --project src/SqlFlow.Cli -- validate samples/quickstart/orders.flow.yaml

# preview the exact T-SQL that would run; changes nothing
dotnet run --project src/SqlFlow.Cli -- plan samples/quickstart/orders.flow.yaml

# create/evolve the table and load the data
dotnet run --project src/SqlFlow.Cli -- run samples/quickstart/orders.flow.yaml
```

`plan` is the safety net: it shows the generated `CREATE`/`ALTER` DDL and the load plan before anything touches the database. A successful `run` prints a one-line result plus a per-operation trace:

```text
OK  'orders': 4 row(s) loaded; 0 DDL statement(s).
  trace:
    ok    source.columns             20.2 ms
    ok    target.introspect         506.3 ms
    ok    target.load               239.7 ms  (4 rows)
    ...
```

## Step 3: inspect the run artifacts

Every run writes its artifacts into a `.sqlflow/runs/<flow>/<yyyyMMdd-HHmmss>_<run-id-prefix>/` folder next to the pipeline file (see src/SqlFlow.Core/Runs/RunHistoryWriter.cs):

| File | Content |
|---|---|
| `run.json` | The full machine-readable result: status, rows loaded, DDL executed, processed files (name, size, modified time, row and column counts), and the per-operation trace with timings. |
| `run.log` | The canonical step-by-step log. |
| `trace.sql` | The generated SQL. |

For the quickstart run above the folder looks like `samples/quickstart/.sqlflow/runs/orders/20260618-112108_019eda76/`. Add `--show-sql` to any `run` to also print the generated SQL to the console.

## Authoring your first flow

### The minimal file

A file flow needs only a name, a source, and a target. `name`, `source`, and `target` are the required top-level keys; within `target`, `connection`, `schema`, and `table` are all required (schemas/sqlflow.flow.schema.json).

```yaml
name: orders

source:
  type: csv
  location: ./orders.csv

target:
  connection: ${env:SQLFLOW_DW}   # a reference, never a secret
  schema: dbo
  table: Orders
```

Everything else has defaults defined in src/SqlFlow.Core/Model/FlowDefinition.cs:

- `schema.evolve` defaults to `widen` (create the table if missing, and `ALTER TABLE ... ADD` newly-seen source columns).
- `schema.defaultColumnType` defaults to `varchar(255)`.
- `load.mode` defaults to `append`; `load.batchSize` defaults to 50000; `load.tableLock` defaults to true; `load.manageIndexes` defaults to false.

### Recommended key order

Author the top-level keys in this order (source to target to behaviour), per samples/csv/README.md:

```text
name -> source -> target -> schema -> load -> incremental -> transform -> desiredIndexes -> preProcess -> postProcess
```

### The annotated reference file

`samples/quickstart/orders.flow.yaml` is the fully annotated reference: it documents the `source.options` parser keys, the provenance column toggles, `target`, `schema`, `load`, and the `preProcess`/`postProcess` hooks inline. A condensed version:

```yaml
name: orders

source:
  type: csv
  location: ./orders.csv          # a file OR a folder (folder = consolidate all matching files)
  options:
    delimiter: ","
    header: true                  # first row holds column names
    textQualifier: "\""           # quote character around values ('qualifier' is an accepted alias)
    encoding: UTF8                # ASCII | UTF8 | Unicode | UTF32 ('srcEncoding' is an accepted alias)
    skipStartingDataRows: 0

target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders

schema:
  evolve: widen                   # create | widen | strict
  defaultColumnType: varchar(255)
  overrides:
    OrderId:
      type: BIGINT
      nullable: false

load:
  mode: append                    # append | truncate-load
  batchSize: 50000
  tableLock: true

preProcess:
  - "PRINT 'sqlflow: pre-process orders'"
postProcess:
  - "UPDATE STATISTICS dbo.Orders"
```

Key behaviours to know:

- **`source.location`** may be a single file, a folder, or a `file://` URI (which routes through `LocalFileStore`; see `samples/quickstart/orders.fileuri.flow.yaml`). A folder consolidates every matching file: columns are unioned across files and rows are NULL-filled where a file lacks a column. Use `options.srcFile` to set the file-name glob, as in `samples/multi/orders_multi.flow.yaml`:

```yaml
source:
  type: csv
  location: C:\Projects\SQLFlowV3\samples\multi   # a folder
  options:
    srcFile: "*.csv"
```

- **`schema.evolve`** accepts `create` (create the table if missing, never alter), `widen` (create if missing plus `ADD` newly-seen columns), or `strict` (fail if the source has columns the target lacks). `schema.overrides` pins individual columns to a concrete `{type, nullable}`.
- **`load.mode`** is `append` (add rows) or `truncate-load` (empty the target first, a full refresh). `load.manageIndexes: true` scripts and drops non-clustered indexes before the load and recreates them after (see `samples/quickstart/orders.indexed.flow.yaml`).
- **`preProcess` / `postProcess`** are lists of raw SQL statements run on the target before and after the load: inline statements or `EXEC` of an existing procedure.

## Editor IntelliSense

A JSON Schema at schemas/sqlflow.flow.schema.json drives autocomplete, hover documentation, and validation for `*.flow.yaml` files. `.vscode/` is git-ignored in this repository, so associate the schema yourself in a local `.vscode/settings.json` (via the Red Hat YAML extension):

```json
{
  "yaml.schemas": {
    "./schemas/sqlflow.flow.schema.json": ["*.flow.yaml", "samples/**/*.flow.yaml"]
  }
}
```

For any other editor with a yaml-language-server integration, add a modeline at the top of the file (adjust the relative path):

```yaml
# yaml-language-server: $schema=../../schemas/sqlflow.flow.schema.json
```

Known limits of the schema, so you are not surprised by editor squiggles:

- It describes only the FILE flow document (top-level `name`, `source`, `target`, `schema`, `load`, `incremental`, `transform`, `desiredIndexes`, `preProcess`, `postProcess`; root `additionalProperties` is false). There is no `flowType` property, so `ing`/`exp`/`sp`/`inv`/`hc`/`scm`/`batch` documents are flagged by the editor even though the CLI loaders accept them (acknowledged in samples/lineage-demo/README.md).
- `source.type` is a free string; the schema lists `csv`, `xls`, `xlsx`, `json`, `jsonl`, `ndjson`, `xml`, `parquet`, `prq` as examples only.
- `source.options` allows arbitrary string-valued keys, so a key the schema does not name still passes validation but gets no hover documentation. `textQualifier` and `encoding` (used above) are checked first by `PreIngestionCsv.FromSource`, but the schema names only their fallback aliases, `qualifier` and `srcEncoding`.

## Where to go next: the sample library

`samples/csv`, `samples/json`, `samples/xml`, and `samples/parquet` hold one runnable flow per feature (delimiters, fixed width, folder union, file-date windows, incremental loads, JSON/XML flattening, and more). Those samples reference their sink as `${env:SQLFlowSinkConStr}`:

```bash
export SQLFlowSinkConStr="Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- run samples/csv/csv-basic.flow.yaml
```

docs/environment-variables.md declares `SQLFLOW_TEST_DB` the canonical spelling for this repository's test sink, with the legacy `SQLFlowSinkConStr` name still honored.

## See also

- [sqlflow run](../cli/run.md): execute a pipeline, including the backfill flags.
- [sqlflow plan](../cli/plan.md): preview the generated SQL without changing anything.
- [sqlflow validate](../cli/validate.md): check a definition.
- [Flow document overview](../flow/overview.md): all flow kinds and the document model.
- [source](../flow/source.md): the source section in full, including every parser option.
- [schema](../flow/schema.md) and [load](../flow/load.md): evolution policy and load behaviour.
- [Connections and secrets](../concepts/connections-and-secrets.md): reference forms, `.sqlflow/env`, Key Vault.
- [Environment variables](../concepts/environment-variables.md): the canonical variable family.
