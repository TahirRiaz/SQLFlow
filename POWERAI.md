# PowerBI Metadata Harvesting and Text-to-Query: Design and Roadmap

Status: implemented; what remains is breadth and deployment, not design. Extraction is done and
landed (both the `.pbix` semantic model and its report/visual layer), the report structure is
retrievable over MCP (`describe_subscriber_report`), and every visual can carry 1-3 LLM-generated
business questions (Section 9, step 3). Retrieval over those questions is built, and so is the
confirmed-example store behind it (`find_similar_questions` reads both halves, `confirm_question`
writes the confirmed one); the GUI asks a person to confirm on every answer that hands back a query,
closing the learning loop. Two items are open: extraction is proven against one report, one Power BI
version, and one source shape (`Sql.Database`), and the isolated extractor is not yet deployed to the
Azure estate. Section 10 states exactly what is built versus what remains. For the systems this feature builds on, see
[docs/reference/flow/subscribers.md](reference/flow/subscribers.md) and
[docs/reference/concepts/data-operations.md](reference/concepts/data-operations.md).

## 1. The goal

Build a text-to-query system: a user types a business question in plain language, and SQLFlow
returns (or helps write) the SQL that answers it, informed by real prior usage rather than a blind
guess against the schema.

## 2. Why PowerBI is the starting source of truth

A PowerBI report is a record of which business questions were already asked and already answered.
Compared to the warehouse schema alone, a report additionally encodes:

- **Which questions get asked.** A chart titled "Revenue by Region over Time" is a business
  question with its shape already decided: revenue, split by region, over time.
- **Which fields actually matter together**, out of everything a table exposes, and which other
  tables they get joined against to answer real questions.
- **The business vocabulary**: what people call things ("Net Revenue", "Active Riders") versus the
  column names the warehouse actually uses. This is the translation layer a text-to-query system
  needs to turn a typed question into a query.
- **Measures**: business logic (a DAX formula for "Net Revenue", including its filters and
  exclusions) that exists nowhere else in the estate.
- **Field ROLE**: whether a field is the axis a chart is broken down BY or the value it plots (the
  difference between "sales by month" and "months by sales"). This is the one fact parsed SQL
  cannot recover on its own, and it is why the visual layer is extracted at all, not only the
  semantic model.

This is the framing to keep: the target is not "PowerBI's data", it is "the questions people already
decided were worth asking, and how they answered them."

## 3. What already exists to build on

This feature extends the existing subscriber pipeline, not a parallel one, per the Single Code Path
Principle.

- **`subscribers.yaml`** ([docs/reference/flow/subscribers.md](reference/flow/subscribers.md))
  declares a PowerBI report as a `CatalogSubscriber` with `type: PowerBI`, an owner, a URL, and a
  list of named queries with their SQL. A subscriber can additionally declare `pbix:`, naming either
  one `.pbix` file or a directory of them (Section 4); every report found is extracted under that
  one subscriber automatically. Hand-transcribed queries still work and are kept alongside an
  extracted report on the same subscriber.
- **Consumption lineage.** Every subscriber query, hand-written or extracted from a report's
  visuals, is parsed the same way a view body is: the tables it touches become real lineage edges
  (`CatalogLineageEdge`), and the joins it writes feed the interpreted data model
  (`CatalogObjectRelationship`, with `Origin`, `Tier`, and `Occurrences`) exactly like a warehouse
  view's joins would.
- **The MCP retrieval surface** already exists and already frames itself around text-to-query:
  `describe_object` (identity, columns, interpreted key, generating code, relationships),
  `get_table_joins` (ranked join paths between tables, explicitly "the observed predicates ARE the
  data model"), `list_subscribers` / `describe_subscriber` (what a given report reads and the SQL it
  runs). **Extended** with `describe_subscriber_report` to surface the report/page/visual/field
  tables (Section 10); the model spec (measures, relationships, calculated columns, table sources)
  is stored at sync and served by the semantic layer (`describe_semantic_table`'s `reportModels`) on the
  warehouse table each model table loads from.
- **The safety net.** Ad-hoc query execution
  ([docs/reference/concepts/data-operations.md](reference/concepts/data-operations.md)) is read-only
  by construction (single `SELECT`, always run inside a rolled-back transaction) and gated behind
  human confirmation and the `ControlPlane__DataOps__Enabled` switch. Any query this feature proposes
  must go through that same gate, not a new one.

What does NOT exist yet: a field to capture the *business question* a query answers (the current
`queries[].name` is a short label, not a question), a store for confirmed question-to-query pairs,
any confidence-scoring or feedback loop, and DAX measure evaluation (the DAX text is extracted and
stored verbatim, but nothing interprets it).

## 4. Format decision: PBIP/TMDL vs `.pbix` (settled)

Settled: extraction targets `.pbix` directly, not PBIP/TMDL. `.pbix` is what reports are actually
distributed as, so requiring a PBIP export first would have added a manual conversion step nobody
would reliably do. Reaching the semantic model means undoing `.pbix`'s compression rather than
reading plain text: the `DataModel` zip member is an XPress9 block stream that decompresses to an
Analysis Services backup image, whose (XML) virtual directory locates an embedded SQLite database
holding the actual metadata. This is fully solved: see Section 10 for what reads it and how.

## 5. Extraction priority (delivered against this list)

Ranked by value versus effort, based on what a text-to-query system actually needs. Status per item
reflects what Section 10 details:

1. **Measures (DAX).** Extracted, with a migration. The DAX expression is stored verbatim; nothing
   evaluates or type-checks it yet.
2. **Model relationships and cardinality.** Extracted, with a migration, tagged as `active`/inactive
   so a generated join can state whether a relationship needs `USERELATIONSHIP` to apply. Stored in
   `CatalogSubscriberModelRelationship`, not merged into
   `CatalogObjectRelationship` (see Section 7 for why they are kept apart).
3. **Calculated columns.** Extracted, with a migration.
4. **Display names.** Extracted (every table/column/measure name as PowerBI labels it). Display
   *folders* (grouping metadata) are not currently read.
5. **Visuals and pages.** Extracted: every page, every visual's chart type and title, and every
   field's role (`Category`/`Y`/`Rows`/`Values`/`Size`, etc.), plus filters (including page-level
   slicers) folded into the visual's synthesized SQL `WHERE` clause rather than stored separately.

