---
id: concept-semantic-layer
title: "Semantic layer: the schema approved AI contexts let an assistant use"
type: concept
summary: "The approved AI contexts as the assistant's only schema: open columns, meanings, keys, joins, measures, examples, and instructions, re-checked whenever served."
keywords:
  - semantic layer
  - semantic context
  - ai context
  - approved context
  - column policy
  - allow-list
  - business glossary
  - synonyms
  - measures
  - curated relationships
  - text to sql
  - schema grounding
  - business question
  - get_semantic_layer
  - search_semantic_layer
  - describe_semantic_table
  - report specification
  - pbix-extractor
related:
  - flow-ai-context
  - concept-data-operations
  - concept-shadow-catalog
  - guide-chat-assistant
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/SemanticLayer.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticExampleAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticReportEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/AiContextEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/AiContextSync.cs
  - src/SqlFlow.Catalog/AiContextMaterializer.cs
  - src/SqlFlow.ControlPlane/Api/ReportExtractionClient.cs
  - src/SqlFlow.PbixExtractor/ExtractionEndpoint.cs
  - src/SqlFlow.PbixExtractor/ExtractorOptions.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyGuard.cs
  - src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.Assistant/AssistantQuestions.cs
  - src/SqlFlow.Assistant/AssistantRouter.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/semantic-layer/SemanticLayerPage.tsx
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - tools/sqlflow-mcp/src/control_plane.rs
---

# Semantic layer

The semantic layer is the schema an AI assistant is allowed to use, together with what that schema means. It is
made of the **AI contexts an admin approved**: files in a repository (`ai-context.yaml`, `*.ai-context.yaml`; see
[ai-context.yaml](../flow/ai-context.md)) that each name tables, the columns they deny, and what everything means.
A file only proposes; nothing in it is used until an admin approves that exact version. Everything is off by default.

- A table is **in the layer** exactly when an approved context names it. A table belongs to one context.
- Its **open columns** are every column the approved version resolved for it but the ones the file denies. Only open
  columns are ever served, and nothing served names any other column: not a key, a join, a measure, or an example query.
