---
id: wiki-schema-registration-flows
title: "An external database is registered by a flow, and the dashboard supplies the model"
type: decision
summary: "Why flowType sch registers only an external database's tables and views, links dashboards to them by name, and leaves joins and measures to the dashboard."
keywords:
  - schema registration
  - flowType sch
  - registered source
  - registers relation
  - datasource gate
  - external database
  - chat with data
  - subscriber is the key
sourceRefs:
  - src/SqlFlow.Yaml/YamlSchemaRegistrationFlowLoader.cs
  - src/SqlFlow.Core/SchemaRegistration/SchemaRegistrationFlow.cs
  - src/SqlFlow.SqlServer/Catalog/SqlServerObjectHarvester.cs
  - src/SqlFlow.SqlServer/Catalog/SchemaRegistrationReader.cs
  - src/SqlFlow.Lineage/Collection/SchemaRegistrationCollector.cs
  - src/SqlFlow.Lineage/Collection/CatalogCollector.cs
  - src/SqlFlow.Lineage/Collection/FlowSetCollector.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Api/DatasourceInference.cs
  - samples/powerai-adventureworks/TESTING.md
referenceRefs:
  - flow-sch
  - flow-subscribers
  - concept-data-operations
  - concept-lineage-graph-and-plan
related:
  - wiki-powerbi-model-entity-resolution
  - wiki-semantic-layer-is-the-allow-list
  - wiki-powerbi-report-specifications
  - wiki-visual-sql-translation
updated: 2026-09-16
---

# An external database is registered by a flow, and the dashboard supplies the model

A `flowType: sch` document registers one SQL Server database's tables and views in the catalog, and a subscriber
library with no connections is linked to those objects by database, schema, and name. What the flow reads and how
the linking works are in [Schema registration flow](../../reference/flow/sch.md) and
[subscribers.yaml](../../reference/flow/subscribers.md). This page records why it has that shape.

## The gap it closed

PowerAI could find a stored question about the AdventureWorks sample report but could not run it. A query may
only run against a connection an active pipeline declares, and the report read a database no flow touched: the
only thing declaring that connection was the subscriber. Auto-run answered 409, prepare 422, and naming the
connection explicitly answered 404. The same held for any dashboard over a database SQLFlow does not load.

## A flow type of its own

Two ways out were put to the human: count connections that subscribers declare, or require a pipeline. The
answer was both, in a specific form. Subscriber connections should count, but through a process that sets the
database up as its own flow type, concerned with reading schema, whose connection is then the one queries use.

The existing kinds did not fit:

- **`scm`** reads a database, but to script its DDL into git. It requires a repository and commits on every
  run, and its pipeline is deliberately invisible to lineage.
- **`inv`** triggers Azure Data Factory pipelines and Automation runbooks; it reads nothing.

A pipeline row was the right carrier because the datasource gate already trusts exactly that: a reference an
active pipeline declares as its source. A `sch` pipeline's source server is its connection, so it passes every
gate without the gate learning anything new. The gates were still folded into one check
(`DatasourceInference.IsDeclaredAsync`) while this was built, because three copies had drifted on whether an
inactive pipeline counts.

## Not the semantic layer

An early option described registering the tables "into the semantic layer". The human rejected that: the
tables of an external database are not stored in the semantic layer. They go into the shadow catalog like any
object a flow manages (`catalog.Object`, `catalog.ObjectColumn`), and the semantic layer stays what
[the allow-list decision](semantic-layer-is-the-allow-list.md) made it, so an admin still allows the columns a
question may read.

## Only simple objects: the dashboard is the key

The first design also harvested foreign keys, `MS_Description` properties, and row counts, for richer metadata.
The human cut that back: fetch only simple objects. Joins and computed values are provided by the dashboard,
which is the key.

So a registration reads tables and views with their columns, primary keys, and scripts, and nothing else: no
foreign keys, no procedures or functions, no synonyms, and no parsing of view bodies (which would have produced
module lineage and inferred joins). A report's relationships, DAX measures, and calculated columns are already
extracted and served on the tables their model loads from, so a question is composed from the registered tables
plus the model the dashboard carries. A registration with its own join inference would have been a second,
competing model of the same data.

The primary key stayed because it is the row identity of a table, not a join. The table and view scripts stayed
because the human asked that registered objects be recorded "just as is if this was a managed object".

## Linking by name, and how that was chosen

Three ways to connect a subscriber's reads to registered objects were weighed:

- **By database metadata** (chosen). The report already names database, schema, and table: a Power BI model
  table's Power Query source, or the three-part names in a hand-written query. Matching those to registered
  objects needs nothing from the subscriber at all.
- **The subscriber names its registration flow.** Explicit, but the human's framing was that a regular
  subscriber and an external database registered for querying are different things, not one configured through
  the other.
- **The same connection reference string on both sides.** The lineage graph only unifies identities that share
  a reference, so this would have worked with no new code, but it ties a report to how a registration spells its
  connection.

A registered-source subscriber's reads therefore start on a synthetic `registered` server identity, and the graph
builder re-keys each one onto the single registered object with the same parts. Nothing is guessed: a name no
registration has, or one registered in two databases, stays unlinked with a warning. A regular subscriber (one
that declares connections) is untouched.

Registrations made by other repositories count too, because catalog objects are global and a dashboard's repo
need not be the registration's. The build reads them from the catalog's `Registers` edges before it runs.

## One reader, one write

The table inventory, column read, primary keys, and `CREATE TABLE` reconstruction already lived in
`CatalogCollector`, the connected lineage tier. They moved into `SqlServerObjectHarvester` in
`SqlFlow.SqlServer`, because the executor that runs a `sch` flow cannot reference the lineage project. The
connected tier and the registration both read through it, so a table is described the same way whichever of them
recorded it. For the same reason the object and column upsert moved out of the lineage write into one
`UpsertObjectsAsync` that a registration run also uses. That extraction also stopped a read with an unknown kind
from overwriting a table's recorded kind with `Unknown`.

A run refreshes the catalog immediately, as the human chose over waiting for a repo sync. Its objects travel in
`run.json`, which every write-back path already reads. The registration's server identity is not in the run
artifact at all: the executor cannot compute it, and the pipeline row already holds it as its source server.

## Found on the way

`ApplyServerAliases`, which rewrites a collection when two connection references prove to be one server, rebuilt
the collection without its subscribers or degraded servers. Any connected sync where two references aliased
therefore dropped every subscriber node and lost the record of which servers had failed. The new registration
lists had to be carried through that function, and the missing fields were carried with them.

## What it leaves open

- A visual's stored SQL names model entities and renamed columns, so it cannot run against the source as
  written. The registration says where the data is; turning a visual's query into source SQL is recorded in
  [A report visual's query is translated into T-SQL at sync](visual-sql-translation.md).
- A dropped table keeps its `catalog.Object` row; it only loses its `Registers` edge, which is enough to stop it
  resolving or running.
- Registration reads SQL Server only.
