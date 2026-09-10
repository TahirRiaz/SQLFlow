# SQLFlow - Project Instructions

## Project Identity

**This project is SQLFlow (SQLFlow V3). It is NOT DeltaForge.** They are different systems and must never be conflated.

- SQLFlow is a .NET solution: an ASP.NET Core control plane, an EF Core shadow catalog on SQL Server, a React + TypeScript + Vite GUI, and a direct ADO.NET execution engine. The product name shown in the GUI is "SQLFlow".
- The `deltaforge` MCP server, and its documentation corpus describing a Rust/Axum/PostgreSQL/Unity-Catalog stack, is a separate and divergent system. Those docs are NOT authoritative for this codebase and routinely do not match it (different language, storage, catalog model, and commands).
- When answering any question about how this system behaves, verify against the actual source in this repository and cite files from here, not the DeltaForge MCP docs.

## Writing Style

**THE EM DASH CHARACTER (Unicode U+2014) IS ABSOLUTELY FORBIDDEN. NEVER USE IT. NOT ONCE. NOT ANYWHERE.**

This means: zero occurrences in prose, headings, descriptions, code comments, doc comments, string literals, Markdown, HTML, YAML, TOML, SQL comments, shell script comments, JSON string values, or any other text context. There are no exceptions. If you find yourself reaching for this character, stop and rewrite the sentence using a comma, colon, semicolon, parentheses, or a new sentence instead.

## No Time Estimates

**Never give time estimates for tasks.** No "this will take a few hours", no "1-2 days", no "should be done in a week", no "expect roughly N hours", no sprint-sized framings, no "small / medium / large effort" gradings that imply duration. Do not include them in plans, summaries, status updates, PR descriptions, design docs, or any other output.

This applies whether the unit is minutes, hours, days, weeks, sprints, or vague qualitative bands ("quick", "longer effort", "non-trivial amount of time"). The user is not asking how long something will take and is not interested in your guess. Your estimates are not calibrated to their environment, their priorities, or the rest of their workload, so the number is noise.

**What to do instead:** describe scope (what gets touched, how many call sites, what the risky steps are, what depends on what). Scope is useful; duration is not. If the user explicitly asks for a time estimate, you may provide one; otherwise, omit it entirely.

## Git Workflow

**Primary work happens directly on the main branch (`main`). Feature branches are not required and must not be created.** All work (backend, frontend, YAML, everything) is implemented, tested, and committed directly on `main`. Never create a branch, and never ask the user which branch to use, unless the user specifically requests a branch. This overrides any default "branch first when on the default branch" behavior. If a branch is ever explicitly requested, delete it once its work lands so the repository stays single-branch.

**Never attribute commits to Claude.** Every commit's author and committer must be the human user's git identity. Do not add "Co-Authored-By: Claude ..." trailers, "Generated with Claude Code" lines, or any other AI attribution to commit messages, PR descriptions, or tags. This overrides any default harness behavior that asks for such trailers.

## Catalog Schema Changes Require a Migration

**Never change the shadow catalog's shape without its EF Core migration.** Any edit to `src/SqlFlow.Catalog/CatalogEntities.cs` or the model configuration in `CatalogDbContext.cs` (a new entity, a new column, a changed length, a new index) is incomplete until a migration exists. The catalog schema is upgraded ONLY by migrations, never by hand.