- A question reaches the layer when the chat message starts with **`!`** (the business-question command; `!cwd` is the
  same command). Such a message is answered with the business tool set, whose only schema is the layer; any other
  message is a question about the estate, answered with the estate tool set, which sees every table and column and
  runs no query (see [MCP tools](#mcp-tools)).
- The layer governs data, not schema. Schema is not sensitive: every schema surface (search, an object's column list
  and dossier, flow YAML, executed statements) serves every column to anyone, the assistant included. What the layer
  limits is what a query reads.

Approving a context, revoking it, and every sync that narrows it go through one writer (`AiContextMaterializer`),
which writes the serving tables below with each row marked by its context (`AiContext`). It writes by difference, so a
sync that changes nothing writes nothing and a served example keeps its id. Nothing else writes those rows: the GUI,
the API, and the CLI only read them, approve, revoke, and remove saved answers a person gave.

| Element | From the context file | Served from | What the assistant gets |
| --- | --- | --- | --- |
| Open columns, their descriptions and synonyms | `tables[]` with `deny`, `columns` | `ColumnPolicy` (`IsAllowed`, `Description`, `Synonyms`) | The columns it may use and what each means; synonyms are matched by search |
| Table business name, description, synonyms, key | `name`, `description`, `synonyms`, `key` | `SemanticObject` | What a row represents, what people call the table, and the columns identifying a row |
| Joins | `joins` | `SemanticRelationship` | Joins the context vouches for, preferred over joins discovered from code |
| Measures | `measures` | `SemanticMeasure` | Named SQL expressions anchored to one table, to reuse verbatim |
| Instructions | `instructions` | `AiContext.Instructions` | Conventions every query over the context's tables must follow, headed by the context's name |
| Example questions | `examples` | `SemanticExample`, provenance `ai-context` | Questions already answered with SQL reading the table |
| Saved answers | (confirmed in chat) | `SemanticExample` | Every question/query pair a person confirmed, served on the tables its SQL reads |
| Power BI models | (the subscriber library) | `SubscriberModelTable`, `SubscriberModelField`, `SubscriberModelRelationship`, tied to the warehouse table each model table loads from (`ObjectKey`) | The measures, calculated columns (DAX), and relationships reports build on the table |

While a file proposes a version other than the approved one, the served version is the approved one narrowed by the
file: what the file takes away (a table, a denied column, a measure, a join, an example, the instructions) stops at
the next sync, and what it adds or changes waits for approval. A column the warehouse adds opens only on approval. See
[Approval](../flow/ai-context.md#approval).

## What is served, and what is withheld

Every annotation is re-checked against the open columns each time it is served (`SemanticLayer.cs`), so a column
closed after something naming it was approved withholds that thing at once:

- **Key**: the context's key if every column of it is open, else the codebase-interpreted key
  (`CatalogObject.KeyColumns`) under the same condition, else none. A partial key is never served.
- **Joins**: the context's joins, then each join discovered from the codebase (`CatalogObjectRelationship`, folded by
  the same code as the object dossier), served only when the other table is in the layer and every column on both sides
  is open. A discovered join with the same columns as a context's join is not served twice.
- **Measures**: composed as `SELECT expression AS [measure_value] FROM anchor` and served only when that parses to
  exactly one scalar select over the anchor (no second table, filter, grouping, or subquery; comments and `;` are
  refused outright) and passes `ReadOnlyQueryGuard` and `ColumnPolicyGuard`.
- **Examples**: the `SemanticExample` rows whose `ObjectKeys` include the table, served only when their SQL passes
  `ReadOnlyQueryGuard` and `ColumnPolicyGuard`; at most 10 per table. They are the newest, or, when the describe call
  carries the question being answered, the closest to it: the table's newest 500 are ranked by how many of the
  question's meaningful words each example question shares, scored as stored-question search scores them, with the
  newest first among equals. An older example that answers the question is then the one an assistant mirrors, not
  lost behind ten newer ones about something else. A confirmed example sent without object keys gets them from the
  catalogued tables its SQL names.
- **Power BI models**: every report model table whose `ObjectKey` is the table (at most 20). A measure or calculated
  column is served only when every column its DAX reads (`Table[Column]`, `'Table'[Column]`, `[Column]`) is an open
  column of the warehouse table its model table loads from, and every measure or calculated column it uses is itself
  servable. A column the report renamed counts as closed, since nothing maps it back to its warehouse column. A
  relationship is served only when both model tables load from layer tables and both columns are open, spelled as the
  catalog spells them. A report none of whose definitions pass is left out. DAX is served verbatim and never evaluated.

The AI context page shows every annotation with its state: `served`, or `withheld` with the reason.

The same checks run before a version can be approved: a sync checks every measure and example of a proposed version
as if it were approved, and a table, measure name, or join another context serves is a problem too (see
[What a sync checks](../flow/ai-context.md#what-a-sync-checks)).

## Read API (read scope)

| Route | Returns |
| --- | --- |
| `GET /api/v1/semantic-layer` | The approved contexts serving tables (name, description, table count), their instructions (each headed `Context '<name>':`), the (database, schema) pairs holding layer tables with counts, and every servable measure |
| `GET /api/v1/semantic-layer/search?q=` | Layer tables matching every word (name, schema, business name, description, synonyms, or an open column's name, description, or synonyms), with the matched columns; plus matching measures |
| `GET /api/v1/semantic-layer/tables?database=&schema=` | Layer tables, paged, with business context and open column count |
| `GET /api/v1/semantic-layer/tables/describe?key=&question=` | One table's bundle: open columns, key, joins with a ready `on` clause, measures, examples (the closest to `question` when given, else the newest), the Power BI `reportModels` built on it, consumers, the `context` serving it and that context's `instructions`. 404 when the table is not in the layer; 400 for a question over 1000 characters |

## Admin API (admin scope)

| Route | Purpose |
| --- | --- |
| `GET /api/v1/powerai/ai-contexts` | Every context: status (`awaiting-approval`, `approved`, `changed`, `invalid`), file, proposed and approved versions, who approved it and when, tables proposed and served, problem count; and `hasEarlierContent` |
| `GET /api/v1/powerai/ai-contexts/{name}` | One context: the version its file proposes, the approved version, what approving would change, and its problems |
| `GET /api/v1/powerai/ai-contexts/{name}/yaml` | The context's file as text: `current`, as the last sync read it, and `approved`, as it was when the approved version was approved (absent while none is), each credential-redacted as a flow's YAML is |
| `POST /api/v1/powerai/ai-contexts/{name}/approve` | Approve `{"version": "<the current version>"}`. 409 when the file proposes another version now, or the version has problems |
| `POST /api/v1/powerai/ai-contexts/{name}/revoke` | Stop serving the context; its version waits for approval again |
| `GET /api/v1/powerai/ai-contexts/earlier-content` | What the GUI held before contexts were files, as one context file to review and commit (404 when there is none) |
| `GET /api/v1/powerai/semantic-layer/schemas` | Objects with catalogued columns per (database, schema), and how many are in the layer |
| `GET /api/v1/powerai/semantic-layer/objects?database=&schema=&name=&inLayer=` | Objects with open/total column counts and the context serving each, paged |
| `GET /api/v1/powerai/semantic-layer/objects/detail?key=` | One object as the layer holds it: every column with its state, the annotation, the context's and discovered joins, measures, examples, and Power BI report models, each with served/withheld state, and the context serving it |
| `GET /api/v1/powerai/column-policies`; `GET .../column-policies/objects/{key}` | Every column not open, across the catalog; one object's columns with their state and context |
| `GET /api/v1/powerai/semantic-layer/examples?search=`; `GET`, `DELETE .../examples/{id}` | List (newest first, with state), read, and remove saved answers. One an AI context gives is removed from its file, so `DELETE` answers 409 for it. Confirming a new one stays `POST /api/v1/powerai/questions/confirm` (operate scope) |
| `GET /api/v1/powerai/semantic-layer/reports?repoId=&subscriber=`; `GET`, `DELETE .../reports/{id}` | List, read (with the specification, what it holds, and `pages`: the pages and visuals the last sync served under that report's label, each visual with its business `questions`; empty until a sync has read the report), and delete the Power BI report specifications the layer holds. Deleting queues the repo's managed sync |
| `DELETE /api/v1/powerai/semantic-layer/reports/questions/{id}` | Remove one business question of a visual. A visual left with none gets fresh ones at the next sync when its subscriber generates questions |
| `GET /api/v1/powerai/semantic-layer/reports/subscribers` | The PowerBI subscribers the repositories declare, each with `generateQuestions` (set in its subscriber library) |
| `POST /api/v1/powerai/semantic-layer/reports` | Store a specification for a subscriber (`sqlflow powerbi publish`): `repoId`, `subscriber` (name), `reportFile` (the report's label), `spec`. It is validated and rewritten into canonical, credential-redacted form first (400 when it is not a readable specification); the subscriber must be declared by that repo and be of type PowerBI. Storing the same `reportFile` again replaces the upload (`replaced`). `syncQueued` says whether the repo's managed sync was queued to apply it |
| `POST /api/v1/powerai/semantic-layer/reports/extract?reportFile=` | Body: the raw `.pbix` (`application/octet-stream`). Forwarded to the isolated extractor and answered with the validated specification and its counts, without storing it. 501 when `ControlPlane:PowerAI:ReportExtraction` is off; 413 over `MaxUploadMegabytes`; the extractor's own refusal is passed on (422) |
| `GET /api/v1/powerai/semantic-layer/reports/capabilities` | Whether `.pbix` extraction is enabled, the size limits, and whether this deployment can generate business questions (`questionGenerationAvailable`: an Anthropic key is set) |

## MCP tools

| Tool | Route |
| --- | --- |
| `get_semantic_layer` | `GET /api/v1/semantic-layer` |
| `search_semantic_layer` | `GET /api/v1/semantic-layer/search` |
| `list_semantic_tables` | `GET /api/v1/semantic-layer/tables` |
| `describe_semantic_table` | `GET /api/v1/semantic-layer/tables/describe` |

The GUI chat and Slack assistants answer each message with one of two tool sets, picked by its first character
(`AssistantQuestions.KindOf`, asked once per run by `AssistantRouter`, which both gateways use), each with
instructions that name only its own tools:

| Message | Tool set | What it gets |
| --- | --- | --- |
| Starts with `!` (a business question) | `McpOptions.BusinessDefaultTools` (override with `Mcp:BusinessTools`) | The four semantic layer tools above, `find_similar_questions`, and the query tools: `prepare_query`, `run_query`, `auto_run_trusted_match`, `confirm_question`. Nothing else, so the layer is the only schema it can find |
| Anything else (a question about the estate) | `McpOptions.GuiDefaultTools` or `McpOptions.SlackDefaultTools` (override with `Mcp:AllowedTools`) | The estate readers (`McpOptions.EstateReadTools`): docs, flows, runs, lineage, schedules, subscribers, insights, search, and the whole schema (`list_schemas`, `catalog_tree`, `describe_object`, `search_all`, `search_schema`, `pipeline_columns`, `get_table_key`, `get_table_joins`, the `lineage_object*` readers). The GUI adds the data-operations checks. No semantic layer tool and no query tool |

So "`!` uses the semantic layer" holds by construction rather than by instruction: a business question has no other
schema to find, and an estate question has no query to run. A reply in a business exchange starts with `!` too
("`!run`", "`!save`" in Slack); in the GUI, running and saving are buttons under the answer. `McpOptions.ExcludedTools`
lists what neither set gets: the tools that start work or author code, `detect_unique_key` (it profiles live rows),
session plumbing, and the git history readers.

## The assistant surface

Both chat gateways give their MCP connector `McpOptions.AssistantServerUri`: `Mcp:ServerUrl` with `surface=assistant`
added to its query. The MCP server reads the query (`request_surface`) and sends `X-SqlFlow-Surface: assistant` on
every control-plane call of that session (`control_plane.rs`). The caller's bearer still decides what it may do; the
header only narrows, and the model cannot set or remove it. It narrows data, never schema: every read endpoint
serves the assistant the same text it serves a person.

`POST /api/v1/datasources/tasks` from a marked request reads live rows, so it is checked as the SELECT it would read,
through `ReadOnlyQueryGuard` and `ColumnPolicyGuard` (`ColumnPolicyGuard.ComposeTaskSelect`), and refused with 400
`Refused by the column allow-list` otherwise:

| Task | Checked as |
| --- | --- |
| `duplicateKeys` | `SELECT <columns> FROM <schema>.<object>`; the key columns must be named |
| `compareBaseline`, `inventory` mode | Refused: it lists every object of a schema |
| `compareBaseline`, `schema` mode | `SELECT * FROM <schema>.<object>` |
| `compareBaseline`, `data` mode | `SELECT <keyExpressions>, <compareColumns, or *> FROM <schema>.<object> WHERE <where>` |
| Any other operation | Refused: no assistant tool enqueues one |

## GUI

Admin > **AI context** (`/semantic-layer`; `/column-policies` and `/saved-answers` redirect there) shows the layer
read-only. The only actions are an admin's: approve or revoke a context, and remove a saved answer or a report question
a person or a report gave. Everything else changes in the files.

- **Contexts**: every context with its status, the version its file proposes, who approved what and when, tables
  served and proposed, and its file. Opening one shows, in the workbench's bottom panel, a review: the version served
  beside the one proposed, its problems, what approving would change, its instructions, tables (with columns, key,
  measures, and joins), and example questions, with **Approve** (for the version shown) and **Revoke** in a header
  that stays in view. Its **File** view shows the file itself as the last sync read it, with the editor's hover docs
  and checks, and, when it differs from the file approved, the **Changes since approval** side by side. **Download
  earlier content** appears while content from before contexts were files is waiting to be exported.
- **Tables**: a database > schema > object tree badged with open/total columns and marked with the context serving
  each object, and for the selected object everything the layer holds for it, each item with its served/withheld
  state.
- **Saved answers**: every saved answer, with a search over question and query text, its datasource, its provenance,
  who last stood behind it, and its served/withheld state.
- **Power BI reports**: the PowerBI subscribers with whether each generates business questions, and the report
  specifications the layer holds, with each visual's business questions.

Every question on the page (a context's example questions, a saved answer, a report visual's questions) has a copy
button; in the Saved answers grid it shows when the row is hovered or focused.

## Power BI reports

A report's measures, relationships, and visuals reach the layer through its specification (see
[subscribers.yaml](../flow/subscribers.md#where-a-report-comes-from)). The layer holds the specifications a
repository does not carry in `catalog.SemanticReportSpec`, which a sync reads but never deletes wholesale: an
`upload` (from `sqlflow powerbi publish`) stays until someone removes it, and an `extracted` row is a sync's own copy
of a declared `.pbix`.

The control plane never parses a `.pbix`. `POST .../reports/extract` streams the upload to the isolated extractor
(`SqlFlow.PbixExtractor`, image `Dockerfile.pbix-extractor`), a separate service holding no catalog, warehouse, or
git credential, reachable only on a private network, and authenticated by a shared key. The extractor writes the
upload to a private temporary directory, runs `pbix-extract` on it, removes it, and answers with the specification.
The control plane then validates that answer as it would any upload, refusing anchors and aliases, oversized
documents, and malformed graphs, before showing or storing it.

| Setting | Meaning |
| --- | --- |
| `ControlPlane:PowerAI:ReportExtraction:Enabled` | Turns `.pbix` uploads on (default off). Specification uploads work either way |
| `ControlPlane:PowerAI:ReportExtraction:Endpoint` | The extractor's base address, e.g. `http://pbix-extractor:8080` |
| `ControlPlane:PowerAI:ReportExtraction:ApiKey` | The shared key, literal or `${env:...}`/`${keyvault:...}` |
| `ControlPlane:PowerAI:ReportExtraction:MaxUploadMegabytes` | Largest `.pbix` accepted (default 256) |
| `ControlPlane:PowerAI:ReportExtraction:TimeoutSeconds` | Budget for one extraction, including waiting for a free slot (default 360) |
| `PbixExtractor:ApiKey` (extractor) | The same shared key, at least 32 bytes |
| `PbixExtractor:MaxUploadMegabytes`, `MaxConcurrent`, `QueueLimit` (extractor) | Upload cap (256), reports extracted at once (2), and requests allowed to wait for a slot before new ones get 503 (8) |
