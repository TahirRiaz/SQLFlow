---
id: guide-explore-json-xml
title: Explore, shape, and load nested JSON/XML data
type: guide
summary: Workflow for nested JSON/XML - list paths, preview flattened rows, generate a runnable flow stub, run it, then infer types.
keywords:
  - paths
  - discover
  - flatten
  - workflow
  - preview data
  - nested data
  - jsonpath
  - xpath
related:
  - cli-paths
  - cli-flatten
  - cli-discover
  - cli-infer
  - concept-json-xml-flattening
  - source-type-json
sourceRefs:
  - src/SqlFlow.Cli/Program.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Yaml/FlowYaml.cs
  - src/SqlFlow.Yaml/InferSpecLoader.cs
  - samples/json/README.md
  - samples/xml/README.md
  - samples/json/json-schema-evolution.flow.yaml
  - samples/infer/orders.infer.yaml
---

# Explore, shape, and load nested JSON/XML data

This guide walks a nested JSON or XML dataset from "unknown file on disk" to a typed SQL Server table. The whole loop uses three exploration commands plus `run` and `infer`:

1. `sqlflow paths <file|folder>` lists every addressable path in the data, with the column each value would become.
2. `sqlflow flatten <file|folder> --data` previews the flattened rows as CSV, no database needed.
3. `sqlflow flatten <file|folder> -o my.flow.yaml` emits the flatten formula: a complete, runnable flow stub.
4. Edit `target.connection` (or rely on the `${env:SQLFlowSinkConStr}` default) and `sqlflow run my.flow.yaml`.
5. Text-format loads land raw strings; run inference afterwards to get typed columns.

`paths` and `flatten` point straight at a file or folder; no pipeline YAML is needed. `discover` is the third view, run against an existing pipeline file, using the source type the pipeline already declares. All three support JSON, NDJSON, and XML. For `paths` and `flatten`, the format is taken from the file extension (`.xml` is XML, `.ndjson` and `.jsonl` are newline-delimited JSON, everything else parses as JSON), or from `--pattern` when the target is a folder.

The exploration commands resolve files through the same code path a real load uses (glob, path mask, date window, incremental watermark; see `ResolveFilesAsync` in src/SqlFlow.Sources/FileSourceReaderBase.cs). What you see during exploration is exactly what the load reads.

## Practice data

The repository ships files to try this on:

| Path | Shape |
| --- | --- |
| `data/jsn/customers.json` | A flat array of objects |
| `data/jsn/product.json` | A nested `vendor` object plus a `details` array of objects |
| `data/jsn/cia-factbook/` | Deeply nested, one country per file, recursive sub-folders |
| `samples/json/data/envelope.json` | Records nested under an envelope (`$.data.records`) |
| `samples/xml/data/purchase-orders.xml` | Orders with repeating `line` elements |

## Step 1: list the paths

```bash
sqlflow paths data/jsn/product.json
sqlflow paths data/jsn/cia-factbook -r          # recurse a folder of *.json files
sqlflow paths data/jsn/customers.json --values  # column paths only, one per line
```

The default output is a table of every path found, one row per path:

- **PATH**: the JSONPath (JSON) or `/`-style path (XML).
- **KIND**: `value`, `container`, or `repeating`. Value paths become columns; container and repeating paths are the targets for `--root`, keep-as-string (`--keep`), `--exclude`, and `--explode`.
- **COLUMN**: the column name the value would flatten to (`-` for containers).
- **PRESENCE**: how many of the scanned records contained the path (`all`, or `n/total`).

When two paths fold onto the same column name under the default flatten, `paths` reports each collision explicitly ("last value wins") and suggests disambiguating with `columnMappings` or a different separator. A `[*]` path becomes a column when its repeating parent is exploded (one row per element).

`--values` prints only the value paths, one per line, for piping into other tools. Scanning is bounded by `--max-files` (default 100), `--max-records` (default 0, meaning all), and `--max-depth` (default 20 for `paths` and `flatten`).

## Step 2: preview the flattened rows

