---
id: source-type-json
title: "JSON / NDJSON source (source.type: json | jsonl | ndjson)"
type: source-type
summary: Flatten JSON and newline-delimited JSON files into raw-string columns with path-based include, exclude, explode, alias, and array-handling rules.
keywords:
  - json
  - ndjson
  - jsonl
  - rootpath
  - flatten
  - includepaths
  - arrayhandling
  - explodepaths
  - pathaliases
yamlPath: "source.options (type: json|jsonl|ndjson)"
related:
  - concept-json-xml-flattening
  - concept-file-discovery-and-lifecycle
  - cli-paths
  - cli-flatten
  - cli-discover
  - guide-explore-json-xml
sourceRefs:
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.Sources/Json/JsonRecordReader.cs
  - src/SqlFlow.Sources/Json/JsonFlattenConfig.cs
  - src/SqlFlow.Sources/Json/JsonPathFlattener.cs
  - src/SqlFlow.Core/Model/PreIngestionJsn.cs
  - samples/json/README.md
---

# JSON / NDJSON source

`source.type: json`, `jsonl`, or `ndjson` (case-insensitive) reads JSON files through a declarative, path-based flattener (`JsonSourceReader` in src/SqlFlow.Sources/JsonSourceReader.cs). Nested keys fold into flat column names with a separator (`address.city` becomes `address_city`), arrays follow a configurable handling rule, and every value lands in the raw table as a string; the transform and inference layer (`sqlflow infer`) proposes typed columns afterward. File selection, schema evolution across files, provenance and synthetic-key columns, and the post-load lifecycle are the same shared file-source pipeline the CSV and XLS readers use.

The flow type discriminator is `jsn` and the metadata model is `PreIngestionJsn` (src/SqlFlow.Core/Model/PreIngestionJsn.cs).

## Minimal example

```yaml
name: Json_Basic
source:
  type: json
  location: ./data/orders.json
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_Basic
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

## Record shapes and parsing

`JsonRecordReader` (src/SqlFlow.Sources/Json/JsonRecordReader.cs) auto-detects the three record shapes:

- A single top-level object: one record.
- A top-level array: one record per element.
- Newline-delimited JSON: one record per line.

`source.type: jsonl` and `ndjson` force line mode up front. `source.type: json` parses the whole document first and falls back to line mode only if whole-document parsing fails. After `rootPath` navigation, an array fans out to one record per element, an object yields a single record, and scalars are not records.

Parsing details, all defined in code:

- A UTF-8 BOM is stripped. An empty file yields no records.
- Trailing commas and comments are tolerated (`JsonDocumentOptions`); the parser depth cap is 256.
- In line mode, blank lines and lines whose first non-whitespace character is `#` are skipped. A malformed line fails the flow with `Invalid JSON in '<file>' at line N: <cause>`.
- Each file is fully buffered into memory and read twice (a schema pass, then a data pass), so a file's column order is deterministic between the two passes.

## Options reference (`source.options`)

All option values are strings. Flatten-specific options:

| Option | Default | Description |
| --- | --- | --- |
| `rootPath` | `$` | JSONPath the records live under (for example `$.data.records`). `$` is the document root. Dot segments plus optional `[index]` steps. An array at the root path fans out to one record per element. |
| `includePaths` | (empty) | Comma-separated whitelist of paths to flatten. Empty means flatten everything. Ancestors on the way to a whitelisted leaf are still traversed. |
| `excludePaths` | (empty) | Comma-separated paths whose subtree is dropped (no column produced). |
| `jsonPaths` | (empty) | Comma-separated paths kept verbatim as a single JSON-string column instead of being flattened into children. |
| `explodePaths` | (empty) | Comma-separated array paths exploded to one output row per element. Multiple exploded arrays in one record cross-product. |
| `pathAliases` | (empty) | Schema-evolution aliases: `col=$.a\|$.b; col2=$.c\|$.d` (semicolon-separated groups; `\|`-separated source paths per column). |
| `columnMappings` | (empty) | Semicolon-separated `jsonPath=columnName` overrides. |
| `separator` | `_` | Separator joining nested keys into a column name. |
| `maxDepth` | `10` | Maximum nesting depth flattened into columns; deeper values become JSON strings. Must be a positive integer. |
| `arrayHandling` | `to_json` | How arrays not covered by an explicit rule become a column: `to_json`, `first_element`, `join`, `count`, `skip`, or `explode`. |
| `joinSeparator` | `,` | Separator used when `arrayHandling` is `join`. |

