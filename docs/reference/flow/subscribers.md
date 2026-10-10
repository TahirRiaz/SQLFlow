---
id: flow-subscribers
title: subscribers.yaml and consumption lineage
type: flow-reference
summary: Declare who consumes the warehouse in a subscribers.yaml; every subscriber query is parsed into read edges, so lineage answers which report uses which table.
keywords:
  - subscribers.yaml
  - data subscriber
  - subscriber
  - consumer
  - powerbi
  - tableau
  - dashboard
  - report
  - consumption lineage
  - who reads this table
  - impact analysis
  - downstream
  - DataSubscriber
  - DataSubscriberQuery
  - subscriber notes
  - incomplete dataset
  - stale report
  - pbix
  - pbix-extract
  - nodes and edges
  - node graph
  - measures
  - relationships
  - visual fields
  - business question
  - question generation
  - text-to-query
  - pbix.yaml
  - report specification
  - sqlflow powerbi extract
  - uploaded report
  - registered source
  - schema registration
yamlPath: subscribers
related:
  - flow-sch
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - flow-schedule
  - flow-overview
sourceRefs:
  - src/SqlFlow.Core/Subscribers/DataSubscriber.cs
  - src/SqlFlow.Lineage/Collection/SchemaRegistrationCollector.cs
  - docs/reference/flow/keys.subscribers.json
  - src/SqlFlow.Yaml/YamlSubscriberLibraryLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Lineage/Collection/PbixExtractTool.cs
  - src/SqlFlow.Lineage/Collection/ReportSpecs.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/SubscriberQuestionEnrichment.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Assistant/QuestionGenerator.cs
  - tools/pbix-extract/src/main.c
---

# subscribers.yaml

Lineage without the consumption side stops at the last table SQLFlow writes. `subscribers.yaml` continues it: it declares the reports, workbooks, notebooks, and applications that READ the warehouse, and the queries each one runs. Those queries are parsed, so the objects they touch become real lineage edges on the same nodes the loading flows write. The estate can then answer, from the graph rather than from memory, which dashboards break if a table changes.

This is the V3 form of the legacy `flw.DataSubscriber` and `flw.DataSubscriberQuery` tables. The legacy pair carried `FlowID`, `FlowType: 'sub'`, and a `Batch`, purely so the old catalog could log a row per subscriber; a subscriber runs nothing, so V3 drops that plumbing and identifies a subscriber by name like every other object.

```yaml
# subscribers.yaml
connections:
  dwh: ${env:SQLFLOW_CONN_DWH}

subscribers:
  Analyse_Citybikes:
    type: PowerBI
    owner: analyse@contoso.example
    description: City bike usage and station occupancy
    notes: |
      Not refreshed since April 2024; owner asked whether it is superseded by Analyse_Citybikes_statistikk.
    url: https://app.powerbi.com/groups/me/reports/abc123
    server: dwh
    queries:
      - name: Turer
        sql: |
          SELECT t.TripId, t.StartedAt, s.StationName
          FROM   arc.Citybikes_Trips AS t
          JOIN   arc.Citybikes_Stations AS s ON s.StationId = t.StartStationId
      - name: Stasjoner
        sql: |
          SELECT * FROM pre.v_Citybikes_Stations
```

## Where the file lives

A subscriber library is any file named `subscribers.yaml` or ending in `.subscribers.yaml`, anywhere under the scanned folder, and every match is merged into one set.

**One file per subscriber is the convention**, named for the subscriber and gathered in a `subscribers/` folder:

```
subscribers/analyse_sanntid.subscribers.yaml
subscribers/dashboard_mpc.subscribers.yaml
subscribers/boatbooking_report.subscribers.yaml
```

A consumer is an independently owned thing: it is added, retired, and re-pointed on its own schedule, by whoever owns the report rather than by whoever owns the estate. One file per subscriber keeps that ownership legible in the diff, and keeps a change to one report out of everyone else's blame. Each file is parsed on its own, so each declares its own `connections:` block. A single file holding several subscribers still works and is the right shape for a handful of them.

A subscriber name is the estate's identity for a consumer, so the same name in two files is a collision, not a merge: the first wins and the second is reported.

Like `schedules.yaml`, these files are NOT flow documents: they are excluded from the flow parse, never become pipelines, never join a schedule, and never appear in an execution wave. A subscriber that leaked into the flow set would sit in a wave forever waiting to run a Power BI report.

