---
id: wiki-assistant-tool-sets-by-question
title: "The assistant has a tool set per kind of question"
type: decision
summary: "Why a message starting with ! gets the semantic layer and the query tools and any other message gets the whole catalog, and why schema stopped being withheld."
keywords:
  - tool set
  - business question
  - "!"
  - estate question
  - schema is not sensitive
  - redaction
  - assistant surface
  - allowed_tools
  - AssistantRouter
sourceRefs:
  - src/SqlFlow.Assistant/AssistantQuestions.cs
  - src/SqlFlow.Assistant/AssistantRouter.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
  - src/SqlFlow.ControlPlane/Api/LineageEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SearchEndpoints.cs
  - tests/SqlFlow.Core.Tests/AssistantModesTests.cs
referenceRefs:
  - concept-semantic-layer
  - concept-data-operations
  - guide-chat-assistant
  - guide-slack-assistant
related:
  - wiki-semantic-layer-is-the-allow-list
  - wiki-ai-contexts-are-files-an-admin-approves
  - wiki-the-mcp-server-is-the-assistants-one-definition
updated: 2026-10-12
---

# The assistant has a tool set per kind of question

Later the same day the per-message tool sets, their lists in the control plane, and the control plane's own
instructions became history: the MCP server is now the assistant's one definition, a conversation stays in data mode
from its `!` message until `!exit`, and a fence rather than a tool list bounds what an assistant run may do.
[The MCP server is the assistant's one definition](the-mcp-server-is-the-assistants-one-definition.md) records why. What
this page says about schema not being sensitive still holds.

The chat assistants answer each message with one of two tool sets, picked by its first character: a message that
starts with `!` is a business question and gets the semantic layer, the stored questions, and the query tools; any
other message is a question about the estate and gets the whole catalog and no query. What each set holds is in
[Semantic layer](../../reference/concepts/semantic-layer.md#mcp-tools). This page records why.

## What it replaced

Every message used to get the same tool list, holding both the data tools and the estate tools. What separated a
`!` question from any other was the wording of the instructions, and nothing enforced it: the model could run a query
for an ordinary question, or skip the semantic layer on a `!` one. That one shared list is also why the schema was
hidden everywhere. With both kinds of question reading from one list, the only way to make the semantic layer "the
schema" was to take the catalog's schema readers off the chat and redact every answer that named a column outside the
layer ([The semantic layer is the column allow-list](semantic-layer-is-the-allow-list.md) records that choice).

## Schema is not sensitive

The human's words: "we should not redact anything when it comes to schema information", and "schema information is
not sensitive information". Table and column names, view bodies, flow YAML, and executed statements are what an
engineer reads in the GUI every day; withholding them from the assistant made it worse at the questions it is mostly
asked (why a run failed, where a column comes from) and protected nothing a signed-in user could not already see.

So the redaction filter on the read and operate endpoints is gone, and so is the column filtering of schema search,
the object column list, and the dossier. The column allow-list now governs data alone: what a query may read, and
what a data-operations task the assistant enqueues may read (it reads live rows, so it still passes the column guard).

## Two sets, chosen by the person

The human agreed to a set per kind of question rather than routing by instruction. The kind comes from the first
character alone (`AssistantQuestions.KindOf`), because a model asked whether a question "sounds like" a business
question gets it wrong both ways, while a person typing `!` never does. `AssistantRouter` builds the two routes, each
a tool list and the instructions that name only those tools, and both gateways ask it per question, so no provider can
cross them.

- **By construction, not by instruction.** A business question has no schema but the semantic layer to find, so it
  cannot reach a table outside the approved AI contexts; an estate question has no query tool, so it cannot read a
  row. A model told to stay on one path can stray; a model given only that path's tools cannot.
- **Each set's instructions describe only its tools.** A tool absent from a run looks absent to the model, and
  instructions naming it make the model say the product cannot do the thing. The estate instructions say to ask again
  with `!` for a figure from the data; the business instructions say to ask again without it about flows and runs.
- **Replies carry the command too.** A tool set is per message, and a Slack reply such as "run it" or "save" would
  otherwise reach the estate set, which has no query tool. So the Slack bot asks for `!run` and `!save`, and the
  `saveOffer` line says `!save`. In the GUI, running and saving are buttons, so a reply needs no command.
- **Rejected: continuing a business exchange without the command.** Carrying the business set into the next message
  after a business answer needs a rule for where the exchange ends, and any such rule misreads some message: a new
  estate question taken for a reply, or a reply for a new question. The command is one character and never wrong.

## What stayed

The assistant marker (`surface=assistant` on the MCP address, `X-SqlFlow-Surface` on its control-plane calls) stays,
for the one thing it still narrows: a data-operations task the assistant enqueues. The tools that start work, author
code, or profile live rows outside a business question (`detect_unique_key`) are in neither set.
