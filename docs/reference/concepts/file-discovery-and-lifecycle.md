---
id: concept-file-discovery-and-lifecycle
title: File discovery, date windows, and post-load lifecycle
type: concept
summary: How file flows select source files (glob, path mask, date window, partition pruning) and what happens to files after a successful load.
keywords:
  - srcfile
  - glob
  - srcpathmask
  - filedate
  - recursion
  - copytopath
  - ziptopath
  - delete
  - partition pruning
related:
  - flow-source
  - flow-incremental
  - concept-file-source-pipeline
sourceRefs:
  - src/SqlFlow.Sources/FileSourceReaderBase.cs
  - src/SqlFlow.Sources/LocalFileStore.cs
  - src/SqlFlow.Sources/FileDateFilter.cs
  - src/SqlFlow.Sources/LocalFileLifecycle.cs
  - src/SqlFlow.Core/Model/FileDateSpec.cs
  - src/SqlFlow.Core/Model/FileDiscovery.cs
  - src/SqlFlow.Core/Model/FileRef.cs
  - src/SqlFlow.Core/Model/PreIngestionCsv.cs
---

# File discovery, date windows, and post-load lifecycle

Every file-based flow (CSV, Excel, JSON, XML, Parquet) selects its source files through one shared discovery pipeline in src/SqlFlow.Sources/FileSourceReaderBase.cs. The same selection runs before the load (to decide what to read), before schema inference (so columns come from exactly the files that will load), and again after the load (to apply the file lifecycle). This page explains the three stages:

1. **Discovery**: enumerate candidate files under `source.location` using a file-name glob, optional recursion, and an optional full-path regex.
2. **Date window**: keep only files whose business date falls in the configured window; the date can come from the file's modified timestamp, from Hive partition folders in the path, or from a regex over the path or name. A path-derived date lets discovery prune whole out-of-window partition folders before listing them, so a lake with years of partitions is never fully enumerated.
3. **Lifecycle**: after a successful load, optionally copy, zip, and delete each ingested file.

Discovery exists as its own layer so that init loads, incremental loads, format tooling (for example JSON structure discovery), and the post-load lifecycle all resolve the identical file set on one code path.

## Stage 1: enumeration (glob, recursion, path mask)

All keys below live under `source.options` in the flow YAML.

| Key | Type | Default | Behavior |
| --- | --- | --- | --- |
| `srcFile` | glob | format-specific | File-name glob applied at every enumerated directory level. Defaults per reader: `*.csv`, `*.xlsx`, `*.json`, `*.xml`, `*.parquet`. |
| `searchSubDirectories` | bool | `false` | Recurse into sub-directories. |
| `srcPathMask` | .NET regex | none | Matched against each candidate file's FULL path (folder plus file name), with `IgnoreCase` and `CultureInvariant`. Applied after the glob and intersected with any date window. |

`source.location` can point at:

- **A single existing file**: it is used directly; the glob is not consulted, but the per-file filter (path mask, date window) still applies.
- **A directory**: files are enumerated level by level with the glob; an empty pattern defaults to `*`. Recursion happens only with `searchSubDirectories: true`.
- **A `file://` URI**: `LocalFileStore` handles plain local paths, UNC paths, and `file://` URIs. Any other `://` scheme is deferred to other registered `IFileStore` implementations; a location no store handles raises `No file store handles location '<location>'.`.

A missing path raises `Path not found: '<location>'.`. An invalid `srcPathMask` raises `Invalid 'srcPathMask' regular expression '<mask>': <cause>`. During a recursive walk, a directory that becomes unreadable mid-walk (permissions, a concurrent delete) is skipped rather than aborting the listing.

Matched files are consolidated into one load, ordered by modified timestamp and then by name. If nothing survives the filters, the run fails with `No files under '<location>' matched the filters (...)`, listing the pattern, path mask, file-date source, date window, and incremental watermark that were active.

