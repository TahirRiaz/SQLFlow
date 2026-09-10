# Parquet sample pipelines

Apache Parquet ingestion, a modern port of the original `flw.PreIngestionPRQ` flow. Parquet is **columnar and
self-describing**: the file carries its own schema and real types, so there is nothing to flatten and there are
no header/delimiter/sheet options. Each `*.flow.yaml` here loads a `.parquet` file (or a folder of them) into a
`dbo.Parquet_*` table in the sink.

Because Parquet already carries an exact schema, this reader maps each Parquet type **straight to its SQL Server
type** and loads the **typed values** as-is - no stringify, no inference round-trip (the reason the original
metadata's `FetchDataTypes` defaults off for Parquet):

| Parquet | SQL Server |
| --- | --- |
| bool | `bit` |
| int8 / int16 / int32 | `tinyint` / `smallint` / `int` |
| int64 | `bigint` |
| float / double | `real` / `float` |
| decimal(p,s) | `decimal(p,s)` (precision/scale from the file) |
| timestamp | `datetime2` |
| date / time | `date` / `time` |
| string | `nvarchar(max)` |
| binary | `varbinary(max)` |
| uuid | `uniqueidentifier` |

This is the one place V3 does **not** use the "strings then infer" path the text formats use, because Parquet
knows the types and the original engine does the same. It still rides the shared pipeline for file selection,
schema evolution, provenance columns, keys, and lifecycle. `NaN`/`+/-Infinity` load as `NULL` (SQL `float`
cannot represent them); struct fields are surfaced as dotted leaf columns (`address.city` becomes
`address_city`); files are read one **row group** at a time to bound memory, just like the original engine.

Because Parquet files are binary, this folder ships flow definitions but no committed `.parquet` data - point
`location` at your own file or folder.

## Running

```
$env:SQLFlowSinkConStr = "Server=localhost,1433;Database=TestDB;User ID=...;Password=...;TrustServerCertificate=True"
dotnet run --project src/SqlFlow.Cli -- run samples/parquet/parquet-basic.flow.yaml
```

The columns come out strongly typed straight from the Parquet schema, so an `infer` pass is not needed.

## The samples

| File | Shows | Table |
| --- | --- | --- |
| `parquet-basic.flow.yaml` | A single `.parquet` file; schema and types come from the file | `Parquet_Basic` |
| `parquet-folder.flow.yaml` | A folder of files with dynamic schema evolution (additive union, null-fill) | `Parquet_Folder` |

## Options reference

Parquet uses only the shared file-selection, provenance, key, and schema options (it has no format-specific
parsing options). Set these under `source.options` (all values are strings).

| Option | Meaning |
| --- | --- |
| `srcFile` | File-name glob when `location` is a folder (default `*.parquet`). |
| `searchSubDirectories` | Recurse into sub-folders. |
| `expectedColumnCount` | Fail a file whose leaf-column count differs; `0` disables. |
| `partitionList` | Partition column list carried for metadata fidelity (Hive-style). Recorded, not used to drive reads. |
| `includeFileName` / `includeFileDate` / `includeFileRowDate` / `includeFileSize` / `includeDataSet` / `includeRowNumber` | Toggle the `_DW` provenance columns (default on). |
| `includeHashKey` / `hashKeyColumns` / `hashKeyType` | Inject a per-row hash key (varbinary) for dedup. |
| `includeConcatKey` / `concatKeyColumns` / `concatKeySeparator` | Inject a readable composite key. |

When the same column has different types across files in one load (e.g. `int` in one, `string` in another), the
unified column is widened to `nvarchar(max)` and both files' values are rendered as text.

### Nested columns

Scalar and struct columns are strongly typed (structs flatten to dotted columns, `address.city` becomes
`address_city`). A **single level of nesting** - a list of scalars, a map, or a list of structs - is
reconstructed and kept as one **JSON `nvarchar(max)`** column (the same keep-as-string idea the JSON/XML readers
use): e.g. a Parquet `array<int>` lands as `[7,8,9]`, a `map<string,int>` as `{"a":1,"b":2}`, a
`array<struct<a,b>>` as `[{"a":1,"b":"x"}]`. Deeper nesting (list-of-list, list-of-map - repetition level 2+) is
**rejected with a clear error**; pre-flatten those.
