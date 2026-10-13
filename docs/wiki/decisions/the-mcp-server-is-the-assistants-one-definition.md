---
id: wiki-the-mcp-server-is-the-assistants-one-definition
title: "The MCP server is the assistant's one definition"
type: decision
summary: "Why the assistant's rules live in the MCP server alone, its two modes only the person's command moves, an editor's registration per mode, and a run's fence."
keywords:
  - mcp server
  - instructions
  - one definition
  - data mode
  - estate mode
  - "!exit"
  - "!estate"
  - "!data"
  - command grammar
  - session mode
  - get_mode
  - start_data_mode
  - follow-up
  - tools/list_changed
  - registered mode
  - "--mode"
  - instructions per mode
  - grounding
  - saved answer coverage
  - assistant fence
  - X-SqlFlow-Surface
sourceRefs:
  - tools/sqlflow-mcp/src/modes.rs
  - tools/sqlflow-mcp/src/server.rs
  - tools/sqlflow-mcp/src/host.rs
  - src/SqlFlow.ControlPlane/Background/QuestionSearch.cs
  - src/SqlFlow.ControlPlane/Api/SemanticLayer.Grounding.cs
  - src/SqlFlow.Assistant/McpServerInstructions.cs
  - src/SqlFlow.Assistant/AssistantQuestions.cs
  - src/SqlFlow.Assistant/AssistantRouter.cs
  - src/SqlFlow.Assistant/AssistantRuns.cs
  - src/SqlFlow.ControlPlane/Api/ChatEndpoints.cs
  - src/SqlFlow.SlackBot/SlackAssistantMode.cs
  - tools/sqlflow-mcp/tests/assistant-commands.json
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
refining or followup question he may forget to use !." So a conversation has a mode. A message starting with `!` (then
`!cwd`, now `!data`) puts it in data mode, where it stays, so "and by region?", "run it" and "save" need no command; `!exit` returns
it to estate mode, and the assistant ended data mode itself when someone forgot `!exit` and a follow-up turned to the
estate (the human chose both; the second went on 2026-10-13, see
[Only the person's command moves a mode](#only-the-persons-command-moves-a-mode)). Only a command starts data mode: phrasing never does, for the reason the earlier record
gives. The human asked to keep every command even where switching may not be perfect: "you dont need to remove any
feature or command", and "if its not working 100% thats fine".

The server defines the modes (`modes.rs`): data mode lists the semantic layer, the stored questions, the query tools
and saving an answer, about a dozen tools; estate mode lists every other tool, SQLFlow's and a product's own module
tools together, without the query tools.

- **A client that opens a session per message names the mode.** The GUI chat and the Slack bot reach the server
  through the model provider, which opens a session per request. The control plane reads the conversation's newest
  command from the person's own messages and opens the session with `mode=data` or `mode=estate`. Until 2026-10-13 a
  data-mode run that called `end_data_mode` was dropped and the question asked again in estate mode; such a session now
  lists no switch, and the run is asked once.
- **A session that names no mode lists every tool.** A stdio client keeps one session across chats. MCP lets a server
  change its tools and announce it (`tools/list_changed`), but Claude Code does not act on that mid-session and VS Code
  only from the next message (claude-code#79826, vscode#303012), so switching the list under such a client would leave
  a `!` question, or the follow-up that leaves data mode, without the tools it needs. Such a client sees everything;
  since 2026-10-13 the server answers it only in the mode it is in.
- **Rejected: one mode per conversation, set by its first message.** Simpler, and briefly built, but it dropped `!exit`
  and the assistant ending data mode, which the human wanted kept.

## The command's word, asking the mode, and remembering a switch

`!cwd` did not survive an editor: "!cdw is read as change folder rather than switching mode. we should also have a
method to check which mode the server currently is in." Claude Code runs a prompt that starts with `!` as a shell
command, so neither `!` nor `!cwd` reaches the model there. The human chose `!data` as the command's word; a bare `!`
still works wherever it reaches the model, and `!cwd` is gone. When a stored question is cleaned, the word is taken off
only when it touches the `!`, since "data" also starts ordinary questions ("! data quality by region").

- **Switching in words**, until 2026-10-13: `start_data_mode` (listed in estate mode) and `end_data_mode` (in data
  mode) moved a session's mode and announced the new tool list. Since then they switch only on the person's command,
  which the model passes them, and only in a session that names no mode.
- **Asking the mode.** `get_mode`, in every session, says the mode, how it was set (named by the client, switched by a
  tool, or the default), whether the session lists every tool, and the commands.
- **The mode is remembered.** A per-message session forgets everything when it closes, so the GUI chat stores each
  answer's mode with the message and the Slack bot puts it on its reply as message metadata. The next message starts
  from the newest of the person's commands and the answers' modes, which also holds a conversation in its mode when
  the command that set it is older than the turns a run replays.
- **Rejected: the MCP server keeping the mode across sessions.** The server would need a conversation identity the
  model provider's connector does not send, and the host already keeps the transcript, so the mode lives beside it.

## An editor registers per mode, and each mode has its own instructions

A `!` question asked through GitHub Copilot in VS Code took ten steps and sixteen seconds to end at a query offered for
approval. The editor's session listed every tool, 108 of them with about 146,000 characters of descriptions, beside
26,000 characters of instructions; with that many, Copilot held the tools back and the model spent steps finding the
one the `!` names. The human's words: "the idea was to make the mcp server load only relevant tools. Maybe our mcp
server is bloated and we need to create multiple modes to reduce the context size and confusion."

The server never sees a message, only the calls a model makes, so it cannot pick tools per question; it picks them
when a session opens. A per-message client already names the mode on the address. An editor names nothing, and the
reason it lists everything still holds: it does not refresh a changed list in time.

- **A registration names its mode.** `--mode data` or `--mode estate` (or `SQLFLOW_MCP_MODE`) registers a stdio server
  in one mode. Its session lists that mode's tools and the sign-in tools, which a stdio session needs whatever its
  mode, and is told what its registration means. An editor holds one registration per mode it uses and a chat turns on
  the one it needs; `install --mode data` prints one named after the mode. A data-mode registration lists about a
  dozen tools.
- **A registered session stays in its mode.** A switch would leave a client that does not refresh its list holding
  the other mode's tools, which the server would then refuse. So the switch tools are not listed, a switch called
  anyway changes nothing (to its own mode it says so, to the other it names the registration that answers it), and a
  call of the other mode's tool names the registration that answers it.
- **Instructions follow the mode.** A session is given its mode's instructions at `initialize`, naming only what it
  lists: data mode gets the modes, the data rules and the rules every answer follows, a small part of the whole; estate
  mode gets everything but the data rules; a session of every tool gets both. The control plane reads each mode's at
  the address its runs open in. Instructions naming tools a session lacks are what the earlier record says make a model
  claim the product cannot do something.
- **The grounding rule is stated once.** It was appended to every tool description because the model providers'
  connectors did not pass the instructions on; since the control plane gives the instructions as the system prompt,
  the hundred copies were a hundred repeats on every turn, and the rule now sits in every session's instructions once.
- **Rejected: more command-driven modes.** Splitting estate mode by area (runs, lineage, delivery) would add commands to
  remember, and estate questions cross areas: a failed delivery run is runs, delivery and lineage at once, and a wrong
  pick leaves the model without the tool it needs.


## Only the person's command moves a mode

An editor's session answered a `!data` question while every estate tool stayed callable in it, and the human asked
for the routing to be certain: "I need 100% reliability in the routing. Its okay to have a session dedicated mode. Data
questions dont mix with estate questions normally." Until then the model routed in three places: it switched when
asked in words, it ended data mode itself when a follow-up turned to the estate, and in a session that named no mode a
switch only recorded the mode, so nothing held the session to it.

- **The person's command is the only switch.** `!data` or a bare `!` starts data mode, `!exit` or `!estate` ends it,
  and nothing else moves a conversation: not a question's wording, not a request in words, not a follow-up of the other
  kind, which is answered with one line naming the command. The two cases the human had kept, the assistant ending data
  mode itself and switching in words, went, since each is the model's judgement.
- **One grammar, three readers.** The control plane reads the command for the GUI chat and the Slack bot, the MCP
  server reads it from the message an editor's model relays, and the GUI reads it when it saves a question.
  `tools/sqlflow-mcp/tests/assistant-commands.json` holds all three to the same answers, and each reader's suite also
  walks every character there is before a command and after each command word. `data` counts only touching the `!`,
  `exit` and `estate` also after a space, and a word ends only at white space or sentence punctuation, so `!exit.` is
  the command and `!exit-interview counts` a question.
- **Every session holds its mode.** A session its client names (`?mode=`) lists no switch, and a call of the other
  mode's tool is refused without running. The control plane reads the mode before the run and stores it with the
  answer, so nothing a run reports moves it, and the run is asked once. A registered session stays in its mode as
  before. A session that names no mode still lists every tool, since its client may not refresh its list, but answers
  only the tools of the mode it is in; its model passes the person's message to the switch, which moves the session
  only when the message carries the command.
- **Rejected: listing only the mode's tools after a switch in a session that names no mode.** A client that does not
  refresh its list would hold the estate's tools after `!data`, with no way to reach `find_similar_questions`; a full
  list with every call checked against the mode holds the mode whatever the client does.
- **What the server cannot hold.** In a session that names no mode the server never sees the person's message, only
  what the model passes, so the switch is as faithful as that relay. A registration per mode takes the model out of the
  switch altogether, and is what Claude Code's terminal, which runs a message starting with `!` in the shell, uses. Two
  chats sharing one stdio session share its mode.
## What a data question is sent

The same run showed the data path's own waste: the saved answers came back with their full SQL however little of them
was asked, and with none trusted the model searched the layer and described a table, whose ten examples were sent
whether or not any was close. The human: "why send queries for questions that do not match. We should only send queries
for questions that have a higher match."

- **A saved answer is sent only when the question covers enough of it.** Its coverage is the share of the saved
  question's own words the question or its synonyms carry, leaving out phrasing words and values; below
  `SavedAnswers:MinCoverage`, half by default, it is not sent at all. A word count alone could not tell "how many
  wellbores per country, basin and field" from "how many wellbores": both share the word that scores. The same floor
  cuts a described table's examples.
- **The layer comes with the answer.** When no saved answer is trusted, the assistant writes the query, so the answer
  carries the tables it needs, their open columns, keys, joins, measures and close examples, and each context's
  instructions, as `grounding`: the whole layer when it is small, else the tables the question's words mention most.
  The search and the description that used to follow are needed only for a table the grounding does not hold.

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
