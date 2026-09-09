---
id: wiki-recipe-api-source
title: "Recipe: a REST API into the warehouse, end to end (api + jsn + ing)"
type: narrative
summary: "The three files that take a paged REST endpoint to an arc table, the schedule that fires them in order, and the commands to validate, run and verify each stage."
keywords:
  - recipe
  - end to end
  - api
  - json
  - ing
  - schedule
  - three stage
  - acquisition
sourceRefs:
  - src/SqlFlow.Acquire/Engine/AcquireEngine.cs
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
referenceRefs:
  - flow-api
  - source-type-json
  - flow-ing
  - flow-transform
  - cli-run
related:
  - wiki-chaining-flows-through-the-lake
  - wiki-recipe-inspect-and-debug
  - wiki-recipe-backfill-and-replay
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: a REST API into the warehouse, end to end

Three files in one folder named for the source. Stage 0 lands raw JSON, stage 1 types it into `pre`,
stage 2 merges it into `arc`. One schedule fires all three, wave-ordered.

```
vendor/
  schedules.yaml           the source's schedule, defined once
  vendor_00_api.yaml       stage 0: fetch  -> raw/vendor/api/trips/history/...
  vendor_trips_01_jsn.yaml stage 1: files  -> pre.Vendor_Trips (+ pre.v_Vendor_Trips)
  vendor_trips_02_ing.yaml stage 2: view   -> arc.Vendor_Trips
```

Naming is `<source>_<object>_<counter>_<flowtype>`, `<source>` being the folder lowercased. `batch`
is the dataset (`trips`), except on the acquisition flow where it is the flow type (`api`).

## schedules.yaml

```yaml
schedules:
  vendor_daily:
    cron: "0 4 * * *"
    timezone: Europe/Oslo
    catchup: false
    enabled: true
```

## Stage 0: vendor_00_api.yaml

```yaml
flowType: api
name: vendor_00_api
batch: api
schedule: vendor_daily

source:
  transport: http
  baseUrl: https://api.partner.example
  auth:
    type: oauth2_client_credentials
    token:
      url: https://auth.partner.example/oauth/token
      bodyKind: form
      body:
        grant_type: client_credentials
        client_id: ${keyvault:my-vault/vendor-client-id}
        client_secret: ${keyvault:my-vault/vendor-client-secret}
      tokenPath: access_token
  request:
    method: GET
    path: /v1/trips
    headers:
      Accept: application/json
    query:
      from: "{window.from:yyyy-MM-dd}"
      to: "{window.to:yyyy-MM-dd}"
  pagination:
    strategy: offset
    offsetParam: offset
    limitParam: limit
    limit: 500
    recordsPath: $.data
    maxPages: 2000
  iterate:
    - kind: date_window
      granularity: daily
      from: now-7d
      to: now
      fromVariable: window.from
      toVariable: window.to
  reliability:
    timeoutSeconds: 120
    rateLimitRps: 5
    concurrency: 4
    urlAllowlist: ["api.partner.example"]
    skipStatusCodes: [404]
    retry:
      maxAttempts: 4
      baseDelayMs: 500
      maxDelayMs: 30000

landing:
  target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/trips
  pathTemplate: "history/{yyyy}/{MM}/vendor_trips_{window.from:yyyyMMdd}_{page}"
  format: json
  skipEmpty: true
  skipUnchanged: true
```

```bash
sqlflow validate vendor/vendor_00_api.yaml
sqlflow run      vendor/vendor_00_api.yaml
```

## Stage 1: vendor_trips_01_jsn.yaml

The `location` is the acquisition `target` plus the static prefix of its `pathTemplate`
(`history/`), and `srcFile` is the literal part of the template's file name. See
[chaining-flows-through-the-lake](chaining-flows-through-the-lake.md).

