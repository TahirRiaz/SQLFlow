# PowerAI Question Retrieval: Design

Status: complete (Section 8, all six steps). Retrieval is built and reachable, and so is the
confirmed-example store it searches alongside the PowerBI-derived questions. The GUI now asks a person to
confirm every answer that hands back a query (`gui/src/features/chat/AnswerConfirmation.tsx`), so the
learning loop no longer depends on the assistant remembering to. The confirmed-example table was later
renamed and moved into the semantic layer: it is now `CatalogSemanticExample` (migration
`MoveQuestionExamplesIntoSemanticLayer`), and the sections below keep its original name,
`CatalogQuestionExample`, as the record of what was designed. What is still open is tuning, not building
(Section 9).

**This document's mechanism changed after it was first written, and the sections below record both.**
It originally specified embedding similarity, and that was implemented; it was then replaced with
LLM query expansion over a word search, and the embedding code was deleted rather than left beside it.
The reason was not technical merit: embeddings require a paid third-party vendor (Anthropic serves no
embeddings endpoint, so it meant OpenAI or Azure on top of the Anthropic account already configured),
which is a poor fit for an open-source project whose contributors would each need their own key.
Section 3 states what was traded away by that choice, since it is a real cost and not a free win.

This is POWERAI.md Section 6 ("the learning loop") and Section
9 step 6 ("build the confirmed-example store and the retrieval step"), scoped out in detail now that
step 3 (the business-question field, `CatalogSubscriberReportVisualQuestion`) has landed and gives
this something real to retrieve against. See
[POWERAI.md](../POWERAI.md) for the surrounding roadmap and
[docs/reference/flow/subscribers.md](reference/flow/subscribers.md) for the systems this builds on.

## 1. The gap this closes

Today, a user's typed question can only be handed to an LLM as an unranked pile of stored
`CatalogSubscriberReportVisualQuestion` rows (11 in the one sample report extracted so far) for it to
eyeball itself. There is no measure of which stored question is actually closest to a new one, and no
signal for how much to trust an answer built from a near-miss versus an exact match. This design adds
that: a retrieval step that ranks stored (question, SQL, objects) examples by similarity to a new
question, and a confidence signal derived from that similarity rather than from the LLM's own
self-assessment (POWERAI.md is explicit that LLM self-rated confidence is not trustworthy: a wrong
query can sound exactly as confident as a right one).

## 2. What gets searched

Two provenances, one shape, per the flat-table decision already made in POWERAI.md Section 7:

- **`powerbi`**: `CatalogSubscriberReportVisualQuestion` rows, already landing today via the sync-time
  enrichment step (`SubscriberQuestionEnrichment`). Each question is backed by a real rendered SQL
  query (via the visual's `QueryName` → `CatalogSubscriberQuery.Sql`) and the object keys that query
  reads (`CatalogSubscriberQuery.ObjectKeys`).
- **`user-confirmed`**: rows written when a person confirms (accepts or corrects) an answer from
  the learning loop itself (Section 6 of POWERAI.md). This is the confirmed-example store, and it is
  scoped alongside retrieval here because the two belong together: retrieval with nothing to learn from
  is just the PowerBI-only case, so the storage and the search step share one query shape rather than
  the search having to grow a second path once confirmation landed.

The `user-confirmed` half gets its own table, `CatalogQuestionExample`, holding those rows alongside
the PowerBI-derived ones the existing table already carries; a search spans both. Under the word-search
mechanism this is simpler than it was under the embedding one, since neither table needs a derived
artifact kept in step with its text: the searched column IS the stored question.

## 3. Matching mechanism: LLM query expansion over a word search

**Built: an LLM expands the typed question into related business vocabulary, and the stored questions
are ranked by how many of those terms they match.** SQL Server's full-text index supplies the
linguistic half (inflections such as "sell"/"sells"/"selling"), and the expansion supplies the
business-vocabulary half a database cannot know ("turnover" also meaning "revenue", "clients" also
meaning "customers"). No embedding vendor is involved: the expansion reuses the same Anthropic
account/model `QuestionGenerator` already uses, and can be switched off to search the typed words
alone with no LLM call at all.

**What this trades away, stated plainly.** Embedding similarity captures paraphrase even when two
questions share no vocabulary a synonym list would connect, and it yields a bounded 0-1 score that
means the same thing across every estate. Term-count scoring does neither: its score is unbounded and
only comparable within one search, and a paraphrase the expansion fails to anticipate is simply
missed. The mitigation is that the expansion is doing the semantic work an embedding would otherwise
do, one LLM call per search rather than one embedding call per search plus one per stored question.
Whether that is good enough is an empirical question this design does not settle, and the reason
`RankThreshold` exists is so an estate can tune where "trusted" starts once it has real questions.

**Why not embeddings (revisited).** Not on quality: on dependency. Anthropic serves no embeddings
endpoint, so embeddings meant adding OpenAI or Azure as a second paid vendor beside the Anthropic key
this feature already needs. For a solo-maintained open-source project that is a barrier to every
contributor, not just to the deployment. A local embedding model (Ollama and similar) would remove the
vendor but adds a runtime dependency and a model download to the same contributors. The word search
needs neither, and degrades to something that still works when the LLM is unreachable.

**Why not naive `LIKE` alone.** It is the fallback, not the mechanism: it runs only where SQL Server's
full-text feature is absent (Section 4). Both paths credit inflections, the index through
`FORMSOF(INFLECTIONAL, ...)` and the fallback through a small suffix comparison in the scoring step, so
"sell" reaches a question worded "sells" either way.

**What no stemmer bridges, verified directly against SQL Server rather than assumed.** "sales" does
NOT reach "sells", under plain `CONTAINS`, under `FORMSOF(INFLECTIONAL, ...)`, or under `FREETEXT`.
They are different lemmas (a noun and a verb), not two forms of one word. An earlier draft of this
document claimed the engine covered that case; a test proved otherwise, and the prompt in
`QuestionExpander` now asks explicitly for grammatical forms including irregulars ("sold", "bought")
precisely because this is the class of gap only the expansion can close. A test asserts both halves:
the inflections that do match, and the different lemma that does not until expansion supplies it.

**A plain `CONTAINS` would have bought nothing.** Worth recording, since it was a real bug caught only
by running against an instance that actually had the feature: `EF.Functions.Contains(column, term)`
emits a literal `CONTAINS`, which matches the exact word and nothing else. The index was present and
being queried, but no inflection was being matched, so the full-text path was doing strictly less than
the `LIKE` fallback while appearing to work. The condition has to say `FORMSOF(INFLECTIONAL, "term")`
(still passed as a parameter) for the index to earn its place.

## 4. Storage: no new columns, one full-text index

**Nothing is added to the row.** The question text the search matches against is already stored, so
unlike the embedding design (which needed `Embedding`/`EmbeddingModel`/`EmbeddedAtUtc` on every row,
plus re-embedding whenever either the text or the model changed) this needs no new column and no
sync-time write of its own. That removes a whole class of staleness: there is no stored artifact that
can fall out of date with the text it was derived from.

What it does need is a full-text index, added by the `AddQuestionFullTextSearch` migration on
`SubscriberReportVisualQuestion(Question)`. It follows the estate's existing precedent exactly
(`ObjectFullTextSearch`, which indexes `Object.Definition` and `RunStatement.Sql`): it reuses that
migration's `CatalogFullText` catalog, guards every statement on `SERVERPROPERTY('IsFullTextInstalled')`,
and runs with `suppressTransaction` because full-text DDL cannot execute inside a user transaction.

**Full-text is an installable SQL Server feature the catalog cannot assume.** Where it is absent the
migration creates nothing (rather than failing the whole catalog upgrade) and the search falls back to
a `LIKE` scan, so a deployment on an instance without it still migrates and still answers, just with
weaker matching. `QuestionSearch` probes for the index once per process and caches the answer, since
it changes only when the catalog is migrated or the feature is installed, both of which restart the
control plane.

## 5. The search step itself

```
Task<QuestionSearchResult> FindSimilarAsync(
    CatalogDbContext db, string question, int topK, QuestionExpander? expander, Guid? repoId, CancellationToken ct)
```

1. Expand the question into terms (one Anthropic call), or fall back to its own words when expansion
   is off or the model is unreachable. Either way the terms are normalized: lowercased, stop words and
   one-character tokens dropped, de-duplicated. Stop words are removed here rather than left to the
   database because they would otherwise count toward a question's score and let "what is the" outrank
   a real vocabulary match.
2. Load the stored questions containing any of those terms, through the full-text index when the
   instance has one and a `LIKE` scan when it does not, bounded by a candidate ceiling.
3. Score each by how many distinct word stems its matching terms reduce to, on word boundaries so
   "sale" does not score against "wholesale", and return the top `topK`. Stems rather than terms,
   because an expansion lists a concept in several forms ("customer", "customers") that all hit the
   same stored word; counted separately, one shared word cleared the trust threshold alone.
4. Resolve only the winners' SQL and object keys (via the visual's `QueryName` →
   `CatalogSubscriberQuery`), so a large corpus costs one expansion call and one scan rather than a
   join across every stored question.

Each `QuestionMatch` carries the matched question, its SQL, its object keys, its provenance, its
score, and **which terms actually hit**, so a caller can say why a question was considered relevant
rather than only how strongly. The result also carries the terms that were searched for, which is what
makes an empty result interpretable ("nothing matched these words") instead of mysterious.

