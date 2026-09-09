---
id: wiki-census-drift
title: "Census drift map: keys the engine has that keys.api.json does not"
type: map
summary: "Nine key paths the api engine accepts that keys.api.json does not declare, plus an enum a version behind; the census drives editor completion."
keywords:
  - keys.api.json
  - census
  - drift
  - lsp
  - vscode
  - autocomplete
  - incremental sql
  - concurrency
sourceRefs:
  - src/SqlFlow.Core/Acquire/AcquireFlow.cs
  - src/SqlFlow.Yaml/YamlAcquireFlowLoader.cs
  - src/SqlFlow.Acquire/Engine/AcquireEngine.cs
  - src/SqlFlow.Acquire/Engine/HttpTransport.cs
  - tools/sqlflow-lang/src/census.rs
rawRefs:
  - docs/reference/flow/keys.api.json
related:
  - wiki-ignored-yaml-keys
  - wiki-api-resume-and-watermarks
  - wiki-design-doc-drift
updated: 2026-09-09
---

# Census drift map: keys the engine has that keys.api.json does not

`docs/reference/flow/keys.api.json` declares 121 key paths for `flowType: api`. The engine accepts
more than that. This matters more than a normal documentation gap, because the census is not only
documentation: `tools/sqlflow-lang/src/census.rs` embeds these files, and the LSP and VSCode
extension complete and validate against them. A key missing from the census is a key the editor will
flag or fail to offer, even though the engine binds it correctly.

Every key below was verified present in the source tree, and every one is already used by at least
one production flow.

## Missing from the census

| Key path | What it does | Production use |
| --- | --- | --- |
| `source.reliability.concurrency` | bounds in-flight requests | 10 folders |
| `source.pagination.keysetIdHeader` | next keyset id read from a response header, for non-JSON bodies | `entur/` |
| `source.pagination.pageVariable` | binds the page number as a template variable | `questback/` |
| `source.iterate[].idBindings` | binds several fields per discovered id, not just the id | `questback/` |
| `source.iterate[].overlapMinutes` | re-reads a tail of the previous window | `citybike/` |
| `source.request.responseCharset` | forces response decoding for a mislabelled charset | `kommunedata/`, `svv/` |
| `incremental.connection` | the connection the SQL watermark probe reads | `questback/` |
| `incremental.query` | the verbatim scalar query supplying the resume point | `questback/` |
| `incremental.keyVariable` | the fan-out variable a per-entity watermark is keyed by | `questback/` |

## The enum that is also behind

The census documents `incremental.source` as `response | lake`. The `AcquireWatermarkSource` enum has
**three** members: `Lake`, `Response`, and `Sql`. The `sql` value, and the three `incremental` keys it
requires, are the durable-watermark mechanism described in
[api-resume-and-watermarks](../patterns/api-resume-and-watermarks.md). It is live in production, so the
census is describing a version of the feature that has been superseded.

## What is not drift

`source.reliability.retry.backoffSeconds` appears in `questback/questback_00_api.yaml` and is **not**
in this table, because it is not in the engine either. It is a key that binds to nothing and is
silently ignored. See [ignored-yaml-keys](../incidents/ignored-yaml-keys.md). The distinction matters
when reading a production YAML: presence in a shipped flow is not evidence that a key is real.

## How to check whether this page is still true

The census names its own provenance in its `generatedFrom` field: the YAML loader, the flow model,
and the acquire engine. Comparing the census key list against the properties of `AcquireFlow` and its
nested records is what produced the table above, and is what would confirm or retire it.

The drift is one-directional here: the engine is ahead. That is the expected direction, and it means
the census needs regenerating rather than the engine needing changes.
