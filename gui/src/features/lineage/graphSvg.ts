import { NODE_HEIGHT, NODE_WIDTH, type Position } from "./graphLayout.ts";
import type { View, ViewNode } from "./graphModel.ts";
import { clipMiddle } from "./graphText.ts";

function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) =>
    ch === "&" ? "&amp;" : ch === "<" ? "&lt;" : ch === ">" ? "&gt;" : ch === "\"" ? "&quot;" : "&apos;");
}

export interface SvgPalette {
  text: string;
  textMuted: string;
  paper: string;
  border: string;
  edge: string;
}

/**
 * The drawing as a standalone SVG, built from the same nodes and positions the canvas shows (not from the canvas's
 * DOM), so the export is a clean vector that opens in any editor, in the current theme's colours: a card per node
 * with its name and caption, data as pills and folders and stacks with a second edge, and a curve per edge with
 * its arrowhead. Returns "" for an empty drawing.
 */
export function buildLineageSvg(
  view: View,
  positions: ReadonlyMap<string, Position>,
  text: (node: ViewNode) => { title: string; caption: string },
  palette: SvgPalette,
): string {
  if (view.nodes.length === 0) {
    return "";
  }
  const PAD = 48;
  const placed = view.nodes.map((node) => ({ node, at: positions.get(node.id) ?? { x: 0, y: 0 } }));
  const minX = Math.min(...placed.map((entry) => entry.at.x));
  const minY = Math.min(...placed.map((entry) => entry.at.y));
  const width = Math.round(Math.max(...placed.map((entry) => entry.at.x)) - minX + NODE_WIDTH + PAD * 2);
  const height = Math.round(Math.max(...placed.map((entry) => entry.at.y)) - minY + NODE_HEIGHT + PAD * 2);
  const ox = PAD - minX;
  const oy = PAD - minY;
  const at = new Map(placed.map((entry) => [entry.node.id, entry.at]));

  const edges = view.edges.map((edge) => {
    const from = at.get(edge.source);
    const to = at.get(edge.target);
    if (!from || !to) {
      return "";
    }
    const sx = from.x + ox + NODE_WIDTH;
    const sy = from.y + oy + NODE_HEIGHT / 2;
    const tx = to.x + ox;
    const ty = to.y + oy + NODE_HEIGHT / 2;
    const bend = Math.max(40, Math.abs(tx - sx) / 2);
    return `<path d="M${sx},${sy} C${sx + bend},${sy} ${tx - bend},${ty} ${tx},${ty}" fill="none" stroke="${palette.edge}" stroke-width="1.25" marker-end="url(#arrow)"/>`;
  }).join("");

  const nodes = placed.map(({ node, at: position }) => {
    const x = position.x + ox;
    const y = position.y + oy;
    const { title, caption } = text(node);
    const several = node.type === "group" || node.type === "stack";
    const pill = node.memberType === "object" && node.type !== "stack";
    const radius = pill ? NODE_HEIGHT / 2 : 6;
    const shadow = several
      ? `<rect x="${x + 3}" y="${y + 3}" width="${NODE_WIDTH}" height="${NODE_HEIGHT}" rx="${radius}" fill="${palette.border}"/>`
      : "";
    return shadow
      + `<rect x="${x}" y="${y}" width="${NODE_WIDTH}" height="${NODE_HEIGHT}" rx="${radius}" fill="${palette.paper}" stroke="${palette.border}"${pill ? " stroke-dasharray=\"4 3\"" : ""}/>`
      + `<text x="${x + 14}" y="${y + 23}" font-size="12" font-weight="600" fill="${palette.text}">${escapeXml(clipMiddle(title, 30))}</text>`
      + `<text x="${x + 14}" y="${y + 40}" font-size="10.5" fill="${palette.textMuted}">${escapeXml(clipMiddle(caption, 38))}</text>`;
  }).join("");

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" `
    + `font-family="Inter, system-ui, -apple-system, sans-serif">`
    + `<rect x="0" y="0" width="100%" height="100%" fill="${palette.paper}"/>`
    + `<defs><marker id="arrow" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">`
    + `<path d="M0,0 L10,5 L0,10 z" fill="${palette.edge}"/></marker></defs>${edges}${nodes}</svg>`;
}
