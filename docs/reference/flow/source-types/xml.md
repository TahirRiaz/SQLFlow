---
id: source-type-xml
title: "XML source (source.type: xml)"
type: source-type
summary: "Ingest XML files with the path-based flattener: rowXPath record selection, attribute columns, namespace stripping, repeat handling, and explode."
keywords:
  - xml
  - rowxpath
  - hierarchyidentifier
  - attributes
  - attributeprefix
  - namespaces
  - flatten
  - repeathandling
  - explodepaths
yamlPath: "source.options (type: xml)"
related:
  - concept-json-xml-flattening
  - concept-file-discovery-and-lifecycle
  - source-type-json
  - cli-paths
  - cli-infer
  - guide-explore-json-xml
sourceRefs:
  - src/SqlFlow.Core/Model/PreIngestionXml.cs
  - src/SqlFlow.Sources/XmlSourceReader.cs
  - src/SqlFlow.Sources/Xml/XmlRecordReader.cs
  - src/SqlFlow.Sources/Xml/XmlFlattenConfig.cs
  - src/SqlFlow.Sources/Xml/XmlPathFlattener.cs
  - src/SqlFlow.Sources/Xml/XmlPathInventory.cs
  - samples/xml/README.md
---

# XML source (source.type: xml)

`source.type: xml` reads XML files and flattens each row element into table columns using a declarative, path-based configuration (the modern replacement for the legacy `XmlToDataTableCode` blob). It is handled by `XmlSourceReader` (src/SqlFlow.Sources/XmlSourceReader.cs) and shares one code path with the CSV, XLS, and JSON readers for file selection, schema evolution across files, provenance and synthetic-key columns, and the post-load file lifecycle.

XML's data model is handled directly:

- **Attributes** become columns, addressed as `/@name` (with `includeAttributes: true`, the default).
- **Nested elements** flatten with a separator: `customer/name` becomes column `customer_name`.
- **Namespaces** are stripped to local names by default (`stripNamespacePrefixes: true`).
- **Repeating sibling elements** (XML's arrays) follow `repeatHandling`, or explode into one output row per element via `explodePaths`.

Every value lands as a raw string; run `sqlflow infer` afterwards to propose typed columns.

## Minimal example

```yaml
name: Xml_Basic
source:
  type: xml
  location: ./data/orders.xml
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Xml_Basic
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

With no `rowXPath`, each direct child element of the root is one record (the common wrapped-rows shape).

## Record selection and parsing

Records are selected by `XmlRecordReader` (src/SqlFlow.Sources/Xml/XmlRecordReader.cs):

- Parsing is XXE-safe: DTD processing is prohibited, the external entity resolver is null, and comments, processing instructions, and insignificant whitespace are ignored. Malformed XML raises `Invalid XML in '<file>': <cause>`.
- Documents nesting elements deeper than 1000 levels are rejected with a clear error (`XML in '<file>' nests elements deeper than 1000 levels, ...`) to avoid unbounded recursion.
- An empty file yields zero records.
- Files are fully buffered into memory before parsing.

`rowXPath` values:

| Value | Meaning |
| --- | --- |
| empty, `/*`, or `*` | Each direct child element of the root is a record (default). |
| `.` or `/` | The root element itself is one record (an unwrapped document). |
| anything else | Evaluated as XPath against the document, with local names if `stripNamespacePrefixes` is on (the default). |

A syntactically invalid expression raises `Invalid rowXPath '<x>' for '<file>': ...`. An expression that is valid but does not evaluate to elements (for example `count(...)` or an attribute) raises `rowXPath '<x>' for '<file>' must select elements, not ...`. The legacy key `hierarchyIdentifier` is accepted as an alias for `rowXPath`.

## XML flatten options

Set these under `source.options`; all values are strings. Paths use `/` syntax relative to the row element, with `/@name` for attributes. Defaults come from `PreIngestionXml` (src/SqlFlow.Core/Model/PreIngestionXml.cs).

| Option | Default | Description |
| --- | --- | --- |
| `rowXPath` | `""` | XPath selecting the row elements (see above). Legacy alias: `hierarchyIdentifier`. |
| `includePaths` | unset | Comma-separated whitelist of paths to flatten. Empty = flatten everything. |
| `excludePaths` | unset | Comma-separated paths whose subtree is dropped (no column). |
| `xmlPaths` | unset | Comma-separated paths kept verbatim as a single XML-fragment column. |
| `explodePaths` | unset | Comma-separated repeating-element paths to explode: one output row per element. Multiple paths cross-product. |
| `pathAliases` | unset | Schema-evolution aliases mapping version-specific paths to one column: `col=/a\|/b; col2=/c\|/d` (semicolon-separated groups). |
| `columnMappings` | unset | Semicolon-separated `xpath=columnName` overrides. |
| `separator` | `_` | Separator joining nested names into a column name. |
| `maxDepth` | `10` | Maximum nesting depth flattened into columns; deeper subtrees become XML-fragment strings. Must be a positive integer. |
| `repeatHandling` | `to_xml` | How repeating sibling elements become a column value: `to_xml`, `first_element`, `last_element`, `join`, `count`, `skip`, `explode`. |
| `joinSeparator` | `,` | Separator used when `repeatHandling` is `join`. |
| `includeAttributes` | `true` | Whether XML attributes become columns. |
| `attributePrefix` | `@` | Prefix applied to an attribute's column name. |
| `stripNamespacePrefixes` | `true` | Rewrite element and attribute names to local names. |

### rowXPath

Selects the record elements. The default (empty) treats each direct child of the root as one record; `.` treats the root element itself as one record. Any other value is evaluated as XPath against the document after namespace handling: with the default `stripNamespacePrefixes: true` that document has local names only, so write local names even when the source uses namespaces; with stripping disabled, write the XPath against the original namespaced document instead.

### repeatHandling

Controls what happens when the same element name occurs more than once under one parent and the path is not an explode target:

| Value | Aliases | Effect |
| --- | --- | --- |
| `to_xml` | `asxml`, `xml`, `tojson` | Keep the repeating elements as one XML-fragment string column (lossless). Default. |
| `first_element` | `first` | Flatten only the first element in place. |
| `last_element` | `last` | Flatten only the last element in place. |
| `join` | `joincomma` | Join the elements' text with `joinSeparator` into one string. |
| `count` | | Emit the element count as the column value. |
| `skip` | | Drop the repeating elements entirely (no column). |
| `explode` | `unnest` | One output row per element for every repeating path. |

Parsing is case-insensitive and ignores `_` and `-`. An unknown value raises `Unknown repeatHandling '<v>'. Use to_xml, first_element, last_element, join, count, skip, or explode.`

### explodePaths

Lists specific repeating-element paths that become one output row per element; several exploded paths cross-product. A lone occurrence of an element normally flattens in place, but when its path is an explode target it still routes through explode, so a field that occurs once in some records and many times in others always lands in the same column. Exploding more than 1,000,000 rows from a single record raises `Exploding '<path>' produced more than 1000000 rows for a single record. Narrow the explode paths or pre-split the data.`

### xmlPaths and maxDepth

`xmlPaths` keeps a subtree verbatim as one XML-fragment column instead of flattening its children. Any subtree past `maxDepth` gets the same treatment. These columns are flagged as large text; type them `nvarchar(max)`. A `maxDepth` below 1 raises `Invalid maxDepth '<n>'. Use a positive integer.`

### pathAliases

Schema-evolution aliases feed several version-specific source paths into one output column: `person_name=/name|/fullName`. A missing source path is filled by another; a non-null value is never overwritten with null. A malformed entry raises `Invalid pathAliases entry '<g>'. Use 'columnName=/path1|/path2' separated by ';'.`

### columnMappings

Explicit `xpath=columnName` overrides, semicolon-separated, for example `/order/@id=OrderId;/line[*]/sku=Sku`. Keys are index-normalized, so a mapping written with the `[*]` wildcard form (as printed by `sqlflow paths`) matches the concrete `[0]`, `[1]` indices the runtime produces. A malformed entry raises `Invalid columnMappings entry '<p>'. Use 'xpath=columnName' separated by ';'.`

### Attribute handling

With `includeAttributes: true` (default), each attribute becomes a column addressed as `<parentPath>/@name`. `attributePrefix` (default `@`) is applied to the attribute's name inside the column; since `@` is not a valid identifier character it is sanitized away by default, so `@id` becomes column `id`. Set a prefix like `attr_` to keep an attribute distinct from a same-named element on the same parent.

### Namespace stripping

With `stripNamespacePrefixes: true` (default), element and attribute names are rewritten to their local names and namespace declarations are dropped, so paths and column names stay clean. When two attributes share a local name across namespaces, the first wins and later collisions are dropped.

### Mixed content

An element carrying its own direct text alongside child elements has that text captured under the element's own path, so it is not silently dropped.

### Column naming

A column name is the path with its leading slash removed, `/` folded onto `separator`, repetition subscripts removed, and the result sanitized to a valid identifier (invalid characters replaced, doubled separators collapsed, a leading digit prefixed with `_`). Casing is preserved. Two distinct paths that would map to the same name are de-collided deterministically with a numeric suffix (`name_2`, `name_3`), identically in the schema and data views.

## Type inference

The reader emits every value as a raw string; the target table is created with `schema.defaultColumnType` (or `defaultColDataType` in options). `sqlflow infer` profiles an already-loaded table and proposes typed columns, but it takes a standalone `.infer.yaml` spec naming the connection and table, not the pipeline flow document itself; see [sqlflow infer](../../cli/infer.md). A spec for the basic sample:

```yaml
connection: ${env:SQLFlowSinkConStr}
table: dbo.Xml_Basic
```

```bash
sqlflow infer xml-basic.infer.yaml
```

Columns produced by `xmlPaths` or the `maxDepth` cutoff are flagged large text and belong in `nvarchar(max)`.

## File discovery, date filtering, and lifecycle

The XML reader rides the shared file-source base, so the common options bound by `PreIngestionXml.FromSource` apply. The default file glob is `*.xml`.

| Option | Default | Description |
| --- | --- | --- |
| `srcFile` | unset | File name glob within `source.location` (defaults to `*.xml`). |
| `srcPathMask` | unset | Path mask for directory matching. |
| `searchSubDirectories` | `false` | Recurse into subdirectories. |
| `initFromFileDate` / `initToFileDate` | unset | Inclusive file-date window for the initial load. |
| `copyToPath` | unset | Copy ingested files to this path. |
| `zipToPath` | unset | Zip ingested files to this path. |
| `srcDeleteIngested` | `false` | Delete files after successful ingestion. |
| `srcDeleteAtPath` | `false` | Delete at the source path. |
| `readAhead` | `1` | How many source files are kept open at once (1 to 32). Files are still read one at a time in file order; a higher value only opens and downloads the following files ahead of their turn, which removes the per-file latency that dominates a source of many small files. The reader parses a whole document, so each extra open file multiplies the peak. An out-of-range value fails with `Invalid 'readAhead' value '<n>'. Use 1 (read one file at a time) to 32.` |
| `showPathWithFileName` | `false` | Include the path in the FileName_DW column. |
| `includeFileName`, `includeFileDate`, `includeFileRowDate`, `includeFileSize`, `includeDataSet`, `includeRowNumber` | `true` | Provenance column toggles. |
| `includeFileLineNumber` | `false` | Include the source record number as a column. |
| `dataSetFromFileName` | `true` | Derive `DataSet_DW` from a date in the file name (fallback: modified date); `false` makes it equal `FileDate_DW`. See [dataSetFromFileName](../../concepts/provenance-and-row-keys.md#dataset_dw-and-datasetfromfilename). |
| `dataSetFormats` | none | Extra .NET date formats for `DataSet_DW` detection, comma- or pipe-separated, tried before the built-ins. |
| `dataSetDayFirst` | (inferred) | Hard-lock day-first (`true`) or month-first (`false`) for ambiguous same-length dates; inferred from the file set when unset. |
| `includeHashKey` / `hashKeyColumns` / `hashKeyType` | `false` / unset / `SHA2_512` | Synthetic hash key. |
| `includeConcatKey` / `concatKeyColumns` / `concatKeySeparator` | `false` / unset / `\|` | Synthetic concatenated key. |
| `expectedColumnCount` | `0` | Expected column count; 0 disables the check. |
| `syncSchema` | `true` | Evolve the target schema across files. |
| `defaultColDataType` | unset | Default SQL type for discovered columns. |
| `fetchDataTypes` | `false` | Fetch data types. |
| `preFilter`, `preProcessOnTrg`, `postProcessOnTrg`, `preInvokeAlias` | unset | Processing hooks. |
| `onErrorResume` | `true` | Continue the batch on error. |
| `noOfThreads` | `4` | Parallelism. |

`syncSchema`, `expectedColumnCount`, `fetchDataTypes`, `onErrorResume`, `noOfThreads`, and the `preFilter` / `preProcessOnTrg` / `postProcessOnTrg` / `preInvokeAlias` hooks are bound by `PreIngestionXml.FromSource` but never reach `FileSourceOptions`: `XmlSourceReader.ReadOptions` does not forward them, so setting any of them under `source.options` has no effect on an XML load (exactly like JSON). They are carried as metadata only.

The `fileDate.*` option group (for example `fileDate.from: path` with `fileDate.hive: "true"`) redirects where the file's business date is read from (path tokens or file name instead of the modified timestamp); see [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md) for the full contract.

The flow-type discriminator is `xml` and `sysAlias` defaults to `default`.

## Exploring a file before writing a flow

The `paths` and `flatten` CLI commands point straight at an XML file or folder, no pipeline YAML needed (src/SqlFlow.Cli/Program.cs):

```bash
# List every addressable XPath (values, containers, repeating elements)
sqlflow paths samples/xml/data/orders.xml

# Emit a runnable flow stub with the resolved column schema, exploding /line
sqlflow flatten samples/xml/data/purchase-orders.xml --explode /line -o po.flow.yaml

# Preview the flattened rows as CSV
sqlflow flatten samples/xml/data/purchase-orders.xml --explode /line --data
```

Flatten rules map to the same options: `--root` (rowXPath), `--include`, `--exclude`, `--explode`, `--keep` (xmlPaths), `--aliases`, `--map` (columnMappings), `--separator`, `--join-separator`, `--repeat`, plus `--max-files`, `--max-records`, `--max-depth`. For a flow that already exists, `sqlflow discover <flow.yaml>` runs the same introspection against the flow's source.

## Realistic example

Adapted from samples/xml/xml-explode.flow.yaml against samples/xml/data/purchase-orders.xml, where each `<order>` wraps repeating `<line>` elements:

```yaml
# Two orders with 2 + 1 lines produce 3 rows: id repeats per line; line_sku and line_qty vary.
name: Xml_Explode
source:
  type: xml
  location: ./data/purchase-orders.xml
  options:
    explodePaths: "/line"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Xml_Explode
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

Schema evolution across file versions, from samples/xml/xml-schema-evolution.flow.yaml: v1 `<person>` has `name`, v2 has `fullName`; both feed one `person_name` column while the additive union null-fills fields the other version lacks.

```yaml
name: Xml_SchemaEvolution
source:
  type: xml
  location: ./data/evolution
  options:
    srcFile: "*.xml"
    pathAliases: "person_name=/name|/fullName"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Xml_SchemaEvolution
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

## See also

- [JSON source](json.md): the same flatten semantics over JSON documents.
- [JSON and XML flattening](../../concepts/json-xml-flattening.md): the shared path-based flatten model.
- [File discovery, date windows, and lifecycle](../../concepts/file-discovery-and-lifecycle.md): the fileDate.* contract in full.
- [paths command](../../cli/paths.md): inventory every addressable path in a file.
- [sqlflow infer](../../cli/infer.md): the standalone type-inference spec and command.
- [Explore JSON and XML files](../../guides/explore-json-xml.md): a walkthrough from raw file to typed table.