## Keys

| Key | Required | Meaning |
| --- | --- | --- |
| `connections` | yes, when any query names a server | The same `connections:` block every flow document uses: alias to a SQL Server reference. A bare alias resolves `${env:SQLFLOW_CONN_<NAME>}` by the canonical convention, so the file can stay reference-free. A library with no `connections:` at all is a [registered-source library](#registered-source-subscribers). |
| `subscribers` | yes | Maps a subscriber's NAME to its declaration. The name is its identity across the estate and the label on its graph node. |
| `subscribers.<name>.type` | no (warns) | What consumes the data (legacy `SubscriberType`): `PowerBI`, `Tableau`, `Excel`, `Notebook`, `Application`, or any label the estate uses. Free text, as the legacy column was. Omitted records `Unknown` with a warning. |
| `subscribers.<name>.owner` | no | Who to contact before a breaking change to a table it reads (legacy `CreatedBy`). |
| `subscribers.<name>.description` | no | What the subscriber is FOR, in one line, for the catalog and the node's tooltip. |
| `subscribers.<name>.notes` | no | Remarks about the subscriber's STATE rather than its purpose. Free text, multi-line via a block scalar. See [Notes](#notes). |
| `subscribers.<name>.url` | no | Where the subscriber LIVES (as opposed to what it reads): the report URL, the workbook path, the share, the repository. Searchable. See [Location](#location). |
| `subscribers.<name>.server` | no | The default connection alias for every query that does not name its own, and the connection a report's visuals are resolved against. |
| `subscribers.<name>.pbix` | no | A `.pbix` file, its committed specification (`<report>.pbix.yaml`), or a directory holding either. See [Extracted from a .pbix report](#extracted-from-a-pbix-report). |
| `subscribers.<name>.queries` | yes, in practice, unless `pbix` is set | The queries the subscriber runs. A subscriber with none, no `pbix`, and no report uploaded to the semantic layer is a node nothing connects to, which is warned. |
| `queries[].name` | no | The query's label (legacy `QueryName`): the dataset, page, or measure group. Defaults to `query<n>` by position. |
| `queries[].server` | yes, unless the subscriber sets one | The connection alias this query runs against (legacy `srcServer`). It is what pins two-part names to the right server and database. |
| `queries[].sql` | yes | The query text as the subscriber runs it (legacy `FullyQualifiedQuery`). Any T-SQL the parser accepts. |

A malformed entry is dropped with a warning rather than throwing, exactly as an unparseable flow document is: one bad subscriber must not blind the estate's lineage. A query whose `server` is not declared in `connections:` is refused, because its objects would otherwise land on an invented identity and quietly build a second, wrong graph.

## Notes

`description` and `notes` answer different questions, and separating them is the point of having both. A description says what the report is FOR, and stays true for as long as the report exists. A note says what is currently WRONG or unresolved about it, and is expected to be fixed and deleted:

```yaml
subscribers:
  Dashboard_Salg:
    type: PowerBI
    description: Sales over the Fara traffic income, the mobile-app sales fact, and the Reisefrihet tickets
    notes: |
      Inaktivitet. Men denne har jo jeg hatt apen denne uka?
      Incomplete dataset. Not resolved in the new warehouse:
        Archive VY_Pr_Dag_Enkeltbillett  (no such object)
        Q_ZoneFra                        (Power BI query step, no warehouse object)
```

Two uses earn their own conventions, because a person scanning the subscriber list should be able to spot them without reading every note:

**Stale or retired reports.** When a review finds a report has not refreshed in months, looks superseded, or could not be opened at all, the finding belongs here rather than in a spreadsheet that drifts away from the estate. The note travels with the declaration, so whoever next changes a table the report reads sees it.

**Incomplete datasets.** A subscriber whose real report reads objects that could NOT be resolved in the warehouse is registering partial lineage, and that partiality must be visible. Start the note with `Incomplete dataset` and list what could not be identified and why. Without it, the graph quietly reads as complete: the report appears to consume exactly the tables that happened to resolve, and the missing ones look like they were never there. This is the normal state during a migration, where a report still names objects the new estate has not built or has retired.

`notes` is unbounded in the catalog where `description` is capped at 1024 characters, so a long remark can never fail a sync. It is searchable from `GET /lineage/subscribers?search=` alongside the name, owner, and description, which is what makes `search=Incomplete dataset` a usable estate-wide audit.

## Location

`url` answers a different question from everything else in the file: not what the report reads, but where to go and look at it. That turns out to be most of what a person wants when a report surfaces in a search, so it is worth filling in even though nothing breaks without it.

```yaml
subscribers:
  Dashboard_Salg:
    url: https://app.powerbi.com/groups/<workspace-id>/reports/<report-id>
  Analyse_Batbooking:
    url: \\fileserver\BI\Rapporter\Analyse_Batbooking.pbix
```

It is free text on purpose, because a consumer is as often a workbook on a share as it is a hosted report. Only an `http`/`https` value renders as a clickable link; anything else is shown as plain selectable text, so a UNC or file path never becomes a link that silently does nothing when clicked.

It is searched alongside the name, owner, description, and notes, which is what lets a person who knows only where a report sits (a workspace id, a share, a folder) get from that back to the tables it reads.

## How a query becomes lineage

Each `sql` goes through the same `TSqlLineageExtractor` a stored-procedure body, a document hook, and a generated transform view go through. The resulting facts are attributed as MODULE facts: `Flow` is null and `ViaModule` is the subscriber's node key. That is precisely what a subscriber is to the graph, a body of SQL that reads objects but runs no pipeline, so nothing in the edge model, the execution plan, or the wave computation needed changing to hold it.

Because the identities go through the same completion (default database from the connection, identity unification, synonym follow) as every other fact, a report reading `arc.Citybikes_Trips` lands on the SAME node the ingestion flow writes. A report reading a view lands on the same view node the flows read, and the view's own module edges continue the chain down to its base tables.

The subscriber itself becomes an object node of kind `Subscriber` on the synthetic server identity `subscriber`. It is the only node kind that lives outside the databases SQLFlow moves data between, and the only one no database inventory can supply.

Joins written inside a subscriber query also feed the interpreted data model (`LineageReport.Relationships`). The joins an analyst writes in a report are evidence of how the business actually relates these tables, and carry the same weight as a warehouse view's.

## Extracted from a .pbix report

A subscriber can declare `pbix:` naming one `.pbix` file or a directory of them, instead of (or alongside) hand-written `queries:`. Every report found is extracted automatically by `tools/pbix-extract`, a standalone C tool run outside the control plane (a `.pbix` is attacker-influenceable input, so its decompression and SQLite reading never happen inside the server process). Each extracted visual becomes a query on this same subscriber, taking the same lineage path as a hand-transcribed one.

```yaml
subscribers:
  Analyse_Bysykkel:
    type: PowerBI
    server: dwh
    pbix: reports/analyse_bysykkel.pbix
```

The tool's output is a YAML specification of the report: its semantic model (tables, columns, measures, calculated columns, relationships, table sources) and its visual layer (pages, visuals, each visual's rendered SQL, and the fields it projects with their role), expressed as a flat graph rather than a name-keyed tree:

```yaml
nodes:
  - id: "Analyse_Bysykkel#analyse_bysykkel.pbix#table:Trips"
    kind: "table"
    powerQuery: "let\n    Source = Sql.Database(\"dwh\", \"OdsDb\"),\n    ..."
    sourceServer: "dwh"
    sourceDatabase: "OdsDb"
    sourceSchema: "arc"
    sourceName: "Trips"
  - id: "Analyse_Bysykkel#analyse_bysykkel.pbix#col:Trips.StationName"
    kind: "column"
    dataType: "string"
  - id: "Analyse_Bysykkel#analyse_bysykkel.pbix#page:1#visual:1"
    kind: "visual"
    visualType: "barChart"
    title: "Trips by Station"
    sql: "SELECT ..."
edges:
  - from: "Analyse_Bysykkel#analyse_bysykkel.pbix#table:Trips"
    to: "Analyse_Bysykkel#analyse_bysykkel.pbix#col:Trips.StationName"
    kind: "hasColumn"
  - from: "Analyse_Bysykkel#analyse_bysykkel.pbix#page:1#visual:1"
    to: "Analyse_Bysykkel#analyse_bysykkel.pbix#col:Trips.StationName"
    kind: "projects"
    role: "Category"
  - id: "Analyse_Bysykkel#analyse_bysykkel.pbix#expr:Stations"
    kind: "expression"
    powerQuery: "let\n    Source = Sql.Database(\"dwh\", \"OdsDb\"),\n    ..."
```

An `expression` node is a Power Query query the model defines but does not load as a table (a lookup a loaded table merges in, for example). It is read from the model's shared expressions, carries its M redacted like a table's, and has no edges; a loaded table's M refers to it by name.

Every node carries a `kind` (`table`, `column`, `measure`, `calculatedColumn`, `expression`, `report`, `page`, `visual`) and every edge a `kind` (`hasColumn`, `definedOn`, `relationship`, `hasPage`, `hasVisual`, `projects`) plus whatever properties that kind needs, so a consumer loads the file directly into an in-memory node/edge graph with no name-matching step: starting from one visual node and walking its `projects` edges reaches exactly the columns and measures it reads, with no unrelated table pulled in. A node id is always `<subscriberName>#<reportFile>#<kind-tag>:<qualifier>`, which keeps ids globally unique across every subscriber and every report a `pbix:` directory can hold, so graphs from many subscribers can be merged without collisions. A `.pbix` connected live to a published dataset carries no semantic model (it stays on the server), so only the report-layer nodes (`report`/`page`/`visual`) and their edges appear; the two halves degrade independently.

The collector reads this specification (from the tool's standard output, or from any of the other places listed below) and both halves are stored in the catalog: the report layer (pages, visuals, projected fields, and each visual's rendered SQL) and the model layer (tables with their Power Query source, columns, calculated columns, measures, and relationships). See [What lands in the catalog](#what-lands-in-the-catalog). `reportWarnings` stays a flat list naming anything the tool declined to extract (an unsupported filter expression, a dangling projection, a table whose source could not be resolved), since a warning is a diagnostic rather than a graph-shaped fact.

### Where a report comes from

The extractor only runs where it is installed, and a `.pbix` is often too large, or too private, to commit. So a
report reaches a subscriber as a SPECIFICATION, read from whichever place holds one:

| Source | What it is | Notes |
| --- | --- | --- |
| A committed specification | `pbix:` names `Sales.pbix.yaml` (or a directory holding `*.pbix.yaml` files), made with `sqlflow powerbi extract Sales.pbix` | Read the same on every machine, extractor or not. The report's label is the file name without `.yaml`, the same one extracting `Sales.pbix` directly gives, so switching between the two keeps every stored key. It wins over a `.pbix` of the same name beside it (warned), and is never parsed as a flow |
| A `.pbix` | `pbix:` names `Sales.pbix` (or a directory holding `*.pbix` files) | Extracted by `pbix-extract` when this machine has it. The sync keeps what it extracted in the semantic layer |
| A kept extraction | The copy an earlier sync kept of a declared `.pbix` | Used only where this machine cannot extract that report: no extractor (the control plane never has one), or the file is not here (a clone of a repo that git-ignores its reports). It is refreshed by the next sync that can extract the report, and dropped once the repository stops declaring it or commits its specification instead. A missing path or an empty directory is not taken as proof the report is gone |
| An upload | A specification stored in the semantic layer for this subscriber, through the GUI or `sqlflow powerbi publish` | Needs no `pbix:` at all, only a PowerBI subscriber with a usable `server`. A report the repository declares under the same name wins over the upload (warned). It stays until someone removes it |

A committed specification is the recommended form for a report that belongs to the repository: it is reviewed like
code and the control plane reads it with no extractor. An upload suits a report that is maintained outside the
repository. Every one of these goes through the same reader, and a malformed specification is a warning naming the
report and the file, never a failed sync.

A sync recomputes the consumption side whenever what it reads changes: the text of every subscriber library and of
every report specification it used is fingerprinted, and a changed fingerprint recomputes the graph even when no
flow changed. That is what applies an edited `subscribers.yaml`, a newly committed specification, or an upload on
the next ordinary sync. An upload or deletion in the semantic layer also queues the repo's managed sync right away.

### Resolving a model table to a warehouse object

A visual names the report's MODEL entity (`Trips`), never the physical table behind it, so a consumption edge built from that visual would land on a name-only node that never unifies with the fully-qualified node an ingestion flow writes (`[OdsDb].[arc].[Trips]`). The `sourceServer`/`sourceDatabase`/`sourceSchema`/`sourceName` properties above close that gap: the tool pattern-matches each table's Power Query (M) expression, and when it names a database object, the collector turns the pairing into the same kind of synonym fact a `sys.synonyms` read produces. The estate's existing synonym-resolution pass then rewrites the bare model name onto the physical object, so a report's reads and a flow's writes meet on one node.

Only a `Sql.Database`-shaped source resolves, and only when server, database, schema, and table are all present:

```
let
    Source = Sql.Database("dwh", "OdsDb"),
    Nav = Source{[Schema="arc", Item="Trips"]}[Data]
in
    Nav
```

Intervening transform steps and formatting variation are tolerated (the navigation step is matched wherever it appears in the `let` chain), but everything else is left UNRESOLVED and reported rather than guessed at: an Excel, CSV, JSON, web, or SharePoint source; a native `[Query="..."]` source, whose objects are inside SQL text rather than a schema/item pair; `Sql.Databases` (plural, which selects its database downstream); a server argument computed by another call; and any connector the matcher does not recognize. Each of those adds a `reportWarnings` line naming the table and the shape that defeated resolution, so lineage that is incomplete says so instead of looking finished. This is deliberate: a wrongly resolved table points a report's whole consumption lineage at an object it never read, which is worse than no lineage at all because nothing looks wrong.

The server string in `sourceServer` is reported for a reader but is **not** used as an estate identity. How a connection string inside a report maps onto a declared `connections:` reference is a question only the estate's own configuration can answer, so the resolved object keeps the subscriber's own `server:` identity and takes only the database, schema, and table name from the M expression.

### Registered-source subscribers

A dashboard often reads a database SQLFlow does not load. Its library then declares **no `connections:` block at all**, and a [schema registration flow](sch.md) (`flowType: sch`) registers that database's tables and views instead:

```yaml
# subscribers/adventureworks_sales.subscribers.yaml
subscribers:
  AdventureWorks_Sales:
    type: PowerBI
    owner: analytics@example.com
    pbix: reports/AdventureWorks Sales.pbix.yaml
```

Such a subscriber's queries need no `server`, and its report is read without one. Every object its queries name, and every model table its report resolves (above), starts on a synthetic `registered` identity carrying only database, schema, and name. The lineage build matches each one to the object a schema registration flow registered with the same database, schema, and name (schema and name when the reference names no database), and re-keys it under that flow's connection, so the report's read edges and its model tables land on the registered objects. A query whose objects all resolved to one connection records that connection as its `ServerRef`.

Registrations made by any repository count, whether this build read them or an earlier sync did. Nothing is guessed: a name no registration has, or one registered in two databases, stays on the `registered` identity with a warning naming why, and a one-part name is a model entity the report itself resolves or reports.

The split is deliberate. The registration says where the data is (its connection and database); the dashboard says how it fits together (its relationships, measures, and calculated columns). A registration therefore reads no foreign keys, and the model a question is answered from is the report's.

### Source SQL per visual

A visual's `sql` is written against the report's model: it names model tables and model columns, lists its tables without join predicates, and may name DAX measures, so it cannot run on a database. The lineage build therefore translates each visual's query into one T-SQL statement over the source tables its model loads from, and stores it beside the report's own query as the query's `SourceSql`. The output is T-SQL only; no DAX survives into it.

How each part translates:

| Part | Translation |
| --- | --- |
| Model table | Its Power Query source (`Sql.Database` plus navigation) as a three-part name, with the joins its M adds |
| Model column | The source column its M maps it to, or the scalar expression an added column computes |
| Tables a visual names | `LEFT JOIN`s along the model's active relationships, rooted at a table that reaches all of them (many side to one side) |
| Plain select items | Grouped (`GROUP BY`) |
| `SUM`, `AVG`, `MIN`, `MAX`, `COUNT` | Kept, over the mapped column |
| Measure | Its DAX translated, see below |
| Filters (`WHERE`) and `ORDER BY` | Rewritten with the same mapping; `ORDER BY` names the output aliases, which keep the visual's own select names |
| Literals | Plain T-SQL (`2020`, `1.5`, `'2020-01-01T00:00:00'`, `1`/`0` for true/false) |

The Power Query steps understood are `Table.SelectColumns`, `RemoveColumns`, `RenameColumns`, `DuplicateColumn`, `ReorderColumns`, `TransformColumnTypes`, `AddColumn` (with column references, text and number literals, `&`, `+ - * /`, `Text.From` and `Number.From`), and `Table.NestedJoin` (left outer or inner) followed by `Table.ExpandTableColumn`, where the joined query is another table or an `expression` node.

The DAX subset is `SUM`, `AVERAGE`, `MIN`, `MAX`, `COUNT`, `COUNTA`, `DISTINCTCOUNT` of a column, `COUNTROWS` of the visual's root table, `DIVIDE` (a division guarded with `NULLIF`, and `COALESCE` for its alternate result), `BLANK()`, arithmetic, references to other measures, and `CALCULATE` whose only filters are `USERELATIONSHIP`. When measures need different join paths (a `USERELATIONSHIP` on an inactive relationship), each join path becomes its own CTE grouped by the visual's keys, and the CTEs are joined with a null-safe `FULL OUTER JOIN` on those keys (a `CROSS JOIN` when there are none).

Anything else is refused rather than approximated, and the query stores a `TranslationProblem` naming what stopped it instead of `SourceSql`:

- a Power Query step not listed above, or one that changes rows (`Table.SelectRows`, `Table.Distinct`, `Table.Group`, ...), which makes the whole table untranslatable because its rows are no longer the source rows;
- a source other than `Sql.Database` (`Json.Document`, a file, a web call);
- any other DAX function (filters inside `CALCULATE`, iterators, time intelligence);
- tables that no active relationship connects;
- a mapped column the source table does not have, when the build knows that table's columns (from a schema registration or a live inventory);
- a report connected live to a published dataset, which has no model to translate from.

A declared query in `subscribers.yaml` is already source SQL and is not translated. Every translation is parsed with the same guard ad-hoc queries go through before it is stored.

## What lands in the catalog

`sqlflow db sync` mirrors subscribers into `catalog.Subscriber` and their queries into `catalog.SubscriberQuery`, repo-scoped and replaced wholesale on each sync, so a subscriber deleted from the YAML stops being listed as a consumer. `FirstSeenUtc` survives the replacement, so the catalog can still say how long a report has been reading the warehouse. Query text is redacted on the same path module bodies take, since authored SQL can embed a literal credential. A report visual's query also stores its `SourceSql` or its `TranslationProblem` (see [Source SQL per visual](#source-sql-per-visual)), redacted the same way.

The consumption itself is not stored twice: the read edges are ordinary `catalog.LineageEdge` rows, so "what consumes table X" is the same edge query as "what writes table X".

A subscriber whose `pbix:` report was extracted also stores the report's structure (`catalog.SubscriberReportPage`, `SubscriberReportVisual`, `SubscriberReportField`) and its semantic model, one per report file:

| Table | One row per |
| --- | --- |
| `catalog.SubscriberModelTable` | Model table: its name, its Power Query (M) expression, and the warehouse database, schema, and table that expression resolved to (null when it did not resolve), with that object's node key (`ObjectKey`) |
| `catalog.SubscriberModelField` | Column (with its data type), calculated column, or measure (with its DAX expression and description) on a model table |
| `catalog.SubscriberModelRelationship` | Relationship between two model tables: their columns, the cardinality, and whether it is active |

They are keyed by subscriber, report file, and table name, and replaced wholesale with the rest of the repo's subscriber rows. Power Query and DAX text is redacted on the same path query text takes. DAX is stored verbatim and never evaluated. Because every sync rebuilds them from the specifications described above, including the semantic layer's uploads and kept extractions, a sync on a machine without `pbix-extract` no longer drops a model an earlier sync extracted.

The specifications themselves live in `catalog.SemanticReportSpec`, one row per subscriber, report, and origin (`upload` or `extracted`), in the canonical, credential-redacted form the semantic layer stores. They are not part of the rows a sync replaces wholesale. `catalog.Repo.SubscriberInputHash` records the fingerprint the stored graph was computed from.

A model table's `ObjectKey` is resolved at sync through the same identity resolution lineage edges take, so it is the key of the catalog object the table loads from. The model is served from there, by the semantic layer: `describe_semantic_table` lists a table's Power BI measures, calculated columns, and relationships as `reportModels`, keeping only those whose every column is on the column allow-list (see [Semantic layer](../concepts/semantic-layer.md)).

## Business questions per visual

When question generation is on, a control-plane-only step runs after each sync that touches subscriber report rows: it turns every extracted visual's title, chart type, and projected fields into 1-3 natural-language business questions the visual answers (`catalog.SubscriberReportVisualQuestion`), the text-to-query training material POWERAI.md's learning loop needs. This is deliberately not part of `tools/pbix-extract` or `CatalogSync`: the tool stays a pure parser with no network access, and the sync itself stays shared code the bare CLI also runs with no LLM wiring at all.

Whether it is on is decided once per sync. `ControlPlane:PowerAI:QuestionGeneration:Enabled` is the deployment's default, and an admin can override it at runtime with the switch on the semantic layer's Power BI reports tab (`PUT /api/v1/powerai/semantic-layer/reports/question-generation`), stored on the layer's settings row. Neither can turn it on without `ControlPlane:Assistant:Anthropic:ApiKey`, whose presence is what registers the generator.

Regeneration is incremental. Every sync deletes and reinserts a repo's subscriber report rows wholesale but leaves the question rows, which are keyed by the visual's positional key, so a visual's `ContentHash` (a SHA-256 of its title, chart type, and fields), snapshotted before the sync, is what tells "the same visual as before" from "new or changed". An unchanged visual with questions keeps them with no LLM call; a new or changed visual, or an unchanged one with no questions yet (synced while generation was off, or whose generation failed), is sent to the model. A visual whose generation fails keeps whatever questions it had rather than blocking the rest of the sync.

Questions are also curated by hand. On the semantic layer's Power BI reports tab, opening a report lists each visual's questions: a person can add one, edit any (a generated question becomes theirs), or delete any (`POST`, `PUT`, `DELETE /api/v1/powerai/semantic-layer/reports/questions`). Each row records its `Origin`: `generated` or `manual`. Generation only ever replaces `generated` questions, so a person's stay as written; a generated question the model repeats in a person's words is skipped. Deleting every question on a visual lets the next sync generate fresh ones while generation is on.

After every control-plane sync, whether or not generation is on, the questions of a visual the sync no longer wrote are reconciled. A visual that only moved (its old content hash now belongs to exactly one visual under a new key that has no questions yet) takes its questions along, a person's included, and is not regenerated. Every other question whose visual is gone (a removed report, page, or visual, or an ambiguous move between identical visuals) is deleted, so question search never serves a question for a visual that no longer exists. The bare CLI's `sqlflow db sync` runs none of this; the next control-plane sync of the repo catches up.

The feature is off by default and independent of `ControlPlane:Assistant:Enabled` (the interactive chat assistant): a deployment may run either without the other, though it reuses `ControlPlane:Assistant:Anthropic`'s API key and model rather than declaring its own.

## API

| Endpoint | Answers |
| --- | --- |
| `GET /lineage/subscribers` | What consumes the warehouse. Filter by `type` (the tool) or `search` (name, owner, description). Each row carries how many queries it runs and how many distinct objects those queries read. |
| `GET /lineage/subscribers/dossier?key=<node key>` | What one subscriber consumes: its queries, and every object they read, named and located from the object registry, with the queries that reference each one. |
| `GET /lineage/subscribers/report?key=<node key>` | The Power BI report structure behind one subscriber: every page, the visuals on it, and each field's role, plus each visual's `questions` (the business questions it answers, generated or written by a person; empty when neither has happened), `questionEntries` (the same questions with each one's `id`, `origin`, and who last edited it), and `visualKey`. The report's semantic model is not served here: the semantic layer serves it on the warehouse table each model table loads from (`describe_semantic_table`'s `reportModels`). |
| `GET /lineage/objects/dossier?key=<node key>` | Now also returns `subscribers`: who consumes THIS object, with the specific queries that name it. |
| `GET /search/subscribers`, and the `subscribers` category of `GET /search/all` | Subscribers as a surface of the GLOBAL search, matched on name, type, owner, description, notes, location, or declaring file. It is the LAST category, deliberately: the warehouse is the subject and consumption is a convention on top of it, so a bare term is far more often a table or a column than the name of a report. A subscriber is neither a database object nor a flow, so without this a report searched for by name returned nothing and looked absent rather than unsearched. |

## Editor support

A subscriber library gets the same editor treatment as a flow document: hover documentation on every key, key completion, and unknown-key diagnostics, in both the workbench editor and the VSCode extension. The engine detects a library by its root `subscribers:` key rather than by file name, since it analyses buffers whose name it may not know, and a document carrying a `flowType` always stays a flow.

The key model is `docs/reference/flow/keys.subscribers.json`, embedded into the analysis engine at compile time exactly like the per-flow-type censuses. The `connections:` block is not repeated there: it is merged in from `keys.shared.json`, so the block a library declares is documented in one place. The other shared blocks (the invoke hooks, the service principals) are deliberately excluded, because they belong to a pipeline and a library declares none.