Shared file-source options, bound by `PreIngestionJsn.FromSource` and threaded into the read pipeline by `JsonSourceReader.ReadOptions`:

| Option | Default | Description |
| --- | --- | --- |
| `srcFile` | (default glob `*.json`) | File name pattern within `source.location`. |
| `srcPathMask` | (none) | Path mask filter. |
| `searchSubDirectories` | `false` | Recurse into subdirectories. |
| `copyToPath` / `zipToPath` | (none) | Post-load file lifecycle: copy or zip ingested files. |
| `srcDeleteIngested` / `srcDeleteAtPath` | `false` | Post-load file lifecycle: delete ingested files. |
| `initFromFileDate` / `initToFileDate` | (none) | File modified-date window for file selection. |
| `readAhead` | `4` | How many source files are kept open at once (1 to 32). Files are still read one at a time in file order; a higher value only opens and downloads the following files ahead of their turn, which removes the per-file latency that dominates a source of many small files. The JSON reader streams records, so an open file costs a small buffer and one record. An out-of-range value fails with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` |
| `includeFileName`, `includeFileDate`, `includeFileRowDate`, `includeFileSize`, `includeDataSet`, `includeRowNumber` | `true` | Provenance column toggles. |
| `includeFileLineNumber` | `false` | Adds the source record ordinal as a column. |
| `showPathWithFileName` | `false` | Include the path with the file name in `FileName_DW`. |
| `dataSetFromFileName` | `true` | Derive `DataSet_DW` from a date in the file name (fallback: modified date); `false` makes it equal `FileDate_DW`. See [dataSetFromFileName](../../concepts/provenance-and-row-keys.md#dataset_dw-and-datasetfromfilename). |
| `dataSetFormats` | none | Extra .NET date formats for `DataSet_DW` detection, comma- or pipe-separated, tried before the built-ins. |
| `dataSetDayFirst` | (inferred) | Hard-lock day-first (`true`) or month-first (`false`) for ambiguous same-length dates; inferred from the file set when unset. |
| `includeHashKey` | `false` | Add `HashKey_DW` over the flattened columns. |
| `hashKeyColumns` | (all columns) | Columns feeding the hash key. |
| `hashKeyType` | `SHA2_512` | Hash algorithm. |
| `includeConcatKey` | `false` | Add `ConcatKey_DW`. |
| `concatKeyColumns` | (none) | Columns feeding the concat key. |
| `concatKeySeparator` | `\|` | Concat key separator. |
| `defaultColDataType` | (none) | SQL type for raw columns when `schema.defaultColumnType` is not set; read directly from `source.options` by `YamlFlowLoader` ahead of the global `varchar(255)` fallback (src/SqlFlow.Yaml/YamlFlowLoader.cs). |

A flattened column whose name matches an enabled provenance or key column (for example a JSON field literally named `RowNumber_DW`) fails the flow: `A source column named '<name>' collides with the enabled '<name>' provenance/key column, whose generated value would overwrite the source data. Rename the source column (columnMappings) or disable that provenance/key column.`

`PreIngestionJsn.FromSource` also binds the wider legacy metadata set carried over from the `flw.PreIngestionJSN` table: `sysAlias`, `flowId`, `servicePrincipalAlias`, `trgServer`, `trgDBSchTbl`, `trgDesiredIndex`, `expectedColumnCount` (default `0`), `syncSchema` (default `true`), `fetchDataTypes` (default `false`), `preFilter`, `preProcessOnTrg`, `postProcessOnTrg`, `preInvokeAlias`, `onErrorResume` (default `true`), `deactivateFromBatch` (default `false`), `enableEventExecution` (default `false`), and `noOfThreads` (default `4`). None of these reach `FileSourceOptions`: `JsonSourceReader.ReadOptions` does not forward them, so setting any of them under `source.options` has no effect on a JSON load. Unlike CSV and Parquet, where `expectedColumnCount` is enforced, JSON parses the value and never checks it.

### rootPath

Navigates from the document root to where the records live, with dot segments and optional bracketed integer index steps, for example `$.data.records` or `$.batches[0].rows`. If a segment or index does not resolve, the document contributes zero records (no error). Malformed bracket syntax fails the flow, for example:

- Non-integer or negative index: `Invalid rootPath index '[<token>]' in '<rootPath>' for '<file>'.`
- Missing `]`: `Invalid rootPath '<rootPath>' for '<file>': missing ']'.`
- Text between two bracket groups that is not itself a bracket (for example `$.a[0]x[1]`): `Invalid rootPath '<rootPath>' for '<file>': expected '[' at '<remainder>'.`

In line mode, `rootPath` is applied to each line's document.

### includePaths, excludePaths, jsonPaths

Path patterns match exactly, or with a single bare `*` wildcard as prefix/suffix glob (for example `$.raw_*`). Concrete array indices in a record's path (`$.a[0].b`) are normalized to the wildcard form, so a pattern written as `$.a[*].b` matches every element. `jsonPaths` entries are implicitly included even when an `includePaths` whitelist is set.

A subtree kept whole via `jsonPaths` (or anything past `maxDepth`) can be large; give those flows a roomy `schema.defaultColumnType` such as `nvarchar(max)`.

### explodePaths

Each listed array path emits one output row per element (SQL UNNEST semantics). Several exploded arrays in one record cross-product. An exploded array is consumed: it does not also produce a JSON-string column. Column names are index-stable: `$.details[0].track_id` and `$.details[7].track_id` both become `details_track_id`. Setting `arrayHandling: explode` explodes every array instead of listing paths.

A safety bound of 1,000,000 output rows per source record applies; exceeding it fails with `Exploding '<path>' produced more than 1000000 rows for a single record. Narrow the explode paths or pre-split the data.`

### pathAliases

Maps version-specific source paths onto one output column for schema evolution, format `columnName=$.path1|$.path2`, groups separated by `;`. Array indices in the paths are normalized to wildcards. A malformed entry fails with `Invalid pathAliases entry '<entry>'. Use 'columnName=$.path1|$.path2' separated by ';'.`

### columnMappings

Explicit `jsonPath=columnName` renames, entries separated by `;`. A malformed entry fails with `Invalid columnMappings entry '<entry>'. Use 'jsonPath=columnName' separated by ';'.` Mapped names are sanitized the same way as generated names.

### arrayHandling

Parsing is case-insensitive and ignores `_` and `-`. Accepted values and synonyms:

| Value | Synonyms | Effect |
| --- | --- | --- |
| `to_json` (default) | `tojson`, `asjson`, `json` | Keep the array as one JSON-string column (lossless). |
| `first_element` | `first` | Flatten the first element in place; its fields become columns under the array's path. |
| `join` | `join_comma` | Join the array's scalar elements with `joinSeparator` into one string. |
| `count` | | Emit the element count as the column value. |
| `skip` | | Drop the array entirely (no column). |
| `explode` | `unnest` | One output row per element for every array. |

An unrecognized value fails with `Unknown arrayHandling '<value>'. Use to_json, first_element, join, count, skip, or explode.`

### maxDepth

Values nested deeper than `maxDepth` are captured as JSON strings at the boundary. `maxDepth` below 1 fails with `Invalid maxDepth '<n>'. Use a positive integer.`

## Column naming

Generated column names strip the `$` root and array subscripts, join the remaining segments with `separator`, and sanitize to a valid SQL identifier (invalid characters replaced by the separator, runs collapsed, trimmed; a leading digit gets a `_` prefix; an empty result becomes `column`). Casing is preserved. Column matching between the schema pass and the data pass is case-insensitive (`OrdinalIgnoreCase`), matching SQL Server collation.

## Type inference behavior

Every value lands as a raw string in the raw layer; numbers keep their raw token so precision is preserved. Run `sqlflow infer` against the flow afterward to propose typed columns (the inference report carries the transform SELECT plus each column's fit/silent-null status).

## Row numbering

When `includeFileLineNumber` is on, the `FileLineNumber` column carries the source record ordinal. `RowNumber_DW` is the output row number, which can exceed the record count when explode paths multiply rows.

## File discovery, date filtering, and lifecycle

`source.location` plus `srcFile` (default `*.json`) and `srcPathMask` select files; `searchSubDirectories: "true"` recurses. `initFromFileDate` / `initToFileDate` bound the file modified-date window, and the engine injects the incremental watermark as an exclusive lower bound between runs. After a successful load, `copyToPath`, `zipToPath`, `srcDeleteIngested`, and `srcDeleteAtPath` drive the file lifecycle. All of this is the shared file-source pipeline, identical to CSV and XLS.

The `fileDate.*` option group (for example `fileDate.from: path` with `fileDate.hive: "true"`) redirects where the file's business date is read from (path tokens or file name instead of the modified timestamp); see [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md) for the full contract.

## Exploring structure before authoring

Three CLI commands (src/SqlFlow.Cli/Program.cs) help author a flatten configuration:

```bash
# List every addressable JSONPath in a file or folder (no pipeline YAML needed),
# including the container paths valid for rootPath / jsonPaths / excludePaths.
sqlflow paths data/jsn/product.json
sqlflow paths data/jsn/cia-factbook -r --values

