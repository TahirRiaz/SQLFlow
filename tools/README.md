# SQLFlow developer tooling

Language intelligence, editor integration, and AI tooling for SQLFlow
`.flow.yaml` projects. Three components share one Cargo workspace plus a
TypeScript VSCode extension; all of them are driven by the machine-readable
reference corpus under [`../docs/reference`](../docs/reference).

| Component | Language | What it is |
| --- | --- | --- |
| [`sqlflow-lang`](sqlflow-lang) | Rust (lib) | Protocol-free analysis engine: parses `.flow.yaml`, resolves cursor→path against the key census, and produces completion / hover / diagnostics / document symbols / code actions. |
| [`sqlflow-lsp`](sqlflow-lsp) | Rust (bin) | Language Server Protocol server over stdio, a thin shell over `sqlflow-lang`. |
| [`sqlflow-mcp`](sqlflow-mcp) | Rust (lib + bin) | Model Context Protocol server: the embedded reference corpus (doc tools) plus a live proxy over the control plane's `/api/v1` surface (catalog, lineage, runs, schedules, search) with device-flow auth. A library a host composes with modules of its own; the binary is that library run with none. |
| [`sqlflow-vscode`](sqlflow-vscode) | TypeScript (lib + extension) | VSCode extension: recognises pipeline documents by their content, launches the LSP, reads the estate through the MCP server and shares its browser sign-in, follows runs, searches the bundled docs, and registers the server with the editor's assistants. A library a host composes with modules of its own; its extension is that library with none. |

The design mirrors DeltaForge's protocol/analysis split, but SQLFlow authors
**YAML flow documents** (not SQL), so the analysis engine is driven by the
per-`flowType` `keys*.json` census files rather than a SQL parser.

## Module census files

A host module that adds flow kinds, or documents of its own, gives the engine a census for each at run time, in the
same `keys` format as SQLFlow's own files with the kind it describes at the top level:

```json
{
  "flowType": "widget",
  "strictKeys": true,
  "includeEnvelope": true,
  "keys": [
    { "path": "source.colour", "type": "enum(red|blue)", "enumValues": ["red", "blue"], "description": "..." },
    { "path": "body", "type": "object", "freeForm": true, "description": "..." }
  ]
}
```

- `flowType` names a flow kind the module adds, or `documentType` a document of its own (the value of its root
  `documentType` key). A file names exactly one; SQLFlow's own flow kinds cannot be replaced.
- `strictKeys: true` says the kind's loader refuses a document with an undocumented key, so the editor reports one as
  an error rather than as a key the loader ignores.
- `includeEnvelope: true` documents the platform envelope (`name`, `description`, `batch`, `schedule`, `mode`,
  `lifecycle`) as the file flow's census does; `includeShared: true` adds the shared blocks (`connections`, the invoke
  hooks, `servicePrincipals`).
- An entry with `freeForm: true` takes any content below it, such as an object an author writes whole.

`sqlflow-lsp` registers every `*.json` file of the directories its client names in the initialization options
(`{ "censusDirectories": ["..."] }`) and logs what it registered and what it skipped; the WebAssembly engine takes a file
through `register_census(json)`, which the GUI calls for each census a GUI module contributes. A document whose
`documentType` has no census is not analysed as a flow: the editor says once that its keys are not checked.

## Build

```sh
# Rust binaries (release)
cargo build --release -p sqlflow-lsp -p sqlflow-mcp

# VSCode extension: typecheck, then bundle (also stages the binaries + docs)
cd sqlflow-vscode
npm install
npm run bundle          # or: npm run package   (produces a .vsix)
```

`esbuild.mjs` copies `../docs/reference` into `dist/reference` (so the docs
browser works offline) and stages the built `sqlflow-lsp` / `sqlflow-mcp`
binaries into `bin/`. The extension also discovers binaries from
`../target/{release,debug}` when run from a checkout, or from the
`sqlflow.lspBinaryPath` / `sqlflow.mcpBinaryPath` settings, which a workspace
cannot set: only the person's own settings name a program it runs.

