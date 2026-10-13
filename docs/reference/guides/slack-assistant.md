---
id: guide-slack-assistant
title: "The Slack assistant: a SQLFlow agent over Foundry's Responses API and the MCP server"
type: guide
summary: The SQLFlow Slack bot, a Socket Mode relay to a model with the SQLFlow MCP server, answering from the catalog and running business queries a thread approves.
keywords:
  - slack
  - assistant
  - bot
  - foundry
  - responses api
  - mcp
  - agent
  - socket mode
  - gpt-5.1
  - business questions
  - run query
  - saved answers
  - vision
  - image
  - screenshot
  - diagnose missing data
  - lineage
related:
  - guide-chat-assistant
  - guide-deployment
  - concept-authentication-and-identity
  - concept-control-plane
sourceRefs:
  - src/SqlFlow.Assistant/ResponsesApiGateway.cs
  - src/SqlFlow.SlackBot/SlackAssistantHandler.cs
  - src/SqlFlow.SlackBot/SlackBotOptions.cs
  - src/SqlFlow.SlackBot/SlackMrkdwn.cs
  - src/SqlFlow.Assistant/AssistantSettings.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - tools/sqlflow-mcp/src/http_server.rs
  - deploy/bicep/slack-bot.bicep
  - deploy/bicep/mcp.bicep
  - deploy/bicep/ai-foundry.bicep
  - deploy/slack/manifest.yaml
---

# The Slack assistant

Ask SQLFlow questions from Slack ("what failed last night?", "what feeds `dbo.Orders`?", "what does the incremental section do in a flow?") and get answers grounded in the live catalog and the reference docs. It explains, diagnoses, and browses, answers business questions by running read-only queries a thread approves, and saves an answer when someone says it is right. It never triggers, cancels, or changes flows, runs, or schedules.

## What it can and cannot do

For everything except a business question it reads **metadata**: the catalog, lineage, runs, file receipts, object definitions, and the reference docs. It reasons from those rather than counting rows.

