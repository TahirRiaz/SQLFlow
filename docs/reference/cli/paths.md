---
id: cli-paths
title: sqlflow paths
type: cli-command
summary: List every addressable JSONPath or XPath in a JSON, NDJSON, or XML file or folder, with the column each value path would become.
keywords:
  - paths
  - jsonpath
  - xpath
  - introspection
  - columns
  - inventory
  - collisions
cliCommand: paths
related:
  - cli-discover
  - cli-flatten
  - concept-json-xml-flattening
  - guide-explore-json-xml
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Sources/FlattenIntrospection.cs
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.Sources/XmlSourceReader.cs
  - src/SqlFlow.Sources/Json/JsonPathInventory.cs
  - src/SqlFlow.Sources/Xml/XmlPathInventory.cs
---

# sqlflow paths

## Synopsis

```bash
sqlflow paths <file|folder> [--values] [--root <path>] [--pattern <glob>] [-r|--recursive]
              [--max-files <n>] [--max-records <n>] [--max-depth <n>]
```

## Description

`paths` points straight at a JSON, NDJSON, or XML file or folder; no pipeline YAML is needed. It scans a sample of records and lists every addressable path with its kind, the SQL column a value path would become under the default flatten, and how many sampled records contained it. It also reports column-name collisions, so you can see where two paths would fold onto one column before authoring a flow.

The source format is resolved from the target, never from a flag:

- For a **file**, the extension decides: `.xml` is read as XML; `.ndjson` and `.jsonl` are read as line-delimited JSON; any other extension is read as JSON.
- For a **folder**, `--pattern` decides: a glob ending in `.xml` (case-insensitive) selects XML, anything else selects JSON. The glob is used to match files in the folder; without `--pattern` the default is `*.json`.

Records are selected the same way a load selects them:

- JSON: records live under `rootPath` (default `$`, the document root). An array there fans out to one record per element; an object is a single record.
- NDJSON / JSONL: each non-blank line is parsed as one JSON document (blank lines and lines starting with `#` are skipped); `rootPath` applies within each line, and a line whose root is an array fans out to one record per element.
- XML: records are selected by `rowXPath`. By default (also with `*` or `/*`) each direct child element of the root is a record; `.` or `/` treats the root element itself as one record; any other value is evaluated as an XPath expression that must select elements.

Both are set with `--root` (see Options). `paths` runs read-only; it never touches a database and never triggers the file lifecycle (no copy, zip, or delete).

Each path is classified as one of three kinds (`SchemaPathKind` in src/SqlFlow.Sources/FlattenIntrospection.cs):

| Kind | Meaning |
| --- | --- |
| `value` | A scalar leaf (JSON value, XML element text, or attribute) that becomes a column. |
| `container` | An object or element container; a valid target for `rootPath` / `rowXPath`, keep-as-string (`jsonPaths` / `xmlPaths`), or `excludePaths`. |
| `repeating` | A JSON array or repeating XML element; it becomes a column under `arrayHandling` / `repeatHandling`, or a target for `explodePaths`. |

A path containing `[*]` addresses the elements of a repeat (for example `$.items[*]` or `/line[*]/sku`). Under the default flatten (`arrayHandling: to_json` / `repeatHandling: to_xml`) such a path produces no column of its own, because the repeat is kept whole as one text column; it becomes a column when its repeat is exploded (one row per element).

Note that `paths` takes no flatten rule flags (`--include`, `--exclude`, `--explode`, `--separator`, `--map`, and so on belong to `sqlflow flatten`). The column names it prints reflect the default flatten: the root marker and subscripts are stripped, nesting separators fold onto `_`, XML attribute names lose their `@` marker, and remaining non-identifier characters are sanitized.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<file\|folder>` | yes | A JSON, NDJSON, or XML file, or a folder of such files. Omitting it prints the usage text and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--values` | flag | off | Print only the `value` paths, one per line, with no header, table, or footer. For piping. |
| `--root <path>` | string | JSON: `$`; XML: each direct child of the root | Sets `rootPath` for JSON sources or `rowXPath` for XML sources. |
| `--pattern <glob>` | string | `*.json` | File glob when the target is a folder. A glob ending `.xml` also switches the format to XML. Ignored for a file target. |
| `-r`, `--recursive` | flag | off | Recurse into sub-folders (sets `searchSubDirectories: true`). Folder targets only. |
| `--max-files <n>` | int | 100 | Maximum files to scan; `0` scans all files. |
| `--max-records <n>` | int | 0 | Maximum records to scan; `0` scans all records. |
| `--max-depth <n>` | int | 20 | Maximum nesting depth to inspect. A value of `0` falls back to a depth of 10 inside the scanner; it does not mean unlimited. |

Numeric options that are negative or not an integer fall back to their default. Options may appear anywhere on the command line relative to the positional argument; an option whose next token starts with `-` (and is longer than one character) is treated as having no value; a lone `-` is accepted as a value.

## Behavior and output

The default output is:

1. A header: `N path(s) across R record(s) in F file(s).`
2. A table with columns `PATH` (truncated to 50 characters with `...`), `KIND` (`value`, `repeating`, or `container`), `COLUMN` (the column a value or repeating path would become; `-` for containers; truncated to 28 characters), and `PRESENCE` (`all` when the path appeared in every sampled record, otherwise `n/m`).
3. A collision report, only when two or more non-container paths (excluding `[*]` element paths) fold onto the same column name, compared case-insensitively. Under the default flatten the later path silently overwrites the earlier, so the report opens with `N column-name collision(s) under the default flatten (last value wins):`, lists each colliding group as `column <- path1, path2`, and closes with the single hint line `disambiguate with columnMappings or a different separator.`
4. A footer explaining the kinds: value paths become columns; container and repeating paths are targets for root, keep-as-string, exclude, or explode; a `[*]` path becomes a column when its repeat is exploded.

Paths are listed in first-seen document order across the sample. When a path has different kinds in different records (heterogeneous data), JSON prefers the more structured kind (object over array over value) so a container is never hidden behind a scalar reading; XML prefers the value reading, so an element that carries text anywhere still produces a column.

If the scan finds no records, the header is followed by `(no records found - check the path, file pattern, and --root)` and nothing else.

With `--values` the command prints only the `value` paths, one per line, and nothing else.

## Examples

Inventory a JSON file (samples/json/data/orders.json, an array of order objects with a nested `customer` object and an `items` array):

```bash
sqlflow paths samples/json/data/orders.json
```

```text
8 path(s) across 3 record(s) in 1 file(s).

  PATH                                               KIND       COLUMN                       PRESENCE
  $.id                                               value      id                           all
  $.customer                                         container  -                            all
  $.customer.name                                    value      customer_name                all
  $.customer.city                                    value      customer_city                all
  $.items                                            repeating  items                        all
  $.items[*]                                         value      items                        2/3
  $.amount                                           value      amount                       all
  $.paid                                             value      paid                         all

  value paths become columns; container/repeating paths are targets for root, keep-as-string,
  exclude, or explode. A [*] path becomes a column when its repeat is exploded (one row per
  element). Use --values to list only the column paths (one per line).
```

`$.items[*]` shows `2/3` because the third record's `items` array is empty, so no element path was observed in it.

Inventory an XML file (samples/xml/data/purchase-orders.xml; the first `<order>` has two `<line>` elements, the second has one, so the same fields surface both as `/line/...` and `/line[*]/...`):

```bash
sqlflow paths samples/xml/data/purchase-orders.xml
```

```text
7 path(s) across 2 record(s) in 1 file(s).

  PATH                                               KIND       COLUMN                       PRESENCE
  /id                                                value      id                           all
  /line                                              repeating  line                         all
  /line[*]                                           container  -                            1/2
  /line[*]/sku                                       value      line_sku                     1/2
  /line[*]/qty                                       value      line_qty                     1/2
  /line/sku                                          value      line_sku                     1/2
  /line/qty                                          value      line_qty                     1/2

  value paths become columns; container/repeating paths are targets for root, keep-as-string,
  exclude, or explode. A [*] path becomes a column when its repeat is exploded (one row per
  element). Use --values to list only the column paths (one per line).
```

List only the column paths of an NDJSON file, one per line, for piping:

```bash
sqlflow paths samples/json/data/events.ndjson --values
```

```text
$.id
$.kind
$.ts
```

Scan a folder of XML exports recursively, selecting rows with an XPath and capping the sample:

```bash
sqlflow paths ./exports --pattern "*.xml" -r --root "//order" --max-files 25 --max-records 500
```

## Exit behavior

- `0`: the scan completed, including a scan that found zero records.
- `1`: the file or folder argument is missing (usage is printed); the resolved source type has no JSON/XML introspector, printing `ERROR  'paths' supports JSON and XML; '<type>' is not one.` to stderr; or the scan failed with an engine error, printed as `ERROR  <message>` to stderr with secret values redacted. An invalid `--root` XPath fails this way with `Invalid rowXPath '<expr>' for '<file>': <cause>`, a target that does not exist with `Path not found: '<path>'.`, and a folder in which no file matches the glob with `No files under '<folder>' matched the filters (pattern '<glob>').`. The zero-record message under exit `0` is reserved for files that matched but yielded no records.
- `-h` / `--help` prints the usage text; the exit code is `0` when the file or folder argument is also present, `1` when it is missing.

## See also

- [sqlflow discover](discover.md): the same scan driven by a pipeline YAML, reporting the columns the configured flatten will produce.
- [sqlflow flatten](flatten.md): emit the full flatten formula (a runnable flow stub) or dump the flattened rows as CSV.
- [JSON and XML flattening](../concepts/json-xml-flattening.md): how value, container, and repeating paths map onto flatten options.
- [Exploring JSON and XML files](../guides/explore-json-xml.md): a walkthrough from `paths` to a running flow.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
