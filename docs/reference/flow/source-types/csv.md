---
id: source-type-csv
title: "CSV source (source.type: csv)"
type: source-type
summary: "Options for delimited and fixed-width text sources: delimiter, qualifier, header, encoding, row shaping, provenance columns, and synthetic row keys."
keywords:
  - csv
  - delimiter
  - textqualifier
  - fixed-width
  - header
  - columnwidths
  - encoding
  - hashkey
  - provenance
yamlPath: source.options
related:
  - flow-source
  - concept-file-source-pipeline
  - concept-file-discovery-and-lifecycle
  - concept-provenance-and-row-keys
sourceRefs:
  - src/SqlFlow.Sources/CsvSourceReader.cs
  - src/SqlFlow.Core/Model/PreIngestionCsv.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Sources/FileDateFilter.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
---

# CSV source (source.type: csv)

`source.type: csv` reads delimited or fixed-width text files. Parsing is done by the GenericParsing library's `GenericParser`, the same library legacy SQLFlow used, so parsing semantics match a legacy install. The type match is case insensitive (`csv`, `CSV`, and `Csv` all resolve to `CsvSourceReader` in src/SqlFlow.Sources/CsvSourceReader.cs).

Only column and row parsing is CSV specific. File selection, schema evolution across files, provenance column injection, synthetic row keys, streaming, and the post-load file lifecycle come from the shared file pipeline (`FileSourceReaderBase`), which every file format shares.

Option values are strings in YAML; booleans and integers are written as quoted strings (for example `header: "false"`, `maxRows: "100"`). Option binding is defined by `PreIngestionCsv.FromSource` in src/SqlFlow.Core/Model/PreIngestionCsv.cs.

## Minimal example

```yaml
name: Csv_Basic
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_Basic
schema:
  evolve: widen
load:
  mode: append
```

Run or preview it with the CLI:

```bash
dotnet run --project src/SqlFlow.Cli -- plan samples/csv/csv-basic.flow.yaml
dotnet run --project src/SqlFlow.Cli -- run samples/csv/csv-basic.flow.yaml
```

## Options reference

### Parsing

| Option | Alias | Default | Description |
|---|---|---|---|
| `delimiter` | `columnDelimiter` | `,` | Column delimiter. `\t` (or a literal tab) selects tab. Only the first character is used. Ignored when `columnWidths` is set. |
| `textQualifier` | `qualifier` | `"` | Text qualifier enclosing string values. First character only. |
| `escapeCharacter` | | none | In-field escape character. Empty or the string `0` means none. First character only. |
| `commentCharacter` | | none | Lines starting with this character are skipped as comments. First character only. |
| `columnWidths` | | none | Comma-separated integer widths for fixed-width files. When set, the delimiter is not used at all. |
| `header` | `firstRowHasHeader` | `true` | First row contains column names. With `false` (or an empty header cell) columns are auto-named `Column1..ColumnN` (1-based). |
| `expectedColumnCount` | | `0` | Expected number of columns; rows with a different count fail parsing. `0` disables the check. |
| `firstRowSetsExpectedColumnCount` | | `false` | Lock the expected column count from the first row. |
| `encoding` | `srcEncoding` | UTF8 | One of `UTF8`/`UTF-8`, `ASCII`, `UNICODE`/`UTF16`/`UTF-16`, `UTF32`/`UTF-32` (case insensitive). Any other value silently falls back to UTF8. A byte order mark in the file overrides the configured encoding (the stream reader detects BOMs). |
| `maxBufferSize` | | `1024` | Parser read buffer in bytes; values below 1024 are floored to 1024. |

### Row shaping

| Option | Alias | Default | Description |
|---|---|---|---|
| `skipStartingDataRows` | | `0` | Data rows to skip after the header, per file. |
| `skipEndingDataRows` | | `0` | Trailing rows to drop per file (footers, totals lines). Implemented with a look-ahead queue in the shared pipeline. |
| `trim` | `trimResults` | `false` | Trim surrounding whitespace from every value. |
| `stripControlChars` | | `false` | Remove non-printable control characters from values. |
| `maxRows` | | `0` | Cap on the total number of rows loaded across all files; `0` loads everything. |

### File selection and lifecycle