- Generate it with `dotnet ef migrations add <PascalCaseName>` from `src/SqlFlow.Catalog` (the design-time factory there makes this work without a startup project, and `migrations add` never touches a database). Commit all three artifacts together: the `{timestamp}_{Name}.cs` migration, its `.Designer.cs`, and the refreshed `CatalogDbContextModelSnapshot.cs`.
- Nothing else needs wiring: migrations are discovered from the `SqlFlow.Catalog` assembly. They apply automatically at control-plane startup (`BootstrapProvisioningService` when `ApplyMigrations=true`, the deployed default), via the CLI's `db` verbs, and in every integration test through `CatalogDatabase.MigrateAsync`.
- Local dev intentionally sets `ApplyMigrations=false` (`.sqlflow/env`) so a laptop never migrates the shared Azure catalog; the pending migration ships with the next deploy. Do not flip that flag to make a local run work against the shared estate.
- The tell-tale of a forgotten migration: the model snapshot differs from the last migration, tests fail with "Invalid column name", or the control plane logs pending-migration warnings. If you touched the entities and `git status` shows no new file under `src/SqlFlow.Catalog/Migrations/`, the work is not done.

## Never Change a Flow's Source or Destination

**NEVER CHANGE A FLOW'S SOURCE OR DESTINATION. NOT THE SOURCE LOCATION, NOT THE TARGET LOCATION, NOT THE STORAGE ACCOUNT, NOT THE BUCKET, NOT THE PATH, NOT THE CREDENTIALS. NOT IN PIPELINE YAML, NOT IN THE CATALOG. NEVER, UNLESS THE USER EXPLICITLY ASKS FOR THAT EXACT CHANGE.**

The declared endpoints of a flow ARE the design. We are upgrading to a new production environment: sources and targets are chosen deliberately as part of that migration, and what looks like a "dead" or "wrong" endpoint from the outside can be intentional, staged, or awaiting an upstream cutover. Repointing a flow at a different store because a run matched zero files is exactly the kind of unilateral rewiring that corrupts the migration plan.

When a flow finds no files or seems to read from the wrong place:

- Diagnose and report: what the flow declares, what actually exists at that endpoint, and where the data really is.
- Propose the endpoint change as an option and STOP. The user decides whether the flow moves, the upstream moves, or the finding changes the plan.
- Fixing anything else about the flow (options, patterns, schedule, comments) is fine when asked; the source and target locations are untouchable without an explicit instruction naming them.

## Single Code Path Principle

**Never create multiple execution pathways for the same feature.** Before implementing something, verify whether an existing path already handles it. Extend or modify the existing path rather than creating a parallel one. This applies at all layers: backend execution, API routes, GUI commands, and frontend data flows.

## Zero Warnings, Zero Errors

**The solution must build with zero warnings and zero errors.** `dotnet build SqlFlow.sln` (Debug or Release) must report `Build succeeded` with no warning or error diagnostics from any project, source or test. Do not hand back work that adds a warning, and do not silence one by lowering the analyzer configuration wholesale.

