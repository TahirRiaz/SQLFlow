---
id: source-type-dataverse
title: "Dataverse source (source.type: dataverse)"
type: source-type
summary: "Read a Dataverse (Power Apps) table through its Web API into SQL Server: typed columns, schema evolution, a watermark sent as $filter, paging, throttling."
keywords:
  - dataverse
  - power apps
  - powerapps
  - dynamics 365
  - web api
  - odata
  - application user
  - versionnumber
  - modifiedon
  - service protection
  - retry-after
yamlPath: "source.options (type: dataverse)"
related:
  - flow-source
  - flow-incremental
  - flow-overview
  - concept-connections-and-secrets
sourceRefs:
  - src/SqlFlow.Core/Dataverse/DataverseSourceOptions.cs
  - src/SqlFlow.Dataverse/DataverseSourceReader.cs
  - src/SqlFlow.Dataverse/DataverseReadPlan.cs
  - src/SqlFlow.Dataverse/DataverseMetadataReader.cs
  - src/SqlFlow.Dataverse/DataverseClient.cs
  - src/SqlFlow.Dataverse/DataverseValues.cs
  - src/SqlFlow.Dataverse/DataverseTokenProvider.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
---

# Dataverse source (source.type: dataverse)

`source.type: dataverse` reads one Microsoft Dataverse table, the store behind Power Apps (and Dynamics 365), through the
Dataverse Web API and loads it into a SQL Server table. It is a file flow with a different reader: the target, the schema
evolution policy, the load modes, the row-level `incremental` block, the transform view, scheduling and lineage all work
as they do for files. The reader is `DataverseSourceReader` (src/SqlFlow.Dataverse/DataverseSourceReader.cs), registered
by the engine beside the file readers.

Each run reads the table's metadata first, so the columns arrive typed (a GUID as `uniqueidentifier`, currency as
`decimal`, a date-only column as `date`) and a column added or lengthened in Power Apps reaches the target through the
`widen` evolution with no change to the flow. The rows are then paged through `@odata.nextLink`, in the watermark
column's order when the flow declares one, with the engine's bound sent as a `$filter` so only new rows cross the network.

## Minimal example

```yaml
name: crm_account_01_dv
batch: account
source:
  type: dataverse
  location: https://contoso.crm4.dynamics.com
  options:
    table: account
target:
  connection: ${env:SQLFLOW_DW}
  schema: pre
  table: Crm_Account
incremental:
  watermarkColumn: versionnumber
```

With no service principal named, the reader signs in as the host's Azure identity. To name one:

```yaml
source:
  type: dataverse
  location: ${env:CRM_URL}
  options:
    table: cr6b2_project
    tenantId: ${env:CRM_TENANT_ID}
    clientId: ${env:CRM_CLIENT_ID}
    clientSecret: ${keyvault:kv-data/crm-client-secret}
    excludeColumns: cr6b2_notes
    formattedValues: "true"
```

`sqlflow validate` checks every option offline (a literal secret, an unknown option, a file-date incremental block are
refused there); the location is checked too unless it names a reference, which resolves only when the flow runs.

## Signing in

Dataverse accepts Microsoft Entra tokens for the scope `<environment URL>/.default`. Whatever identity reads the table
must be an **application user** in the environment (Power Platform admin center, the environment's Settings, Users and
permissions, Application users) with a security role that can read the table. A managed identity can be added as an
application user by its application id.