## The VSCode extension

The extension is a client of the MCP server: every view, panel and command reads through the server's tools, so it
sees what an assistant sees, under the same rules, and it holds no REST client. It starts the program on first use and
signs in through its `login` tool: the browser opens the control plane's approval, which goes on to the GUI, so an account
that signs in with Microsoft Entra approves there. The server keeps the session, and an assistant registered with the same
program shares it. The extension registers the server with VS Code's own assistants, once per mode, and
**Connect an AI Assistant** prints another client's registration from the server's `install`.

A YAML file is a pipeline document by what it holds (`src/documents/recognition.ts`, which follows
`FlowDocumentRecognition` and the engine's document kinds), since a repository's flows are plain `*.yaml` files. What one
file cannot know, such as the schedule a flow joins or the connections other flows use, comes from
`src/editor/references.ts`, which reads the workspace and the estate.

A product composes the extension as it composes the MCP server. `src/host.ts` is the library; a host is a package of its
own that compiles these sources in place and calls `activateHost`:

```ts
export async function activate(context: vscode.ExtensionContext) {
    return activateHost(context, {
        product: 'Widget',
        mcp: { program: 'widget-mcp', statePrefix: 'widget-mcp', providerId: 'widget.mcp', searchPaths: () => [] },
        lspSearchPaths: () => [],
        censusDirectories: (extensionPath) => [path.join(extensionPath, 'dist', 'census')],
        docCorpora: (extensionPath) => [path.join(extensionPath, 'dist', 'reference')],
        skeletons: [],
        cliVerbs: [],
        modules: [widgetModule],
    });
}
```

| A module adds | With | What it gets |
| --- | --- | --- |
| Sections of the Document view | `documentView.register` | Rows about the document in the editor, read when opened. |
| References | `references.register` | What a key may hold and the file a value refers to, as completion, links and go to definition. |
| Lenses | `lenses.register` | Actions at the top of a document. |
| Commands | `command` | A command that reads the estate: a failure is shown, a missing sign-in offers one. |
| A run's panel and form | `addRunSections`, `addRunParameters` | What it knows of a run of its kinds. |

VS Code reads a manifest as a file, so a host's `package.json` restates what this one contributes under the same `sqlflow.*`
ids, and a host's extension replaces this one rather than sitting beside it.

```sh
npm run compile   # type check
npm test          # the unit tests, which need no editor
```

## Test

```sh
cargo test --workspace
```

## MCP server

Register the built binary with an AI assistant (or use the extension's
**SQLFlow: Set Up MCP Server** command):

```sh
claude mcp add sqlflow --env SQLFLOW_CONTROL_PLANE_URL=http://localhost:8080 -- /path/to/sqlflow-mcp
```

Then, from the assistant, run the `login` tool (device flow) or
`set_access_token`. The device flow is served by the control plane at
`POST /api/v1/auth/device` and approved in a browser at `/device`. Over HTTP
(`sqlflow-mcp http`) every call carries its caller's bearer, so those sign-in
tools are not listed there.

The server's instructions are the assistant's one set of rules: the GUI chat
and the Slack bot read them from the server's `initialize` answer and add only
how their surface renders an answer, and an editor's assistant reads the same
text. A conversation is in estate mode or data mode (`src/modes.rs`), and only
the person's command moves it: a message starting with `!data` or a bare `!` puts
it in data mode until `!exit` (or `!estate`), so follow-ups need no command, and
every session refuses a call of a tool its mode does not answer. A client that
opens a session per message reads the command itself and names the mode on the
address (`?mode=data`, `?mode=estate`): the session lists only that mode's tools
and no switch. A session that names none lists every tool and starts in estate
mode; its model passes the person's message to `start_data_mode` or
`end_data_mode`, and the server switches only on the command it reads there.
`get_mode` says which mode a session is in and what set it. The grammar of a
command is one for every reader, held by `tests/assistant-commands.json`. A session is given the
instructions of its mode at `initialize`, naming only what it lists: a data-mode
session gets the modes, the rules for a question answered from data and the rules
every answer follows, a small part of the whole; an estate-mode session everything
but the data rules. The rule that every stated fact is a value a tool returned is
stated once there, not in each tool's description.

An editor's assistant keeps one session and may not refresh its tools, so it
lists every tool unless the server is registered in a mode (Claude Code's
terminal runs a message starting with `!` as a shell command, so there it is): `sqlflow-mcp --mode
data` (or `--mode estate`, or `SQLFLOW_MCP_MODE`) lists that mode's tools and the
sign-in tools, gives that mode's instructions, and stays in the mode: the switch
tools are not listed, and a call of the other mode's tool says which registration
answers it. Register the server once per mode
(`sqlflow-mcp install claude --mode data` prints `claude mcp add sqlflow-data --
<path> --mode data`) and turn on the one a chat needs; a data-mode registration
lists about a dozen tools. An unknown argument or mode stops the server with its
usage.

To write SQL, start from `find_similar_questions`: when no saved answer is
trusted, its answer's `grounding` carries the semantic layer the question needs
(the tables, their allowed columns, keys, joins, measures, the examples close to
the question, and each context's instructions). The layer's own tools find a
table the grounding does not hold: `get_semantic_layer` (the layer's
instructions, where its tables live, and its measures), `search_semantic_layer`
(tables, columns, and measures by name or business synonym), and
`describe_semantic_table` (one table's allowed columns, key, joins, measures,
example queries, and consumers). They serve only the tables and columns on the
column allow-list, and they are the only schema tools a data-mode session lists.

For lineage and code questions, browse with `list_schemas` (every server/database/
schema and its object count) and `lineage_objects` (filter by database, schema,
kind, or name). Then, for a specific object, `describe_object` returns its
identity, columns, generating script, module body, and lineage edges in one
payload.

### Two change histories, two tool families

The estate versions two different things, so "what changed" has two answers and the tools stay separate rather
than merging into one ambiguous search:

| Question | Tools | Reads |
| --- | --- | --- |
| What changed in a managed **database** (tables, views, procedures)? | `database_schema_changes`, `database_schema_history_databases`, `database_object_compare`, `database_object_ddl` | The schema history the source-control (`scm`) flows record when they snapshot each database |
| What changed in a **pipeline definition** (the flow YAML)? | `flow_definition_history`, `flow_definition_file_history`, `flow_definition_diff` | The git history of the repository the estate syncs |

Both are served by the control plane, which resolves the git credential itself from the reference stored against
the repo source or the scm flow. The MCP server holds no git credential and cannot address an arbitrary remote:
only repositories the estate already manages are readable.

Every online result carries a `links` object per row: the GUI page for the row
itself, its lineage graph, and the entities it references (its flow, the objects
a lineage step touches, the run behind a file). Use them when an answer names a
table, flow, or run, so the reader can open it instead of hunting for it.

Every SQL field of a result (`sql`, and any field whose name ends in `Sql`, such
as `sourceSql`, `reportSql` or `failedStatementSql`) comes laid out on lines, a
clause to a line and two spaces to an indent, because the control plane holds
most statements as one line and an editor or Slack shows them as they come
(`src/sql_format.rs`). Only the whitespace between tokens changes: names,
literals, comments and keyword case stay as written, and a statement that cannot
be laid out without changing a token comes back exactly as it was. A host
module's results, rendered with `json_str`, get the same layout.

Environment: `SQLFLOW_CONTROL_PLANE_URL` (default `http://localhost:8080`),
`SQLFLOW_CONTROL_PLANE_TOKEN` (optional token override), `SQLFLOW_GUI_URL`
(public GUI base URL; makes those links absolute instead of root-relative),
`SQLFLOW_MCP_LOG` (log filter). Config and token are persisted under
`~/.sqlflow/`.

A flow of a kind a host module registers is run with `trigger_run` like any other, with the kind's own arguments in
place of the built-in overrides: `operation` (what the run does, by the name the kind gives it), `values` (the flow's
parameter values) and `payload` (a JSON object the kind owns). The control plane hands them to the kind, which
validates them; SQLFlow's own kinds refuse them.

### Host modules

A product built on SQLFlow composes the control plane, the CLI and the GUI with modules of its own, and its MCP server
the same way. `sqlflow-mcp` is a library (`sqlflow_mcp`) whose binary is the host with no modules; a host crate depends
on it, names its program, and adds what its module contributes:

```rust
use sqlflow_mcp::rmcp::handler::server::wrapper::Parameters;
use sqlflow_mcp::rmcp::{self, schemars, tool, tool_router};
use sqlflow_mcp::{EmptyInput, McpContext, McpHost, McpModule};

#[derive(Clone)]
struct WidgetTools { ctx: McpContext }

#[tool_router(router = router)]
impl WidgetTools {
    #[tool(description = "List the widgets the estate holds.")]
    async fn list_widgets(&self, Parameters(_): Parameters<EmptyInput>) -> String {
        self.ctx.get("/api/v1/widgets", &[]).await
    }
}

#[tokio::main]
async fn main() -> anyhow::Result<()> {
    let module = McpModule::new("widget")
        .tools(|ctx| (WidgetTools { ctx }, WidgetTools::router()))
        .instructions("WIDGETS:\n- list_widgets lists them.");
    sqlflow_mcp::run(
        McpHost::new("widget-mcp", env!("CARGO_PKG_VERSION"))
            .registered_as("widget")
            .state_prefix("widget-mcp")
            .with_module(module),
    )
    .await
}
```

| A module adds | With | What it gets |
| --- | --- | --- |
| Tools | `McpModule::tools` | A service type of its own carrying `#[tool]` methods, listed beside SQLFlow's and called through the server's one dispatch. `McpContext` is the control-plane client (`get`, `get_about`, `post`, and `follow_task` for a compute task an endpoint queued), the GUI links, and whether the server runs over HTTP. Over HTTP a module's requests carry the calling user's bearer, as SQLFlow's do. |
| Instructions | `McpModule::instructions` | Its section of the text a client reads at `initialize`, after SQLFlow's own. |
| Reference pages | `McpModule::docs` | `DocPage`s (metadata plus an `include_str!` body) the doc tools search and fetch beside the embedded corpus. |
| GUI links | `McpModule::link_rule` | A rule that recognises its rows and links them to its pages. A rule that returns a `page` claims the row, so it is not read as one of SQLFlow's that carries the same fields; the row's references (its run, its flow) are still linked. |
| Key census files | `McpModule::census` | The census of each flow kind and document it adds (the format above), so `validate_flow`, `list_flow_keys` and `describe_flow_key` cover them. |

The host itself names the program (`--version`, the server's reported implementation, the user agent, the label of the
access token it mints), the name `install` registers it under, and the prefix of its two state files under
`~/.sqlflow/`, so it can be installed beside `sqlflow-mcp` and signed in to a control plane of its own.

A host also says which of SQLFlow's own tools it does not offer (`McpHost::without_tools`): a product that answers from
metadata alone leaves out the tools that read rows (`prepare_query`, `run_query` and the rest of the data-operations
surface). A tool left out is neither listed nor callable, and the instructions end by naming what is not offered, since
other tools' descriptions still refer to it.

Everything a module declares is checked when the host starts, and a problem stops the server with a message naming the
module: a name another module took, a tool SQLFlow's server already has, a page id already indexed, a census that does
not parse. The same goes for a tool the host leaves out that SQLFlow's server does not have, so one renamed upstream is
never offered again by accident. `tests/host_module.rs` composes a probe module through this public API and drives it over both transports.