| Option | Default | Description |
|---|---|---|
| `location` (or `srcPath` in options) | required | File path or folder. `source.location` wins; the run fails with `Source requires a 'location' (file path, folder, or URI).` when both are absent. |
| `srcFile` | `*.csv` | File-name glob used to enumerate candidate files when the location is a folder. |
| `srcPathMask` | none | Case-insensitive regular expression applied to each candidate file's full path (folder and name). An invalid pattern fails with `Invalid 'srcPathMask' regular expression '<mask>': <cause>`. |
| `searchSubDirectories` | `false` | Recurse into sub-folders of the location. |
| `initFromFileDate` | none | Earliest file date to include (inclusive). Accepts `yyyy-MM-dd`, `yyyyMMdd`, `yyyy-MM-dd HH:mm:ss`, `yyyy-MM-ddTHH:mm:ss`, `yyyyMMddHHmmss`, or anything invariant-culture parseable; otherwise fails with `Invalid 'initFromFileDate' value '<value>'. Use yyyy-MM-dd or a full timestamp.` |
| `initToFileDate` | none | Latest file date to include (inclusive). Same formats as above. |
| `copyToPath` | none | Copy each ingested file here after the load completes. |
| `zipToPath` | none | Compress each ingested file to this path after the load completes. |
| `srcDeleteIngested` | `false` | Delete each source file after ingestion. |
| `srcDeleteAtPath` | `false` | Delete each source file at the source path after ingestion (either delete flag triggers the delete). |
| `readAhead` | `4` | How many source files are kept open at once (1 to 32). Files are still read one at a time in file order; a higher value only opens and downloads the following files ahead of their turn, which removes the per-file latency that dominates a source of many small files. CSV streams each file line by line, so an open file costs little. An out-of-range value fails with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` |

The `fileDate.*` option group (for example `fileDate.from: path` with `fileDate.hive: "true"`) redirects where the file's business date is read from (path tokens or file name instead of the modified timestamp); see [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md) for the full contract.

Files are processed in ascending modified-date order, ties broken by name. When no file survives the filters, the run fails with `No files under '<path>' matched the filters (...)` listing the active filters (pattern, path mask, file date source, date window, incremental watermark).

`incrementalAfterDate` is not meant to be authored directly: when the flow has a file-date-based `incremental` watermark, the engine resolves it against the target and injects `incrementalAfterDate` into `source.options` before files are listed (src/SqlFlow.Core/Engine/FlowRunner.cs), so only files modified strictly after that watermark are read.

### Provenance columns

All toggles below default to `true` except `includeFileLineNumber` and `showPathWithFileName`. Generated columns are appended after the source columns with their real types.

| Option | Column | Type | Value |
|---|---|---|---|
| `includeFileName` | `FileName_DW` | varchar(255) | File name; the full path when `showPathWithFileName: "true"`. |
| `includeFileDate` | `FileDate_DW` | varchar(255) | Source file modified date (UTC). |
| `includeFileRowDate` | `FileRowDate_DW` | varchar(255) | Ingest timestamp (UTC). |
| `includeFileSize` | `FileSize_DW` | varchar(255) | File size in bytes. |
| `includeDataSet` | `DataSet_DW` | varchar(255) | Dataset date: a date detected in the file name, else the file modified date (UTC). See [dataSetFromFileName](../../concepts/provenance-and-row-keys.md#dataset_dw-and-datasetfromfilename). |
| `includeRowNumber` | `RowNumber_DW` | bigint | Data row number within each file. |
| `includeFileLineNumber` | `FileLineNumber` | bigint | Physical line number in the source file. Default `false`. |

The raw landing layer is string-first: the file/date/size provenance columns land as `varchar(255)` text, not as `datetime2` or `bigint`. Values are written in the encodings the generated transformation view's casts expect: `yyyyMMddHHmmss` for `FileDate_DW` and `DataSet_DW`, `yyyy-MM-dd HH:mm:ss` for `FileRowDate_DW`, and digit strings for `FileSize_DW`. The transformation view applies the real types downstream. Only `RowNumber_DW` and `FileLineNumber` land as `bigint` in the raw layer.
| `showPathWithFileName` | | | Store the full path instead of just the name in `FileName_DW`. Default `false`. |
| `dataSetFromFileName` | | | Derive `DataSet_DW` from a date in the file name (fallback: modified date). Default `true`; `false` makes `DataSet_DW` equal `FileDate_DW`. |
| `dataSetFormats` | | | Extra .NET date formats for `DataSet_DW` detection, comma- or pipe-separated, tried before the built-ins. |
| `dataSetDayFirst` | | | Hard-lock the day-first (`true`) or month-first (`false`) reading of an ambiguous same-length date; inferred from the file set when unset. |

A source column whose name collides with an enabled provenance or key column fails the flow with an error telling you to rename the source column or disable that system column; a disabled toggle leaves a same-named source column alone.

### Synthetic row keys

| Option | Default | Description |
|---|---|---|
| `includeHashKey` | `false` | Inject `HashKey_DW`, a per-row hash computed at ingestion time. Stored as `varbinary(N)` where N is the digest length (64 for SHA2_512, 32 for SHA2_256, 20 for SHA1, 16 for MD5). Not nullable. |
| `hashKeyColumns` | all source columns | Comma-separated columns feeding the hash. Empty means every source column, excluding the generated provenance/key columns. Order is significant and honoured as listed. A listed name that matches no column is dropped silently; if none of the listed names match, the flow fails with `Key columns '<list>' did not match any column in the source.` |
| `hashKeyType` | `SHA2_512` | `SHA2_512`, `SHA2_256`, `SHA1`, or `MD5` (underscores/hyphens and case are ignored, so `SHA256` also works). Unrecognized values fall back to SHA2_512. |
| `includeConcatKey` | `false` | Inject `ConcatKey_DW`, a readable composite key built by joining the chosen columns. String, max length 4000, not nullable. |
| `concatKeyColumns` | all source columns | Comma-separated columns to concatenate; same resolution rules as `hashKeyColumns`. |
| `concatKeySeparator` | `\|` | Separator placed between concatenated values. |

Hash input is length-prefixed per value (NULL is distinguished from empty), so column-boundary ambiguity cannot produce colliding hashes for different rows.

### Schema and typing

| Option | Default | Description |
|---|---|---|
| `defaultColDataType` | none | SQL type for raw CSV columns when `schema.defaultColumnType` is not set in the flow; when neither is set the default is `varchar(255)`. Resolution order is `schema.defaultColumnType`, then this option, then `varchar(255)` (src/SqlFlow.Yaml/YamlFlowLoader.cs). |
| `sysAlias` | `default` | Alias identifying the source system. |

### Accepted but inert on this path

These options are bound into the CSV metadata record but the current file ingestion path does not act on them:

- `skipEmptyRows` (default `true`): parsed but never forwarded to `GenericParser`, so setting it has no effect on parsing.
- `syncSchema`, `trgDBSchTbl`, `fullLoad`: bound onto the CSV metadata record but not read by the file-ingestion path; carried as metadata only.
- `flowId`: not meant to be authored. The YAML loader injects it into every source's `options` from the flow's `name` (src/SqlFlow.Yaml/YamlFlowLoader.cs), overwriting any value placed under this key, and the file-ingestion path does not read it back out.

## Type inference

Every CSV cell is read as a string; an empty string becomes SQL NULL. Raw columns are created with the resolved default column type (`varchar(255)` unless overridden as described above); typing beyond that is left to the downstream transform and type-inference steps. To preserve non-Latin Unicode end to end, set an `nvarchar` default column type in addition to the `encoding` option: reading the encoding is separate from storing it.

Header-derived column names go through the legacy column-name cleanup, so a V3 run produces the same target column names a legacy install did. When the same column appears in several files, the schemas are unioned; missing columns are NULL-filled per file.

## Error and edge-case behavior

- A parse failure raises `SqlFlowException`: `Parse error in '<file>' near file line N: <cause>`.
- A header-only file (a header line with no data rows) is a valid empty dataset: the reader re-reads the first line as data to recover the column names.
- A file whose final line has no trailing newline can make the parser surface a synthetic empty end-of-file record; the reader detects and skips it, so no spurious all-NULL row is loaded.

## Examples

Fixed-width file (widths replace the delimiter; `trim` removes the padding), adapted from samples/csv/csv-fixed-width.flow.yaml:

```yaml
name: Csv_FixedWidth
source:
  type: csv
  location: ./data/fixed-width.csv
  options:
    columnWidths: "3,5"
    trim: "true"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_FixedWidth
