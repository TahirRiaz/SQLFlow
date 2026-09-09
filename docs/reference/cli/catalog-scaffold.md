---
id: cli-catalog-scaffold
title: sqlflow catalog scaffold and scaffold-all
type: cli-command
summary: Generate runnable flowType ing YAML from discovered source tables, one object at a time (scaffold) or in bulk (scaffold-all).
keywords:
  - scaffold
  - scaffold-all
  - generate flow
  - replication
  - ingestion yaml
  - key detection
  - detect-keys
  - keycolumns
cliCommand: catalog
related:
  - cli-catalog
  - cli-detect-unique-key
  - flow-ing
  - guide-table-to-table-ingestion
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Catalog/CatalogScaffolder.cs
  - tests/SqlFlow.Core.Tests/CatalogScaffolderTests.cs
  - samples/ingestion/mysql-to-sqlserver.flow.yaml
---

# sqlflow catalog scaffold and scaffold-all

## Synopsis

```bash
sqlflow catalog scaffold --source <ref> --object <schema.table> --target-object <schema.table> \
    [--target <ref>] [--provider mssql|azdb|mysql|postgres|oracle] \
    [--keys a,b] [--detect-keys] [--sample N] [--name <flow-name>] [--out <file> | -o <file>]

sqlflow catalog scaffold-all --source <ref> --out <directory> \
    [--target <ref>] [--provider mssql|azdb|mysql|postgres|oracle] \
    [--database <db>] [--schema <s>] [--like <pattern>] [--target-schema <s>] \
    [--limit N] [--offset N] [--system] [--no-views] [--no-tables]
```

## Description

`catalog scaffold` turns one discovered table or view into a complete, runnable `flowType: ing`
document: the connections block, `source.server`/`source.object`, `target.server`/`target.object`,
and `load.keyColumns` filled from the introspected primary key, so `sqlflow run` accepts the file
as written. Two replication helpers are added for human review: candidate incremental date columns
are suggested as a commented `incremental:` block, and the full detected column inventory is
appended as comments.

`catalog scaffold-all` applies the same generation to every table and view matching the discovery
filters and writes one `<schema>.<table>.flow.yaml` file per object into `--out`.

Both subcommands live under `sqlflow catalog` and share its discovery machinery
(SQL Server, Azure SQL, MySQL, PostgreSQL, Oracle sources). Generation itself is
src/SqlFlow.Core/Catalog/CatalogScaffolder.cs; the CLI wiring is in src/SqlFlow.Cli/Program.cs.

No secret is ever written into a generated file. Only a connection reference that is a whole
`${...}` expression (for example `${env:SHOP_MYSQL}` or `${keyvault:vault/secret}`) is embedded
verbatim. A literal connection string or an `@alias` given as `--source` becomes the placeholder
`${env:SQLFLOW_SOURCE}` in the document; the same rule applies to `--target`, whose placeholder is
`${env:SQLFLOW_DW}`. Omitting `--target` also yields `${env:SQLFLOW_DW}`.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `scaffold` \| `scaffold-all` | yes | Subcommand of `sqlflow catalog`. Any other value prints the catalog usage line and exits 1. |

