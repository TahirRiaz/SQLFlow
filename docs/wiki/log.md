# Wiki log

Append-only, chronological. Newest entries go at the bottom. Every entry starts with the same
prefix so the log stays greppable:

```
grep "^## \[" docs/wiki/log.md | tail -5
```

Entry format, enforced by [lint_wiki.py](lint_wiki.py):

```
## [YYYY-MM-DD] <ingest|query|lint> | <short title>
```

## [2026-09-09] ingest | Wiki instantiated from the Karpathy LLM wiki pattern

Source: Karpathy's `llm-wiki` gist (https://gist.github.com/karpathy/442a6bf555914893e9891c11519de94f).

Established the three-layer split for this repository: raw sources are `src/`, the historical
design documents under `docs/`, and the git history; the wiki is `docs/wiki/`; the schema is the
"Internals Wiki" section of `CLAUDE.md`. Chose `index.md` as the only navigational surface rather
than a second `manifest.json`, so `docs/reference/build_manifest.py` stays the single manifest
builder and the MCP corpus stays purely code-verified.

Created `index.md`, `log.md`, `lint_wiki.py`, and the `narratives/ decisions/ incidents/ maps/`
taxonomy.

## [2026-09-09] ingest | Design documents under docs/ and their drift banners

Sources: the nine documents directly under `docs/`, read for their drift banners and status.

Wrote [maps/design-doc-drift.md](maps/design-doc-drift.md). Finding worth surfacing: `flowType: api`
has a key census (`docs/reference/flow/keys.api.json`) but no reference prose page, so the
unbannered `docs/acquisition.md` is the only prose describing it and is load-bearing rather than
historical. Every other design document under `docs/` carries an explicit banner deferring to
`docs/reference/`.

## [2026-09-09] ingest | String-first landing as a deliberate decision

Sources: `docs/reference/concepts/type-inference.md`, `docs/reference/concepts/pre-ingestion-transform.md`.

Wrote [decisions/string-first-landing.md](decisions/string-first-landing.md), recording the
rationale, the Parquet exception, and the downstream consequence that a ported predicate written
against a legacy empty-string convention silently changes meaning.

## [2026-09-09] ingest | Production pattern estate (dwh-pipelines-prod)

Source: the production pipeline repository, 751 flow documents across 39 source folders, harvested
structurally rather than sampled (669 distinct key paths, 0 parse errors).

Wrote the ten pattern pages and [maps/pattern-catalog.md](maps/pattern-catalog.md), which indexes a
data-engineering problem onto the SQLFlow shape that solves it and the production folder that proves
it. Frequencies quoted on the pattern pages are counts from that harvest.

Two findings came out of verifying production keys against the source tree rather than trusting the
YAML: [maps/census-drift.md](maps/census-drift.md) records nine key paths the engine accepts that the
api key census does not declare (plus the `incremental.source: sql` enum value), and
[incidents/ignored-yaml-keys.md](incidents/ignored-yaml-keys.md) records a live production flow
configuring `retry.backoffSeconds`, which exists nowhere in the engine and is silently ignored.

## [2026-09-09] lint | Wiki wired into the MCP corpus

Reversed the earlier decision to keep the wiki out of the indexed corpus: it is now embedded and
searchable alongside the reference pages. `build_manifest.py` scans both trees into one
`manifest.json`, each entry carrying a `corpus` field naming which root its path is relative to;
`tools/sqlflow-mcp/build.rs` resolves the two roots when embedding bodies. `docs.rs` needed no change
because serde ignores unknown manifest fields. The `.dockerignore` and `Dockerfile.mcp` now carry
`docs/wiki` into the image context, without which the container build would have failed on a path
that does not exist.

Added the `pattern` page type to the lint and to the manifest builder's type validation, which now
also reports a page whose type belongs to the other corpus.

## [2026-09-10] ingest | Composition grammar from the lineage graph

Source: `sqlflow lineage` over the production estate (708 flows, 1,495 objects, 2,175 edges, 282 flow
dependencies, 4 waves), rather than inference from the YAML.