```

Synthetic row keys for a file with no business key, adapted from samples/csv/csv-row-keys.flow.yaml:

```yaml
name: Csv_RowKeys
source:
  type: csv
  location: ./data/orders.csv
  options:
    includeHashKey: "true"
    hashKeyType: SHA2_512
    hashKeyColumns: OrderId,Customer
    includeConcatKey: "true"
    concatKeyColumns: OrderId,Customer
    concatKeySeparator: "|"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_RowKeys
```

UTF-16 input stored losslessly (encoding plus an nvarchar default type), adapted from samples/csv/csv-encoding-utf16.flow.yaml:

```yaml
name: Csv_Utf16
source:
  type: csv
  location: ./data/encoding-utf16.csv
  options:
    srcEncoding: Unicode
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_Utf16
schema:
  evolve: widen
  defaultColumnType: nvarchar(400)
```

There is one runnable sample per CSV option under samples/csv (matrix in samples/csv/README.md); the same files are executed against a sink database by `SampleFlowIntegrationTests`, so they are guaranteed to work. The `data/control-chars.csv` and `data/encoding-utf16.csv` inputs are generated by the test harness because their bytes cannot be committed as plain text.

## See also

- [source block reference](../source.md)
- [File source pipeline](../../concepts/file-source-pipeline.md)
- [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md)
- [Provenance and row keys](../../concepts/provenance-and-row-keys.md)
