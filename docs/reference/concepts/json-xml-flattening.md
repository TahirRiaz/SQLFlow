---
id: concept-json-xml-flattening
title: "JSON and XML flattening: paths, arrays, aliases, introspection"
type: concept
summary: How the path-based flatteners turn nested JSON and XML into flat columns, handle arrays, coalesce renamed fields, and expose introspection.
keywords:
  - jsonpath
  - xpath
  - arrayhandling
  - explode
  - pathaliases
  - iflattenintrospector
  - collisions
  - columnmappings
related:
  - source-type-json
  - source-type-xml
  - cli-flatten
  - guide-explore-json-xml
sourceRefs:
  - src/SqlFlow.Sources/Json/JsonFlattenConfig.cs
  - src/SqlFlow.Sources/Json/JsonPathFlattener.cs
  - src/SqlFlow.Sources/Json/JsonFlattenFormula.cs
  - src/SqlFlow.Sources/Json/JsonPathInventory.cs
  - src/SqlFlow.Sources/Xml/XmlFlattenConfig.cs
  - src/SqlFlow.Sources/Xml/XmlPathFlattener.cs
  - src/SqlFlow.Sources/Xml/XmlFlattenFormula.cs
  - src/SqlFlow.Sources/Xml/XmlRecordReader.cs
  - src/SqlFlow.Sources/FlattenIntrospection.cs
  - src/SqlFlow.Sources/JsonSourceReader.cs
  - src/SqlFlow.Sources/XmlSourceReader.cs
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Core/Model/PreIngestionJsn.cs
  - src/SqlFlow.Core/Model/PreIngestionXml.cs
  - src/SqlFlow.Cli/Program.cs
---

# JSON and XML flattening: paths, arrays, aliases, introspection

Nested JSON and XML documents do not map naturally onto SQL Server tables. SQLFlow bridges the gap with two declarative, path-based flatteners: `JsonFlattenConfig` plus `JsonPathFlattener` for JSON (src/SqlFlow.Sources/Json), and `XmlFlattenConfig` plus `XmlPathFlattener` for XML (src/SqlFlow.Sources/Xml). Both share the same rules model: path expressions decide which subtrees become columns, which are kept whole as strings, which are dropped, and which arrays multiply into rows. Every emitted value is a raw string; the downstream type-inference step owns all typing (see [type inference](type-inference.md)).

Both readers also implement one introspection interface, `IFlattenIntrospector` (src/SqlFlow.Sources/FlattenIntrospection.cs), so the `discover`, `paths`, and `flatten` CLI commands can explore either format through a single code path.

## Path expressions

JSON paths are JSONPath-like: `$` is the record root, dot segments address object keys, and `[i]` or `[*]` subscripts address array elements (`$.details[*].track_id`). XML paths are XPath-like and relative to the record element: `/a/b` addresses nested elements, `/@name` addresses an attribute, and `[i]`/`[*]` subscripts address repeating same-named siblings.

Two matching rules apply when a runtime path is tested against a configured pattern:

- Concrete indices normalize to the wildcard form: `$.a[0].b` and `$.a[7].b` both match a pattern written as `$.a[*].b`. Only a purely numeric or `*` subscript normalizes; anything else inside brackets is kept literal.
- A single bare `*` in a pattern is a prefix/suffix glob (`$.raw_*` matches `$.raw_payload`). A bracketed `[*]` is only an array wildcard, never a free glob, so a pattern like `$.items[*]` cannot accidentally match an unrelated key that merely contains brackets.

## Column naming

A column name derives from the path unless overridden:

1. An explicit `columnMappings` entry wins. For JSON the mapping key must match the path exactly as written; for XML the key is index-normalized (`[0]`, `[1]` fold to `[*]`), so a mapping written against the wildcard form the discovery output prints still applies at load time.
2. A `pathAliases` entry wins next (see below).
3. Otherwise: the root marker (`$.` or leading `/`) is stripped, every `[...]` subscript is removed (so `$.details[0].track_id` and `$.details[7].track_id` share the column `details_track_id`), dots or slashes fold onto the separator (default `_`), invalid characters are replaced by the separator, runs of the separator collapse to one and are trimmed from the ends, a leading digit gets a `_` prefix, and an empty result becomes `column`. Casing is preserved (SQL Server compares case-insensitively, and mixed case reads better).

