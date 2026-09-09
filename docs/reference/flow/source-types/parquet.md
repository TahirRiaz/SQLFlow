---
id: source-type-parquet
title: "Parquet source (source.type: parquet | prq)"
type: source-type
summary: "Typed Parquet ingestion: file schema maps straight to SQL Server types, nested data becomes JSON, row groups stream and prune on the incremental watermark."
keywords:
  - parquet
  - prq
  - typed columns
  - self-describing
  - type mapping
  - row group
  - nested
  - decimal
  - watermark pruning
  - partitionlist
yamlPath: "source.options (type: parquet|prq)"
related:
  - source-type-duckdb
  - flow-source
  - concept-type-inference
sourceRefs:
  - src/SqlFlow.Sources/ParquetSourceReader.cs
  - src/SqlFlow.Core/Model/PreIngestionParquet.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Core/Model/Watermark.cs
  - src/SqlFlow.Core/Model/FileDateSpec.cs
  - samples/parquet/README.md
  - samples/parquet/parquet-basic.flow.yaml
  - samples/parquet/parquet-folder.flow.yaml
---

# Parquet source (`source.type: parquet | prq`)

Ingests Apache Parquet files into a SQL Server target table. The type token is `parquet` or `prq`, matched case-insensitively (`ParquetSourceReader.CanHandle` in src/SqlFlow.Sources/ParquetSourceReader.cs). The default file glob when `location` is a folder is `*.parquet`.

Parquet is columnar and self-describing: the file's own schema supplies both the column names and their real types. There are no delimiter, header, or sheet options. Unlike the text formats (CSV, XLS, JSON, XML), which declare string columns and leave typing to the downstream inference step, the Parquet reader maps each scalar Parquet type straight to an explicit SQL Server type and streams the typed values to the bulk loader. There is no stringify-then-infer round trip, so an `infer` pass is not needed.

The reader rides the shared file-ingestion pipeline (`FileSourceReaderBase`): file selection (glob, path-mask regex, date window, incremental watermark), dynamic schema evolution (additive column union across files with NULL-fill), provenance column injection, synthetic hash and concatenation keys, bounded-memory streaming, and the post-load file lifecycle (copy, zip, delete).

## Minimal example

```yaml
name: Parquet_Basic
source:
  type: parquet
  location: ./data/orders.parquet
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Parquet_Basic
load:
  mode: truncate-load
```

Adapted from samples/parquet/parquet-basic.flow.yaml. Point `location` at a single `.parquet` file, or at a folder (optionally with `options.srcFile`) to load many files as one stream. The samples folder ships no committed `.parquet` data because Parquet is binary; supply your own files.

## Options reference

All options live under `source.options` and all values are YAML strings. Parquet has no format-specific parsing options; it uses the shared file-selection, lifecycle, provenance, key, and schema set, plus two Parquet-specific checks (`expectedColumnCount`, `partitionList`).

