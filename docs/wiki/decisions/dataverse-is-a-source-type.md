---
id: wiki-dataverse-is-a-source-type
title: "A Dataverse table is a source type of the file flow, not a flow type of its own"
type: decision
summary: "Why Power Apps data is read by a source reader, not a new flowType; why it lands typed; why deletes need a full reload; why readers get the watermark order."
keywords:
  - dataverse
  - power apps
  - source type
  - flow type
  - single code path
  - typed landing
  - watermark order
  - change tracking
  - deleted rows
sourceRefs:
  - src/SqlFlow.Dataverse/DataverseSourceReader.cs
  - src/SqlFlow.Dataverse/DataverseReadPlan.cs
  - src/SqlFlow.Core/Dataverse/DataverseSourceOptions.cs
  - src/SqlFlow.Core/Abstractions/ISourceReader.cs
  - src/SqlFlow.Core/Engine/FlowRunner.cs
  - src/SqlFlow.SqlServer/SqlBulkLoader.cs
  - src/SqlFlow.Acquire/Engine/HttpTransport.cs
  - tests/SqlFlow.Core.Tests/Integration/DataverseIngestionIntegrationTests.cs
referenceRefs:
  - source-type-dataverse
  - flow-source
  - flow-incremental
  - flow-api
related:
  - wiki-string-first-landing
updated: 2026-11-11
---

# A Dataverse table is a source type of the file flow, not a flow type of its own

Power Apps keeps its data in Microsoft Dataverse. SQLFlow reads a Dataverse table with `source.type: dataverse` under the
file flow: one new `ISourceReader`, and everything after the read is the file flow's. What the source does is in
[the reference page](../../reference/flow/source-types/dataverse.md); this page records why it has that shape.

## What was asked, and what already existed

The request was a native way to fetch Power Apps data into a target table, with dynamic schema evolution, and watermark
based incremental loading as a plus. Before it, the only route was the `api` flow landing raw JSON for a `json` file flow
to read. That route could not page a Dataverse table at all: the Web API puts the next page's absolute URL in the response
body (`@odata.nextLink`), and the `api` flow's paging strategies follow a `Link` header or send a body cursor as a query
parameter (src/SqlFlow.Acquire/Engine/HttpTransport.cs), so a table past one page stopped after its first.

Everything else the request named already existed behind one interface. `ISourceReader` is documented as the single
extension point for ingesting any source, and the file flow's runner around it plans `create`, `widen` or `strict`
schema evolution, probes the target for a row watermark and hands the bound to the reader, applies the load mode,
refreshes the typed view, resets a consolidated landing table, and is what lineage, the catalog and the scheduler know.

## Why a source type and not a flow type

The request said "a new flow type". A `flowType: dataverse` would have needed its own loader, model, executor dispatch,
catalog projection, census and documentation, and would then have had to run the file flow's load underneath to get
schema evolution and the watermark: two paths into the same load, which the single code path principle rules out. The
human was shown both shapes side by side and chose the source type. The cost of the choice is vocabulary: a Dataverse
read is a "file flow" by the document kind's name, as a DuckDB query already was.

Rejected besides the flow type:

- **The `api` flow plus a JSON file flow, with a new paging strategy.** Two flows per table, the payload landed raw and
  re-read, and every column a string, so the target's types and their evolution would be guessed from text rather than
  read from the table.
- **Dataverse's SQL (TDS) endpoint through an `ing` flow.** It must be enabled per environment, is read-only with its own
  limits, and whether the queries an `ing` flow runs work against it was not verified.
- **Exporting through Synapse Link or Fabric Link and reading the lake.** It needs Azure infrastructure outside SQLFlow
  for every environment, which is a deployment decision rather than a reader.

## Why it lands typed

[Landing is string-first](string-first-landing.md) so that a load cannot fail on one malformed cell, so that a wrong type
guess is fixed without re-fetching, and so that SQL Server profiles the real text. None of that applies to a Dataverse
table, for the reason it does not apply to Parquet: the types are the store's own and are enforced by it. A whole number
column cannot hold a word, a lookup holds a GUID, and the metadata says which is which, so there is nothing to infer and
no vendor format that can drift. Reading the metadata on every run is also what makes evolution meaningful: a column
added in Power Apps arrives with its real type, and a text column made longer widens rather than staying at a guessed
length. A value that still fails to convert is a service fault, and the run fails naming the row and column rather than
coercing it to NULL, which the string-first page rejects for the same reason.

Decimal and currency columns get one fixed type each instead of their declared precision, so a precision change made in
Power Apps never asks the target for a type change the reconciler would refuse to make.

## Why deletes are left to a full reload

A watermark read never sees a deleted row. Dataverse change tracking would: its delta links report deleted ids. It was
offered and the human chose a full reload instead. Change tracking must be enabled per table, the service refuses
`$filter` and `$orderby` on a change-tracking request, and its delta token has to be kept between runs (seven days by
default before it lapses), which is state the file flow does not have: its watermark lives in the target table. A full
reload (`truncateLoad`, or an occasional `--full` run) reconciles deletes with no new state.

## Why the engine now names the watermark order on every run

The bulk load commits batch by batch with no transaction around the whole load (src/SqlFlow.SqlServer/SqlBulkLoader.cs),
so a run that fails part way keeps what it committed, and the next run starts after the target's `MAX`. That resumes
correctly only if the committed rows were the lowest watermarks. A reader that pages in watermark order guarantees it, but
the engine injected the watermark column only together with a bound, and the run that most needs the order, the first
one into an empty target, has no bound. A first run failing after two pages would have committed whichever rows came
first by primary key, and every lower row not yet loaded would have fallen behind the next bound for good.

So the engine now injects `incrementalOrderColumn` on every run that declares a watermark (`AnnounceWatermarkOrder` in
src/SqlFlow.Core/Engine/FlowRunner.cs), and the Dataverse reader orders by it. Readers that cannot choose their order
ignore it. The integration test that fails the third page of a first run, then resumes, proves the rows land exactly once
(tests/SqlFlow.Core.Tests/Integration/DataverseIngestionIntegrationTests.cs).

## What Microsoft's documentation does and does not settle

The paging rules (5,000 rows at most, use `@odata.nextLink` unchanged, no `$skip`), the lookup property form
`_<name>_value`, the `Retry-After` on a 429, the token scope `<environment URL>/.default`, the 32 KB URL limit, and the
advice to classify columns by `AttributeTypeName` are documented on Microsoft Learn and the reader follows them. Not
documented: filtering or ordering by `versionnumber`, the precision `modifiedon` keeps, and how many fractional seconds a
date literal may carry. The reader writes whole-second literals, which are valid either way, and a service that refused
`versionnumber` would fail the run with its own message, with `modifiedon` as the documented alternative.