The graph settled how flows actually compose. There is no `dependsOn`: flows are joined by naming the
same artifact, and exactly two joints do all the work. A lake path binds every file producer (`api`,
`cpy`, `sftp`) to every file consumer, with `abfss://`, `https://` and `az://` normalized to one
canonical key, which is why a producer and consumer written in different URI forms still bind. The
pre view binds a file flow to its `ing` flow, named `v_` + the file flow's `target.table`.

Measured hand-offs: `cpy`->`file` 96, `api`->`file` 40, `sftp`->`file` 6, `sftp`->`cpy` 1,
`cpy`->`cpy` 2, `file`->`ing` 124, `ing`->`ing` 13. Written up in
[narratives/chaining-flows-through-the-lake.md](narratives/chaining-flows-through-the-lake.md).

Two things the graph made visible that the YAML alone did not: `sftp` flows need an explicit `output`
block or the graph has a hole where the data enters, and `sp` flows contribute no edges without
`--connect`, so a chain running through one looks broken when it is not.

## [2026-09-10] ingest | Twelve code-first recipes

Populated `narratives/` as runnable recipes rather than prose: connecting any source, the three
end-to-end source shapes (api, vendor files, database), incremental load, staging to silver,
dimensions and surrogate keys, fan-in to a shared target, backfill and replay, export and delivery,
quality monitoring, and inspect/debug.

All examples use generic table and column names so a recipe reads as a reusable shape rather than as
estate documentation; production provenance stays in the pattern pages' exemplar tables.

Two areas were added beyond what was asked, because the estate exercises them and nothing covered
them: getting data back out (`exp`, `trl`, `inv`, plus the `subscribers` registry that answers what
breaks), and noticing a run that succeeds while being wrong.

Findings recorded along the way: `incremental.columns` silently ignores `overlapDays`, because only
an `IsDate` mark receives the `DATEADD`, so the rewind that shape actually has is `lookback`. And a
fan-in to one target starves every writer but the furthest ahead unless each scopes its probe with
`source.incrementalClause`; total row count keeps growing throughout, so only per-discriminator
counts reveal it.

The lint's `sourceRefs` tripwire caught two invented paths in this pass before they shipped.

## [2026-09-11] ingest | Dispatch design document

Ingested `docs/dispatch-design.md`, written and implemented the same day: the run queue leaves SQL
Server for an in-memory dispatcher inside the control plane, journaled to the catalog with plain
conditional updates, with compute nodes pulling work over an HTTP node protocol under a `node` scope
and holding leases instead of claiming rows. Updated the design-doc drift map with a section for a
phased design whose status line tracks what has shipped (phase 1 as of this entry), so a reader does
not mistake its "what exists today" sections for the current code. The reference pages that carry the
shipped behaviour (`concepts/control-plane.md`, `cli/worker.md`, `concepts/architecture-and-execution.md`,
`concepts/environment-variables.md`, `guides/deployment.md`) were rewritten in the same change and the
manifest rebuilt; no new wiki page was written for the decision itself yet (that is the design's
phase 3), so the map is the only wiki pointer to it for now.

## [2026-09-11] ingest | Dispatch design phase 2: the node needs only the control plane

Re-ingested `docs/dispatch-design.md` after its phase 2 shipped: every hand-out now carries the run's
execution spec (read from the catalog before the hand-out is journaled, so a failed read consumes no
attempt), the snapshotted YAML is fetched by hash, the watermark table and landing-reset verdict are
resolved by a context call the node makes after parsing the document, and the live trace streams in
batches, all over the node protocol, so `SqlFlow.Node` no longer references the catalog and
`sqlflow worker` no longer takes `--db`. Updated the drift map's phased-design section (phases 1 and
2 shipped, phase 3 owed, three decisions changed during implementation, the third being that the
lineage facts are resolved on a separate call rather than at hand-out time, because only the parsed
document says whether the flow participates). The reference pages that carry the shipped behaviour
(`cli/worker.md` rewritten, `concepts/control-plane.md`, `concepts/architecture-and-execution.md`,
`concepts/environment-variables.md`, `concepts/shadow-catalog.md`, `guides/deployment.md`) were
updated in the same change and the manifest rebuilt. The wiki decision page still waits for phase 3.
