---
id: cli-flatten
title: sqlflow flatten
type: cli-command
summary: Point at a JSON or XML file or folder to emit a runnable flow stub (the flatten formula) or, with --data, dump the flattened rows as CSV.
keywords:
  - flatten
  - formula
  - flow stub
  - csv preview
  - column mappings
  - arrayhandling
  - repeathandling
cliCommand: flatten
related:
  - cli-paths
  - cli-discover
  - concept-json-xml-flattening
  - guide-explore-json-xml
  - concept-cli-conventions
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.Sources/Json/JsonFlattenConfig.cs
  - src/SqlFlow.Sources/Xml/XmlFlattenConfig.cs
---

# sqlflow flatten

## Synopsis

```bash
sqlflow flatten <file|folder> [--out|-o <file>] [flatten rule flags] [--pattern <glob>] [-r]
sqlflow flatten <file|folder> --data [--provenance] [--max-records <n>] [--out|-o <file>] [flatten rule flags] [--pattern <glob>] [-r]
```

## Description

`flatten` points straight at a JSON or XML file or folder; no pipeline YAML is needed. It has two modes:

- **Default (formula) mode** emits the flatten "formula": a complete, runnable file-flow YAML stub with the resolved column schema. Column-name collisions are fixed via explicit `columnMappings` so the flatten stays lossless. Write it to a file with `--out`, fill in `target.connection` (or rely on the `${env:SQLFlowSinkConStr}` default), then `sqlflow run` it.
- **`--data` mode** skips the formula and streams the actual flattened rows as CSV, so you can preview the result without a database.

The source format is taken from the file extension: `.xml` is XML; `.ndjson` and `.jsonl` keep their extension as the source type; everything else is treated as JSON. For a folder, the format comes from `--pattern` (a pattern ending in `.xml` means XML, otherwise JSON) and the pattern becomes the `srcFile` glob; without `--pattern` a folder is treated as JSON with the glob `*.json`. Only JSON and XML sources qualify; anything else fails with `ERROR  'flatten' supports JSON and XML; '<type>' is not one.`

All flatten rule flags are threaded into the synthesized source's options using the format's own option keys, so the flags shape both the formula and the `--data` preview identically.

## Arguments

| Argument | Required | Description |
| --- | --- | --- |
| `<file\|folder>` | yes | A JSON or XML file, or a folder of them. Omitting it prints usage and exits 1. |

## Options

| Flag | Type | Default | Description |
| --- | --- | --- | --- |
| `--data` | switch | off | Output the flattened rows as CSV instead of the formula. |
| `--provenance` | switch | off | Keep the `_DW` provenance columns in `--data` output. Without it, `includeFileName`, `includeFileDate`, `includeFileRowDate`, `includeFileSize`, `includeDataSet`, and `includeRowNumber` are all forced to `false`. |
| `-o`, `--out <file>` | path | stdout | Write the formula or the CSV to a file instead of stdout. |
| `--root <path>` | string | none | Records live under this path. Maps to `rootPath` (JSON) or `rowXPath` (XML). |
| `--include <paths>` | string | none | Comma-separated whitelist of paths to flatten. Maps to `includePaths`. |
| `--exclude <paths>` | string | none | Comma-separated paths whose subtree is dropped. Maps to `excludePaths`. |
| `--explode <paths>` | string | none | Comma-separated array or repeating-element paths to explode into one row per element. Maps to `explodePaths`. |
| `--keep <paths>` | string | none | Keep subtrees as one string column each. Maps to `jsonPaths` (JSON) or `xmlPaths` (XML) by format. |
| `--json <paths>` | string | none | Explicitly set `jsonPaths`. |
| `--xml <paths>` | string | none | Explicitly set `xmlPaths`. |
| `--aliases <col=/a\|/b; ...>` | string | none | Schema-evolution aliases mapping several paths to one column. Maps to `pathAliases`. |
| `--map <path=col;...>` | string | none | Column-name overrides. Maps to `columnMappings`. |
| `--array <handling>` | enum | `to_json` | JSON array handling: `to_json`, `first_element`, `join`, `count`, `skip`, or `explode`. Maps to `arrayHandling`. |
| `--repeat <handling>` | enum | `to_xml` | XML repeating-element handling: `to_xml`, `first_element`, `last_element`, `join`, `count`, `skip`, or `explode`. Maps to `repeatHandling`. |
| `--separator <s>` | string | `_` | Separator joining nested names into a column name. Maps to `separator`. |
| `--join-separator <s>` | string | `,` | Separator used by `join` handling. Maps to `joinSeparator`. |
| `--pattern <glob>` | glob | `*.json` | File glob when the target is a folder; also selects the format by its extension (a pattern ending in `.xml` selects XML). |
| `-r`, `--recursive` | switch | off | Recurse into sub-folders (sets `searchSubDirectories: true`). |
| `--max-files <n>` | int | 100 | Files to scan when building the formula. |
| `--max-records <n>` | int | 0 | Formula mode: records to scan (0 = all). `--data` mode: caps the CSV rows written (0 = all). |
| `--max-depth <n>` | int | 20 | Maximum nesting depth to inspect in formula mode. |

