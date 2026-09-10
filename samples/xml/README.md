# XML sample pipelines

A showcase of the path-based XML flattener, the modern replacement for the original `XmlToDataTableCode`
blob. Each `*.flow.yaml` here loads an XML file into a `dbo.Xml_*` table in the sink database; they are
exercised by `XmlSampleFlowIntegrationTests`, which drops each table up front and leaves it for inspection.

XML's model is handled directly: **attributes** become columns (`@id` becomes `id`; set `attributePrefix`
to keep an attribute distinct from a same-named element), **nested elements** flatten with a separator
(`customer/name` becomes `customer_name`), **namespaces** are stripped by default, and **repeating
elements** (XML's arrays) follow `repeatHandling` or `explodePaths` (one row per element). Every value lands
as a raw string; run `sqlflow infer` afterwards to propose typed columns.

By default each direct child element of the root is a record (the common wrapped-rows shape); set `rowXPath`
to select a different element, or `.` to treat the root element itself as one record.

## Running

```
$env:SQLFlowSinkConStr = "Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- run samples/xml/xml-basic.flow.yaml
```

Explore a file's structure or generate a formula (these work for JSON and XML alike):

```
dotnet run --project src/SqlFlow.Cli -- paths   samples/xml/data/orders.xml
dotnet run --project src/SqlFlow.Cli -- flatten samples/xml/data/purchase-orders.xml --explode /line -o po.flow.yaml
```

## The samples

| File | Shows | Table |
| --- | --- | --- |
| `xml-basic.flow.yaml` | Wrapped rows, attributes, nested elements | `Xml_Basic` |
| `xml-explode.flow.yaml` | `explodePaths` to emit one row per repeating element (SQL UNNEST) | `Xml_Explode` |
| `xml-keep-subtree.flow.yaml` | `xmlPaths` to keep a subtree as one XML-fragment column | `Xml_KeepSubtree` |
| `xml-schema-evolution.flow.yaml` | one process over v1/v2 shapes; `pathAliases` reconciles a renamed element | `Xml_SchemaEvolution` |

## Flatten options reference

Set these under `source.options` (all values are strings). Paths use `/` syntax relative to the row, with
`/@name` for attributes.

| Option | Meaning |
| --- | --- |
| `rowXPath` | XPath selecting the row elements (carries the original `hierarchyIdentifier`). Empty = each direct child of the root; `.` = the root element itself. |
| `includePaths` | Comma-separated whitelist of paths to flatten. Empty = flatten everything. |
| `excludePaths` | Comma-separated paths whose subtree is dropped (no column). |
| `xmlPaths` | Comma-separated paths kept verbatim as a single XML-fragment column. |
| `explodePaths` | Comma-separated repeating-element paths to explode: each element becomes its own row. Several cross-product. |
| `pathAliases` | Schema-evolution aliases mapping version-specific paths to one column: `col=/a\|/b; col2=/c\|/d`. A missing source path is filled by another, never nulled. |
| `columnMappings` | Semicolon-separated `xpath=columnName` overrides. |
| `repeatHandling` | `to_xml` (default), `first_element`, `last_element`, `join`, `count`, `skip`, or `explode`. |
| `includeAttributes` | Whether attributes become columns (default true). |
| `attributePrefix` | Prefix on an attribute's column name (default `@`); set e.g. `attr_` to disambiguate from a same-named element. |
| `stripNamespacePrefixes` | Strip namespace prefixes from names so paths/columns use local names (default true). |
| `separator` | Separator joining nested names into a column name (default `_`). |
| `maxDepth` | Maximum nesting depth flattened into columns; deeper elements become XML strings (default 10). |

A subtree kept whole (`xmlPaths`, or anything past `maxDepth`) can be large; the flatten formula and the
keep-subtree sample type those columns `nvarchar(max)`.