# Against a pipeline YAML: the columns the flatten will produce, drift across files,
# and a starter source.options block.
sqlflow discover samples/json/json-basic.flow.yaml

# Emit a complete runnable flow stub (the flatten formula), or dry-run the rows as CSV.
sqlflow flatten data/jsn/product.json -o product.flow.yaml
sqlflow flatten data/jsn/product.json --data --max-records 20
```

`flatten` accepts inline rule flags that map onto the same options: `--root`, `--include`, `--exclude`, `--explode`, `--aliases`, `--keep` (or the JSON-specific `--json`) to set `jsonPaths`, `--array`, `--separator`, `--join-separator`, `--map`, plus `--pattern` and `--recursive`/`-r` for folders. All three commands accept `--max-files`, `--max-records`, and `--max-depth` scan bounds.

## Fuller examples

Records nested under an envelope, selected with `rootPath` (samples/json/json-root-path.flow.yaml):

```yaml
name: Json_RootPath
source:
  type: json
  location: ./data/envelope.json
  options:
    rootPath: "$.data.records"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_RootPath
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

Schema evolution across two dataset versions in one folder (samples/json/json-schema-evolution.flow.yaml). The additive column union gives the table every column across shapes; `pathAliases` reconciles the renamed field so `$.name` (v1) and `$.fullName` (v2) both feed one `person_name` column:

```yaml
name: Json_SchemaEvolution
source:
  type: json
  location: ./data/evolution
  options:
    srcFile: "*.json"
    pathAliases: "person_name=$.name|$.fullName"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_SchemaEvolution
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

Explode with synthetic keys (adapted from samples/json/json-explode.flow.yaml and samples/json/json-keys.flow.yaml):

```yaml
name: Json_Explode
source:
  type: json
  location: ./data/lineitems.json
  options:
    explodePaths: "$.lines"
    includeHashKey: "true"
    includeConcatKey: "true"
    concatKeyColumns: "id, lines_sku"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_Explode
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

The full sample set under samples/json/ loads into `dbo.Json_*` tables and is exercised by tests/SqlFlow.Core.Tests/Integration/JsonSampleFlowIntegrationTests.cs.

## See also

- [JSON and XML flattening concepts](../../concepts/json-xml-flattening.md)
- [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md)
- [sqlflow paths](../../cli/paths.md)
- [sqlflow discover](../../cli/discover.md)
- [sqlflow flatten](../../cli/flatten.md)
- [Exploring JSON and XML sources](../../guides/explore-json-xml.md)