- **The host's identity** (no `tenantId`, `clientId` or `clientSecret`): the credential `SQLFLOW_AZURE_AUTH` selects for
  the whole host (managed identity, a service principal from `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_CLIENT_SECRET`,
  the Azure CLI's `az login`, or the default chain), the same one Key Vault and lake storage use. Nothing about the
  identity rests in the flow.
- **An explicit service principal**: `tenantId`, `clientId` and `clientSecret` together. `clientSecret` must be one whole
  `${env:NAME}` or `${keyvault:vault/secret}` reference; a literal fails the load. `authorityHost` sets the Entra
  authority for a sovereign cloud (`https://login.microsoftonline.us`); the host identity takes its own from
  `AZURE_AUTHORITY_HOST` instead.

The token is reused until five minutes before it expires. A request the service answers 401 gets one new token and is
sent again; a second 401 fails the run saying the identity must be an application user of this environment.

## Options

| Option | Default | Description |
| --- | --- | --- |
| `table` | (required) | The table's logical name (`account`, `cr6b2_project`), lower-cased. |
| `columns` | every readable column | Comma-separated logical names to read, in that order. The primary key and the watermark column are always read. |
| `excludeColumns` | none | Comma-separated logical names to leave out of a full read. Not with `columns`; never the primary key or the watermark column. |
| `filter` | none | An OData `$filter` expression (`statecode eq 0`), AND-combined with the watermark bound. The flow author is the trust boundary. |
| `formattedValues` | `false` | Adds a `<column>name` label column beside each choice, status, state, yes/no, multi-select and lookup column. |
| `pageSize` | `5000` | Rows per page (the `odata.maxpagesize` preference), 1 to 5000. |
| `tenantId`, `clientId`, `clientSecret` | host identity | An explicit service principal; all three or none. |
| `authorityHost` | public cloud | The explicit service principal's Entra authority host. |
| `apiVersion` | `9.2` | The Web API version (`api/data/v9.2/`). |
| `timeoutSeconds` | `300` | Seconds one request may take before it is retried, 10 to 3600. |
| `maxAttempts` | `8` | Total attempts per request, 1 to 20. |
| `maxRetryDelaySeconds` | `300` | The longest wait between attempts, which also bounds an honoured `Retry-After`, 1 to 3600. |
| `maxPageMegabytes` | `256` | The largest page held in memory, 1 to 2048. |

`location` is the environment URL alone, `https://<org>.crm<n>.dynamics.com`, or a `${...}` reference to it; the reader
adds the Web API path. A path, query or `http` scheme is refused. Any option not in the table is refused, so a misspelt
key fails rather than being ignored.

## Columns and types

The metadata is read through `EntityDefinitions(LogicalName='<table>')`: the table's collection name and primary key,
its columns, and through type casts the facts the target types depend on (text lengths, date behaviours, lookup targets).
Columns are classified by `AttributeTypeName`, as Microsoft advises, since `AttributeType` reports several kinds as
`Virtual`.

| Dataverse type | Target type | Row property |
| --- | --- | --- |
| Unique identifier | `UNIQUEIDENTIFIER` | the logical name |
| Single line of text | `NVARCHAR(n)`, n the larger of `MaxLength` and `DatabaseLength`; `NVARCHAR(MAX)` past 4000 | the logical name |
| Multiple lines of text | `NVARCHAR(MaxLength)`, `NVARCHAR(MAX)` past 4000 | the logical name |
| Whole number, choice, status, status reason | `INT` (a choice as its value) | the logical name |
| Big integer (`versionnumber`) | `BIGINT` | the logical name |
| Decimal | `DECIMAL(23, 10)` | the logical name |
| Currency | `DECIMAL(19, 4)` | the logical name |
| Float | `FLOAT` | the logical name |
| Yes/no | `BIT` | the logical name |
| Date and time (user local, time zone independent) | `DATETIME2`, in UTC | the logical name |
| Date only (DateOnly behaviour) | `DATE` | the logical name |
| Multi-select choice | `NVARCHAR(4000)`, its values comma-separated | the logical name |
| Table name | `NVARCHAR(128)` | the logical name |
| Lookup, customer, owner | `UNIQUEIDENTIFIER` | `_<name>_value` |
| Any other type (a managed property, a type added later) | `NVARCHAR(MAX)`, the value as the Web API writes it | the logical name |

Decimal and currency get one type whatever precision the column is set to (decimal holds up to 10 places within
plus or minus 100 billion, currency up to 4 within plus or minus 922 trillion), so changing a column's precision in
Power Apps never asks for a type change. A 64-bit value such as `versionnumber` is read exactly, never through a double.

The primary key lands first, then every other column by name (or in the order `columns` names them). A polymorphic
lookup (a customer or owner, or a lookup with several target tables) also gets `<column>type`, the table its value
points at (the `Microsoft.Dynamics.CRM.lookuplogicalname` annotation); with `formattedValues` the labels land as
`<column>name` (the `OData.Community.Display.V1.FormattedValue` annotation). A label or table column that would land on
a name the table already has is refused naming both.

Left out, and refused with the reason when named in `columns`: a column that is part of another (`AttributeOf` set, such
as a lookup's name), one not valid for read or not exposed by the Web API, image columns (a row holds only the
thumbnail), file columns (a row holds only the file's id), party lists, calendar rules and virtual columns.

## Schema evolution

The engine's `schema.evolve` applies as for any file flow ([Schema](../schema.md)). With the default `widen`, a column
added in Power Apps is added to the target on the next run, and a text column made longer is widened. A column removed
in Power Apps stays on the target and the rows loaded after carry no value in it. A column deleted and recreated with an
incompatible type is left alone by the reconciler, and the load then fails on the first value that does not convert:
give the column its new type with `schema.overrides`, or alter the target.

## Incremental reads

Declare a row-level watermark and the engine does the rest ([Incremental loading](../incremental.md)):

```yaml
incremental:
  watermarkColumn: versionnumber   # or modifiedon
  watermarkOverlap: 0              # raw units for a number, days for a date
```

A file-date block (`dateColumn`, `overlapDays`, or none of `watermarkColumn`) is refused at load: a table has no files.

- The engine probes the target's `MAX(versionnumber)` and the reader asks the service only for the rows past it:
  `$filter=versionnumber gt 638183964640916102` (combined with `filter` when one is set). A date bound is written to the
  whole second in UTC (`modifiedon gt 2026-11-01T08:15:30Z`), a date-only column's as a date (`2026-11-01`); flooring can
  only widen the read, and the engine still drops every row not strictly past the exact bound.
- Rows are read in watermark order, then by primary key (`$orderby=versionnumber asc,accountid asc`), on every run that
  declares a watermark, the first included. The bulk load commits batch by batch, so a run that fails part way keeps the
  lowest watermarks, and the next run resumes exactly after them.
- A bounded read that finds nothing is the steady state: the run succeeds with no rows, reported at the schema step,
  and a landing table is not reset. An unbounded read of an empty table loads nothing, so `truncateLoad` empties the
  target as the source is.

`versionnumber` grows with every change to a row, across the whole environment, so two rows never share one; it is the
first choice. Microsoft documents it as a read-only 64-bit column on every table but does not document filtering or
ordering by it; an environment that refuses either fails the run with the service's own message, and `modifiedon` is the
alternative. `modifiedon` keeps whole seconds, so several rows can share one: when a run fails between two batches of
rows modified in the same second, the rest of that second is behind the next bound. Set `watermarkOverlap` to a few
seconds' worth of days (0.0001 is about 9 seconds) to read them again. A transaction that commits after a later change
was read is behind the bound the same way, for either column, and an overlap covers it too.

Each incremental run appends the current state of every row that changed, so with `load.mode: append` the target is a
change log: one row per change read, not one per record. To keep the current state, either reload in full
(`load.mode: truncateLoad`, no incremental block) or read the newest row per key downstream
(`ROW_NUMBER() OVER (PARTITION BY accountid ORDER BY versionnumber DESC) = 1`). An `ing` flow keyed on the primary key
keeps one row per key from what its source holds without regard to which is newest, so let it consolidate each load
before the next (`load.resetWhenConsolidated`, the default) when it reads this table.

## Deleted rows

A row deleted in Power Apps leaves no trace a watermark read can see. A full reload reconciles deletes: `truncateLoad`
with no incremental block, on its own schedule or as an occasional `sqlflow run --full`. Dataverse change tracking is
not used. A backfill window (`--from`/`--to`) or a file pattern does not apply to a table and is refused; use `--full`.

## Paging, limits and failures

- Pages are requested with `Prefer: odata.maxpagesize` and followed through `@odata.nextLink` exactly as the service
  writes it. The next page is fetched while the current one loads, so at most two pages are held in memory. A page past
  `maxPageMegabytes` fails the run naming `pageSize` and `maxPageMegabytes`.
- Every request goes to the environment's own host only. A next link to any other host stops the read before the token
  is sent anywhere; a page that points back at itself, or a long run of empty pages that keep pointing on, stops it too.
- The Web API takes request URLs up to 32,768 characters. A table so wide that its column list passes that is read
  without `$select` (every column comes back and the reader keeps its own); a `filter` too long even then is refused.
- Service protection answers 429 with `Retry-After`; the reader waits at least that long (up to `maxRetryDelaySeconds`)
  and retries, as it does for 408, 425, 500, 502, 503, 504 and a dropped connection, up to `maxAttempts` in all.
- Any other refusal fails the run with the service's own message and code, plus what to do for the common ones: 401
  (the identity is not an application user here), 403 (give its application user a security role that reads the table),
  404 (check the table's logical name and the URL), and a request too long for the service.
- A value that cannot be its column's type fails the run naming the table, the row's primary key, the column and an
  excerpt of the value.

## Lineage and the catalog

A dataverse source is one lineage node per environment and table, identified as `<location>/<table>` (the location as
written, so a `${...}` reference stays a reference), and nothing lands into it. The flow's load profile names the table
it reads rather than files, and says when it reads in watermark order. The run's processed-file line reads
`read 'account (3 page(s))' (12 row(s))`.

## Errors

| Condition | Message (abridged) |
| --- | --- |
| No `table` | `A dataverse source needs the option 'table': the table's logical name, such as account.` |
| A location that is not the environment URL | `The dataverse source location '<v>' must be the environment URL alone (https://<host>); ...` |
| A literal secret | `'clientSecret' must be a ${env:NAME} or ${keyvault:vault/secret} reference, never a literal.` |
| An unknown option | `A dataverse source does not take the option '<key>'. Its options are: ...` |
| A file-date incremental block | `A dataverse source reads a table, not files, so its incremental block needs 'watermarkColumn' ...` |
| A column named that the table lacks | `'columns' names '<name>', which the Dataverse table '<table>' does not have. Its readable columns are: ...` |
| A watermark column with no order | `The incremental watermark column '<c>' ... is a <type> column, which has no order to resume from. ...` |
| Throttled past every attempt | `Reading a page of the table '<t>' from the Dataverse environment <url> failed after every retry: HTTP 429 ...` |
| Refused | `Reading <what> from the Dataverse environment <url> was refused: HTTP <code> <status>: <service message> (code <c>). <advice>` |
