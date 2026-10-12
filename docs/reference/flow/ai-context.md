---
id: flow-ai-context
title: "ai-context.yaml: what the AI assistant may use"
type: flow-reference
summary: A file naming the tables an assistant may use, every column open but those it denies, with meanings, measures, and examples; an admin approves each version.
keywords:
  - ai context
  - ai-context.yaml
  - aiContext
  - semantic layer
  - deny
  - denied columns
  - column allow-list
  - approve
  - approval
  - revoke
  - context version
  - business question
  - instructions
  - measures
  - joins
  - example questions
  - sqlflow ai-context
  - earlier gui content
yamlPath: aiContext
related:
  - concept-semantic-layer
  - concept-data-operations
  - flow-subscribers
  - guide-chat-assistant
sourceRefs:
  - src/SqlFlow.Core/AiContext/AiContextDocument.cs
  - src/SqlFlow.Yaml/YamlAiContextLoader.cs
  - src/SqlFlow.Yaml/AiContextYamlWriter.cs
  - src/SqlFlow.Catalog/AiContextVersion.cs
  - src/SqlFlow.Catalog/AiContextMaterializer.cs
  - src/SqlFlow.ControlPlane/Api/AiContextSync.cs
  - src/SqlFlow.ControlPlane/Api/AiContextEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncFollowUp.cs
  - src/SqlFlow.Cli/Remote/RemoteVerbs.AiContext.cs
  - docs/reference/flow/keys.ai-context.json
  - tools/sqlflow-lang/src/document.rs
---

# ai-context.yaml

An AI context says which warehouse tables an AI assistant may answer business questions from (a chat message that
starts with `!`), and what they mean. It is a file in a repository like every other definition, so it is reviewed,
versioned, and diffed in source control. A file only **proposes**: nothing in it reaches an assistant until an admin
approves that exact version. Everything is off by default: a table no approved context names does not exist for the
assistant, and neither does a column a context denies.

```yaml
# sales.ai-context.yaml
aiContext: sales
description: Orders, customers, and products, for questions about revenue and volume.
instructions: |
  Amounts are in the reporting currency, excluding tax.
  An order counts as sold once its Status is 'Shipped' or 'Invoiced'.
tables:
  - table: dw.sales.Orders
    name: Order lines
    description: One row per order line. Lines arrive within an hour of the order.
    synonyms: [sales, order lines]
    key: [OrderId, LineNumber]
    columns:
      - column: Amount
        description: Line amount excluding tax.
        synonyms: [revenue, turnover]
    deny: [CustomerEmail, CustomerPhone]
    measures:
      - name: net_revenue
        expression: SUM(Amount) - SUM(RefundAmount)
        description: Revenue after refunds.
    joins:
      - to: dw.sales.Customers
        columns: [CustomerId]
        type: left
        description: Every order line to the customer who placed it.
  - table: dw.sales.Customers
    deny: [Email, Phone, BirthDate]
examples:
  - question: What was net revenue by region last month?
    sql: |
      SELECT c.Region, SUM(o.Amount) - SUM(o.RefundAmount) AS net_revenue
      FROM sales.Orders AS o
      JOIN sales.Customers AS c ON c.CustomerId = o.CustomerId
      WHERE o.OrderDate >= DATEADD(month, DATEDIFF(month, 0, GETDATE()) - 1, 0)
        AND o.OrderDate < DATEADD(month, DATEDIFF(month, 0, GETDATE()), 0)
      GROUP BY c.Region
```

## Where the file lives

An AI context file is any file named `ai-context.yaml` or ending in `.ai-context.yaml`, anywhere in a synced
repository (`.git` excluded). The root `aiContext:` key marks it and names the context. It is NOT a flow document:
it is excluded from the flow parse, never becomes a pipeline, and never runs. Several files make several contexts, so
each area of the warehouse can have its own owner and its own approval; one file per context is the convention.

The name is the context's identity across the estate, and the thing an admin approves. Moving or renaming the file
keeps the approval; renaming the context makes a new one that waits for approval (the old one goes with its file). If
two files of one repository name the same context, the first in path order defines it and the other is reported and
ignored. A name another repository already defines is reported and ignored while that one exists.

The reader is strict: an unknown key, a wrong type, a value over its limit, or a repeated entry is a problem, with the
file and line, that keeps the version from being approved. It is never silently ignored. A file over 4 MB is skipped
with a warning.

## Keys

| Key | Required | Meaning |
| --- | --- | --- |
| `aiContext` | yes | The context's name: lowercase letters, digits, `.`, `_` and `-`, starting with a letter or digit, at most 100 characters. |
| `description` | no | What the context covers, in a sentence. Shown to admins beside the approval and to the assistant choosing a context. At most 1000 characters. |
| `instructions` | no | Rules the assistant follows for every query over this context's tables: units and currency, which status values count, the fiscal calendar, filters to always apply. At most 4000 characters. |
| `tables` | yes | The tables and views the context opens. At most 500. A table belongs to one context. |
| `tables[].table` | yes | `database.schema.name`, or `schema.name` when the catalog has the name in one database only. Bracket a part holding a dot: `[Sales Mart].dbo.[Order Lines]`. |
| `tables[].datasource` | no | The connection reference the table lives on (`${env:NAME}`, `${keyvault:NAME}` or `@alias`), needed only when the name exists on more than one datasource. Never a connection string. |
| `tables[].name` | no | The name a business reader knows the table by. At most 200 characters. |
| `tables[].description` | no | What one row represents, and what a query author must know (grain, filters, latency). At most 4000 characters. |
| `tables[].synonyms` | no | Other words for the table, matched by the assistant's search. At most 30, each at most 100 characters. |
| `tables[].key` | no | The columns identifying one row, in order. None may be denied. |
| `tables[].columns` | no | What columns mean. Each entry is a mapping with `column` and a `description` or `synonyms`. Describing a column does not open it, and a column may not be both described and denied. |
| `tables[].deny` | no | The columns the assistant may not use. Every other column of the table is open. |
| `tables[].measures` | no | Named SQL expressions over the table's open columns, reused verbatim: `name` (an identifier unique across every context), `expression` (one scalar expression, no `FROM`, `WHERE`, subquery, comment or `;`), `description`. |
| `tables[].joins` | no | How the table joins to others: `to` (named as `table` is), `columns` (this table's), `toColumns` (the other's, paired by position; the same names when left out), `type` (`inner`, the default, or `left`), `description`. A join is the same from either end, so declare it once. |
| `examples` | no | Questions the context answers, each with `question`, `sql` (one read-only `SELECT` over open columns), and an optional `datasource`. At most 500. A leading `!` on a question is dropped. |

