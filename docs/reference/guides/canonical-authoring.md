---
id: guide-canonical-authoring
title: "Canonical flow authoring: the simplest design that the engine already owns"
type: guide
summary: "The design loop for creating or changing a pipeline (scaffold or discover, edit, validate, propose) and the declare-intent-not-mechanism rules: incremental via the incremental block, upsert via load.keyColumns, narrowing via source.filter, and never macro tokens or hand-written watermark SQL."
keywords:
  - canonical
  - authoring
  - design
  - new pipeline
  - scaffold
  - incremental
  - watermark
  - upsert
  - keycolumns
  - macro
  - best practice
related:
  - flow-incremental
  - flow-ing-schema-incremental
  - guide-incremental-and-backfill
  - concept-upsert-and-change-detection
  - cli-catalog-scaffold
  - cli-validate
sourceRefs:
  - src/SqlFlow.Core/Catalog/CatalogScaffolder.cs
  - src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.Yaml/YamlDocumentLoader.cs
  - src/SqlFlow.ControlPlane/Api/FlowProposalEndpoints.cs
  - tools/sqlflow-lang/src/features.rs
---

# Canonical flow authoring

A `.flow.yaml` declares WHAT moves; the engine owns HOW. Nearly every off-canon flow comes from re-implementing, in hand-written SQL, a mechanism the engine already provides declaratively. This guide is the short path to the design the engine expects, for a new pipeline and for changing an existing one.

## The design loop

1. **Start from a generator, not a blank file.**
   - A relational (table-to-table) source: `sqlflow catalog scaffold --source <ref> --object db.schema.table --target-object raw.Table --detect-keys` emits a runnable `flowType: ing` document with the introspected key columns and candidate incremental columns (src/SqlFlow.Core/Catalog/CatalogScaffolder.cs). Over MCP this is the `scaffold_ingestion_flow` tool.
   - A JSON/NDJSON/XML file source: `sqlflow flatten <sample>` emits a runnable file-flow stub with the record grain auto-detected. Over MCP this is `discover_source`.
   - Neither fits (an acquisition, an export, a copy): copy the closest existing flow in the estate and adapt it. An estate sibling is the convention; an invented shape is not.
2. **Edit only what the generator could not know**: the schedule, the batch label, the incremental column choice, assertions.
3. **Validate before anyone sees it**: the MCP `validate_flow` tool (also the LSP and the VS Code extension, which share its engine) reports parse errors, unknown/misplaced keys, invalid enum values, AND the canonical-pattern lints below; `sqlflow validate <file>` additionally runs the engine's own loader validation. Fix everything they report.
4. **Propose, never push**: the MCP `propose_pipelines` tool (or `POST /api/v1/repos/sources/{id}/proposals`) opens a pull request. The control plane preflights every file with the same loader the sync uses and rejects a proposal whose flow would not import; a human reviews and merges; the managed sync imports the flows.

## Declare intent, never mechanism

| You want | The canonical declaration | Never |
| --- | --- | --- |
| Incremental load (ing flow) | `incremental:` with `columns:`, or `dateColumn:` + `overlapDays:`, or `lookback:` for numeric keys | A watermark predicate in `source.filter` or a `SELECT MAX(...)` subquery |
| Incremental load (file flow) | `incremental:` with `dateColumn`/`overlapDays` (file dates) or `watermarkColumn` (row-level) | A hand-written bound in `source.options.query` |
| Upsert / dedup | `load.keyColumns:` | Hand-written MERGE in hooks |
| Narrow the read | `source.filter:` (static predicates only, e.g. `AND SystemID = 13`) | Encoding the watermark there |
| Reload everything once | `incremental.fullLoad: true`, or `--full` on the run | Deleting the incremental block |
| Two flows into one fact | Both flows share `target` and `load.keyColumns`; each declares its own `incremental` block | One bespoke query stitching both sources |

The engine probes the watermark on the target (or the anchored downstream table), composes the WHERE clause, and combines it with `source.filter` automatically (src/SqlFlow.SqlServer/Ingestion/IncrementalWindowResolver.cs, src/SqlFlow.Core/Engine/FlowRunner.cs). A hand-coded watermark bypasses the probe, the overlap window, the run report's watermark line, and the backfill parameters.

## There are no macros

SQLFlow performs **no macro or parameter expansion** in any SQL it executes: not in `source.query` (trl), not in `source.filter`, not in hooks, not in `source.options.query`. A token like `@sf_incremental_watermark` is not a SQLFlow feature; it reaches the database verbatim as an undefined variable and the statement fails. `validate_flow` flags such tokens as errors (`flow-invented-macro`). The only substitutions that exist in a flow document are `${env:...}` / `${keyvault:...}` secret references and, in acquisition landing templates, `{...}` path tokens.

## Changing an existing flow

- Change the smallest declaration that expresses the intent, in the flow's existing file. Do not add a parallel flow for a behavior the existing flow can declare.
- A flow's `source` and `target` endpoints are the design. The proposal preflight compares a revised flow against the catalog and calls out any endpoint change in the pull request, so a repoint is always a visible, deliberate decision, never a side effect.
- A key the loader does not know is silently ignored (`IgnoreUnmatchedProperties`), so a misplaced key reads as "the option did nothing". `validate_flow` names the documented home of a misplaced key (`flow-misplaced-key`); run it after every edit.

## See also

- [File flow incremental](../flow/incremental.md) and [ing schema/incremental/initLoad](../flow/ing-schema-incremental.md): the two incremental blocks in full.
- [Incremental and backfill guide](incremental-and-backfill.md): operating the watermark (full loads, windows, file patterns) without editing YAML.
- [Upsert and change detection](../concepts/upsert-and-change-detection.md): what `load.keyColumns` drives.
