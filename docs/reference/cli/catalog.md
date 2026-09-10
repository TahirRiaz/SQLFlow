---
id: cli-catalog
title: "sqlflow catalog: databases, schemas, tables, search, columns"
type: cli-command
summary: Browse a live source connection from the CLI; list databases, schemas, tables and views, search objects, and introspect columns on any supported provider.
keywords:
  - catalog
  - discovery
  - source browsing
  - providers
  - mysql
  - postgres
  - oracle
cliCommand: catalog
related:
  - cli-catalog-scaffold
  - concept-connections-and-secrets
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Core/Catalog/CatalogService.cs
  - src/SqlFlow.Core/Catalog/CatalogQuery.cs
  - src/SqlFlow.Core/Catalog/CatalogModel.cs
  - src/SqlFlow.Core/Catalog/ICatalogReader.cs
  - src/SqlFlow.Core/Catalog/ThreePartName.cs
  - src/SqlFlow.Core/Connections/ConnectionResolver.cs
---

# sqlflow catalog

## Synopsis

```bash
sqlflow catalog <databases|schemas|tables|search|columns> --source <ref> \
  [--provider mssql|azdb|mysql|postgres|oracle] \
  [--database <name>] [--schema <name>] \
  [--like <text>] [--term <text>] [--object <name>] \
  [--system] [--no-views] [--no-tables] \
  [--offset <N>] [--limit <N>] [--json]
```

## Description

`sqlflow catalog` browses a live source connection: databases, schemas, tables and views, object search, and column-level introspection. It is read-only discovery; nothing is written to the source or to any control database.

Every subcommand goes through `CatalogService` (src/SqlFlow.Core/Catalog/CatalogService.cs), the single resolve-open-read entry point for source discovery (other CLI verbs, such as `detect-unique-key`, introspect through it too). For each call the service resolves the `--source` reference with `ConnectionRole.Source`, opens the connection through the same kind-dispatching connection factory the engine ingests with, and selects the provider's `ICatalogReader`. Provider readers exist for SQL Server (also serving Azure SQL), MySQL, PostgreSQL, and Oracle; each runs over the already-open connection and never sees a connection string.

The `catalog` verb also has `scaffold` and `scaffold-all` subcommands that turn introspection results into ingestion-flow YAML; they are documented separately (see [sqlflow catalog scaffold](catalog-scaffold.md)).

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<subcommand>` | yes | One of `databases`, `schemas`, `tables`, `search`, `columns` (plus `scaffold` and `scaffold-all`, documented on their own page). Any other value prints the usage line and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--source <ref>` | string | none, required | The source connection: an inline connection string, a `${...}` secret reference (for example `${env:SQLFLOW_SOURCE}`), or an `@alias`. Missing or blank prints `ERROR  catalog commands require --source <@alias \| ${ref} \| connection string>.` and exits 1. |
| `--provider <p>` | string | SQL Server | Provider of the source. Accepted tokens: `mssql` or `sqlserver` (SQL Server, the default when omitted), `azdb`, `mysql`, `postgres` or `postgresql`, `oracle`. Any other value fails with `Unknown --provider '<v>'. Allowed: mssql, azdb, mysql, postgres, oracle.` |
| `--database <d>` | string | connection's current database | Database scope for `schemas`, `tables`, and `search`; provider semantics differ (see provider notes). Not used by `columns` (put the database in `--object` instead). |
| `--schema <s>` | string | all schemas | Schema scope for `tables`. |
| `--like <text>` | string | none | Substring name filter (maps to `CatalogQuery.NameLike`); the reader wraps the value in `%...%`. Always sent as a query parameter, never interpolated. Every provider applies it to `tables`; the SQL Server reader also applies it to `databases` and `schemas`, and escapes LIKE wildcards in the value, so it matches literally there. PostgreSQL and Oracle match case-insensitively. |
| `--term <text>` | string | none | Search text; required by `search`. |
| `--object <name>` | string | none | Object name for `columns`: `schema.object` or `database.schema.object`, with optional `[bracketed]` parts. |
| `--system` | switch | off | Include system objects (`CatalogQuery.IncludeSystem`). |
| `--no-views` | switch | off | Exclude views from `tables` listings (`IncludeViews` defaults to true). |
| `--no-tables` | switch | off | Exclude tables from `tables` listings (`IncludeTables` defaults to true). |
| `--offset <N>` | int | 0 | Paging offset for `tables` (the SQL Server reader also pages `databases`). |
| `--limit <N>` | int | 200 | Page size for `tables` (the SQL Server reader also pages `databases`, and clamps the value to 1..2000). |
| `--json` | switch | off | Emit JSON instead of plain text, for every subcommand. |

The listing flags (`--like`, `--system`, `--no-views`, `--no-tables`, `--offset`, `--limit`) populate a single `CatalogQuery` (src/SqlFlow.Core/Catalog/CatalogQuery.cs) that is passed to `databases`, `schemas`, and `tables`. The `search` subcommand takes only `--source`, `--database`, `--term`, `--provider`, and `--json`; the query flags do not apply to it. `columns` takes only `--source`, `--object`, `--provider`, and `--json`.

## Behavior and output

### Source resolution

`--source` accepts three reference forms, resolved by the single connection pipeline in src/SqlFlow.Core/Connections/ConnectionResolver.cs:

- An inline connection string. It defaults to SQL Server; `--provider` overrides the kind. Only SQL Server (`mssql`, `azdb`) accepts a literal inline string, and only a passwordless one: `Integrated Security=true` or `Authentication=` Active Directory Default, Managed Identity, or Workload Identity. MySQL, PostgreSQL, and Oracle reject every literal string with `<provider> has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.`
- A whole `${scheme:locator}` secret reference such as `${env:SQLFLOW_SOURCE}` or `${keyvault:vault/secret}`. The expanded value is trusted to carry credentials because it came from a secret store.
- An `@alias` from the connection registry. Aliases require full mode (a configured control database). In the CLI's lightweight mode an alias fails with: `Connection '@<name>' is an alias and requires full mode (a configured control database). Supply an inline connection string or a ${...} secret reference instead.`

### Subcommands

**`databases`**: lists databases on the connection (PostgreSQL and Oracle report exactly the connected database; see provider notes). Text output is one line per database: the name, followed by two spaces and `(<collation>)` when the reader reports a collation.

**`schemas`**: lists schemas. `--database` selects the database on SQL Server (the reader switches context) and on MySQL (where a schema is a database); PostgreSQL and Oracle list the connected database's schemas and ignore `--database`. Text output is one schema name per line.

**`tables`**: lists tables and views in the `--database` / `--schema` scope as a page. Text output per object is `<schema>.<name>  <Type>  ~<approxRows> row(s)`, followed by a `(<shown> of <total>)` footer. With `--json` the whole page object is emitted (`items`, `offset`, `limit`, `total`, `hasMore`). Use `--offset` and `--limit` to page; readers sort by schema, then name.

**`search`**: substring search on table and view names for `--term`. Requires `--term`; without it: `ERROR  catalog search requires --term <text>.` and exit 1. Text output per match is `<schema>.<name>  (<type>)`. The SQL Server reader ranks matches (exact name first, then prefix, then contains) and returns at most 200; the MySQL, PostgreSQL, and Oracle readers order by schema then name and return at most 100. `--database` scopes the search on SQL Server and MySQL; PostgreSQL and Oracle ignore it.

**`columns`**: full introspection of one table or view named by `--object`. Text output per column is `<name>  <nativeType>  NULL|NOT NULL`. With `--json` the complete introspection DTO (`SqlFlow.Core.Catalog.CatalogObject`) is emitted: object type (`Table` or `View`), columns (name, ordinal, rendered native type such as `nvarchar(50)` or `decimal(18, 2)`, nullability, collation, identity with seed and increment, computed expression, default expression, primary-key membership), indexes, constraints (default, check, foreign key, primary key, unique), and the temporal flag. This is the same shape the schema-sync planner consumes.

### Provider notes

- **SQL Server** (`mssql`, the default, and `azdb`): `databases` lists online databases the login can access (`HAS_DBACCESS`), hiding the four system databases unless `--system`. `--database` switches the connection's database context for `schemas`, `tables`, and `search`, as does the database part of `--object`.
- **MySQL**: databases and schemas are one namespace. `databases` and `schemas` both list them, hiding `mysql`, `information_schema`, `performance_schema`, and `sys` unless `--system`. The schema part of `--object` addresses the MySQL database; for `tables`, `--schema` wins over `--database` when both are given.
- **PostgreSQL**: a connection is pinned to one database, so `databases` returns just that database and `--database` is ignored everywhere. `schemas` hides `pg_*` and `information_schema` unless `--system`.
- **Oracle**: `databases` returns the connected pluggable database. `schemas` lists users (a schema is its owning user), hiding Oracle-maintained ones unless `--system`. The schema part of `--object` is the owner; for `tables`, `--database` acts as the owner filter when `--schema` is absent.

### --object parsing

`--object` accepts `schema.object` or `database.schema.object`. Parts may be bracket-quoted (`[Sales Data].[Order Lines]`), and dots inside brackets do not split. Errors:

- Missing flag: `This command requires --object <schema.object | database.schema.object>.`
- Wrong shape (fewer than two parts): `--object must be 'schema.object' or 'database.schema.object'; got '<raw>'.`
- Object not found: `ERROR  object [db].[schema].[name] was not found.` (the name is rendered bracket-quoted; the database part is omitted when not given) and exit 1.

## Examples

List tables in one schema of a PostgreSQL source, filtered by name substring:

```bash
sqlflow catalog tables \
  --source '${env:PG_SOURCE}' --provider postgres \
  --schema public --like order --limit 50
```

Search a SQL Server source and then introspect one hit as JSON:

```bash
sqlflow catalog search --source '${env:SQLFLOW_SOURCE}' --term customer

sqlflow catalog columns \
  --source '${env:SQLFLOW_SOURCE}' \
  --object AdventureWorks.Sales.Customer --json
```

Enumerate databases and schemas on a MySQL server (the whole connection string must come from a secret reference; MySQL has no self-authenticating inline form):

```bash
sqlflow catalog databases --source '${env:MYSQL_SOURCE}' --provider mysql
sqlflow catalog schemas   --source '${env:MYSQL_SOURCE}' --provider mysql --database shop
```

Page through a large listing, tables only:

```bash
sqlflow catalog tables --source '${env:SQLFLOW_SOURCE}' --no-views --limit 100 --offset 100
```

## Exit behavior

| Exit code | Condition |
| --- | --- |
| 0 | Subcommand completed (including an empty result set). |
| 1 | Missing `--source`; `search` without `--term`; `columns` on an object that does not exist; unknown subcommand (usage printed); unknown `--provider`; missing or malformed `--object`; connection resolution or alias failure. Errors print to stderr with an `ERROR` prefix; thrown error messages pass through secret redaction first. |

## See also

- [sqlflow catalog scaffold and scaffold-all](catalog-scaffold.md): generate ingestion-flow YAML from introspected objects.
- [Connections and secrets](../concepts/connections-and-secrets.md): `@alias`, `${...}` references, and the secretless gate.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