`flatten --data` runs the real flattener and dumps the resulting rows as CSV to stdout (or to a file with `-o`). No database connection is involved. The `_DW` provenance columns are suppressed for a clean preview unless you pass `--provenance`.

```bash
sqlflow flatten data/jsn/product.json --data --max-records 20
sqlflow flatten samples/xml/data/purchase-orders.xml --data --explode /line
```

This is the fastest way to check that a shaping decision (root path, explode, keep-as-string) produces the rows you expect before anything touches SQL Server.

## Step 3: generate the flatten formula

Without `--data`, `flatten` emits the formula: a complete runnable flow stub containing the resolved `source.options`, the full column list as comments (column, and the source path it came from), name collisions fixed via generated `columnMappings` so the flatten is lossless, and every large-text column typed `nvarchar(max)`.

```bash
sqlflow flatten data/jsn/product.json -o product.flow.yaml
```

The generated stub has this shape:

```yaml
# Flatten formula for product.json - N column(s), N record(s), 1 file(s).
# Generated by 'sqlflow flatten'. Set target.connection, then 'sqlflow run' this file.
name: product
source:
  type: json
  location: data/jsn/product.json
  options:
    # the resolved flatten options, including any collision-fixing columnMappings
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: product
schema:
  defaultColumnType: nvarchar(4000)
  overrides:
    # every large-text column (kept subtree, or whole array):
    #   <column>:
    #     type: nvarchar(max)
load:
  mode: truncate-load
# columns (column <- source path):
#   ...
```

Columns that keep a whole array or subtree as one string (via `jsonPaths`/`xmlPaths`, or whole-array `arrayHandling: to_json`) can be arbitrarily large. The formula already types them `nvarchar(max)` under `schema.overrides`; keep that when you edit the file. Separately, at load time a subtree nested deeper than `maxDepth` (default 10) is captured whole as one JSON-string or XML-fragment column instead of being flattened further; for data nested that deeply, re-check the generated column list (and widen its type if needed) after the first run.

### Shaping flags

`flatten` accepts the flatten rules inline. Each flag maps onto the format's `source.options` key, so the generated formula carries your shaping decisions:

| Flag | Option key (JSON / XML) | Meaning |
| --- | --- | --- |
| `--root <path>` | `rootPath` / `rowXPath` | Where the records live |
| `--include <paths>` | `includePaths` | Comma-separated whitelist |
| `--exclude <paths>` | `excludePaths` | Drop these subtrees |
| `--explode <paths>` | `explodePaths` | One row per array/repeating element |
| `--keep <paths>` | `jsonPaths` / `xmlPaths` | Keep a subtree as one string column |
| `--json <paths>` | `jsonPaths` | JSON keep-as-string, named explicitly |
| `--xml <paths>` | `xmlPaths` | XML keep-as-string, named explicitly |
| `--aliases <col=/a\|/b; ...>` | `pathAliases` | Many version-specific paths, one column |
| `--array <mode>` | `arrayHandling` | `to_json`, `first_element`, `join`, `count`, `skip`, `explode` |
| `--repeat <mode>` | `repeatHandling` | `to_xml`, `first_element`, `last_element`, `join`, `count`, `skip`, `explode` |
| `--separator <s>` | `separator` | Column-name separator (default `_`) |
| `--join-separator <s>` | `joinSeparator` | Separator for `join` handling |
| `--map <path=col;...>` | `columnMappings` | Column-name overrides |

Folder targets take `--pattern <glob>` (default `*.json`, or `*.xml` when the pattern ends in `.xml`) and `-r`/`--recursive` to recurse sub-folders. Scanning is bounded by `--max-files` (default 100), `--max-records` (default 0 = all), and `--max-depth` (default 20).

Example: records nested under an envelope, reached with `--root`:

```bash
sqlflow paths samples/json/data/envelope.json
sqlflow flatten samples/json/data/envelope.json --root '$.data.records' -o records.flow.yaml
```

## Step 4: run it

```bash
export SQLFlowSinkConStr="Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
sqlflow run product.flow.yaml
```