## Options: scaffold

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--source <ref>` | string | none, required | Source connection: `@alias`, `${...}` reference, or a connection string. Missing: `ERROR  catalog commands require --source <@alias \| ${ref} \| connection string>.` and exit 1. |
| `--object <name>` | string | none, required | The source object, `schema.object` or `database.schema.object`. Bracketed parts (`[dbo].[My.Table]`) are supported. Missing: `ERROR  This command requires --object <schema.object \| database.schema.object>.` and exit 1. |
| `--target-object <name>` | string | none, required | Target object as `schema.table`. Missing: `ERROR  catalog scaffold requires --target-object <schema.table>.` and exit 1. |
| `--target <ref>` | string | `${env:SQLFLOW_DW}` placeholder | Target (data warehouse) connection reference. Embedded verbatim only when it is a whole `${...}` reference. |
| `--provider <p>` | enum | `mssql` | Source provider: `mssql`, `sqlserver`, `azdb`, `mysql`, `postgres`, `postgresql`, `oracle`. Any other value: `ERROR  Unknown --provider '<p>'. Allowed: mssql, azdb, mysql, postgres, oracle.` and exit 1. |
| `--keys <a,b>` | string | introspected primary key | Comma-separated key columns for `load.keyColumns`; entries are trimmed and empties dropped. Overrides both the primary key and `--detect-keys`. |
| `--detect-keys` | flag | off | When `--keys` is not given, run live unique-key detection (the same detector and probe as `sqlflow detect-unique-key`) and use the top verified unique key. Only effective for SQL Server and Azure SQL sources; for `mysql`, `postgres`, or `oracle` sources it yields no key. |
| `--sample <N>` | int | auto | Sample size for `--detect-keys` profiling. Absent: auto-sample (large tables only). `0`: full scan. Positive: used verbatim. |
| `--name <n>` | string | `<schema>-<table>` lowercased | The flow `name` in the generated document. |
| `--out <file>`, `-o <file>` | path | stdout | Write the YAML to a file and print `Wrote ingestion-flow scaffold to <file>`. Without it the YAML goes to stdout. |

To scaffold from a specific database, use the three-part form of `--object`
(`database.schema.object`); `scaffold` does not read the `--database` flag.

## Options: scaffold-all

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--source <ref>` | string | none, required | Same as for `scaffold`. |
| `--out <dir>`, `-o <dir>` | path | none, required | Output directory for the generated flow files; created if missing. Missing: `ERROR  catalog scaffold-all requires --out <directory> for the generated flow files.` and exit 1. |
| `--target <ref>` | string | `${env:SQLFLOW_DW}` placeholder | Same embedding rule as for `scaffold`. |
| `--provider <p>` | enum | `mssql` | Same values as for `scaffold`. |
| `--database <db>` | string | connection default | Database to enumerate. |
| `--schema <s>` | string | all schemas | Restrict enumeration to one schema. |
| `--like <pattern>` | string | none | Name filter applied during enumeration. |
| `--target-schema <s>` | string | source schema | Target schema for every generated flow; the target object becomes `<target-schema>.<table>`. |
| `--limit <N>` | int | 1000 | Page size of the enumeration; objects beyond it are not scaffolded. |
| `--offset <N>` | int | 0 | Enumeration offset. |
| `--system` | flag | off | Include system objects. |
| `--no-views` | flag | off | Exclude views. |
| `--no-tables` | flag | off | Exclude tables. |

`scaffold-all` always uses the default flow name and the introspected primary key per object;
`--keys`, `--detect-keys`, and `--name` apply to `scaffold` only.

## Behavior and output notes

### The generated document

The output is a complete `flowType: ing` YAML that round-trips through the real ingestion loader
(verified by tests/SqlFlow.Core.Tests/CatalogScaffolderTests.cs):

- Header comments name the scaffolded object and suggest `sqlflow run <thisfile>`.
- `name:` defaults to `<schema>-<table>` lowercased.
- `connections:` declares `src` and `dwh`. A SQL Server source uses the plain-string form
  (`src: ${env:...}`). A non-SQL-Server source uses the map form with a `provider:` key
  (`mysql`, `postgres`, `oracle`, or `azdb`) and a `connection:` value.
- `source:` has `server: src` and `object: <schema>.<table>` from the introspected object.
- `target:` has `server: dwh` and `object:` from `--target-object` (or
  `<target-schema>.<table>` in `scaffold-all`).
- `load.keyColumns:` lists the chosen keys. With no primary key and no explicit or detected key,
  the list is empty and the line is followed by the comment
  `# no primary key detected: without keyColumns every run appends all rows.`
- When date-like columns exist (native type starting with `date`, `smalldatetime`, or
  `timestamp`, case-insensitive), a commented `incremental:` block suggests the first such column
  with `overlapDays: 7`; when there is more than one, a commented `candidates:` line lists them
  all.
- The full column inventory is appended as comments in the form `name : type : nullability`,
  with `[PK]` marking primary-key members.

### Key selection order (scaffold)

