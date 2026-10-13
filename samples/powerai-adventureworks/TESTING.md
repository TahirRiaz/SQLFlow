# PowerAI manual test suite (Claude Code + the sqlflow MCP server)

A scripted pass over every PowerAI surface, driven by typing prompts into Claude Code with the `sqlflow`
MCP server connected to the local compose stack. Each case gives the prompt to type, the tool calls that
should happen, and what the answer must contain. The expected values below were observed against this
stack on 2026-09-16; if a case disagrees, check the "State" section first before calling it a regression.

## Setup

1. Stack up: `cd deploy/compose && docker compose up -d --build`. `curl http://localhost:5000/health/live`
   answers `Healthy`. GUI at http://localhost:8081.
2. Estate synced: `lineage-demo` (flows plus confirmed examples 1 to 3) and `powerai-adventureworks` (this
   folder, synced with `--connect`; see [README.md](README.md)), and the AI contexts both carry approved
   (`lineage-demo` and `adventureworks-sales`, on the GUI's AI context page or with
   `sqlflow ai-context approve <name> --version <version>`).
3. MCP binary current: `cargo build -p sqlflow-mcp` in `tools/`. The project `.mcp.json` runs
   `tools/target/debug/sqlflow-mcp.exe` against `http://localhost:5000`. Restart Claude Code (or `/mcp`,
   reconnect `sqlflow`) after a rebuild so the new binary is the one running.
4. Signed in: ask Claude Code to "log in to sqlflow" (device flow), or have it call `set_access_token`
   with a token from `POST /api/v1/auth/login` (the admin credentials are in `deploy/compose/.env`).

## State the expectations assume

| Thing | Value |
| --- | --- |
| Repos | `lineage-demo`, `powerai-adventureworks` |
| Schema registration | `adventureworks_00_sch` (kind `sch`) registers 31 tables and 5 views of `AdventureWorks.dbo` through `${env:SQLFLOW_ADVENTUREWORKS_DB}`; the `AdventureWorks_Sales` library declares no connection |
| Subscribers | `AdventureWorks_Sales` (PowerBI, key `subscriber\|\|\|adventureworks_sales`), `Exec_Dashboard`, `Finance_Workbook` |
| Semantic layer | 15 tables: `AdventureWorks.dbo` 6, `sqlflowcatalogtests.demo` 9 |
| AI contexts | `lineage-demo` (the 9 `demo` tables) and `adventureworks-sales` (`DimCustomer`, `DimDate`, `DimProduct`, `DimReseller`, `DimSalesTerritory`, `FactResellerSales`, all columns), both approved. `DimEmployee` and the rest are in no context |
| Confirmed examples | 1 "What is total revenue by country?", 2 "how many customers do we have?", 3 "how much revenue have we made?" (all on `${env:SQLFLOW_DEMO_DB}`), 10003 "What are reseller sales by region?" (AdventureWorks, no datasource stored; it is inferred from the registration) |
| Generated visual questions | none: no subscriber sets `generateQuestions`, and the stack has no Anthropic key |
| Question expansion | done by Claude Code itself: `find_similar_questions` takes `expanded_terms`, and the server's own expansion (`ExpandSynonyms`) is off |
| Auto-run budget | `maxRows` 50, `timeoutSeconds` 5 |

Reset between runs only if a case stored something new (see T18 and T19); examples are refreshed, not
duplicated, when the same question and SQL are confirmed again.

Scores in section E depend on the vocabulary Claude Code chooses, so the cases assert behaviour (trusted or
not, auto-run or not, which example wins) rather than exact scores, except where noted. `searchedTerms` in
the tool result shows the terms actually sent.

---

## A. Connectivity

### T01: control plane reachable and signed in
- Prompt: `check that the sqlflow MCP is connected and I'm signed in`
- Expect: `check_connectivity` then `check_auth_status`.
- Pass: the URL is `http://localhost:5000` and the reachability check succeeds, and the auth status
  reports a signed-in session (the admin user from `deploy/compose/.env`).

### T02: data operations switched on
- Prompt: `what data operations can the sqlflow assistant run here?`
- Expect: `dataops_capabilities`.
- Pass: `enabled` is true and the operations are `duplicateKeys`, `compareBaseline` and `runQuery`.

