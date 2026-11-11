import type { Orientation } from "./graphLayout.ts";
import type { GraphLevel, TraceDirection } from "./graphModel.ts";

/**
 * The lineage graph's whole state as its query string holds it, so a drawing is a link: what is in scope, how much
 * of it is drawn, and around which node. Reading is forgiving (an unknown or stale value falls back to the default
 * and links written before a parameter existed still open), writing is canonical (defaults are left out).
 */
export interface GraphParams {
  /** The repository in scope. With no project, the whole repository is drawn. */
  repoId: string;
  /** A repository's root folder, narrowing the scope to its flows and what is downstream of them. */
  project: string;
  /** The node a link landed on: selected once the graph holds it, and what is traced until the reader traces another. */
  focus: string;
  /** Frontier nodes the server is asked to walk past. */
  expand: string[];
  /** How much data is drawn; null leaves it to the scope's default. */
  level: GraphLevel | null;
  /** The first and last wave drawn (-1 is the unwaved stop, after every wave); null draws all. */
  waves: { from: number; to: number } | null;
  /** Kind keys the reader hid. */
  hide: string[];
  /** Whether folders are collapsed; null leaves it to the scope's size. */
  grouped: boolean | null;
  /** Folders the reader opened. */
  open: string[];
  /** The node traced; empty traces the focus node, when there is one. */
  trace: string;
  /** Hops drawn around the traced node; null draws the whole trace. */
  hops: number | null;
  direction: TraceDirection;
  stacked: boolean;
  /** Which way data runs on the canvas: top to bottom unless the reader chose left to right. */
  orientation: Orientation;
}

/** A scope with more flows than this opens with its folders collapsed. */
export const AUTO_GROUP_ABOVE = 30;

const LEVELS: readonly GraphLevel[] = ["flows", "handovers", "all", "objects"];

function readWave(text: string): number | null {
  if (text === "u") {
    return -1;
  }
  return /^\d+$/.test(text) ? Number(text) : null;
}

function writeWave(wave: number): string {
  return wave < 0 ? "u" : String(wave);
}

export function readGraphParams(search: URLSearchParams): GraphParams {
  const show = search.get("show");
  let level: GraphLevel | null = LEVELS.includes(show as GraphLevel) ? (show as GraphLevel) : null;
  // Links written when the page had two views named the data-only drawing `view=objects`.
  if (level === null && search.get("view") === "objects") {
    level = "objects";
  }

  let waves: GraphParams["waves"] = null;
  const range = search.get("waves");
  if (range !== null) {
    const [first, last] = range.split("-", 2);
    const from = readWave(first ?? "");
    const to = readWave(last ?? first ?? "");
    if (from !== null && to !== null) {
      waves = { from, to };
    }
  } else {
    // The single wave of the earlier filter.
    const single = readWave(search.get("wave") ?? "");
    if (single !== null) {
      waves = { from: single, to: single };
    }
  }

  const hops = search.get("hops");
  const direction = search.get("dir");
  const group = search.get("group");
  return {
    repoId: search.get("repoId") ?? "",
    project: search.get("project") ?? "",
    focus: search.get("focus") ?? "",
    expand: search.getAll("expand"),
    level,
    waves,
    hide: search.getAll("hide"),
    grouped: group === "on" ? true : group === "off" ? false : null,
    open: search.getAll("open"),
    trace: search.get("trace") ?? "",
    hops: hops !== null && /^[1-9]\d?$/.test(hops) ? Number(hops) : null,
    direction: direction === "up" ? "upstream" : direction === "down" ? "downstream" : "both",
    stacked: search.get("stacks") !== "off",
    orientation: search.get("layout") === "right" ? "right" : "down",
  };
}

export function writeGraphParams(params: GraphParams): URLSearchParams {
  const search = new URLSearchParams();
  const set = (key: string, value: string) => {
    if (value !== "") {
      search.set(key, value);
    }
  };
  set("repoId", params.repoId);
  set("project", params.project);
  set("focus", params.focus);
  for (const token of params.expand) {
    search.append("expand", token);
  }
  set("show", params.level ?? "");
  if (params.waves !== null) {
    search.set("waves", `${writeWave(params.waves.from)}-${writeWave(params.waves.to)}`);
  }
  for (const key of params.hide) {
    search.append("hide", key);
  }
  if (params.grouped !== null) {
    search.set("group", params.grouped ? "on" : "off");
  }
  for (const folder of params.open) {
    search.append("open", folder);
  }
  set("trace", params.trace);
  if (params.hops !== null) {
    search.set("hops", String(params.hops));
  }
  if (params.direction !== "both") {
    search.set("dir", params.direction === "upstream" ? "up" : "down");
  }
  if (!params.stacked) {
    search.set("stacks", "off");
  }
  if (params.orientation === "right") {
    search.set("layout", "right");
  }
  return search;
}

/** What the graph is seeded from. */
export type GraphScope = "none" | "repo" | "project" | "node";

/**
 * A repository draws whole, a project draws its flows and what is downstream of them, and a link onto a node with
 * no repository draws that node's own neighbourhood, which the server walks across every repository.
 */
export function scopeOf(params: Pick<GraphParams, "repoId" | "project" | "focus">): GraphScope {
  if (params.repoId !== "") {
    return params.project !== "" ? "project" : "repo";
  }
  return params.focus !== "" ? "node" : "none";
}

/**
 * The level a scope opens at: a repository or a project opens as its flows alone, the smallest drawing that still
 * tells the order of things, while a node's neighbourhood opens with everything, since it is already small and the
 * node asked for is often data.
 */
export function levelOf(params: Pick<GraphParams, "level" | "repoId" | "project" | "focus">): GraphLevel {
  return params.level ?? (scopeOf(params) === "node" ? "all" : "flows");
}

/** The node a repository's or a project's graph is traced around: the one the reader traced, or the one a link landed on. */
export function tracedNode(params: Pick<GraphParams, "trace" | "repoId" | "project" | "focus">): string {
  if (params.trace !== "") {
    return params.trace;
  }
  const scope = scopeOf(params);
  return scope === "repo" || scope === "project" ? params.focus : "";
}

/** Whether folders are collapsed: as the reader chose, or by the scope's size. */
export function groupedBy(params: Pick<GraphParams, "grouped">, flowCount: number): boolean {
  return params.grouped ?? flowCount > AUTO_GROUP_ABOVE;
}

/** The waves a range draws, given the scope's waves in running order (unwaved last). Null when the range is every wave. */
export function wavesInRange(
  range: GraphParams["waves"],
  stops: readonly number[],
): Set<number> | null {
  if (range === null || stops.length === 0) {
    return null;
  }
  const rank = (wave: number) => (wave < 0 ? Number.MAX_SAFE_INTEGER : wave);
  const low = Math.min(rank(range.from), rank(range.to));
  const high = Math.max(rank(range.from), rank(range.to));
  const drawn = stops.filter((wave) => rank(wave) >= low && rank(wave) <= high);
  // A range naming no wave of this scope (a link from another repository) draws every wave, never an empty canvas.
  return drawn.length === 0 || drawn.length === stops.length ? null : new Set(drawn);
}
