---
id: concept-semantic-layer
title: "Semantic layer: the allow-listed schema an assistant writes SQL against"
type: concept
summary: The column allow-list as the AI assistant's only schema, with descriptions, synonyms, curated keys and joins, measures, examples, and instructions.
keywords:
  - semantic layer
  - semantic context
  - column policy
  - allow-list
  - whitelist
  - business glossary
  - synonyms
  - measures
  - curated relationships
  - text to sql
  - schema grounding
  - get_semantic_layer
  - search_semantic_layer
  - describe_semantic_table
  - power bi upload
  - report specification
  - pbix-extractor
related:
  - concept-data-operations
  - concept-shadow-catalog
sourceRefs:
  - src/SqlFlow.ControlPlane/Api/SemanticLayer.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayerAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticExampleAdminEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticReportEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ReportExtractionClient.cs
  - src/SqlFlow.PbixExtractor/ExtractionEndpoint.cs
  - src/SqlFlow.PbixExtractor/ExtractorOptions.cs
  - gui/src/features/semantic-layer/ReportsPanel.tsx
  - gui/src/features/semantic-layer/ReportUploadDialog.tsx
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - gui/src/features/semantic-layer/SavedAnswersPanel.tsx
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ColumnPolicyGuard.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/semantic-layer/SemanticLayerPage.tsx
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceEndpoints.cs
  - tools/sqlflow-mcp/src/control_plane.rs
---

# Semantic layer

