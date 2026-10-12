---
id: wiki-the-mcp-server-is-the-assistants-one-definition
title: "The MCP server is the assistant's one definition"
type: decision
summary: "Why the assistant's rules live in the MCP server alone, why a conversation has data and estate modes, and why a fence bounds an assistant run."
keywords:
  - mcp server
  - instructions
  - one definition
  - data mode
  - estate mode
  - "!exit"
  - follow-up
  - tools/list_changed
  - assistant fence
  - X-SqlFlow-Surface
sourceRefs:
  - tools/sqlflow-mcp/src/modes.rs
  - tools/sqlflow-mcp/src/server.rs
  - src/SqlFlow.Assistant/McpServerInstructions.cs
  - src/SqlFlow.Assistant/AssistantQuestions.cs
  - src/SqlFlow.Assistant/AssistantRouter.cs
  - src/SqlFlow.Assistant/AssistantRuns.cs
  - src/SqlFlow.Assistant/AssistantInstructions.cs
  - src/SqlFlow.ControlPlane/Security/AssistantDelegation.cs
  - src/SqlFlow.ControlPlane/Api/AssistantScope.cs
referenceRefs:
  - guide-chat-assistant
  - guide-slack-assistant
  - concept-semantic-layer
  - concept-authentication-and-identity
related:
  - wiki-assistant-tool-sets-by-question
  - wiki-semantic-layer-is-the-allow-list
updated: 2026-10-12
---

# The MCP server is the assistant's one definition

How the assistant behaves (what a `!` question is, which tool answers which question, how a business answer reads) is
defined once, in the MCP server's instructions and tool descriptions. The GUI chat and the Slack bot read those
instructions from the server and add only how their surface renders an answer; an editor's assistant (VS Code, Claude
Code) reads the same text. A conversation is in estate mode or data mode, and what an assistant run may do beyond
reading is bounded by the control plane's fence, not by giving clients different tool lists. This page records why.

## What it replaced

The control plane carried its own copy of the rules: instructions per kind of question (`AssistantInstructions`), a
router that picked a tool set per message by its first character, and the lists those sets were made of
([The assistant has a tool set per kind of question](assistant-tool-sets-by-question.md)). The MCP server carried
another: its own instructions, which every other client read. The two drifted. A `!` question asked in VS Code about a
wellbore's curves was answered from record history, because the product's MCP server withheld the query tools and its
instructions never mentioned `!`, while the GUI chat answered the same question from its own C# rules. The human's
words: "There should be just 1 version of the truth. Multiple paths should be consolidated within mcp." and "The MCP
interface should be generic and support the same features in GUI and VSCODE or where ever the mcp server is deployed."

## One text, read by every client

The providers' MCP connectors pass a server's tools on but not its instructions, so the control plane reads the
instructions from the server's `initialize` answer (`McpServerInstructions`, kept five minutes) and makes them the
system prompt, adding only the surface's rendering: Slack's mrkdwn and layout fields, or the GUI's result block and
its Run and Save buttons. A test fails when a surface's text names any of the server's tools, so a rule cannot creep
back into the control plane. The rules the control plane alone used to state (grounding every fact in a tool result,
quoting executed SQL only from `run_statements`, the load mode from the load profile, the report priority, the
canonical flow authoring path, refusals) moved into the server's instructions, where a test pins them.

## Two modes, and the commands between them

Forgetting the `!` on a follow-up was the problem the human raised: "A user starts with ! the asks a question. The in a
refining or followup question he may forget to use !." So a conversation has a mode. A message starting with `!` (or
`!cwd`) puts it in data mode, where it stays, so "and by region?", "run it" and "save" need no command; `!exit` returns
it to estate mode, and the assistant ends data mode itself when someone forgets `!exit` and a follow-up turns to the
estate (the human chose both). Only a command starts data mode: phrasing never does, for the reason the earlier record
gives. The human asked to keep every command even where switching may not be perfect: "you dont need to remove any
feature or command", and "if its not working 100% thats fine".

The server defines the modes (`modes.rs`): data mode lists the semantic layer, the stored questions, the query tools
and saving an answer, about a dozen tools; estate mode lists every other tool, SQLFlow's and a product's own module
tools together, without the query tools.

- **A client that opens a session per message names the mode.** The GUI chat and the Slack bot reach the server
  through the model provider, which opens a session per request. The control plane reads the conversation's newest
  command from the person's own messages and opens the session with `mode=data` or `mode=estate`. When a data-mode run
  calls `end_data_mode`, the run is dropped and the question asked again in estate mode, under the same deadline, and
  the stream tells the host to drop what the first attempt wrote.
- **A session that names no mode lists every tool.** A stdio client keeps one session across chats. MCP lets a server
  change its tools and announce it (`tools/list_changed`), but Claude Code does not act on that mid-session and VS Code
  only from the next message (claude-code#79826, vscode#303012), so switching the list under such a client would leave
  a `!` question, or the follow-up that leaves data mode, without the tools it needs. Such a client sees everything,
  and the model keeps the mode from the instructions.
- **Rejected: one mode per conversation, set by its first message.** Simpler, and briefly built, but it dropped `!exit`
  and the assistant ending data mode, which the human wanted kept.

## The fence bounds a run, not a tool list

With the tool lists gone from the control plane, an assistant run sees tools that start work. The GUI chat's
delegated token was already default-deny outside the routes opened to assistant runs. The Slack bot's personal access
token was not: the read, operate and author policies only ask for a signed-in principal, and its allowlist had been the
only thing between anyone in a channel and `trigger_run`. So the fence now holds every call the MCP server marked as
the assistant's (`X-SqlFlow-Surface: assistant`), whatever credential it carries. The model cannot drop the marker, as
the address is the gateway's. The routes opened to an assistant run gained the two that the shared rules call on
every surface: confirming an answer and running a trusted stored one.

## What stayed

The column allow-list governs every query in either mode, on prepare and on run; schema is still not sensitive and is
served whole in estate mode. `Mcp:AllowedTools` remains for a deployment that wants fewer tools; it is empty by
default, meaning every tool the session's mode lists.