The generated formula's target connection defaults to `${env:SQLFlowSinkConStr}`; either set that variable (the process environment or a git-ignored `.sqlflow/env` file next to the document) or edit `target.connection` to your own reference. Secrets never go in the file itself.

## Step 5: type the columns

Every value from a text format lands as a raw string. Two ways to get typed columns:

- Write a standalone `.infer.yaml` spec (its own shape, separate from a pipeline flow: `connection`, `table` (schema-qualified like `dbo.product`, or one-part with a separate `schema` key), and optionally `onConvertError`, `threshold`, `sample`, `preserveLeadingZeros`, `culture`) pointing at the table the flow just loaded, then run `sqlflow infer <spec.infer.yaml>`. A pipeline flow document cannot be passed to `infer` directly: it has no root-level `connection`/`table` keys and fails spec validation. The command profiles the table and outputs an inference report as JSON (typed columns plus a transform SELECT); `-o` writes it to a file, `--no-validate` skips the validation pass that checks per-column fit against the loaded data. Adapted from samples/infer/orders.infer.yaml:

  ```yaml
  connection: ${env:SQLFlowSinkConStr}
  table: dbo.product

  onConvertError: silentNull     # silentNull | fail | keepString
  threshold: 1.0                 # fraction of non-null values that must convert
  sample: 0                      # 0 = full scan
  preserveLeadingZeros: true
  culture: nb-NO                 # optional BCP-47 locale override; omit to use the server's locale
  ```

  `culture` is optional: it overrides the locale used for type inference with the named BCP-47 culture (for example `nb-NO`), so locale-formatted numbers and dates (decimal comma, `dd.MM.yyyy`) are interpreted correctly. When omitted, the run binds to the locale configured on the target SQL Server; an unrecognised culture name fails the run (see `InferSpecLoader`/`InferYaml` in src/SqlFlow.Yaml/InferSpecLoader.cs and the resolution in `InferenceService.ResolveLocaleAsync`).

- Or set `transform.inferTypes: true` in the flow so typing happens as part of the pipeline (see src/SqlFlow.Yaml/FlowYaml.cs for the full `transform` block: `inferTypes`, `onConvertError`, `threshold`, `sample`, `preserveLeadingZeros`, `generateView`, `columns`).

Do not retype the `nvarchar(max)` columns that hold whole arrays or kept subtrees; they are intentionally unbounded.

## Datasets whose shape changed over time

When a folder mixes file versions (a field was renamed between v1 and v2), add `pathAliases` so both shapes fill the same column in one process. A missing version-specific path is filled by another alias, never left null for the wrong reason. From samples/json/json-schema-evolution.flow.yaml:

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

Here v1 records carry `$.name` and v2 records carry `$.fullName`; both feed one `person_name` column. Purely additive fields need no alias: the union gives the table every column and the older shape null-fills the newer fields.

The same flag exists inline: `sqlflow flatten <folder> --aliases "person_name=$.name|$.fullName"`.

## Checking an existing pipeline: discover

Once a pipeline file exists, `sqlflow discover <pipeline.yaml>` is the config-authoring view against it: the paths and columns the flatten will produce, a schema-drift warning when a path is missing from some files ("a path missing from a file becomes NULL for that file's rows (reconcile renames with pathAliases)"), and a ready-to-paste starter `source.options` block.

```bash
sqlflow discover samples/json/json-basic.flow.yaml
```

`discover` supports JSON and XML sources only and scans with `--max-files` (default 100), `--max-records` (default 0 = all), and `--max-depth` (default 10 for `discover`, versus 20 for `paths`/`flatten`). It exits 1 with an error when the pipeline's source type is not JSON or XML.

## See also

- [sqlflow paths](../cli/paths.md)
- [sqlflow flatten](../cli/flatten.md)
- [sqlflow discover](../cli/discover.md)
- [sqlflow infer](../cli/infer.md)
- [JSON/XML flattening concepts](../concepts/json-xml-flattening.md)
- [JSON source type](../flow/source-types/json.md)
