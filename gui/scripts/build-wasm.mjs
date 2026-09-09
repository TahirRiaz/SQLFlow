// Regenerates gui/src/lib/lsp/pkg from the sqlflow-lang-wasm crate.
//
// The generated package (wasm + JS glue + .d.ts) is committed so a GUI-only
// build needs no Rust toolchain, mirroring how tools/sqlflow-vscode commits its
// release binaries. Run this after changing the analysis engine or the wasm
// bindings: `npm run build:wasm` (needs cargo, the wasm32-unknown-unknown
// target, and a wasm-bindgen CLI matching the wasm-bindgen crate version).
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const repoRoot = join(here, "..", "..");
const toolsDir = join(repoRoot, "tools");
const outDir = join(repoRoot, "gui", "src", "lib", "lsp", "pkg");
const wasmFile = join(
  toolsDir,
  "target",
  "wasm32-unknown-unknown",
  "release",
  "sqlflow_lang_wasm.wasm",
);

const run = (cmd, args, cwd) => {
  console.log(`> ${cmd} ${args.join(" ")}`);
  execFileSync(cmd, args, { cwd, stdio: "inherit" });
};

run("cargo", ["build", "-p", "sqlflow-lang-wasm", "--release", "--target", "wasm32-unknown-unknown"], toolsDir);
run("wasm-bindgen", ["--target", "web", "--out-dir", outDir, "--out-name", "sqlflow_lang_wasm", wasmFile], repoRoot);

console.log(`\nGenerated ${outDir}`);