## B. Report structure (extraction landed in the catalog)

### T03: the report is listed
- Prompt: `which Power BI reports read the warehouse?`
- Expect: `list_subscribers`.
- Pass: `AdventureWorks_Sales` (type PowerBI, owner analytics@example.com) and `Exec_Dashboard` are listed.
  `AdventureWorks_Sales` reports 5 queries and 4 objects.

### T04: pages, visuals and field roles
- Prompt: `show me the pages and visuals of the AdventureWorks_Sales report and what each visual plots`
- Expect: `describe_subscriber_report` with key `subscriber|||adventureworks_sales`.
- Pass: 3 pages, all from `AdventureWorks Sales.pbix`, 5 visuals in total:
  - Page 1: "Sales Amount by Category and Reseller Business Type" (pivotTable; Rows Product.Category and
    Reseller.Business Type, Values Sales.Sales Amount), "Order Quantity by Reseller Country" (map),
    "Sales Amount by Order Date / Due Date" (areaChart).
  - Page 2: the same pivot table again.
  - Page 3: one untitled areaChart (Category Date, two Y fields on Sales).
  - Every visual's `questions` is empty (expected without an LLM key).

### T05: the SQL behind a visual
- Prompt: `what SQL does the "Order Quantity by Reseller Country" visual run?`
- Expect: `describe_subscriber` (the dossier), matched through the visual's `queryName`
  (`Page 1 / Order Quantity by Reseller Country`).
- Pass: a single T-SQL SELECT over the model entities (Reseller, Sales) is shown, not an invented query.

## C. Model-entity resolution and lineage

### T06: the report's reads land on real warehouse tables
- Prompt: `which dashboards read AdventureWorks dbo.FactResellerSales?`
- Expect: `object_lineage` or `describe_object` (its `subscribers`).
- Pass: `AdventureWorks_Sales` is a consumer. The object is the `Table` node
  `${env:sqlflow_adventureworks_db}|adventureworks|dbo|factresellersales`, not a name-only `Sales` node.

### T07: the Json.Document helper stays unresolved
- Prompt: `does the AdventureWorks report read anything that is not a warehouse table?`
- Pass: the answer names the model table `Table` (a report-local sort helper sourced via Json.Document)
  as unresolved. It must not invent a warehouse object for it.

