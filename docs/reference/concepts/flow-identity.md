---
id: concept-flow-identity
title: "Flow identity: deterministic FlowId and per-run RunId"
type: concept
summary: How SQLFlow derives a stable FlowId from the flow name (RFC 4122 v5 GUID) and stamps a fresh RunId on every execution.
keywords:
  - flowid
  - runid
  - version 5 guid
  - deterministic
  - rename
  - identity
  - sysalias
related:
  - concept-shadow-catalog
  - concept-run-artifacts
  - flow-overview
sourceRefs:
  - src/SqlFlow.Core/Identity/FlowIdentity.cs
  - src/SqlFlow.Core/Model/FlowDefinition.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Yaml/YamlFlowLoader.cs
  - src/SqlFlow.Yaml/YamlDocumentParts.cs
  - src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs
  - src/SqlFlow.Catalog/CatalogIdentity.cs
  - tests/SqlFlow.Core.Tests/FlowIdentityTests.cs
---

# Flow identity: deterministic FlowId and per-run RunId

Every flow has two identifiers with opposite lifetimes:

- **FlowId** is the flow's stable identity. It is identical on every execution, on every machine, with no database and no write-back. Logs, lineage, run history, and file-log dedup all join on it across runs.
- **RunId** is minted fresh for each execution. It attributes events, log scopes, and results to one specific run, so concurrent runs of the same flow stay individually distinguishable.

FlowId answers "which pipeline is this"; RunId answers "which execution of it".

## How FlowId is computed

`FlowIdentity.FromName` (src/SqlFlow.Core/Identity/FlowIdentity.cs) computes an RFC 4122 **version 5** (SHA-1 name-based) GUID over a fixed namespace plus the UTF-8 bytes of the flow name:

- The namespace is the constant `4f1f6b1e-8a2d-4c3b-9e7a-2c5d8f0b1a63`. It never changes; changing it would re-key every flow that relies on the computed identity.
- The SHA-1 hash of namespace-plus-name is truncated to 16 bytes; the version nibble is set to 5 and the variant bits to the RFC 4122 form. Byte order is swapped to and from RFC network order around the hash so the result matches other RFC 4122 v5 implementations.
- The same name always yields the same GUID; different names yield different GUIDs. A null or whitespace name throws.

`FlowDefinition.FlowId` (src/SqlFlow.Core/Model/FlowDefinition.cs) is a computed property: `FlowIdentity.FromName(Name)`. It is **not authored in YAML**; there is no YAML key, CLI flag, or environment variable that sets it. The `name:` key is the sole input.

Consequence: **renaming a flow yields a new identity by design.** History recorded under the old name stays keyed to the old FlowId.

### Pinned identities

`FlowIdentity.Resolve(explicitId, flowName)` returns the explicit GUID when a caller supplies a non-empty one, and falls back to the name-derived GUID when the explicit id is null or `Guid.Empty`. It is a programmatic escape hatch for a caller that already holds an identity. No loader or CLI path in this repository calls it today (only tests exercise it directly, in tests/SqlFlow.Core.Tests/FlowIdentityTests.cs and tests/SqlFlow.Core.Tests/MiscCoreEdgeCaseTests.cs); the YAML flow loader always computes the identity from the name via `FromName` and never pins one.

## RunId: per-execution, time-ordered

- The file-flow engine (`FlowRunner`, src/SqlFlow.Core/Engine/FlowRunner.cs) mints `Guid.CreateVersion7()` per plan or run call. Version 7 GUIDs are time-ordered, so run ids sort chronologically. An orchestrator (the control-plane trigger) may pass an existing RunId into `RunAsync` so the recorded run resolves under the id it already handed the caller; every direct CLI run mints a fresh one.
- The relational ingestion runner (`IngestionFlowRunner`, src/SqlFlow.SqlServer/Ingestion/IngestionFlowRunner.cs) uses `options.RunId ?? Guid.NewGuid()`.

`FlowRunner` stamps both ids on its logging scope (`Flow {FlowName} {FlowId} ({RunId})`) and on the diagnostic activity (`flow.id`, `flow.run_id` tags on both the `flow.plan` and `flow.run` activities), so a log line or trace span alone identifies the pipeline and the specific execution. `IngestionFlowRunner` carries no `ILogger` scope or `Activity` tags; it writes to its own run-event sink instead, and its `run.start` event embeds the identities as text: `ingestion '<name>' (flow <FlowId>, run <runToken>): ...`.

