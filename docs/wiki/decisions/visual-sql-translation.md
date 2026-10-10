---
id: wiki-visual-sql-translation
title: "A report visual's query is translated into T-SQL at sync, or refused with a reason"
type: decision
summary: "Why each Power BI visual query is stored as T-SQL over its source tables, why only a stated M and DAX subset translates, and why matches serve it."
keywords:
  - visual sql translation
  - source sql
  - translation problem
  - dax to t-sql
  - power query lineage
  - dashboard match
  - userelationship
  - shared expressions
sourceRefs:
  - src/SqlFlow.Lineage/PowerBi/VisualSqlTranslator.cs
  - src/SqlFlow.Lineage/PowerBi/DaxTranslator.cs
  - src/SqlFlow.Lineage/PowerBi/Relational.cs
  - src/SqlFlow.Lineage/PowerQuery/MSyntax.cs
  - src/SqlFlow.Lineage/PowerQuery/PowerQueryLineage.cs
  - src/SqlFlow.Lineage/Graph/LineageGraphBuilder.cs
  - src/SqlFlow.Catalog/CatalogSync.cs
  - src/SqlFlow.ControlPlane/Background/QuestionSearch.cs
  - tools/pbix-extract/src/metadata.c
  - tools/pbix-extract/src/sqlrender.c
  - tests/SqlFlow.Core.Tests/PowerBi/VisualSqlTranslatorTests.cs
referenceRefs:
  - flow-subscribers
  - guide-chat-assistant
related:
  - wiki-schema-registration-flows
  - wiki-powerbi-report-specifications
  - wiki-powerbi-model-entity-resolution
updated: 2026-09-16
---

# A report visual's query is translated into T-SQL at sync, or refused with a reason

Every Power BI visual's query is translated into one T-SQL statement over the tables its model loads from, stored
beside the report's own query, and served as the runnable SQL of a dashboard match. What translates and what is
refused is listed in [subscribers.yaml, Source SQL per visual](../../reference/flow/subscribers.md); the match
fields are in the [chat assistant guide](../../reference/guides/chat-assistant.md). This page records why it has
that shape.

## The gap it closed

[Schema registration](schema-registration-flows.md) told a dashboard question where its tables live, and left
open the other half: the visual's SQL that `find_similar_questions` returned could not run. `pbix-extract` renders
a visual as `SELECT ... FROM [Product] AS [Product], [Sales] AS [Sales]`, which names model tables and renamed
columns, lists tables with no join predicate, aggregates without a `GROUP BY`, can name DAX measures, and wrote
literals in Power BI's typed forms (`2020L`, `datetime'...'`). A match was a question with nothing to run.

## T-SQL is the only output

Two targets were possible: DAX, which Power BI itself would evaluate against the model, or T-SQL against the
source. The human chose T-SQL only, with no DAX in the final form. Every other query path (prepare, run, the
column allow-list, `ReadOnlyQueryGuard`, datasource inference) already speaks T-SQL against catalog objects, so a
translated visual goes through them unchanged, and nothing needs a live Power BI dataset to answer.

## A stated subset, not best effort

The human chose to translate a common subset of DAX. The same rule was applied to Power Query: a step or function
outside the subset refuses the whole visual and records the reason, rather than emitting SQL that runs but
answers a different question. A plausible-looking wrong number is worse for a business reader than no number,
because the assistant would present it as the dashboard's answer.

Row-changing M steps (`Table.SelectRows`, `Table.Distinct`, `Table.Group`) refuse the whole table for the same
reason: after them, the table's rows are no longer the source rows, so every column mapping would be right and
every aggregate wrong. Column-only steps keep the rows and translate. A left join that the query never expands
adds no columns and cannot change the row count, so it is dropped; an unexpanded inner join can remove rows, so
it is refused.

`CALCULATE` translates only with `USERELATIONSHIP` filters. That form is common (the sample's due-date measure)
and changes only which relationship joins the tables, so it maps onto a separate join context. Each context is a
CTE grouped by the visual's keys, and the CTEs are joined null-safely with `FULL OUTER JOIN`, so a key present in
one context and not another still appears. Other filter arguments would need Power BI's filter-context
semantics, which the subset does not claim.

## Merged queries needed the model's shared expressions

The sample's `Product` table gets its category by merging queries (`DimProductSubcategory`,
`DimProductCategory`) that the model defines but does not load. `pbix-extract` did not read those, so the
extractor now emits each one as an `expression` node, read from the model database's `Expression` table and
redacted like a table's M. The C renderer was changed at the same time to write plain T-SQL literals, since
the translation parses the visual SQL with ScriptDom, and a typed literal would not parse.

The sample report's own `DimProductSubcategory` merge joins `ProductSubcategoryKey` to `ProductCategoryKey`. The
translation reproduces that join exactly as written, because it translates the report and does not correct it.

## At sync, in the graph builder

The translation runs where subscriber queries are projected in `LineageGraphBuilder`. That is the one place
that holds the report's model, the object each model table resolved to (including a registered one), and each
object's known columns, so a mapped column that the source table lacks is refused at build time rather than at
query time. Translating at question time would have repeated that resolution on every search and still needed
the same inputs. The result is stored on `catalog.SubscriberQuery` (`SourceSql`, `TranslationProblem`) through
the same redaction path as the query text.

Only visual queries are translated. A query declared in `subscribers.yaml` is already source SQL.

## Serving: the translation is the match's SQL

The human chose to serve the translation as the match's `sql`, keeping the model query as `reportSql` and the
reason as `translationProblem`. Callers already run a match's `sql`, so nothing downstream had to learn a new
field in order to run a dashboard match; a caller that wants the report's own text still has it. A match with a
`translationProblem` has an empty `sql`, so it cannot be run by accident and remains a lead for composing from
the semantic layer.

## Found on the way

`QuestionSearch` looked up a visual's query by name alone, so two reports with a same-named visual could serve
each other's SQL. The lookup is now keyed by repository, subscriber, and name, and visuals are joined to their
pages on repository as well as page key.

## What it leaves open

- The subset rests on one sample report. Real reports will reach refusals (filters inside `CALCULATE`, time
  intelligence, iterators) that only more reports can prioritize.
- A table whose columns the build does not know (not registered, no live inventory) is taken as its model
  states it, so a wrong mapping there surfaces only when the query runs.