**The terms never reach SQL as text.** They are passed through `EF.Functions.Contains` (or `LIKE`),
which parameterizes them. A model-authored fragment pasted into `CONTAINS` syntax would be both a
correctness hazard (an apostrophe in "customer's" breaks the predicate) and injection-shaped; a test
covers exactly that input. This is why `QuestionExpander` returns a term LIST rather than a ready-made
query string, even though the model could easily produce one.

## 6. Where this runs

- **Expansion and search both happen on demand**, when a question is asked. There is no sync-time work
  at all, which is the main operational simplification over the embedding design: nothing to backfill,
  nothing to re-embed, no second vendor credential held by the sync.
- **The caller expands, when the caller is a model.** `find_similar_questions` takes `expanded_terms`
  and sends them as repeated `expandedTerms` keys; the assistant already reading the question produces
  the vocabulary, so the control plane makes no expansion call of its own and needs no Anthropic key for
  retrieval. `QuestionExpander` remains the fallback for a caller that sends no terms (a script, a
  future search box) where `ExpandSynonyms` is on; with it off, such a caller searches its typed words.
  Either way the terms pass through the same normalization and the same `QuestionExpander.MaxTerms`
  bound inside `QuestionSearch.FindSimilarAsync`, so a caller's expansion cannot widen the search beyond
  what the server's own would.
- **Reachable as `find_similar_questions`** over `GET /api/v1/lineage/subscribers/similar-questions`,
  on the shared MCP read surface so both the GUI chat and Slack get it (it reaches no datasource,
  returning only SQL text the catalog already stores and `describe_subscriber` already exposes).
- **Never in `tools/pbix-extract` or the bare CLI**, unchanged from the question-generation precedent:
  the parser stays a parser, and `sqlflow db sync` needs no model credential.

## 7. Schema summary

No entity changes. One migration, `AddQuestionFullTextSearch`, adding a full-text index on
`SubscriberReportVisualQuestion(Question)` and reusing the existing `CatalogFullText` catalog; it
creates nothing on an instance without the full-text feature. The `CatalogQuestionExample` table for
the `user-confirmed` half (Section 2) was unbuilt when this was written and was unchanged by this
mechanism switch: a word search over it works the same way. It has since landed (Section 8, step 6) and
now lives in the semantic layer as `CatalogSemanticExample`.

## 8. Sequencing (each step landable and testable independently, no time estimates)

Steps 1-3 below describe the ORIGINAL embedding sequence, which was built and then removed when the
mechanism changed (see the status note at the top). They are kept as a record of what was tried and
what replaced it, not as outstanding work.

1. ~~`IEmbeddingProvider` abstraction + one implementation.~~ **Built, then removed.** `IEmbeddingProvider`
   and `EmbeddingGateway` served both OpenAI and Azure behind one interface. Deleted with the mechanism
   switch; its replacement is `QuestionExpander` (`src/SqlFlow.Assistant/QuestionExpander.cs`), which
   reuses the Anthropic account already configured for `QuestionGenerator` instead of a second vendor.
2. ~~Migration adding `Embedding`/`EmbeddingModel`/`EmbeddedAtUtc`.~~ **Built, then reverted.** Migration
   `AddQuestionEmbeddings` was applied only to a throwaway test database, reverted there, and removed
   before any deployment saw it. Its replacement is `AddQuestionFullTextSearch`, which adds no columns.
3. ~~Embed questions at sync time.~~ **Built, then removed.** `EmbedQuestionsAsync` embedded new and
   model-stale rows during a sync. The word search needs no sync-time work at all, so this step has no
   replacement: the text it searches is already stored.
4. ~~The search step + an integration test asserting the ranking for an obvious paraphrase.~~ **Done**:
   `QuestionSearch.FindSimilarAsync` (`src/SqlFlow.ControlPlane/Background/QuestionSearch.cs`), returning
   `QuestionSearchResult` (the terms searched, plus `QuestionMatch` rows carrying the question, the SQL
   that answers it, its object keys, provenance, score, matched terms, subscriber, and visual title). It
   ranks first and resolves second, so only the winning handful cost a lookup of their SQL and objects.
   Terms reach the database as parameters, never as predicate text. Tested (`QuestionSearchTests`) against
   a real SQL Server: the differently-worded match ("turnover per territory" finding "revenue by region"),
   stop-word-only input matching nothing rather than everything, word-boundary matching ("sale" not
   matching "wholesale"), apostrophes and full-text operators treated as text, the no-expander fallback,
   and the inflection behavior of both paths pinned explicitly (what stems, and the different-lemma case
   that does not until the expansion supplies it).
