# PowerAI Model-Entity-to-Warehouse-Object Resolution: Design

Status: steps 1 and 3 are implemented (the M pattern-matcher in the C tool, and the synonym emission
that feeds its output into the existing resolution pass). Step 2 (`modelSourceServer`) was NOT built,
deliberately: see Section 5's note. Step 4's integration test is limited by fixture reality, as
Section 8 explains. Step 5 is done: the sample report was repointed at the restored AdventureWorksDW2022
database and a `db sync` resolves all seven of its SQL-backed tables end to end
([the handoff](powerai-adventureworks-sql-resolution-handoff.md)). What remains unproven is breadth:
one report and one source shape.

This closes the known limitation described in POWERAI.md
Sections 5, 8, and 10 ("Model-entity resolution"): an extracted PowerBI visual's synthesized SQL, and
the query/lineage facts built from it, name the report's MODEL entity (e.g. `Sales`), not the physical
warehouse object it actually reads (e.g. server `dwh`, database `OdsDb`, schema `arc`, table `Sales`).
See [POWERAI.md](../POWERAI.md) for the surrounding roadmap and
[docs/reference/flow/subscribers.md](reference/flow/subscribers.md) for the systems this builds on.

## 1. The problem, precisely

A PowerBI report's semantic model wraps a physical source behind a Power Query (M) expression per
table (`tableSources[].powerQuery` in the extracted spec). The report's visuals and DAX only ever
name the MODEL's table (`Sales`), never the M expression that produced it. Consumption lineage is
built by parsing each visual's synthesized SQL (`FROM [Sales] AS [s]`, verbatim from
`tools/pbix-extract/src/sqlrender.c:345-359`) through `TSqlLineageExtractor`
(`src/SqlFlow.Lineage/Extraction/TSqlLineageExtractor.cs`), which resolves `Sales` as a bare 1-part
name with no default database (`FlowSetCollector.cs:208` passes `defaultDatabase: null` for every
subscriber query). The resulting node lands on `NodeKey.For(serverRef, database: null, schema: null,
"sales")`: a name-only node that never unifies with the fully-qualified node an ingestion flow
writes, `NodeKey.For("dwh", "odsdb", "arc", "sales")` (`src/SqlFlow.Lineage/Collection/LineageFacts.cs:364-372`).

Confirmed from the actual sample (`samples/powerbi/AdventureWorks_Sales.spec.yaml:198-208`): the one
real M expression on file is
```
let
    Source = Excel.Workbook(File.Contents("<redacted>"), null, true),
    Sales_Table = Source{[Item="Sales",Kind="Table"]}[Data],
    #"Changed Type" = Table.TransformColumnTypes(Sales_Table,{...})
in
    #"Changed Type"
```
an Excel-backed table, not a SQL-backed one. This matters: **not every model table is resolvable to a
warehouse object at all**, and the design must treat "cannot resolve" as a normal, reported outcome,
not an error, exactly like `subscribers.yaml`'s existing "Incomplete dataset" convention
(`docs/reference/flow/subscribers.md:141`) already does for a report reading objects lineage could not
otherwise identify.

## 2. What "done" looks like

Given a table's M expression, produce zero or one resolved `(serverRef, database, schema, name)`
tuple naming the physical warehouse object it was loaded from, for every table whose M expression is
a recognizable direct-database-source shape. Feed that mapping into the SAME identity-resolution pass
the estate already runs for synonyms (Section 4), so a PowerBI-sourced consumption edge lands on the
identical node an ingestion flow's write edge lands on, with zero changes to the graph builder's core
algorithm. A table whose M expression is not a recognized shape (Excel, web, a merged/appended query,
an unsupported connector) is left unresolved and reported, not guessed at.

## 3. M-expression pattern matching, not a general M parser

**Recommendation: pattern-match known top-level M function calls, do not build a general M-language
parser.** No M lexer/parser/AST exists anywhere in this codebase today (confirmed by research: the
current `redact.h` bar is "recognize a `File.Contents(\"...\")`-shaped literal and blank it," not
structural parsing). M is a full functional language; a general parser is a large, ongoing maintenance
surface for a feature whose payoff is a name-to-name mapping, not language execution. The realistic M
shapes a warehouse-fed report actually uses are a short, closed list:

- `Sql.Database("server", "database")` (or `Sql.Databases("server")` then a database-name index),
  followed later in the `let` chain by a step that indexes `[Schema="dbo",Item="Sales"]` or
  `{[Schema="dbo",Item="Sales"]}[Data]` off the result (directly or via an intermediate step name).
