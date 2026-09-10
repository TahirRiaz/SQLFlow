---
id: source-type-duckdb
title: "DuckDB / Delta source (source.type: duckdb | delta)"
type: source-type
summary: Run a DuckDB query over Parquet/CSV/JSON files or Delta tables, locally or on ADLS/S3, with typed columns and predicate pushdown.
keywords:
  - duckdb
  - delta
  - query
  - extensions
  - adls
  - s3
  - cloud storage auth
  - predicate pushdown
  - hive partitioning
yamlPath: "source.options (type: duckdb|delta)"
related:
  - cli-auth
  - source-type-parquet
  - flow-source
  - flow-incremental
sourceRefs:
  - src/SqlFlow.DuckDb/DuckDbSourceReader.cs
  - src/SqlFlow.DuckDb/DuckDbQuery.cs
  - src/SqlFlow.DuckDb/DuckDbAzureSecret.cs
  - src/SqlFlow.DuckDb/DuckDbTypeMap.cs
  - src/SqlFlow.Azure/AzureStorageCredentialProvider.cs
  - src/SqlFlow.Core/Connections/CloudCredential.cs
  - src/SqlFlow.Core/Model/FileDateSpec.cs
  - src/SqlFlow.Core/Model/Watermark.cs
---

# DuckDB / Delta source (source.type: duckdb | delta)

The DuckDB source is the power-tier alternative to the managed Parquet reader. `source.type: duckdb` (or `delta`, both case-insensitive) opens an in-memory DuckDB database (`DataSource=:memory:`), builds a scan or a verbatim SQL query over the source location, and streams the typed result rows straight into the engine's bulk loader. It reads Parquet, CSV, and JSON files (a single path, a glob, or a partitioned dataset) and, through auto-loaded DuckDB extensions, ADLS/Blob and S3-compatible object storage and Delta tables. Unlike every managed file reader (CSV, JSON, XML, XLS, and Parquet all share the `FileSourceReaderBase` pipeline), it carries no file lifecycle (copy/zip/delete), no provenance columns, and no hash or concatenation keys; it is a typed scan-and-stream reader that trades that pipeline for predicate pushdown, cloud storage auth, and Delta table support. The native libduckdb dependency is isolated to src/SqlFlow.DuckDb, so the managed core stays dependency-free.

`source.type: delta` is the same reader with the format forced to `delta`: the location is read through `delta_scan`, which follows the Delta transaction log to the current snapshot (or, with time travel options, an earlier one) instead of the raw Parquet files.

## Minimal example

```yaml
name: DuckDb_Orders
source:
  type: duckdb
  location: ./data/orders/**/*.parquet
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Orders
```

A Delta table needs only the type token:

```yaml
name: Delta_Orders
source:
  type: delta
  location: abfss://lake@myaccount.dfs.core.windows.net/silver/orders
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Orders
```

## Location and relation

`source.location` is a file path, a glob, or a Delta table path. Alternatively `options.query` supplies a full SQL query that overrides the location scan entirely. With neither, the flow fails with:

```text
A duckdb source needs a 'location' (a file/glob/table path) or a 'query' option.
```

The location literal is single-quote escaped before it is placed inside the scan call, so a path can never inject SQL. The resolved relation is one of:

| Format | Relation |
|---|---|
| `parquet` | `read_parquet('<location>' [, hive_partitioning = true] [, union_by_name = true])` |
| `csv` | `read_csv_auto('<location>')` |
| `json` | `read_json_auto('<location>')` |
| `delta` | `delta_scan('<location>' [, version = N \| , timestamp = '...'])` |
| `query` option set | `(<query>) AS _src` |

## Options reference

All options live under `source.options` and are matched case-insensitively.

