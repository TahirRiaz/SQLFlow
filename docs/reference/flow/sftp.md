---
id: flow-sftp
title: "SFTP flow (flowType: sftp): download from / upload to an SFTP server"
type: flow-reference
summary: "flowType: sftp downloads files from an SFTP server into the data lake / local, or uploads the other way, byte-for-byte."
keywords:
  - sftp
  - download
  - upload
  - vendor drop
  - private key
related:
  - flow-overview
  - flow-cpy
  - flow-api
sourceRefs:
  - src/SqlFlow.Core/Sftp/SftpFlow.cs
  - src/SqlFlow.Sftp/SftpEngine.cs
  - src/SqlFlow.Yaml/YamlSftpFlowLoader.cs
  - samples/sftp/citybike-nets-download.flow.yaml
---

# SFTP flow (flowType: sftp)

An `sftp` flow transfers files byte-for-byte between an SFTP server and the data lake (or a local path): `download` (server -> lake/local, the default) or `upload` (lake/local -> server). It performs no parsing; the downstream json/csv/xml file flows ingest what it lands. It is the dedicated interface to SFTP - the file counterpart to the `api` (REST) flow type. The lake side accepts an Azure storage URI (managed-identity authenticated) or a local path.

## Minimal example

```yaml
flowType: sftp
name: Vendor_Settlement
direction: download
server:
  host: sftp.vendor.com
  username: svc
  passwordRef: ${keyvault:dw-keyvault-prod/vendor-sftp-password}
remotePath: /outbound
local: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor
pattern: "*.xml"
modifiedWithinDays: 3
```

```bash
sqlflow validate vendor-settlement.flow.yaml
sqlflow run      vendor-settlement.flow.yaml
```

## Keys reference

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `flowType` | string | yes | | Must be `sftp`. |
| `name` | string | yes | | Flow identity; seeds the stable flow id. |
| `batch` | string | no | | Batch label recorded with the run. |
| `direction` | enum | no | `download` | `download` (server -> local) or `upload` (local -> server). Applies to the whole flow. |
| `server` | map | yes | | The SFTP server and credentials. |
| `local` | string | one of | | Single-transfer: the non-SFTP side (Azure storage URI or local/UNC path). Target on download, source on upload. Use `local`/`remotePath` for one transfer, or `items` for several - not both. |
| `remotePath` | string | no | `.` | Single-transfer: the directory on the server. Listed on download, written to on upload. |
| `pattern` | string | no | `*` | Single-transfer: file-name glob selecting which files transfer. |
| `recursive` | bool | no | `true` | Single-transfer: recurse into subdirectories under the root. |
| `modifiedWithinDays` | int | no | `0` | Single-transfer: only transfer files modified within this many days; 0 = all. |
| `items` | list | one of | | Several transfers in one pipeline: one entry per file set (see [Transferring many file sets](#transferring-many-file-sets)). |
| `overwrite` | bool | no | `true` | Overwrite an existing destination file; when false a collision fails rather than clobbers. Applies to every step. |
| `preserveStructure` | bool | no | `true` | Preserve the source's folder structure under the destination; flat by name otherwise. Applies to every step. |
| `skipUnchanged` | bool | no | `true` | On a download, skip writing a lake/local file whose target already holds byte-identical content (compared by content hash), so an unchanged re-download does not bump its modified time and re-trigger downstream ingestion. Set false to write every downloaded file unconditionally. No effect on upload, or when `overwrite` is false. |
| `output` | map | no | | A single explicitly declared output, for lineage (see [Declared outputs](#declared-outputs-for-lineage)). |
| `outputs` | list | no | | Several explicitly declared outputs, for lineage (see [Declared outputs](#declared-outputs-for-lineage)). |

A flow declares **either** a single top-level `local`/`remotePath` transfer **or** an `items` list, never both. Each `items` entry carries its own `local`, `remotePath`, `pattern`, `recursive`, and `modifiedWithinDays`; `server`, `direction`, `overwrite`, and `preserveStructure` are shared by the whole flow.

### server

| Key | Type | Required | Default | Description |
|---|---|---|---|---|
| `host` | string | yes | | The SFTP host name or IP. |
| `port` | int | no | `22` | The SFTP port. |
| `username` | string | yes | | The SFTP username (a coordinate, not a secret). |
| `passwordRef` | secret ref | one of | | A `${...}` reference to the password. |
| `privateKeyRef` | secret ref | one of | | A `${...}` reference to the private-key PEM (an alternative to `passwordRef`). |
| `passphraseRef` | secret ref | no | | A `${...}` reference to the passphrase protecting `privateKeyRef`. |

Exactly one of `passwordRef` / `privateKeyRef` is required; a flow with neither fails at run time. Secret material is always a whole `${keyvault:...}` / `${env:...}` reference, never inline.

## Transferring many file sets

One `sftp` pipeline moves as many file sets as it lists, over a single connection, so a vendor that drops several file sets is one pipeline rather than one flow per set. Each `items` entry is an independent transfer with its own `remotePath`, `local`, and selection; `server`, `direction`, `overwrite`, and `preserveStructure` are shared:

```yaml
flowType: sftp
name: Vendor_Download
direction: download
server:
  host: sftp.vendor.com
  username: svc
  passwordRef: ${keyvault:dw-keyvault-prod/vendor-sftp-password}
items:
  - remotePath: /outbound/orders
    local: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/orders
    pattern: "orders_*.json"
    modifiedWithinDays: 3
  - remotePath: /outbound/invoices
    local: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/invoices
    pattern: "invoices_*.json"
```

The run reports one aggregated result across all transfers. Lineage is computed from the items: on a download each step's `local` target is a written node the downstream ingestion reads, so every consumer of a downloaded file gets an edge from this one flow.

## Declared outputs (for lineage)

A download's output is `local`, so by default lineage binds that single folder to the downstream file ingestion that reads it. When one download drops several distinct file sets that feed different ingestions, declare each output explicitly so every consumer of a downloaded file gets an edge from this download:

```yaml
flowType: sftp
name: Vendor_Drop
direction: download
server:
  host: sftp.vendor.com
  username: svc
  passwordRef: ${keyvault:dw-keyvault-prod/vendor-sftp-password}
remotePath: /outbound
local: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/inbound
outputs:
  - { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/inbound, srcFile: "orders_*.json" }
  - { location: abfss://datalakev2@dwacct.dfs.core.windows.net/raw/vendor/inbound, srcFile: "invoices_*.json" }
```

Each entry declares one file set. Matching to a downstream ingestion is engine-parity and supports three forms, the same matcher the file readers use:

| Field | Match |
|---|---|
| `location` | Path match: the ingestion's watched folder is this folder or a folder beneath it. |
| `srcFile` | File-name glob (e.g. `orders_*.json`); two wildcard patterns bind when they can produce a common name. |
| `srcPathMask` | A regex over the full landing path, applied symmetrically with the ingestion's own `srcPathMask`, for cases the folder prefix cannot express. |

`output` is the singular convenience for the one-folder case; `outputs` is the list. A file set nothing consumes still records its own file node, so it is visible and binds automatically once a matching ingestion is added. Many files landing in one folder need only a single entry with a glob, since the lineage node is the folder. Declared outputs describe the lake side of a download; on an upload the remote server is the output.

## See also

- [Copy flow](cpy.md)
- [Flow types overview](overview.md)