- `Sql.Database("server", "database", [Query="select ..."])`: a native-query source; the physical
  object(s) are inside the query text, not a schema/item pair, so this shape needs its `Query` value
  run back through `TSqlLineageExtractor` itself (already exists) rather than a schema/item pattern
  match, effectively "this M table's source is itself parseable T-SQL."
- `Odbc.DataSource("dsn-or-connection-string", ...)` with a similar downstream `[Schema=...,Item=...]`
  navigation: the same match shape as `Sql.Database`, differing only in how the server identity is
  spelled (a DSN name or connection string rather than a bare server name), which needs its own
  server-identity handling (Section 5) since it will not already appear as a declared `connections:`
  entry the way a `server:`-referenced subscriber does.
- Everything else (`Excel.Workbook`, `Csv.Document`, `Web.Contents`, `SharePoint.Files`,
  `Table.Combine`/merged queries spanning more than one source, `AnalysisServices.Database` against
  another model) is explicitly UNRESOLVED. Recognize these shapes only well enough to name them in a
  warning ("table 'X' is Excel-sourced, not resolvable to a warehouse object"), never to guess a
  target.

This is regex/token-scanning work (find the function-call head, find the trailing `[Schema=...,
Item=...]` or `{[...]}[Data]` step, extract the quoted literals), not a parser for arbitrary
`let...in` nesting. It should tolerate irrelevant intermediate steps (a `#"Changed Type"` transform
between the source step and the visual-facing name, as the sample already shows) by matching the
*shape* of the relevant step wherever it appears in the `let` chain, not assuming a fixed position.

## 4. Where the matching runs: the C tool, in Rust/C, not the control plane

**Recommendation: extend `tools/pbix-extract` itself** to attempt this pattern match at extraction
time, emitting the result as new properties on the `table` node (`resolvedServer`/`resolvedDatabase`/
`resolvedSchema`/`resolvedName` when matched, absent when not), rather than doing the pattern-matching
in C#/`SqlFlow.Lineage` against the raw `powerQuery` text.

Why here and not downstream: the tool already parses the M text into memory to extract `TableSource`
(`tools/pbix-extract/src/metadata.h:50-53`), and already owns the "this is untrusted input, never let
it reach the control plane's process" security boundary (POWERAI.md's own stated posture). Adding a
second reader of the same M text in C# would recreate exactly the tool-duplication problem POWERAI.md
Section 10 already recorded once and had to resolve by deleting a redundant reader. One parser, one
place, feeding structured facts downstream: the Single Code Path Principle applied to this feature
specifically.

This DOES mean the pattern-matcher is written in C, joining the existing hand-rolled JSON/XML/SQLite
handling already in that tool. That is consistent with the tool's existing shape and its "no .NET/
Python dependency" design constraint; it is not free (new C parsing code is real surface area to test
under the tool's existing `make test`/`make test-asan` suite), but it keeps the untrusted-input
boundary exactly where it already is.

## 5. Server identity: the part that needs a human, not just a parser

A resolved `Sql.Database("myserver.database.windows.net", "OdsDb")` names a server by its M-literal
connection string, which is NOT automatically the same identity string a subscriber's `server:` YAML
key or a flow's `connection:` reference uses (`ServerIdentity.From`,
`src/SqlFlow.Lineage/Collection/LineageFacts.cs:380-404`, hashes an inline literal or passes through a
`${env:...}`/`@alias` reference verbatim). Two servers named differently in M text versus in
`connections:` must not silently become two different nodes for the same physical server.

**What was actually built differs from the recommendation below, and is simpler.** The synonym's
target keeps the SUBSCRIBER's own resolved server identity, taking only database/schema/name from the
M expression. The M-literal server is emitted on the table node for a human to read but is never used
as an estate identity, so the two are never compared and cannot disagree. That removes the need for
`modelSourceServer` entirely (Section 8, step 2). The reasoning below is kept because it explains why
the M literal must NOT become a server identity, which is exactly the constraint the implementation
honors by a different route.

Original recommendation: **do not try to auto-derive `serverRef` from the M literal.** Instead, extraction
emits the *M-literal* server/database/schema/table verbatim as `resolvedServer`/etc. (a "candidate"
identity, not yet an estate identity), and a human maps it once per subscriber via a small, explicit
declaration on the subscriber, e.g.:

```yaml
subscribers:
  Analyse_Bysykkel:
    type: PowerBI
    server: dwh          # the estate's connection reference for THIS subscriber's queries
    pbix: reports/analyse_bysykkel.pbix
    modelSourceServer: myserver.database.windows.net   # OPTIONAL: only needed if the M-literal
                                                         # server string differs from `server:`'s
                                                         # resolved identity, so the two can be
                                                         # correctly treated as the same server.
```

When `modelSourceServer` is absent (the common case: the M expression's server literal already
matches, or is close enough to, what `server:` resolves to), the resolved candidate is used as-is.
When it is present, the candidate's server portion is rewritten to `server:`'s resolved identity
before the mapping is applied. This keeps the estate's existing "connections are declared, not
inferred" posture (per `docs/reference/flow/subscribers.md`'s `connections:` block) rather than
having the tool guess at server equivalence.

## 6. Feeding the mapping into the existing identity-resolution pass

**This is the one part of the design with a strong, already-proven precedent, and it needs no new
graph-builder mechanism.** `LineageGraphBuilder.Build` (`src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs:38-53`)
already resolves exactly this shape of fact, a `SynonymLink` (`LineageFacts.cs:258-269`,
`(ServerRef, Database, Schema, Name) -> (TargetDatabase?, TargetSchema?, TargetName?)`), through
`ResolveSynonyms`, applied to every fact before it lands in the graph. A model-entity resolution is
structurally identical: "this name means that other name," just sourced from a `.pbix`'s M expression
rather than from a live `sys.synonyms` read.

Plan: `FlowSetCollector`'s PowerBI extraction path (`ExtractOneReport`,
`src/SqlFlow.Lineage/Collection/FlowSetCollector.cs:369-`) reads the new `resolvedServer`/
`resolvedDatabase`/`resolvedSchema`/`resolvedName` table-node properties (once `PbixExtractTool.cs`'s
`SpecNode` gains them, mirroring how it already reads `visualType`/`title`/`sql`) and, for every
resolved table, emits one `SynonymLink` into `CollectionResult.Synonyms` with:
- `ServerRef`/`Database`/`Schema`/`Name` = the subscriber's own `server:` identity + the MODEL entity
  name (the "from" side: what the bare `Sales` reference in the synthesized SQL resolves to today).
- `TargetDatabase`/`TargetSchema`/`TargetName` = the resolved physical object (the "to" side).

No change to `LineageGraphBuilder` itself: it already merges every source's synonyms into one
`synonymTargets` dictionary and applies them uniformly. A PowerBI-sourced synonym and a live
`sys.synonyms`-sourced synonym are indistinguishable to the resolution pass, which is exactly the
point: one mechanism, two producers.

**Consequence for `defaultDatabase`:** `TSqlLineageExtractor.Extract` is still called with
`defaultDatabase: null` for subscriber queries (`FlowSetCollector.cs:208`, unchanged); the model
entity's bare name still parses as a 1-part name at that stage. The synonym pass, which runs AFTER
extraction (`LineageGraphBuilder.Build` line 38 onward, before the graph is finalized), is what
rewrites the 1-part name onto the resolved 4-tuple. This means no change to the SQL rendering
(`sqlrender.c`) or to `TSqlLineageExtractor` is needed at all; the fix is entirely additive
(new extracted data + new synonym facts), which is the cleanest possible integration into an existing,
tested pipeline.

## 7. Catalog storage and reporting

- `CatalogSubscriberReportField.TableName` (today: the bare model entity, per its own doc comment)
  stays as-is; it is describing what the VISUAL projects, which is correctly the model's own
  vocabulary; resolution is a lineage-identity concern, not a display concern.
- The resolved mapping itself does not need new dedicated catalog storage: it becomes ordinary
  `CatalogLineageEdge` rows once folded through the synonym pass, exactly like a live-synonym-derived
  edge does today. This matches POWERAI.md Section 7's storage precedent (no parallel structure for
  something the existing graph already models).
- A table that could NOT be resolved should surface exactly like an "Incomplete dataset" subscriber
  note today: added to `reportWarnings` (already a flat, non-graph-shaped list per
  `docs/reference/flow/subscribers.md:207-209`) naming the table and the M source shape that defeated
  resolution ("table 'Sales' sourced via Excel.Workbook, not resolvable to a warehouse object"), so a
  person reads the estate's own warnings rather than lineage silently looking complete when it is not.

## 8. Sequencing (each step landable and testable independently, no time estimates)

