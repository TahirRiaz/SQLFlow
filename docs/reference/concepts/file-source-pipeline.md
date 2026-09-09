---
id: concept-file-source-pipeline
title: "The shared file-ingestion pipeline, source.options parsing, and column-name cleanup"
type: concept
summary: How every file format rides one pipeline (FileSourceReaderBase), how the source.options string bag is parsed, and the always-on legacy column cleanup.
keywords:
  - filesourcereaderbase
  - options bag
  - aliases
  - getbool
  - column cleanup
  - legacy parity
  - manifest
  - coercion
related:
  - flow-source
  - concept-file-discovery-and-lifecycle
  - source-type-csv
  - source-type-parquet
  - flow-incremental
sourceRefs:
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Sources/FileSourceOptions.cs
  - src/SqlFlow.Core/Model/SourceOptions.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Core/Model/PreIngestionCsv.cs
  - src/SqlFlow.Core/Model/PreIngestionXls.cs
  - src/SqlFlow.Core/Model/PreIngestionJsn.cs
  - src/SqlFlow.Core/Model/PreIngestionXml.cs
  - src/SqlFlow.Core/Model/PreIngestionParquet.cs
  - src/SqlFlow.Core/Ingestion/LegacyColumnCleanup.cs
  - src/SqlFlow.Sources/ParquetSourceReader.cs
---

# The shared file-ingestion pipeline, source.options parsing, and column-name cleanup

Every file format SQLFlow reads (CSV, XLS, JSON, XML, Parquet) rides one abstract base class, `FileSourceReaderBase` in src/SqlFlow.Sources/FileSourceReaderBase.cs. A concrete format supplies only the format-specific parts:

- `CanHandle(sourceType)`: whether the reader handles a `source.type` value such as `"csv"` or `"xls"`.
- `DefaultFilePattern`: the glob used when no `srcFile` option is given (for example `*.csv`).
- `ReadOptions(source)`: maps the format's own metadata onto the common `FileSourceOptions` shape.
- `ReadColumnNamesAsync`: a single file's column names (header, generated, or recovered).
- `ReadLinesAsync`: streams a single file's rows, cells in the same order as the column names.
- Optionally `ReadColumnSchemaAsync`: a self-describing format (Parquet) overrides this to declare real CLR and SQL types; the default treats every column as a nullable string and leaves typing to downstream inference.

Everything else is owned by the base and is therefore identical across formats: file selection (glob, path-mask regex, date window, incremental watermark), cross-file schema union, provenance and key column injection, row streaming into the bulk loader, and the post-load file lifecycle (copy, zip, delete). This is the Single Code Path principle applied to file ingestion: a fix to file selection or coercion applies to every format at once.

## How the pipeline works

### File selection

`ResolveAsync` (src/SqlFlow.Sources/FileSourceReaderBase.cs) resolves the files a run reads:

1. A missing or blank `source.location` fails with: `Source requires a 'location' (file path, folder, or URI).`
2. The first registered `IFileStore` whose `CanHandle` accepts the path is chosen; if none does: `No file store handles location '<path>'.`
3. The glob is `options.srcFile` when set, otherwise the format's `DefaultFilePattern`.
4. A `srcPathMask` option compiles to a case-insensitive, culture-invariant regex over the full path; an invalid pattern fails with `Invalid 'srcPathMask' regular expression '<mask>': <reason>`.
5. `initFromFileDate`, `initToFileDate`, and `incrementalAfterDate` parse with the exact formats `yyyy-MM-dd`, `yyyyMMdd`, `yyyy-MM-dd HH:mm:ss`, `yyyy-MM-ddTHH:mm:ss`, `yyyyMMddHHmmss` (then a general invariant parse), assumed UTC; an unparsable value fails with `Invalid '<field>' value '<value>'. Use yyyy-MM-dd or a full timestamp.`
6. The path mask and date window are pushed into the store as a `FileDateFilter` so it can prune whole out-of-window partition folders during the walk instead of listing everything and discarding most of it.

Selected files are processed in deterministic order: modified timestamp ascending (files without one sort first), then name with ordinal comparison.

When no files survive, a `NoSourceFilesException` reports which of three distinct outcomes it was, carried on its `Reason` (src/SqlFlow.Core/SqlFlowException.cs). "The location holds nothing" and "every file is older than the watermark" have different causes and different fixes, so they are never reported as the same thing. The `FileDateFilter` tallies what it examined and why it rejected it during the walk, which classifies the outcome without a second, unpruned listing of the tree:

| Reason | When | Message |
| --- | --- | --- |
| `NoCandidates` | The glob matched nothing anywhere under the location, so no filter was ever reached. An empty folder, or a wrong path or pattern. | `No files under './data' match pattern '*.csv'.` |
| `NoneAfterWatermark` | The watermark is the only date bound and it excluded everything: an incremental flow with nothing new. | `No new files under './data': nothing matching pattern '*.csv' is newer than 2026-06-01 00:00:00Z (examined 128 file(s), pruned 12 out-of-window folder(s)).` |
| `NoneSelected` | Candidates exist but the init window or path mask excluded them (or a window and a watermark are both set, so no single bound can be blamed). | `No files under './data' matched the filters (pattern '*.csv', path mask 'orders_\d{8}', date window [2026-01-01 .. max]); examined 128 file(s).` |

Because a `NoCandidates` result never reached the date test, its message never mentions the watermark: an empty location must not be explained by a filter that excluded nothing. Every non-`NoCandidates` message states what the walk covered, so an empty result carries its evidence and not just its verdict.

### Cross-file schema union

`GetColumnsAsync` reads each selected file's column schema and unions the columns in first-seen order (case-insensitive names). The same column seen in two files keeps its type when both files agree on `Type`, `SqlType`, and `MaxLength` (nullability is widened); any disagreement widens the column to a string with an explicit `nvarchar(max)` SQL type, the safe lossless union. Only a typed format (Parquet) can produce a disagreement; the string formats always agree with themselves.

### Provenance and key columns

The base appends the enabled system columns after the source columns:

| Column | Enabled by default | Type | Value |
|---|---|---|---|
| `FileName_DW` | yes | string (4000) | file name, or full path with `showPathWithFileName: "true"` |
| `FileDate_DW` | yes | datetime | the file's modified timestamp (UTC) |
| `FileRowDate_DW` | yes | datetime | ingestion timestamp (UTC) |
| `FileSize_DW` | yes | bigint | file size in bytes |
| `DataSet_DW` | yes | datetime | the file's modified timestamp (UTC) |
| `RowNumber_DW` | yes | bigint | data row index within the file |
| `FileLineNumber` | no | bigint | physical line number in the file |
| `HashKey_DW` | no | `varbinary(N)` | hash over the key input columns; digest length 64 (SHA2_512, the default), 32 (SHA2_256), 20 (SHA1), 16 (MD5) |
| `ConcatKey_DW` | no | string (4000) | key input columns joined by `concatKeySeparator` (default `\|`) |

A source column that shares the name of an enabled system column is rejected rather than silently overwritten:

```text
A source column named 'FileName_DW' collides with the enabled 'FileName_DW' provenance/key column, whose generated value would overwrite the source data. Rename the source column (columnMappings) or disable that provenance/key column.
```

When the system column is disabled, a source column with that name is an ordinary data column and is never overwritten. Key input columns default to all non-generated source columns; an explicit `hashKeyColumns`/`concatKeyColumns` list that matches nothing fails with `Key columns '<list>' did not match any column in the source.`

### Streaming and the per-file manifest

`OpenAsync` streams rows file by file through a `StreamingDataReader` into the bulk loader at bounded memory. Each file's cells are mapped positionally onto the union schema by column name; `maxRows` caps the total rows across files, and `skipEndingDataRows` holds back the trailing N rows of each file via a sliding window. Both are CSV-only: `PreIngestionCsv` is the only metadata record with `MaxRows`/`SkipEndingDataRows` fields, so the other formats always run with these caps unset (the `FileSourceOptions` default of 0, unbounded). Every read returns a manifest of `ProcessedFile` records, one per file: `Name`, `Path`, `SizeBytes`, `Modified`, `Rows`, `Columns`.

### How many files are open at once (`readAhead`)

Files are always consumed one at a time, in file order, on both passes: the schema union and the row stream must be file-ordered, so nothing about the result depends on which read finished first. What `readAhead` controls is how many files are OPENED ahead of the one being read, on the schema pass (`ReadAheadAsync`) and the data pass (the prefetch queue in `StreamRowsAsync`) alike. It buys latency, not parallel parsing: for a source of many small remote files, the per-file round trip is most of the wall clock, and overlapping the opens removes it.

Because the cost is per open file, the default is a property of the format, and the option tunes it per source:

| Format | Default | Why |
|---|---|---|
| csv, json, jsonl, ndjson | `4` | The reader streams the file, so an open file costs a small buffer and one record. |
| xls, xlsx, parquet, prq | `1` | A non-seekable stream is buffered in full to be read (`OpenSeekableAsync`), so each extra open file multiplies the peak. |
| xml | `1` | The reader parses a whole document. |

Values outside 1 to 32 fail with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` A failure or cancellation cancels the reads still in flight and disposes them, so no download or stream leaks, and a file's own error still surfaces at that file's position in the order.

### Cell coercion

`CoerceCell` maps each source cell onto its resolved target column type:

- `null` stays NULL.
- An empty string becomes NULL.
- A non-empty string passes through unchanged.
- A typed cell (Parquet) streams straight into its typed target column, or is rendered as a faithful invariant string when the target column was widened to string by a cross-file type disagreement.

The faithful string renderings (also used for hash and concat key input) are:

| CLR value | Rendering |
|---|---|
| `bool` | `True` / `False` |
| `float`, `double`, `decimal` | invariant culture |
| `DateTime` | `yyyy-MM-dd HH:mm:ss`; a `.fffffff` fraction is appended only when the value has sub-second ticks, with trailing zeros trimmed |
| `DateTimeOffset` | `yyyy-MM-dd HH:mm:sszzz`, with `.fffffff` when sub-second |
| `DateOnly` | `yyyy-MM-dd` |
| `TimeOnly` | `HH:mm:ss` with trimmed sub-second fraction |
| `TimeSpan` | the constant `"c"` format |
| `Guid` | the `"D"` format |
| `byte[]` | Base64 |

### Post-load file lifecycle

After a successful load, `CompleteAsync` re-resolves the same file set and applies, per file: copy to `copyToPath`, zip to `zipToPath`, and delete when `srcDeleteIngested` or `srcDeleteAtPath` is `"true"`. When none of those are configured it does nothing.

## How source.options is parsed

`source.options` is a free-form string-to-string dictionary with case-insensitive keys (src/SqlFlow.Yaml/YamlFlowLoader.cs builds it with `StringComparer.OrdinalIgnoreCase`). All values are YAML strings; booleans must be quoted `"true"`/`"false"`.

The typed accessors in src/SqlFlow.Core/Model/SourceOptions.cs define the parsing semantics every reader shares:

- `GetString(key, fallback)`: returns the fallback when the key is absent or the value is whitespace-only.
- `GetBool(key, fallback)`: `bool.TryParse`; any unparsable value silently falls back to the default.
- `GetInt(key, fallback)`: invariant integer parsing with the same silent fallback.

A wrong-typed value therefore silently becomes the default rather than an error: `maxRows: "ten"` behaves as `maxRows: "0"`.

Loader-level validation (src/SqlFlow.Yaml/YamlFlowLoader.cs):

- `source.type` is required; a missing value fails with `'source.type' is required.` (prefixed with the file path).
- `source.location` is the path or URI. The metadata binders fall back to an `options.srcPath` value only when `location` is absent; `location` takes precedence.

### Aliases

Individual readers accept legacy and modern spellings for the same option, bound in their own `PreIngestion*` metadata record (src/SqlFlow.Core/Model/PreIngestionCsv.cs, PreIngestionXls.cs, PreIngestionXml.cs). These are per-format, not a convention every reader shares: most exist only on the CSV record.

| Modern key | Legacy alias | Default | Formats |
|---|---|---|---|
| `delimiter` | `columnDelimiter` | `,` | CSV |
| `qualifier` | `textQualifier` | `"` | CSV |
| `header` | `firstRowHasHeader` | `true` | CSV, XLS |
| `trim` | `trimResults` | `false` | CSV |
| `encoding` | `srcEncoding` | auto-detect | CSV |
| `rowXPath` | `hierarchyIdentifier` | (empty) | XML |

### Injected and cross-cutting options

- `options.flowId` is not authored: the YAML loader injects the deterministic flow GUID (computed from `name`, see `FlowDefinition.FlowId`) into every source's option bag so the metadata records carry it.
- `options.sysAlias` (default `"default"`) labels the source system.
- `options.defaultColDataType` is bound as the raw-column SQL type override and also feeds `schema.defaultColumnType` when that key is unset; the global default is `varchar(255)`.
- Several legacy-fidelity fields are bound into the format metadata records (PreIngestionJsn, PreIngestionXls, PreIngestionXml, PreIngestionParquet) but are not consumed by the file readers themselves: `noOfThreads`, `onErrorResume`, `preFilter`, `preProcessOnTrg`, `postProcessOnTrg`, `trgServer`, `trgDesiredIndex`. They exist for legacy metadata parity.

