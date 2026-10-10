---
id: guide-chat-assistant
title: "The GUI chat assistant: the SQLFlow agent in the workbench, with per-user authority and voice input"
type: guide
summary: How the GUI's Assistant page answers questions - the same assistant core as the Slack bot, streamed over SSE from the control plane, with tool calls made under a short-lived token delegated from the signed-in user, persisted conversations, image paste, and voice input via server-side transcription.
keywords:
  - chat
  - assistant
  - gui
  - streaming
  - sse
  - conversations
  - foundry
  - responses api
  - mcp
  - per-user
  - voice
  - dictation
  - transcription
  - image
  - screenshot
  - confirmation
  - learning loop
  - powerai
  - run query
  - dataops
  - chart
  - saved answers
related:
  - guide-slack-assistant
  - guide-deployment
  - concept-authentication-and-identity
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.Assistant/IAssistantGateway.cs
  - src/SqlFlow.Assistant/ResponsesApiGateway.cs
  - src/SqlFlow.Assistant/AnthropicGateway.cs
  - src/SqlFlow.Assistant/TranscriptionGateway.cs
  - src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - src/SqlFlow.ControlPlane/Configuration/ControlPlaneOptions.cs
  - src/SqlFlow.Catalog/CatalogEntities.cs
  - gui/src/features/chat/ChatPage.tsx
  - gui/src/features/chat/chatAdapters.ts
  - gui/src/features/chat/AnswerConfirmation.tsx
  - src/SqlFlow.ControlPlane/Api/QuestionExampleEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/SemanticExampleAdminEndpoints.cs
  - gui/src/features/semantic-layer/SavedAnswersPanel.tsx
  - gui/src/features/semantic-layer/ExampleDialog.tsx
  - gui/src/features/chat/QueryResultPage.tsx
  - tools/sqlflow-mcp/src/server.rs
  - gui/src/features/chat/SqlRunPanel.tsx
  - gui/src/features/chat/QueryResultView.tsx
  - gui/src/features/chat/QueryResultEmbed.tsx
  - src/SqlFlow.ControlPlane/Api/DatasourceInference.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.ControlPlane/Api/QueryEndpoints.cs
  - deploy/bicep/control-plane.bicep
  - deploy/bicep/ai-foundry.bicep
---

# The GUI chat assistant

The workbench's Assistant page (`/chat`) is a ChatGPT-style chat over the SQLFlow estate: ask "what failed last night?", "what feeds `dbo.Orders`?", or "why is this table short?", and the answer streams in, grounded in the live catalog and the reference docs through the same SQLFlow MCP server the Slack assistant uses. Paste a screenshot to ask about it, or press the microphone and ask out loud; the recording is transcribed server-side and lands in the composer as text.

## One assistant, two surfaces

