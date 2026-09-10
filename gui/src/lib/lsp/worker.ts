// Web worker hosting the SQLFlow analysis engine (sqlflow-lang) compiled to
// WebAssembly. It runs off the UI thread so parsing a flow document never
// blocks rendering. Each message parses the document and calls one engine
// function; the wasm returns JSON strings which are parsed here and posted back
// as structured results correlated by request id.
import init, { hover, diagnostics, semantic_tokens } from "./pkg/sqlflow_lang_wasm.js";

// Minimal typing of the worker global (the project's tsconfig uses the DOM lib,
// not WebWorker, so DedicatedWorkerGlobalScope is not in scope here).
const ctx = self as unknown as {
  postMessage(message: unknown): void;
  onmessage: ((event: MessageEvent) => void) | null;
};

// `--target web` init: point it at the co-located wasm asset. Vite fingerprints
// and bundles the wasm, so this URL resolves in dev and in an offline build.
const ready = init({
  module_or_path: new URL("./pkg/sqlflow_lang_wasm_bg.wasm", import.meta.url),
});
ready.then(() => ctx.postMessage({ type: "ready" }));

type Request =
  | { id: number; op: "hover"; source: string; line: number; character: number }
  | { id: number; op: "diagnostics"; source: string }
  | { id: number; op: "semanticTokens"; source: string };

ctx.onmessage = async (event: MessageEvent<Request>) => {
  await ready;
  const req = event.data;
  try {
    let result: unknown;
    switch (req.op) {
      case "hover": {
        const json = hover(req.source, req.line, req.character);
        result = json ? JSON.parse(json) : null;
        break;
      }
      case "diagnostics":
        result = JSON.parse(diagnostics(req.source));
        break;
      case "semanticTokens":
        result = JSON.parse(semantic_tokens(req.source));
        break;
    }
    ctx.postMessage({ id: req.id, result });
  } catch (err) {
    ctx.postMessage({ id: req.id, error: String(err) });
  }
};
