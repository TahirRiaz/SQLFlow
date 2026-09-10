# SQLFlow GUI

The control plane GUI: a React + TypeScript + Vite single-page app over the control plane REST API
(`/api/v1`). It is an observe-and-operate surface for a YAML-first product: pipelines are authored in git and
synced into the shadow catalog; the GUI shows them read-only (Monaco), triggers and monitors runs, watches the
compute fleet, and manages schedules, repo sources, and users.

## Sign-in

Three methods, all ending in the same SQLFlow-issued bearer token:

- Regular SQLFlow users: username + password (`POST /auth/login`).
- Azure single sign-on: "Sign in with Microsoft" (MSAL popup, then `POST /auth/exchange`). Shown when the
  control plane has `ControlPlane:AzureAd` enabled; first sign-in provisions the user with the viewer role.
- Bootstrap secret (break-glass, behind "Advanced" on the login page). Use it once to provision real users.

The token lives in memory + sessionStorage: a reload keeps the session, closing the tab ends it.

## Configuration

The API base URL resolves in this order:

- Production build: `public/config.json` (`{"apiBaseUrl": "https://controlplane.example.com"}`), swapped per
  environment at deploy time; falls back to the build-time `VITE_API_BASE_URL`.
- Dev server: the `VITE_API_BASE_URL` environment variable wins (see `.env.development`), so a shell or the
  e2e harness can point the GUI anywhere; falls back to `config.json`.

The control plane must allow this origin in `ControlPlane:Cors:AllowedOrigins`
(for development: `["http://localhost:5173"]`).

## Development

```bash
npm install
npm run dev        # http://localhost:5173
npm run typecheck
npm run build      # tsc + vite build into dist/
```

Run the control plane alongside (from the repo root):

```bash
ASPNETCORE_URLS=http://localhost:5000 \
SQLFLOW_CATALOG_DB="Server=localhost;Database=SqlFlowCatalog;Trusted_Connection=True;TrustServerCertificate=True" \
ControlPlane__Jwt__SigningKey=<32+ byte secret> \
ControlPlane__Jwt__BootstrapSecret=<32+ byte secret> \
ControlPlane__Cors__AllowedOrigins__0=http://localhost:5173 \
dotnet run --project src/SqlFlow.ControlPlane
```

Bootstrap provisioning applies catalog migrations, seeds the roles, and (when
`ControlPlane:Bootstrap:AdminUsername` / `AdminPasswordReference` are set) creates the first admin. To watch
compute node mode, start a worker: `sqlflow worker --db "<catalog connection>"`; it appears on the Nodes page
within a heartbeat.

## Flow-YAML language intelligence (the in-browser LSP)

The read-only Monaco view of a pipeline's YAML (the Pipelines detail page, YAML tab) has full language
intelligence: hover any attribute for its documentation, census-driven colouring (documented key vs a key the
loader will ignore, and valid vs invalid enum values), and validation squiggles for unknown keys, bad enum
values, and missing required keys.

**There is no separate LSP server process.** This is the `sqlflow-lang` analysis engine (the same engine behind
the `tools/sqlflow-lsp` language server that VS Code uses) compiled to WebAssembly and run in a Monaco web
worker in the browser. The engine is the single source of truth: the stdio LSP, the MCP `validate_flow` tool,
and this GUI are three bindings onto it, so a hover or diagnostic here matches the editor and the CLI exactly.
Because it is wasm, it needs no network and works in an offline/air-gapped deployment.

Layout:

- `tools/sqlflow-lang-wasm` (Rust) exposes `hover` / `diagnostics` / `semantic_tokens` over the engine.
- `src/lib/lsp/pkg/` is the generated wasm + JS glue, committed so a GUI-only build needs no Rust toolchain
  (as `tools/sqlflow-vscode` commits its binaries).
- `src/lib/lsp/worker.ts` loads the wasm off the UI thread; `src/lib/lsp/sqlflowLsp.ts` registers the Monaco
  hover, semantic-token, and diagnostics providers. `CodeView` opts a flow model in via its `lsp` prop.

### Booting it for testing

The GUI carries the LSP, so "start the LSP" means: run the control plane (to serve a flow's YAML) and the GUI
(which runs the wasm). In VS Code, the **Run and Debug** compound `Dev: Control plane + GUI (flow LSP)` does
both in one click: it debugs the control plane and starts the dev server after rebuilding the wasm, so you are
always testing the current engine. Then open `http://localhost:5173`, go to a pipeline, and select the YAML
tab. See `.vscode/launch.json`.

From the command line, the equivalent is the control plane (see [Development](#development) above) plus:

```bash
cd gui
npm run build:wasm   # after any change to the engine or the wasm bindings; needs the Rust wasm toolchain
npm run dev          # http://localhost:5173
```

`npm run build:wasm` (`scripts/build-wasm.mjs`) recompiles `sqlflow-lang-wasm` and regenerates `src/lib/lsp/pkg`.
It needs `cargo`, the `wasm32-unknown-unknown` target (`rustup target add wasm32-unknown-unknown`), and a
`wasm-bindgen` CLI matching the `wasm-bindgen` crate version. The generated `pkg/` is committed, so you only
rerun this when the engine changes; a plain `npm run dev` uses whatever is committed.

## End-to-end tests (Playwright)

```bash
npx playwright install chromium   # once
npm run e2e
```

The Playwright config spins up everything itself: the control plane (against a dedicated local test catalog
database, with bootstrap provisioning creating the e2e admin) and the Vite dev server, then exercises the GUI
element by element. See `playwright.config.ts` and `e2e/`.

## Branding

`src/theme/branding.css` holds the design tokens (carried over from the previous SQLFlow GUI's Radzen
Material 3 color base). The MUI theme is built from those custom properties at runtime
(`src/theme/ThemeModeContext.tsx`), so brand changes happen in one file.