| Option | Type | Default | Description |
|---|---|---|---|
| `query` | string | none | Verbatim SQL query used as the relation, wrapped as `(query) AS _src`. Overrides the location scan. |
| `format` | string | inferred | Explicit file format: `parquet` (or `prq`), `csv`, `json` (or `ndjson`, `jsonl`), `delta`. |
| `columns` | string | all columns | Comma-separated projection; keeps exactly those columns in the requested order. |
| `filter` | string | none | Verbatim WHERE expression pushed into the scan. |
| `init` | string | none | Semicolon-separated setup SQL run before the query (`SET`, `CREATE SECRET`, and similar). |
| `extensions` | string | none | Comma-separated extra DuckDB extensions to INSTALL and LOAD. |
| `deltaVersion` | integer | latest | Delta time travel: pins the snapshot at this non-negative version number. |
| `deltaTimestamp` | string | latest | Delta time travel: pins the snapshot at this timestamp. Ignored when `deltaVersion` is set. |
| `hivePartitioning` | bool string | `false` | Adds `hive_partitioning = true` to `read_parquet`, exposing Hive path partition columns as columns. |
| `unionByName` | bool string | `false` | Adds `union_by_name = true` to `read_parquet`, reconciling files whose columns differ in order or presence. |
| `cloudAuth` | string | enabled | `off`, `false`, `none`, `no`, or `0` disables automatic Azure storage authentication; any other value, or omitting the option, leaves it enabled. |
| `fileDate.from` | string | `modified` | `path` or `name` reads the file's business date from its path or name (see file date pushdown). |
| `fileDate.hive` | bool string | `false` | With `fileDate.from: path`, parse Hive `key=value` tokens (`year=2025/month=03`). |
| `fileDate.partitions` | string | `year,month,day` | Ordered Hive partition column names used for window pushdown; only `year`, `month`, `day` are recognized. |
| `initFromFileDate` | date string | none | Lower bound of the file-date window (`yyyy-MM-dd` or a full timestamp). |
| `initToFileDate` | date string | none | Upper bound of the file-date window. |

### query

A full SQL query run verbatim as `(query) AS _src`. Use it when a plain scan is not enough (joins across files, aggregation, DuckDB functions). `columns` and `filter` still apply on top of the query result, since they are rendered into the outer SELECT.

### format

The effective format is resolved in this order:

1. `source.type: delta` forces `delta`, regardless of the `format` option.
2. An explicit `format` option wins otherwise.
3. Else the format is inferred from the location's file extension (query strings after `?` are ignored): `csv`, `tsv`, `txt` map to `csv`; `json`, `ndjson`, `jsonl` map to `json`; everything else defaults to `parquet`.

Recognized format tokens are `parquet`, `prq`, `csv`, `json`, `ndjson`, `jsonl`, and `delta`. Anything else fails with:

```text
Unknown duckdb source format '<f>'. Use parquet, csv, json, or delta.
```

### columns

A comma-separated list of column names (matched case-insensitively against the relation). An empty or absent option keeps every column in file order; a non-empty list keeps exactly the named columns in the requested order. An unknown name fails with:

```text
duckdb source 'columns' names '<name>', which the relation does not have. Available: <col1>, <col2>, ...
```

### filter

A verbatim WHERE expression rendered into the streamed SELECT, so DuckDB pushes it into the scan (row-group and partition skipping for Parquet/Delta). The filter is combined with the engine-injected incremental watermark predicate and the file-date partition predicate using AND.

### init

Semicolon-separated statements executed on the connection before the query, after extensions load and after any auto-generated Azure secret (so an explicit `init` secret can override it). The trust boundary is the flow author, exactly like a preProcess hook. Typical uses are `SET` pragmas and `CREATE SECRET` statements for cloud credentials the auto-auth path does not cover (S3, for example).

### extensions

Comma-separated extension names, each validated to letters, digits, and underscore (then lowercased); an invalid name fails with:

```text
duckdb extension name '<ext>' is invalid; use letters, digits, and underscore.
```

Extensions the format and location imply are added automatically and de-duplicated:

- `delta` when the effective format is `delta`.
- `azure` when the location starts with `abfss://`, `abfs://`, `az://`, or `azure://`.
- `httpfs` when the location starts with `s3://`, `gs://`, `gcs://`, `r2://`, `http://`, or `https://`.

Each required extension is run through `INSTALL <ext>; LOAD <ext>;` before the scan, so a Delta table on ADLS reads with no manual wiring.

### deltaVersion and deltaTimestamp

Delta time travel. `deltaVersion` must be a non-negative whole number and is rendered as `version = N` in `delta_scan`; an invalid value fails with:

```text
duckdb source 'deltaVersion' must be a non-negative whole number, got '<v>'.
```

When `deltaVersion` is absent, `deltaTimestamp` (single-quote escaped) is rendered as `timestamp = '<t>'`. With neither, `delta_scan` reads the latest snapshot.

### hivePartitioning and unionByName

Both apply only to the `read_parquet` relation. `hivePartitioning: "true"` exposes Hive path partition columns (`year=2025/month=03`) as regular columns; it is also enabled implicitly whenever `fileDate.from: path` with `fileDate.hive: "true"` is set, because the file-date window pushes down onto those columns. `unionByName: "true"` reconciles files whose columns differ in order or presence (schema drift across parts).

