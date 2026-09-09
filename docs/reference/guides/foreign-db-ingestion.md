---
id: guide-foreign-db-ingestion
title: MySQL, PostgreSQL, and Oracle sources into SQL Server
type: guide
summary: Ingest from MySQL, PostgreSQL, or Oracle into SQL Server by declaring a provider on the connection; dialects and type mapping are automatic.
keywords:
  - mysql
  - postgres
  - oracle
  - provider
  - foreign sources
  - dialects
  - type mapping
related:
  - flow-connections
  - cli-catalog
  - guide-table-to-table-ingestion
sourceRefs:
  - samples/ingestion/mysql-to-sqlserver.flow.yaml
  - src/SqlFlow.Providers/SqlFlowSourceProviders.cs
  - src/SqlFlow.Providers/MySql/MySqlSourceProvider.cs
  - src/SqlFlow.Providers/MySql/MySqlSourceTypeMapper.cs
  - src/SqlFlow.Providers/Postgres/PostgresSourceProvider.cs
  - src/SqlFlow.Providers/Postgres/PostgresSourceTypeMapper.cs
  - src/SqlFlow.Providers/Oracle/OracleSourceProvider.cs
  - src/SqlFlow.Providers/Oracle/OracleSourceTypeMapper.cs
  - src/SqlFlow.Core/Connections/SourceProviders.cs
  - src/SqlFlow.Core/Connections/DataSource.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Cli/Program.cs
  - docs/environment-variables.md
---

# MySQL, PostgreSQL, and Oracle sources into SQL Server

An ingestion flow (`flowType: ing`) can read from MySQL, PostgreSQL, or Oracle instead of SQL Server. The only authoring difference is one key: the connection declares its `provider`. The engine then reads the source with that system's own SQL dialect and translates every introspected column type to the SQL Server type that will hold it. Staging, schema evolution, change detection, and the keyed upsert all run on the SQL Server target, exactly as they do for a SQL Server source.

MySQL, PostgreSQL, and Oracle are source-only kinds. The target of every flow is always SQL Server; a foreign target is rejected at parse time (see below).

## Step 1: declare the provider on the connection

A connection in the `connections:` block takes one of three forms (src/SqlFlow.Yaml/YamlDocumentParts.cs):

```yaml
connections:
  shop:                                 # map form: a non-SQL-Server source declares its provider
    provider: mysql
    connection: ${env:SHOP_MYSQL}
  dwh: ${env:SQLFLOW_DW}                # plain string form = SQL Server (back compatible)
  audit:                                # bare form = SQL Server, resolves ${env:SQLFLOW_CONN_AUDIT}
```

Accepted `provider` tokens (case-insensitive): `mssql` (also `sqlserver`), `azdb`, `mysql`, `postgres` (also `postgresql`), `oracle`. Omitting the token means SQL Server. Anything else fails at parse time:

```text
<file>: 'connections.<name>.provider' has unknown provider '<value>'. Allowed: mssql, azdb, mysql, postgres, oracle.
```

The bare-alias environment convention also works in the map form: a `provider` with no `connection:` keeps the provider and resolves the conventional variable `SQLFLOW_CONN_<NAME>` (docs/environment-variables.md):

```yaml
connections:
  shop:
    provider: mysql     # resolves ${env:SQLFLOW_CONN_SHOP}
```

The same `provider` key is accepted directly on a `source:` endpoint that carries its own `connection:` instead of a `server:` reference; the loader synthesizes a connection named `source` with that kind.

The target must be SQL Server. Pointing `target.server` at a foreign connection fails at parse time, not deep in the run:

```text
<file>: the target connection 'shop' is 'MySQL'; an ingestion flow's target must be SQL Server (mssql or azdb).
```

### Secrets: foreign connection strings must be whole references

None of the three foreign providers has a self-authenticating connection-string mode, so a foreign connection string is expected to arrive as a whole `${...}` secret reference (environment variable or secret store), never as a literal in a context that requires self-authentication. Where a literal is passed in such a context (for example a raw string to `sqlflow catalog --source`), the canonicalizer rejects it:

```text
MySQL has no self-authenticating connection-string mode. Supply the whole connection string as a ${...} secret reference.
```

PostgreSQL and Oracle produce the same message with their own names. Under the reject-resting-secret policy (injected-token credential modes), a string that carries a password fails with:

```text
MySQL connection strings must not contain a resting password. Supply the whole string as a ${...} secret reference.
```

MySQL and PostgreSQL connections are stamped with `ApplicationName` = `SQLFlow Source` or `SQLFlow Target` according to their role; the redacted form used in logs and errors empties the password and user id. The Oracle managed driver's connection-string builder exposes no application-name key, so Oracle connections carry no role tag; the redaction (user id and password stripped) still applies.

## Step 2: name the source object

`source.object` is a three-part `Database.Schema.Table` name, with one convenience per system:

- **MySQL**: MySQL has no schema level separate from the database (a schema IS the database), so a two-part `database.table` is accepted and the database doubles into the schema slot. `shop.orders` is exactly `shop.shop.orders`. The schema part of the name addresses the MySQL database when the source SQL is generated.
- **PostgreSQL**: use `database.schema.table`, for example `erp.public.orders`. The generated source SQL qualifies `"schema"."table"` on the connection's current database, so the connection string must select the database named in the first part.
- **Oracle**: use `database.OWNER.TABLE`, where the schema part is the Oracle owner (user). Unquoted Oracle identifiers fold to upper case, so a table created as `sf_orders` is addressed as `SF_ORDERS`. As with PostgreSQL, the first part is carried by the model; the connection itself determines what the reads hit.

A missing object fails with:

```text
<file>: 'source.object' is required (a three-part name like Database.Schema.Table, or Database.Table for MySQL, where the database is the schema).
```

## Step 3: the complete flow

Adapted from samples/ingestion/mysql-to-sqlserver.flow.yaml:

```yaml
flowType: ing
name: shop-orders

connections:
  shop:                                 # a non-SQL-Server source declares its provider
    provider: mysql                     # mssql | azdb | mysql | postgres | oracle
    connection: ${env:SHOP_MYSQL}       # whole ${...} reference; secrets never rest in the file
  dwh: ${env:SQLFLOW_DW}                # plain string = SQL Server (the target must be SQL Server)

source:
  server: shop
  object: shop.orders                   # MySQL: database.table (the database is the schema)

target:
  server: dwh
  object: DW.raw.ShopOrders

load:
  keyColumns: [id]

incremental:
  columns: [updated_at]
  overlapDays: 7
```

The PostgreSQL variant changes only the provider and the object form:

```yaml
connections:
  erp:
    provider: postgres
    connection: ${env:ERP_PG}
source:
  server: erp
  object: erp.public.orders             # database.schema.table
```

And Oracle:

```yaml
connections:
  erp:
    provider: oracle
    connection: ${env:ERP_ORACLE}
source:
  server: erp
  object: erp.SHOPOWNER.ORDERS          # database.owner.table; unquoted Oracle names fold to upper case
```

Validate and run like any other flow:

```bash
sqlflow validate shop-orders.flow.yaml
sqlflow run      shop-orders.flow.yaml
```

Everything else in the document works exactly as for a SQL Server source: incremental watermarks, init-load chunking, assertions, virtual columns, surrogate keys, match-key delete detection, and dynamic schema evolution on the target. The only pieces that change per provider are the SQL fragments of the source-side statements and the type mapping.

## How the source SQL is written

Every source-side statement (the extract SELECT, the incremental MIN probe, init-load chunk predicates) is composed through the `ISourceSqlDialect` seam (src/SqlFlow.Core/Connections/SourceProviders.cs), so the single ingestion code path emits each system's own syntax:

| Aspect | MySQL | PostgreSQL | Oracle |
|---|---|---|---|
| Identifier quoting | `` `name` `` (literal backtick doubles) | `"name"` (literal quote doubles; preserves case) | `"name"` (literal quote doubles; preserves case) |
| Object qualification | `` `database`.`table` `` (schema IS the database) | `"schema"."table"` on the connection's database | `"OWNER"."TABLE"` (schema part is the owner) |
| Date arithmetic | `DATE_SUB(x, INTERVAL n DAY)` | `(x - INTERVAL 'n days')` | `(x - NUMTODSINTERVAL(n, 'DAY'))` |
| Binary literals | `0x...` hex | `'\x...'::bytea` | `HEXTORAW('...')` |
| Temporal watermark literals | quoted ISO string | quoted ISO string | `TO_DATE` / `TO_TIMESTAMP` / `TO_TIMESTAMP_TZ` with explicit format masks |