1. `--keys a,b` wins outright.
2. Otherwise `--detect-keys` runs live unique-key detection against the source. Only a key that
   is unique and verified against the whole table is used. Progress notes go to stderr so stdout
   stays clean YAML: `detected key: <columns>` or
   `no unique key detected; scaffolding without keyColumns.` (both indented two spaces).
   The probe is T-SQL, so detection
   returns no key for `mysql`, `postgres`, or `oracle` sources.
3. Otherwise the introspected primary key, ordered by column ordinal.

### scaffold-all specifics

- Enumerates tables and views with the discovery filters, paging with `--limit` (default 1000).
- Zero matches: `ERROR  no tables or views matched (check --schema/--like and the connection).`
  and exit 1.
- Writes one file per object named `<schema>.<table>.flow.yaml`; characters invalid in file
  names are folded to `_`. Each written path is printed.
- An object that disappears between enumeration and introspection is skipped with
  `WARN  <schema>.<table> disappeared during scaffolding; skipped.` and does not fail the run.
- Finishes with `Scaffolded <N> flow file(s) of <T> matching object(s).`; when the page has more
  objects than `--limit`, the summary appends `(raise --limit for the rest)`.

## Examples

Scaffold one SQL Server table to stdout, redirecting into a flow file:

```bash
sqlflow catalog scaffold --source '${env:SQLFLOW_SOURCE}' \
    --object sales.orders --target-object raw.orders > flows/sales.orders.flow.yaml
```

Scaffold a MySQL table, writing directly to a file:

```bash
sqlflow catalog scaffold --source '${env:SHOP_MYSQL}' --provider mysql \
    --object shop.orders --target '${env:SQLFLOW_DW}' --target-object raw.ShopOrders \
    --name shop-orders --out flows/shop.orders.flow.yaml
# Wrote ingestion-flow scaffold to flows/shop.orders.flow.yaml
```

Scaffold a SQL Server table with unique-key detection on a sample:

```bash
sqlflow catalog scaffold --source '${env:SQLFLOW_SOURCE}' \
    --object dbo.EventLog --target-object raw.EventLog --detect-keys --sample 100000
#   detected key: EventId
```

Scaffold every table in one schema into a directory, landing in a `raw` target schema:

```bash
sqlflow catalog scaffold-all --source '${env:SQLFLOW_SOURCE}' \
    --schema sales --target '${env:SQLFLOW_DW}' --target-schema raw --out ./flows
#   ./flows/sales.orders.flow.yaml
#   ./flows/sales.customers.flow.yaml
# Scaffolded 2 flow file(s) of 2 matching object(s).
```

A representative generated document (source table `sales.orders` with primary key `id` and a
`datetime2` column `updated_at`):

```yaml
# Scaffolded by 'sqlflow catalog scaffold' from sales.orders.
# Review, then run:  sqlflow run <thisfile>
flowType: ing
name: sales-orders
connections:
  src: ${env:SQLFLOW_SOURCE}
  dwh: ${env:SQLFLOW_DW}
source:
  server: src
  object: sales.orders
target:
  server: dwh
  object: raw.orders
load:
  keyColumns: [id]
# Incremental loading (uncomment and pick the change-tracking column):
# incremental:
#   columns: [updated_at]
#   overlapDays: 7

# Detected source columns (name : type : nullability):
#   id : int : NOT NULL  [PK]
#   amount : decimal(10, 2) : NOT NULL
#   updated_at : datetime2(3) : NOT NULL
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | Scaffold written to stdout or `--out`; for `scaffold-all`, all matched objects processed (skipped-with-warning objects do not fail the run). |
| 1 | Missing `--source`, missing `--object`, missing `--target-object` (scaffold), missing `--out` (scaffold-all), unknown `--provider`, source object not found (`ERROR  object <name> was not found.`), no matching objects (scaffold-all), or an unresolvable connection reference. |

## See also

- [sqlflow catalog](./catalog.md): discovery subcommands (`databases`, `schemas`, `tables`, `search`, `columns`) sharing the same `--source` and filter flags.
- [sqlflow detect-unique-key](./detect-unique-key.md): the standalone unique-key detector that `--detect-keys` reuses.
- [flowType: ing reference](../flow/ing.md): every key the generated document can carry.
- [Guide: table-to-table ingestion](../guides/table-to-table-ingestion.md): end-to-end replication walkthrough.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