The ingestion runner also embeds the flow identity in its canonical staging table name: `[raw].[{targetSchema}_{targetTable}_{FlowId}]`, where `FlowId` is the document's integer id (see below). The staging table is per flow, not per execution, so the RunId does not appear in it; the run identity lives in the run log and the `run.start` event.

## The integer flow id for relational document kinds

The relational document kinds (`ing`, `exp`, `sp`, `inv`, `hc`, `scm`, `batch`; dispatched in src/SqlFlow.Yaml/YamlDocumentLoader.cs) model the legacy control-plane schema, where `FlowID` is an `int`. Their loaders derive a stable positive integer from the same name-based GUID via `YamlDocumentParts.StableFlowId` (src/SqlFlow.Yaml/YamlDocumentParts.cs):

```text
StableFlowId(name) = first 4 bytes of FlowIdentity.FromName(name) as Int32, masked with 0x7FFFFFFF
```

So logs and staging tables key consistently across runs without a database to assign ids. The document's `name:` becomes `SysAlias` when present. Six of the seven kinds (`exp`, `sp`, `inv`, `hc`, `scm`, `batch`) require it and fail validation with `'name' is required for <kind>` when it is blank (`YamlDocumentParts.RequireFlowName`). The seventh, `ing`, treats `name:` as optional: an ingestion document that omits it gets `FlowId = 0` and `SysAlias = null` instead of failing (src/SqlFlow.Yaml/YamlIngestionFlowLoader.cs). The ad hoc `healthcheck` CLI path applies the same masking to a name of the form `database.schema.table` (src/SqlFlow.Cli/Program.cs).

## Propagation into source metadata

The file-flow YAML loader (src/SqlFlow.Yaml/YamlFlowLoader.cs) stamps the computed GUID into the source options bag:

```text
options["flowId"] = FlowIdentity.FromName(name).ToString()
```

The per-format source metadata records (`PreIngestionCsv`, `PreIngestionJsn`, `PreIngestionXml`, `PreIngestionXls`, `PreIngestionParquet` in src/SqlFlow.Core/Model) parse that option into their `FlowId` property, described in code as "the universal key in the metadata store". A record built without the option carries `Guid.Empty`.

## Catalog identities built on the same primitive

The catalog derives further deterministic ids from `FlowIdentity.FromName` over composite names (src/SqlFlow.Catalog/CatalogIdentity.cs): a catalog pipeline id from `{repoId:N}/{flowName}` and a schedule id from `{repoId:N}/{flowName}/schedule`. Catalog run rows store the pipeline's stable id as a soft link (no foreign key), so a run record survives even if the pipeline row is later removed.

## Configuration touchpoints

| Touchpoint | Effect on identity |
| --- | --- |
| `name:` (YAML, all flow kinds) | Sole input to FlowId (GUID for file flows, masked int for relational kinds); also becomes SysAlias when present. Required for file flows and six of the seven relational kinds; optional for `ing`, which falls back to `FlowId = 0` / `SysAlias = null`. |
| `source.options.flowId` | Written by the loader, not authored; carries the GUID into PreIngestion metadata records. |
| CLI | No flag sets or overrides FlowId. `sqlflow run` executions mint their own RunId. |

## Example

Given this flow (adapted from samples/quickstart/orders.flow.yaml):

```yaml
name: orders

source:
  type: csv
  location: ./orders.csv
  options:
    delimiter: ","
    header: true

target:
  connection: ${env:SQLFLOW_DW}
  schema: dbo
  table: Orders
```

`FlowIdentity.FromName("orders")` yields the same version 5 GUID on every machine and every run, `id.Version == 5`, and the variant octet has the `10xx` high bits (asserted in tests/SqlFlow.Core.Tests/FlowIdentityTests.cs). Running it twice produces two distinct RunIds under the one FlowId:

```bash
sqlflow run orders.flow.yaml
sqlflow run orders.flow.yaml
```

Renaming the flow to `name: orders_v2` produces a different GUID; run history recorded under `orders` does not follow it.

## See also

- [Shadow catalog](./shadow-catalog.md)
- [Run artifacts](./run-artifacts.md)
- [Flow overview](../flow/overview.md)
