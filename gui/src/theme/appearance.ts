// Puts an appearance (designs.ts) on the page and keeps it in the browser. The design tokens live in the stylesheets,
// keyed on what this sets on <html>: the `dark` class (index.css, DESIGN.md section 3), `data-sf-design` for a module's
// design, and `data-sf-theme`, the mode by name for anything styling off it. index.html puts the remembered appearance on
// before the page first paints, from the same storage keys, so a person never sees another design flash first.

import { DESIGN_STORAGE_KEY, MODE_STORAGE_KEY, designById, type Appearance, type StoredAppearance } from "./designs";

/** The stylesheets put in the page, by URL, so a design worn twice loads its stylesheets once. */
const loadedStylesheets = new Set<string>();

let applied: string | null = null;

/**
 * Puts the appearance on <html>, and the design's own stylesheets in the page the first time it is worn. Call before
 * anything reads tokens for it; putting on the appearance already worn does nothing.
 */
export function applyAppearance(appearance: Appearance): void {
  const key = `${appearance.design}:${appearance.mode}`;
  if (applied === key) {
    return;
  }

  applied = key;
  const root = document.documentElement;
  root.setAttribute("data-sf-theme", appearance.mode);
  root.classList.toggle("dark", appearance.mode === "dark");
  root.setAttribute("data-sf-design", appearance.design);
  for (const href of designById(appearance.design).stylesheets) {
    if (loadedStylesheets.has(href)) {
      continue;
    }

    loadedStylesheets.add(href);
    const link = document.createElement("link");
    link.rel = "stylesheet";
    link.href = href;
    document.head.append(link);
  }
}

/** Whether the operating system prefers dark. */
export function systemPrefersDark(): boolean {
  return window.matchMedia("(prefers-color-scheme: dark)").matches;
}

function read(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Storage the browser refuses (a private window, a blocked site) holds nothing, so the start applies.
    return null;
  }
}

/** What the browser keeps of the appearance a person wore last. */
export function readStoredAppearance(): StoredAppearance {
  return { design: read(DESIGN_STORAGE_KEY), mode: read(MODE_STORAGE_KEY) };
}

/** Keeps the appearance, so the person's next visit puts it on again. */
export function rememberAppearance(appearance: Appearance): void {
  try {
    window.localStorage.setItem(DESIGN_STORAGE_KEY, appearance.design);
    window.localStorage.setItem(MODE_STORAGE_KEY, appearance.mode);
  } catch {
    // Storage the browser refuses keeps nothing: the appearance is still worn for this visit, and the next one starts as
    // a first visit does.
  }
}