| Option | Type | Default | Description |
| --- | --- | --- | --- |
| `srcFile` | string | `*.parquet` | File-name glob when `location` is a folder. |
| `srcPathMask` | string | none | Regex applied to the file path during discovery. |
| `searchSubDirectories` | bool | `false` | Recurse into sub-folders. |
| `expectedColumnCount` | int | `0` | Fail a file whose leaf-column count differs; `0` disables. |
| `partitionList` | string | none | Partition column list carried for metadata fidelity (Hive-style datasets). Recorded, never used to drive reads, matching the original engine. |
| `initFromFileDate` | string | none | Lower bound of the file date window during discovery. |
| `initToFileDate` | string | none | Upper bound of the file date window during discovery. |
| `fileDate.from` | string | `modified` | Where the file's business date is read from: `modified` (file-system timestamp), `path`, or `name`. Invalid values raise: `Invalid 'fileDate.from' value '<v>'. Use path, name, or modified.` |
| `fileDate.hive` | bool | `false` | With `fileDate.from: path`, parse Hive `key=value` tokens (for example `year=2025/month=03`). Mutually exclusive with `fileDate.pattern`. |
| `fileDate.pattern` | string | none | Regex whose named groups `year`/`month`/`day`/`hour` (or unnamed groups in that order) yield the date. Mutually exclusive with `fileDate.hive`. |
| `copyToPath` | string | none | Copy each ingested file to this path after load. |
| `zipToPath` | string | none | Zip each ingested file to this path after load. |
| `srcDeleteIngested` | bool | `false` | Delete each file after successful ingestion. |
| `srcDeleteAtPath` | bool | `false` | Delete at the source path as part of the lifecycle. |
| `readAhead` | `1` | How many source files are kept open at once (1 to 32). Files are still read one at a time in file order; a higher value only opens and downloads the following files ahead of their turn, which removes the per-file latency that dominates a source of many small files. The reader buffers a non-seekable stream in full, so each extra open file multiplies the peak. An out-of-range value fails with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` |
| `showPathWithFileName` | bool | `false` | Include the full path in the `FileName_DW` value. |
| `includeFileName` | bool | `true` | Inject the `FileName_DW` provenance column. |
| `includeFileDate` | bool | `true` | Inject the `FileDate_DW` provenance column. |
| `includeFileRowDate` | bool | `true` | Inject the `FileRowDate_DW` provenance column. |
| `includeFileSize` | bool | `true` | Inject the `FileSize_DW` provenance column. |
| `includeDataSet` | bool | `true` | Inject the `DataSet_DW` provenance column (a date detected in the file name, else the modified date; see [dataSetFromFileName](../../concepts/provenance-and-row-keys.md#dataset_dw-and-datasetfromfilename)). |
| `includeRowNumber` | bool | `true` | Inject the `RowNumber_DW` provenance column. |
| `dataSetFromFileName` | bool | `true` | Derive `DataSet_DW` from a date in the file name (fallback: modified date). `false` makes `DataSet_DW` equal `FileDate_DW`. |
| `dataSetFormats` | string | none | Extra .NET date formats for `DataSet_DW` detection, comma- or pipe-separated, tried before the built-ins. |
| `dataSetDayFirst` | bool | (inferred) | Hard-lock day-first (`true`) or month-first (`false`) for ambiguous same-length dates; inferred from the file set when unset. |
| `includeFileLineNumber` | bool | `false` | Inject the source row index as the `FileLineNumber` column. |
| `includeHashKey` | bool | `false` | Inject the `HashKey_DW` synthetic hash key. |
| `hashKeyColumns` | string | none | Columns the hash key is computed over. |
| `hashKeyType` | string | `SHA2_512` | Hash algorithm for the hash key. |
| `includeConcatKey` | bool | `false` | Inject the `ConcatKey_DW` readable composite key. |
| `concatKeyColumns` | string | none | Columns concatenated into the composite key. |
| `concatKeySeparator` | string | `\|` | Separator between concatenated key parts. |

Provenance columns default on because the original engine (`flw.PreIngestionPRQ`) always injects the `_DW` columns. A source column whose cleaned name collides with an enabled provenance or key column is rejected with: `A source column named '<name>' collides with the enabled '<name>' provenance/key column, whose generated value would overwrite the source data. Rename the source column (columnMappings) or disable that provenance/key column.`

`PreIngestionParquet.FromSource` (src/SqlFlow.Core/Model/PreIngestionParquet.cs) also parses the wider legacy metadata set (`sysAlias`, `flowId`, `trgServer`, `trgDBSchTbl`, `syncSchema` default true, `fetchDataTypes` default false, `preFilter`, `preProcessOnTrg`, `postProcessOnTrg`, `preInvokeAlias`, `onErrorResume` default true, `noOfThreads` default 1). The flow-type discriminator is `prq`. Of these, only the options in the table above are consumed by the file reader path itself.

### expectedColumnCount

The check counts the file's leaf columns (what Parquet tooling and the original engine count), not the output columns: each collection collapses to one JSON output column and each struct expands to several. A mismatch fails the file with: `Parquet file '<f>' has <N> leaf column(s) but ExpectedColumnCount is <M>.`

## Type mapping (no inference)

Parquet is the one V3 source that does not use the strings-then-infer path. Each Parquet leaf maps to an explicit SQL Server type via `MapClrType` and `MapColumn` in src/SqlFlow.Sources/ParquetSourceReader.cs:

| Parquet / CLR value | SQL Server type |
| --- | --- |
| bool | `bit` |
| sbyte (int8) | `smallint` |
| byte (uint8) | `tinyint` |
| short (int16) | `smallint` |
| ushort (uint16) | `int` |
| int (int32) | `int` |
| uint (uint32) | `bigint` |
| long (int64) | `bigint` |
| ulong (uint64) | `decimal(20,0)` |
| float | `real` |
| double | `float` |
| decimal with declared precision/scale | `decimal(p,s)` from the file, precision clamped to 38, scale clamped to precision |
| decimal without declared precision/scale | `decimal(38,18)` |
| DateTime (timestamp; also a Parquet `date` column, see notes) | `datetime2` |
| DateTimeOffset | `datetimeoffset` |
| Guid (uuid) | `uniqueidentifier` |
| byte[] (binary) | `varbinary(max)` |
| string, TimeSpan (also a Parquet time-of-day column, see notes), and everything else | `nvarchar(max)` |

Notes verified in the reader:

- Narrower or unsigned values with no exact SQL Server counterpart are promoted to the next signed CLR type that holds them (`ConvertValue`): `sbyte` to `short`, `ushort` to `int`, `uint` to `long`, `ulong` to `decimal`.
- `TimeSpan` is deliberately not mapped to SQL `time`: Parquet.Net surfaces both time-of-day and unbounded, possibly negative durations as `TimeSpan`, and SQL `time` only holds values in [0, 24h). Durations render as text in the invariant `"c"` format.
- `MapClrType` also declares `DateOnly -> date` and `TimeOnly -> time` branches, but the current Parquet.Net read path never produces those CLR types: a Parquet `date` column reads back as `DateTime` (the `datetime2` row above), and a Parquet time-of-day column reads back as `TimeSpan` (the `nvarchar(max)` row), so neither branch is reached in practice (confirmed in tests/SqlFlow.Core.Tests/ParquetEdgeCaseTests.cs).
- A decimal value that overflows `System.Decimal` (about 28 to 29 significant digits) fails the file with: `Parquet file '<f>' column '<c>' has a numeric value outside the range of a .NET/SQL Server decimal and cannot be ingested.`
- IEEE `NaN` and positive or negative `Infinity` load as `NULL`; SQL Server `float`/`real` cannot represent them.
- Every Parquet-derived column is declared nullable, so cross-file schema evolution can NULL-fill a column that is missing from some files.

## Struct and nested data

- **Structs** flatten into typed dotted leaf columns joined with `_`: `address.city` becomes `address_city`. Name collisions after flattening get a `_2`, `_3`, ... suffix.
- **One level of nesting** is reconstructed into a single JSON `nvarchar(max)` column, the same keep-a-subtree-as-a-string fallback the JSON/XML readers use:
  - list of scalars: `array<int>` lands as `[7,8,9]`
  - map with scalar key and value: `map<string,int>` lands as `{"a":1,"b":2}`
  - list of flat structs: `array<struct<a,b>>` lands as `[{"a":1,"b":"x"}]`
  - an empty list yields `[]`; a null list yields SQL `NULL`
- **Deeper nesting is rejected** with a clear error rather than reassembled wrong. The exact messages, all ending `Pre-flatten the dataset.`:
  - map with non-scalar key or value: `Parquet file '<f>' column '<path>' is a map with a non-scalar key or value, which this reader does not flatten. Pre-flatten the dataset.`
  - list of structs with a nested struct/list/map field: `Parquet file '<f>' column '<path>' is a list of structs with a nested struct/list/map field '<name>', which this reader does not flatten. Pre-flatten the dataset.`
  - list/map of list/map: `Parquet file '<f>' column '<path>' nests collections (a list/map of list/map), which this reader does not flatten. Pre-flatten the dataset.`
  - repetition level above 1: `Parquet file '<f>' column '<path>' nests more than one level deep (repetition <n>), which this reader does not flatten. Pre-flatten the dataset.`
- An unsupported schema field type fails with: `Parquet file '<f>' has an unsupported schema field '<name>' (<TypeName>).`

Field names route through the same legacy column-name cleanup as every other file source (`CleanColumnNames`, index-preserving), so a V3 run produces the same target column names a legacy install did.

## Multi-file loads and schema evolution

When `location` is a folder, the column set is the additive union across all discovered files and missing columns NULL-fill. When the same column carries different types across files in one load (`MergeColumns` in src/SqlFlow.Sources/FileSourceReaderBase.cs), the unified column widens to an explicit `nvarchar(max)` and values render as text. Columns that agree on CLR type, SQL type, and max length keep their typed declaration.

## Streaming and incremental watermark pruning

Files are read one row group at a time to bound memory, mirroring the original engine.

When the engine injects a row-level watermark into the source options (`incrementalColumn`, `incrementalValue`, `incrementalKind`; constants in src/SqlFlow.Core/Model/Watermark.cs; these are engine-injected, not authored in the flow file), the reader skips whole row groups whose recorded MAX statistic for the watermark column is at or below the bound. Pruning is best-effort and only an optimization: a missing statistic, an unreadable max, or a nested watermark column simply reads the group, and the engine's row-level filter still drops old rows inside kept groups, so correctness never depends on statistics being present. Skipped groups still advance the row counter, so kept rows keep their true line numbers. The watermark column is matched against the cleaned column names, case-insensitively, and only scalar columns are prunable.

## File discovery, date filtering, and lifecycle

Discovery, date-window filtering, and post-load lifecycle are the shared `FileSourceReaderBase` behavior:

- `srcFile` globs and `srcPathMask` regex-filters candidate files; `searchSubDirectories` recurses.
- `initFromFileDate`/`initToFileDate` bound the file date window; by default the file-system modified timestamp is the file date, and `fileDate.from: path|name` with `fileDate.hive` or `fileDate.pattern` derives it from the path or file name instead (Hive partition folders can then be pruned before listing). Setting both `fileDate.hive` and `fileDate.pattern` fails with: `fileDate cannot set both 'fileDate.hive' and 'fileDate.pattern'; choose one.`
- After a successful load, `copyToPath`, `zipToPath`, `srcDeleteIngested`, and `srcDeleteAtPath` run the file lifecycle.

The reader opens each file through a seekable stream (Parquet reads its footer at the end); a non-seekable store stream is buffered in memory first.

## Folder example with schema evolution

Adapted from samples/parquet/parquet-folder.flow.yaml:

```yaml
name: Parquet_Folder
source:
  type: parquet
  location: ./data
  options:
    srcFile: "*.parquet"
    searchSubDirectories: "false"
    expectedColumnCount: "0"
    partitionList: "year,month"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Parquet_Folder
schema:
  evolve: widen
load:
  mode: truncate-load
```

Run it with the CLI (src/SqlFlow.Cli/Program.cs, `run` command):

```bash
dotnet run --project src/SqlFlow.Cli -- run samples/parquet/parquet-folder.flow.yaml
```

## See also

- [DuckDB source](duckdb.md): SQL over Parquet and other files, with predicate pushdown.
- [Flow source section](../source.md): the `source` block shared by all source types.
- [Type inference](../../concepts/type-inference.md): the strings-then-infer path the text formats use; Parquet bypasses it because the file already carries exact types.
