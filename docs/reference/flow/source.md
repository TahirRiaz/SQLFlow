---
id: flow-source
title: "File flow: source section"
type: flow-reference
summary: "The source section of a file flow: type selects the reader, location points at the data, options carries reader-specific settings."
keywords:
  - source.type
  - source.location
  - source.options
  - reader selection
  - file flow
  - srcpath
  - defaultcoldatatype
yamlPath: source
related:
  - source-type-csv
  - source-type-json
  - concept-file-source-pipeline
  - concept-file-discovery-and-lifecycle
sourceRefs:
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Execution/SqlFlowEngineServices.cs
  - src/SqlFlow.Core/Model/SourceOptions.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
---

# File flow: source section

The `source` section of a file flow describes what to read. It is deliberately minimal: `type` is an open-ended discriminator that selects an `ISourceReader` implementation, `location` is the path or URI the reader interprets, and `options` is a free-form string map of reader-specific settings. The model (`SourceSpec` in src/SqlFlow.Core/Model/FlowDefinition.cs) is agnostic to the kind of system being read; adding a new source type means implementing one reader interface, with no change to the model or the engine.

```yaml
name: Csv_Basic
source:
  type: csv
  location: ./data/orders.csv
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_Basic
```

## Keys reference

| Key | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `source.type` | string | yes | none | Selects the source reader. Matched case-insensitively against the registered readers. |
| `source.location` | string | no | none | Path, folder, or URI, interpreted by the selected reader (the DuckDB reader also accepts a glob). File readers fall back to `options.srcPath` when omitted. |
| `source.options` | map of string to string | no | empty map | Reader-specific settings. Keys are matched case-insensitively. |

The whole `source` section is itself required: a flow document without one fails validation with `<file>: 'source' is required.` (src/SqlFlow.Yaml/YamlFlowLoader.cs).

## source.type

Required. When missing or blank the loader fails with:

```text
<file>: 'source.type' is required.
```

At run time the engine picks the first registered `ISourceReader` whose `CanHandle(sourceType)` returns true; every reader compares case-insensitively, so `CSV`, `Csv`, and `csv` are equivalent. The readers registered by the engine (src/SqlFlow.Execution/SqlFlowEngineServices.cs) accept these tokens:

| Token(s) | Reader | Reads |
| --- | --- | --- |
| `csv` | `CsvSourceReader` | Delimited and fixed-width text files. |
| `xls`, `xlsx` | `XlsSourceReader` | Excel workbooks. |
| `json`, `jsonl`, `ndjson` | `JsonSourceReader` | JSON and newline-delimited JSON files, flattened path-based. |
| `xml` | `XmlSourceReader` | XML files, flattened path-based. |
| `parquet`, `prq` | `ParquetSourceReader` | Apache Parquet files; the file's own schema supplies typed columns. |
| `duckdb`, `delta` | `DuckDbSourceReader` | DuckDB queries over Parquet/CSV/JSON files, globs, partitioned datasets, and Delta tables. |

A `type` no reader claims fails at run time (src/SqlFlow.Core/Engine/FlowRunner.cs):

```text
No source reader is registered for source type '<type>'.
```

## source.location

Optional in the model. A path to a single file, a folder (file readers consolidate every matching file in it), or a URI; the DuckDB reader also accepts a glob. Interpretation is entirely up to the reader.

For the file readers (csv, xls/xlsx, json, xml, parquet), `location` and the `srcPath` option are two spellings of the same input: the reader resolves the source path as `source.Location ?? options.srcPath` (see for example src/SqlFlow.Core/Model/PreIngestionCsv.cs). When `location` is set it wins; when both are absent file resolution fails (src/SqlFlow.Sources/FileSourceReaderBase.cs) with:

```text
Source requires a 'location' (file path, folder, or URI).
```

The DuckDB reader requires either `location` or a `query` option; with neither it fails with:

```text
A duckdb source needs a 'location' (a file/glob/table path) or a 'query' option.
```

## source.options

Optional. A free-form `map<string, string?>`; the loader copies it into a case-insensitive dictionary, so `srcFile`, `srcfile`, and `SRCFILE` address the same option. Which keys are meaningful depends on the selected reader; unrecognized keys are ignored. See the per-type pages for each reader's full option list.

Readers access the bag through the shared typed accessors in src/SqlFlow.Core/Model/SourceOptions.cs:

- `GetString(key, fallback)`: returns the value unless it is missing or whitespace-only, in which case the fallback applies.
- `GetBool(key, fallback)`: parsed with `bool.TryParse` (`true`/`false`, case-insensitive); anything unparsable falls back.
- `GetInt(key, fallback)`: parsed as an invariant-culture integer; anything unparsable falls back.

Unparsable option values therefore never raise an error at this layer; they silently resolve to the reader's default.

The loader also injects one system entry into the bag: `options["flowId"]` is set to the flow's deterministic identity GUID (computed from `name`), so the option map carried into the reader always contains it. It is not authored in YAML; an authored value is overwritten.

### Interaction with schema.defaultColumnType

The default SQL type for raw (untyped) columns is resolved in this order (src/SqlFlow.Yaml/YamlFlowLoader.cs, `ResolveDefaultColumnType`):

1. `schema.defaultColumnType`, when present and non-blank.
2. `source.options.defaultColDataType`, when present and non-blank (the SQLFlow metadata convention).
3. The global default `varchar(255)`.

## Full example

Adapted from samples/quickstart/orders.flow.yaml. The `options` shown are CSV reader options; the file lifecycle and provenance-column options are shared by all file readers.

```yaml
name: orders

source:
  type: csv
  location: ./incoming            # a file OR a folder of files
  options:
    delimiter: ","
    header: true                  # first row has column names
    textQualifier: "\""
    encoding: UTF8
    srcFile: "orders_*.csv"       # glob applied when 'location' is a folder
    copyToPath: ./archive         # file lifecycle: copy each ingested file here

target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders

schema:
  evolve: widen
  defaultColumnType: varchar(255)

load:
  mode: append
```

Validate, preview, and run it with the CLI:

```bash
sqlflow validate orders.flow.yaml
sqlflow plan orders.flow.yaml
sqlflow run orders.flow.yaml
```

## See also

- [File flow overview](./overview.md)
- [File flow: target section](./target.md)
- [File flow: load section](./load.md)
- [CSV source type](./source-types/csv.md)
- [JSON source type](./source-types/json.md)
- [File source pipeline](../concepts/file-source-pipeline.md)
- [File discovery and lifecycle](../concepts/file-discovery-and-lifecycle.md)
