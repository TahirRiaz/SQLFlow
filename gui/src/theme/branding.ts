// Reads the design tokens (index.css) off the document, so chart code that needs literal colors (recharts,
// React Flow, SVG exports) is built from the same custom properties the rest of the app's CSS uses:
// index.css stays the single source of truth (DESIGN.md section 3).

const fallbacks: Record<string, string> = {
  // Matches the light block in index.css, used only before the stylesheet is applied (a frame at boot).
  "--primary": "#2264c2",
  "--chart-1": "#2a78d6",
};

export function brandToken(name: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value !== "" ? value : fallbacks[name] ?? "#2264c2";
}

/** The chart series palette: the validated categorical slots (DESIGN.md 3.4), cycled for any series index. */
export function seriesColor(index: number): string {
  return brandToken(`--chart-${(((index % 8) + 8) % 8) + 1}`);
}
