# JSON sample pipelines

A showcase of the path-based JSON flattener, the modern replacement for the original
`JsonToDataTableCode` blob. Each `*.flow.yaml` here is a runnable pipeline that loads a JSON or NDJSON
file into a `dbo.Json_*` table in the sink database. They are exercised by
`JsonSampleFlowIntegrationTests`, which drops each table up front and leaves it behind for inspection.

Every value lands as a raw string; run `sqlflow infer` afterwards to propose typed columns
(int / decimal / datetime). Arrays follow `arrayHandling` (or `explodePaths` to emit one row per element,
SQL UNNEST style). Nested keys flatten with a separator (`address.city` becomes `address_city`).

## Running

```
$env:SQLFlowSinkConStr = "Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- run samples/json/json-basic.flow.yaml
```

To author a config against your own files, explore their structure first. Two commands help:

```
# Point straight at a file or folder - no pipeline YAML needed - to list every JSONPath in it,
# including the object/array container paths you target with rootPath / jsonPaths / excludePaths.
dotnet run --project src/SqlFlow.Cli -- paths data/jsn/product.json
dotnet run --project src/SqlFlow.Cli -- paths data/jsn/cia-factbook -r --values   # recurse a folder; print column paths only

# Against a pipeline, report the columns the flatten will produce, schema drift across files,
# and a starter source.options block to paste in.
dotnet run --project src/SqlFlow.Cli -- discover samples/json/json-basic.flow.yaml
```

`paths` shows each path's kind (value / object / array), the column a value would become, how many
records contained it, and any column-name collisions (two paths folding onto one column, where the later
value wins). Use `--values` to print just the column paths, one per line, for piping. `discover` is the
config-authoring view: the columns you will get plus a ready-to-paste `source.options` block.

To get the whole recipe in one shot, ask for the flatten **formula** - a complete, runnable flow stub
with the resolved column schema, name collisions fixed (so it is lossless), and large array/json columns
typed as `nvarchar(max)`:

```
dotnet run --project src/SqlFlow.Cli -- flatten data/jsn/product.json -o product.flow.yaml
# fill in target.connection (or use the ${env:SQLFlowSinkConStr} default), then:
dotnet run --project src/SqlFlow.Cli -- run product.flow.yaml

# or dry-run the actual flattened rows as CSV (no database needed):
dotnet run --project src/SqlFlow.Cli -- flatten data/jsn/product.json --data --max-records 20
```

`flatten` accepts the same rules as inline options so you can shape the formula: `--root`, `--include`,
`--exclude`, `--json`, `--array`, `--separator`, `--map`. The default output is the formula; `--data`
emits the flattened rows (provenance columns suppressed unless you pass `--provenance`).

The `data/jsn` folder has example files to try these on: `customers.json` (a flat array),
`product.json` (a nested vendor object and a details array of objects), and `cia-factbook/` (deeply
nested, one country per file across recursive sub-folders).

## The samples

| File | Shows | Table |
| --- | --- | --- |
| `json-basic.flow.yaml` | Array of objects, nested keys, an array kept as JSON (default) | `Json_Basic` |
| `json-ndjson.flow.yaml` | Newline-delimited JSON, one object per line | `Json_Ndjson` |
| `json-root-path.flow.yaml` | `rootPath` to reach records nested under an envelope | `Json_RootPath` |
| `json-keep-subtree.flow.yaml` | `jsonPaths` to keep a subtree as one JSON-string column | `Json_KeepSubtree` |
| `json-exclude.flow.yaml` | `excludePaths` to drop a subtree entirely | `Json_Exclude` |
| `json-include.flow.yaml` | `includePaths` whitelist (everything else dropped) | `Json_Include` |
| `json-array-join.flow.yaml` | `arrayHandling: join` to flatten a scalar array to a delimited string | `Json_ArrayJoin` |
| `json-array-count.flow.yaml` | `arrayHandling: count` to store the element count | `Json_ArrayCount` |
| `json-array-first.flow.yaml` | `arrayHandling: first_element` to flatten the first element in place | `Json_ArrayFirst` |
| `json-explode.flow.yaml` | `explodePaths` to emit one row per array element (SQL UNNEST) | `Json_Explode` |
| `json-schema-evolution.flow.yaml` | one process over v1/v2 shapes; `pathAliases` reconciles a renamed field | `Json_SchemaEvolution` |
| `json-column-mappings.flow.yaml` | `columnMappings` to rename specific paths | `Json_ColumnMappings` |
| `json-keys.flow.yaml` | `HashKey_DW` and `ConcatKey_DW` synthetic keys over flattened columns | `Json_Keys` |

## Flatten options reference

Set these under `source.options` (all values are strings):

| Option | Meaning |
| --- | --- |
| `rootPath` | JSONPath the records live under, e.g. `$.data.records`. `$` = document root. An array there fans out to one record per element. |
| `includePaths` | Comma-separated whitelist of paths to flatten. Empty = flatten everything. |
| `excludePaths` | Comma-separated paths whose subtree is dropped (no column). |
| `jsonPaths` | Comma-separated paths kept verbatim as a single JSON-string column. |
| `explodePaths` | Comma-separated array paths to explode: each element becomes its own row. Several exploded arrays cross-product. |
| `pathAliases` | Schema-evolution aliases mapping version-specific paths to one column: `col=$.a\|$.b; col2=$.c\|$.d`. A missing source path is filled by another, never nulled. |
| `columnMappings` | Semicolon-separated `jsonPath=columnName` overrides. |
| `separator` | Separator joining nested keys into a column name (default `_`). |
| `maxDepth` | Maximum nesting depth flattened into columns; deeper values become JSON strings (default 10). |
| `arrayHandling` | `to_json` (default), `first_element`, `join`, `count`, or `skip`. |
| `joinSeparator` | Separator used when `arrayHandling` is `join` (default comma). |

A subtree kept whole (`jsonPaths`, or anything past `maxDepth`) can be large; give those flows a roomy
`schema.defaultColumnType` such as `nvarchar(max)`.
