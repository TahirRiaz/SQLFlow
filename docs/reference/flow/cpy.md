---
id: flow-cpy
title: "Copy flow (flowType: cpy): file copy between storage endpoints, with zip/unzip"
type: flow-reference
summary: "flowType: cpy copies files byte-for-byte between local disk and Azure Blob/ADLS in any direction, optionally zipping or unzipping."
keywords:
  - copy
  - cpy
  - lake to lake
  - zip
  - unzip
  - azure blob
  - adls
related:
  - flow-overview
  - flow-sftp
  - flow-api
sourceRefs:
  - src/SqlFlow.Core/Copy/CopyFlow.cs
  - src/SqlFlow.Copy/CopyEngine.cs
  - src/SqlFlow.Copy/LocalCopyEndpoint.cs
  - src/SqlFlow.Copy/AzureBlobCopyEndpoint.cs
  - src/SqlFlow.Yaml/YamlCopyFlowLoader.cs
  - samples/copy/boatbooking-storage-to-lake.flow.yaml
---

# Copy flow (flowType: cpy)

A `cpy` flow copies files **byte-for-byte** between two storage endpoints, in any direction: Azure Blob/ADLS Gen2 to/from another account, local disk to/from Azure, or local to local. It performs no parsing; it optionally bundles the matched files into one `.zip` (`operation: zip`, a zip-only run) or extracts archives (`operation: unzip`). It replaces the estate's lake-to-lake / drop-zone copy runbooks (e.g. boatbooking). SFTP is a separate flow type (`sftp`), not a copy endpoint.

## Minimal example

```yaml
flowType: cpy
name: Lake_Relay
source:
  location: abfss://drop@vendoracct.dfs.core.windows.net
  modifiedWithinDays: 14
target:
  location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/history
```

```bash
sqlflow validate lake-relay.flow.yaml
sqlflow run      lake-relay.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `cpy`. |
| `name` | string | yes | | Flow identity; seeds the stable flow id. |
| `batch` | string | no | | Batch label recorded with the run. |
| `operation` | enum | no | `copy` | `copy` (verbatim), `zip` (bundle matched files into one archive at the target - a zip-only run), or `unzip` (extract each matched `.zip` to the target). Applies to every step. |
| `source` | map | one of | | The source of a single copy. Use `source`+`target` for one copy, or `items` for several - not both. |
| `target` | map | one of | | The target of a single copy. |
| `items` | list | one of | | Several copies in one pipeline: one entry per source-to-target file set (see [Copying many file sets](#copying-many-file-sets)). |
| `options` | map | no | | Copy behavior; applies to every step. |
| `output` | map | no | | A single explicitly declared output, for lineage (see [Declared outputs](#declared-outputs-for-lineage)). |
| `outputs` | list | no | | Several explicitly declared outputs, for lineage (see [Declared outputs](#declared-outputs-for-lineage)). |

A flow declares **either** a single top-level `source`/`target` pair **or** an `items` list, never both. Both forms produce the same thing internally (a list of copy steps); the single pair is the one-copy shortcut.

### source / target

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `location` | string | yes | | A local/UNC path or an Azure storage URI (`abfss://fs@account.dfs.core.windows.net/path` or the https form). |
| `pattern` | string | no | `*` | Source only: file-name glob selecting which files copy. |
| `recursive` | bool | no | `true` | Source only: recurse into subfolders. |
| `modifiedWithinDays` | int | no | `0` | Source only: only copy files modified within this many days; 0 = all. |
| `connectionStringRef` | secret ref | no | | Azure endpoint: `${...}` reference to a connection string. Absent = ambient managed identity. |
| `sasTokenRef` | secret ref | no | | Azure endpoint: `${...}` reference to a SAS token. |
| `accountKeyRef` | secret ref | no | | Azure endpoint: `${...}` reference to a storage account key (the shape the legacy runbooks used). Prefer managed identity. |

The target ignores `pattern`/`recursive`/`modifiedWithinDays`.

