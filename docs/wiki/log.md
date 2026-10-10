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

## [2026-09-11] ingest | Dispatch design phase 3 and the decision page

Ingested the completed `docs/dispatch-design.md` after phase 3 shipped: the KEDA scaler reads a
pool's replica target from the control plane's own `GET /api/v1/node/scale-target` endpoint with the
node token, computed from the journal on every replica by loading the queued and running rows into
the dispatcher's own in-memory state (so the gates are evaluated by one code path) and dividing the
eligible backlog by the slot count the nodes report, which removed the mssql scaler, its Go-driver
catalog secret and the `maxConcurrentRunsPerReplica` parameter; the Nodes page gained the
dispatcher's panel. Wrote the decision page
[decisions/dispatch-in-control-plane.md](decisions/dispatch-in-control-plane.md) (the why, the four
rejected alternatives, the four decisions that changed while it shipped, what generalizes), indexed
it, and updated the drift map's phased-design section to point at it. Reference pages
(`concepts/control-plane.md`, `guides/deployment.md`, `cli/worker.md`) updated in the same change;
manifest rebuilt.

## [2026-09-11] lint | An unquoted null keyword crash-looped the MCP server

The `40312a0` MCP image built cleanly and then exited at startup: `decisions/string-first-landing.md`
listed the keyword `null` unquoted, YAML read it as a null, `build_manifest.py` wrote it into the
manifest, and `DocMeta` in `tools/sqlflow-mcp/src/docs.rs` refuses a non-string keyword. Quoted it and
rebuilt the manifest. So the next one fails before a deploy rather than in a container: the manifest
builder now refuses to write an entry carrying a non-string value, `lint_wiki.py` reports non-string
frontmatter scalars and list entries, and `tools/sqlflow-mcp/build.rs` validates every entry's
field types so a bad manifest fails `cargo build` and therefore the image build.

## [2026-09-13] ingest | PowerBI model-entity resolution

Ingested the model-entity resolution work landed in `tools/pbix-extract` and `SqlFlow.Lineage`,
alongside its design document (`docs/powerai-model-entity-resolution-design.md`) and the roadmap in
`POWERAI.md`.

One new decision page: why the Power Query expression is pattern-matched in the C tool rather than
parsed, why the resulting mapping is emitted as an ordinary `SynonymLink` instead of a new graph
mechanism, and why the M-literal server is reported but never used as an estate identity.

The page records two departures from its own design document, since both are the kind of thing that
looks like an oversight later: the proposed `modelSourceServer` subscriber key was not built (the
implementation removed the need for it by never comparing the two server identities), and native
`[Query="..."]` sources are refused rather than re-parsed, because resolving them would mean a second
T-SQL parser inside the C tool.

Also recorded, because it is the honest state rather than the intended one: only the refusal path has
run end to end. Every table in the one sample report is Excel- or JSON-backed, and the C# test fixture
cannot synthesize a model source, so the resolved path is proven by the tool's own unit checks and by
the existing synonym mechanism, not by a test spanning the seam between them.

`docs/reference/flow/subscribers.md` gained the table node's new `source*` properties and a section on
what resolves and what does not; that is reference material (what the surface does), so it lives there
rather than here.

## [2026-09-13] ingest | Proving model-entity resolution, and the report format that changed underneath it

Source: a working session repointing `samples/powerbi/AdventureWorks Sales.pbix` from its original
Excel workbook onto the restored AdventureWorksDW2022 database, plus the `tools/pbix-extract` change
that came out of it.

The decision page on model-entity resolution had a "What is still unproven" section saying only the
refusal path had ever run against a real file. That is now half closed and the page says so precisely:
all seven SQL-backed tables resolve to real `dbo.*` warehouse objects, the `Json.Document` helper table
is still correctly refused in the same report, and the remaining gap is the control-plane half
(`FlowSetCollector` to `SynonymLink` to `LineageGraphBuilder`), against which no `db sync` has run.
Narrowing the claim rather than deleting it matters here, because the chain stops halfway and the
rendered SQL still being in model terms is easy to misread as a failure when it is correct at that
layer.