- **Diagnosing missing or late data.** For "why is `dbo.X` empty / short?", it does not guess: it locates the table, walks lineage upstream to the feeding source, then reads that source's recent runs and `run_files` to see whether it delivered, comparing the latest run's **file size and row count** against prior runs (a succeeded run can still under-deliver). It concludes with the specific cause and the numbers: the source run failed, ran with zero files, has not run since the data was due, or delivered well below its norm.
- **Concrete queries to go further.** Once a diagnosis has identified the real objects, it hands you ready-to-run T-SQL against them, fully qualified and using only the allowed columns the semantic layer lists, so you can inspect the data directly.
- **Reading images.** Paste a screenshot (an error dialog, a run's log, a flow YAML) and ask about it; the vision-capable model reads the image, then answers using the tools (looking up the named run or table rather than trusting the picture alone). This needs the `files:read` scope on the Slack app.
- **Thread context, only when invoked in a thread.** A mention inside a thread uses that thread's replies as context. A top-level mention is answered on its own; the bot never reads the broader channel history, so it only ever sees what a conversation it was invoked in contains.

## Business questions and modes (`!`, `!exit`)

A thread is a conversation, with the same two modes as the [GUI chat](chat-assistant.md#business-questions-and-modes--exit): a message that starts with `!data` or a bare `!` puts the thread in data mode, answered from saved answers and the tables approved AI contexts open; the thread stays in it, so a reply needs no `!` (`run` to run an offered query, `save` to save an answer, "and by region?" to change the question); `!exit` (or `!estate`) returns it to estate mode; nothing else moves it, neither a reply that turns to the other kind of question nor a request in words. Each of the bot's answers carries the mode it was given in as Slack message metadata (event type `sqlflow_assistant_mode`, not shown in the thread), and the bot reads the thread's commands and that metadata, so the next reply starts in the mode the last answer was given in, and a top-level mention starts in the mode its own command sets. A command is read after the mentions are taken out of the message (`<@U123>` and `<@U123|name>` alike), so a channel question that opens with the bot's mention is routed as typed. The rules are the MCP server's instructions; this surface adds only Slack's layout:

1. **A trusted saved answer runs straight away** with `auto_run_trusted_match`, no approval asked.
2. **Anything else is offered first.** The bot says it has no saved answer yet (or that a report already answers the question), says what the query will show, gives the SQL, and asks for `run` in the thread to run it. In a channel that reply must mention the bot, because the bot only receives @-mentions there; in a DM a plain reply works. Anyone in the thread can approve, since the thread shares the bot's one identity.
3. **On approval it runs the plan** with `run_query`. A prepared plan is single-use and expires, and the Anthropic gateway does not carry earlier tool results between turns, so when the plan is gone the bot prepares exactly the approved SQL again and runs it in the same turn.
4. **The result** follows the `answerFormat` layout the tool returns: the finding, "Here's the query I ran:", the SQL, and a "View this as a chart" link to the GUI's `/query-results/<taskId>` page when the MCP server has `SQLFLOW_GUI_URL`. `SlackMrkdwn` drops a fence's language tag (```` ```sql ````), because Slack does not highlight code and would show the tag as the first line.
5. **Saving is a separate reply.** After a result that did not come from a saved answer, the bot ends with the `saveOffer` line `run_query` returns: "If this is right, reply *save* and I'll remember it for next time." It calls `confirm_question` only when someone then says the answer is right or asks it to save (outcome `corrected`, with the corrected SQL, when they fixed it). Approving a run is never a request to save, and a "that's wrong" saves nothing.

Running a query, saving an answer and running a trusted saved answer all need `ControlPlane:AiContext:Enabled` on the control plane (a trusted saved answer waits for `run` instead when `ControlPlane:AiContext:AutoRun:Enabled` is false); with it off the tool answers 403 or 501 and the bot falls back to giving the SQL. Every query is refused unless it is a single read-only SELECT over allow-listed columns (`ReadOnlyQueryGuard`, `ColumnPolicyGuard`), on prepare, run, confirm, and auto-run alike.

## The chain

```
Slack thread -> sqlflow-slack-bot (Socket Mode relay)
             -> Azure AI Foundry, OpenAI Responses API (a model + the MCP tool)
             -> sqlflow-mcp over streamable HTTP (the tools)
             -> control plane /api/v1 (bearer-scoped)
```

Each hop has one job. `sqlflow-slack-bot` (`SqlFlow.SlackBot`) is a Socket Mode worker: it dials out to Slack over a websocket (no public inbound surface), receives an @-mention or a DM, and relays the question. For each question it makes one `POST /openai/responses` call to the Foundry account's Azure OpenAI endpoint, carrying the model, the assistant's instructions (the MCP server's own, read from its `initialize` answer, with Slack's layout added), and the MCP tool. The model decides which tools to call; Foundry's runtime connects to `sqlflow-mcp` and runs them; the model composes the answer, which the bot posts back into the Slack thread. `sqlflow-mcp` is the same MCP binary IDE assistants use locally over stdio, deployed here in `http` mode as a shared remote tool source (see `tools/sqlflow-mcp/src/http_server.rs`); it holds no credentials and forwards each request's bearer token to the control plane, which enforces scopes exactly as it does for the CLI and GUI.

## Why the Responses API (and which models work)

The bot drives Foundry through the **OpenAI Responses API**, not the older persistent-agents (Assistants) API. This is a hard requirement of the MCP tool, not a preference: current model deployments support the MCP tool only through the Responses API. A persistent-agents run with a current model fails - `gpt-5-mini` returns `unsupported_model: This model only supports Responses API compatible tools`, and `gpt-5.1` is accepted at create time but fails at run time - and the models the persistent-agents MCP tool did support (`gpt-4.1`, `gpt-4o`) are closed to new deployments. So the model deployment behind the assistant must be one that supports both the Responses API and its MCP tool. There is no built-in default: `aiFoundryModelName` is empty unless you set it (empty deploys no model), so the model must be chosen explicitly at deploy time. Any Responses-API + MCP-capable deployment works; the Bicep recommends `gpt-5.1`.

The gateway (`ResponsesApiGateway`, shared with the GUI chat assistant via the `SqlFlow.Assistant` library) builds each request as: the model deployment name, the instructions (the MCP server's own instructions, the single source of truth for behavior on every client of the server, with Slack's layout added), an `input` (the new turn, or the replayed Slack transcript on a cold thread), and one `mcp` tool object carrying the MCP server URL with the thread's mode (`?surface=assistant&mode=data`), `require_approval: never`, and the `Authorization` header. There is no hosted agent object to create or converge; the definition lives entirely in the request.

## What it is allowed to do (the assistant fence and the one bot identity)

1. **The mode decides the tools; the fence decides what they may do.** The MCP session opens in the thread's mode, and the MCP server lists that mode's tools: in data mode the semantic layer tools (`get_semantic_layer`, `search_semantic_layer`, `list_semantic_tables`, `describe_semantic_table`) as the only schema readers, with `find_similar_questions`, `prepare_query`, `run_query`, `auto_run_trusted_match` and `confirm_question`; in estate mode the docs, the catalog readers and the whole schema, which is not sensitive, and every other tool the server offers. `SlackBot:Mcp:AllowedTools` narrows the list further when a deployment wants fewer. See [Semantic layer](../concepts/semantic-layer.md#mcp-tools).
2. **The token is an identity, not a narrower scope.** The bot's whole SQLFlow authority is one personal access token, sent to the MCP server as the MCP tool's `Authorization` header and forwarded to the control plane per call. The control plane's `read`, `operate`, and `author` policies only require a signed-in principal (only `admin` checks a scope), so the token's scopes would not stop a tool that starts work. The MCP server marks every call of the bot's session as the assistant's (`?surface=assistant`, `X-SqlFlow-Surface: assistant`), and the control plane holds a marked call to the routes opened to assistant runs, whatever credential it carries ([assistant run tokens](../concepts/authentication-and-identity.md#assistant-run-tokens)): the read surface, preparing and running a query, confirming an answer and running a trusted one. `trigger_run`, `cancel_run`, `propose_pipelines` and every other tool that starts work are refused there, and the bot says where a person does it.

Everyone in a workspace shares this one bot identity, which is why the fence stays read-only. Anyone in a thread can approve a query, every query runs as the token's owner, and every answer the bot saves records that owner as `ConfirmedBy`. That is acceptable for the data-mode tools because what they can do does not depend on who approves: a query is only ever a single read-only SELECT over allow-listed columns, a plan is single-use, and admins review and remove saved answers on the AI context page's Saved answers tab. It is not acceptable for a tool that starts work, which is why the fence, not a tool list, stops it: no configuration of the bot opens a route the fence keeps closed. The surface with a per-user identity is the [GUI chat assistant](chat-assistant.md), where every agent run carries a short-lived token delegated from the signed-in user.

## Conversation state

A Slack thread maps to a server-side Responses conversation. The bot stores each response id and passes it as `previous_response_id` on the next question in that thread, so follow-ups keep context without resending the history. The Slack thread itself is the durable transcript: if the in-memory mapping is lost (a bot restart) or the stored response has expired server-side, the bot rebuilds the context once by replaying the thread's recent messages into the request `input`.

## How it reaches users

The bot answers two ways (`SlackAssistantHandler`): an @-mention in any channel it is a member of (public or private, after `/invite`), and a direct message. It reacts with 👀 to acknowledge, then replies in-thread. Answers are formatted for Slack mrkdwn, and when a GUI base URL is configured, runs and pipelines are linked to their GUI pages.

## Deploying it

Three optional templates extend the core estate; `main.bicep` wires them when their inputs are set:

- `ai-foundry.bicep`: the Foundry account, a project, and a pinned model deployment (`aiFoundryModelName`, use a Responses-API + MCP-capable model such as `gpt-5.1`; it has no default, and empty deploys no model). It grants the bot identity the **Cognitive Services OpenAI User** role, which is what lets the bot call the Responses API with its managed identity, no key.
- `mcp.bicep`: the MCP server in HTTP mode, external ingress but bearer-gated (`/mcp` requires a token; only `/healthz` is open).
- `slack-bot.bicep`: the Socket Mode bot, no ingress, secrets from Key Vault via managed identity.

Setup, in order:

1. **Deploy the estate** (see [Deploying](deployment.md)), if it is not already up.
2. **Create the Slack app**: at https://api.slack.com/apps choose "From an app manifest" and paste `deploy/slack/manifest.yaml`. Install it to the workspace, then collect the app-level token (`xapp-...`, Basic Information, App-Level Tokens, `connections:write`) and the bot token (`xoxb-...`, OAuth & Permissions, after Install to Workspace).
3. **Mint the bot's SQLFlow token**: create a personal access token for a dedicated SQLFlow account (for example `slack-bot`) rather than a person's own. This is the credential forwarded to the MCP server on every call, and every query the bot runs and every answer it saves is attributed to that account.
4. **Build and push the two images** (`Dockerfile.mcp` and `Dockerfile.slackbot`).
5. **Redeploy `main.bicep`** with `aiFoundryName`, `aiFoundryModelName=gpt-5.1` (or another Responses-API + MCP-capable deployment), `mcpImage`, `slackBotImage`, and `slackAppToken`/`slackBotToken`/`slackBotSqlflowToken`. This writes the three tokens into Key Vault and grants the bot the Cognitive Services OpenAI User role. Then `/invite` the bot to a channel (or DM it) and ask.

Notes:

- **A first question right after deployment can fail once** while the Cognitive Services OpenAI User role assignment propagates to the bot's managed identity; ask again and it recovers.
- **The MCP endpoint is public but bearer-gated**: Foundry's runtime calls in from Microsoft-managed compute, so `sqlflow-mcp` has external ingress; every `/mcp` request must carry a valid SQLFlow token or it is rejected at the edge, and all data access is enforced by the control plane per token scope.
- **Model support moves**: MCP-over-Responses-API is a current-generation capability. If a model deployment starts rejecting the MCP tool, move `aiFoundryModelName` to a supported deployment rather than changing the bot.
