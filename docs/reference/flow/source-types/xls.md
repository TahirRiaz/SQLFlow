---
id: source-type-xls
title: "Excel source (source.type: xls | xlsx)"
type: source-type
summary: "Read Excel workbooks (.xls/.xlsx) into SQL Server: sheet selection by name or index, cell ranges, headers, and the shared file-ingestion pipeline."
keywords:
  - excel
  - xls
  - xlsx
  - sheetname
  - sheetrange
  - usesheetindex
  - exceldatareader
  - workbook
yamlPath: "source.options (type: xls|xlsx)"
related:
  - flow-source
  - source-type-csv
  - concept-file-source-pipeline
sourceRefs:
  - src/SqlFlow.Sources/XlsSourceReader.cs
  - src/SqlFlow.Core/Model/PreIngestionXls.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Core/Model/FileDateSpec.cs
  - src/SqlFlow.Sources/FileDateFilter.cs
  - src/SqlFlow.Core/Model/SourceOptions.cs
  - src/SqlFlow.Core/Ingestion/LegacyColumnCleanup.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - tests/SqlFlow.Core.Tests/XlsSourceReaderTests.cs
---

# Excel source (source.type: xls | xlsx)

`source.type: xls` or `source.type: xlsx` (matched case-insensitively) selects `XlsSourceReader` (src/SqlFlow.Sources/XlsSourceReader.cs), which reads Excel workbooks with the ExcelDataReader library. The Excel-specific options pick a worksheet (`sheetName`, `useSheetIndex`), bound the cells (`sheetRange`), and name the columns (`header`). Everything else, file selection, schema union across files, provenance and key columns, streaming into the bulk loader, and the post-load file lifecycle, comes from the shared file-ingestion pipeline in src/SqlFlow.Sources/FileSourceReaderBase.cs, so an Excel flow behaves exactly like a CSV flow apart from how cells are read.

The reader's metadata is bound by `PreIngestionXls.FromSource` (src/SqlFlow.Core/Model/PreIngestionXls.cs), a port of the legacy `flw.PreIngestionXLS` table. Its flow type discriminator is `xls`. Option keys are matched case-insensitively (the loader builds the option map with an ordinal-ignore-case comparer).

## Minimal example

```yaml
name: Xlsx_Orders
source:
  type: xlsx
  location: ./data/orders.xlsx
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Xlsx_Orders
schema:
  evolve: widen
load:
  mode: append
```

## Options reference

All options live under `source.options` and are string-valued. Booleans must parse with `bool.TryParse` (`true`/`false`); a non-parsable value silently falls back to the default. Integers parse with invariant culture.

### Excel-specific

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `sheetName` | string | empty | Worksheet to read, matched case-insensitively by name. Empty reads the first worksheet. |
| `useSheetIndex` | bool | `false` | Interpret `sheetName` as a 0-based sheet index instead of a name. |
| `sheetRange` | string | empty | Cell range to read, e.g. `A1:D100`. Empty reads the whole used sheet. A single cell like `B3` opens an unbounded range from that cell to the end of the sheet. |
| `header` | bool | `true` | First in-range row contains column headers. Alias: `firstRowHasHeader` (`header` wins when both are set). |
| `expectedColumnCount` | int | `0` | Bound into the flow metadata but not enforced by the Excel reader. |

### Header and column naming

With `header: true`, column names come from the first in-range row; a missing or empty header cell yields a generated name `Column1`, `Column2`, ... (1-based, in range order). With `header: false`, every column gets a generated name and the first row is loaded as data. All header-derived names then pass through the legacy column-name cleanup (`CleanColumnNames`), the same index-preserving cleanup every file source applies, so V3 produces the same target column names a legacy install did.

A source column whose (cleaned) name collides with an enabled provenance or key column (for example a header literally named `FileName_DW`) fails with an explicit error rather than being silently overwritten; rename the source column or disable that system column.

### Sheet selection

`sheetName` empty: the first worksheet is read.

`sheetName` set, `useSheetIndex: false`: worksheets are scanned in order and matched case-insensitively. No match fails with:

```text
Sheet '<name>' not found in '<file>'.
```

`useSheetIndex: true`: `sheetName` must be a non-negative integer, otherwise:

```text
Invalid sheet index '<value>'.
```

An index past the last sheet fails with:

```text
Sheet index <n> not found in '<file>'.
```

### sheetRange

`sheetRange` bounds both rows and columns. `B2:C4` reads columns B..C, rows 2..4; the header row (when `header: true`) is the first row of the range, so `B2:C4` yields one header row and two data rows. The two corners may be given in either order; the parser normalizes to min/max. A single cell (`B2`) fixes the top-left corner and reads to the end of the sheet. Rows past the range's last row are not read; columns are clipped to the intersection of the range and the sheet's actual width.