## Legacy column-name cleanup

Every header-derived or field-derived column name from any file source is cleaned exactly as legacy SQLFlow's `Shared.cs` did, so a V3 run produces the same target column names as a legacy install; otherwise schema sync would treat every existing column as missing and add renamed duplicates. It is always on, with no flag. The implementation is `LegacyColumnCleanup.CleanFileColumnNames` in src/SqlFlow.Core/Ingestion/LegacyColumnCleanup.cs, invoked by `FileSourceReaderBase.CleanColumnNames`.

The rules:

- The default cleanup regex is `[^a-zA-Z0-9æøåÆØÅ_]` (the legacy `flw.SysCFG.ColCleanupSQLRegExp` value): it keeps ASCII letters, digits, underscore, and the six Norwegian letters; every matched character is replaced with an underscore.
- A cleaned name that collides case-insensitively with an earlier cleaned name gets the column's zero-based ordinal index appended (legacy behavior). If even that collides, further `_N` suffixes disambiguate; legacy would have thrown in that degenerate case, so the divergence only changes behavior where legacy already failed.
- Cleanup is index-preserving: the result list is aligned with the raw names, so the positional row reader stays correct.
- Parquet's self-describing field names route through the same cleanup (src/SqlFlow.Sources/ParquetSourceReader.cs, `ReadColumnSchemaAsync`), index-preserving so the positional read plan stays aligned.
- A column with no header gets the generated name `Column1`, `Column2`, ... (1-based).

Example: the header row `Order Id;Order-Id;Amount (EUR)` cleans to `Order_Id`, `Order_Id1` (collision at zero-based index 1), `Amount__EUR_`.

## Configuration touchpoints

- YAML: `source.type` selects the reader; `source.location` is the path or URI; `source.options.*` carries every reader setting listed above (selection: `srcFile`, `srcPathMask`, `searchSubDirectories`, `initFromFileDate`, `initToFileDate`, `incrementalAfterDate`; lifecycle: `copyToPath`, `zipToPath`, `srcDeleteIngested`, `srcDeleteAtPath`; provenance: `includeFileName`, `includeFileDate`, `includeFileRowDate`, `includeFileSize`, `includeDataSet`, `includeRowNumber`, `includeFileLineNumber`, `showPathWithFileName`; keys: `includeHashKey`, `hashKeyColumns`, `hashKeyType`, `includeConcatKey`, `concatKeyColumns`, `concatKeySeparator`; caps, CSV only: `maxRows`, `skipEndingDataRows`).
- `schema.defaultColumnType` types the untyped string columns when the target is created; `options.defaultColDataType` seeds it when unset.
- CLI: `sqlflow validate <pipeline.yaml>` checks the definition, `sqlflow plan <pipeline.yaml>` shows the SQL a file flow would run, `sqlflow run <pipeline.yaml>` executes it; `sqlflow run --file-pattern <glob>` narrows a file flow to one glob for a single run, and `--full` ignores the watermark (src/SqlFlow.Cli/Program.cs).

## Example

A CSV flow exercising the option bag, string-typed booleans, aliases, and synthetic keys (adapted from samples/csv/csv-row-keys.flow.yaml):

```yaml
name: Csv_RowKeys
source:
  type: csv
  location: ./data/orders.csv
  options:
    delimiter: ";"                 # alias of columnDelimiter
    header: "true"                 # alias of firstRowHasHeader; booleans are YAML strings
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

Run it:

```bash
sqlflow validate samples/csv/csv-row-keys.flow.yaml
sqlflow run samples/csv/csv-row-keys.flow.yaml
```

The target gets the cleaned source columns, the six default provenance columns, plus `HashKey_DW varbinary(64)` and `ConcatKey_DW`.

## See also

- [source](../flow/source.md): the `source:` block reference.
- [csv](../flow/source-types/csv.md): the CSV reader's full option reference.
- [parquet](../flow/source-types/parquet.md): the typed, self-describing format on the same pipeline.
- [incremental](../flow/incremental.md): file-date and row-level watermarks that feed file selection.