Each discovered file is represented by a storage-agnostic `FileRef` (src/SqlFlow.Core/Model/FileRef.cs): `Path` (full path or URI), `Name` (file name only), `Size` (bytes), and `Modified` (nullable UTC `DateTimeOffset`).

## Stage 2: the date window

Three `source.options` keys bound the selection by each file's business date:

| Key | Bound | Authored? |
| --- | --- | --- |
| `initFromFileDate` | inclusive lower | yes |
| `initToFileDate` | inclusive upper | yes |
| `incrementalAfterDate` | exclusive lower | no; injected by the engine from the incremental watermark |

Accepted formats: `yyyy-MM-dd`, `yyyyMMdd`, `yyyy-MM-dd HH:mm:ss`, `yyyy-MM-ddTHH:mm:ss`, `yyyyMMddHHmmss`, then a general invariant-culture parse. All values are treated as UTC. A value that parses under none of these raises `Invalid '<field>' value '<value>'. Use yyyy-MM-dd or a full timestamp.`. Either bound may be omitted for an open-ended window.

The same `FileDateFilter` (src/SqlFlow.Sources/FileDateFilter.cs) serves the authored init window and the engine-injected incremental watermark, so init load and incremental load share one selection path.

### Where the date comes from: `fileDate.*`

By default the business date is the file's modified timestamp. The `fileDate.*` options move it into the path or the name:

| Key | Values | Behavior |
| --- | --- | --- |
| `fileDate.from` | `modified` (default), `path`, `name` | Where the date is read. Any other value raises `Invalid 'fileDate.from' value '<v>'. Use path, name, or modified.`. |
| `fileDate.hive` | bool | Parse Hive `key=value` tokens from the path. Recognized keys (case-insensitive): `year` or `yyyy`, `month` or `mm`, `day` or `dd`, `hour` or `hh`. Path source only. |
| `fileDate.pattern` | .NET regex | Extract the date via named groups `year`/`month`/`day`/`hour` (aliases `y`/`m`/`d`/`h`), or unnamed groups read positionally in that order. An invalid regex raises `Invalid 'fileDate.pattern' regular expression '<p>': <cause>`. |

With `fileDate.from: path` or `name`, exactly one of `fileDate.hive: true` or `fileDate.pattern` is required:

- Both set: `fileDate cannot set both 'fileDate.hive' and 'fileDate.pattern'; choose one.`
- Neither set: `fileDate with from 'path' or 'name' needs either 'fileDate.hive: true' (Hive key=value tokens) or 'fileDate.pattern' (a regex yielding year/month/day).`
- `hive` with `from: name`: `fileDate.hive applies to the path; use 'fileDate.pattern' for a name-derived date.`

The pattern is matched against the full path when `from: path` and against the file name when `from: name`.

### Date intervals and overlap

A parsed date composes a closed UTC interval `[Lo, Hi]` (`DateInterval` in src/SqlFlow.Core/Model/FileDateSpec.cs). Missing finer components widen the interval:

| Components present | Interval |
| --- | --- |
| year | the whole year |
| year, month | the whole month |
| year, month, day | the whole day |
| year, month, day, hour | one hour |

A file is kept (and a folder descended into) when its interval OVERLAPS the window; this is the correct test for coarse partitions, since a `year=2025` folder overlaps a window starting mid-2025. Out-of-range components (month 13, day 32, hour 24) make the text carry no date at all. A month without a year also carries no date.

Two asymmetric rules govern text with no recognizable date:

- **A folder with no date tokens is always entered** (it cannot be ruled out, so nothing is missed).
- **A file with no parsable date is excluded** from a date-scoped selection rather than guessed at. With no window set, undated files still load.

### Partition pruning

Only a path-derived date (`fileDate.from: path`) can prune directories: during the recursive walk, `LocalFileStore` consults the filter's `ShouldEnterDirectory` before descending, so an out-of-window partition subtree (say `year=2024` when the window is 2025) is skipped without ever being listed. A name-derived or modified-timestamp date cannot rule a whole folder out, so every folder is walked and files are tested individually.

