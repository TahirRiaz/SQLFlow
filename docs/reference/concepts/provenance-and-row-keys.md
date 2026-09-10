---
id: concept-provenance-and-row-keys
title: Injected _DW provenance columns and synthetic row keys
type: concept
summary: The _DW provenance columns every file flow injects, plus the opt-in HashKey_DW and ConcatKey_DW synthetic row keys and their toggles.
keywords:
  - filename_dw
  - filedate_dw
  - rownumber_dw
  - hashkey_dw
  - concatkey_dw
  - hashkeytype
  - provenance
  - system columns
related:
  - flow-source
  - flow-incremental
  - concept-upsert-and-change-detection
sourceRefs:
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Sources/FileSourceOptions.cs
  - src/SqlFlow.Core/Model/PreIngestionCsv.cs
  - src/SqlFlow.Core/Model/SourceOptions.cs
  - src/SqlFlow.SqlServer/SqlServerTypeMapper.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Execution/DocumentLoader.cs
  - src/SqlFlow.Cli/Program.cs
---

# Injected _DW provenance columns and synthetic row keys

Every file flow (CSV, XLS, JSON, XML, Parquet) injects a set of generated system columns into the loaded rows. They fall into two groups:

- **Provenance columns**, default on, that record where each row came from: which file, when the file was modified, when the row was ingested, how large the file was, and the row's position within it. They make every warehouse row traceable back to its source file without any pipeline-specific bookkeeping.
- **Synthetic row keys**, default off, that derive a per-row identity when the incoming data has no business key: `HashKey_DW` (a fixed-width binary hash) and `ConcatKey_DW` (a readable delimited composite).

The injection lives in the shared file pipeline (src/SqlFlow.Sources/FileSourceReaderBase.cs), so all file formats behave identically. Each format's reader only supplies column names and rows; the base class appends the system columns to the column union, types them, and writes their generated values into every row after the source cells are mapped.

## Provenance columns

Six provenance columns are injected by default for every file source. Each has an independent toggle under `source.options`:

| Column | SQL type | Value | Toggle | Default |
| --- | --- | --- | --- | --- |
| `FileName_DW` | nvarchar(255) | File name; the full path when `showPathWithFileName: "true"` | `includeFileName` | `true` |
| `FileDate_DW` | nvarchar(255) | The file's modified time (UTC), stored as a `yyyyMMddHHmmss` string | `includeFileDate` | `true` |
| `FileRowDate_DW` | nvarchar(255) | The ingest timestamp (UTC), captured once per file, stored as a `yyyy-MM-dd HH:mm:ss` string | `includeFileRowDate` | `true` |
| `FileSize_DW` | nvarchar(255) | File size in bytes, stored as a digit string (not a CLR `long`) | `includeFileSize` | `true` |
| `DataSet_DW` | nvarchar(255) | The dataset/partition date: a date detected in the file NAME, else the file's modified time (UTC), stored as a `yyyyMMddHHmmss` string. See [DataSet_DW and dataSetFromFileName](#dataset_dw-and-datasetfromfilename) | `includeDataSet` | `true` |
| `RowNumber_DW` | bigint (CLR `long`) | Data-row number within the file | `includeRowNumber` | `true` |

One further traceability column is opt-in:

| Column | SQL type | Value | Toggle | Default |
| --- | --- | --- | --- | --- |
| `FileLineNumber` | bigint (CLR `long`) | The physical line / source record ordinal the row came from | `includeFileLineNumber` | `false` |

Note that `FileLineNumber` has no `_DW` suffix; the name is exact. It differs from `RowNumber_DW` when the reader skips lines (headers, comments, empty rows): `RowNumber_DW` counts data rows, `FileLineNumber` records the physical position in the file (samples/csv/csv-include-line-number.flow.yaml).

All option values are strings in YAML (`"true"` / `"false"`), matching the generic `source.options` map.

### showPathWithFileName

`options.showPathWithFileName` (default `false`) switches the value stored in `FileName_DW` from the bare file name to the full resolved path (samples/csv/csv-show-full-path.flow.yaml). It changes only the value, not the column.

### DataSet_DW and dataSetFromFileName

`DataSet_DW` is the file's dataset/partition date, the value a dataset-partitioned load (`load.dataSetColumn`) orders by so files apply in the order their data was produced. By default the engine reads that date from the file NAME: `options.dataSetFromFileName` (default `"true"`) turns on detection, and the file's last-modified timestamp is the fallback when the name carries no detectable date (`src/SqlFlow.Core/Model/DataSetDateSpec.cs`). `FileDate_DW` is unaffected: it is always the last-modified timestamp (and the incremental watermark).

