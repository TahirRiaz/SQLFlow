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
yamlPath: subscribers
related:
  - concept-lineage-graph-and-plan
  - concept-lineage-tiers
  - flow-schedule
  - flow-overview
sourceRefs:
  - src/SqlFlow.Core/Subscribers/DataSubscriber.cs
  - docs/reference/flow/keys.subscribers.json
  - src/SqlFlow.Yaml/YamlSubscriberLibraryLoader.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Collection/LineageFacts.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Core/Lineage/LineageReport.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
---

# subscribers.yaml

Lineage without the consumption side stops at the last table SQLFlow writes. `subscribers.yaml` continues it: it declares the reports, workbooks, notebooks, and applications that READ the warehouse, and the queries each one runs. Those queries are parsed, so the objects they touch become real lineage edges on the same nodes the loading flows write. The estate can then answer, from the graph rather than from memory, which dashboards break if a table changes.

This is the V3 form of the legacy `flw.DataSubscriber` and `flw.DataSubscriberQuery` tables. The legacy pair carried `FlowID`, `FlowType: 'sub'`, and a `Batch`, purely so the old catalog could log a row per subscriber; a subscriber runs nothing, so V3 drops that plumbing and identifies a subscriber by name like every other object.

```yaml
# subscribers.yaml
connections:
  dwh: ${env:SQLFLOW_CONN_DWH}

subscribers:
  Analyse_Bysykkel:
    type: PowerBI
    owner: analyse@kolumbus.no
    description: City bike usage and station occupancy
    notes: |
      Not refreshed since April 2024; owner asked whether it is superseded by Analyse_Bysykkel_statistikk.
    url: https://app.powerbi.com/groups/me/reports/abc123
    server: dwh
    queries:
      - name: Turer
        sql: |
          SELECT t.TripId, t.StartedAt, s.StationName
          FROM   arc.Bysykkel_Trips AS t
          JOIN   arc.Bysykkel_Stations AS s ON s.StationId = t.StartStationId
      - name: Stasjoner
        sql: |
          SELECT * FROM pre.v_Bysykkel_Stations
```

## Where the file lives

A subscriber library is any file named `subscribers.yaml` or ending in `.subscribers.yaml`, anywhere under the scanned folder, and every match is merged into one set.

**One file per subscriber is the convention**, named for the subscriber and gathered in a `subscribers/` folder:

```
subscribers/analyse_sanntid.subscribers.yaml
subscribers/dashboard_mpc.subscribers.yaml
subscribers/baatbooking_report.subscribers.yaml
```

A consumer is an independently owned thing: it is added, retired, and re-pointed on its own schedule, by whoever owns the report rather than by whoever owns the estate. One file per subscriber keeps that ownership legible in the diff, and keeps a change to one report out of everyone else's blame. Each file is parsed on its own, so each declares its own `connections:` block. A single file holding several subscribers still works and is the right shape for a handful of them.

A subscriber name is the estate's identity for a consumer, so the same name in two files is a collision, not a merge: the first wins and the second is reported.

Like `schedules.yaml`, these files are NOT flow documents: they are excluded from the flow parse, never become pipelines, never join a schedule, and never appear in an execution wave. A subscriber that leaked into the flow set would sit in a wave forever waiting to run a Power BI report.

## Keys