## Stage 3: post-load lifecycle (copy, zip, delete)

After a successful load, `CompleteAsync` reuses the same file snapshot the load resolved and applies the lifecycle, in order, per file. It acts on the run's cached snapshot (`run?.Snapshot?.Files`), never a fresh re-listing, so a file that appeared after the load is not copied or deleted as if it had been ingested; only a standalone `CompleteAsync` with no prior pass on this spec resolves the list itself, and the resulting set is identical to what the load selected. The lifecycle runs, in order, per file:

1. **Copy** to `options.copyToPath`, when set. Overwrites an existing destination file.
2. **Zip** to `options.zipToPath`, when set. Produces a single-entry archive named `<sourceFileNameWithoutExtension>.zip`; an existing archive at the destination is deleted first.
3. **Delete** the source file, when `options.srcDeleteIngested` or `options.srcDeleteAtPath` is true. The two flags currently behave identically. Delete is idempotent: a missing file is not an error.

The whole step is a no-op when none of the four options is set.

A lifecycle target path is treated as a directory when it exists as one or has no file extension; otherwise it is used as the destination file name. Missing destination directories are created automatically (src/SqlFlow.Sources/LocalFileLifecycle.cs).

## Configuration touchpoints

- **YAML** (`source.options`): `srcFile`, `srcPathMask`, `searchSubDirectories`, `initFromFileDate`, `initToFileDate`, `fileDate.from`, `fileDate.hive`, `fileDate.pattern`, `copyToPath`, `zipToPath`, `srcDeleteIngested`, `srcDeleteAtPath` apply to every file format on this pipeline (CSV, XLS, JSON, XML, Parquet). `maxRows` (0 = all) caps how many data rows load after selection, but only the CSV reader maps it into the shared pipeline; the JSON, XML, XLS, and Parquet readers do not wire it through.
- **Engine-injected**: `incrementalAfterDate` is written by the incremental machinery from the watermark; do not author it.
- **Location schemes**: plain paths, UNC paths, and `file://` go to `LocalFileStore`; other schemes go to whichever registered `IFileStore` claims them.

## Examples

Hive-partitioned lake, pruned by path date (from samples/csv/csv-file-date-partitions.flow.yaml):

```yaml
name: Csv_FileDatePartitions
source:
  type: csv
  location: ./data/partitioned
  options:
    srcFile: "*.csv"
    searchSubDirectories: "true"
    fileDate.from: path
    fileDate.hive: "true"
    initFromFileDate: "2025-01-01"
    initToFileDate: "2025-12-31"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Csv_FileDatePartitions
```

With data laid out as `data/partitioned/year=2025/month=03/orders.csv`, the `year=2024` and `year=2026` folders are pruned during discovery and never listed.

Name-derived date with a regex, plus archive-and-delete lifecycle:

```yaml
name: Csv_NameDatedArchive
source:
  type: csv
  location: ./inbox
  options:
    srcFile: "orders_*.csv"
    srcPathMask: "orders_\\d{8}\\.csv$"
    fileDate.from: name
    fileDate.pattern: "(?<year>\\d{4})(?<month>\\d{2})(?<day>\\d{2})"
    initFromFileDate: "2025-06-01"
    zipToPath: ./archive
    srcDeleteIngested: "true"
target:
  connection: ${env:SQLFlowSinkConStr}
  schema: dbo
  table: Orders
```

Each ingested file (for example `orders_20250630.csv`) is zipped to `./archive/orders_20250630.zip` and then deleted from `./inbox`.

## See also

- [source](../flow/source.md): the `source` section reference, including `location` and `options`.
- [incremental](../flow/incremental.md): how the watermark that feeds `incrementalAfterDate` is maintained.
- [csv source type](../flow/source-types/csv.md): the full CSV option set that shares this discovery pipeline.