`docs/reference/flow/keys.ai-context.json` is the full census (types, limits, and the exact messages), and is what
the editor reads.

## Which columns are open

A table in a context opens **every column the catalog holds for it, except the ones it denies**. That is the version
an admin approves: the columns are resolved against the catalog when the file is synced, so the approval names exact
columns, not a rule.

- **Denying a column** closes it at the next sync, with no approval: a denial only takes something away.
- **A column the warehouse adds** later appears in the next version the file proposes, and opens only when an admin
  approves that version. Until then it is closed.
- **A column the warehouse drops** stops being served at the next sync.

What a column means comes from `columns:`; that is description, never permission.

## Approval

The sync records each context's **current version**, the version its file proposes (a SHA-256 of the resolved
context, shown by its first 12 characters), with any problems. An admin approves a version on the AI context page in
the GUI (whose **File** view shows the file as the last sync read it, and its changes since the one approved), with
`sqlflow ai-context approve <name> --version <version>`, or with
`POST /api/v1/powerai/ai-contexts/{name}/approve` and `{"version": "<version>"}`. The version must be the one the
file proposes now, so a file that changed while someone was reviewing it is never approved unseen (409), and a version
with problems cannot be approved at all (409).

| Status | Meaning |
| --- | --- |
| `awaiting-approval` | Never approved, or revoked. Nothing is served; the current version can be approved. |
| `approved` | The approved version is the one the file proposes, and it is served. |
| `changed` | The file proposes another version. The approved one is still served, narrowed as below, until the new one is approved. |
| `invalid` | The current version has problems and cannot be approved. An approved version is still served. |

While a file proposes a version other than the approved one, what is served is the approved version **narrowed** by the
current one: anything the file takes away stops at once, and anything it adds or changes waits.

| The file | Takes effect |
| --- | --- |
| Removes a table, a measure, a join, an example, or the instructions | At the next sync |
| Denies a column, or the warehouse drops one | At the next sync |
| Adds a table, a column (by no longer denying it, or the warehouse adding it), a measure, a join, or an example | On approval |
| Changes a description, synonyms, a key, a measure's expression, a join's type, an example's SQL, or the instructions | On approval; the approved text is served until then |

A file that no longer reads or resolves (an unknown key, a table the catalog does not hold) narrows nothing: the
approved version is served as it was, the context shows `invalid` with the problems, and revoking it stops it at once.
Revoking (`sqlflow ai-context revoke <name>`, or the GUI) stops serving the context and leaves the file's version
waiting for approval. A context whose file is removed is removed, and stops being served.

## What a sync checks

Every version is resolved and checked as if it were approved, so its problems show before anyone tries:

- Every table and join target is one catalogued table or view with columns; every denied, described, key, and join
  column is a column of its table.
- A table denies at least one column fewer than it has.
- No table, measure name, or join is served by another context.
- Every measure, composed as `SELECT <expression> FROM <table>`, and every example query passes the read-only guard and
  the column guard with this version's columns as the open ones.
- No join is declared twice, and no question is given twice.

## Examples

An approved context's examples are saved answers with provenance `ai-context`: the assistant finds them for similar
questions and may run them as they are. They come and go with the context, so they are removed from the file, never
by hand (removing one on the AI context page answers 409). An example whose question and SQL are already a saved answer
(confirmed by someone in chat, or from a report) is left as that answer.

## Content from before contexts were files

The semantic layer used to be edited in the GUI. That content is no longer served, and is kept until a context takes
its tables. `sqlflow ai-context export` (or `GET /api/v1/powerai/ai-contexts/earlier-content`, or the AI context
page's **Download earlier content**) writes it as one context file, named `earlier-gui-content`: each table opens the
columns that were open and denies the rest, with its description, key, column notes, measures, and joins. Review it,
rename it, commit it, and approve it.

## Editor support

An AI context file gets hover documentation, key completion, an `aiContext` skeleton, and unknown-key diagnostics in
the workbench editor and the VSCode extension. The engine recognises the file by its root `aiContext:` key, and its
key model is `keys.ai-context.json`, embedded at compile time. `sqlflow validate` reports a context file's problems
too, without a catalog.

## CLI

```
sqlflow ai-context list                       # every context, its status, version, and tables
sqlflow ai-context show sales                 # the version proposed, problems, and what approving would change
sqlflow ai-context approve sales --version 3f9c2a1b7d04...
sqlflow ai-context revoke sales
sqlflow ai-context export [--out <file>|-]    # earlier GUI content as a context file
```

All of them need the admin scope.
