---
id: cli-discover
title: sqlflow discover
type: cli-command
summary: Scan a pipeline's declared JSON or XML source and report its path structure, flatten columns, schema drift, and a starter source.options block.
keywords:
  - discover
  - json
  - xml
  - structure scan
  - collisions
  - source options
cliCommand: discover
related:
  - cli-paths
  - cli-flatten
  - concept-json-xml-flattening
  - guide-explore-json-xml
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.Sources/XmlSourceReader.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Sources/FlattenIntrospection.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
---

# sqlflow discover

## Synopsis

```bash
sqlflow discover <pipeline.flow.yaml> [--max-files N] [--max-records N] [--max-depth N]
```

## Description

`discover` is the config-authoring view of a pipeline's JSON or XML source. It loads the pipeline YAML, resolves the declared source, scans a bounded sample of its files, and reports:

- every discovered path and the column the current flatten configuration would produce for it,
- how many of the scanned records contained each path (schema drift across files),
- a starter `source.options` block, echoing the resolved flatten options, ready to paste into the pipeline.

Unlike `sqlflow paths` and `sqlflow flatten`, which point straight at a data file or folder, `discover` takes a pipeline file. It therefore sees the source through the pipeline's own configuration: the declared `source.type`, `source.location`, and any flatten options already set under `source.options` all shape the report.

Only JSON and XML sources qualify. The command matches the source type against the flatten introspectors: `json`, `jsonl`, and `ndjson` are handled by the JSON reader (src/SqlFlow.Sources/JsonSourceReader.cs), `xml` by the XML reader (src/SqlFlow.Sources/XmlSourceReader.cs). Any other type fails:

```text
ERROR  'discover' supports JSON and XML sources; 'csv' is not one.
```

The pipeline must parse as a file flow (the default pipeline kind, with top-level `name`, `source`, and `target` keys). A document missing one of those keys fails validation with an error such as `<file>: 'source' is required.` and exit code 1.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<pipeline.flow.yaml>` | yes | Path to a file-flow pipeline whose `source.type` is `json`, `jsonl`, `ndjson`, or `xml`. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--max-files <n>` | int | `100` | Maximum number of source files to scan. `0` scans every selected file. |
| `--max-records <n>` | int | `0` | Maximum number of records to scan across all files. `0` scans every record. |
| `--max-depth <n>` | int | `10` | Maximum nesting depth to inspect. Values below `1` fall back to `10`. |

A non-integer or negative option value is ignored and the default applies. The sibling `paths` and `flatten` commands use the same flags but default `--max-depth` to `20`; `discover` uses `10`, matching the flatten's own `maxDepth` default.

## Behavior and output

### Source resolution

A relative `source.location` is resolved against the pipeline file's own directory before scanning (src/SqlFlow.Execution/DocumentLoader.cs, `ResolveRelativeLocation`), so a sample's `./data/orders.json` works no matter which directory the command runs from. Absolute locations are left untouched.

### File selection

Discovery selects files through the same resolver a load uses (src/SqlFlow.Sources/FileSourceReaderBase.cs, `ResolveFilesAsync`), applying every selection option declared on the source: the `srcFile` glob (default `*.json` or `*.xml`), `searchSubDirectories`, the `srcPathMask`, the `initFromFileDate`/`initToFileDate` window, the `fileDate.*` date interpretation, and an `incrementalAfterDate` bound when one is present in `source.options`. Files are scanned in ascending modified-time order.

One run-time behavior is absent: `discover` does not probe the target database for an incremental watermark (that injection happens inside a run, in `FlowRunner`). An incremental flow's discovery therefore scans the files an initial load would see.

### Report anatomy

The header states the sample size:

```text
Discovered N path(s) for '<name>' across R record(s) in F file(s).
```

When any path is absent from some scanned records, a drift note follows:

```text
schema drift: a path missing from a file becomes NULL for that file's rows (reconcile renames with pathAliases).
```

If the scan finds no records at all, the command prints `(no records found - check the location, file pattern, and the row path)` and still exits 0.

The table has three columns:

- `PATH`: the JSONPath (for example `$.customer.name`) or XPath (for example `/customer/name`, `/@id` for attributes). A JSON array contributes the array path (`$.items`) plus, when it has elements, an element path (`$.items[*]`).
- `COLUMN`: the column name the current flatten configuration produces for that path. Container paths (JSON objects, non-repeating XML elements with children) show `-`; they are targets for `rootPath`/`rowXPath`, keep-as-string (`jsonPaths`/`xmlPaths`), or `excludePaths` rules, not columns. Repeating paths (JSON arrays, repeating XML elements) show the column their array/repeat handling produces and are the valid `explodePaths` targets.
- `PRESENCE`: `all`, or `k/R` when only `k` of the `R` scanned records contained the path.

