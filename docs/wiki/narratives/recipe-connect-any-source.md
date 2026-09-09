---
id: wiki-recipe-connect-any-source
title: "Recipe: connecting any source, and picking the right flow kind for it"
type: narrative
summary: "Every integration in one table: which flow kind fetches it, the minimum YAML, and the rule for when two kinds could both work."
keywords:
  - integration
  - connectors
  - source types
  - http
  - graphql
  - soap
  - sftp
  - s3
  - azure table
  - mysql
  - postgres
  - oracle
  - parquet
  - delta
  - duckdb
sourceRefs:
  - src/SqlFlow.Acquire/Engine/IAcquireTransport.cs
  - src/SqlFlow.Providers/SqlFlowSourceProviders.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
referenceRefs:
  - flow-overview
  - flow-source
  - flow-connections
  - concept-connections-and-secrets
related:
  - wiki-recipe-api-source
  - wiki-recipe-vendor-file-source
  - wiki-recipe-database-source
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: connecting any source

## Pick the flow kind first

| The source is | Flow kind | Then |
| --- | --- | --- |
| An HTTP API (REST, GraphQL, SOAP) | `api` | file flow -> `ing` |
| An SFTP server | `api` (`transport: sftp`) or `sftp` | file flow -> `ing` |
| An Azure Storage Table | `api` (`transport: azuretable`) | file flow -> `ing` |
| An S3 bucket | `cpy` | file flow -> `ing` |
| Another Azure lake or local disk | `cpy` | file flow -> `ing` |
| Files already in your lake | file flow directly | `ing` |
| A relational database | `ing` directly | no lake hop |
| A system that pushes to you | a receive endpoint | file flow -> `ing` |
| A pipeline you do not own | `inv` | it reports back |

**The rule when two could work:** `api` when the fetch needs request semantics (auth, pagination,
fan-out, a watermark). `sftp` or `cpy` when it is a pure file transfer. `ing` when the source is
already a queryable table, because a lake hop you do not need is two more flows to maintain.

## HTTP, in its three dialects

```yaml
# REST
source:
  transport: http
  baseUrl: https://api.partner.example
  request: { method: GET, path: /v1/orders }
```

```yaml
# GraphQL: the body is the query text, wrapped and sent as JSON
source:
  request:
    method: POST
    path: /graphql
    bodyKind: graphql
    body: "{ orders(from: \"{window.from:yyyy-MM-dd}\") { id total } }"
```

```yaml
# SOAP: the body is the envelope, sent as text/xml
source:
  request:
    method: POST
    path: /services/OrderService
    bodyKind: soap
    contentType: text/xml; charset=utf-8
    body: |
      <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
        <soap:Body><GetOrders xmlns="urn:example"/></soap:Body>
      </soap:Envelope>
```

Also available: `bodyKind: json`, `form` (with `bodyFields`), `raw` (with `contentType`), and `none`.

## SFTP, two ways

```yaml
# as an api transport: credentials and locators live in source.options
source:
  transport: sftp
  baseUrl: sftp://sftp.partner.example:22
  options:
    username: svcaccount
    privateKey: ${keyvault:my-vault/partner-sftp-key}
    remotePath: /outbound
    pattern: "*.csv"
    modifiedWithinDays: 4
    take: "5"
landing:
  target: abfss://fs@account.dfs.core.windows.net/raw/partner/history
  pathTemplate: "{yyyy}/{page}"
```

```yaml
# as a dedicated sftp flow: byte-for-byte, and it can upload as well as download
flowType: sftp
direction: download
server: { host: sftp.partner.example, port: 22, username: svcaccount,
          privateKeyRef: ${keyvault:my-vault/partner-sftp-key} }
remotePath: "/outbound"
pattern: "*.csv"
local: abfss://fs@account.dfs.core.windows.net/raw/partner/history
output: { location: abfss://fs@account.dfs.core.windows.net/raw/partner/history/, srcFile: "*.csv" }
```