## Column types

Unlike the text-format managed readers (CSV, JSON, XML, XLS), which declare string columns and leave typing to the downstream inference step, the DuckDB source exposes typed columns, not raw strings, the same way the managed Parquet reader does. The reader runs `DESCRIBE SELECT * FROM <relation>` and maps each DuckDB logical type to a CLR type and SQL Server type through src/SqlFlow.DuckDb/DuckDbTypeMap.cs:

| DuckDB type | CLR type | SQL Server type |
|---|---|---|
| `BOOLEAN` | `bool` | `bit` |
| `TINYINT` / `SMALLINT` | `short` | `smallint` (DuckDB TINYINT is signed) |
| `UTINYINT` | `byte` | `tinyint` |
| `INTEGER` / `USMALLINT` | `int` | `int` |
| `BIGINT` / `UINTEGER` | `long` | `bigint` |
| `UBIGINT` | `decimal` | `decimal(20,0)` |
| `HUGEINT` / `UHUGEINT` | `decimal` | `decimal(38,0)` |
| `DECIMAL(p,s)` | `decimal` | `decimal(p,s)` (p clamped to 1..38, s to 0..p; bare DECIMAL is 18,3) |
| `FLOAT` | `float` | `real` |
| `DOUBLE` | `double` | `float` |
| `VARCHAR(n)` | `string` | `nvarchar(n)` when n is 1..4000, else `nvarchar(max)` |
| `VARCHAR` / `TEXT` | `string` | `nvarchar(max)` |
| `BLOB` | `byte[]` | `varbinary(max)` |
| `DATE` | `DateTime` | `date` |
| `TIME` | `TimeSpan` | `time` |
| `TIMESTAMP` | `DateTime` | `datetime2` |
| `TIMESTAMP WITH TIME ZONE` | `DateTimeOffset` | `datetimeoffset` |
| `UUID` | `Guid` | `uniqueidentifier` |
| list / struct / map / union | `string` | `nvarchar(max)` (projected as JSON via `to_json(...)`) |
| anything else (INTERVAL, ENUM, JSON, ...) | `string` | `nvarchar(max)` |

Nested columns (lists, structs, maps, unions) are projected as JSON text, the same subtree-as-string contract the Parquet reader uses. All columns are treated as nullable, because schema evolution across files null-fills a column missing from some of them.

A relation that describes to zero columns fails with:

```text
The duckdb source produced no columns (relation: <r>).
```

## File date pushdown

When the flow declares a path-derived Hive file date (`fileDate.from: path` plus `fileDate.hive: "true"`) and the effective format is `parquet` or `delta`, the file-date window is rendered as a predicate on the Hive partition columns and pushed into the scan, so DuckDB prunes whole partition folders instead of reading them. A name-derived or modified-timestamp date has no scan-level column to push down to and produces no predicate; CSV and JSON scans do not push down either.

The bound dates come from `initFromFileDate` and `initToFileDate` (authored, or injected by a backfill window) and from the engine-injected `incrementalAfterDate` watermark; the effective lower bound is the later of init-from and the watermark. Bounds accept `yyyy-MM-dd`, `yyyyMMdd`, or full timestamps; an unparseable bound fails with:

```text
Invalid file date bound '<value>'. Use yyyy-MM-dd or a full timestamp.
```

Comparison happens at day granularity: a partition on the boundary day is kept (its later rows may be past the watermark) and the keyed upsert dedups any overlap.

`fileDate.partitions` names the ordered Hive partition columns (default `year,month,day`). Only `year`, `month`, and `day` are recognized; a coarser layout widens the predicate (a `year`-only partition spans the whole year, a `year,month` partition the whole month). Validation errors:

```text
fileDate.partitions column '<name>' is invalid; use letters, digits, and underscore.
fileDate.partitions recognizes year, month, and day; got '<name>'.
fileDate.partitions must include a 'year' column for partition pushdown.
fileDate.partitions lists 'day' without 'month'; a day partition needs its month.
```

## Row-level incremental pushdown

When the flow has a row-level watermark (`incremental.watermarkColumn`), the engine probes the target for the high-water mark and injects it into the source options as `incrementalColumn` / `incrementalValue` / `incrementalKind`. The DuckDB reader renders the bound as an injection-safe predicate (`"col" > <literal>`, with typed literals such as `TIMESTAMP '...'` for datetimes) and pushes it into the scan for row-group skipping. These three options are engine-managed; do not author them.

