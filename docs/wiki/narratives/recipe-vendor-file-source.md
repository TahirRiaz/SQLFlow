---
id: wiki-recipe-vendor-file-source
title: "Recipe: vendor files into the warehouse (sftp or cpy, then csv/xls, then ing)"
type: narrative
summary: "Pulling an SFTP drop or bridging another lake with cpy, the output block lineage needs, and one file feeding several tables."
keywords:
  - recipe
  - sftp
  - cpy
  - vendor drop
  - xls
  - csv
  - output lineage
  - fan-out to tables
sourceRefs:
  - src/SqlFlow.Sftp/SftpEngine.cs
  - src/SqlFlow.Copy/CopyEngine.cs
  - src/SqlFlow.Sources/CsvSourceReader.cs
referenceRefs:
  - flow-sftp
  - flow-cpy
  - source-type-xls
  - source-type-csv
  - concept-file-discovery-and-lifecycle
related:
  - wiki-chaining-flows-through-the-lake
  - wiki-recipe-inspect-and-debug
  - wiki-file-ingestion-shaping
  - wiki-pattern-catalog
updated: 2026-09-10
---

# Recipe: vendor files into the warehouse

Two ways files arrive, one way they leave. `sftp` pulls a vendor drop; `cpy` bridges from another
store. Both write a lake path, and from there the chain is identical to the API recipe.

## A: SFTP drop

```yaml
# partner/partner_00_sftp.yaml
flowType: sftp
name: partner_00_sftp
batch: sftp
schedule: partner_daily
direction: download

server:
  host: sftp.partner.example
  port: 22
  username: svcaccount
  privateKeyRef: ${keyvault:my-vault/partner-sftp-key}

remotePath: "."
pattern: "partner_export*.xlsx"
recursive: false
modifiedWithinDays: 4

local: https://account.dfs.core.windows.net/fs/raw/partner/history
preserveStructure: false
skipUnchanged: true

# REQUIRED for lineage. Without it the graph has a hole where the data enters.
output:
  location: https://account.dfs.core.windows.net/fs/raw/partner/history/
  srcFile: "partner_export*.xlsx"
```

`username` and `host` are coordinates, not secrets, so they are literal. Only key material takes a
`${...}` reference. Use `passwordRef` instead of `privateKeyRef` for password auth, and
`passphraseRef` alongside a protected key.

`modifiedWithinDays` bounds each run to the recent window and makes it self-healing if the vendor
re-touches a file. `skipUnchanged` stops an unchanged re-download from bumping the modified time and
re-triggering everything downstream.

## B: bridging another lake with cpy

One flow, one `items[]` entry per dataset. Each entry chains independently, so a single `cpy` can
feed eight file flows.

```yaml
# region_a/region_a_00_cpy.yaml
flowType: cpy
name: region_a_00_cpy
batch: copy
operation: copy
schedule: region_a_daily

options:
  overwrite: true
  preserveStructure: true
  skipUnchanged: true

items:
  - source: { location: abfss://fs@oldaccount.dfs.core.windows.net/raw/region_a/history/Calls,
              pattern: "*.csv", recursive: true, modifiedWithinDays: 7 }
    target: { location: abfss://fs@account.dfs.core.windows.net/raw/region_a/history/Calls }
  - source: { location: abfss://fs@oldaccount.dfs.core.windows.net/raw/region_a/history/Line,
              pattern: "*.csv", recursive: true, modifiedWithinDays: 7 }
    target: { location: abfss://fs@account.dfs.core.windows.net/raw/region_a/history/Line }
```

`cpy` needs no `output` block: its `items[].target.location` is the declared output.

**Copy the files to the SAME paths the pre flows already read.** That is what makes the copy bind as
wave 0 ahead of the existing chain, instead of creating a parallel set of paths nothing consumes.

A `cpy` can also read another `cpy`'s target, which is how a staged migration works. Same rule: the
downstream `items[].source.location` equals the upstream `items[].target.location`.

## Stage 1: the file flow

```yaml
# partner/partner_events_01_xls.yaml
name: partner_events_01_xls
batch: events
schedule: partner_daily

source:
  type: xls
  location: https://account.dfs.core.windows.net/fs/raw/partner/history/
  options:
    srcFile: "partner_export-????????.xlsx"
    searchSubDirectories: "true"
    firstRowHasHeader: "true"
    includeFileLineNumber: "true"
    includeRowNumber: "false"

target:
  connection: ${env:SQLFLOW_CONN_PRE}
  schema: pre
  table: Partner_Events

schema:
  evolve: widen
load:
  mode: append
transform:
  generateView: true
  columns:
    - { name: LocationCode,  expr: "CAST(@ColName AS varchar(100))" }
    - { name: EventDate,        expr: "TRY_CONVERT(date, @ColName, 23)" }
    - { name: EventCount, expr: "CASE WHEN LEN(@ColName) > 0 THEN CAST(@ColName AS int) ELSE NULL END" }
```

A `?` in `srcFile` matches one character, so `partner_export-????????.xlsx` pins an 8-digit date stamp and
will not also match `partner_export-backup.xlsx`.

CSV instead of XLSX, with a dialect:

```yaml
source:
  type: csv
  location: https://account.dfs.core.windows.net/fs/raw/vendor/history/
  options:
    srcFile: "*.csv"
    searchSubDirectories: "true"
    firstRowHasHeader: "true"
    delimiter: ";"
    textQualifier: "\""
    srcEncoding: "windows-1252"
    includeFileLineNumber: "true"
```

A tab delimiter is written `delimiter: "\\t"`. Header spaces become underscores in column names.

## One file, several tables

Point several file flows at the same lake path with different `includePaths` or `rootPath`. Each
gets its own `pre` table, its own view, and its own `ing` flow. Lineage binds all of them to the one
producer.

```yaml
# partner_header_01_xml   -> pre.Partner_Header
source: { type: xml, location: .../raw/partner/history/, options: { rootPath: "$.batch" } }
```
```yaml
# partner_lines_01_xml   -> pre.Partner_Lines
source: { type: xml, location: .../raw/partner/history/, options: { rootPath: "$.fees" } }
```

## Stage 2 and verification

```yaml
flowType: ing
name: partner_events_02_ing
batch: events
schedule: partner_daily
connections:
  pre: ${env:SQLFLOW_CONN_PRE}
  ods: ${env:SQLFLOW_CONN_ODS}
source: { server: pre, object: "[StagingDb].[pre].[v_Partner_Events]" }
target: { server: ods, object: "[WarehouseDb].[arc].[Partner_Events]", identityColumn: PartnerEventsPK }
load:
  keyColumns: [DataSet_DW, FileLineNumber_DW]
incremental:
  columns: [FileDate_DW]
  overlapDays: 7
schema: { sync: true }
```

```bash
sqlflow validate partner/partner_00_sftp.yaml
sqlflow run      partner/partner_00_sftp.yaml
sqlflow run      partner/partner_events_01_xls.yaml
sqlflow run      partner/partner_events_02_ing.yaml
sqlflow lineage  partner/ --strict
```

**A file-provenance merge key such as `(DataSet_DW, FileLineNumber_DW)` makes the row count track
files loaded, not facts.** That is correct for a raw per-line archive, and wrong for anything a
consumer will aggregate over: a rolling-window export restates the same days every run. Put
aggregate reporting on a table keyed by the business grain instead. See
[upsert-and-history](../patterns/upsert-and-history.md).
