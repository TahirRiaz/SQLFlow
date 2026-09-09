// The one Monaco theme every code surface in the product registers: the single-document viewer (CodeView) and the
// before/after comparison (DiffView) share it, so a script reads identically whichever one is showing it. Colours
// come from the workbench tokens in index.css, read live so the theme follows light/dark (DESIGN.md 7.6).

import type { Monaco } from "@monaco-editor/react";

/** The registered theme name. Both surfaces pass this to Monaco's `theme` prop. */
export const sqlflowEditorTheme = "sqlflow";

const hexPattern = /^#?([0-9a-fA-F]{3,8})$/;
const rgbPattern = /^rgba?\(\s*([\d.]+)[\s,]+([\d.]+)[\s,]+([\d.]+)(?:[\s,/]+([\d.]+%?))?\s*\)$/;

/** A canvas 2d context used only to normalise colour syntax; created once, null where there is no DOM. */
let colorProbe: CanvasRenderingContext2D | null | undefined;

/** Resolves a design token to its current value, honouring light/dark; falls back if unset. */
function cssVar(name: string, fallback: string): string {
  if (typeof document === "undefined") {
    return fallback;
  }
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value || fallback;
}

function byte(value: number): string {
  return Math.max(0, Math.min(255, Math.round(value))).toString(16).padStart(2, "0");
}

/**
 * Normalises whatever the browser reports for a colour (`#rrggbb`, `#rrggbbaa`, `rgb()`, `rgba()`) into the
 * long hex form Monaco demands. Returns null for anything that is not a colour.
 */
function normalizeColor(value: string): string | null {
  const hex = hexPattern.exec(value);
  if (hex) {
    const digits = hex[1];
    // The production stylesheet is minified, which shortens #ffffff to #fff. Monaco's token theme rejects
    // three- and four-digit hex outright ("Illegal value for token color"), so expand it here.
    if (digits.length === 3 || digits.length === 4) {
      return `#${Array.from(digits, (digit) => digit + digit).join("")}`;
    }
    return digits.length === 6 || digits.length === 8 ? `#${digits}` : null;
  }

  const rgb = rgbPattern.exec(value);
  if (rgb) {
    const alpha = rgb[4] === undefined
      ? ""
      : byte(rgb[4].endsWith("%") ? (Number.parseFloat(rgb[4]) / 100) * 255 : Number.parseFloat(rgb[4]) * 255);
    return `#${byte(Number.parseFloat(rgb[1]))}${byte(Number.parseFloat(rgb[2]))}${byte(Number.parseFloat(rgb[3]))}${alpha}`;
  }

  return null;
}

/**
 * Resolves a token written in any other CSS colour syntax (a named colour, `oklch()`, `color-mix()`) by letting
 * the browser parse it: an unparseable value leaves `fillStyle` untouched, so probing from two different
 * starting colours tells a real colour apart from a rejected one.
 */
function probeColor(value: string): string | null {
  if (colorProbe === undefined) {
    colorProbe = typeof document === "undefined" ? null : document.createElement("canvas").getContext("2d");
  }
  if (!colorProbe) {
    return null;
  }

  colorProbe.fillStyle = "#000000";
  colorProbe.fillStyle = value;
  const fromBlack = colorProbe.fillStyle;
  colorProbe.fillStyle = "#ffffff";
  colorProbe.fillStyle = value;
  return colorProbe.fillStyle === fromBlack ? normalizeColor(String(fromBlack)) : null;
}

/**
 * Reads a design token as a Monaco-safe `#rrggbb`/`#rrggbbaa` colour, falling back to the literal when the
 * token is unset or holds something the browser will not parse as a colour.
 */
function themeColor(name: string, fallback: string): string {
  const value = cssVar(name, fallback);
  return normalizeColor(value) ?? probeColor(value) ?? fallback;
}

/**
 * Defines the editor theme from the workbench tokens (index.css is the single colour source, so the code
 * view matches every other surface; DESIGN.md 7.6). Read live via getComputedStyle so it reflects the
 * active mode; re-defined whenever the app toggles light/dark. Rule colours are hex without '#'; editor
 * colours keep it.
 */
export function defineSqlflowTheme(monaco: Monaco, mode: "light" | "dark"): void {
  const color = (name: string, fallback: string) => themeColor(name, fallback);
  const rule = (name: string, fallback: string) => color(name, fallback).replace("#", "");

  monaco.editor.defineTheme(sqlflowEditorTheme, {
    base: mode === "dark" ? "vs-dark" : "vs",
    inherit: true,
    rules: [
      { token: "keyword", foreground: rule("--primary", "#2f6fce"), fontStyle: "bold" },
      { token: "operator", foreground: rule("--muted-foreground", "#5b6b7f") },
      { token: "type", foreground: rule("--info", "#0969da") },
      { token: "predefined", foreground: rule("--info", "#0969da") },
      { token: "string", foreground: rule("--success", "#1a7f37") },
      { token: "number", foreground: rule("--warning", "#9a6700") },
      { token: "comment", foreground: rule("--muted-foreground", "#5b6b7f"), fontStyle: "italic" },
      { token: "delimiter", foreground: rule("--muted-foreground", "#5b6b7f") },
      { token: "tag", foreground: rule("--primary", "#2f6fce") },
      { token: "attribute.name", foreground: rule("--info", "#0969da") },
      // Semantic tokens from the flow-YAML analysis engine (see lib/lsp). These
      // carry census knowledge the YAML grammar cannot: a documented key, a key
      // the loader will ignore, and valid vs invalid enum values.
      { token: "property", foreground: rule("--info", "#0969da") },
      { token: "unknownKey", foreground: rule("--warning", "#9a6700"), fontStyle: "italic" },
      { token: "enumMember", foreground: rule("--success", "#1a7f37") },
      { token: "invalidValue", foreground: rule("--destructive", "#d1242f"), fontStyle: "underline" },
    ],
    colors: {
      "editor.background": color("--card", mode === "dark" ? "#16202f" : "#ffffff"),
      "editor.foreground": color("--foreground", mode === "dark" ? "#dce6f2" : "#1d2733"),
      "editorLineNumber.foreground": color("--muted-foreground", "#5b6b7f"),
      "editorLineNumber.activeForeground": color("--primary", "#2f6fce"),
      "editorCursor.foreground": color("--primary", "#2f6fce"),
      "editorIndentGuide.background": color("--border", "#dfe5ee"),
      "editorGutter.background": color("--card", mode === "dark" ? "#16202f" : "#ffffff"),
      "editor.lineHighlightBorder": "#00000000",
    },
  });
}