For XML, attributes take the configured `attributePrefix` (default `@`) so an attribute can stay distinct from a same-named element on the same parent.

`columnMappings` is written as `jsonPath=columnName` pairs separated by `;`. A malformed pair fails with:

```text
Invalid columnMappings entry '<pair>'. Use 'jsonPath=columnName' separated by ';'.
```

(the XML parser says `xpath=columnName`).

Within one JSON record, duplicate column names are last-write-wins, compared case-insensitively: two source keys differing only in case (`Id` and `id`) fold onto one column and the later value silently overwrites the earlier (the `flatten` command's generated `columnMappings` is what turns this into a lossless flatten; see collision resolution below). The XML flattener never loses data to a collision this way: it de-collides two distinct paths that resolve to the same column name (again compared case-insensitively) into separate columns as part of the flatten itself, the first path keeping the natural name and each later one suffixed `_2`, `_3`, ..., with no `columnMappings` needed.

## Value conversion

Values pass through as raw strings, never re-typed by this layer:

- Numbers keep their original JSON token (no parse/print round-trip, so precision and leading/trailing significance survive).
- `true`/`false` become the strings `true`/`false`.
- `null` becomes SQL NULL.
- Objects or arrays kept whole (via `jsonPaths`/`xmlPaths` or the depth limit) are serialized as compact JSON (or an XML fragment).

Anything nested deeper than `maxDepth` (default 10) is captured whole as a JSON-string (or XML-fragment) column instead of being flattened further.

Distinct from `maxDepth`, which only decides where flattening stops, each reader also enforces a hard parse-time nesting guard against pathological documents. The XML reader rejects any document whose elements nest deeper than 1000 levels with an explicit error (`XmlRecordReader.MaxNestingDepth`), telling the operator to pre-split or reduce nesting rather than risk unbounded recursion. JSON record parsing caps at a depth of 256 (`JsonRecordReader`'s `JsonDocumentOptions.MaxDepth`); a document past that limit fails to parse. Neither limit is configurable, and both sit well above any realistic `maxDepth`.

## Array handling (JSON) and repeat handling (XML)

`source.options.arrayHandling` decides what happens to a JSON array that is not an explicit explode target. Parsing is case-insensitive and ignores `_` and `-`:

| Value | Aliases | Behavior |
|---|---|---|
| `to_json` (default) | `asjson`, `json` | Keep the array as one compact JSON-string column (lossless). |
| `first_element` | `first` | Flatten element 0 in place; its fields become columns under the array's path. An empty array yields a NULL column. |
| `join` | `joincomma` | Join the array's scalar elements with `joinSeparator` (default `,`) into one string. |
| `count` | | Emit the element count as the column value. |
| `skip` | | Drop the array entirely; no column. |
| `explode` | `unnest` | Every array emits one output row per element. |

An unknown value fails with:

```text
Unknown arrayHandling '<value>'. Use to_json, first_element, join, count, skip, or explode.
```

XML's counterpart is `source.options.repeatHandling` for repeating same-named sibling elements. It accepts `to_xml` (default; aliases `asxml`, `xml`, and `tojson`), `first_element`/`first`, `last_element`/`last` (XML only), `join`/`joincomma`, `count`, `skip`, and `explode`/`unnest`. An unknown value fails with:

```text
Unknown repeatHandling '<value>'. Use to_xml, first_element, last_element, join, count, skip, or explode.
```

### explodePaths

`source.options.explodePaths` (comma-separated) explodes specific arrays into one output row per element, like SQL UNNEST, while other arrays keep the global handling:

- Several exploded arrays in one record cross-product: rows are cloned per element of each exploded array.
- An empty exploded array keeps the parent row with the element columns NULL (left-join semantics); the record is never dropped.
- A safety bound caps the cross-product: more than 1,000,000 rows from a single record fails with `Exploding '<path>' produced more than 1000000 rows for a single record. Narrow the explode paths or pre-split the data.`
- An exploded array is consumed into rows; it does not also produce a JSON-string column.

The flattener exposes two views of a record: `Flatten` is the schema view (one row carrying every column the record can produce, exploded arrays visited without multiplying rows, used to discover a file's columns) and `FlattenRows` is the data view (the actual output rows).

## Selecting subtrees: includePaths, excludePaths, jsonPaths/xmlPaths

- `includePaths` is a whitelist: when non-empty, only the listed paths (and everything under them) are flattened. Ancestors on the way to a whitelisted leaf are still traversed, so `includePaths: "$.keep.city"` works without listing `$.keep`. Paths listed in `jsonPaths`/`xmlPaths` are implicitly included.
- `excludePaths` drops a subtree entirely; no column is produced for the path or anything under it.
- `jsonPaths` (XML: `xmlPaths`) keeps a subtree verbatim as one compacted JSON-string (XML-fragment) column instead of flattening its children. Useful for irregular or schemaless blobs stored as-is; type such columns `nvarchar(max)`.

All three take comma-separated path lists and support the wildcard rules above.

## Schema-evolution aliases: pathAliases

`source.options.pathAliases` maps several version-specific source paths onto one output column, reconciling a field that was renamed or moved between dataset versions. Format, one group per column, groups separated by `;`, paths separated by `|`:

- JSON: `columnName=$.path1|$.path2; column2=$.pathA|$.pathB`
- XML: `col=/a|/b; col2=/c|/d`

A malformed group fails with `Invalid pathAliases entry '<group>'. Use 'columnName=$.path1|$.path2' separated by ';'.` (the XML variant says `'columnName=/path1|/path2'`).

Semantics:

- Aliased values coalesce. A later missing or null source path never overwrites a non-null value another path already supplied, so mixed v1/v2 files in one load fill the same column.
- Array indices inside alias paths are normalized to `[*]`, so aliases apply to repeating elements.
- In the XML flattener, alias target column names are reserved up front (`XmlFlattenConfig.AliasTargetColumns`): a natural field whose own name resolves to an alias target is de-collided into its own column, regardless of document order, instead of colliding with and dropping the aliased value.

Example, adapted from samples/json/json-schema-evolution.flow.yaml (v1 records have `{ id, name }`, v2 records have `{ id, fullName, email }`):

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

Both versions feed one `person_name` column; the additive column union null-fills `email` for v1 rows.

## Introspection: inventory, formula, collision resolution

`JsonSourceReader` and `XmlSourceReader` implement `IFlattenIntrospector`, the single interface behind the `discover`, `paths`, and `flatten` CLI commands. `IntrospectAsync` scans the same files a load would select (same glob, mask, and date filters) and returns a `FlattenIntrospection` with three parts:

- **SchemaInventory**: every addressable path found across the sample, each with a kind (`Value`, `Container`, or `Repeating`) and how many sampled records contained it. Container and repeating paths matter because they are the valid targets for `rootPath`/`rowXPath`, `jsonPaths`/`xmlPaths`, `excludePaths`, and `explodePaths`.
- **SchemaFormula**: the resolved output columns, each with its source path and an is-large-text flag, plus the collision mappings needed to keep the flatten lossless.
- **Options**: the key/value pairs that reproduce the flatten configuration.

Collision resolution in the formula (src/SqlFlow.Sources/Json/JsonFlattenFormula.cs):

- Two unrelated paths can fold onto one column name (`$.vendor_id` and `$.vendor.id` both become `vendor_id`); under a plain flatten the later silently overwrites the earlier. The formula keeps the first at its natural name and remaps the rest to `name_2`, `name_3`, ... via explicit `columnMappings`, making the flatten lossless.
- Sometimes-array fields (`$.a.b` in one record, `$.a[*].b` in another) are recognized as the same field and converge to one column instead of colliding.
- Aliased (`pathAliases`) paths intentionally share one output column and are never remapped.

XML never needs this pass: `XmlFlattenFormulaBuilder` (src/SqlFlow.Sources/Xml/XmlFlattenFormula.cs) unions the schema columns `XmlPathFlattener` already resolved, and that flattener resolves name collisions itself (see Column naming above), so an XML `SchemaFormula` never carries collision mappings.

Columns flagged as large text (`IsLargeText` / `IsJsonText`) hold whole arrays or kept subtrees; a generated formula types them `nvarchar(max)` via `schema.overrides` because their values can be arbitrarily large.

Scans are bounded by the `maxFiles`, `maxRecords`, and `maxDepth` arguments; a depth below 1 falls back to 10.

## Configuration touchpoints

YAML keys under `source.options` (JSON keys from src/SqlFlow.Core/Model/PreIngestionJsn.cs, XML from src/SqlFlow.Core/Model/PreIngestionXml.cs):

| Key | Format | Default | Purpose |
|---|---|---|---|
| `rootPath` | JSON | `$` | JSONPath the records live under. |
| `rowXPath` | XML | empty | XPath selecting the record elements (`hierarchyIdentifier` is accepted as a fallback key). |
| `includePaths` | both | unset | Comma-separated whitelist. |
| `excludePaths` | both | unset | Comma-separated subtrees to drop. |
| `jsonPaths` / `xmlPaths` | JSON / XML | unset | Subtrees kept verbatim as one string column. |
| `explodePaths` | both | unset | Arrays / repeating elements exploded into rows. |
| `pathAliases` | both | unset | Version-specific paths coalesced onto one column. |
| `columnMappings` | both | unset | Explicit path-to-column overrides. |
| `separator` | both | `_` | Column-name separator. |
| `maxDepth` | both | `10` | Nesting depth flattened into columns. |
| `arrayHandling` / `repeatHandling` | JSON / XML | `to_json` / `to_xml` | Fallback array / repeat behavior. |
| `joinSeparator` | both | `,` | Separator for the `join` handling. |
| `includeAttributes` | XML | `true` | Whether attributes become columns. |
| `attributePrefix` | XML | `@` | Prefix on attribute column names. |
| `stripNamespacePrefixes` | XML | `true` | Strip namespaces before path matching. |

CLI commands (src/SqlFlow.Cli/Program.cs):

- `sqlflow discover <pipeline.yaml>` introspects the flow's JSON or XML source and prints paths, columns, and options (`--max-files` default 100, `--max-records` 0 for unlimited, `--max-depth` 10).
- `sqlflow paths <file|folder>` points straight at a JSON/NDJSON/XML file or folder (no pipeline YAML) and lists every addressable path; `--values` prints only value paths, one per line (`--max-depth` default 20).
- `sqlflow flatten <file|folder>` emits the flatten formula as a runnable flow stub with collision-resolved `columnMappings` and `nvarchar(max)` overrides for large-text columns; `--data` dumps the flattened rows as CSV instead (`--provenance` keeps the `_DW` columns). Flatten rules are passed with `--root`, `--include`, `--exclude`, `--explode`, `--keep` (or `--json`/`--xml`), `--aliases`, `--array`/`--repeat`, `--separator`, `--join-separator`, and `--map`; `--pattern` and `-r`/`--recursive` control folder scans, `-o`/`--out` writes to a file.

Example: explore a folder, then generate and run the lossless formula.

```bash
sqlflow paths ./data/orders --pattern "*.json" --max-records 500
sqlflow flatten ./data/orders --explode "$.lines" --exclude "$.debug" -o orders.flow.yaml
sqlflow run orders.flow.yaml
```

Example flow using explode (samples/json/json-explode.flow.yaml):

```yaml
name: Json_Explode
source:
  type: json
  location: ./data/lineitems.json
  options:
    explodePaths: "$.lines"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_Explode
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

Two orders with 2 + 1 line items produce 3 rows: the order fields repeat on every line and `lines_sku`/`lines_qty` vary per row.

## See also

- [JSON source type](../flow/source-types/json.md)
- [XML source type](../flow/source-types/xml.md)
- [flatten command](../cli/flatten.md)
- [paths command](../cli/paths.md)
- [discover command](../cli/discover.md)
- [Type inference](type-inference.md)
- [Pre-ingestion transform](pre-ingestion-transform.md)