```yaml
name: vendor_trips_01_jsn
batch: trips
schedule: vendor_daily

source:
  type: json
  location: https://account.dfs.core.windows.net/fs/raw/vendor/api/trips/history/
  options:
    srcFile: "vendor_trips_*.json"
    searchSubDirectories: "true"
    rootPath: "$.data"
    includePaths: "$.tripId,$.startedAt,$.endedAt,$.riderId,$.distanceM"
    includeRowNumber: "false"

target:
  connection: ${env:SQLFLOW_CONN_PRE}
  schema: pre
  table: Vendor_Trips

schema:
  evolve: widen

load:
  mode: append

incremental:
  dateColumn: FileDate_DW
  overlapDays: 0

transform:
  generateView: true
  columns:
    - { name: tripId,     expr: "CAST(@ColName AS bigint)" }
    - { name: startedAt,  expr: "COALESCE(TRY_CONVERT(datetime,@ColName,127),TRY_CONVERT(datetime,@ColName,21))" }
    - { name: endedAt,    expr: "COALESCE(TRY_CONVERT(datetime,@ColName,127),TRY_CONVERT(datetime,@ColName,21))" }
    - { name: riderId,    expr: "CAST(@ColName AS varchar(64))" }
    - { name: distanceM,  expr: "CASE WHEN LEN(@ColName) > 0 THEN CAST(@ColName AS int) ELSE NULL END" }
    - { name: FileDate_DW,     expr: "CAST(@ColName as decimal(14,0))" }
    - { name: FileName_DW,     expr: "CAST(@ColName as varchar(255))" }
    - { name: DataSet_DW,      expr: "CAST(@ColName as decimal(14,0))" }
```

Do not guess `includePaths`. Read them off the landed files:

```bash
sqlflow paths  "https://account.dfs.core.windows.net/fs/raw/vendor/api/trips/history/" -r
sqlflow discover vendor/vendor_trips_01_jsn.yaml
sqlflow plan     vendor/vendor_trips_01_jsn.yaml
sqlflow run      vendor/vendor_trips_01_jsn.yaml
```

## Stage 2: vendor_trips_02_ing.yaml

```yaml
flowType: ing
name: vendor_trips_02_ing
batch: trips
schedule: vendor_daily

connections:
  pre: ${env:SQLFLOW_CONN_PRE}
  ods: ${env:SQLFLOW_CONN_ODS}

source:
  server: pre
  object: "[StagingDb].[pre].[v_Vendor_Trips]"

target:
  server: ods
  object: "[WarehouseDb].[arc].[Vendor_Trips]"
  identityColumn: VendorTripsPK

load:
  keyColumns: [tripId]

incremental:
  columns: [FileDate_DW]
  overlapDays: 7

schema:
  sync: true

systemColumns:
  insertedDate: false
  updatedDate: true
```

```bash
sqlflow validate vendor/vendor_trips_02_ing.yaml
sqlflow run      vendor/vendor_trips_02_ing.yaml
```

## Verify the chain before scheduling it

```bash
# all three flows present, bound, and in waves 0/1/2
sqlflow lineage vendor/ --strict

# why is stage 2 where it is?
sqlflow lineage vendor/ --explain vendor_trips_02_ing

# project the estate into the shadow catalog so the control plane can schedule it
sqlflow db sync vendor/
```

Expected wave assignment:

```
wave 0  vendor_00_api
wave 1  vendor_trips_01_jsn
wave 2  vendor_trips_02_ing
```

If stage 1 lands in wave 0, the lake paths did not bind: compare
`landing.target` + static prefix against `source.location`.

## Adding a second dataset to the same source

Add the endpoint to the acquisition as an `items[]` entry with its own landing path, then add a
`_01_jsn` / `_02_ing` pair with `batch: <dataset>`. The acquisition stays one flow.

```yaml
# vendor_00_api.yaml, multi-item form: source holds only the shared envelope
items:
  - name: trips
    request: { method: GET, path: /v1/trips }
    landing:
      target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/trips
      pathTemplate: "history/{yyyy}/{MM}/vendor_trips_{yyyyMMdd}"
      format: json
  - name: stations
    request: { method: GET, path: /v1/stations }
    landing:
      target: abfss://fs@account.dfs.core.windows.net/raw/vendor/api/stations
      pathTemplate: "history/{yyyy}/vendor_stations_{yyyyMMdd}"
      format: json
```

In the multi-item form `request`, `pagination`, `iterate` and `landing` move under each item, and a
top-level `landing` or `incremental` becomes a validation error.
