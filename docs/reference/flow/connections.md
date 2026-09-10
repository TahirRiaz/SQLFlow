---
id: flow-connections
title: connections block and endpoint resolution
type: flow-reference
summary: The connections block of a flow YAML document, provider tokens, the SQLFLOW_CONN convention, and how endpoints resolve to a declared or direct connection.
keywords:
  - connections
  - alias
  - provider
  - server
  - connection
  - sqlflow_conn
  - endpoint resolution
  - mssql
  - mysql
  - postgres
yamlPath: connections
related:
  - concept-connections-and-secrets
  - concept-environment-variables
  - guide-table-to-table-ingestion
sourceRefs:
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.Core/Connections/ConnectionConvention.cs
  - src/SqlFlow.Core/Connections/DataSource.cs
  - src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs
  - src/SqlFlow.Yaml/YamlExportFlowLoader.cs
  - src/SqlFlow.Yaml/YamlStoredProcedureFlowLoader.cs
  - src/SqlFlow.Yaml/YamlHealthCheckFlowLoader.cs
  - src/SqlFlow.Yaml/YamlSourceControlFlowLoader.cs
---

# connections block and endpoint resolution

The top-level `connections:` block names every database connection a flow document uses. Endpoint sections (`source:`, `target:`, `procedure:`) then reference a declared connection by name via `server:`, or carry a direct `connection:` reference of their own. One shared implementation (src/SqlFlow.Yaml/YamlDocumentParts.cs) maps and validates the block for every flow type that declares one (`ing`, `exp`, `sp`, `hc`, `scm`), so the forms, provider tokens, and error wording are identical across those document kinds. Connection values are `${...}` references (environment or Key Vault) or passwordless literal connection strings; secrets do not belong in the file.

```yaml
flowType: ing
name: orders-ingestion

connections:
  erp: ${env:SQLFLOW_SRC}
  dwh: ${env:SQLFLOW_DW}

source:
  server: erp
  object: AdventureWorks.Sales.Orders

target:
  server: dwh
  object: DW.raw.Orders

load:
  keyColumns: [OrderID]
```

## Keys reference

### The `connections:` block

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `connections` | map | no | empty map | Named connections. Each key is the connection name; each value is one of the three entry forms below. |
| `connections.<name>` | string, map, or empty | no (the value may be empty) | `${env:SQLFLOW_CONN_<NAME>}` | Plain string: a SQL Server connection reference (back-compatible form). Map: `provider` plus `connection`. Bare or empty: resolves the `SQLFLOW_CONN_<NAME>` environment variable by convention. |
| `connections.<name>.provider` | string | no | `mssql` | Database provider token. One of `mssql`, `sqlserver`, `azdb`, `mysql`, `postgres`, `postgresql`, `oracle` (case-insensitive). |
| `connections.<name>.connection` | string | no | convention reference | Connection string or `${...}` reference. When omitted in the map form, the entry keeps its provider and takes the `${env:SQLFLOW_CONN_<NAME>}` convention. |

### Endpoint connection keys (on `source:`, `target:`, `procedure:`)

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `server` | string | one of `server`/`connection` | none | Name of a connection declared under `connections:`. |
| `connection` | string | one of `server`/`connection` | none | Direct connection string or `${...}` reference; registered under a synthetic connection name (`source` or `target`). |
| `provider` | string | no | `mssql` | Provider token for a direct `connection:`. Same tokens as `connections.<name>.provider`. Ignored when `server:` is used (the declared connection already carries its provider). |

## `connections.<name>`: the three entry forms

### 1. Plain string

A string value is a SQL Server connection reference (the back-compatible form). It can be a whole `${env:NAME}` or `${keyvault:vault/secret}` reference, or a passwordless literal connection string:

```yaml
connections:
  dwh: ${env:SQLFLOW_DW}
```

The provider is always `MSSQL` for the string form; a foreign provider requires the map form.

### 2. Map with `provider` and `connection`

```yaml
connections:
  shop:
    provider: mysql
    connection: ${env:SHOP_MYSQL}
```

Only the `provider` and `connection` keys are read from the map (key matching is case-insensitive after trimming). A map without `connection:` keeps the declared provider and takes the environment-variable convention:

```yaml
connections:
  shop:
    provider: mysql    # resolves ${env:SQLFLOW_CONN_SHOP}, kind stays MySQL
```

### 3. Bare alias (the convention)

A name with no value at all resolves its well-known environment variable:

```yaml
connections:
  dwh:                 # resolves ${env:SQLFLOW_CONN_DWH}
```

The convention is defined once in src/SqlFlow.Core/Connections/ConnectionConvention.cs: the name is trimmed, uppercased, every non-alphanumeric character is folded to `_`, and the result is prefixed with `SQLFLOW_CONN_`. So `my-dwh` resolves `${env:SQLFLOW_CONN_MY_DWH}`. Names are strictly uppercase because environment variables are case-sensitive on Linux. A bare alias is SQL Server (`MSSQL`) by default.

A value that is none of these three forms (for example a YAML list) fails with:

```text
<file>: connection '<name>' must be a connection string, a ${...} reference, a map with 'provider' and 'connection', or bare (which resolves '${env:SQLFLOW_CONN_<NAME>}' by convention).
```

## Connection names

Names must be non-empty and use only ASCII letters, digits, `_`, `.`, or `-`. An invalid name fails with:

```text
<file>: connection name '<name>' is invalid. Use letters, digits, '_', '.', or '-'.
```

Names are compared case-insensitively; a duplicate (including one differing only in case) fails with:

```text
<file>: connection '<name>' is declared more than once.
```

## `provider`

Provider tokens map to the `DataSourceKind` enum (src/SqlFlow.Core/Connections/DataSource.cs). Matching is case-insensitive after trimming; a missing or empty token defaults to `MSSQL`.

| Token | Kind |
| --- | --- |
| `mssql`, `sqlserver` (or omitted) | `MSSQL` |
| `azdb` | `AZDB` |
| `mysql` | `MySQL` |
| `postgres`, `postgresql` | `PostgreSQL` |
| `oracle` | `Oracle` |

Any other token fails at parse time:

```text
<file>: 'connections.<name>.provider' has unknown provider '<value>'. Allowed: mssql, azdb, mysql, postgres, oracle.
```

The same parser handles an endpoint's own `provider:`, where the field in the message reads `<section>.provider` instead.

## Endpoint resolution: `server:` versus `connection:`

Each endpoint sets exactly one of the two keys.

**Both set** fails with:

```text
<file>: '<section>' sets both 'server' and 'connection'; use exactly one.
```

**Neither set** fails with:

```text
<file>: '<section>' needs a connection. Set '<section>.connection' to a connection string or a ${...} reference, or '<section>.server' to a name declared under 'connections:'.
```

**`server:` set**: the value must name a declared connection (lookup is case-insensitive). An undeclared name fails with:

```text
<file>: '<section>.server' references '<name>', which is not declared under 'connections:'.
```

**`connection:` set**: the direct reference is registered under a synthetic connection name; an optional sibling `provider:` selects its kind. The synthetic names per flow type:

| Flow type | Section | Synthetic name |
| --- | --- | --- |
| `ing` | `source` / `target` | `source` / `target` |
| `exp` | `source` | `source` |
| `sp` | `procedure` | `target` |
| `hc` | `target` | `target` |
| `scm` | `source` | `source` |

If `connections:` already declares the synthetic name, the direct form is ambiguous and fails with:

```text
<file>: '<section>.connection' is set, but 'connections:' already declares '<name>'. Either rename that connection or use '<section>.server: <name>'.
```

## SQL Server requirements per flow type

Some endpoints run T-SQL and must be SQL Server (`MSSQL` or `AZDB`). The constraint is enforced at parse time, not deep in the run:

| Flow type | Constrained endpoint | Requirement wording |
| --- | --- | --- |
| `ing` | `target` | an ingestion flow's target |
| `exp` | `source` | an export flow's source |
| `sp` | `procedure` | a stored-procedure flow's server |
| `hc` | `target` | a health-check flow's target |
| `scm` | `source` | a source-control flow's source |

A foreign provider on a constrained endpoint fails with:

```text
<file>: the <role> connection '<name>' is '<kind>'; <requirement> must be SQL Server (mssql or azdb).
```

For example, a `postgres` connection used as an ingestion target produces: `<file>: the target connection 'erp' is 'PostgreSQL'; an ingestion flow's target must be SQL Server (mssql or azdb).`

## Unmatched keys

The YAML loaders use CamelCase naming and ignore properties that do not match the model (YamlDotNet `IgnoreUnmatchedProperties`), so an unknown key inside an endpoint section is silently skipped rather than rejected. A connection map behaves the same way for a different reason: the block binds as a plain dictionary and the loader reads only the `provider` and `connection` keys from it.

## Full example

Adapted from samples/ingestion/mysql-to-sqlserver.flow.yaml: a MySQL source ingested into a SQL Server target, mixing all three entry forms.

```yaml
flowType: ing
name: shop-orders

connections:
  shop:                                 # foreign source declares its provider
    provider: mysql
    connection: ${env:SHOP_MYSQL}       # whole ${...} reference; no secret in the file
  dwh: ${env:SQLFLOW_DW}                # plain string = SQL Server
  audit:                                # bare alias; resolves ${env:SQLFLOW_CONN_AUDIT}

source:
  server: shop
  object: shop.orders                   # MySQL: database.table

target:
  server: dwh
  object: DW.raw.ShopOrders

load:
  keyColumns: [id]
```

The direct form, with no `connections:` block at all:

```yaml
flowType: ing
name: shop-orders-direct

source:
  provider: mysql
  connection: ${env:SHOP_MYSQL}         # registered as connection 'source'
  object: shop.orders

target:
  connection: ${env:SQLFLOW_DW}         # registered as connection 'target', MSSQL by default
  object: DW.raw.ShopOrders

load:
  keyColumns: [id]
```

## See also

- [Connections and secrets](../concepts/connections-and-secrets.md)
- [Environment variables](../concepts/environment-variables.md)
- [Table-to-table ingestion end to end](../guides/table-to-table-ingestion.md)
