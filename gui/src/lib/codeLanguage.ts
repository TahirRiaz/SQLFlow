/**
 * The languages the code views colour, as the editor names them: the ones the product's own documents are written in
 * (YAML, JSON, SQL) and the others a repository of flows keeps beside them (its notes, scripts and configuration).
 * Anything else shows as plain text.
 */
export type CodeLanguage =
  | "yaml" | "json" | "sql" | "markdown" | "xml" | "html" | "css" | "python" | "powershell" | "shell" | "bat"
  | "javascript" | "typescript" | "csharp" | "ini" | "dockerfile" | "plaintext";

/** A file's extension, lower-cased and without its dot, to the language it is written in. */
const BY_EXTENSION: Readonly<Record<string, CodeLanguage>> = {
  yaml: "yaml", yml: "yaml",
  json: "json", jsonc: "json", geojson: "json", ipynb: "json",
  sql: "sql",
  md: "markdown", markdown: "markdown",
  xml: "xml", xsd: "xml", xsl: "xml", svg: "xml", csproj: "xml", props: "xml", targets: "xml",
  html: "html", htm: "html",
  css: "css",
  py: "python",
  ps1: "powershell", psm1: "powershell", psd1: "powershell",
  sh: "shell", bash: "shell", zsh: "shell",
  bat: "bat", cmd: "bat",
  js: "javascript", mjs: "javascript", cjs: "javascript", jsx: "javascript",
  ts: "typescript", mts: "typescript", cts: "typescript", tsx: "typescript",
  cs: "csharp",
  ini: "ini", cfg: "ini", conf: "ini", toml: "ini", properties: "ini", editorconfig: "ini",
};

/** Whole file names that say their language without an extension. */
const BY_NAME: Readonly<Record<string, CodeLanguage>> = { dockerfile: "dockerfile", makefile: "shell" };

/** The language a repository file is written in, read from its name (`flows/orders.flow.yaml` is YAML); plain text when
 * the name does not say. */
export function languageOfPath(path: string): CodeLanguage {
  const name = path.slice(path.lastIndexOf("/") + 1).toLowerCase();
  const named = BY_NAME[name];
  if (named !== undefined) {
    return named;
  }

  const dot = name.lastIndexOf(".");
  return dot < 0 ? "plaintext" : BY_EXTENSION[name.slice(dot + 1)] ?? "plaintext";
}