5. ~~MCP tool surface so an assistant surface can call it.~~ **Done**: `find_similar_questions`
   (`tools/sqlflow-mcp/src/server.rs`) over `GET /api/v1/lineage/subscribers/similar-questions`
   (`FindSimilarQuestionsAsync`, `src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs`), on the SHARED read
   surface so both the GUI and Slack get it: it reaches no datasource, returning only SQL text the catalog
   already stores and that `describe_subscriber` already exposes on both surfaces. Each match carries
   `score`, the `matchedTerms` that produced it, and a `trusted` flag saying whether it cleared the
   deployment's threshold; the response repeats the threshold and the full `searchedTerms`, so a caller
   reports confidence from retrieval rather than inventing one. A deployment without retrieval configured
   answers **501, not an empty match list**: "nothing matched your question" and "this deployment never
   looked" demand opposite follow-ups, and conflating them would have an assistant claim an estate has no
   matching dashboard when it never searched. The tool description states that the score is the only
   trustworthy confidence signal, that an untrusted match is a lead rather than an answer, and that
   execution still goes through `prepare_query`/`run_query`'s human gate unchanged.
6. ~~Migration + `CatalogQuestionExample` table, and the actual confirm/correct/reject flow
   (POWERAI.md Section 6) that writes into it.~~ **Done, including the GUI affordance.** The table landed as
   `CatalogQuestionExample` (migrations `AddQuestionExamples` and `AddQuestionExampleFullTextSearch`),
   holding the question, the SQL, the object keys, the provenance, the retrieval score the answer was built
   from, the timestamp and the confirming user. `POST /api/v1/powerai/questions/confirm`
   (`QuestionExampleEndpoints`, under the "operate" scope because a caller who can add examples steers every
   later answer) records an accept, a correct, or a reject, and `confirm_question` exposes it to every
   assistant surface. Three decisions are worth stating because each closed a way the store could have gone
   wrong:
   - **A rejection stores nothing** and is answered before the SQL is even parsed. POWERAI.md Section 6 is
     explicit that nothing is learned as fact on rejection, and refusing the caller's report because the
     wrong query was also malformed would throw away the one signal the exchange carried.
   - **The SQL is parsed by the same `ReadOnlyQueryGuard` the DataOps prepare step uses.** An example is a
     query later answers get adapted from, so one that would write if it ran must never enter the store,
     however it was labelled on the way in. Storing an example still runs nothing; execution goes through
     prepare/run and their human gate, unchanged.
   - **A unique `ContentHash` over the normalized question and SQL** makes reaffirming an answer refresh one
     row rather than store a duplicate. Two identical examples would each match the same terms and take two
     of the top-K slots, crowding out every other precedent for that question.

   `QuestionSearch.FindSimilarAsync` reads BOTH stores in one pass and ranks them together, so a confirmed
   example competes with the dashboards' questions instead of arriving in a second list a caller would have
   to merge; on an equal score the confirmed one wins, as the precedent a person actually checked, and each
   match carries its provenance and (for a confirmed one) who confirmed it. The full-text probe is per TABLE
   rather than per deployment, because the two indexes ship in separate migrations and an estate migrated
   while the Full-Text feature was absent can genuinely carry one and not the other.

   The GUI's own accept/correct/reject affordance has since landed: a Yes / Not quite / No row under every
   finished answer that hands back a query (`AnswerConfirmation`), posting to the same confirm endpoint, so
   a click and a `confirm_question` call land the same row and confirming no longer depends on the
   assistant remembering to ask. POWERAI.md Section 10, item 6, records what that row sends and what it
   deliberately does not.

## 9. Open questions

- ~~**Estate-wide vs repo-scoped search.**~~ **Settled**: the caller chooses. `FindSimilarAsync` and the
  endpoint both take a nullable `repoId`, and `find_similar_questions` exposes it as an optional argument
  documented as "omit to search the whole estate", so estate-wide is the default a model gets by doing
  nothing while a caller that knows it wants one repo can say so.
- ~~**Which embedding provider is the default.**~~ **Moot**: there is no embedding provider. Retrieval is
  off by default, and enabling it with `ExpandSynonyms` on but no Anthropic key is a startup error naming
  the exact configuration key (`RetrievalOptions.Validate`), the same posture question generation takes.
- ~~**Re-embedding on a model upgrade.**~~ **Moot**: nothing is stored that a model upgrade invalidates.
- **Is term-count scoring good enough, and where should `RankThreshold` sit?** Unsettled, and the main
  open risk of the mechanism switch (Section 3). The default of 2 is reasoning, not evidence: one shared
  term is routinely coincidence, two rarely is. This needs real questions and real typed queries to tune,
  and it is the first thing to revisit once anyone has run the feature against a live estate.
- **How much does expansion quality vary between questions?** One Anthropic call decides the entire
  search's vocabulary, so a bad expansion is a bad search with no second signal to fall back on. Worth
  watching once there is real usage; `searchedTerms` is returned on every response specifically so this
  is diagnosable from the outside rather than needing a log dive.