## Cloud storage authentication (Azure)

Reads from Azure object storage authenticate automatically from the ambient `SQLFLOW_AZURE_AUTH` intent, the same intent Key Vault and invoke use. When the host wires an `ICloudCredentialProvider`, the reader generates a single statement before the query:

```sql
CREATE OR REPLACE SECRET sqlflow_azure (TYPE AZURE, ...)
```

The secret name is the fixed constant `sqlflow_azure`, re-created idempotently on each open. The provider variants:

- Service principal (`SQLFLOW_AZURE_AUTH=sp`): `PROVIDER service_principal` with `TENANT_ID`, `CLIENT_ID`, `CLIENT_SECRET` taken from the `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` / `AZURE_CLIENT_SECRET` environment variables (never from the flow file); each value is single-quote escaped. A missing variable fails with `Azure service-principal auth (SQLFLOW_AZURE_AUTH=sp) requires environment variable '<name>'.`
- Managed identity (`SQLFLOW_AZURE_AUTH=mi`): `PROVIDER credential_chain, CHAIN 'managed_identity'`.
- Azure CLI (`SQLFLOW_AZURE_AUTH=cli`): `PROVIDER credential_chain, CHAIN 'cli'`.
- Default chain: `CHAIN 'managed_identity;cli;env'` when running in Azure, `CHAIN 'cli;env'` off-cloud (so the read does not stall on the IMDS probe on a developer machine).

A user-assigned managed identity is rejected loudly, because DuckDB's managed-identity chain cannot target a specific client id:

```text
DuckDB cloud reads support only a system-assigned managed identity; a user-assigned identity (AZURE_CLIENT_ID with SQLFLOW_AZURE_AUTH=mi) cannot be passed to DuckDB. Use a service principal (SQLFLOW_AZURE_AUTH=sp) for the DuckDB source, or run where it is the default identity. The .NET paths (Key Vault, invoke) do honor AZURE_CLIENT_ID.
```

Auto-auth is skipped entirely when any of these hold:

- No cloud credential provider is wired by the host.
- `cloudAuth` is `off`, `false`, `none`, `no`, or `0`.
- The author's `init` statements already contain a `CREATE SECRET` (the operator manages auth).
- The location is not resolvable Azure storage.

The storage account is parsed from `abfss://`, `abfs://`, `az://`, `azure://`, `wasb://`, and `wasbs://` URIs: the `container@account.dfs.core.windows.net` form or any `*.core.windows.net` host yields the account. A bare `az://container/path` carries no account and yields no auto-auth (declare a secret in `init` instead). Non-Azure schemes (`s3`, `gs`, `file`) return null. A parsed account name must be purely alphanumeric or the credential is not generated, so a malformed URI never reaches the secret statement.

For S3-compatible storage the `httpfs` extension auto-loads, but credentials are not auto-generated: declare them in `init` (for example `CREATE SECRET s3 (TYPE S3, KEY_ID '...', SECRET '...')`).

## Full example

A partitioned Parquet lake with a Hive file-date window, projection, and a pushed-down filter (adapted from samples/csv/csv-file-date-partitions.flow.yaml):

```yaml
name: DuckDb_LakeOrders
source:
  type: duckdb
  location: abfss://lake@myaccount.dfs.core.windows.net/bronze/orders/**/*.parquet
  options:
    fileDate.from: path
    fileDate.hive: "true"
    fileDate.partitions: year,month
    initFromFileDate: "2025-01-01"
    initToFileDate: "2025-12-31"
    columns: OrderId, CustomerId, Amount, UpdatedAt
    filter: Amount > 0
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: LakeOrders
```

A Delta table pinned to an earlier snapshot:

```yaml
name: Delta_OrdersAsOf
source:
  type: delta
  location: abfss://lake@myaccount.dfs.core.windows.net/silver/orders
  options:
    deltaVersion: "42"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: OrdersAsOf
```

Run either with the CLI:

```bash
sqlflow run flows/duckdb-lake-orders.flow.yaml
```

## See also

- [source](../source.md): the source section every flow declares.
- [Parquet source](parquet.md): the pure-managed Parquet reader this source is the power-tier alternative to.
- [incremental](../incremental.md): file-date and row-level watermark modes that feed this reader's pushdown.
- [auth](../../cli/auth.md): the SQLFLOW_AZURE_AUTH intent and Azure credential setup.