A value-taking flag followed by another flag is ignored rather than swallowing the flag as its value (a lone `-` is still accepted, for example as a separator).

## Behavior and output

### Formula mode (default)

The emitted stub contains, in order:

- A comment header with the source file name, column count, records scanned, and files scanned, plus the instruction to set `target.connection` and `sqlflow run` the file.
- `name:` sanitized from the file name (non-alphanumeric characters become `_`, leading and trailing `_` trimmed, a leading digit gets a `_` prefix).
- `source:` with `type`, `location`, and `options` holding the resolved flatten configuration (for JSON always `rootPath`, `separator`, and `arrayHandling`; plus any of `includePaths`, `excludePaths`, `jsonPaths`, `explodePaths`, `pathAliases` that are set).
- `columnMappings` (inside `source.options`) merging your `--map` value with the auto-resolved collision mappings, so two paths that fold onto the same column name both survive: the first path keeps the natural name and each later colliding path is remapped to a suffixed column such as `vendor_id_2`.
- `target:` with `connection: ${env:SQLFlowSinkConStr}`, `schema: dbo`, and `table:` set to the sanitized name.
- `schema:` with `defaultColumnType: nvarchar(4000)`, plus `overrides:` typing each large-text column (whole arrays kept as JSON, kept subtrees) as `nvarchar(max)`.
- `load:` with `mode: truncate-load`.
- A comment block listing every column and its source path (`column <- source path`) and, when collisions were resolved, a comment noting how many.

With `--out` the command confirms `Wrote flatten formula (N column(s)) to <path>`; without it the stub goes to stdout.

### --data mode

The reader opens the synthesized source and writes CSV: a header row of column names, then one line per row. Cells containing a comma, double quote, CR, or LF are wrapped in double quotes with embedded quotes doubled; NULL becomes an empty string. `--max-records` caps the row count (0 = all). Provenance `_DW` columns are suppressed unless `--provenance` is passed. With `--out` the command confirms `Wrote N row(s), M column(s) to <path>`.

## Examples

Generate a formula from a sample file and write it next to your flows:

```bash
sqlflow flatten samples/json/data/orders.json -o orders.flow.yaml
# Wrote flatten formula (6 column(s)) to orders.flow.yaml
sqlflow run orders.flow.yaml
```

Preview the flattened rows as CSV, joining the scalar array instead of keeping it as JSON:

```bash
sqlflow flatten samples/json/data/orders.json --data --array join --max-records 20
```

Flatten XML files, then explode a repeating element into one row per element:

```bash
sqlflow flatten samples/xml/data/orders.xml --data
sqlflow flatten samples/xml/data/purchase-orders.xml --explode /line -o po.flow.yaml
```

Scan a folder of JSON files recursively and shape the formula with rules:

```bash
sqlflow flatten data/exports --pattern '*.json' -r --exclude $.debug --keep $.payload -o exports.flow.yaml
```

## Exit behavior

- `0` on success in either mode.
- `1` when the file or folder argument is missing (usage is printed).
- `1` with `ERROR  'flatten' supports JSON and XML; '<type>' is not one.` when the source type has no flatten introspector (formula mode).
- `1` with `ERROR  no reader handles '<type>'.` when no source reader accepts the type (`--data` mode).
- `1` when `--array` has an invalid value on a JSON source, or `--repeat` has an invalid value on an XML source (each format reads only its own handling option; the other format's flag is ignored). The failure surfaces when the configuration is built, as `ERROR  Unknown arrayHandling '<value>'. Use to_json, first_element, join, count, skip, or explode.` or `ERROR  Unknown repeatHandling '<value>'. Use to_xml, first_element, last_element, join, count, skip, or explode.`

## See also

- [sqlflow paths](paths.md): list every addressable path in a JSON or XML file.
- [sqlflow discover](discover.md): report the columns a pipeline's flatten will produce, plus schema drift.
- [JSON and XML flattening](../concepts/json-xml-flattening.md): the flattening model behind these options.
- [Exploring JSON and XML files](../guides/explore-json-xml.md): the authoring workflow from raw file to running flow.
- [CLI conventions](../concepts/cli-conventions.md): argument parsing and exit codes shared by every command.