The page also now records two things about the repointing that a later reader would otherwise take for
mistakes: the `Customer`-to-`Sales` relationship was dropped because `FactResellerSales` genuinely has
no `CustomerKey`, and rebuilding tables under new queries cost the model a calculated column and two
visuals' field bindings.

One new incident page: extracting the re-saved report returned zero pages and zero visuals, because
Power BI Desktop now writes the visual layer as `Report/definition/...`, one document per page and per
visual, instead of a single `Report/Layout`. What generalizes is the shape of the failure rather than
the format detail. The loss arrived as a smaller number instead of an error, below the layer where the
tool's refusal discipline operates; the shared "member not found" message explained it as a missing
model, a cause that fit only the caller it was written for; and the expression vocabulary turned out to
be unchanged, so the fix was a translation of structure rather than a second implementation.

Both report shapes must stay supported, and the sample having been re-saved means the real file now
exercises the new reader while synthetic fixtures hold the old one, exactly reversing which path had
real coverage the day before.

## [2026-09-13] ingest | Two bugs found by running model-entity resolution end to end

Source: a `db sync --connect` against the repointed sample report, and the two fixes it forced.

The decision page's "What is still unproven" section had just been narrowed to "only the extractor
half". Running the other half showed the claim was too generous in the other direction: the chain had
never worked at all, and the extractor half only looked proven because the check that seemed to prove
it (grepping the emitted YAML for `sourceName`) cannot see the defect.

Two bugs, one on each side of the seam. The C tool wrote every table's `source*` properties onto the
last column node instead of its own table node, because the YAML is a stream and the sources were
written in a later pass. The graph builder registered a model entity's synonym as a one-part name with
no database, while the default-database pass filled the connection's catalog into exactly those facts
first, so the two identities never met for any connection string that names a database.

The page now records both, and what generalizes from them: each half was individually tested and
individually correct-looking, the defect lived in the agreement between them, and a seam between two
languages is where no unit test reaches. "The values are right" turned out to be a different claim
from "the consumer can read them".

Both POWERAI.md Section 10 and this page previously said the extractor half was proven; both are
corrected rather than quietly amended, since the earlier claim is exactly the kind a later reader
would otherwise rely on.

## [2026-09-15] ingest | The semantic layer is the column allow-list

Wrote [The semantic layer is the column allow-list](decisions/semantic-layer-is-the-allow-list.md) from the
semantic layer implementation (`SemanticLayer.cs`, its read and admin endpoint files, the chat tool allowlist in
`AssistantSettings.cs`) and the two design decisions the human made while it was built: the allow-list as the
layer's only membership, and the chat assistants losing the raw schema tools rather than the shared endpoints
being filtered. It also records the no-tracking update bug the new integration tests exposed in the column policy
upsert. The surface itself is documented in the reference page `concept-semantic-layer`.

## [2026-09-15] ingest | The assistant surface closes the text leaks

Extended [The semantic layer is the column allow-list](decisions/semantic-layer-is-the-allow-list.md) with why the
chat's operational tools and its two data-operations tasks are narrowed by an advisory `surface=assistant` marker
(`AssistantScope.cs`, `control_plane.rs`) rather than removed, given a separate token, or redacted for everyone.
The mechanism is documented in the reference page `concept-semantic-layer`.

## [2026-09-15] ingest | PowerAI retrieval design drift

Mapped [docs/powerai-question-retrieval-design.md](../powerai-question-retrieval-design.md) into the
[design document drift map](maps/design-doc-drift.md). Its status line, Section 7, and Section 8 step 6
still describe the confirmed-example table and the GUI confirmation row as unbuilt; the code shows both
(`CatalogQuestionExample`, `AnswerConfirmation.tsx`), plus the admin Saved answers page added the same
day. The raw document is left as written. The map now links the semantic layer decision page, which had
no inbound `related` link.

## [2026-09-15] ingest | Saved answers and Power BI models join the semantic layer