A corner that is not a letters-then-digits cell reference fails with:

```text
Invalid cell reference '<cell>' in sheetRange (use e.g. A1:D100).
```

### File selection and discovery

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `srcPath` | string | empty | Fallback for `source.location` when `location` is omitted. A missing path fails with `Source requires a 'location' (file path, folder, or URI).` |
| `srcFile` | string | `*.xlsx` | File glob applied when `location` is a folder. The default only matches `.xlsx`; to ingest legacy `.xls` files from a folder, set `srcFile: "*.xls"` (or a union-covering pattern). |
| `srcPathMask` | string | empty | Regular expression (case-insensitive) that a file's path must match. An invalid regex fails with `Invalid 'srcPathMask' regular expression '<mask>': <reason>`. |
| `searchSubDirectories` | bool | `false` | Recurse into subdirectories of `location`. |
| `initFromFileDate` | string | empty | Inclusive lower bound of the file-date window. Accepts `yyyy-MM-dd`, `yyyyMMdd`, `yyyy-MM-dd HH:mm:ss`, `yyyy-MM-ddTHH:mm:ss`, `yyyyMMddHHmmss`, or anything invariant-parseable. |
| `initToFileDate` | string | empty | Inclusive upper bound of the file-date window; same formats. |
| `incrementalAfterDate` | string | empty | Exclusive lower bound on the file date; normally injected by the engine from the incremental watermark rather than authored. |
| `fileDate.from` | string | `modified` | Where the file date is read from: `modified` (timestamp), `path`, or `name`. With `path` or `name`, exactly one of `fileDate.hive` or `fileDate.pattern` is required. |
| `fileDate.hive` | bool | `false` | Read the file date from Hive `key=value` partition tokens in the path. |
| `fileDate.pattern` | string | empty | Regex over the path/name whose `year`/`month`/`day`/`hour` groups yield the file date. |

Matched files are processed in ascending modified-date order (name as tiebreaker). When nothing matches, the run fails with `No files under '<path>' matched the filters (...)` listing every active filter. The same selection applies to every file format; see the shared pipeline page for details.