Detection is deterministic. For each format in a baked-in vocabulary, most specific (longest) first, a precise regex locates the matching segment anywhere in the name and `DateTime.TryParseExact` validates it; the first valid date (year `>= 1900`) wins. A segment is bounded by digit look-arounds, so a date is never matched inside a longer run of digits (an id or version number), and an invalid date (month 13, `20241301`) is rejected rather than guessed. The built-in vocabulary covers the filename-safe **full-date** forms (separators `-`, `_`, `.`, or none; `:`/`/` cannot appear in a name): `yyyyMMdd`, `yyyy-MM-dd`, `yyyy_MM_dd`, `yyyy.MM.dd`, `yyyyMMddHHmmss`, `yyyyMMdd_HHmmss`, `yyyyMMdd-HHmmss`, `yyyy_MM_dd_HH_mm_ss`, `yyyy-MM-dd_HH-mm-ss`, `dd-MM-yyyy` (and `.`/`_` variants), `MM-dd-yyyy`, `yyyy-M-d`, `ddMMyyyy`, `MMddyyyy`. No year-month format ships by default (a delimited `yyyy-MM` is a prefix of `yyyy-MM-dd`, so it would degrade an invalid `2024-02-30` to a bare year-month); a flow that lands monthly files adds `yyyy-MM` (or `yyyyMM`) via `dataSetFormats`.

**Ambiguous same-length dates are resolved from the file SET, not one name.** A name where a component exceeds 12 (`03-15-2024`) is resolved per file, because only one ordering is a valid date. A name where both are `<= 12` (`01-02-2024`) cannot be resolved alone, so the reader looks at the other files in the resolved set: if an unambiguous sibling proves the set is month-first (or day-first), that reading is applied to the ambiguous names too. This is inference from the batch each run, so it stays consistent for a stable source location and adapts if the naming convention changes. When the set gives no evidence, or contradicts itself (some names force day-first and others month-first), the day-first default holds. To pin the reading deterministically regardless of the set, set `options.dataSetDayFirst` (`"true"` day-first, `"false"` month-first).

`options.dataSetFormats` adds extra .NET date formats (comma- or pipe-separated), tried ahead of the built-ins, so a flow can support a house convention or override an ambiguous tie:

```yaml
source:
  options:
    dataSetFromFileName: "true"     # default; "false" makes DataSet_DW equal FileDate_DW (last-modified)
    dataSetFormats: "yyMMdd|yyyyDDD"
    dataSetDayFirst: "false"        # optional: hard-lock month-first for ambiguous same-length dates
```

To keep the pre-port behavior where `DataSet_DW` equals the last-modified timestamp, set `dataSetFromFileName: "false"`.

### Name collisions with source columns

Provenance and key values are generated and written after the source cells are mapped, so a source column that shared an enabled system column's name would be silently overwritten and re-typed. The pipeline rejects this at schema time instead:

```text
A source column named '<name>' collides with the enabled '<name>' provenance/key column, whose generated value would overwrite the source data. Rename the source column (columnMappings) or disable that provenance/key column.
```

The two fixes are exactly what the message says: rename the source column, or turn the corresponding `include*` toggle off.

When a toggle is off, the guard does not apply: a source column named, say, `RowNumber_DW` passes through as ordinary data with the type its reader declared, and is never overwritten by a generated value. Only the system columns that are actually enabled get their generated types and values.

## Synthetic row keys

Both keys are off by default and work identically across CSV, XLS, JSON, XML, and Parquet sources. They are computed from the assembled output row, after the provenance values have been written, using the same faithful invariant string rendering that string columns use (so a typed Parquet cell hashes the same text a string source would carry).

### HashKey_DW

`options.includeHashKey: "true"` injects `HashKey_DW`, a NOT NULL `varbinary` column holding a hash of the selected column values. `options.hashKeyType` selects the algorithm and therefore the column width:

| hashKeyType | Algorithm | Column type |
| --- | --- | --- |
| `SHA2_512` (default) | SHA-512 | `varbinary(64)` |
| `SHA2_256` | SHA-256 | `varbinary(32)` |
| `SHA1` | SHA-1 | `varbinary(20)` |
| `MD5` | MD5 | `varbinary(16)` |

Algorithm names are normalized before matching: underscores and hyphens are stripped and the comparison is case-insensitive, so `sha2-512`, `SHA2_512`, and `sha512` all resolve to SHA-512. Any unrecognized value silently falls back to SHA-512.