Oracle's temporal wrapping exists because an unquoted date string resolves through the session's NLS format, which is not ISO-8601 by default; a bare `'2024-01-03 10:00:00.000'` raises ORA-01843 under a non-ISO NLS setting. The explicit conversion makes the watermark comparison deterministic regardless of session settings (src/SqlFlow.Providers/Oracle/OracleSourceProvider.cs).

Incremental loading uses these seams end to end: when `incremental.fetchMinValuesFromSource` is on, the MIN watermark probe runs against the source using the source's own quoting and date arithmetic, with `overlapDays` subtracted in the source's dialect, and `incremental.lookback` subtracted from a numeric watermark's `MIN` as plain arithmetic that needs no dialect support (src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs).

## How types map to SQL Server

Each introspected source column's native type is translated to SQL Server type text by the provider's `ISourceTypeMapper`. The contract is strict: a type with no safe mapping throws with the column and type named; nothing silently degrades or falls through.

MySQL (src/SqlFlow.Providers/MySql/MySqlSourceTypeMapper.cs) follows the SSMA default mapping with two deliberate deviations, noted below:

| MySQL | SQL Server |
|---|---|
| `tinyint(1)` (signed) | `bit` |
| `int unsigned` | `bigint` |
| `bigint unsigned` | `decimal(20, 0)` (SSMA's bigint would overflow above 2^63-1) |
| `decimal(p, s)` (p <= 38) | `decimal(p, s)` |
| `datetime(n)` / `timestamp(n)` | `datetime2(n)` (timestamp deliberately not legacy datetime) |
| `varchar(n)` (n <= 4000) | `nvarchar(n)` |
| `char(n)` (n <= 4000) | `nchar(n)`, including the `char(36)` UUID idiom (see below) |
| `text` / `mediumtext` / `longtext` | `nvarchar(max)` |
| `enum(...)` | `nvarchar(255)` |
| `json` | `nvarchar(max)` |
| `blob` / `mediumblob` / `longblob` / spatial types | `varbinary(max)` |

MySQL has no UUID type, so a `char(36)` is text and stays text: it lands as `nchar(36)` holding the source's exact characters. The engine enforces that on the connection itself (`GuidFormat=None`, set by the canonicalizer and not overridable from the connection string), because the driver's default reinterprets every `char(36)` column as a CLR `Guid`, which the bulk copy into an `nchar` column rejects outright and which fails to parse for a `char(36)` that is not a UUID at all. A target column that really is `uniqueidentifier` (a pre-created table with `schema.sync: false`) still gets one: SQL Server converts the string on the insert. PostgreSQL is the contrasting case above, where `uuid` is a real type and maps to `uniqueidentifier`.

PostgreSQL (src/SqlFlow.Providers/Postgres/PostgresSourceTypeMapper.cs):

| PostgreSQL | SQL Server |
|---|---|
| `smallint` / `int` / `bigint` (and serials) | `smallint` / `int` / `bigint` |
| `numeric(p, s)` (p <= 38) | `decimal(p, s)` |
| `timestamp(n)` | `datetime2(n)` (default precision 6) |
| `timestamptz(n)` | `datetimeoffset(n)` |
| `uuid` | `uniqueidentifier` |
| `text` / `citext` | `nvarchar(max)` |
| `varchar(n)` (n <= 4000) | `nvarchar(n)`; unmodified `varchar` is `nvarchar(max)` |
| `json` / `jsonb` | `nvarchar(max)` |
| `bytea` | `varbinary(max)` |
| `xml` | `xml` |
| arrays, `inet`, ranges, geometric types | `nvarchar(max)` (read as text) |

Oracle (src/SqlFlow.Providers/Oracle/OracleSourceTypeMapper.cs), following the SSMA default mapping:

| Oracle | SQL Server |
|---|---|
| `NUMBER(p, s)` | `decimal(p, s)` (negative scale clamps to 0) |
| bare `NUMBER` / `FLOAT` | `float(53)` |
| `DATE` | `datetime2(0)` (Oracle DATE carries a time of day) |
| `TIMESTAMP(n)` | `datetime2(min(n, 7))` |
| `TIMESTAMP(n) WITH TIME ZONE` | `datetimeoffset(min(n, 7))` |
| `VARCHAR2(n)` / `NVARCHAR2(n)` (n <= 4000) | `nvarchar(n)` |
| `CLOB` / `NCLOB` / `LONG` | `nvarchar(max)` |
| `RAW(n)` (n <= 8000) | `varbinary(n)` |
| `BLOB` / `LONG RAW` | `varbinary(max)` |
| `XMLTYPE` | `xml` |
| `INTERVAL ...` | `nvarchar(50)` |

Failure modes are explicit and actionable:

```text
Column 'payload' has MySQL type '<type>', which has no safe SQL Server mapping. Exclude it with ignoreColumns or convert it in the source.
Column 'total' is decimal(45, 2); SQL Server supports at most precision 38. Reduce the source precision or exclude the column.
Column 'amount' is an unbounded PostgreSQL numeric; SQL Server needs an explicit precision (max 38). Declare numeric(p, s) in the source or exclude the column.
```

The PostgreSQL and Oracle variants of the unmappable-type message suggest `ignoreColumns` or casting the column in a source view.

## Browsing and scaffolding foreign sources from the CLI

The catalog commands take a `--provider` option with the same tokens as the YAML key, so you can explore a foreign source and scaffold a flow from it (src/SqlFlow.Cli/Program.cs):

```bash
# List tables in a MySQL database
sqlflow catalog tables --source '${env:SHOP_MYSQL}' --provider mysql --database shop

# Inspect columns with their native types
sqlflow catalog columns --source '${env:SHOP_MYSQL}' --provider mysql --object shop.orders

# Scaffold a ready-to-edit ingestion flow; the provider is embedded in the connections block
sqlflow catalog scaffold --source '${env:SHOP_MYSQL}' --provider mysql \
  --object shop.orders --target-object raw.ShopOrders --detect-keys --out shop-orders.flow.yaml
```

For a foreign provider, the scaffold emits the map connection form (`provider:` plus `connection:`) rather than the plain string form. `--detect-keys` profiles with T-SQL, so it is only effective for SQL Server and Azure SQL sources: for `mysql`, `postgres`, or `oracle` it always reports `no unique key detected; scaffolding without keyColumns.` without attempting to profile the table. Pin the key explicitly with `--keys id` for a foreign source instead. `sqlflow healthcheck` and `sqlflow detect-unique-key` run T-SQL against the inspected table too, so they reject `--provider mysql|postgres|oracle` with an explicit error.

## Under the hood: the provider registry

`SqlFlowSourceProviders.CreateRegistry()` (src/SqlFlow.Providers/SqlFlowSourceProviders.cs) bundles everything one provider contributes: the connection-string canonicalizer, the connection factory (MySqlConnector, Npgsql, Oracle.ManagedDataAccess), the catalog reader, the SQL dialect, and the type mapper. The composition roots concatenate it after the built-in SQL Server registry (src/SqlFlow.Execution/SqlFlowEngineServices.cs), so the CLI resolves all five kinds out of the box.

Dispatch is by the resolved connection's `DataSourceKind` (`MSSQL`, `AZDB`, `MySQL`, `PostgreSQL`, `Oracle`; src/SqlFlow.Core/Connections/DataSource.cs), and an unregistered kind fails loudly instead of loading zero rows:

```text
No connection provider is registered for data source kind 'MySQL'. Register the matching provider (for example from SqlFlow.Providers).
No catalog reader is registered for data source kind 'MySQL'. Register the matching provider (for example from SqlFlow.Providers).
```

End-to-end coverage for all three sources lives in tests/SqlFlow.Core.Tests/Integration/ForeignSourceIngestionTests.cs (gated on `SQLFLOW_TEST_MYSQL`, `SQLFLOW_TEST_PG`, and `SQLFLOW_TEST_ORACLE`), with per-provider catalog and type-fidelity suites under tests/SqlFlow.Core.Tests/Integration/MySql, Postgres, and Oracle. The connection-form parsing rules are covered by tests/SqlFlow.Core.Tests/YamlProviderConnectionTests.cs.

## See also

- [connections block and endpoint resolution](../flow/connections.md)
- [catalog CLI](../cli/catalog.md)
- [Table-to-table ingestion guide](table-to-table-ingestion.md)
- [Connections and secrets](../concepts/connections-and-secrets.md)
- [Environment variables](../concepts/environment-variables.md)
