---
id: wiki-ai-contexts-are-files-an-admin-approves
title: "AI contexts are files an admin approves"
type: decision
summary: "Why AI contexts are files that only propose, why an admin approves an exact version, why files deny columns, and why removals apply at once and additions wait."
keywords:
  - ai context
  - ai-context.yaml
  - approval
  - context version
  - deny list
  - default deny
  - narrowing
  - source control
  - semantic layer
  - column allow-list
  - read-only gui
  - generateQuestions
  - business question command
sourceRefs:
  - src/SqlFlow.Yaml/YamlAiContextLoader.cs
  - src/SqlFlow.Catalog/AiContextVersion.cs
  - src/SqlFlow.Catalog/AiContextMaterializer.cs
  - src/SqlFlow.ControlPlane/Api/AiContextSync.cs
  - src/SqlFlow.ControlPlane/Api/AiContextEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/RepoSyncFollowUp.cs
  - src/SqlFlow.Catalog/Migrations/20261012070812_AiContextsFromFiles.cs
  - src/SqlFlow.Catalog/Migrations/20260915090833_RenameColumnPolicyToAllowList.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneHost.cs
  - tests/SqlFlow.ControlPlane.Tests/AiContextApiTests.cs
referenceRefs:
  - flow-ai-context
  - concept-semantic-layer
  - concept-data-operations
  - flow-subscribers
  - guide-chat-assistant
related:
  - wiki-semantic-layer-is-the-allow-list
updated: 2026-10-12
---

# AI contexts are files an admin approves

The semantic layer used to be edited in the GUI: an admin allowed columns one by one, described tables, wrote
measures and joins, and typed the instructions. It is now defined by [ai-context.yaml](../../reference/flow/ai-context.md)
files in a repository, which only propose, and an admin approves each version before an assistant uses it. What the
layer serves did not change; [The semantic layer is the column allow-list](semantic-layer-is-the-allow-list.md)
still holds. This page records why the authoring moved and why the approval has the shape it has.

## What prompted it: the GUI showed everything open

Looking at the GUI, the human found every column of every table allowed. The cause was
`RenameColumnPolicyToAllowList`: when the policy turned from a blacklist into an allow-list, it seeded an allowing
row for every column the catalog held, so the existing surfaces kept working. That kept the engineers' pages intact
and left the assistant's layer wide open, with nothing in the GUI to tell a seeded row from a reviewed one.

The human's direction, in their words: "everything should be off by default"; "manage AI context using yaml
file... that gives us source control. However, it should be admin that enables or whitelists a table"; "yaml file
alone is not enough. Admin must approve it"; "there can be multiple files so all files must carry an identification
so admin can approve a certain set"; "GUI can be simplified to read only".

`AiContextsFromFiles` therefore removes the seeded rows that carry no content, removes the denying rows (a missing
row says the same now that closed is the default), and closes the rest. Nothing written before is served; what an
admin had typed is kept, unserved, so it can be exported into a file.

## Files propose, an admin approves an exact version

A merged file is not an approval: a repository has more writers than the people who decide what an assistant may
read, and a pull request review is about code, not data access. So a sync only records the version a file proposes
(the resolved context, hashed) beside the version an admin approved, and an approval names the version it is for.

- **Rejected: approving a context by name.** The file could change between the review and the click, and the
  approval would cover a version nobody read. The approve call carries the hash the reviewer saw and is refused when
  the file proposes another one now.
- **Rejected: approving files rather than names.** A file is moved and split as a repository is reorganised. The
  root `aiContext:` key is the identity the human asked for, so moving a file keeps its approval and renaming the
  context starts a new one.

## A context is the unit of approval, not a scope the assistant chooses

[The allow-list decision](semantic-layer-is-the-allow-list.md) rejected named contexts because they would add a
membership store beside the allow-list and a step where the assistant picks a context before it can find a table.
AI contexts avoid both. A context is what an admin approves and where instructions are written; it is not what the
assistant reads. Approving one writes the same allow-list rows as before, so `ColumnPolicy` is still the only
membership, and the assistant still sees one estate-wide layer and never chooses a context. A table belongs to one
context, so two approvals can never disagree about its columns.

## A file lists what it denies

The human's call: "yaml file should list what's denied. If a table is allowed then all columns should be allowed by
default." Listing every open column made a context file as long as the warehouse is wide, and most of the list
would be noise around the few columns that matter.

The risk of a deny list is a column the warehouse adds later, which no file denies. The version is what closes that:
columns are resolved when the file is synced, so the approved version names exact columns, and a new one appears
in the next proposed version and opens only when an admin approves it. Describing a column (`columns:`) is kept
apart from permission, and a column may not be both described and denied, so a description can never be mistaken
for opening something.

## Removals apply at once, additions wait

Between approvals the served version is the approved one narrowed by the file. What the file takes away (a table, a
denied column, a measure, a join, an example, the instructions) stops at the next sync; what it adds or changes
waits for an admin. Closing access is never the risky direction, and someone who finds a column that should not be
open must be able to close it with a commit, without waiting for an admin.

- **Rejected: serving nothing while a context is changed.** Every edit would take the assistant offline until the
  next approval.
- **Chosen: last known good for a file that does not read.** A file with an unknown key or a table the catalog does
  not hold narrows nothing; the approved version is served as it was, the context shows `invalid`, and revoking it
  stops it at once. Failing closed there would turn a typo into an outage for everyone using the context.

## One writer, writing by difference

Approval, revocation, and a sync's narrowing all go through `AiContextMaterializer`, the only writer of the serving
rows, each marked with its context. It writes by difference: a sync runs after every repository sync, and rewriting
every served row each time would churn thousands of rows and give each example question a new id, while an assistant
cites a saved answer by its id.

## Examples in files, saved answers at run time

Curated example questions are part of a context and approved with it. Answers people confirm in chat stay run-time
data: they are the learning loop, and routing each through a pull request would stop it. They are read-only in the
GUI except for removal. An answer a context gives is removed from its file, never by hand, so the file and what is
served cannot disagree; an answer that already exists as a confirmed one is left as that answer.

## The rest moved into files too

- **Question generation** became `generateQuestions:` on each subscriber in its library. The runtime switch and the
  deployment default (`ControlPlane:PowerAI:QuestionGeneration`) went away; a host still carrying the setting refuses
  to start and names what replaced it, rather than leaving a deployment believing generation is on.
- **The business-question command** became a leading `!` (`!cwd` still works), the only trigger for the semantic
  layer.

## Content written in the GUI

Content typed into the old editor was not migrated into served contexts: that would serve it with no approval. It is
exported as one context file (`sqlflow ai-context export`), each table opening what it opened and denying the rest,
to review, commit, and approve. A context that takes one of those tables replaces what the GUI held for it.