The hash input is unambiguous across column boundaries: for each selected value, a 4-byte little-endian length prefix (the string's character count, or `-1` for a null value) is appended, followed by the value's UTF-8 bytes. Two rows whose concatenated text happens to match but whose per-column splits differ therefore produce different hashes.

### ConcatKey_DW

`options.includeConcatKey: "true"` injects `ConcatKey_DW`, a NOT NULL string column (max length 4000) that joins the selected values with `options.concatKeySeparator` (default `|`). Null values render as empty strings in the composite.

### Selecting the key columns

`options.hashKeyColumns` and `options.concatKeyColumns` are comma-separated source column names. Whitespace around names is trimmed and the listed order is honored, so reordering the list changes the key.

When a list is empty or omitted, the key covers **all** source columns, excluding every generated provenance/key column (`FileName_DW`, `FileDate_DW`, `FileRowDate_DW`, `FileSize_DW`, `DataSet_DW`, `RowNumber_DW`, `FileLineNumber`, `HashKey_DW`, `ConcatKey_DW`). That exclusion keeps the key stable across files: the same data row in two differently named files hashes identically. An explicit list may name a provenance column if you deliberately want file identity folded into the key.

Two validation errors guard the selection:

- An explicit list that matches no column in the source fails with:

  ```text
  Key columns '<list>' did not match any column in the source.
  ```

  Names that do not resolve are dropped individually; the error fires only when nothing in the list matched.

- A key requested on a source with no usable columns fails with:

  ```text
  A row key was requested but the source has no columns to derive it from.
  ```

## Configuration touchpoints

- **YAML**: all toggles live under `source.options` of a file flow: `includeFileName`, `includeFileDate`, `includeFileRowDate`, `includeFileSize`, `includeDataSet`, `includeRowNumber`, `includeFileLineNumber`, `showPathWithFileName`, `includeHashKey`, `hashKeyColumns`, `hashKeyType`, `includeConcatKey`, `concatKeyColumns`, `concatKeySeparator`.
- **CLI**: `sqlflow flatten <file> --data` previews flattened JSON/XML rows as CSV with the six `_DW` provenance columns suppressed for a clean data preview; pass `--provenance` to keep them (src/SqlFlow.Cli/Program.cs).
- The collision guard runs whenever the source schema is resolved (`GetColumnsAsync`). Both `sqlflow plan` and `sqlflow run` resolve it, so either command surfaces a provenance/key name collision before any DDL executes or any row is read. The two key-column validation errors are raised lazily, the first time a row is pulled from the source, so they surface only during `sqlflow run`, once the load stage starts streaming rows, never during `sqlflow plan`. `sqlflow validate` only parses the YAML into a well-formed flow document and never opens the source, so none of these errors appear there.

## Examples

Default provenance, everything on (nothing to configure):

```yaml
name: orders
source:
  type: csv
  location: ./orders.csv
target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders
```

The loaded table carries the source columns plus `FileName_DW`, `FileDate_DW`, `FileRowDate_DW`, `FileSize_DW`, `DataSet_DW`, and `RowNumber_DW`.

Raw columns only, all provenance off (samples/csv/csv-no-system-columns.flow.yaml):

```yaml
name: Csv_NoSystemColumns
source:
  type: csv
  location: ./data/orders.csv
  options:
    includeFileName: "false"
    includeFileDate: "false"
    includeFileRowDate: "false"
    includeFileSize: "false"
    includeDataSet: "false"
    includeRowNumber: "false"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_NoSystemColumns
```

Both synthetic keys over explicit columns (samples/csv/csv-row-keys.flow.yaml):

```yaml
name: Csv_RowKeys
source:
  type: csv
  location: ./data/orders.csv
  options:
    includeHashKey: "true"
    hashKeyType: SHA2_512
    hashKeyColumns: OrderId,Customer
    includeConcatKey: "true"
    concatKeyColumns: OrderId,Customer
    concatKeySeparator: "|"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_RowKeys
```

The same keys over flattened JSON, with the hash covering all source columns (samples/json/json-keys.flow.yaml):

```yaml
name: Json_Keys
source:
  type: json
  location: ./data/users.json
  options:
    includeHashKey: "true"
    includeConcatKey: "true"
    concatKeyColumns: "user_id, email"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Json_Keys
schema:
  defaultColumnType: nvarchar(4000)
load:
  mode: truncate-load
```

Full path and physical line number for maximum traceability (combining samples/csv/csv-show-full-path.flow.yaml and samples/csv/csv-include-line-number.flow.yaml into one flow):

```yaml
name: Csv_Traceable
source:
  type: csv
  location: ./data/orders.csv
  options:
    showPathWithFileName: "true"
    includeFileLineNumber: "true"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_Traceable
```

Previewing flattened data with and without provenance columns:

```bash
sqlflow flatten ./data/users.json --data
sqlflow flatten ./data/users.json --data --provenance
```

## See also

- [source](../flow/source.md): the file source section and its full options reference
- [incremental](../flow/incremental.md): incremental file selection, which pairs naturally with `FileDate_DW` and `DataSet_DW`
- [upsert and change detection](./upsert-and-change-detection.md): using `HashKey_DW` as a dedup or change-detection key
