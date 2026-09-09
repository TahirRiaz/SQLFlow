// Wires the SQLFlow analysis engine (running in a wasm web worker) into Monaco
// as language providers for flow YAML: hover documentation for every attribute,
// census-driven semantic-token colouring, and validation diagnostics. This is
// the browser's equivalent of the sqlflow-lsp language server, backed by the
// same sqlflow-lang engine so hovers and diagnostics match the VSCode editor.
//
// Providers are registered once, globally, for the `yaml` language, but only
// act on models opted in via `markFlowModel` so unrelated YAML editors are left
// untouched.
import type { Monaco } from "@monaco-editor/react";
import type { editor, IRange } from "monaco-editor";

// --- Engine positions (LSP form: zero-based line, UTF-16 character) ---------

interface Pos {
  line: number;
  character: number;
}
interface LspRange {
  start: Pos;
  end: Pos;
}
interface HoverResult {
  markdown: string;
  range: LspRange | null;
}
type Severity = "error" | "warning" | "information" | "hint";
interface DiagnosticResult {
  range: LspRange;
  severity: Severity;
  message: string;
  code: string | null;
}
type TokenKind = "property" | "unknownKey" | "enumMember" | "invalidValue";
interface TokenResult {
  range: LspRange;
  kind: TokenKind;
}

// The legend order must match the token-index map below and the Rust binding.
const TOKEN_TYPES: TokenKind[] = ["property", "unknownKey", "enumMember", "invalidValue"];
const TOKEN_INDEX: Record<TokenKind, number> = {
  property: 0,
  unknownKey: 1,
  enumMember: 2,
  invalidValue: 3,
};

// --- Worker RPC -------------------------------------------------------------

let worker: Worker | null = null;
let seq = 0;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: unknown) => void }>();

function ensureWorker(): Worker {
  if (!worker) {
    worker = new Worker(new URL("./worker.ts", import.meta.url), { type: "module" });
    worker.onmessage = (event: MessageEvent) => {
      const data = event.data as
        | { type: "ready" }
        | { id: number; result: unknown }
        | { id: number; error: string };
      if ("type" in data) {
        return; // "ready": init handshake, no waiter
      }
      const waiter = pending.get(data.id);
      if (!waiter) {
        return;
      }
      pending.delete(data.id);
      if ("error" in data) {
        waiter.reject(new Error(data.error));
      } else {
        waiter.resolve(data.result);
      }
    };
  }
  return worker;
}

function request<T>(message: Record<string, unknown>): Promise<T> {
  const w = ensureWorker();
  const id = ++seq;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (v: unknown) => void, reject });
    w.postMessage({ id, ...message });
  });
}

// --- Model opt-in -----------------------------------------------------------

const flowModels = new WeakSet<editor.ITextModel>();

/** Opt a model into SQLFlow flow-YAML analysis (hover, colouring, diagnostics). */
export function markFlowModel(model: editor.ITextModel): void {
  flowModels.add(model);
}

// --- Conversions ------------------------------------------------------------

function toMonacoRange(monaco: Monaco, r: LspRange): IRange {
  return new monaco.Range(
    r.start.line + 1,
    r.start.character + 1,
    r.end.line + 1,
    r.end.character + 1,
  );
}

function markerSeverity(monaco: Monaco, s: Severity): number {
  switch (s) {
    case "error":
      return monaco.MarkerSeverity.Error;
    case "warning":
      return monaco.MarkerSeverity.Warning;
    case "information":
      return monaco.MarkerSeverity.Info;
    case "hint":
      return monaco.MarkerSeverity.Hint;
  }
}

/** Delta-encode the engine's sorted, single-line tokens into Monaco's format. */
function encodeTokens(tokens: TokenResult[]): Uint32Array {
  const data: number[] = [];
  let prevLine = 0;
  let prevChar = 0;
  for (const tok of tokens) {
    const line = tok.range.start.line;
    const char = tok.range.start.character;
    const length = Math.max(0, tok.range.end.character - char);
    const deltaLine = line - prevLine;
    const deltaChar = deltaLine === 0 ? char - prevChar : char;
    data.push(deltaLine, deltaChar, length, TOKEN_INDEX[tok.kind] ?? 0, 0);
    prevLine = line;
    prevChar = char;
  }
  return new Uint32Array(data);
}

// --- Registration -----------------------------------------------------------

const MARKER_OWNER = "sqlflow";
let registered = false;

/** Register the flow-YAML providers with Monaco. Idempotent. */
export function registerSqlflowYamlProviders(monaco: Monaco): void {
  if (registered) {
    return;
  }
  registered = true;

  monaco.languages.registerHoverProvider("yaml", {
    async provideHover(model, position) {
      if (!flowModels.has(model)) {
        return null;
      }
      const res = await request<HoverResult | null>({
        op: "hover",
        source: model.getValue(),
        line: position.lineNumber - 1,
        character: position.column - 1,
      });
      if (!res) {
        return null;
      }
      return {
        contents: [{ value: res.markdown }],
        range: res.range ? toMonacoRange(monaco, res.range) : undefined,
      };
    },
  });

  monaco.languages.registerDocumentSemanticTokensProvider("yaml", {
    getLegend: () => ({ tokenTypes: TOKEN_TYPES, tokenModifiers: [] }),
    async provideDocumentSemanticTokens(model) {
      if (!flowModels.has(model)) {
        return { data: new Uint32Array() };
      }
      const tokens = await request<TokenResult[]>({ op: "semanticTokens", source: model.getValue() });
      return { data: encodeTokens(tokens) };
    },
    releaseDocumentSemanticTokens() {
      // Stateless provider: nothing to release.
    },
  });
}

/** Recompute and publish diagnostics for a flow model as editor markers. */
export async function refreshDiagnostics(monaco: Monaco, model: editor.ITextModel): Promise<void> {
  const diags = await request<DiagnosticResult[]>({ op: "diagnostics", source: model.getValue() });
  if (model.isDisposed()) {
    return;
  }
  const markers = diags.map((d) => ({
    startLineNumber: d.range.start.line + 1,
    startColumn: d.range.start.character + 1,
    endLineNumber: d.range.end.line + 1,
    endColumn: d.range.end.character + 1,
    message: d.message,
    severity: markerSeverity(monaco, d.severity),
    source: MARKER_OWNER,
    code: d.code ?? undefined,
  }));
  monaco.editor.setModelMarkers(model, MARKER_OWNER, markers);
}