### options

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `overwrite` | bool | no | `true` | Overwrite an existing target file; when false a collision fails rather than clobbers. |
| `preserveStructure` | bool | no | `true` | Preserve each file's folder structure (relative to the source root) under the target; flat by name otherwise. |
| `skipUnchanged` | bool | no | `true` | Skip writing a file whose target copy already holds byte-identical content (compared by content hash from one target listing), so an unchanged re-run does not bump the target's modified time and re-trigger downstream ingestion. Set false to rewrite every matched file unconditionally and avoid the comparison cost. No effect when `overwrite` is false. |
| `zipName` | string | no | `<flow>_<timestamp>.zip` | The archive name for `operation: zip`; ignored otherwise. |

## Copying many file sets

One `cpy` pipeline copies as many file sets as it lists, so a whole source system is one pipeline rather than one flow per folder. Each `items` entry is an independent `source`→`target` copy with its own selection; the flow-level `operation` and `options` apply to every entry. This is the shape for a vendor whose objects each land in their own folder:

```yaml
flowType: cpy
name: BB_Boatbooking_00_cpy
batch: BB
operation: copy
options:
  overwrite: true
  preserveStructure: true
items:
  - source: { location: abfss://boatbooking@dwstorageboatbookingprod.dfs.core.windows.net/DETAIL, pattern: "*.json", modifiedWithinDays: 14 }
    target: { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/boatbooking/history/detail }
  - source: { location: abfss://boatbooking@dwstorageboatbookingprod.dfs.core.windows.net/SESS, pattern: "*.json", modifiedWithinDays: 14 }
    target: { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/boatbooking/history/sess }
```

The run reports one aggregated result (files matched and written across all steps). Lineage is computed from the items: each step's source is a read node and its target a written node the downstream ingestion reads, so every landed folder binds to its load and all the loads run after the one copy. Scaling to a hundred file sets is a hundred `items` entries, not a hundred pipelines.

## Authentication

Azure endpoints authenticate with the ambient managed identity / az login (`SQLFLOW_AZURE_AUTH`) by default - the same credential Key Vault, invoke, and the other file engines use, so no per-flow secret is needed. For a drop zone that only issues a key, set `accountKeyRef` (account key), `sasTokenRef` (SAS), or `connectionStringRef` (connection string); each is a whole `${keyvault:...}` / `${env:...}` reference, never the value itself.

## Operations

- **copy**: each matched source file is written to the target, at its relative path (or flat when `preserveStructure: false`).
- **zip**: every matched file is read and bundled into a single `.zip` written to the target under `zipName`. Running a flow purely to produce an archive is a zip-only run.
- **unzip**: each matched source archive (a `.zip`) is extracted; with `preserveStructure` its entries nest under a folder named for the archive so two archives never collide, flat by entry name otherwise.

## Declared outputs (for lineage)

A copy's target is its output, so by default lineage binds the single target folder to the downstream file ingestion that reads it (Azure storage URIs are matched by canonical identity, so the copy writing `abfss://…/detail` and a load reading `https://….dfs…/detail/` are one node). When one copy fans files into several folders that feed different ingestions (for example `preserveStructure` lands per-object subfolders), declare each output explicitly so each binds independently and every downstream consumer gets an edge from this copy:

```yaml
flowType: cpy
name: Vendor_Relay
source:
  location: abfss://drop@vendoracct.dfs.core.windows.net
target:
  location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/history
outputs:
  - { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/history/detail, srcFile: "*.json" }
  - { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/history/sess,   srcFile: "*.json" }
```

Each entry declares one file set. Matching to a downstream ingestion is engine-parity and supports three forms, the same matcher the file readers use:

| Field | Match |
|---|---|
| `location` | Path match: the ingestion's watched folder is this folder or a folder beneath it. |
| `srcFile` | File-name glob (e.g. `detail_*.json`); two wildcard patterns bind when they can produce a common name. |
| `srcPathMask` | A regex over the full landing path, applied symmetrically with the ingestion's own `srcPathMask`, for cases the folder prefix cannot express. |

`output` is the singular convenience for the one-folder case; `outputs` is the list. A file set nothing consumes still records its own file node, so it is visible and binds automatically once a matching ingestion is added. Many files landing in one folder need only a single entry with a glob, since the lineage node is the folder.

## See also

- [SFTP flow](sftp.md)
- [Flow types overview](overview.md)