### File lifecycle (post-load)

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `copyToPath` | string | empty | Copy each ingested file to this path after the load. |
| `zipToPath` | string | empty | Zip each ingested file to this path after the load. |
| `srcDeleteIngested` | bool | `false` | Delete each ingested file after the load. |
| `srcDeleteAtPath` | bool | `false` | Delete each ingested file at its source path after the load. |
| `readAhead` | `1` | How many source files are kept open at once (1 to 32). Files are still read one at a time in file order; a higher value only opens and downloads the following files ahead of their turn, which removes the per-file latency that dominates a source of many small files. The reader buffers a non-seekable stream in full, so each extra open file multiplies the peak. An out-of-range value fails with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` |

### Provenance columns (default on)

| Key | Type | Default | Column added |
| --- | --- | --- | --- |
| `includeFileName` | bool | `true` | `FileName_DW` (varchar(255), the file name; the full path when `showPathWithFileName: true`) |
| `includeFileDate` | bool | `true` | `FileDate_DW` (varchar(255), the file's modified timestamp, UTC) |
| `includeFileRowDate` | bool | `true` | `FileRowDate_DW` (varchar(255), ingestion timestamp, UTC) |
| `includeFileSize` | bool | `true` | `FileSize_DW` (varchar(255)) |
| `includeDataSet` | bool | `true` | `DataSet_DW` (varchar(255), a date detected in the file name, else the file's modified timestamp, UTC; see [dataSetFromFileName](../../concepts/provenance-and-row-keys.md#dataset_dw-and-datasetfromfilename)) |
| `includeRowNumber` | bool | `true` | `RowNumber_DW` (bigint, 1-based data-row number within the file) |
| `includeFileLineNumber` | bool | `false` | `FileLineNumber` (bigint, 1-based sheet row number of the row) |

The raw landing layer is string-first: the file/date/size provenance columns land as `varchar(255)` text, not as `datetime2` or `bigint`. Values are written in the encodings the generated transformation view's casts expect: `yyyyMMddHHmmss` for `FileDate_DW` and `DataSet_DW`, `yyyy-MM-dd HH:mm:ss` for `FileRowDate_DW`, and digit strings for `FileSize_DW`. The transformation view applies the real types downstream. Only `RowNumber_DW` and `FileLineNumber` land as `bigint`.
| `showPathWithFileName` | bool | `false` | Put the full path (not just the name) into `FileName_DW`. |
| `dataSetFromFileName` | bool | `true` | Derive `DataSet_DW` from a date in the file name (fallback: modified date). `false` makes `DataSet_DW` equal `FileDate_DW`. |
| `dataSetFormats` | string | none | Extra .NET date formats for `DataSet_DW` detection, comma- or pipe-separated, tried before the built-ins. |
| `dataSetDayFirst` | bool | (inferred) | Hard-lock day-first (`true`) or month-first (`false`) for ambiguous same-length dates; inferred from the file set when unset. |

### Synthetic keys

| Key | Type | Default | Description |
| --- | --- | --- | --- |
| `includeHashKey` | bool | `false` | Add a `HashKey_DW` column (varbinary sized to the digest, e.g. `varbinary(64)` for SHA-512). |
| `hashKeyColumns` | string | empty | Comma-separated source columns hashed into the key, in the order listed; empty hashes every non-generated column. A name with no matching column is dropped; a list where none of the names match fails with `Key columns '<list>' did not match any column in the source.` |
| `hashKeyType` | string | `SHA2_512` | Hash algorithm; `SHA2_256`/`SHA256`, `SHA1`, and `MD5` are recognized, anything else falls back to SHA-512. |
| `includeConcatKey` | bool | `false` | Add a `ConcatKey_DW` column (String, max length 4000). |
| `concatKeyColumns` | string | empty | Comma-separated source columns concatenated into the key; empty uses every non-generated column. |
| `concatKeySeparator` | string | `\|` | Separator between concatenated values. |

### Identity, target, schema, hooks, orchestration

These keys are bound into the flow metadata by `PreIngestionXls.FromSource`, identically to the CSV source:

| Key | Type | Default |
| --- | --- | --- |
| `flowId` | GUID | empty GUID |
| `sysAlias` | string | `default` |
| `servicePrincipalAlias` | string | empty |
| `trgServer` | string | empty |
| `trgDBSchTbl` | string | empty |
| `trgDesiredIndex` | string | empty |
| `syncSchema` | bool | `true` |
| `defaultColDataType` | string | empty |
| `fetchDataTypes` | bool | `false` |
| `preFilter` | string | empty |
| `preProcessOnTrg` | string | empty |
| `postProcessOnTrg` | string | empty |
| `preInvokeAlias` | string | empty |
| `onErrorResume` | bool | `true` |
| `deactivateFromBatch` | bool | `false` |
| `enableEventExecution` | bool | `false` |
| `noOfThreads` | int | `4` |

## Type behavior

Excel is not treated as a self-describing format: every source column is declared as a nullable string, and typing is left to the downstream flow (the flow's default column type, `defaultColDataType`, or the type-inference tooling). Cell values are rendered to strings deterministically:

| Excel cell | Rendered value |
| --- | --- |
| Empty cell | NULL |
| Text | the string as-is |
| Number (Excel stores numbers as double) | invariant-culture string, e.g. `7`, `9.5` |
| Date/time | `yyyy-MM-dd HH:mm:ss` |
| Boolean | `True` / `False` |
| Anything else | invariant-culture `Convert.ToString` |

An empty-string cell also loads as NULL. Rows shorter than the header are NULL-filled on the trailing columns.

When a folder contains multiple workbooks, the reader unions the columns across all matched files (first-seen order); rows from a file that lacks a unioned column get NULL there, the same schema-union behavior as CSV.

## Streams and encodings

ExcelDataReader needs random access (xlsx is a zip, xls is BIFF), so a non-seekable stream (a cloud file store) is fully buffered into memory before parsing; very large workbooks on remote stores cost their full size in memory. `CodePagesEncodingProvider` is registered so legacy `.xls` files using non-Unicode code pages open correctly.

## Full example

A monthly finance workbook: a named sheet, data starting at B2, archived after load.

```yaml
name: Xlsx_Finance_Monthly
source:
  type: xlsx
  location: ./data/finance
  options:
    srcFile: "*.xlsx"
    searchSubDirectories: "true"
    sheetName: Summary
    sheetRange: B2:H500
    header: "true"
    includeFileLineNumber: "true"
    copyToPath: ./archive/finance
    srcDeleteIngested: "true"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Xlsx_Finance_Monthly
schema:
  evolve: widen
load:
  mode: append
```

Selecting a sheet by position instead of name:

```yaml
source:
  type: xlsx
  location: ./data/report.xlsx
  options:
    sheetName: "1"          # the second worksheet
    useSheetIndex: "true"
```

Run it like any other flow:

```bash
sqlflow run ./flows/xlsx-finance-monthly.flow.yaml --json
```

## See also

- [source section reference](../source.md)
- [CSV source](./csv.md)
- [The shared file-source pipeline](../../concepts/file-source-pipeline.md)