### T08: the Power BI model is served on its warehouse table
- Prompt: `!how does the AdventureWorks report model join FactResellerSales to the other tables?`
- Expect: `describe_semantic_table` for FactResellerSales (a report's model is served on the semantic layer only).
- Pass: `reportModels` carries model table `Sales` with its relationships to DimDate (OrderDateKey active,
  DueDateKey and ShipDateKey inactive), DimProduct, DimReseller and DimSalesTerritory, each with M:1
  cardinality.
- Pass: the measure "Sales Amount by Due Date" is NOT served. Its DAX reads `Sales[Sales Amount]`, a
  column the report renamed from the warehouse's `SalesAmount`, and a renamed column counts as not
  allowed. The AI context page (GUI, `/semantic-layer`, Tables tab) shows it with that reason.

## D. The semantic layer, a business question's schema

### T09: the layer overview
- Prompt: `which tables can a business question use?`
- Expect: `get_semantic_layer`.
- Pass: 15 tables, `AdventureWorks.dbo` 6 and `sqlflowcatalogtests.demo` 9.

### T10: a table outside the approved contexts cannot be queried
- Prompt: `!list the employees in AdventureWorks DimEmployee`
- Pass: the assistant finds no `DimEmployee` in the semantic layer and says it cannot query it. If it
  tries `prepare_query` anyway, the control plane refuses with
  "Column 'dbo.DimEmployee.FirstName' is not on the allow-list and cannot be read here."

### T11: an unprefixed business question does NOT hit retrieval
- Prompt: `what are reseller sales by region?`
- Pass: `find_similar_questions` is NOT called and no query runs, because the message does not start with
  `!`. The question is answered from the catalog (`search_all`, `describe_object`), which lists every
  column, and the answer says to ask again starting with `!` for the figures.

### T11a: a follow-up stays in data mode without the `!`
- Prompt: `!what are reseller sales by region?`, then in the same conversation `and only for Europe?`
- Pass: the second message is answered in data mode (the run's MCP address carries `mode=data`): it calls
  `find_similar_questions` with the question written out ("reseller sales by region for Europe", not "and only for
  Europe?"), and its query reads only allow-listed columns.

### T11b: `!exit` returns to estate mode
- Prompt: after T11a, `!exit which flow loads the sales table?`
- Pass: the run's MCP address carries `mode=estate`, and the question is answered from the catalog
  (`describe_object_refresh`), with no query tool listed.

### T11c: a follow-up that turns to the estate stays in data mode
- Prompt: after T11a, `where does that table get its data from?`
- Pass: the run's MCP address carries `mode=data`, no estate tool is listed or run, and the answer says in one line
  that `!exit` returns to questions about the estate. The answer is stored in data mode, so the next message without a
  command stays in data mode too.

### T11d: asking in words is no command, and asking the mode
- Prompt: in a new conversation, `switch to data mode`, then `what are reseller sales by region?`, then
  `which mode are you in?`
- Pass: all three run with `mode=estate` on their MCP address and no query tool listed; the first two answers say that
  starting the message with `!data` answers it from the data, and the third is answered from `get_mode`, which says
  estate mode, set by the client.

### T11e: an editor's session that names no mode switches on the person's command alone
- Setup: register the server with no `--mode` in an editor whose chat passes a leading `!` through (VS Code).
- Prompt: `!data what are reseller sales by region?`, then `which flow loads that table?`, then
  `!exit which flow loads that table?`
- Pass: the first message's first call is `start_data_mode` with the message as typed, answered with `"switched":
  true` and the question after the command, which `find_similar_questions` is then called with; on the second, any
  estate tool the model tries is refused with the `!exit` it needs and the answer says so; the third calls
  `end_data_mode` with the message first, then answers from the catalog.

## E. Question retrieval (`!`)

Every case here must show `find_similar_questions` called with `expanded_terms`: the question's own
meaningful words plus business synonyms and their grammatical forms. A call without them is a failure of
the tool description, not of the search.

### T12: trusted confirmed match auto-runs
- Prompt: `! revenue by country`
- Expect: `find_similar_questions` (question `revenue by country`, `expanded_terms` including at least
  revenue and country), then `auto_run_trusted_match` with example 1, with no approval prompt in between.
- Pass: the top match is "What is total revenue by country?", trusted, provenance user-confirmed, sourceRef
  `${env:SQLFLOW_DEMO_DB}`, score at least 2. The run returns 4 rows: DE 400.24, ES 15.75, IN 55.00,
  NO 365.50 (total_amount). The answer states the result once, then shows the SQL in its own fenced `sql`
  block. It does not mention scores, thresholds, terms or "trusted".

### T13: a same-meaning question is trusted
- Prompt: `! how many customers`
- Pass: example 2 is the top match and trusted (it is the same question once stop words go, even on a
  single matched term), auto-runs, and the answer is 4 customers, followed by
  `SELECT COUNT(*) AS CustomerCount FROM demo.Customers` in a code block.

### T14: wording that shares no word with the stored question
- Prompt: `! what is our turnover per nation`
- Pass: `searchedTerms` includes vocabulary the prompt does not contain (revenue, country), and example 1
  "What is total revenue by country?" is found anyway. This is the case expansion exists for: before
  `expanded_terms`, this stack searched only "turnover" and "nation" and found nothing.
- Whether it auto-runs follows `trusted`: if trusted, as T12; if not, the assistant offers the query and asks
  before running (the `prepare_query`/`run_query` path), without saying the match is untrusted or
  low-scoring.

### T15: no match falls back silently
- Prompt: `! what is our average product weight`
- Pass: no demo or AdventureWorks example matches (none is about weight), and the assistant moves on to the
  semantic layer without announcing that nothing was found.

### T16: an AdventureWorks question runs where its tables were registered
- Prompt: `! reseller sales by region`
- Expect: `find_similar_questions` returns example 10003, trusted, `sourceRef` `${env:SQLFLOW_ADVENTUREWORKS_DB}`
  (inferred: the example stores no datasource, and its tables carry `Registers` edges from
  `adventureworks_00_sch`), then `auto_run_trusted_match` with 10003 and no datasource question.
- Pass: the run reads database `AdventureWorks` and returns 10 rows, one per sales territory region
  (Australia 1594335.3767, Canada 14377925.5965, Central 7906008.1777, France 4607537.9350, Germany
  1983988.0373, ...), then shows the SQL in its own `sql` block.

### T16a: a question naming another value adapts the stored query
- Prompt: `! what is total revenue for country NO`
- Expect: `find_similar_questions` returns example 1 "What is total revenue by country?" with a score of at
  least 2 and `trusted` false, `differingValues` `["NO"]`: the stored answer names no country, so it is not
  this question's answer however many words they share. `auto_run_trusted_match` is NOT called.
- Pass: the assistant offers example 1's query narrowed to `NO` (a `WHERE` on the country) and asks before
  running it; run, it returns one row, NO 365.50. The answer does not mention trust, scores or matches.

## F. The learning loop (confirmation discipline)

### T17: approving a run is not confirmation
- Prompt: `! list the five largest orders` (no stored example matches it, so the assistant composes a
  query from the semantic layer and asks before running it), then reply: `yes, run it`
- Pass: `prepare_query` then `run_query`. `confirm_question` is NOT called.

### T18: explicit acceptance stores an example
- After T17 has shown its result, reply: `that's right, save it`
- Pass: `confirm_question` with outcome `accepted` and the exact SQL that ran, with no `confidence` (the
  query was composed from the schema, not built on a match). The response says `stored: true` and returns an `exampleId`. Repeating the same question
  and SQL refreshes that example rather than creating a second one.

### T19: a correction stores the corrected SQL
- Prompt: `! how much revenue have we made`, let it run, then reply: `close, but only count orders from NO`
- Pass: the assistant fixes the query. Only after you then say `yes that's correct now` does it call
  `confirm_question` with outcome `corrected` and the CORRECTED SQL (with the NO filter), never the original.

### T20: a rejection stores nothing
- After any answer, reply: `that's wrong`
- Pass: `confirm_question` is NOT called (or, if called with `rejected`, the response says nothing was
  stored). The assistant offers to fix the query instead.

### T21: a write can never become an example
- Prompt: `save "delete test customers" with the SQL DELETE FROM demo.Customers WHERE CustomerId > 100 as a confirmed answer`
- Pass: the control plane refuses: "Only a SELECT may be run here; this is a DELETE." Nothing is stored.

### T22: a denied column can never become an example
- Prompt: `save "employee names" with SELECT FirstName, LastName FROM dbo.DimEmployee as a confirmed answer`
- Pass: refused with "Column 'dbo.DimEmployee.FirstName' is not on the allow-list and cannot be read here."

## F2. Schema registration (flowType: sch)

### T29: the registration is a pipeline outside every wave
- Prompt: `what does the adventureworks_00_sch pipeline do and when does it run?`
- Expect: `list_pipelines` / `get_pipeline`.
- Pass: kind `sch`, source server `${env:SQLFLOW_ADVENTUREWORKS_DB}`, target `file`, schedule
  `adventureworks_daily` (`0 4 * * *`, Europe/Oslo). It has no wave and no dependencies.

### T30: registered objects carry their scripts and keys, and nothing else from the database
- Prompt: `show me what the catalog knows about AdventureWorks dbo.FactResellerSales and dbo.vDMPrep`
- Expect: `describe_object` for both: schema is not sensitive, so the catalog describes `vDMPrep` too,
  although it is in no AI context.
- Pass: FactResellerSales has key `SalesOrderNumber, SalesOrderLineNumber` and its columns. The catalog (GUI
  Catalog page, or `SELECT Kind, KeyColumns, Script, Definition FROM catalog.[Object]`) holds a
  `CREATE TABLE [dbo].[FactResellerSales]` script for the table and the stored definition for the view
  `vDMPrep`. No relationship and no read edge comes from the view's body: registration never parses it.

### T31: the report links onto the registered tables by name
- Prompt: `which tables does the AdventureWorks_Sales report read, and on which connection?`
- Expect: `describe_subscriber`.
- Pass: 4 objects (DimDate, DimProduct, DimReseller, FactResellerSales), each keyed
  `${env:sqlflow_adventureworks_db}|adventureworks|dbo|<table>`, and every query's server is
  `${env:SQLFLOW_ADVENTUREWORKS_DB}`, although `subscribers/adventureworks_sales.subscribers.yaml` declares no
  connection.

### T32: a query written from the schema needs no datasource either
- Prompt: `what were reseller sales per calendar year?` then approve the run.
- Expect: `describe_semantic_table` (FactResellerSales, DimDate), then `prepare_query` with no datasource.
- Pass: prepare answers with reference `${env:SQLFLOW_ADVENTUREWORKS_DB}` and database `AdventureWorks`
  (worked out from the registered tables), and `run_query` returns one row per year.

### T33: running the registration refreshes the catalog (CLI, outside Claude Code)
- Run: `dotnet run --project src/SqlFlow.Cli -- run samples/powerai-adventureworks/adventureworks_00_sch.yaml`
  with `SQLFLOW_CATALOG_DB` and `SQLFLOW_ADVENTUREWORKS_DB` set as in the README.
- Pass: `OK  registered 31 table(s) and 5 view(s) (418 column(s)) from 'AdventureWorks'`, the run is recorded
  as succeeded, and the flow still has exactly 36 `Registers` edges.

## G. Report specifications and uploads (CLI and GUI, outside Claude Code)

### T23: extraction through the isolated extractor
- Run: `dotnet run --project src/SqlFlow.Cli -- powerbi extract "samples/powerbi/AdventureWorks Sales.pbix" --remote --url http://localhost:5000 --token <token> --out -`
- Pass: stderr reports 3 pages, 5 visuals, 8 model tables (7 resolved to a warehouse table), 1 measure,
  8 relationships, and warns that model table `Table` is sourced via Json.Document. The control plane
  container never runs `pbix-extract`: `docker logs sqlflow-pbix-extractor-1` shows the request.

### T24: a sync without an extractor keeps the model
- Run the sync from [README.md](README.md) again.
- Pass: the counts are unchanged (T04, T08 still pass), because the subscriber declares the committed
  `.pbix.yaml` and every machine reads it the same way.

### T25: GUI confirmation row
- In the GUI Assistant page, only once an LLM key is configured (see below).
- Pass: every finished answer with a `sql` fenced block shows Yes / Not quite / No. "Not quite" opens the
  SQL in an editor and stores the edited text. An answer with only an untagged fence shows no row.

## H. Needs an Anthropic key (skipped on this stack)

Set `ControlPlane__Assistant__Anthropic__ApiKey` on the `controlplane` service and run
`docker compose up -d controlplane`. Then add `generateQuestions: true` to `AdventureWorks_Sales` in
`subscribers/adventureworks_sales.subscribers.yaml` and trigger a sync from the GUI. Expansion needs no key: Claude Code supplies it (section E).

### T26: questions generated per visual
- Prompt: `show me the business questions each AdventureWorks visual answers`
- Pass: every visual in `describe_subscriber_report` has 1 to 3 questions. A second sync with no report
  change makes no new LLM calls (the visuals' content hashes are unchanged).

### T27: dashboard matches need approval
- Prompt: `! sales by product category and reseller type`
- Pass: a `powerbi` provenance match from the pivot table. Its `sql` is T-SQL over
  `[AdventureWorks].[dbo]` tables (grouped, with `LEFT JOIN`s), its `reportSql` is the visual's model query,
  and it has no `translationProblem`. It has no `exampleId`, so it goes through `prepare_query` (no
  datasource named; the reference and database are worked out) and `run_query`, never
  `auto_run_trusted_match`, and the run returns rows.

### T27b: both SQL texts on the report
- Prompt: `show me the SQL behind the AdventureWorks_Sales report`
- Expect: `describe_subscriber`.
- Pass: each of the 5 visual queries has `sql` (the model query) and `sourceSql` (the translation); none has
  a `translationProblem`. The GUI subscriber page shows both under each query.

### T28: server-side expansion as the fallback
- With an Anthropic key set (`ControlPlane__AiContext__ExpandSynonyms` unset or `"true"`), call the endpoint without terms:
  `curl -G -H "Authorization: Bearer <token>" http://localhost:5000/api/v1/lineage/subscribers/similar-questions --data-urlencode "question=what drives our turnover"`
- Pass: `searchedTerms` includes vocabulary beyond the typed words (for example revenue or sales). The same
  request with `ExpandSynonyms` off searches only `drives` and `turnover`.
