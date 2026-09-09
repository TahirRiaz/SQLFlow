/* tslint:disable */
/* eslint-disable */

/**
 * All diagnostics for the document, as a JSON array of
 * `{ range, severity, message, code }`.
 */
export function diagnostics(source: string): string;

/**
 * Hover documentation for the attribute at `(line, character)`, as a JSON
 * `{ markdown, range }` object, or `null` when nothing is documented there.
 */
export function hover(source: string, line: number, character: number): string | undefined;

/**
 * Census-driven semantic tokens, as a JSON array of `{ range, kind }`, sorted
 * by position. The GUI builds Monaco's delta-encoded token array from this.
 */
export function semantic_tokens(source: string): string;

/**
 * The engine version, so the GUI can surface which analysis build is loaded.
 */
export function version(): string;

export type InitInput = RequestInfo | URL | Response | BufferSource | WebAssembly.Module;

export interface InitOutput {
    readonly memory: WebAssembly.Memory;
    readonly diagnostics: (a: number, b: number, c: number) => void;
    readonly hover: (a: number, b: number, c: number, d: number, e: number) => void;
    readonly semantic_tokens: (a: number, b: number, c: number) => void;
    readonly version: (a: number) => void;
    readonly __wbindgen_add_to_stack_pointer: (a: number) => number;
    readonly __wbindgen_export: (a: number, b: number) => number;
    readonly __wbindgen_export2: (a: number, b: number, c: number, d: number) => number;
    readonly __wbindgen_export3: (a: number, b: number, c: number) => void;
}

export type SyncInitInput = BufferSource | WebAssembly.Module;

/**
 * Instantiates the given `module`, which can either be bytes or
 * a precompiled `WebAssembly.Module`.
 *
 * @param {{ module: SyncInitInput }} module - Passing `SyncInitInput` directly is deprecated.
 *
 * @returns {InitOutput}
 */
export function initSync(module: { module: SyncInitInput } | SyncInitInput): InitOutput;

/**
 * If `module_or_path` is {RequestInfo} or {URL}, makes a request and
 * for everything else, calls `WebAssembly.instantiate` directly.
 *
 * @param {{ module_or_path: InitInput | Promise<InitInput> }} module_or_path - Passing `InitInput` directly is deprecated.
 *
 * @returns {Promise<InitOutput>}
 */
export default function __wbg_init (module_or_path?: { module_or_path: InitInput | Promise<InitInput> } | InitInput | Promise<InitInput>): Promise<InitOutput>;