- Fix warnings properly at the source: correct the doc comment, add the missing culture/`IFormatProvider`, make the method static, implement `IDisposable`, forward the `CancellationToken`, and so on.
- Suppress a warning only when the flagged behavior is deliberate and correct (for example an intentional insecure-TLS opt-in, an EF Core query where the analyzer's rewrite would break SQL translation, a serialized DTO whose member name is an API contract, or an xUnit convention). Use a narrow `#pragma warning disable`/`restore` around the exact lines, or a `[SuppressMessage]` with a `Justification` that states why. Never a blanket file- or project-wide disable.
- Verify with a clean rebuild (`-t:Rebuild`) before committing, since an incremental build only re-reports warnings for recompiled projects.

## No TODOs, No Stubs, Production-Grade Only

**It is forbidden to leave TODOs, FIXMEs, placeholders, stubs, fake return values, or "will implement later" markers in any code you write.** Every function, branch, and module you touch must be fully implemented and production-grade before you hand it back. There is no such thing as a "first pass" that is allowed to be incomplete.

**This rule covers, without exception:**

- No `todo!()`, `unimplemented!()`, `unreachable!()` (unless the branch is genuinely unreachable and you can prove it from the type system, not from "it should not happen"), or `panic!("TODO ...")` in Rust.
- No `// TODO`, `// FIXME`, `// XXX`, `// HACK`, or `// stub` comments anywhere in code, doc comments, SQL, shell, TOML, YAML, JSON, HTML, CSS, or Markdown.
- No functions that return a hardcoded default, empty `Vec`, `None`, `null`, `0`, `""`, or fabricated success status as a placeholder for real logic.
- No `throw new Error("not implemented")`, `NotImplementedException`, `raise NotImplementedError`, or equivalent in any language.
- No half-wired control flow: every `match`/`switch`/`if` arm must do the real work, not log "would handle X here" and fall through.
- No commented-out code left in as a "future hook." If it is not running, delete it.
- No silent error swallowing as a stand-in for real handling. Every `Result`/`Option`/`try` boundary must do the right thing for both the success and failure path: propagate, recover, or surface to the user with context. "I will harden this later" is not an option.

**What "production-grade" means at the point you write the code:**

- Edge cases are handled: empty inputs, oversized inputs, unicode, concurrent access where the function can be hit concurrently, partial failure of downstream calls, IO/network/permission errors, malformed data, version skew across protocol boundaries.
- Resource lifetimes are explicit: files closed, transactions committed or rolled back, locks released on every exit path including panic/error, async tasks awaited or deliberately detached with a documented reason.
- Inputs are validated at trust boundaries (user input, external API responses, file contents, FFI), and trusted internal calls are not over-validated (see the global "no defensive checks for impossible scenarios" rule).
- Errors carry enough context to debug from a log line alone: which operation, which inputs (sanitised), which underlying cause.
- The code compiles, the types are tight (no stray `any`/`unknown`/`Box<dyn Any>` to dodge a real signature), and any new public surface has the same level of polish as the surrounding code.
## The Internals Wiki (docs/wiki/)

**`docs/wiki/` is an LLM-maintained knowledge base about how SQLFlow works and why. You write and maintain all of it; the human curates, directs, and asks the questions.** It follows the LLM wiki pattern: three layers, three operations, one index, one log.

**The three layers.**

- **Raw sources (immutable, never edited to fit the wiki):** the code under `src/`, the historical design documents directly under `docs/`, and the git history. Read them; do not rewrite them. If a raw document is wrong, record that in the wiki, do not silently correct the raw document.
- **The wiki (`docs/wiki/`, yours entirely):** interlinked markdown you create, update, and cross-reference.
- **The schema (this section):** the conventions and workflows below. Co-evolve it with the human as the wiki grows; when a convention here stops fitting, change it here rather than quietly diverging.

**The boundary with `docs/reference/` is absolute, and it is the Single Code Path Principle applied to documentation.** `docs/reference/` documents WHAT the surface does, is verified against code, is indexed by `manifest.json` via `build_manifest.py`, and is embedded into the MCP server at compile time. `docs/wiki/` documents WHY it is that way and HOW pieces fit across features. Never restate in the wiki what a reference page already says: link to it with `referenceRefs` and add only what the reference cannot carry, which is rationale, rejected alternatives, cross-cutting narrative, and history. If the right place for a fact is a reference page, put it there and regenerate the manifest instead of writing a wiki page.

**Taxonomy.** A page lives in exactly one directory, and its `type` must match: `patterns/` (`pattern`, which shape solves which data-engineering problem, with the production flow that proves it), `narratives/` (`narrative`, how pieces fit end to end), `decisions/` (`decision`, why it is this way and what was rejected), `incidents/` (`incident`, what broke and what generalizes), `maps/` (`map`, where knowledge lives and which parts are stale).

**Page format.** YAML frontmatter, then the body. Required: `id` (always `wiki-` plus the filename stem), `title`, `type`, `summary`, `keywords`, `updated` (ISO date). Optional: `sourceRefs` (repo-relative code paths the page's claims rest on), `rawRefs` (historical documents consulted), `referenceRefs` (`docs/reference/` page ids), `related` (other wiki page ids). Cite by file path and by `sourceRefs`, never by persisted line numbers, which rot; a reader greps. Keep `sourceRefs` honest, because it is the staleness tripwire: when one of those files disappears, lint fails the page.

**The three operations:**

- **Ingest.** One source at a time. Read it, discuss the takeaways with the human, write or update the pages it touches (a single source usually touches several), update `index.md`, and append an entry to `log.md`. Never batch-ingest many sources unsupervised.
- **Query.** Read `index.md` first to find candidate pages, then drill in, and answer with links to the pages you used. When an answer is worth keeping, file it back as a new page rather than letting it die in chat; that is how exploration compounds.
- **Lint.** Run `python docs/wiki/lint_wiki.py` for the mechanical half (frontmatter, dead `sourceRefs`, broken links, unindexed pages, orphans, log format and ordering). It must exit 0 before you hand back wiki work. Then do the half a script cannot: contradictions between pages, claims newer code has superseded, important concepts mentioned everywhere but lacking a page, and missing cross-references.

**Two special files, both mandatory to keep current.** `index.md` is the human and agent entry point, grouped by type, and a page missing from it fails lint. `log.md` is append-only and chronological, one entry per operation, always `## [YYYY-MM-DD] <ingest|query|lint> | <title>` so `grep "^## \[" docs/wiki/log.md | tail -5` works.

**The wiki is part of the MCP-indexed corpus, so adding a page is not finished until the manifest is rebuilt.** `docs/reference/build_manifest.py` scans BOTH `docs/reference/` and `docs/wiki/` into the single `docs/reference/manifest.json`, each entry carrying a `corpus` field naming which root its `path` is relative to; `tools/sqlflow-mcp/build.rs` resolves those roots and embeds every body at compile time. Run `python docs/reference/build_manifest.py` after any page or frontmatter change, and keep both trees in the Docker context (`.dockerignore` re-includes them; `Dockerfile.mcp` copies them) or the MCP image will not build.

**Write only what you have verified.** Every claim traces to code you read, a raw document you read, or a reference page. A wiki page is production-grade prose under the same no-stubs rule as code: no placeholder pages, no "to be filled in", no invented rationale. An absent page is honest; a speculative one is not.

## Convert Legacy Sources One At A Time (Controlled, Step By Step)

**Porting legacy runbooks/sources into V3 is a CONTROLLED, ONE-SOURCE-AT-A-TIME process. Never fan out across many sources in parallel, and never spawn subagents to author a batch of sources at once.** That produces a scattered mess of half-verified files and lands nothing. Do exactly one source, end to end, before touching the next.

**Each source is INDEPENDENT and unrelated.** Kolumbus ingests mobility data from many separate vendors: Citybike (bysykkel) is Kolumbus's own city bikes; Voi and Ryde are e-scooter operators; Getaround is car-share; EasyPark and Stavanger Parkering are parking; Fjord1 and Norled are ferries; Entur is national transit; Statens vegvesen (SVV) is roads/traffic; and so on. They share nothing, feed nothing into each other, and none of them are part of Citybike. Each has its own lake path (`raw/<source>/...`), its own catalog batch, its own folder, and its own DWH tables. Never merge, group, or imply a relationship between sources.

**The controlled per-source loop (finish every step for a source before starting the next):**

1. Read that ONE source's runbook(s). Confirm it is LIVE, not dead/retired/commented-out and not pointed at a test host. If it is dead or test-only, STOP and report it: do not ship an acquisition for it.
2. Author the acquisition flow for that one source.
3. Validate it.
4. RUN it and LAND real data into the V3 lake. Landing data is the point; an authored YAML that never runs is not progress.
5. Show the landed result (files/bytes/rows).
6. Only then move to the next source, and get the go-ahead first.

Do not batch, do not parallelize sources, do not report "authored N flows" as if that were done. "Done" for a source means its data landed and was shown.

## Source Flow Naming And Batch Conventions (match the sibling folders exactly)

When porting a source, the file names, flow `name:` fields, and `batch:` values MUST match the estate convention shown by the sibling folders (baatbooking, billettapp). Do not invent a variant.

- **Folder** = the readable source name, e.g. `Citybike/`, `Baatbooking/`.
- **File name and flow `name:`** = `<source>_<object>_<counter>_<flowtype>`, where `<source>` is the folder/source name lowercased (NOT the target table's prefix). Example: the `Citybike` folder holds `citybike_bikes_01_jsn`, `citybike_alert_02_ing`, and the acquisition `citybike_00_api`. The pipeline prefix ALWAYS matches its repo folder (like `billettapp_*`, `baatbooking_*`); it does NOT track the DWH table name (the Citybike tables are `Bysykkel_*`, but the flows are `citybike_*`). The `Bysykkel_*` names stay ONLY inside the flow bodies where they reference the real pre view / arc table.
- **`batch:` = a per-object (per-dataset) grouping label, NOT the source name.** Each dataset's pre+ods pair shares `batch: <object>` (e.g. `citybike_bikes_01_jsn` and `citybike_bikes_02_ing` are both `batch: bikes`); the acquisition flow uses `batch:` = its flow type (`api`, or `copy` for a `cpy` flow). This mirrors baatbooking (`detail`, `sess`, `trans`, `trans_item`, `copy`). `batch` is only a grouping/filter label and has NO effect on scheduling.
- **Scheduling** is by schedule membership, never by batch: define one schedule on the acquisition anchor (`schedule:` block, e.g. `citybike_daily`, daily `0 4 * * *` Europe/Oslo) and have every pre/ods flow join it with `schedule: <name>`. One fire runs the whole source wave-ordered (acquire -> pre -> ods).

## FUNDAMENTAL PRINCIPLE: MATCH THE OLD PRODUCTION FORMAT EXACTLY

**PORTING A SOURCE TO V3 MUST NOT CHANGE WHAT DOWNSTREAM CONSUMERS SEE. THE CONSUMER-FACING OUTPUT MUST MATCH THE OLD PRODUCTION TABLE EXACTLY: SAME TABLE NAME, SAME COLUMN SET, SAME COLUMN ORDER, SAME COLUMN NAMES, SAME TYPES (INCLUDING LENGTHS, AND decimal vs numeric). DOWNSTREAM USAGE MUST REMAIN INTACT. THIS IS NON-NEGOTIABLE.**

The V3 ODS/arc table is built from the typed view, so its physical shape (column ORDER, and occasionally a type/length) will NOT match the hand-built old production table even when the column SET is identical: the framework appends the surrogate PK, the audit columns, and any declared-but-not-landed "legacy extra" columns, so their positions shift. A consumer doing `SELECT *` or positional access would break.

So for EVERY ported ODS table, cross-match it column-for-column, IN ORDER, against the old production DDL (`B:\SQLFlowUpgradeV3\dw-dwh-prod\arc.<OldTable>.Table.sql`). When it is not an EXACT match:

- Point the ods flow at a physical table named for the V3 SOURCE, matching the flow's source prefix (e.g. the Citybike source's physical tables are `arc.Citybike_<Object>`, not the old `arc.Bysykkel_<Object>`). Do NOT use an `_ods` suffix.
- Create a VIEW with the OLD production table name (e.g. `arc.Bysykkel_<Object>`) over that physical table, projecting the EXACT old-production format: exact column order, names, and types (`CAST` every column to its old-prod type, listed in old-prod order). Downstream keeps querying the old name and sees the unchanged shape.
- Only skip the compat view when the V3 table is already byte-for-byte identical to old production.
- Creating the view is a one-time/manual DDL operation (no engine change; keep the `CREATE OR ALTER VIEW` statements in a `compat_views.sql` in the source folder for reproducibility). Do NOT bolt it onto the ods run.

Never reshape or rename what downstream depends on; interpose a compatibility view under the old name instead.