The compatibility-view complication flagged in the original design is now addressed for the shape
that matters: a report in Import mode names MODEL entities (`Sales`), not warehouse objects, and the
table's Power Query expression is pattern-matched to recover the physical object it loads from, which
then unifies with a fully-qualified ingestion target like `[OdsDb].[arc].[Sales]` through the
existing synonym pass. Only `Sql.Database`-shaped sources resolve; an Excel-, JSON-, or
native-query-backed table stays on its model name and says so in `reportWarnings`. See Section 10's
known limitations for what remains unproven.

## 6. The learning loop (question -> guess -> confirm -> remember)

Built, including the confirmation moment (Section 10) and admin curation of saved answers in the semantic
layer (the Semantic layer page's Saved answers tab). Separate from extraction: a live
feedback loop that grows the example set from real usage, not just from PowerBI.

- A user asks a question. The system searches confirmed question/query examples (from PowerBI
  extraction and from prior confirmed answers alike) for a close match.
- **Confidence is retrieval similarity, not LLM self-rating.** LLMs are not reliable judges of their
  own correctness; a wrong query can sound exactly as confident as a right one. The trustworthy
  signal is how close the new question is to an existing *confirmed* example (embedding/keyword
  similarity), not a number the LLM invents about itself.
- Below a similarity threshold, there is no proven precedent: the system says plainly that this is
  an unverified guess and always routes it through human confirmation before anything is treated as
  correct. This is not a new gate; it is the existing DataOps human-confirmation requirement, with
  the similarity score used to decide how much a caller should trust a result already flagged for
  review, not whether review happens at all.
- On confirmation (accept, correct, or reject), only accept and correct write the question/query pair
  back into the example store, with its provenance (`powerbi` vs `user-confirmed`) and an updated
  confidence. **A rejection is not stored.** Only correct, verified answers become precedent this
  estate learns from; a query a person told us was wrong is not knowledge, and keeping it around risks
  a mistake resurfacing as if it had been checked. Rejections are not recorded anywhere: only
  confirmations are. `confirm_question` accepts `outcome="rejected"` so saying no is not an error, but
  the endpoint writes nothing and says so in its response, and the assistants are told not to call it
  for a rejection at all. Saved answers are the semantic layer's example queries
  (`CatalogSemanticExample`, table `SemanticExample`), curated by admins on the Semantic layer page's Saved
  answers tab and on each table's Examples tab, where one can be corrected or deleted.
- This makes the system self-improving under real usage. Auto-running a TRUSTED match directly
  (capped: a short server-enforced timeout and a row limit, falling back to the existing manual
  confirmation gate when it would not finish in that budget) reads `CatalogSemanticExample.SourceRef`
  (Section 8) to know which datasource to run a trusted match's SQL against. The GUI confirmation row
  sets it through a datasource picker, and when nobody picks one it is inferred from the tables the
  query reads (`DatasourceInference`).

## 7. Storage: flat confirmed examples, not a separate AST/graph store

Considered and rejected, and this decision held through implementation: storing PowerBI-derived
metadata as its own graph/AST structure, separate from `subscribers.yaml`/the catalog.

Reasoning: accepting a second source of question/query facts (user-confirmed answers, alongside
PowerBI-extracted ones) does not require a new storage shape. Both are the same shape of fact: a
question, a query, the objects/columns it touches, a provenance, and a confidence. What got built
is flat, appendable catalog tables extending the existing pattern (`CatalogSubscriberReportPage` /
`Visual` / `Field`, modeled on `CatalogSubscriberQuery`), keeping one code path for read, write,
sync, and (once built) MCP exposure. No parallel graph store exists.

PowerBI's own declared relationships are extracted (Section 5) but are deliberately **not** merged
into `CatalogObjectRelationship`: a model relationship declares the report author's intent about how
entities may be joined, while an edge in that table records a join a query actually performed.
Conflating them would put claims in the lineage graph that no executed SQL supports. The one part of
this that genuinely is graph-shaped, table-to-table join relationships inferred from real queries, is
already stored as a graph today and already walked as one by `get_table_joins`; PowerBI's declared
relationships are a second, clearly-labeled kind of fact, not a merge into that same graph.

The confirmed-example store (Section 6) landed as `CatalogSemanticExample` and followed this same
flat-table precedent exactly: one appendable table holding the question, the query, the objects it
reads, the provenance and the confidence, searched by the same pass that searches the PowerBI-derived
questions. No new storage shape was introduced.

## 8. Anticipated schema work (updated against what has landed)

Per the Catalog Schema Changes Require a Migration rule. Landed:

- `CatalogSubscriberReportPage` / `CatalogSubscriberReportVisual` / `CatalogSubscriberReportField`:
  a subscriber's report pages, visuals, and each field's role. Linked by derived string key
  (`SubscriberKey#reportFile#pageOrdinal`, then `#visualOrdinal`), not surrogate FK, because
  `CatalogTransaction.InSerializableAsync` commits a whole sync in one `SaveChanges`, so no identity
  value exists while children are staged.
- The model spec itself (measures, calculated columns, relationships with `active`/cardinality,
  columns, table sources/M expressions) is emitted by `tools/pbix-extract` on its standard output,
  read by `FlowSetCollector` during a sync, and stored in `CatalogSubscriberModelTable`,
  `CatalogSubscriberModelField`, and `CatalogSubscriberModelRelationship`, keyed by subscriber, report
  file, and table name the same way the report structure is. Each model table also records the
  warehouse object its source resolved to (`ObjectKey`), and `describe_semantic_table` serves the model
  there as `reportModels`, keeping only definitions whose every column is on the column allow-list. The same specification is also the unit that is stored and shipped: `sqlflow powerbi extract` writes it to a
  `<report>.pbix.yaml` a subscriber can declare instead of the `.pbix`, and the semantic layer keeps uploaded ones
  and the copies syncs extracted (`CatalogSemanticReportSpec`, Section 10).
- The YAML shape for that model spec, and for the report layer alongside it, is a flat `nodes:` and
  `edges:` graph rather than a name-keyed tree of `tables[].columns[]`, `measures[].table`,
  `relationships[].fromTable/toTable`, and `report[].visuals[].fields[].table/field`. The old shape
  needed a consumer to cross-reference name strings across separate top-level lists to reconstruct
  which columns, measures, and relationships were relevant to one visual; the graph shape lets a
  consumer start from one visual node and walk `projects`/`hasColumn`/`definedOn`/`relationship`
  edges outward to get exactly that visual's relevant closure with no unrelated table pulled in.
  Every node carries a `kind` (`table`, `column`, `measure`, `calculatedColumn`, `report`, `page`,
  `visual`); every edge carries a `kind` (`hasColumn`, `definedOn`, `relationship`, `hasPage`,
  `hasVisual`, `projects`) plus whatever properties that kind needs. Node ids follow the same
  derived-string-key convention as the catalog's page/visual keys above, but prefixed by the
  subscriber name and the report file (`<subscriberName>#<reportFile>#<kind-tag>:<qualifier>`), so
  ids stay globally unique across every subscriber's and every report's graph, which is what lets
  many subscribers' YAML be merged into one master graph with no collisions. This is a
  parsing-layer-only change: `tools/pbix-extract` emits the new shape and
  `src/SqlFlow.Lineage/Collection/PbixExtractTool.cs` parses it back into an in-memory node/edge
  graph internally, but reconstructs the exact same `LineageSubscriberPage`/`Visual`/`Field` records
  it always has, so the catalog tables above, `FlowSetCollector`, and everything else downstream
  needed zero changes. The flat-table decision in Section 7 and the MCP tool surface are both
  unaffected; no persisted graph database was introduced anywhere in the estate.

Landed since:

- `CatalogSubscriberReportVisual.ContentHash` and the new `CatalogSubscriberReportVisualQuestion`
  table (migration `AddSubscriberReportVisualQuestions`) hold, per visual, 1-3 LLM-generated
  business questions, following this section's flat-table precedent exactly (a small child table
  keyed by `VisualKey`, matching `CatalogSubscriberReportField`'s own shape).

Landed since that:

- `CatalogSemanticExample` (migration `AddQuestionExamples`, full-text index in
  `AddQuestionExampleFullTextSearch`): the confirmed-example store this section called for, carrying
  the question text, the SQL, the source object keys, the provenance (`powerbi` | `user-confirmed`),
  the retrieval score the answer was built from, the timestamp, and the confirming user. Flat and
  appendable exactly as Section 7 requires, with no parallel graph store.
  - It is the one subscriber-adjacent table a sync does NOT delete and reinsert. Every other
    subscriber report table is repo-scoped and replaced wholesale on each pass; a human confirmation
    is not a fact about the current contents of a repository, so a sync that no longer finds the
    report a question came from must not erase the knowledge that the question was answered
    correctly. `RepoId` is nullable for the same reason, and a repo-scoped search still sees the
    unattached rows.
  - `ContentHash` (`QuestionExampleHash.Compute`, the SHA-256 of the whitespace- and case-normalized
    question and SQL) carries a UNIQUE index, so reaffirming an answer refreshes the one row instead
    of storing a duplicate that would score identically to its twin and crowd every other match out
    of the same search.

Landed since that:

- `CatalogSemanticExample.SourceRef` (nullable, migration `AddQuestionExampleSourceRef`): the
  datasource a confirmed example's SQL runs against, in the same whole-reference shape
  (`${env:...}`/`${keyvault:...}`/`@alias`) the DataOps query surface already requires, and gated by
  the same known-reference check `PrepareQueryRequest.Reference` enforces (a caller cannot set it to
  a connection the catalog has not already declared on some pipeline). `confirm_question` accepts it
  optionally and `find_similar_questions` returns it on every match, so this is the piece Section 6's
  auto-run step needed: without it a stored example carried no fact about which connection its SQL
  runs against, so auto-running a trusted match had nothing to prepare the query with. Null on a
  confirmed example stored before a datasource was chosen for it, and always null for a PowerBI-derived
  question (which names a model entity, not a live connection). A person is no longer asked for it:
  `DatasourceInference` (`src/SqlFlow.ControlPlane/Api/DatasourceInference.cs`) works it out from what
  the catalog already knows, first from the lineage object keys (a node key's first segment is the
  connection reference the object was reached through, which is also where a PowerBI visual's resolved
  model entities land), then from the tables the SQL reads, and answers only when that points at
  exactly one datasource an active pipeline declares. Confirm stores the inferred value when none was
  supplied, `find_similar_questions` fills it on matches stored without one, auto-run uses it for an
  example with none, and `prepare_query` uses it when no reference is passed (422 naming the
  candidates otherwise).
- The auto-run step itself (`POST /api/v1/powerai/questions/{id}/auto-run`, `auto_run_trusted_match`):
  runs a TRUSTED match's SQL directly, capped to the deployment's own row limit and timeout, with no
  separate prepare/approve round trip, since the SQL was already shown to and confirmed by a person
  when it was stored. Falls back cleanly to the existing manual confirmation gate when the match has
  no `exampleId`, when no datasource can be stored or inferred for it, or when the run does not finish
  inside its budget. A match is `trusted` when its score clears `RankThreshold` OR it is the same
  question as the one typed (equal sets of meaningful words, stop words dropped and inflections folded):
  a threshold on term count alone could never trust a short question, whose verbatim twin scores 1.

Still needed: nothing from this section's original list. The confirm/correct/reject flow that calls
into the store (Section 10) and the GUI control that sets `SourceRef` have both landed.

## 9. Roadmap

Sequenced, each step landing before the next starts. Strikethrough marks what has landed.

1. ~~Confirm the PowerBI file format available (PBIP/TMDL vs `.pbix`).~~ **Done**: `.pbix` (Section 4).
2. ~~Extract queries and relationships first.~~ **Done**, and gone further than originally scoped:
   built a standalone C tool (`tools/pbix-extract`, no .NET/Python dependency) that reads both the
   semantic model and the visual layer, renders each visual's question as SQL, and is invoked by
   `FlowSetCollector` (used by `sqlflow db sync` and `sqlflow lineage`). This closes consumption
   lineage for every report and feeds the join graph, plus (beyond original scope) extracts the full
   semantic model: measures, calculated columns, relationships, table sources. See Section 10 for
   the tool duplication this once created between two readers, since resolved.
3. ~~Add the "business question" field.~~ **Done**: 1-3 LLM-generated questions per visual
   (`CatalogSubscriberReportVisualQuestion`, migration `AddSubscriberReportVisualQuestions`; see
   Section 10). Generated by a new control-plane-only step (`SubscriberQuestionEnrichment`) that
   runs after a sync, never by `tools/pbix-extract` or `CatalogSync` themselves, and regenerated
   only for visuals whose content changed (`CatalogSubscriberReportVisual.ContentHash`), so an
   unchanged visual keeps its questions across syncs with no LLM call. Independently toggleable
   (`ControlPlane:PowerAI:QuestionGeneration:Enabled`, off by default, overridable at runtime from the
   semantic layer's Power BI reports tab) from the interactive chat assistant, reusing its Anthropic
   API key/model. The same tab lets a person add, edit, and delete a visual's questions; those are
   marked `manual` and generation never replaces them.
4. ~~Extract measures.~~ **Done** (Section 5), via `tools/pbix-extract`, stored in
   `CatalogSubscriberModelField` (Section 8).
5. ~~Extract declared model relationships and cardinality.~~ **Done** (Section 5), tagged `active`;
   kept out of `CatalogObjectRelationship` per Section 7, stored in `CatalogSubscriberModelRelationship`.
6. ~~**Build the confirmed-example store and the retrieval step.**~~ **Done.** A typed question is expanded by
   an LLM into related business vocabulary and the stored questions are ranked by how many of those terms they
   match, reachable as the `find_similar_questions` MCP tool; the `user-confirmed` half is
   `CatalogSemanticExample` (Section 8), written through `POST /api/v1/powerai/questions/confirm` and the
   `confirm_question` MCP tool, and searched by the SAME `QuestionSearch.FindSimilarAsync` pass rather than a
   second one. See
   [docs/powerai-question-retrieval-design.md](powerai-question-retrieval-design.md) for the design and what
   each step landed.
7. ~~**Wire the learning loop.**~~ **Done.** The store and the write path already existed, reachable over MCP,
   so a model that was TOLD to confirm an answer could record it; what was missing was the part that makes it
   happen reliably rather than opportunistically. The GUI now carries its own accept/correct/reject row under
   every finished answer that hands back a query (`gui/src/features/chat/AnswerConfirmation.tsx`), so a person
   confirms by clicking rather than by the assistant remembering to ask. Section 10 states exactly what the
   affordance does and what it deliberately does not send.
8. ~~Visuals/field-co-occurrence and default filters.~~ **Done**, and delivered earlier than
   originally sequenced (visual-layer data turned out to be the load-bearing signal: it is the only
   place a field's ROLE is recorded, and SQL-derived lineage cannot recover that fact). Every page,
   visual, chart type, title, and field role is extracted and stored; filters (including page-level
   slicers) are folded into the visual's synthesized SQL rather than kept as a separate structure.

## 10. What is built, what remains, and what a finished product needs

### Built and verified

- **One `.pbix` reader**: `tools/pbix-extract`, a standalone C binary with no .NET/Python dependency.
  Decompresses the `DataModel` (vendored MIT-licensed XPress9 decoder, decode-only), parses the ABF
  container and its embedded `metadata.sqlitedb` (via `sqlite3_deserialize`, no temp files), and
  reads the visual layer in either shape Power BI Desktop writes it (the older single
  `Report/Layout` part, or the newer split `Report/definition/...` tree of one document per page and
  per visual), keeping each visual's query and filters as an expression tree rather than flattening
  them. Renders each visual's question as one T-SQL
  `SELECT ... WHERE ...` (`sqlrender.c`, ported from the former `VisualQueryTranslator` so the output
  matches character for character), folding page-level filters into every visual on that page.
  Emits one YAML spec per report as a flat graph, `nodes:` and `edges:`, rather than a name-keyed
  tree: table/column/measure/calculatedColumn/report/page/visual nodes, and
  hasColumn/definedOn/relationship/hasPage/hasVisual/projects edges carrying the properties each
  kind needs (a column's `dataType`, a visual's rendered SQL and title, a `projects` edge's `role`,
  a `relationship` edge's join columns/cardinality/active). `reportWarnings:` stays a flat list
  naming anything dropped, since a warning is a diagnostic rather than a graph-shaped fact. Node ids
  are `<subscriberName>#<reportFile>#<kind-tag>:<qualifier>`, globally unique so many subscribers'
  and reports' graphs merge without collision (Section 8 has the full convention). Runtime schema
  probing handles PowerBI SQLite
  schema drift across versions with a stated error naming the missing table/column, rather than a
  silent empty result. Verified clean under AddressSanitizer/UndefinedBehaviorSanitizer, including
  catching and fixing a genuine upstream bug in Microsoft's reference decoder (`memcpy` over an
  overlapping range). Redacts the report author's local file paths (a
  `File.Contents("C:\Users\...")`-shaped literal) out of extracted M expressions before they reach
  disk. A report connected live to a published dataset (no `DataModel` part) still yields its full
  visual layer; the model and report halves degrade independently.
- **`SqlFlow.Lineage`'s `FlowSetCollector` invokes the tool** rather than parsing `.pbix` itself: it
  locates the binary (`SQLFLOW_PBIX_EXTRACT`, then beside the entry assembly, then `PATH`), runs it
  per declared report, and reads back its YAML, feeding each visual's pre-rendered SQL through the
  *existing* `TSqlLineageExtractor`/`ScriptFactBuilder` path, so a visual's question becomes a real
  consumption edge with no second lineage mechanism. An absent binary is a warning naming
  `SQLFLOW_PBIX_EXTRACT`, not a failure, exactly like an unreadable report; the rest of the estate's
  lineage still resolves. Wired into `sqlflow db sync` (writes the three catalog tables below) and
  `sqlflow lineage` (works with no catalog database at all).
- **Catalog storage**: `CatalogSubscriberReportPage` / `Visual` / `Field`, migrated, keyed by derived
  string (not surrogate FK, since a whole sync commits in one `SaveChanges`). A subscriber's report
  file is part of the page key, so two reports in one directory sharing a page name (the common case:
  two files from the same template) do not collide.
- **Directory-of-reports subscribers**: `pbix:` names either a file or a folder; every `.pbix` under
  a folder becomes part of the same subscriber, so a team's workspace of several reports needs one
  hand-written subscriber entry, not one per file.
- Verified end to end against a real report (AdventureWorks Sales): 8 tables / 57 columns, 1
  measure, 1 calculated column, 8 relationships, 8 table sources, 3 pages, 5 visuals, every visual
  carrying its rendered SQL (recounted on 2026-09-16 by running the tool over the sample file; the
  committed `samples/powerbi/AdventureWorks_Sales.spec.yaml` holds the same counts).
- Solution builds with 0 errors and 0 warnings. 73 checks in the C tool's own suite (`make test`
  / `make test-asan`, both clean), plus the collector-level suite in
  `LineagePowerBiSubscriberTests` (13 facts, running against the real tool in CI via a
  `pbix-extract` job, skipping loudly elsewhere when the binary is absent); the whole .NET suite
  passes.
- **An MCP surface over the report structure**: `describe_subscriber_report`
  (`tools/sqlflow-mcp/src/server.rs`), backed by `GetSubscriberReportAsync`
  (`/api/v1/lineage/subscribers/report`, `src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs`), returns
  a subscriber's pages, the visuals on each, and every field's role, nested exactly as extracted.
  Each visual's `queryName` links back to the matching entry in `describe_subscriber`'s own query
  list, so a caller goes from "this field is the Y axis" to the actual rendered SQL without text
  matching; that link needed `CatalogSubscriberReportVisual.QueryName`, added by migration
  `AddSubscriberReportVisualQueryName` since the sync had never persisted it despite the in-memory
  extraction already carrying it. `describe_subscriber`'s own description now points here. `TableName`
  on a field is still the Power BI model-entity name, unresolved to a warehouse object (known
  limitation, below). Verified against a real SQL Server instance: the migration applies, and two
  same-titled pages from different files in a directory-of-reports subscriber stay distinct by
  `reportFile`.
- **1-3 business questions generated per visual**: `CatalogSubscriberReportVisualQuestion` (migration
  `AddSubscriberReportVisualQuestions`), populated by a new control-plane-only step
  (`SubscriberQuestionEnrichment.EnrichAsync`, `src/SqlFlow.ControlPlane/Background/`) that runs
  after `CatalogSync.SyncAsync` finishes writing a repo's subscriber report rows, wired into both
  `RepoSyncService` (the managed poller) and the manual "sync now" endpoint. Neither
  `tools/pbix-extract` nor `CatalogSync`/`FlowSetCollector` changed to support this: both stay shared
  code the bare CLI's `sqlflow db sync` also runs with no Anthropic wiring at all, per the security
  posture that keeps LLM calls out of code paths the CLI runs unattended. Regeneration is
  incremental: `CatalogSubscriberReportVisual.ContentHash` (a SHA-256 of the visual's title, chart
  type, and fields, `SubscriberReportVisualHash.Compute` in `CatalogEntities.cs`) lets the enrichment
  step tell an unchanged visual (whose questions carry forward with no LLM call) from a new or
  changed one (which is sent to the model), since every sync deletes and reinserts a repo's
  subscriber report rows wholesale and preserves no other row identity across passes. The LLM call
  itself is a new `QuestionGenerator` (`src/SqlFlow.Assistant/QuestionGenerator.cs`), deliberately
  not built on `IAssistantGateway`/`AnthropicGateway`: that gateway carries an MCP tool loop, adaptive
  thinking, streaming, and a per-user MCP bearer, none of which a stateless, unattended, sync-time
  completion needs. It uses the Anthropic SDK's structured-output helper
  (`StructuredOutputModel`/`client.Messages.Create<T>`) so the 1-3 questions are parsed directly
  rather than hand-parsed JSON, and degrades to a warning (never a thrown exception) when generation
  fails, so a flaky LLM call never loses or blocks the visual/field rows the sync itself wrote. Off
  by default and independently toggleable from the interactive chat assistant
  (`ControlPlane:PowerAI:QuestionGeneration:Enabled` as the default, which an admin overrides with the
  switch on the semantic layer's Power BI reports tab), though it reuses
  `ControlPlane:Assistant:Anthropic`'s API key and model rather than declaring its own. Exposed over
  `describe_subscriber_report`/`GET /lineage/subscribers/report` as each visual's `questions` array.

### Extraction noise audit

Reviewed every field either extractor captures against the stated goal (matching a user's typed
question to the right dashboard query) and cut what does not serve it. No visual styling ever leaked
in (colors, positions, sizes were never captured by either reader), and the C tool already dropped
internal PowerBI identifiers (the page's internal generated name, visual ordinal, `queryRef`) before
this pass. Cut in this pass:

- **`measures[].displayFolder`** (`tools/pbix-extract`) and **`relationships[].crossFilterDirection`**
  (`tools/pbix-extract`): pure PowerBI UI/jargon metadata with no business-facing signal, dropped from
  the C tool's struct, SQL, and YAML emission.
- **`CatalogSubscriberReportField.QueryRef`**: the C# path was persisting PowerBI's internal
  per-visual-query alias into the catalog even though the C tool already omitted the equivalent field
  from its YAML output. Dropped from `ReportField`, `LineageSubscriberField`, and
  `CatalogSubscriberReportField` alike, so both extractors now agree; the local `queryRef` value is
  still used to *resolve* a field's column/measure in `PbixReportReader`, only the act of storing it
  downstream was removed. Needed an EF Core migration (`DropSubscriberReportFieldQueryRef`) since it
  is a catalog schema change.

Kept, despite being verbose: `tableSources[].powerQuery` (the full M expression). It is the noisiest
field in the spec, but it is also what the model-entity-to-warehouse-object resolution (Known
limitations, below) will need to read; cutting it now would mean re-adding it once that work starts.

### Known limitations (real, not hidden)

- **Model-entity resolution: proven end to end, against one report.** A
  visual names the MODEL entity (`Sales`), not the warehouse object. `tools/pbix-extract`
  pattern-matches a table's Power Query expression (`src/msource.c`) and, for a `Sql.Database`-shaped
  source, emits the physical `sourceServer`/`sourceDatabase`/`sourceSchema`/`sourceName` on the table
  node; `FlowSetCollector` turns each of those into a `SynonymLink`, so the EXISTING
  synonym-resolution pass in `LineageGraphBuilder` rewrites the bare model name onto the
  fully-qualified node an ingestion flow writes. No change was needed to the graph builder, the T-SQL
  extractor, or the SQL renderer. A table whose source is not a recognized shape (Excel, JSON, a
  native query, an unknown connector) stays unresolved and says so in `reportWarnings`, naming the
  shape, rather than being guessed at.

  **What is proven.** The sample report's model was repointed in Power BI Desktop from its original
  Excel workbook to the restored AdventureWorksDW2022 database
  (`deploy/docker/adventureworks-restore.sh`), and `sqlflow db sync . --connect` against it lands
  the report's read edges on the very objects the live harvest inventoried: `catalog.LineageEdge`
  rows reading `AdventureWorks.dbo.DimDate`, `DimProduct`, `DimReseller` and `FactResellerSales`,
  each a `Kind = Table` row in `catalog.Object` rather than a name-only node. All seven SQL-backed
  tables resolve (`Customer` to `dbo.DimCustomer`, `Sales` and `Sales Order` to
  `dbo.FactResellerSales`, and so on). The refusal path still behaves in the same report: the
  `Table` helper is report-local sort metadata sourced via `Json.Document` and is correctly reported
  unresolved. The committed evidence is `samples/powerbi/AdventureWorks_Sales.spec.yaml` (the
  `.pbix` itself is gitignored as a large binary).

  **Running it end to end is what found the two bugs that had kept it from ever working.** Neither
  was visible to a unit test, because each sat on one side of the seam between the C tool and the
  graph builder. The extractor wrote every table's `source*` properties onto the last COLUMN node
  instead of the table's own, since the YAML is emitted as a stream and the sources were written in
  a later pass; grepping the output for `sourceName` looked entirely correct, which is how it
  survived. And the graph builder would not have matched them anyway: a model entity's synonym is
  registered as a one-part name with no database, while the default-database pass fills the
  connection's catalog into exactly those facts first, so the two identities never met for any
  connection string that names a database, which is all of them. Both are fixed and covered by
  tests. See
  [docs/powerai-model-entity-resolution-design.md](powerai-model-entity-resolution-design.md).

  **What that leaves**: one report, one source shape. `Sql.Database` is the only recognized shape,
  and the proof rests on a single file whose model reads a single SQL Server database.
- **Verified against one report.** PowerBI's embedded SQLite schema has shifted across versions
  before (column renames observed directly: `FromColumnID` vs `FromEndColumnID`). The C tool probes
  the schema at runtime and fails loudly naming what is missing, rather than assuming a shape, but
  that is a safety net, not proof every version is covered. The same caveat now applies to the visual
  layer from the other direction: the sample report was re-saved by a current Power BI Desktop, which
  writes the split `Report/definition` form, so it is the split reader that a real file exercises and
  the older `Report/Layout` reader that only synthetic fixtures do. Both are covered by the C tool's
  suite (73 checks, clean under ASan/UBSan).
- **The DAX is stored verbatim and never evaluated.** Measures (with their DAX), relationships,
  calculated columns, and table sources land in the catalog at sync and are served by the semantic layer
  (`describe_semantic_table`'s `reportModels`). A model no longer depends on WHERE the sync ran: see
  "Resolved: the model vanished on syncs that could not extract" below.
- **A visual can project a field the model spec never resolved to a node.** A visual names its
  field independently of the model reader (for example a hierarchy level whose field name is not
  literally one of the model's declared columns, or any field on a report whose model half is
  absent because it is connected live to a published dataset). The `projects` edge's target id
  still carries the table and field name (and whether it is a measure) even when no matching
  `column`/`measure` node exists to receive it, so the fact is not lost, but a consumer walking
  strictly from node to node rather than reading the edge's own id will not find a node at the
  other end. `PbixExtractTool.cs` accounts for this when reconstructing `LineageSubscriberField`
  (it reads the field straight off the edge's target id, not off a resolved node), but any other
  future graph consumer needs to do the same rather than assuming every edge target is materialized.

### Resolved: tool duplication

`SqlFlow.PowerBi` and `tools/pbix-extract` both read `Report/Layout` and both derived a visual's
pages/visuals/field roles, so the logic existed twice, in two languages, and a fix to either had to
be remembered twice or the two silently diverged. **Settled by deleting the C# reader.**
`tools/pbix-extract` is now the single reader of a `.pbix`: it gained the query expression tree and
a SQL renderer (`sqlrender.c`, ported from `VisualQueryTranslator` so the output matches character
for character), and `FlowSetCollector` runs it and consumes its YAML.

The binary is located via `SQLFLOW_PBIX_EXTRACT`, then next to the entry assembly, then `PATH`. It
is deliberately **not** declared in the estate's YAML, since the binary is a property of the machine
running the sync rather than of the repository.

This also fixed a refusal found while wiring it up: a report connected live to a published dataset
keeps its model on the server and so carries no `DataModel` part, which the tool had treated as
fatal even though such a file still has a complete visual layer. The model and report halves now
degrade independently, and only a file yielding neither is an error.

### Resolved: the model vanished on syncs that could not extract

Every sync deletes and rebuilds a repo's subscriber report and model rows. A sync that could not run the
extractor (the control plane never can, by design) or could not see the `.pbix` (usually git-ignored) rebuilt
them from nothing, so the managed sync erased, on its next poll, the model a developer's sync had just written.

**Settled by making the specification the unit that moves, and giving the semantic layer a store for it.** The
tool stays where it is; what changed is where its output lives:

- A subscriber's `pbix:` may name the committed specification (`Sales.pbix.yaml`, written by
  `sqlflow powerbi extract`) instead of the `.pbix`. Every machine reads it the same way, extractor or not.
- `CatalogSemanticReportSpec` (migration `AddSemanticReportSpecs`) holds specifications the repository does not:
  a person's UPLOADS, and the EXTRACTED copy a sync keeps of each declared `.pbix` it could read. A later sync
  that cannot extract a declared report serves the kept copy instead of dropping it. Neither origin is part of the
  rows a sync replaces wholesale; a kept copy is removed only once the repository stops declaring its report, and
  an upload only by a person.
- `CatalogRepo.SubscriberInputHash` fingerprints every subscriber library and specification a sync read, so a new
  upload, a committed specification, or an edited `subscribers.yaml` recomputes the graph on the next ordinary
  sync. Before this, a `subscribers.yaml` edit with no flow change was not applied until a forced sync.

The consumption edges derived from a report stay where they were (lineage, repo-scoped, rebuilt each pass): they
are simply rebuilt from a specification that is now always available. See
[docs/wiki/decisions/powerbi-report-specifications.md](docs/wiki/decisions/powerbi-report-specifications.md) for
the alternatives considered.

### The security posture: extraction does not run in the control plane

Extraction runs where the report files live (a developer machine, a build agent), never inside the
control-plane container, and this is a deliberate choice rather than an unfinished packaging step.

A `.pbix` is untrusted, attacker-influenceable input: reaching its contents means running a vendored
XPress9 decoder and a SQLite reader over bytes SQLFlow did not write. The risk is not theoretical,
since sanitizer runs already caught a real overlapping-`memcpy` bug in Microsoft's reference
decoder. Confining that parser to a laptop or an ephemeral build agent keeps a malicious report away
from the process holding catalog credentials and warehouse reach, and keeps libzip, expat and
sqlite3 out of the production runtime image.

The consequence is stated rather than hidden: a sync inside the container never runs the tool. It serves a
declared report from its committed specification or from the copy an earlier sync kept, and only when neither
exists does it warn that the report was not extracted, naming `SQLFLOW_PBIX_EXTRACT`.

Uploading a `.pbix` from the GUI is served by exactly the separate sandboxed job this section anticipated:
`SqlFlow.PbixExtractor` (`Dockerfile.pbix-extractor`), a small service in its own image that holds no catalog,
warehouse, or git credential, is reachable only on a private network (compose: an `internal` network shared with
the control plane alone, running read-only with every capability dropped), and requires a shared key. It writes
the upload to a private temporary directory, runs the same `pbix-extract` through the same `ReportSpecs.Extract`
path a developer sync uses, deletes the file, and answers with the specification. The control plane streams the
upload through without reading it and validates the answer (size, no YAML anchors or aliases, a well-formed
graph) before showing or storing it, so a compromised extractor can at worst hand back a specification that is
refused. `sqlflow powerbi extract` uses the local tool when a machine has one and this service when it does not,
which is how an analyst on Windows (where the tool is not built) produces a specification.

### What remains for a finished product

In roughly the order it would need to land, since each depends on groundwork the previous step laid:

1. ~~Resolve the tool duplication.~~ **Done** (above): `tools/pbix-extract` is the single reader,
   `SqlFlow.PowerBi` is deleted, and `FlowSetCollector` consumes the tool's YAML.
2. ~~An MCP surface over pages/visuals/fields.~~ **Done** (above): `describe_subscriber_report`. The
   model spec (measures, relationships, calculated columns, table sources) is now exposed through
   the semantic layer, on the warehouse table each model table loads from, as `describe_semantic_table`'s
   `reportModels`.
3. ~~The business-question field~~ **Done** (Section 8, Section 9 step 3): every extracted visual
   can now carry 1-3 LLM-generated questions (`CatalogSubscriberReportVisualQuestion`), generated by
   a control-plane-only post-sync step and regenerated only when the visual's content changed. This
   is what retrieval-by-similarity (step 5 below) matches against, alongside the confirmed examples
   a person accepted.
4. ~~**Model-entity-to-warehouse-object resolution**~~ **Done** (known limitations, above): the M
   source expressions are pattern-matched in `tools/pbix-extract` and fed into the existing synonym
   pass as `SynonymLink` facts, so a resolved model table's consumption edge lands on the same node
   an ingestion flow writes. Only `Sql.Database`-shaped sources resolve; everything else is reported
   unresolved rather than guessed. Proven end to end against the restored AdventureWorksDW2022
   database, which is also what found the two bugs that had kept the chain from ever working; what
   it does not prove is breadth, since it rests on one report and one source shape.
5. ~~**The confirmed-example store and retrieval** (Sections 6 and 8).~~ **Done.** Retrieval:
   the assistant calling `find_similar_questions` expands the typed question into related business
   vocabulary itself and passes it as `expanded_terms` (`QuestionExpander`, on the Anthropic account
   question generation already uses, is the server-side fallback for a caller that sends none),
   `QuestionSearch.FindSimilarAsync` ranks the stored questions by how
   many of those terms they match (through a full-text index where the instance has one, a `LIKE` scan
   where it does not), and `find_similar_questions` exposes it to every assistant surface, returning each
   match with the SQL that already answers it, the terms that matched, and a score-derived confidence
   rather than an LLM's self-rating. All of it is off by default behind `ControlPlane:PowerAI:Retrieval`,
   and it adds no vendor beyond the Anthropic key: an embedding-based version was built first and removed
   for that reason, which
   [docs/powerai-question-retrieval-design.md](powerai-question-retrieval-design.md) records along with
   what the switch traded away. The store: `CatalogSemanticExample` (Section 8) holds the flat
   question/query/objects/provenance/confidence/timestamp/user row, `POST /api/v1/powerai/questions/confirm`
   (`QuestionExampleEndpoints`, "operate" scope) writes it, and `confirm_question` exposes that to every
   assistant surface. The SAME search reads both halves and ranks them together, so a confirmed example
   competes with the dashboards' questions rather than sitting in a second list a caller would have to
   merge; on an equal score the confirmed one wins, as the precedent a person actually checked. A
   rejection is accepted and stores nothing, and a query that would write is refused by the same
   `ReadOnlyQueryGuard` the DataOps prepare step uses, so the store cannot come to hold a statement that
   would mutate the warehouse if a later answer were adapted from it.
6. ~~**The learning loop**~~ (Section 6): **Done.** What was missing was the CONFIRMATION MOMENT, not the
   machinery behind it: a person's decision was recorded only when the assistant chose to call
   `confirm_question` after being told the answer was right, which made the loop real but opportunistic.
   The GUI now shows its own Yes / Not quite / No row under every finished answer that hands back a query
   (`AnswerConfirmation`, hung off a new `AnswerFooter` slot on the thread so the answer renderer did not
   have to be forked to add a row beneath it). Nothing new is stored and there is no second write path: it
   posts to `POST /api/v1/powerai/questions/confirm`, the endpoint already behind `confirm_question`, so a
   click and a tool call land the same row.

   **What the query being judged is.** The `sql`-tagged fenced blocks of the answer itself, which is exactly
   what the person read before deciding, and which the assistant is already instructed to write every query
   in (`AssistantInstructions`). An untagged fence is deliberately ignored: it is as likely to be YAML or a
   table of results, and storing one of those as a confirmed example would poison the store with something
   no future question should ever be answered with. An answer carrying several queries gets a numbered
   picker; "Not quite" opens the chosen one in a SQL editor and stores the CORRECTED text, never the
   original proposal.

   **What it deliberately does not send.** No `objectKeys`, because no lineage pass produced them on this
   path and inventing identities would put unverified keys in the catalog, and no `confidence`, because the
   endpoint defines that as the retrieval score a proposal was built from, never anyone's impression that a
   query looks right. The datasource is optional and is picked from `GET /api/v1/datasources`, so a stored
   example can only ever name a connection the reviewed git estate already declares.

   **It appears only where it would work.** `GET /api/v1/chat/capabilities` gained
   `questionConfirmation`, read from the same `ControlPlane:PowerAI:Retrieval:Enabled` option the confirm
   endpoint answers 501 on, so a deployment without the example store shows no button rather than one whose
   only possible answer is an error.
7. **Broader version coverage**: extraction is proven against one report from one PowerBI version.
   Widening this is a matter of running the tool against more real files as they turn up and fixing
   what the schema probes catch, not a design change.
8. ~~**In-container extraction, if ever needed.**~~ **Done** (above): report specifications are stored in the
   semantic layer and read by every sync, and a `.pbix` uploaded through the GUI or `sqlflow powerbi` is read by
   the isolated `SqlFlow.PbixExtractor` service, never by the control plane. What remains of it is operational:
   the extractor is added to `deploy/compose`, but the Azure estate still needs its container app created (a
   `pbix-extractor.bicep` module is provided) before `ControlPlane:PowerAI:ReportExtraction` can be enabled there.

9. ~~**Questions about a database no flow loads.**~~ **Done.** A query could only run on a connection an active
   pipeline declares, so a dashboard over a database SQLFlow does not load could be searched but never run. A
   schema registration flow (`flowType: sch`, [docs/reference/flow/sch.md](docs/reference/flow/sch.md)) now
   registers that database's tables and views in the catalog; its connection is a declared datasource, a
   subscriber library with no connections links onto the registered tables by database, schema, and name, and
   datasource inference follows the `Registers` edges to the connection and database. The dashboard, not the
   registration, supplies joins and measures. See
   [docs/wiki/decisions/schema-registration-flows.md](docs/wiki/decisions/schema-registration-flows.md).
10. ~~**Dashboard matches whose SQL cannot run.**~~ **Done.** A visual's rendered SQL names the report's model,
   not the database, so a dashboard match could be found but not run. Each sync now translates every visual's
   query into T-SQL over the source tables its model loads from (`VisualSqlTranslator`: Power Query column
   lineage including merged queries, joins along the model's active relationships, and a stated DAX subset),
   stores it as `SourceSql` beside the report's query, and serves it as the match's runnable `sql`. A visual
   outside the subset carries a `translationProblem` and stays a lead. See
   [docs/reference/flow/subscribers.md](docs/reference/flow/subscribers.md#source-sql-per-visual) and
   [docs/wiki/decisions/visual-sql-translation.md](docs/wiki/decisions/visual-sql-translation.md).

Each item is scoped so it can be picked up, implemented, and landed independently; nothing here
should be treated as a single large batch of work.