Extended [The semantic layer is the column allow-list](decisions/semantic-layer-is-the-allow-list.md) with two
decisions the human made: the saved answers are stored in the semantic layer (`QuestionExample` renamed to
`SemanticExample` by a hand-written rename migration, curation moved to the Semantic layer page, the confirm and
auto-run routes kept), and a Power BI report's model is served on the warehouse table each model table loads from
(`describe_semantic_table`'s `reportModels`), graded against the column allow-list, rather than imported as curated
annotations or served ungraded. The surfaces are documented in `concept-semantic-layer` and `flow-subscribers`.

## [2026-09-16] ingest | Power BI report specifications move into the semantic layer

Wrote [A Power BI report travels as its specification](decisions/powerbi-report-specifications.md) from the
human's question of whether `pbix-extract` should move into the semantic layer, and the design settled with them:
the tool stays, its specification becomes the stored and shipped unit (`ReportSpecs.cs`, `CatalogSemanticReportSpec`,
the `AddSemanticReportSpecs` migration), GUI uploads are read by the isolated `SqlFlow.PbixExtractor` service, and
`sqlflow powerbi extract|publish` serves report owners without a C toolchain. It records the defect that forced the
change (the managed sync erasing a model a developer sync had written) and the subscriber-library fingerprint that
closed a second gap found on the way. The surfaces are documented in `flow-subscribers`, `concept-semantic-layer`,
and `cli-control-plane`.

## [2026-09-16] ingest | Schema registration flows (flowType: sch)

Wrote [An external database is registered by a flow, and the dashboard supplies the model](decisions/schema-registration-flows.md)
from the design settled with the human while closing PowerAI's gap on databases no flow loads: a new `sch` flow
kind instead of scm or inv, the catalog rather than the semantic layer, only tables and views because the
dashboard supplies joins and computed values, and linking registered-source subscribers by database metadata. It
also records the harvester and object-write extractions and the alias remap that had been dropping subscribers.
The surfaces are documented in `flow-sch`, `flow-subscribers`, `concept-data-operations`, and
`concept-lineage-graph-and-plan`.

## [2026-09-16] ingest | Visual SQL translation

Wrote [A report visual's query is translated into T-SQL at sync, or refused with a reason](decisions/visual-sql-translation.md)
from the choices the human made while closing the gap schema registration left open: T-SQL as the only output, a
common DAX subset, and the translation served as a dashboard match's runnable SQL. It records the refusal rules for
Power Query and DAX, the shared expressions and literal rendering added to `pbix-extract`, and the per-subscriber
query lookup fixed on the way. The surfaces are documented in `flow-subscribers` and `guide-chat-assistant`.

## [2026-09-16] lint | PowerAI design-doc status refresh

At the human's request, refreshed the status notes of the three PowerAI design documents under `docs/` that
still described landed work as open: the retrieval design (the GUI confirmation row exists, and the example
table now lives in the semantic layer as `CatalogSemanticExample`), the SQL-resolution handoff (the end-to-end
`db sync` was run on 2026-09-13 and found the two seam bugs), and the model-entity resolution design (step 5,
the SQL-backed sample report, is done). Only status lines and step markers changed; the design text stays as
the record. Updated [the design document drift map](maps/design-doc-drift.md) to say the retrieval document's
three stale claims are now corrected in place.

## [2026-09-17] ingest | MCP tool consolidation and the capitals convention

Merged the seven single-surface search tools into two that take a `surface` argument, splitting them along the
privacy boundary the assistant allowlist already drew rather than into one tool: `search` pages the ETL-side
surfaces (flows, files, statements) that the chat surfaces are given, and `search_schema` pages the warehouse
schema surfaces (objects, columns, definitions, flowColumns) that they are denied, because those see every
column regardless of the semantic layer's allow-list. A single merged tool could not express that split, since
`McpOptions` gates by tool name and not by argument. 93 tools became 88.

Also rationed capitals across every description, from 222 emphasized words to 100. They had stopped carrying
information: `START HERE` sat on two tools that answer different questions (`get_semantic_layer`, which now
keeps it, and `search_all`, which lost it), and `USE THIS` on three that overlap. The convention (capitals for
a routing lead and a real safety constraint, every other contrast in lowercase prose) is documented above
`GROUNDING_RULE` in `server.rs` and enforced by `capitals_are_rationed_to_routing_and_safety`, which caps the
per-description budget and asserts at most one tool claims `START HERE`. Updated
[the semantic layer allow-list decision](decisions/semantic-layer-is-the-allow-list.md) for the renamed tools.