Values wider than the column are truncated with `...`.

Finally, the starter block echoes the resolved flatten options as YAML key/value lines to paste under `source.options`:

- JSON sources always echo `rootPath`, `separator`, and `arrayHandling` (their resolved values, including defaults `$`, `_`, and `to_json`), plus `includePaths`, `excludePaths`, `jsonPaths`, `explodePaths`, `pathAliases`, and `columnMappings` when set.
- XML sources always echo `separator` and `repeatHandling` (defaults `_` and `to_xml`), plus `rowXPath` when set, `includeAttributes: "false"` when attributes are disabled, `attributePrefix` when overridden, and the same conditional path options with `xmlPaths` in place of `jsonPaths`.

`discover` does not print a column-name collision section; run `sqlflow paths` against the same data to see collisions (two paths folding onto one column, where the last value wins under the default flatten) and each path's kind.

## Examples

Discover a JSON flow (samples/json/json-basic.flow.yaml, which reads `./data/orders.json` relative to the pipeline file):

```bash
sqlflow discover samples/json/json-basic.flow.yaml
```

```text
Discovered 8 path(s) for 'Json_Basic' across 3 record(s) in 1 file(s).
  schema drift: a path missing from a file becomes NULL for that file's rows (reconcile renames with pathAliases).

  PATH                                               COLUMN                       PRESENCE
  $.id                                               id                           all
  $.customer                                         -                            all
  $.customer.name                                    customer_name                all
  $.customer.city                                    customer_city                all
  $.items                                            items                        all
  $.items[*]                                         items                        2/3
  $.amount                                           amount                       all
  $.paid                                             paid                         all

  Starter flatten config (paste under source.options):
    rootPath: "$"
    separator: "_"
    arrayHandling: "to_json"
```

Here the drift note fires because one record's `items` array is empty, so its element path `$.items[*]` appears in only 2 of 3 records.

Discover an XML flow (samples/xml/xml-basic.flow.yaml):

```bash
sqlflow discover samples/xml/xml-basic.flow.yaml
```

```text
Discovered 6 path(s) for 'Xml_Basic' across 2 record(s) in 1 file(s).

  PATH                                               COLUMN                       PRESENCE
  /@id                                               id                           all
  /customer                                          -                            all
  /customer/name                                     customer_name                all
  /customer/city                                     customer_city                all
  /total                                             total                        all
  /paid                                              paid                         all

  Starter flatten config (paste under source.options):
    separator: "_"
    repeatHandling: "to_xml"
```

Bound the scan on a large folder-backed source (sample the first 10 files and 500 records, inspecting 6 levels deep):

```bash
sqlflow discover flows/orders-lake.flow.yaml --max-files 10 --max-records 500 --max-depth 6
```

Paste the starter block into the pipeline and refine it. The keys are the flatten options the source readers parse (src/SqlFlow.Core/Model/PreIngestionJsn.cs and PreIngestionXml.cs):

```yaml
name: Json_Basic
source:
  type: json
  location: ./data/orders.json
  options:
    rootPath: "$"
    separator: "_"
    arrayHandling: "to_json"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_Basic
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

## Exit behavior

| Exit code | When |
| --- | --- |
| `0` | The scan completed, including scans that found no records. |
| `1` | The source type is not JSON or XML (`ERROR  'discover' supports JSON and XML sources; '<type>' is not one.`); the pipeline file is missing or fails validation (for example `Pipeline file not found: '<path>'.` or `<file>: 'source' is required.`); the source has no `location` or no file store handles it; no files match the selection filters (`No files under '<path>' matched the filters (...)`); or the positional argument is missing (usage is printed). |

`-h`/`--help` prints usage; the exit code is 0 when the pipeline argument is also present, and 1 when it is missing.

## See also

- [sqlflow paths](./paths.md): list every addressable path in a JSON/XML file or folder directly, including path kinds and column-name collisions, no pipeline YAML needed.
- [sqlflow flatten](./flatten.md): emit the full flatten formula as a runnable flow stub, or dump the flattened rows as CSV with `--data`.
- [JSON and XML flattening](../concepts/json-xml-flattening.md): how paths become columns, array and repeat handling, and the flatten option keys.
- [Exploring JSON and XML sources](../guides/explore-json-xml.md): a walkthrough from raw files to a running pipeline.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