1. ~~Extend `tools/pbix-extract`'s `TableSource` handling with the M pattern-matcher.~~ **Done**:
   `tools/pbix-extract/src/msource.{c,h}`, emitting `sourceServer`/`sourceDatabase`/`sourceSchema`/
   `sourceName` on the table node when matched and a `reportWarnings` line naming the shape when not.
   Scope is `Sql.Database` only, per Section 9's own recommendation; `Odbc.DataSource` is recognized
   well enough to NAME in a warning but never resolved. The native-query (`[Query="..."]`) variant is
   likewise recognized and left unresolved rather than half-resolved: re-parsing its SQL was dropped
   from this pass because it would mean a second T-SQL parser inside the C tool, which is a far larger
   commitment than the name-to-name mapping this feature needs. 27 new checks in the tool's own suite,
   clean under `make test` and `make test-asan`.
2. ~~Add `modelSourceServer` as an optional subscriber YAML key.~~ **NOT built, deliberately.** The
   implementation made it unnecessary: the synonym's target keeps the SUBSCRIBER's own server identity
   and takes only database/schema/name from the M expression (Section 6), so the M-literal server
   string is never used as an estate identity and never needs remapping onto one. Adding a YAML key to
   reconcile two identities that are no longer compared would be configuration for a problem that does
   not arise. The M server string is still emitted on the node for a reader, just not used for
   identity.
3. ~~`PbixExtractTool.cs`'s `SpecNode` gains the new table-node properties; `FlowSetCollector` emits
   one `SynonymLink` per resolved table.~~ **Done**, exactly as designed and with no change to
   `LineageGraphBuilder`, `TSqlLineageExtractor`, or `sqlrender.c`.
4. Integration test asserting a resolved edge unifies with an ingestion's node: **not built, and here
   is why.** `LineagePowerBiSubscriberTests` builds its `.pbix` fixture as a zip carrying only a
   `Report/Layout` part. A model source lives in the `DataModel` part, which is an XPress9-compressed
   Analysis Services backup image wrapping a SQLite database; synthesizing one in a test is not
   reasonable, and the tool is decode-only by design (it vendors a decompressor, not a compressor).
   The resolution logic is therefore tested where it can be tested honestly, in the C tool's own suite
   against the M text directly, and the existing C# test now states that its fixture exercises the
   unresolved path only. Closing this properly needs step 5, not more test scaffolding.
5. ~~A SQL-backed sample report.~~ **Done** on 2026-09-13: the sample's model was repointed at the
   restored AdventureWorksDW2022 database, and `sqlflow db sync . --connect` lands its read edges on the
   harvested warehouse tables, which is what finally demonstrates the seam described at the end of this
   item. Doing so found two bugs on either side of that seam; see
   [the handoff](powerai-adventureworks-sql-resolution-handoff.md) and POWERAI.md Section 10. As
   originally written, the item read: no SQL-backed sample report exists. The current
   `AdventureWorks_Sales.pbix` is Excel-backed (verified: all 8 of its tables now emit an
   "Excel.Workbook / Json.Document names no warehouse object" warning), so it exercises only the
   refusal path. Proving the resolved path end to end, from `.pbix` through to a unified
   `CatalogLineageEdge`, needs a real report whose model reads a SQL database. Until then the C
   tests prove the matcher is correct and the synonym mechanism is proven by its existing use, but
   the seam between them is argued rather than demonstrated.

## 9. Open questions to settle before implementation starts

- **How much of the M grammar's whitespace/formatting variation must the pattern-matcher tolerate?**
  PowerBI Desktop's own M formatter is fairly consistent, but a hand-edited or older-version file could
  vary. Recommendation: match liberally (ignore whitespace/newlines, allow the schema/item navigation
  step to appear anywhere after the source step) and fail closed (report unresolved) rather than
  guess, consistent with the rest of this design's "unresolved is honest, guessed is not" stance.
- **Should `Odbc.DataSource` really be in scope for a first pass**, given it needs its own server-
  identity handling (Section 5) distinct from `Sql.Database`, or should the first pass be
  `Sql.Database` (including its native-query form) only, with `Odbc.DataSource` as an explicit
  follow-up once real reports using it are seen? Recommendation: `Sql.Database` only for the first
  pass; it is very likely the dominant shape for a warehouse-fed report, and narrowing scope keeps
  step 1 reviewable.
- **Does a resolved mapping ever need to be REVOKED** (a report re-points its model source, the old
  mapping should stop applying)? Since extraction re-runs and re-emits the full spec on every sync
  (nothing here is incremental the way question generation's content-hash gate is), a stale mapping
  cannot persist past the next sync, but this should be confirmed against `CatalogSync`'s existing
  wholesale-replace behavior for lineage edges before assuming it "just works."