| Key | Required | Meaning |
| --- | --- | --- |
| `connections` | yes, when any query names a server | The same `connections:` block every flow document uses: alias to a SQL Server reference. A bare alias resolves `${env:SQLFLOW_CONN_<NAME>}` by the canonical convention, so the file can stay reference-free. |
| `subscribers` | yes | Maps a subscriber's NAME to its declaration. The name is its identity across the estate and the label on its graph node. |
| `subscribers.<name>.type` | no (warns) | What consumes the data (legacy `SubscriberType`): `PowerBI`, `Tableau`, `Excel`, `Notebook`, `Application`, or any label the estate uses. Free text, as the legacy column was. Omitted records `Unknown` with a warning. |
| `subscribers.<name>.owner` | no | Who to contact before a breaking change to a table it reads (legacy `CreatedBy`). |
| `subscribers.<name>.description` | no | What the subscriber is FOR, in one line, for the catalog and the node's tooltip. |
| `subscribers.<name>.notes` | no | Remarks about the subscriber's STATE rather than its purpose. Free text, multi-line via a block scalar. See [Notes](#notes). |
| `subscribers.<name>.url` | no | Where the subscriber LIVES (as opposed to what it reads): the report URL, the workbook path, the share, the repository. Searchable. See [Location](#location). |
| `subscribers.<name>.server` | no | The default connection alias for every query that does not name its own. |
| `subscribers.<name>.queries` | yes, in practice | The queries the subscriber runs. A subscriber with none is a node nothing connects to, which is warned. |
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

Because the identities go through the same completion (default database from the connection, identity unification, synonym follow) as every other fact, a report reading `arc.Bysykkel_Trips` lands on the SAME node the ingestion flow writes. A report reading a view lands on the same view node the flows read, and the view's own module edges continue the chain down to its base tables.

The subscriber itself becomes an object node of kind `Subscriber` on the synthetic server identity `subscriber`. It is the only node kind that lives outside the databases SQLFlow moves data between, and the only one no database inventory can supply.

Joins written inside a subscriber query also feed the interpreted data model (`LineageReport.Relationships`). The joins an analyst writes in a report are evidence of how the business actually relates these tables, and carry the same weight as a warehouse view's.

## What lands in the catalog

`sqlflow db sync` mirrors subscribers into `catalog.Subscriber` and their queries into `catalog.SubscriberQuery`, repo-scoped and replaced wholesale on each sync, so a subscriber deleted from the YAML stops being listed as a consumer. `FirstSeenUtc` survives the replacement, so the catalog can still say how long a report has been reading the warehouse. Query text is redacted on the same path module bodies take, since authored SQL can embed a literal credential.

The consumption itself is not stored twice: the read edges are ordinary `catalog.LineageEdge` rows, so "what consumes table X" is the same edge query as "what writes table X".

## API

| Endpoint | Answers |
| --- | --- |
| `GET /lineage/subscribers` | What consumes the warehouse. Filter by `type` (the tool) or `search` (name, owner, description). Each row carries how many queries it runs and how many distinct objects those queries read. |
| `GET /lineage/subscribers/dossier?key=<node key>` | What one subscriber consumes: its queries, and every object they read, named and located from the object registry, with the queries that reference each one. |
| `GET /lineage/objects/dossier?key=<node key>` | Now also returns `subscribers`: who consumes THIS object, with the specific queries that name it. |
| `GET /search/subscribers`, and the `subscribers` category of `GET /search/all` | Subscribers as a surface of the GLOBAL search, matched on name, type, owner, description, notes, location, or declaring file. It is the LAST category, deliberately: the warehouse is the subject and consumption is a convention on top of it, so a bare term is far more often a table or a column than the name of a report. A subscriber is neither a database object nor a flow, so without this a report searched for by name returned nothing and looked absent rather than unsearched. |

## Editor support

A subscriber library gets the same editor treatment as a flow document: hover documentation on every key, key completion, and unknown-key diagnostics, in both the workbench editor and the VSCode extension. The engine detects a library by its root `subscribers:` key rather than by file name, since it analyses buffers whose name it may not know, and a document carrying a `flowType` always stays a flow.

The key model is `docs/reference/flow/keys.subscribers.json`, embedded into the analysis engine at compile time exactly like the per-flow-type censuses. The `connections:` block is not repeated there: it is merged in from `keys.shared.json`, so the block a library declares is documented in one place. The other shared blocks (the invoke hooks, the service principals) are deliberately excluded, because they belong to a pipeline and a library declares none.