The Slack bot and the GUI chat share one implementation: `SqlFlow.Assistant`, the provider gateways (Azure AI Foundry / OpenAI over the Responses API, Anthropic over the Messages API's MCP connector) and one instructions document. Only the surface differs: Slack answers are formatted as mrkdwn and posted once; GUI answers are GitHub-flavored Markdown streamed token by token, with live tool-call activity shown in the thread. Switching providers or updating the assistant's knowledge changes both surfaces at once.

## Per-user authority (the difference from Slack)

The Slack bot holds one shared read-scoped token, because everyone in a channel shares the bot's identity. The GUI chat has a signed-in user on every request, so each agent run gets a token the control plane delegates from that user for that one run: the assistant reads at most what that user may read, the control plane enforces it per tool call, and the model host never holds the user's own session. The delegated token expires with the run (`RunTimeoutSeconds` plus 60 seconds), cannot be renewed, and is refused everywhere outside the read surface the tools need, so it cannot mint a personal access token, approve a device sign-in, read the user's conversations, or start work; see [Assistant run tokens](../concepts/authentication-and-identity.md#assistant-run-tokens). The tool allowlist still defaults to the read-only surface (`ControlPlane:Assistant:Mcp:AllowedTools`).

## The chain

```
GUI /chat -> control plane POST /api/v1/chat/ask (SSE stream)
          -> SqlFlow.Assistant gateway (Foundry / OpenAI / Anthropic)
          -> sqlflow-mcp over streamable HTTP (the tools, the run's delegated token)
          -> control plane /api/v1 (fenced to the read surface the tools need)
```

Conversations persist in the catalog (`ChatConversation` / `ChatMessage`): the transcript is the durable record, provider-side conversation state is only a cache of it, so a control-plane restart or a re-opened browser loses nothing. Deleting a conversation deletes its messages; conversations are strictly per-user.

## Links in answers

Answers are navigational: when the assistant names a table, flow, run, schedule, or report, it links the name to that thing's page in the workbench, and a table also gets a link to its lineage graph. The links are not composed by the model. Every online MCP tool result carries a `links` object on each row, built by the MCP server from the identity the row already had:

| Row | `links` |
| --- | --- |
| a warehouse object | `page` (its catalog page), `lineage` (the graph focused on it) |
| a flow | `page` (`/pipelines/<id>`), `lineage` (the graph focused on it, scoped to its repo) |
| a run | `page` (`/runs/<id>`), `flow`, `runGroup` |
| a schedule | `page` (its runs board, filtered to it) |
| a subscriber (report/dashboard) | `page` (`/subscribers?key=...`); its own `url` still points at the report itself |
| a lineage step, edge, or search hit | `object`, `objectLineage`, `flow`, `run` for whatever it references |

Rows are recognised by the identity fields they carry rather than by the tool that returned them, so the same shape links identically wherever it appears, and a tool added later is linked without being wired up.

`SQLFLOW_GUI_URL` on the MCP server decides the form: set it to the GUI's public base URL and the links are absolute, which is what Slack and any client rendering outside the GUI need. Left unset they are root-relative (`/catalog?node=...`), which resolves for the GUI chat and nowhere else. `main.bicep` sets it from the deployed GUI automatically.

## Confirming an answer

Under every finished answer that hands back a SQL query, the thread shows one row: **Yes**, **Not quite**, **No**. This is the confirmation moment of PowerAI's learning loop, and it is what turns an answer a person checked into precedent the next similar question is matched against.

- **Yes** opens the query read-only for a last look, with an optional datasource picker, and stores it as a confirmed example.
- **Not quite** opens the same query in an editable SQL editor. What is stored is the CORRECTED query, never the original proposal, so the example the estate learns from is the one that actually worked.
- **No** stores nothing, and nothing records that it was pressed: only confirmations are kept. Only correct, verified answers become precedent, so a refuted query is discarded rather than kept as a "do not propose this again" row.

The query being judged is read from the answer's own ```sql fenced blocks, which is exactly what the person read before deciding; an answer carrying several queries gets a numbered picker. Only `sql`-tagged fences count, since an untagged one is as likely to be YAML or a table of results. The question is the user turn the answer replies to, so an answer confirmed from a re-opened conversation records the same pair a live one would. A leading `!cwd` command is dropped when the question is stored (`QuestionExampleEndpoints.NormalizeQuestion`, applied to confirming and to editing a saved answer), so the same question saved from the GUI, Slack, or another MCP client is one question.

The datasource is optional and must be a whole `${env:...}` / `${keyvault:...}` reference or an `@alias` that the catalog already declares. Without one, the confirm endpoint stores the datasource the query's objects live on (from the object keys when supplied, whose first segment is the connection reference, otherwise from the tables the SQL reads), and leaves it empty only when those do not point at exactly one declared datasource. The objects the example reads are resolved the same way when the caller sends none (the catalogued tables the SQL names), so an answer confirmed from this row is served as an example of those tables in the semantic layer.

The row posts to `POST /api/v1/powerai/questions/confirm`, the same endpoint behind the `confirm_question` MCP tool, so a click and a tool call land the same row and nothing new is stored on this path. It appears only when the deployment has the example store turned on: `GET /api/v1/chat/capabilities` reports `questionConfirmation`, which is `ControlPlane:PowerAI:Retrieval:Enabled` read from the same options the confirm endpoint answers 501 on. Confirming the same question and query again refreshes the existing example rather than adding a duplicate, so reaffirming an answer does not give it extra weight in later searches.

The assistant itself calls `confirm_question` only when the person, after seeing a result, says it is right, corrects it, or asks to save it. Approving a run ("yes", "go ahead") is never that, and a person saying an answer is wrong saves nothing, so the assistant does not call it then.

## Managing saved answers

Saved answers are the semantic layer's example queries (`catalog.SemanticExample`, see [Semantic layer](../concepts/semantic-layer.md)). Admins curate them under Admin > **AI knowledge**, on the **Saved answers** tab (`/semantic-layer?tab=examples`; the old `/saved-answers` link redirects there) and on each table's **Examples** tab, which holds the answers that read that table (`admin` scope). The Saved answers tab lists every saved answer newest first, with a search over question and query text, the datasource it runs against, who last stood behind it, and whether the assistant is currently offered it (`served`, or `withheld` with the reason, when its SQL no longer passes the read-only guard or the column allow-list).

- **Edit** changes the question, the query (edited as plain text, stored exactly as written), or the datasource. It is validated exactly as a confirmation is (the question length, `ReadOnlyQueryGuard`, `ColumnPolicyGuard`, the known-reference gate). The duplicate hash is recomputed, and an edit that would make the answer identical to another saved one answers 409. When the query changes, the tables it reads are resolved again; a blank datasource is worked out from them. The editor becomes `ConfirmedBy`, because a trusted answer auto-runs on the strength of someone having checked exactly that query.
- **Delete** removes it: later questions stop finding it, and it can no longer be auto-run.

Adding an answer is still only done by confirming one. The curation endpoints (`SemanticExampleAdminEndpoints`, admin scope):

| Endpoint | Purpose |
| --- | --- |
| `GET /api/v1/powerai/semantic-layer/examples?search=&page=&pageSize=` | Saved answers, newest first, each with its served/withheld `problem` |
| `GET /api/v1/powerai/semantic-layer/examples/{id}` | One saved answer |
| `PUT /api/v1/powerai/semantic-layer/examples/{id}` | Edit `question`, `sql`, and `sourceRef` (blank to infer); 400 when refused, 409 when it would duplicate another |
| `DELETE /api/v1/powerai/semantic-layer/examples/{id}` | Delete it; 404 when it does not exist |

Confirming (`POST /api/v1/powerai/questions/confirm`) and auto-running a trusted match (`POST /api/v1/powerai/questions/{id}/auto-run`) keep their routes.

## Running a query

Every finished `sql`-fenced block in the thread carries a Run (play) button in its own toolbar row, beside the format/copy buttons `CodeView` already draws. Clicking it runs the query through the *same* two-step DataOps path (`docs/reference/concepts/data-operations.md`, "Running a query from the chat GUI") `prepare_query`/`run_query` use over MCP: prepare mints a token, run redeems it. The SQL is already fully visible in the block, so the click itself is the person's approval; there is no second confirmation dialog, matching how a confirmed example needs no re-approval before `auto_run_trusted_match`.

The click names no datasource: the control plane works it out from the tables the query reads (`DatasourceInference`, the same resolver retrieval, confirmation and auto-run use). Only when it cannot tell, because none of the tables is on a datasource the estate declares or they exist on several, does prepare answer 422 and a picker (filtered to datasources the estate can actually reach) appear with that explanation. The result renders as a fitting chart, never a fixed one: a single row of one to four numbers becomes stat tiles, one category column against one measure becomes a bar chart, a date column against up to four measures becomes a line chart (splitting into small multiples rather than sharing an axis when those measures' scales are far apart, since a shared axis would flatten the smaller one to the floor), and any other shape falls back to a table. A Chart/Table toggle is always available next to a rendered chart. `gui/src/features/chat/SqlRunPanel.tsx` owns the run state; `QueryResultView.tsx` is the classifier and renderer.

## Business questions (`!cwd`)

A `!cwd` question is answered for a business reader: the answer opens with the plain finding in a sentence or two, without retrieval scores, matched terms, datasource references or tool names. A trusted confirmed match (`find_similar_questions` reports `trusted`, and the match carries an `exampleId`) is run immediately with `auto_run_trusted_match`, with no approval prompt and no datasource question; `auto_run_trusted_match` is on the GUI's default tool allowlist for exactly this. A dashboard match (`provenance` `powerbi`) carries three SQL-related fields: `sql` is the visual's query translated into T-SQL over the source tables and runs through `prepare_query`/`run_query` like any other query; `reportSql` is the visual's own query against the report's model and is never run; `translationProblem` says why a visual could not be translated, in which case `sql` is empty and the match is only a lead, so the assistant composes the query from the semantic layer. The subscriber dossier (`describe_subscriber`) shows the same pair as each query's `sql` and `sourceSql`. A match is trusted when it clears `RankThreshold` or is the same question as the one typed (the same meaningful words once stop words and plural or verb endings are ignored), so a short question's verbatim twin is not refused for having only one word to count.

When a query actually ran, the answer carries a fenced `query-result` block holding the run's compute task id. `QueryResultEmbed.tsx` renders it by reading the stored task (`GET /api/v1/datasources/tasks/{id}`), as the same chart or table the Run button shows, so nothing executes again and a re-opened conversation shows the same result. The SQL behind the answer follows in a `sql` block, which keeps the Run button and the confirmation row available on it.

When nothing has run yet, the answer opens with where the query comes from ("I don't have a saved answer for this question yet, so I've put together a query that should answer it.", or that one of the estate's reports already answers the question), says what the query will show, and gives the `sql` block to Run. It never tells the person a match is trusted, untrusted, or low-scoring: that is the assistant's working knowledge, not something a business reader can act on.

## Answer layout for other MCP clients

A client outside the GUI (Claude Code, a desktop app) has no layout of its own for these answers, and may truncate the MCP server's instructions, so the tool results carry the layout themselves:

| Field | Returned by | What it says |
| --- | --- | --- |
| `responseRule` | `find_similar_questions` | Say nothing between tool calls; no remarks about matches, trust, or scores |
| `approvalFormat` | `prepare_query` | The no-saved-answer opening, what the query will show, the SQL, then "Want me to run it?"; never a word about trust or scores, never a request to judge the data source |
| `answerFormat`, `sqlIntro`, `sqlBlock` | `run_query`, `auto_run_trusted_match` (successful results only) | The finding, then `sqlIntro` ("Here's the saved query I ran:" for a trusted auto-run, "Here's the query I ran:" otherwise), then `sqlBlock`, the SQL already fenced; and that running is not confirming |
| `saveOffer` | `run_query` only (an auto-run replays an answer already saved) | The last line, "If this is right, reply *save* and I'll remember it for next time."; saving still happens only on that explicit reply |
| `chartLink` | the same, only when the MCP server has `SQLFLOW_GUI_URL` | `/query-results/<taskId>`, a GUI page that draws the stored result with `QueryResultEmbed` (the same chart as the chat) and shows its SQL, without running it again |

Every layout field opens by yielding to the client's own instructions, which is how the GUI chat and the Slack bot keep their layouts: the GUI chat's instructions tell it to ignore them (it draws the chart inline), and the Slack instructions adopt `approvalFormat` and `answerFormat`. The MCP server cannot tell those clients apart, since both connect with `surface=assistant`.

The affordance is gated by `dataOpsRunQuery` on `GET /api/v1/chat/capabilities` (`ControlPlane:DataOps:Enabled`, the same switch `prepare_query`/`run_query` answer 403 on), so it is absent, not merely disabled, on a deployment without the data-operations surface turned on.

## Voice input

Two modes, picked automatically:

- **Server-side transcription** (preferred): when a transcription deployment is configured (`ControlPlane:Assistant:Foundry:TranscriptionDeploymentName`, or `OpenAI:TranscriptionModel` in OpenAI mode), the mic records in the browser and `POST /api/v1/chat/transcribe` turns the recording into text via the provider's `/audio/transcriptions` endpoint, using the same managed identity / API key as the answers.
- **Browser speech recognition** (fallback): without a transcription model, the mic uses the browser's built-in speech recognition where available.

## Configuration

The feature is off by default and reports itself through `GET /api/v1/chat/capabilities`, so a GUI on an estate without AI simply explains that instead of erroring. Enable it with `ControlPlane:Assistant:*`:

- `Enabled=true`
- `Provider`: `AzureFoundry` (default, managed identity, no key), `OpenAI`, or `Anthropic`
- `Foundry:ProjectEndpoint` + `Foundry:ModelDeploymentName` (AzureFoundry mode)
- `Mcp:ServerUrl`: the deployed SQLFlow MCP endpoint
- optional: `Foundry:TranscriptionDeploymentName`, `MaxImages`, `MaxImageBytes`, `RunTimeoutSeconds`
- key-based modes: `OpenAI:ApiKey` / `Anthropic:ApiKey` accept `${env:...}`/`${keyvault:...}` references

In the Bicep estate this wires itself: once `aiFoundryName` + `aiFoundryModelName` and `mcpImage` are set (the same inputs the Slack assistant needs), `main.bicep` enables the chat on the control plane, points it at the estate's Foundry project and MCP server, and grants the control-plane identity the Cognitive Services OpenAI User role. Set `aiFoundryTranscriptionModelName` (for example `gpt-4o-mini-transcribe`) to deploy the audio model and light up server-side voice input.