The semantic layer is the schema the AI assistant is allowed to use, together with what that schema means. It
is built on the column allow-list (`CatalogColumnPolicy`, see
[Data operations](data-operations.md#column-policy-what-the-assistant-may-read-at-all)):

- An object (table or view) is **in the layer** exactly when at least one of its catalogued columns is allowed.
  Nothing else records membership.
- Only **allowed columns** are ever served, and nothing served names a column outside the layer: not a key, a
  join, a measure, or an example query.

On top of that set the layer holds business context, all admin-authored:

| Element | Where it lives | What the assistant gets |
| --- | --- | --- |
| Column description and synonyms | `ColumnPolicy.Description`, `ColumnPolicy.Synonyms` | The meaning of each allowed column; synonyms are matched by search |
| Table business name, description, synonyms | `SemanticObject` | What a row represents and what people call the table |
| Curated key | `SemanticObject.KeyColumns` | The columns identifying one row, preferred over the key interpreted from code |
| Curated relationships | `SemanticRelationship` | Joins an admin vouches for, preferred over joins discovered from code |
| Measures | `SemanticMeasure` | Named SQL expressions anchored to one table, to reuse verbatim |
| General instructions | `SemanticLayerSettings.Instructions` | Conventions every query must follow |
| Power BI models | `SubscriberModelTable`, `SubscriberModelField`, `SubscriberModelRelationship`, written at sync from each report and tied to the warehouse table each model table loads from (`ObjectKey`) | The measures, calculated columns (DAX), and relationships reports build on the table |
| Example queries (saved answers) | `SemanticExample` (every question/query pair a person confirmed; renamed from `QuestionExample` by migration `MoveQuestionExamplesIntoSemanticLayer`) | Questions already answered with SQL reading the table |

## What is served, and what is withheld

Every annotation is re-checked against the allow-list each time it is served, because a column can be denied
long after something naming it was written (`SemanticLayer.cs`):

- **Key**: the curated key if every column of it is still allowed, else the codebase-interpreted key
  (`CatalogObject.KeyColumns`) under the same condition, else none. A partial key is never served.
- **Joins**: a curated join, then each join discovered from the codebase (`CatalogObjectRelationship`, folded by
  the same code as the object dossier), served only when the other table is in the layer and every column on
  both sides is allowed. A discovered join with the same columns as a curated one is not served twice.
- **Measures**: composed as `SELECT expression AS [measure_value] FROM anchor` and served only when that parses
  to exactly one scalar select over the anchor (no second table, filter, grouping, or subquery; comments and `;`
  are refused outright) and passes `ReadOnlyQueryGuard` and `ColumnPolicyGuard`.
- **Examples**: the newest `SemanticExample` rows whose `ObjectKeys` include the table, served only when their
  SQL passes `ReadOnlyQueryGuard` and `ColumnPolicyGuard`; at most 10 per table. A confirmed example sent without
  object keys gets them from the catalogued tables its SQL names, and admins correct or delete them on the editor's
  Saved answers tab and on each table's Examples tab.
- **Power BI models**: every report model table whose `ObjectKey` is the table (at most 20). A measure or calculated
  column is served only when every column its DAX reads (`Table[Column]`, `'Table'[Column]`, `[Column]`) is an allowed
  column of the warehouse table its model table loads from, and every measure or calculated column it uses is itself
  servable. A column the report renamed counts as not allowed, since nothing maps it back to its warehouse column. A
  relationship is served only when both model tables load from layer tables and both columns are allowed, spelled as
  the catalog spells them. A report none of whose definitions pass is left out. DAX is served verbatim and never
  evaluated.

The admin editor shows every annotation with its state: `served`, or `withheld` with the reason.

Writes are checked the same way: saving a curated key, a relationship, or a measure over a column that is not
allowed is refused with 400. A duplicate measure name, or a relationship repeating the same tables and columns,
is refused with 409.

## Read API (read scope)

| Route | Returns |
| --- | --- |
| `GET /api/v1/semantic-layer` | Instructions, the (database, schema) pairs holding layer tables with counts, and every servable measure |
| `GET /api/v1/semantic-layer/search?q=` | Layer tables matching every word (name, schema, business name, description, synonyms, or an allowed column's name, description, or synonyms), with the matched columns; plus matching measures |
| `GET /api/v1/semantic-layer/tables?database=&schema=` | Layer tables, paged, with business context and allowed column count |
| `GET /api/v1/semantic-layer/tables/describe?key=` | One table's bundle: allowed columns, key, joins with a ready `on` clause, measures, examples, the Power BI `reportModels` built on it, consumers, instructions. 404 when the table is not in the layer |

## Admin API (admin scope)

| Route | Purpose |
| --- | --- |
| `GET /api/v1/powerai/semantic-layer/schemas` | Objects with catalogued columns per (database, schema), and how many are in the layer |
| `GET /api/v1/powerai/semantic-layer/objects?database=&schema=&name=&inLayer=` | Objects with allowed/total column counts, paged |
| `GET /api/v1/powerai/semantic-layer/objects/detail?key=` | One object's editor state: every column with its policy, annotation, curated and discovered joins, measures, examples, and Power BI report models, each with served/withheld state |
| `PUT /api/v1/powerai/semantic-layer/objects/annotation` | Replace business name, description, synonyms, curated key (all empty removes the annotation) |
| `GET`, `PUT /api/v1/powerai/semantic-layer/instructions` | Read or replace the layer-wide instructions (at most 20000 characters) |
| `GET`, `POST /api/v1/powerai/semantic-layer/measures`; `PUT`, `DELETE .../measures/{id}` | List (with state), create, replace, delete measures |
| `POST /api/v1/powerai/semantic-layer/relationships`; `PUT`, `DELETE .../relationships/{id}` | Create, replace, delete curated relationships (`joinType` `Inner` or `Left`) |
| `GET /api/v1/powerai/semantic-layer/examples?search=`; `GET`, `PUT`, `DELETE .../examples/{id}` | List (newest first, with state), read, correct, and delete saved answers. An edit takes `question`, `sql`, and `sourceRef` (blank to infer), is validated exactly as a confirmation is, re-resolves the tables it reads when the SQL changes, and answers 409 when it would duplicate another. Confirming a new one stays `POST /api/v1/powerai/questions/confirm` (operate scope) |

| `GET /api/v1/powerai/semantic-layer/reports?repoId=&subscriber=`; `GET`, `DELETE .../reports/{id}` | List, read (with the specification, what it holds, and `pages`: the pages and visuals the last sync served under that report's label, each visual with its generated `questions`; empty until a sync has read the report), and delete the Power BI report specifications the layer holds. Deleting queues the repo's managed sync |
| `POST /api/v1/powerai/semantic-layer/reports/questions`; `PUT`, `DELETE .../reports/questions/{id}` | Curate a visual's business questions. `POST` takes `repoId`, `visualKey` (from the report detail), and `question`; `PUT` takes `question`. The text is trimmed, required, one line, and at most 400 characters; a question the visual already has (ignoring case) is refused (409), as is a `visualKey` the repo no longer has (404). Added and edited questions are `manual`: generation never replaces them, and they go only with their visual. An edit that races a sync regenerating the same generated question answers 409 |
| `GET /api/v1/powerai/semantic-layer/reports/subscribers` | The PowerBI subscribers the repositories declare, which are what a report can be attached to |
| `POST /api/v1/powerai/semantic-layer/reports` | Store a specification for a subscriber: `repoId`, `subscriber` (name), `reportFile` (the report's label), `spec`. It is validated and rewritten into canonical, credential-redacted form first (400 when it is not a readable specification); the subscriber must be declared by that repo and be of type PowerBI. Storing the same `reportFile` again replaces the upload (`replaced`). `syncQueued` says whether the repo's managed sync was queued to apply it |
| `POST /api/v1/powerai/semantic-layer/reports/extract?reportFile=` | Body: the raw `.pbix` (`application/octet-stream`). Forwarded to the isolated extractor and answered with the validated specification and its counts, without storing it. 501 when `ControlPlane:PowerAI:ReportExtraction` is off; 413 over `MaxUploadMegabytes`; the extractor's own refusal is passed on (422) |
| `GET /api/v1/powerai/semantic-layer/reports/capabilities` | Whether `.pbix` extraction is enabled, the size limits, and whether sync-time question generation is on (`questionGenerationEnabled`) |
| `GET`, `PUT /api/v1/powerai/semantic-layer/reports/question-generation` | Read or set whether a sync generates business questions for report visuals. `PUT` takes `enabled`: `true` or `false` overrides the deployment's `ControlPlane:PowerAI:QuestionGeneration:Enabled`, `null` follows it again. The answer carries `available` (the deployment has an Anthropic key), `deploymentDefault`, `override`, the effective `enabled`, who changed it and when, and `syncsQueued`. Turning generation on without a key is refused (409). A change that turns it on queues the managed sync of every repo holding report visuals, so the missing questions are generated |

Column allow state, description, and synonyms are written through `PUT /api/v1/powerai/column-policies` (one
column) and `PUT /api/v1/powerai/column-policies/objects` (every column of an object).

## MCP tools

| Tool | Route |
| --- | --- |
| `get_semantic_layer` | `GET /api/v1/semantic-layer` |
| `search_semantic_layer` | `GET /api/v1/semantic-layer/search` |
| `list_semantic_tables` | `GET /api/v1/semantic-layer/tables` |
| `describe_semantic_table` | `GET /api/v1/semantic-layer/tables/describe` |

On the GUI chat and Slack assistants these are the **only** schema tools (`McpOptions.GuiDefaultTools`,
`McpOptions.SlackDefaultTools`). The raw schema readers, which see every catalogued column regardless of the
allow-list (`list_schemas`, `catalog_tree`, `lineage_objects`, `lineage_object_detail`, `lineage_object_columns`,
`describe_object`, `search_all`, `search_schema` (its `objects`, `columns`, `definitions`, and `flowColumns` surfaces),
`pipeline_columns`, `get_table_key`, `get_table_joins`, `detect_unique_key`), are in `McpOptions.ExcludedTools`.
They remain available to other MCP clients, and the GUI's Catalog pages are unchanged.

## The assistant surface

The tool allowlist decides which tools the chat assistants have; the assistant surface decides what the remaining
tools show them. Operational tools such as `run_statements`, `search` (its `statements` and `flows` surfaces),
`pipeline_definition`, and `find_similar_questions` return free text (SQL, flow YAML, stored example queries) that
can name any column, so the allow-list is applied to that text too:

- **Marking.** Both chat gateways give their MCP connector `McpOptions.AssistantServerUri`: `Mcp:ServerUrl` with
  `surface=assistant` added to its query. The MCP server reads the query (`request_surface`) and sends
  `X-SqlFlow-Surface: assistant` on every control-plane call of that session (`control_plane.rs`). The caller's
  bearer still decides what it may do; the header only narrows what it is shown, and the model cannot set or remove
  it. A request without the header, from a person or another MCP client, is served exactly as before.
- **Text.** `AssistantRedactionFilter`, on the read and operate endpoint groups, re-serializes a successful JSON
  result for a marked request and withholds every string that contains a column name outside the layer when that
  column's table is named in the same string or in a string of the same or an enclosing JSON object or array. A
  withheld string reads `[withheld: names a column outside the semantic layer]`; a problem result's `detail` is
  withheld the same way. Results that are not a 200 JSON value (streams, files, 201 and 202 responses) pass through
  unchanged. Matching is by identifier token (bare words, `[bracketed]` and `"quoted"` names), case-insensitive,
  and errs toward withholding: a column name that is allowed on one table but denied on another is withheld when
  the denying table is named nearby.
- **Tasks.** `POST /api/v1/datasources/tasks` from a marked request checks the task as the SELECT it would read,
  through `ReadOnlyQueryGuard` and `ColumnPolicyGuard` (`ColumnPolicyGuard.ComposeTaskSelect`), and refuses it with
  400 `Refused by the column allow-list` otherwise:

  | Task | Checked as |
  | --- | --- |
  | `duplicateKeys` | `SELECT <columns> FROM <schema>.<object>`; the key columns must be named |
  | `compareBaseline`, `inventory` mode | Refused: it lists every object of a schema |
  | `compareBaseline`, `schema` mode | `SELECT * FROM <schema>.<object>` |
  | `compareBaseline`, `data` mode | `SELECT <keyExpressions>, <compareColumns, or *> FROM <schema>.<object> WHERE <where>` |
  | Any other operation | Refused: no assistant tool enqueues one |

Object names are not withheld: operational tools still name tables outside the layer (a run's target, a lineage
step), but never their columns.

## GUI

Admin > **AI knowledge** (`/semantic-layer`; `/column-policies` redirects there) is the semantic layer's editor, labelled for
the people who use it. It has five tabs:

- **Tables**: a database > schema > object tree badged with allowed/total columns (optionally only objects in
  the layer), and for the selected object the About (business name, description, synonyms, key), Columns (allow
  toggle, description, synonyms, reason; allow all, deny all), Relationships (curated and discovered), Measures,
  and Examples (each saved answer reading the table, with edit and delete), and Power BI (what reports define on the table, read-only, each with its
  served/withheld state) tabs. The selected object is in the URL
  as `?object=`.
- **Instructions & measures**: the layer-wide instructions and every measure.
- **Saved answers** (`?tab=examples`; `/saved-answers` redirects here): every saved answer across the layer, with a
  search over question and query text, its datasource, who last stood behind it, its served/withheld state, and
  edit and delete.
- **Power BI reports** (`?tab=reports`): a **Generate business questions at sync** switch (disabled without an
  Anthropic key, with **Use deployment default** to drop an override), then every report specification the layer
  holds, uploaded or kept by a sync,
  with what it contains, who stored it, and whether its subscriber is still declared; open it to see the business
  questions of each of its visuals (page by page, each marked generated or curated) and add, edit, or delete them,
  see its specification, or remove it. **Upload report** attaches a `.pbix` (read by the isolated extractor and shown for review before it is
  saved) or a `.pbix.yaml` specification to a PowerBI subscriber.
- **Blocked columns**: every column outside the layer, denied or never reviewed.

## Power BI reports

A report's measures, relationships, and visuals reach the layer through its specification (see
[subscribers.yaml](../flow/subscribers.md#where-a-report-comes-from)). The layer holds the specifications a
repository does not carry in `catalog.SemanticReportSpec`, which a sync reads but never deletes wholesale: an
`upload` stays until someone removes it, and an `extracted` row is a sync's own copy of a declared `.pbix`.

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