Prefer the `sftp` flow for a plain drop: it does `direction: upload` too, and its `output` block makes
the lineage edge explicit. Use the `api` transport when the same flow also needs the acquisition
envelope (rate limits, retry, protect rules).

## Azure Storage Table

```yaml
source:
  transport: azuretable
  baseUrl: https://myaccount.table.core.windows.net
  options:
    tableName: OrderEvents
    accountUrl: https://myaccount.table.core.windows.net
    sasToken: ${keyvault:my-vault/table-sas}
    filter: "PartitionKey eq '{window.from:yyyyMMdd}'"
    select: "PartitionKey,RowKey,OrderId,Total"
```

`filter` is OData and is templated with iteration variables, so a date-window fan-out drives it. A
SAS token containing `&` must be a secret reference, never pasted on a command line, where the `&`
truncates it silently.

## S3

S3 is a `cpy` source, not an `api` transport. It has no ambient identity, so each item carries its
key pair:

```yaml
flowType: cpy
operation: copy
options: { overwrite: true, preserveStructure: false }
items:
  - source:
      location: s3://my-bucket/export_orders
      pattern: "*"
      modifiedWithinDays: 10
      accessKeyRef: ${keyvault:my-vault/aws-access-key}
      secretKeyRef: ${keyvault:my-vault/aws-secret-key}
      region: eu-west-1
    target:
      location: abfss://fs@account.dfs.core.windows.net/raw/vendor/history/orders
```

The Azure side uses the ambient managed identity, so only the foreign cloud needs a stored
credential.

## Files already in the lake

```yaml
source:
  type: csv          # csv | json | xml | xls | parquet | duckdb | delta
  location: https://account.dfs.core.windows.net/fs/raw/vendor/history/
  options:
    srcFile: "*.csv"
    searchSubDirectories: "true"
```

Two of these behave differently from the rest and are worth knowing about:

```yaml
# parquet carries its own schema, so it needs no type inference at all
source:
  type: parquet
  location: abfss://fs@account.dfs.core.windows.net/silver/orders/
```

```yaml
# duckdb runs a query over files, with predicate pushdown, before anything is loaded
source:
  type: duckdb
  location: ./data/orders/**/*.parquet
```

```yaml
# delta reads a Delta table directly
source:
  type: delta
  location: abfss://fs@account.dfs.core.windows.net/silver/orders
```

Reach for `duckdb` when the useful thing is a subset or an aggregate of a large file set: filtering
before the load beats loading then filtering.

## Relational databases

```yaml
flowType: ing
connections:
  src: ${env:SQLFLOW_CONN_SOURCE}
  ods: ${env:SQLFLOW_CONN_ODS}
source:
  server: src
  object: "[SourceDb].[public].[orders]"
target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Orders]"
load:
  keyColumns: [order_id]
```

Providers: `mssql`, `azdb`, `mysql`, `postgres`, `oracle`. The provider is named on the CLI when
browsing, and resolved from the connection when running.

```bash
sqlflow catalog tables --source ${env:SQLFLOW_CONN_SOURCE} --provider postgres --database vendor
```

## Sources that push to you

Some systems will not be polled: they deliver. That acquisition is a **receive endpoint** rather than
a flow, so there is no stage-0 YAML to write. The chain starts at the file flow reading whatever the
sender wrote, and the flows downstream are ordinary.

The thing to get right is that the landing path the sender writes to is the same path your file flow
reads, since that is the joint lineage binds on. See
[chaining-flows-through-the-lake](chaining-flows-through-the-lake.md).

## Credentials, uniformly

Every kind above follows one rule: **a coordinate is literal, key material is a reference.** Host
names, usernames, bucket names, table names and regions are written plainly. Passwords, keys, tokens
and SAS strings are `${env:NAME}` or `${keyvault:vault/secret}`, resolved at run time.

```bash
sqlflow validate vendor/vendor_00_api.yaml   # warns on anything that looks like an embedded credential
sqlflow auth                                 # confirm the Azure identity resolves before blaming the source
```
