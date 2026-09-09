import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import {
  Background,
  Controls,
  MarkerType,
  MiniMap,
  Panel,
  ReactFlow,
  ReactFlowProvider,
  useNodesState,
  useReactFlow,
  type Edge as FlowEdge,
  type Node as FlowNode,
} from "@xyflow/react";
import { CheckIcon, ChevronRight, ChevronsUpDown, Download, Info, Loader2, Rows3 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { Separator } from "@/components/ui/separator";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { lineageApi, searchApi } from "../../api/endpoints";
import type { LineageProject, RunScope, WavePipeline } from "../../api/types";
import { CodeView } from "../../components/CodeView";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { rememberGraphSearch } from "./graphLocation";
import { TriggerRunDialog } from "../runs/TriggerRunDialog";
import { seriesColor } from "../../theme/branding";
import { useThemeMode } from "../../theme/ThemeModeContext";
import "@xyflow/react/dist/style.css";

const NODE_WIDTH = 200;
const NODE_HEIGHT = 56;

/** The node-key prefix every data subscriber carries (subscribers live on a synthetic server identity), so a
 * consuming leaf can be told from a database object without a second lookup. */
const SUBSCRIBER_KEY_PREFIX = "subscriber|";
const LAYER_SPACING = 150; // vertical gap between layers (rows, top to bottom); clears NODE_HEIGHT + edge/label room
const NODE_SPACING = 260;  // horizontal gap between nodes within a layer; clears NODE_WIDTH

type GraphView = "flows" | "objects";

/**
 * Reads a semantic design token's current value off the document root, so graph internals that need literal
 * colors (React Flow inline styles, the SVG export) stay in lockstep with index.css in both themes.
 */
function cssColor(name: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  return value !== "" ? value : fallback;
}

/** The literal accent colors the graph paints with, resolved from the semantic tokens for the active theme. */
interface GraphAccents {
  /** The focused node's fill and ring (`--primary`). */
  focus: string;
  /** Upstream (ancestor) highlight, the violet chart slot. */
  upstream: string;
  /** Downstream (descendant) highlight, the green chart slot. */
  downstream: string;
  /** The dashed outline marking cross-repo flows and object nodes. */
  outline: string;
}

function readAccents(): GraphAccents {
  return {
    focus: cssColor("--primary", "#2f6fce"),
    upstream: cssColor("--chart-7", "#4a3aa7"),
    downstream: cssColor("--chart-2", "#008300"),
    outline: cssColor("--muted-foreground", "#5b6b7f"),
  };
}

function truncate(value: string, max: number): string {
  return value.length > max ? `${value.slice(0, max)}...` : value;
}

/**
 * A top-to-bottom layered (Sugiyama) layout - the layout that makes data flow readable: sources at the top,
 * consumers below, one row per level. Levels come from a longest-path layering over the lineage edges, with
 * cycles broken first (a DFS drops the edges that close a cycle from the level calculation, so a flow that
 * reads a view derived from its own output still gets a real level instead of collapsing onto the top row).
 * Within each layer, several barycenter passes order nodes by the average position of their neighbours to
 * minimize edge crossings. Ported from the DeltaForge forge-graph hierarchical layout; independent of any
 * layout library so the level alignment is exact.
 */
function layeredLayout(nodes: FlowNode[], edges: FlowEdge[]): FlowNode[] {
  if (nodes.length === 0) {
    return nodes;
  }

  const ids = new Set(nodes.map((n) => n.id));
  const outgoing = new Map<string, string[]>();
  const incoming = new Map<string, string[]>();
  for (const id of ids) {
    outgoing.set(id, []);
    incoming.set(id, []);
  }
  for (const edge of edges) {
    if (ids.has(edge.source) && ids.has(edge.target) && edge.source !== edge.target) {
      outgoing.get(edge.source)!.push(edge.target);
      incoming.get(edge.target)!.push(edge.source);
    }
  }

  // De-duplicated adjacency for the level calculation: the objects view records the same source->target pair
  // once per flow, and a duplicate edge must not be mistaken for a cycle by the DFS below.
  const uniqueOut = new Map<string, string[]>();
  const layerInDegree = new Map<string, number>();
  for (const id of ids) {
    uniqueOut.set(id, []);
    layerInDegree.set(id, 0);
  }
  for (const [source, targets] of outgoing) {
    const seen = new Set<string>();
    for (const target of targets) {
      if (!seen.has(target)) {
        seen.add(target);
        uniqueOut.get(source)!.push(target);
        layerInDegree.set(target, layerInDegree.get(target)! + 1);
      }
    }
  }

  // ---- Layer assignment: longest path, with cycles broken first. --------------------------------------------
  // A cyclic subgraph (e.g. a flow that reads the view derived from its own output table) would leave Kahn's
  // queue undrained and pin every node of the cycle to the top row. A DFS therefore marks each edge that closes
  // back onto its own stack; those edges are excluded from the level calculation (they still draw, as upward
  // curves), so the remaining graph is a DAG and every node gets a real level. Nodes with the fewest inbound
  // edges are visited first so cycles break in the natural flow direction.
  const backTargets = new Map<string, Set<string>>();
  const state = new Map<string, number>(); // 1 = on the DFS stack, 2 = finished
  const roots = [...ids].sort((a, b) => layerInDegree.get(a)! - layerInDegree.get(b)!);
  for (const root of roots) {
    if (state.has(root)) {
      continue;
    }
    const stack: Array<{ id: string; next: number }> = [{ id: root, next: 0 }];
    state.set(root, 1);
    while (stack.length > 0) {
      const frame = stack[stack.length - 1];
      const children = uniqueOut.get(frame.id)!;
      if (frame.next < children.length) {
        const child = children[frame.next];
        frame.next += 1;
        const childState = state.get(child);
        if (childState === 1) {
          (backTargets.get(frame.id) ?? backTargets.set(frame.id, new Set()).get(frame.id)!).add(child);
          layerInDegree.set(child, layerInDegree.get(child)! - 1);
        } else if (childState === undefined) {
          state.set(child, 1);
          stack.push({ id: child, next: 0 });
        }
      } else {
        state.set(frame.id, 2);
        stack.pop();
      }
    }
  }

  // Longest-path layering over the acyclic remainder: a node sits one row below its deepest producer, so the
  // levels are consecutive from 0 and every row reads top to bottom in dependency order.
  const layer = new Map<string, number>();
  const queue: string[] = [];
  for (const id of ids) {
    if (layerInDegree.get(id) === 0) {
      layer.set(id, 0);
      queue.push(id);
    }
  }
  for (let head = 0; head < queue.length; head++) {
    const id = queue[head];
    const level = layer.get(id)!;
    for (const child of uniqueOut.get(id)!) {
      if (backTargets.get(id)?.has(child)) {
        continue;
      }
      layer.set(child, Math.max(layer.get(child) ?? 0, level + 1));
      const rem = layerInDegree.get(child)! - 1;
      layerInDegree.set(child, rem);
      if (rem === 0) {
        queue.push(child);
      }
    }
  }

  // ALAP relaxation: slide every node down to one row above its EARLIEST consumer. Longest-path places each
  // node as early as possible, which strands a slack chain (a manually maintained input, its loader, its dim
  // table) at the top of the drawing even when its only consumers sit rows below. Walking the topological
  // order in reverse (consumers finalized first) and only ever moving a node DOWN keeps every producer above
  // its consumers while dense chains stay put; a sink or an isolated node has no successors and never moves.
  for (let i = queue.length - 1; i >= 0; i--) {
    const id = queue[i];
    const back = backTargets.get(id);
    let minChild = Number.POSITIVE_INFINITY;
    for (const child of uniqueOut.get(id)!) {
      if (!back?.has(child)) {
        minChild = Math.min(minChild, layer.get(child)!);
      }
    }
    if (Number.isFinite(minChild) && minChild - 1 > layer.get(id)!) {
      layer.set(id, minChild - 1);
    }
  }

  const groups = new Map<number, string[]>();
  for (const id of ids) {
    const level = layer.get(id)!;
    (groups.get(level) ?? groups.set(level, []).get(level)!).push(id);
  }
  const layers = [...groups.keys()].sort((a, b) => a - b);

  // ---- Crossing minimization: order each layer by the barycenter of its neighbours, both directions. --------
  const order = new Map<string, number>();
  for (const level of layers) {
    const group = groups.get(level)!;
    group.sort((a, b) =>
      outgoing.get(b)!.length + incoming.get(b)!.length - (outgoing.get(a)!.length + incoming.get(a)!.length));
    group.forEach((id, i) => order.set(id, i));
  }

  const barycenter = (id: string, neighbours: Map<string, string[]>, neighbourLevel: number): number => {
    const relevant = neighbours.get(id)!.filter((n) => layer.get(n) === neighbourLevel);
    if (relevant.length === 0) {
      return order.get(id) ?? 0;
    }
    return relevant.reduce((sum, n) => sum + (order.get(n) ?? 0), 0) / relevant.length;
  };

  for (let pass = 0; pass < 6; pass++) {
    for (let i = 1; i < layers.length; i++) {
      const group = groups.get(layers[i])!;
      group.sort((a, b) => barycenter(a, incoming, layers[i - 1]) - barycenter(b, incoming, layers[i - 1]));
      group.forEach((id, j) => order.set(id, j));
    }
    for (let i = layers.length - 2; i >= 0; i--) {
      const group = groups.get(layers[i])!;
      group.sort((a, b) => barycenter(a, outgoing, layers[i + 1]) - barycenter(b, outgoing, layers[i + 1]));
      group.forEach((id, j) => order.set(id, j));
    }
  }

  // ---- Positions (top-to-bottom): y by layer, x by order within the layer, each layer horizontally centered. -
  const position = new Map<string, { x: number; y: number }>();
  for (const level of layers) {
    const group = groups.get(level)!;
    const width = (group.length - 1) * NODE_SPACING;
    group.forEach((id, i) => {
      position.set(id, { x: i * NODE_SPACING - width / 2, y: level * LAYER_SPACING });
    });
  }

  return nodes.map((node) => ({ ...node, position: position.get(node.id) ?? { x: 0, y: 0 } }));
}

/** A built graph plus the indexes navigation needs: display names for search, per-flow colors for the legend,
 * and directed adjacency for the upstream/downstream focus traversal. */
interface BuiltGraph {
  nodes: FlowNode[];
  edges: FlowEdge[];
  names: Map<string, string>;
  flowColors: Map<string, string>;
  incoming: Map<string, string[]>;
  outgoing: Map<string, string[]>;
  /** Object keys at the depth-capped frontier with un-included downstream consumers; a client can expand these. */
  frontier: Set<string>;
  /** Pipeline id -> its repo id, so repo-aware actions (only a seed-repo flow can be run from here) can be gated. */
  repoOf: Map<string, string>;
  /** How to open the selected node elsewhere in the app. */
  openTarget: (id: string) => { label: string; to: string };
}

/** The BFS closure behind the focus mode: everything upstream (ancestors) and downstream (descendants). */
function reachability(start: string, adjacency: Map<string, string[]>): Set<string> {
  const seen = new Set<string>();
  const queue = [start];
  while (queue.length > 0) {
    const current = queue.pop()!;
    for (const next of adjacency.get(current) ?? []) {
      if (!seen.has(next)) {
        seen.add(next);
        queue.push(next);
      }
    }
  }
  seen.delete(start);
  return seen;
}

function addAdjacency(map: Map<string, string[]>, from: string, to: string): void {
  const list = map.get(from);
  if (list) {
    list.push(to);
  } else {
    map.set(from, [to]);
  }
}

function escapeXml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) =>
    ch === "&" ? "&amp;" : ch === "<" ? "&lt;" : ch === ">" ? "&gt;" : ch === "\"" ? "&quot;" : "&apos;");
}

/**
 * Serialize the built graph to a standalone SVG from the computed layout: rounded node cards with a colored accent
 * bar, bezier edges with arrowheads and relation labels. Built from the graph model rather than React Flow's DOM,
 * so the export is a clean vector (no HTML foreignObject) that opens in any SVG editor, in the current theme's
 * colors. Returns "" for an empty graph.
 */
function buildLineageSvg(
  graph: BuiltGraph,
  palette: { text: string; textMuted: string; paper: string; border: string },
): string {
  if (graph.nodes.length === 0) {
    return "";
  }
  const PAD = 48;
  const xs = graph.nodes.map((node) => node.position.x);
  const ys = graph.nodes.map((node) => node.position.y);
  const minX = Math.min(...xs);
  const minY = Math.min(...ys);
  const width = Math.round(Math.max(...xs) - minX + NODE_WIDTH + PAD * 2);
  const height = Math.round(Math.max(...ys) - minY + NODE_HEIGHT + PAD * 2);
  const ox = PAD - minX;
  const oy = PAD - minY;

  const nodeById = new Map(graph.nodes.map((node) => [node.id, node]));
  const strokeOf = (color: unknown) => (typeof color === "string" ? color : palette.border);
  const markerId = (color: string) =>
    `arrow-${[...color].reduce((hash, ch) => (hash * 31 + ch.charCodeAt(0)) >>> 0, 0).toString(36)}`;

  const colors = new Set<string>();
  for (const edge of graph.edges) {
    colors.add(strokeOf(edge.style?.stroke));
  }
  const defs = [...colors]
    .map((color) =>
      `<marker id="${markerId(color)}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse">`
      + `<path d="M0,0 L10,5 L0,10 z" fill="${color}"/></marker>`)
    .join("");

  const edgeSvg = graph.edges.map((edge) => {
    const from = nodeById.get(edge.source);
    const to = nodeById.get(edge.target);
    if (!from || !to) {
      return "";
    }
    const stroke = strokeOf(edge.style?.stroke);
    const sx = from.position.x + ox + NODE_WIDTH / 2;
    const sy = from.position.y + oy + NODE_HEIGHT;
    const tx = to.position.x + ox + NODE_WIDTH / 2;
    const ty = to.position.y + oy;
    const my = (sy + ty) / 2;
    const path = `<path d="M${sx},${sy} C${sx},${my} ${tx},${my} ${tx},${ty}" fill="none" stroke="${stroke}" stroke-width="1.5" marker-end="url(#${markerId(stroke)})"/>`;
    const label = typeof edge.label === "string" ? edge.label : "";
    if (label === "") {
      return path;
    }
    const lx = (sx + tx) / 2;
    const boxWidth = label.length * 6.5 + 12;
    return path
      + `<rect x="${lx - boxWidth / 2}" y="${my - 9}" width="${boxWidth}" height="18" rx="4" fill="${palette.paper}" stroke="${palette.border}"/>`
      + `<text x="${lx}" y="${my + 4}" text-anchor="middle" font-size="11" fill="${palette.textMuted}">${escapeXml(label)}</text>`;
  }).join("");

  const nodeSvg = graph.nodes.map((node) => {
    const x = node.position.x + ox;
    const y = node.position.y + oy;
    const accent = graph.flowColors.get(node.id) ?? palette.border;
    const name = graph.names.get(node.id) ?? node.id;
    const label = name.length > 26 ? `${name.slice(0, 25)}…` : name;
    return `<rect x="${x}" y="${y}" width="${NODE_WIDTH}" height="${NODE_HEIGHT}" rx="8" fill="${palette.paper}" stroke="${palette.border}"/>`
      + `<rect x="${x}" y="${y}" width="4" height="${NODE_HEIGHT}" rx="2" fill="${accent}"/>`
      + `<text x="${x + 14}" y="${y + NODE_HEIGHT / 2 + 4}" font-size="13" font-weight="600" fill="${palette.text}">${escapeXml(label)}</text>`;
  }).join("");

  return `<svg xmlns="http://www.w3.org/2000/svg" width="${width}" height="${height}" viewBox="0 0 ${width} ${height}" `
    + `font-family="Inter, system-ui, -apple-system, sans-serif">`
    + `<rect x="0" y="0" width="100%" height="100%" fill="${palette.paper}"/>`
    + `<defs>${defs}</defs>${edgeSvg}${nodeSvg}</svg>`;
}

/** The two-line label inside a graph node: name over a muted caption, both truncated to the card. */
function NodeLabel({ name, caption }: { name: string; caption: string }) {
  return (
    <div className="overflow-hidden text-left">
      <div className="truncate text-[13px] font-semibold">{name}</div>
      <div className="truncate text-[11px] text-muted-foreground">{caption}</div>
    </div>
  );
}

interface FocusState {
  id: string;
  upstream: Set<string>;
  downstream: Set<string>;
}

interface CanvasProps {
  graph: BuiltGraph;
  focus: FocusState | null;
  colorMode: "light" | "dark";
  accents: GraphAccents;
  /** Bumps when the node search picks a node, so the canvas centers on it. */
  centerRequest: { id: string; nonce: number; zoom?: number } | null;
  onFocus: (id: string | null) => void;
  onOpen: (id: string) => void;
  /** Right-click on a node: opens the node context menu at the pointer. */
  onNodeContextMenu: (id: string, position: { x: number; y: number }) => void;
  /** Overlay content rendered inside the flow pane (the floating details panel). */
  children?: ReactNode;
}

/**
 * The canvas: positions live in React Flow state (dragging works, layout re-seeds on data change), while the
 * focus styling is applied in place so a focus change never resets positions the user arranged. Single click
 * focuses (upstream/downstream highlight); double click opens the node's page; clicking the pane clears focus.
 */
function GraphCanvas({ graph, focus, colorMode, accents, centerRequest, onFocus, onOpen, onNodeContextMenu, children }: CanvasProps) {
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState(graph.nodes);
  const view = useReactFlow();

  useEffect(() => {
    setFlowNodes(graph.nodes);
  }, [graph, setFlowNodes]);

  useEffect(() => {
    if (centerRequest === null) {
      return;
    }
    const node = view.getNodes().find((candidate) => candidate.id === centerRequest.id);
    if (node) {
      void view.setCenter(node.position.x + NODE_WIDTH / 2, node.position.y + NODE_HEIGHT / 2, {
        // A deep-link jump requests a firm zoom so the landed-on node dominates the view; the in-graph search and
        // wave chips omit it and keep the user's current zoom (never zooming out below 1).
        zoom: centerRequest.zoom ?? Math.max(view.getZoom(), 1),
        duration: 500,
      });
    }
  }, [centerRequest, view]);

  const styledNodes = useMemo(() => {
    if (focus === null) {
      return flowNodes.map((node) => ({
        ...node,
        className: undefined,
        style: { ...node.style, opacity: 1, boxShadow: undefined, zIndex: undefined },
      }));
    }
    return flowNodes.map((node) => {
      const isFocus = node.id === focus.id;
      const isUpstream = focus.upstream.has(node.id);
      const isDownstream = focus.downstream.has(node.id);
      const related = isFocus || isUpstream || isDownstream;
      const accent = isFocus ? accents.focus : isUpstream ? accents.upstream : accents.downstream;
      return {
        ...node,
        // The focused node gets a bold ring + outer glow (and the one-shot pulse via the class) so it clearly
        // stands out from its upstream/downstream neighbours; related nodes get a thin accent ring; the rest dim.
        className: isFocus ? "sf-focus-node" : undefined,
        style: {
          ...node.style,
          opacity: related ? 1 : 0.15,
          zIndex: isFocus ? 10 : undefined,
          // The focused node is filled solid with the primary color (its label text is forced to the contrast
          // color via the .sf-focus-node rule) plus a ring and outer glow, so it is unmistakable; related nodes
          // keep a thin accent ring, the rest dim.
          backgroundColor: isFocus ? accent : undefined,
          boxShadow: isFocus
            ? `0 0 0 3px ${accent}, 0 0 18px 5px ${accent}`
            : related ? `0 0 0 2px ${accent}` : undefined,
        },
      };
    });
  }, [flowNodes, focus, accents]);

  const styledEdges = useMemo(() => {
    if (focus === null) {
      return graph.edges;
    }
    const closure = new Set<string>([focus.id, ...focus.upstream, ...focus.downstream]);
    return graph.edges.map((edge) => {
      const related = closure.has(edge.source) && closure.has(edge.target);
      return {
        ...edge,
        style: { ...edge.style, opacity: related ? 1 : 0.08 },
        labelStyle: { ...edge.labelStyle, opacity: related ? 1 : 0.08 },
      };
    });
  }, [graph.edges, focus]);

  return (
    <ReactFlow
      nodes={styledNodes}
      edges={styledEdges}
      onNodesChange={onNodesChange}
      onNodeClick={(_, node) => onFocus(node.id)}
      onNodeDoubleClick={(_, node) => onOpen(node.id)}
      onNodeContextMenu={(event, node) => {
        event.preventDefault();
        onNodeContextMenu(node.id, { x: event.clientX, y: event.clientY });
      }}
      onPaneClick={() => onFocus(null)}
      colorMode={colorMode}
      fitView
      minZoom={0.05}
      nodesConnectable={false}
      nodesDraggable
    >
      <Background />
      <Controls />
      <MiniMap pannable zoomable />
      {children}
    </ReactFlow>
  );
}

/** Local debounce so the catalog search fires after typing pauses, not per keystroke. */
function useDebounced(value: string, delayMs: number): string {
  const [debounced, setDebounced] = useState(value);
  useEffect(() => {
    const handle = window.setTimeout(() => setDebounced(value), delayMs);
    return () => window.clearTimeout(handle);
  }, [value, delayMs]);
  return debounced;
}

/**
 * The in-toolbar search, over two scopes at once: the drawn graph and the whole catalog. Matching only the drawn
 * nodes would make the box useless for the thing people actually want - finding a table or a flow they cannot see,
 * because the canvas holds one project and the estate holds thousands of objects. So every query also runs against
 * the catalog's object and flow search. Picking a drawn node focuses and centers it in place; picking a catalog hit
 * re-seeds the graph on that node (via ?focus=), which draws its own upstream/downstream context. cmdk's own
 * filtering is off: the catalog hits are already server-ranked and must not be re-filtered away client side.
 * Options carry role="option" for accessibility.
 */
function GraphSearch({ options, onPickNode, onPickCatalog }: {
  options: { id: string; name: string }[];
  onPickNode: (id: string) => void;
  onPickCatalog: (id: string, repoId?: string) => void;
}) {
  const [query, setQuery] = useState("");
  const trimmed = query.trim();
  const debounced = useDebounced(trimmed, 300);
  const open = trimmed !== "";
  // One character matches most of the estate, so the catalog call waits for a term that can actually narrow.
  const catalogEnabled = debounced.length >= 2;

  const drawn = useMemo(() => new Set(options.map((option) => option.id)), [options]);
  const localMatches = useMemo(() => {
    const needle = trimmed.toLowerCase();
    return needle === ""
      ? []
      : options.filter((option) => option.name.toLowerCase().includes(needle)).slice(0, 20);
  }, [options, trimmed]);

  const objectsQuery = useQuery({
    queryKey: ["lineage-search-objects", debounced],
    enabled: catalogEnabled,
    queryFn: () => searchApi.objects(debounced, { pageSize: 8 }),
  });
  const flowsQuery = useQuery({
    queryKey: ["lineage-search-flows", debounced],
    enabled: catalogEnabled,
    queryFn: () => searchApi.flows(debounced, { pageSize: 8 }),
  });

  // A catalog hit already on the canvas is dropped: the in-graph group above focuses it without a refetch, and
  // offering the same node twice would only invite a pointless re-seed.
  const objectHits = (objectsQuery.data?.items ?? []).filter((hit) => !drawn.has(hit.key));
  const flowHits = (flowsQuery.data?.items ?? []).filter((hit) => !drawn.has(hit.id));
  const searching = catalogEnabled && (objectsQuery.isFetching || flowsQuery.isFetching);
  const catalogError = objectsQuery.isError || flowsQuery.isError;
  const empty = localMatches.length === 0 && objectHits.length === 0 && flowHits.length === 0;

  const pickNode = (id: string) => {
    setQuery("");
    onPickNode(id);
  };
  const pickCatalog = (id: string, repoId?: string) => {
    setQuery("");
    onPickCatalog(id, repoId);
  };

  return (
    <Command
      shouldFilter={false}
      className="relative w-64 overflow-visible rounded-md border border-input bg-transparent **:data-[slot=command-input-wrapper]:h-8 **:data-[slot=command-input-wrapper]:border-b-0"
    >
      <CommandInput
        value={query}
        onValueChange={setQuery}
        placeholder="Search objects and flows"
        aria-label="Search objects and flows"
        data-testid="graph-node-search"
        className="h-8 py-0 text-[13px]"
      />
      {open && (
        <CommandList
          className="absolute top-full left-0 z-20 mt-1 max-h-96 w-96 overflow-y-auto rounded-md border border-border bg-popover shadow-md"
          data-testid="graph-search-results"
          // Keep the input focused while an option is clicked, so the list is not dismissed mid-click.
          onMouseDown={(event) => event.preventDefault()}
        >
          {localMatches.length > 0 && (
            <CommandGroup heading="In this graph">
              {localMatches.map((option) => (
                <CommandItem
                  key={option.id}
                  value={option.id}
                  onSelect={() => pickNode(option.id)}
                  className="font-mono text-[12px]"
                >
                  {option.name}
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {objectHits.length > 0 && (
            <CommandGroup heading="Objects in the catalog">
              {objectHits.map((hit) => (
                <CommandItem
                  key={hit.key}
                  value={hit.key}
                  onSelect={() => pickCatalog(hit.key)}
                  data-testid="graph-search-object"
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate font-mono text-[12px]">{hit.name}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {[hit.kind, hit.database ?? hit.serverRef, hit.schema].filter((part) => part !== null && part !== "").join(" · ")}
                    </div>
                  </div>
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {flowHits.length > 0 && (
            <CommandGroup heading="Flows in the catalog">
              {flowHits.map((hit) => (
                <CommandItem
                  key={hit.id}
                  value={hit.id}
                  onSelect={() => pickCatalog(hit.id, hit.repoId)}
                  data-testid="graph-search-flow"
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate font-mono text-[12px]">{hit.name}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {hit.kind} · {hit.repoName}
                    </div>
                  </div>
                </CommandItem>
              ))}
            </CommandGroup>
          )}
          {empty && (
            <div className="px-3 py-3 text-[13px] text-muted-foreground" data-testid="graph-search-status">
              {searching
                ? "Searching the catalog..."
                : catalogError
                  ? "The catalog search failed. Retry the query."
                  : catalogEnabled
                    ? "Nothing in the graph or the catalog matches."
                    : "Type at least two characters to search the catalog."}
            </div>
          )}
        </CommandList>
      )}
    </Command>
  );
}

/**
 * The searchable (repo, project) scope picker: a combobox trigger showing the current selection, with the
 * full project list (repo, flow count) filterable in a popover. Clearing returns to the pick-a-project state.
 */
function ProjectSelect({ items, selected, onSelect }: {
  items: LineageProject[];
  selected: LineageProject | null;
  onSelect: (option: LineageProject | null) => void;
}) {
  const [open, setOpen] = useState(false);
  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label="Select a project"
          data-testid="graph-project-select"
          className="h-8 min-w-[240px] justify-between font-normal"
        >
          {selected !== null
            ? <span className="truncate">{selected.repoName} / {selected.project}</span>
            : <span className="text-muted-foreground">Select a project</span>}
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="end" className="w-80 p-0">
        <Command>
          <CommandInput placeholder="Search projects" />
          <CommandList>
            <CommandEmpty>No projects found.</CommandEmpty>
            {selected !== null && (
              <CommandItem
                value="clear-project-selection"
                onSelect={() => {
                  setOpen(false);
                  onSelect(null);
                }}
                className="text-muted-foreground"
              >
                Clear selection
              </CommandItem>
            )}
            {items.map((option) => {
              const isSelected = selected !== null
                && selected.repoId === option.repoId && selected.project === option.project;
              return (
                <CommandItem
                  key={`${option.repoId}:${option.project}`}
                  value={`${option.repoName} / ${option.project}`}
                  onSelect={() => {
                    setOpen(false);
                    onSelect(option);
                  }}
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate text-[13px]">{option.project}</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {option.repoName} · {option.flowCount} {option.flowCount === 1 ? "flow" : "flows"}
                    </div>
                  </div>
                  {isSelected && <CheckIcon className="shrink-0" />}
                </CommandItem>
              );
            })}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}

/** A legend/wave chip: secondary pill with the flow's series color as a left accent bar. */
const chipClass = "inline-flex max-w-full items-center truncate rounded-sm bg-secondary py-0.5 pl-1.5 pr-2 font-mono text-[11px] text-secondary-foreground";

/**
 * The repo-scoped lineage graph, built for two things above all: a tree-based (layered, top-to-bottom) layout
 * that reads as data flow, and navigation: click to focus a node and light up everything upstream (purple) and
 * downstream (green) of it, search to jump to a node, double click (or the panel button) to open it. Two views
 * share the canvas: Flows (pipelines AND the physical objects they read/write, so a table is a node between its
 * producer and consumers) and Objects (tables/files connected object-to-object by the flows that move data
 * between them, edges colored per flow). Deep-linkable via ?repoId= and ?view=.
 */
export default function LineageGraphPage() {
  const navigate = useNavigate();
  const { mode } = useThemeMode();
  const [searchParams, setSearchParams] = useSearchParams();
  const repoId = searchParams.get("repoId") ?? "";
  const project = searchParams.get("project") ?? "";
  const graphView: GraphView = searchParams.get("view") === "objects" ? "objects" : "flows";
  // A deep-link (from search) can target a node to focus: an object key or a pipeline id. It also seeds the graph:
  // rather than a separate whole-repo drawing path, the focused node is passed as an expand seed so the same
  // project-graph closure draws that node's local upstream/downstream context.
  const focusParam = searchParams.get("focus") ?? "";
  // Frontier nodes the user expanded, held in the URL so the deeper graph is deep-linkable and survives a reload.
  const expand = useMemo(() => searchParams.getAll("expand"), [searchParams]);
  const [focus, setFocus] = useState<FocusState | null>(null);
  const [centerRequest, setCenterRequest] = useState<{ id: string; nonce: number; zoom?: number } | null>(null);
  const [panelOpen, setPanelOpen] = useState(true);
  // Right-click context menu on a node, and the node whose script is open in the sheet. A flow node has two
  // scripts: its authored YAML (the default) and, for an sp flow, the SQL of the procedure it executes
  // (view "object"); an object node has only its SQL.
  const [nodeMenu, setNodeMenu] = useState<{ id: string; x: number; y: number } | null>(null);
  const [scriptTarget, setScriptTarget] = useState<{ key: string; view?: "object" } | null>(null);
  // A flow node's Run action opens the trigger dialog prefilled with that flow and the chosen scope.
  const [runDialog, setRunDialog] = useState<{ flowName: string; scope: RunScope } | null>(null);

  // The literal accent colors, re-read when the theme flips so inline node styles track the tokens.
  const accents = useMemo(() => readAccents(), [mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const scriptQuery = useQuery({
    queryKey: ["lineage-node-script", scriptTarget?.key, scriptTarget?.view ?? ""],
    queryFn: () => {
      const target = scriptTarget as { key: string; view?: "object" };
      return lineageApi.script(target.key, target.view);
    },
    enabled: scriptTarget !== null,
  });

  // Every (repo, project) pair, for the searchable scope picker. The graph is seeded from a project, not a repo.
  const projectsQuery = useQuery({
    queryKey: ["lineage-projects"],
    queryFn: () => lineageApi.projects(),
  });

  // A deep-link onto a node (an object key or pipeline id) seeds the graph on that node when no project is chosen,
  // so a search jump into lineage draws the node's own closure via the same endpoint. A pipeline deep-link may carry
  // its repoId too; that is fine (it only scopes an unused project lookup), the focus node still drives the graph.
  const focusSeed = project === "" && focusParam !== "" ? focusParam : "";
  const activeExpand = useMemo(
    () => (focusSeed === "" ? expand : [focusSeed, ...expand]),
    [focusSeed, expand],
  );
  const graphEnabled = (repoId !== "" && project !== "") || focusSeed !== "";

  // The graph rewrites its query string in place (replace: true), so history holds nothing to go back to. Remember
  // the drawing's parameters while a scope is set, and forget them once it is cleared, so the object explorer's
  // "Graph view" button returns to this same drawing instead of the bare landing.
  const graphSearch = searchParams.toString();
  useEffect(() => {
    rememberGraphSearch(graphEnabled ? graphSearch : "");
  }, [graphEnabled, graphSearch]);

  // The one graph query: a project's cross-repo downstream closure (or a focused node's local context). Both the
  // Flows and Objects views build from this single payload; the walk crosses repos freely via global object keys.
  const projectGraph = useQuery({
    queryKey: ["lineage-project-graph", repoId, project, activeExpand.join("")],
    enabled: graphEnabled,
    queryFn: () => lineageApi.projectGraph({
      repoId: repoId || undefined,
      project: project || undefined,
      expand: activeExpand,
    }),
  });

  const pipelines = projectGraph.data?.pipelines;
  const graphObjects = projectGraph.data?.objects;
  const flowGraphEdges = projectGraph.data?.flowGraph;
  const objectGraphEdges = projectGraph.data?.objectGraph;
  const frontierSet = useMemo(() => new Set(projectGraph.data?.frontier ?? []), [projectGraph.data]);

  // Each flow node's kind, for the script actions: every flow offers its YAML, and an sp flow additionally
  // offers the SQL of the procedure it executes. Object keys never collide with pipeline ids (GUIDs), so this
  // map also answers "is this node a flow".
  const flowKindById = useMemo(() => {
    const map = new Map<string, string>();
    for (const pipeline of pipelines ?? []) {
      map.set(pipeline.id, pipeline.kind);
    }
    return map;
  }, [pipelines]);

  const setParam = useCallback((key: string, value: string) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (value === "") {
        next.delete(key);
      } else {
        next.set(key, value);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  // Pick a (repo, project) scope: set both params together and drop any prior focus/expand so the new project draws
  // from its own seed rather than inheriting the previous graph's node expansions.
  const selectProject = useCallback((option: LineageProject | null) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      next.delete("focus");
      next.delete("expand");
      if (option === null) {
        next.delete("repoId");
        next.delete("project");
      } else {
        next.set("repoId", option.repoId);
        next.set("project", option.project);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  // Expand a frontier object: append it to ?expand= so the walk continues past it, deep-linkably.
  const expandNode = useCallback((id: string) => {
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      if (!next.getAll("expand").includes(id)) {
        next.append("expand", id);
      }
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  const projectItems = projectsQuery.data ?? [];
  const selectedProject = useMemo(
    () => projectItems.find((item) => item.repoId === repoId && item.project === project) ?? null,
    [projectItems, repoId, project],
  );

  // The waves for the flows view's batch filter and details panel: the closure's pipelines grouped by their own
  // execution wave. Waves are a per-repo plan, so across repos a shared "Wave N" only groups flows that each sit at
  // that wave in their own repo; it stays a useful batch label without pretending the number is estate-global.
  const sortedWaves = useMemo(() => {
    if (!pipelines) {
      return [] as { wave: number; pipelines: WavePipeline[] }[];
    }
    const byWave = new Map<number, WavePipeline[]>();
    for (const pipeline of pipelines) {
      const list = byWave.get(pipeline.wave) ?? byWave.set(pipeline.wave, []).get(pipeline.wave)!;
      list.push({ id: pipeline.id, name: pipeline.name, kind: pipeline.kind });
    }
    const rank = (wave: number) => (wave === -1 ? Number.MAX_SAFE_INTEGER : wave);
    return [...byWave.entries()]
      .map(([wave, group]) => ({ wave, pipelines: group }))
      .sort((a, b) => rank(a.wave) - rank(b.wave));
  }, [pipelines]);

  // The wave (batch) top filter, read from ?wave= and validated against the repo's actual waves so a stale value
  // (e.g. after switching repos) simply falls back to "all waves" instead of drawing an empty graph.
  const rawWave = searchParams.get("wave");
  const selectedWave = useMemo(() => {
    if (rawWave === null || rawWave === "") {
      return null;
    }
    const parsed = Number(rawWave);
    return sortedWaves.some((wave) => wave.wave === parsed) ? parsed : null;
  }, [rawWave, sortedWaves]);

  // ---- Flows view: pipelines AND the physical objects they move data through, all as nodes -------------------
  // A pipeline reads and writes objects, so a physical table (Fact_OrderSummary, a mart) is its OWN node sitting
  // between the flow that produces it and the flow(s) that consume it - not a label collapsed onto one edge. A
  // procedure that writes several tables therefore gets one node and one edge per table. A view is wired to its
  // base table (not the file the flow read), exactly as in the objects view.
  const flowsGraph = useMemo<BuiltGraph | null>(() => {
    if (!pipelines || !graphObjects || !flowGraphEdges) {
      return null;
    }

    const objectsData = graphObjects;
    const flowGraphData = flowGraphEdges;

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const repoOf = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const objectNodeIds = new Set<string>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();
    const outline = cssColor("--muted-foreground", "#5b6b7f");
    const frontierAccent = cssColor("--chart-7", "#4a3aa7");
    const objectAccent = cssColor("--chart-2", "#008300");
    const warnAccent = cssColor("--chart-4", "#b45309");

    // Stable per-pipeline accent color and repo across the whole closure first, keyed by the pipeline id (unique
    // across repos, unlike a flow name), so a pipeline keeps its color and repo whether or not the wave filter is
    // applied (the filter only changes which nodes are drawn).
    let colorIndex = 0;
    for (const pipeline of pipelines) {
      if (!flowColors.has(pipeline.id)) {
        flowColors.set(pipeline.id, seriesColor(colorIndex));
        colorIndex += 1;
      }
      repoOf.set(pipeline.id, pipeline.repoId);
    }

    // Pipeline nodes, restricted to the selected wave when the batch filter is set. A flow from a repo other than
    // the seed project's repo (a cross-repo downstream hop) gets a dashed outline and its repo in the caption, so
    // where data crosses a repo boundary is legible without turning the graph into a patchwork.
    const drawn = selectedWave === null
      ? pipelines
      : pipelines.filter((pipeline) => pipeline.wave === selectedWave);
    for (const pipeline of drawn) {
      if (names.has(pipeline.id)) {
        continue;
      }
      const color = flowColors.get(pipeline.id)!;
      const crossRepo = repoId !== "" && pipeline.repoId !== repoId;
      // Lineage the sync could not derive is rendered as such, never as an edgeless fact: the caption says
      // "lineage not derived", the border warns, and the reason travels on the node title for hover.
      const incomplete = pipeline.lineageComplete === false;
      let base = pipeline.wave >= 0 ? `${pipeline.kind}, wave ${pipeline.wave}` : pipeline.kind;
      if (crossRepo) {
        base = `${base} · ${pipeline.repoName}`;
      }
      if (incomplete) {
        base = `${base} · lineage not derived`;
      }
      names.set(pipeline.id, pipeline.name);
      nodes.push({
        id: pipeline.id,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <span title={incomplete ? pipeline.incompleteReason ?? undefined : undefined}>
              <NodeLabel name={pipeline.name} caption={base} />
            </span>
          ),
        },
        style: {
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
          padding: 8,
          borderRadius: 8,
          ...(incomplete
            ? { border: "2px dashed", borderColor: warnAccent }
            : crossRepo
              ? { border: "1px dashed", borderColor: outline }
              : {}),
          borderLeft: `5px solid ${color}`,
        },
      });
    }

    // The drawable graph arrives from the server verbatim: nodes typed from the registry, views wired to their
    // base tables, procedures excluded. The client's only judgment here is DISPLAY filtering under the wave
    // selection; it re-derives no semantics from the underlying facts.
    const objectByKey = new Map(objectsData.map((object) => [object.key, object]));
    const ensureObject = (key: string) => {
      if (names.has(key)) {
        return;
      }
      const info = objectByKey.get(key);
      objectNodeIds.add(key);
      names.set(key, info?.name ?? key);
      // The caption places the object: its kind plus where it lives (database.schema); a file has no location. A
      // frontier object (downstream was cut by the depth cap) says so, and a heavier border invites expanding it.
      const isFrontier = info?.frontier ?? false;
      const kind = info?.kind ?? "table";
      const base = info?.location ? `${kind} · ${info.location}` : kind;
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: (
            <NodeLabel
              name={info?.name ?? key}
              caption={isFrontier ? `${base} · more downstream` : base}
            />
          ),
        },
        style: {
          width: NODE_WIDTH,
          height: NODE_HEIGHT,
          padding: 8,
          borderRadius: 20,
          border: isFrontier ? "2px solid" : "1px dashed",
          borderColor: isFrontier ? frontierAccent : objectAccent,
        },
      });
    };

    const addEdge = (source: string, target: string, color: string, label: string) => {
      if (source === target) {
        return;
      }
      const id = `${source}|${target}`;
      if (seenEdges.has(id)) {
        return;
      }
      seenEdges.add(id);
      edges.push({
        id,
        source,
        target,
        label,
        markerEnd: { type: MarkerType.ArrowClosed, color },
        style: { stroke: color, strokeWidth: 1.5 },
      });
      addAdjacency(outgoing, source, target);
      addAdjacency(incoming, target, source);
    };

    // Pass 1: edges attributed to a drawn pipeline (movement edges, and derivation edges of the view's
    // maintaining flow). A wave-filtered-out pipeline holds its edges back for its own batch.
    const deferred: typeof flowGraphData = [];
    for (const edge of flowGraphData) {
      if (edge.pipelineId === null || (edge.label === "view" && !names.has(edge.pipelineId))) {
        deferred.push(edge);
        continue;
      }
      if (!names.has(edge.pipelineId)) {
        continue;
      }
      const color = flowColors.get(edge.pipelineId) ?? seriesColor(colorIndex++);
      if (edge.source !== edge.pipelineId) {
        ensureObject(edge.source);
      }
      if (edge.target !== edge.pipelineId) {
        ensureObject(edge.target);
      }
      addEdge(edge.source, edge.target, color, edge.label);
    }

    // Pass 2: derivation edges no drawn flow owns (a DB-managed view, or an owned view whose maintainer the
    // wave filter hid). They draw when they touch a node already placed, so the view chains into the visible
    // graph instead of dangling, without dragging a whole hidden batch in.
    for (const edge of deferred) {
      if (!names.has(edge.source) && !names.has(edge.target)) {
        continue;
      }
      const color = edge.pipelineId !== null
        ? (flowColors.get(edge.pipelineId) ?? seriesColor(colorIndex++))
        : objectAccent;
      ensureObject(edge.source);
      ensureObject(edge.target);
      addEdge(edge.source, edge.target, color, edge.label);
    }

    return {
      nodes: layeredLayout(nodes, edges),
      edges,
      names,
      flowColors,
      incoming,
      outgoing,
      frontier: frontierSet,
      repoOf,
      // A subscriber is a consumer, not a catalog object: it opens where its queries and reads live.
      openTarget: (id) => (id.startsWith(SUBSCRIBER_KEY_PREFIX)
        ? { label: "Open subscriber", to: `/subscribers?key=${encodeURIComponent(id)}` }
        : objectNodeIds.has(id)
          ? { label: "Open in explorer", to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}` }
          : { label: "Open pipeline", to: `/pipelines/${id}` }),
    };
    // The accent colors are theme-scoped custom properties; rebuilding on mode change keeps them in sync.
  }, [pipelines, graphObjects, flowGraphEdges, frontierSet, selectedWave, repoId, mode]); // eslint-disable-line react-hooks/exhaustive-deps

  // ---- Objects view: tables/files as nodes, "flow moves data from A to B" as edges, colored per flow -------------
  const objectsGraph = useMemo<BuiltGraph | null>(() => {
    if (graphView !== "objects" || !graphObjects || !objectGraphEdges) {
      return null;
    }

    const objectsData = graphObjects;
    const objectGraphData = objectGraphEdges;

    const names = new Map<string, string>();
    const flowColors = new Map<string, string>();
    const incoming = new Map<string, string[]>();
    const outgoing = new Map<string, string[]>();
    const nodes: FlowNode[] = [];
    const edges: FlowEdge[] = [];
    const seenEdges = new Set<string>();
    const frontierAccent = cssColor("--chart-7", "#4a3aa7");

    // The drawable object graph arrives from the server verbatim (data movement per flow, views wired to their
    // base tables, procedures excluded); the client only paints it. Colors are stable per attributed pipeline;
    // an unattributed view-derivation edge is neutral.
    const objectByKey = new Map(objectsData.map((object) => [object.key, object]));
    const ensureNode = (key: string) => {
      if (names.has(key)) {
        return;
      }
      const info = objectByKey.get(key);
      names.set(key, info?.name ?? key);
      // The caption places the object: its kind plus where it lives (database.schema; a file just says "file").
      // A frontier object (downstream cut by the depth cap) says so and gets a heavier border, inviting the
      // user to expand it.
      const isFrontier = info?.frontier ?? false;
      const kind = info?.kind ?? "table";
      const base = info?.location ? `${kind} · ${info.location}` : kind;
      nodes.push({
        id: key,
        position: { x: 0, y: 0 },
        data: {
          label: <NodeLabel name={info?.name ?? key} caption={isFrontier ? `${base} · more downstream` : base} />,
        },
        style: isFrontier
          ? { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8, border: "2px solid", borderColor: frontierAccent }
          : { width: NODE_WIDTH, height: NODE_HEIGHT, padding: 8, borderRadius: 8 },
      });
    };

    let colorIndex = 0;
    const neutral = cssColor("--chart-2", "#008300");
    const colorOf = (pipelineId: string | null): string => {
      if (pipelineId === null) {
        return neutral;
      }
      let color = flowColors.get(pipelineId);
      if (color === undefined) {
        color = seriesColor(colorIndex);
        colorIndex += 1;
        flowColors.set(pipelineId, color);
      }
      return color;
    };

    for (const edge of objectGraphData) {
      const id = `${edge.label}|${edge.source}|${edge.target}`;
      if (edge.source === edge.target || seenEdges.has(id)) {
        continue;
      }
      seenEdges.add(id);
      ensureNode(edge.source);
      ensureNode(edge.target);
      const color = colorOf(edge.pipelineId);
      edges.push({
        id,
        source: edge.source,
        target: edge.target,
        label: truncate(edge.label, 40),
        markerEnd: { type: MarkerType.ArrowClosed, color },
        style: { stroke: color, strokeWidth: 1.5 },
      });
      addAdjacency(outgoing, edge.source, edge.target);
      addAdjacency(incoming, edge.target, edge.source);
    }

    return {
      nodes: layeredLayout(nodes, edges),
      edges,
      names,
      flowColors,
      incoming,
      outgoing,
      frontier: frontierSet,
      repoOf: new Map<string, string>(),
      openTarget: (id) => ({
        label: "Open in explorer",
        to: `/lineage/objects?name=${encodeURIComponent(names.get(id) ?? id)}`,
      }),
    };
  }, [graphView, graphObjects, objectGraphEdges, frontierSet, mode]); // eslint-disable-line react-hooks/exhaustive-deps

  const graph = graphView === "flows" ? flowsGraph : objectsGraph;

  // Focus and centering are per graph; switching project or view resets them. Expanding a frontier node does not
  // (it grows the same graph), so ?expand= is deliberately not a dependency here.
  useEffect(() => {
    setFocus(null);
    setCenterRequest(null);
  }, [repoId, project, graphView]);

  const focusNode = useCallback((id: string | null) => {
    if (id === null || graph === null) {
      setFocus(null);
      return;
    }
    setFocus({
      id,
      upstream: reachability(id, graph.incoming),
      downstream: reachability(id, graph.outgoing),
    });
  }, [graph]);

  // Apply a deep-linked focus once the drawn graph actually contains the node (waves/edges have loaded and, for an
  // object hit, the resolved repo has been adopted). Applied once per focus value so the user's later interaction
  // (clicking elsewhere, switching view) is not overridden.
  const appliedFocus = useRef<string | null>(null);
  useEffect(() => {
    if (focusParam === "" || graph === null || appliedFocus.current === focusParam) {
      return;
    }
    if (!graph.names.has(focusParam)) {
      return;
    }
    appliedFocus.current = focusParam;
    focusNode(focusParam);
    // A deep-link jump zooms in firmly on the landed-on node so it dominates the view (the highlight then makes it
    // unmistakable); in-graph search and wave chips keep the user's current zoom instead.
    setCenterRequest((previous) => ({ id: focusParam, nonce: (previous?.nonce ?? 0) + 1, zoom: 1.9 }));
  }, [focusParam, graph, focusNode]);

  // Re-seed the graph on a node found in the catalog rather than on the canvas: it becomes the ?focus= seed, so the
  // same project-graph endpoint draws that node's own upstream/downstream context. The project scope, the frontier
  // expansions, and the wave filter all belong to the graph being left behind, so they go. A flow hit keeps its
  // repoId (that is what makes the node's Run action available); an object key needs none, the walk is cross-repo.
  // The applied-focus latch is released so re-picking the node currently focused still re-centers on it.
  const seedFocus = useCallback((id: string, hitRepoId?: string) => {
    appliedFocus.current = null;
    setSearchParams((previous) => {
      const next = new URLSearchParams(previous);
      next.delete("project");
      next.delete("expand");
      next.delete("wave");
      if (hitRepoId === undefined) {
        next.delete("repoId");
      } else {
        next.set("repoId", hitRepoId);
      }
      next.set("focus", id);
      return next;
    }, { replace: true });
  }, [setSearchParams]);

  const openNode = useCallback((id: string) => {
    if (graph !== null) {
      navigate(graph.openTarget(id).to);
    }
  }, [graph, navigate]);

  const searchOptions = useMemo(
    () => (graph === null ? [] : [...graph.names.entries()].map(([id, name]) => ({ id, name }))
      .sort((a, b) => a.name.localeCompare(b.name))),
    [graph],
  );

  const queryError = [projectsQuery, projectGraph].find((query) => query.isError)?.error;
  const loadingGraph = graphEnabled && projectGraph.isLoading;
  const hasContent = graph !== null && graph.nodes.length > 0;
  // Flows whose module bodies the sync could not harvest: the graph is knowingly incomplete for them, and that
  // is stated on the canvas instead of letting missing edges read as "this flow moves no data".
  const incompletePipelines = useMemo(
    () => (pipelines ?? []).filter((p) => p.lineageComplete === false),
    [pipelines]);
  // A deep-link onto a node whose closure is still loading, or that came back empty (the node has no lineage, so it
  // is in no graph). Both are gated on a focus seed being what drives the graph (no project chosen).
  const resolvingFocus = focusSeed !== "" && projectGraph.isLoading;
  const focusHasNoRepo = focusSeed !== "" && projectGraph.isSuccess
    && (projectGraph.data?.pipelines.length ?? 0) === 0;
  const repoName = selectedProject?.repoName ?? repoId;

  // Export the drawn graph as an SVG vector of the current layout (nodes, edges, labels) in the active theme,
  // built from the same in-memory graph so it stays a single source of truth.
  const downloadLineage = useCallback(() => {
    if (graph === null) {
      return;
    }
    const svg = buildLineageSvg(graph, {
      text: cssColor("--foreground", "#1d2733"),
      textMuted: cssColor("--muted-foreground", "#5b6b7f"),
      paper: cssColor("--card", "#ffffff"),
      border: cssColor("--border", "#dfe5ee"),
    });
    if (svg === "") {
      return;
    }
    const safeName = (repoName || repoId || "graph").replace(/[^\w.-]+/g, "_");
    const blob = new Blob([svg], { type: "image/svg+xml" });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = `lineage-${safeName}-${graphView}.svg`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    URL.revokeObjectURL(url);
  }, [graph, graphView, repoId, repoName]);

  // Whether the context-menu node is a runnable flow: a pipeline in the seed repo (only a seed-repo flow can be
  // launched from here; cross-repo nodes and objects get the navigation actions only).
  const menuCanRun = nodeMenu !== null && repoId !== "" && graph !== null
    && graph.openTarget(nodeMenu.id).label === "Open pipeline"
    && graph.repoOf.get(nodeMenu.id) === repoId;

  // The floating details panel: node focus (upstream/downstream trace + open) over the execution waves (flows)
  // or the per-flow edge legend (objects). It rides on the canvas via a React Flow <Panel> so the graph keeps the
  // full width, and collapses to a single button when the user wants the whole canvas.
  const detailsPanel = graph === null ? null : (
    <div
      data-testid="graph-side-panel"
      className="flex max-h-[calc(100vh-140px)] w-[300px] flex-col overflow-hidden rounded-lg border border-border bg-card shadow-sm"
    >
      <div className="flex shrink-0 items-center justify-between border-b border-border px-3 py-1.5">
        <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">Details</span>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-xs"
              onClick={() => setPanelOpen(false)}
              data-testid="graph-panel-collapse"
              aria-label="Collapse panel"
            >
              <ChevronRight />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Collapse panel</TooltipContent>
        </Tooltip>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        {focus !== null && (
          <div data-testid="graph-focus-panel" className="mb-3">
            <div className="truncate font-mono text-[13px] font-semibold">
              {graph.names.get(focus.id) ?? focus.id}
            </div>
            <div className="my-2 flex items-center gap-1.5">
              <Badge variant="secondary" className="gap-1.5">
                <span
                  aria-hidden
                  className="size-2 rounded-full"
                  style={{ backgroundColor: accents.upstream }}
                />
                {focus.upstream.size} upstream
              </Badge>
              <Badge variant="secondary" className="gap-1.5">
                <span
                  aria-hidden
                  className="size-2 rounded-full"
                  style={{ backgroundColor: accents.downstream }}
                />
                {focus.downstream.size} downstream
              </Badge>
            </div>
            <div className="flex flex-wrap items-center gap-1.5">
              <Button size="xs" onClick={() => openNode(focus.id)} data-testid="graph-open-selected">
                {graph.openTarget(focus.id).label}
              </Button>
              {(!flowKindById.has(focus.id) || flowKindById.get(focus.id) === "sp") && (
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => setScriptTarget(flowKindById.has(focus.id) ? { key: focus.id, view: "object" } : { key: focus.id })}
                  data-testid="graph-view-script"
                >
                  View script
                </Button>
              )}
              {flowKindById.has(focus.id) && (
                <Button
                  variant="ghost"
                  size="xs"
                  onClick={() => setScriptTarget({ key: focus.id })}
                  data-testid="graph-view-yaml"
                >
                  View YAML
                </Button>
              )}
              <Button variant="ghost" size="xs" onClick={() => focusNode(null)} data-testid="graph-clear-focus">
                Clear
              </Button>
            </div>
            <Separator className="mt-3" />
          </div>
        )}

        {graphView === "flows" ? (
          <>
            <h3 className="mb-1.5 text-[13px] font-medium">Execution waves</h3>
            {sortedWaves.map((wave) => (
              <div key={wave.wave} className="mb-3">
                <div className="text-xs text-muted-foreground">
                  {wave.wave === -1 ? "Unwaved (lineage not computed)" : `Wave ${wave.wave}`}
                </div>
                <div className="mt-1 flex flex-wrap gap-1" data-testid="wave-list">
                  {wave.pipelines.map((pipeline) => (
                    <button
                      type="button"
                      key={pipeline.id}
                      onClick={() => {
                        focusNode(pipeline.id);
                        setCenterRequest((previous) => ({ id: pipeline.id, nonce: (previous?.nonce ?? 0) + 1 }));
                      }}
                      className={cn(chipClass, "cursor-pointer transition-colors hover:bg-accent")}
                      style={{ borderLeft: `3px solid ${graph.flowColors.get(pipeline.id) ?? "transparent"}` }}
                      data-testid={`wave-pipeline-${pipeline.id}`}
                    >
                      {pipeline.name}
                    </button>
                  ))}
                </div>
              </div>
            ))}
          </>
        ) : (
          <>
            <h3 className="mb-1.5 text-[13px] font-medium">Flows (edge colors)</h3>
            <div className="flex flex-wrap gap-1" data-testid="graph-flow-legend">
              {[...graph.flowColors.entries()].map(([flow, color]) => (
                <span key={flow} className={chipClass} style={{ borderLeft: `3px solid ${color}` }}>
                  {flow}
                </span>
              ))}
            </div>
          </>
        )}
      </div>
    </div>
  );

  return (
    <div
      data-testid="page-lineage-graph"
      // Full-bleed: negative margins cancel the editor's content padding so the canvas runs edge to edge, and
      // the height claims the viewport below the fixed chrome (title bar 36px + tab strip 35px + status bar 22px).
      className="relative -m-4 h-[calc(100vh-93px)] min-w-0 overflow-hidden bg-card md:-m-6"
    >
      {/* The focused node's emphasis: its label text is forced to the primary contrast color (it sits on a solid
          primary fill), and it plays a brief glow pulse when it becomes the focus so the eye lands on it. */}
      <style>{`
        @keyframes sf-focus-pulse {
          0% { filter: drop-shadow(0 0 2px var(--primary)); }
          50% { filter: drop-shadow(0 0 16px var(--primary)); }
          100% { filter: drop-shadow(0 0 2px var(--primary)); }
        }
        .react-flow__node.sf-focus-node { animation: sf-focus-pulse 1.3s ease-in-out 3; }
        .react-flow__node.sf-focus-node div { color: var(--primary-foreground) !important; }
      `}</style>
      {/* The toolbar floats over the canvas (top-left) instead of sitting in a page header, so the graph itself
          fills the whole page; the details panel floats at top-right via a React Flow <Panel>. The toolbar's max
          width leaves a clear column on the right for that panel (wider when it is open, narrow for its collapsed
          icon), so a wrapping toolbar can never slide underneath it. */}
      <div
        data-testid="graph-toolbar"
        className={cn(
          "absolute top-3 left-3 z-10 flex flex-wrap items-center gap-2 rounded-lg border border-border bg-card p-2 shadow-sm",
          panelOpen ? "max-w-[calc(100%-340px)]" : "max-w-[calc(100%-84px)]",
        )}
      >
        <Tooltip>
          <TooltipTrigger asChild>
            <Button
              variant="ghost"
              size="icon-sm"
              onClick={() => navigate("/lineage/objects")}
              aria-label="Open explorer"
              data-testid="open-lineage-objects"
            >
              <Rows3 />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Open explorer</TooltipContent>
        </Tooltip>
        {hasContent && (
          <Tooltip>
            <TooltipTrigger asChild>
              <Button
                variant="ghost"
                size="icon-sm"
                onClick={downloadLineage}
                aria-label="Download SVG"
                data-testid="download-lineage"
              >
                <Download />
              </Button>
            </TooltipTrigger>
            <TooltipContent>Download SVG</TooltipContent>
          </Tooltip>
        )}
        <ToggleGroup
          type="single"
          variant="outline"
          size="sm"
          value={graphView}
          onValueChange={(value) => {
            if (value !== "") {
              setParam("view", value === "flows" ? "" : value);
            }
          }}
          aria-label="Graph view"
          data-testid="graph-view-toggle"
        >
          <ToggleGroupItem value="flows" data-testid="graph-view-flows" className="h-8 px-3 text-xs">
            Flows
          </ToggleGroupItem>
          <ToggleGroupItem value="objects" data-testid="graph-view-objects" className="h-8 px-3 text-xs">
            Objects
          </ToggleGroupItem>
        </ToggleGroup>
        {graphView === "flows" && sortedWaves.length > 0 && (
          <Select
            value={selectedWave === null ? "all" : String(selectedWave)}
            onValueChange={(value) => setParam("wave", value === "all" ? "" : value)}
          >
            <SelectTrigger size="sm" className="h-8 w-[150px]" aria-label="Wave" data-testid="graph-wave-filter">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="all">All waves</SelectItem>
              {sortedWaves.map((wave) => (
                <SelectItem key={wave.wave} value={String(wave.wave)}>
                  {wave.wave === -1 ? "Unwaved" : `Wave ${wave.wave}`}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
        )}
        <GraphSearch
          options={searchOptions}
          onPickNode={(id) => {
            focusNode(id);
            setCenterRequest((previous) => ({ id, nonce: (previous?.nonce ?? 0) + 1 }));
          }}
          onPickCatalog={seedFocus}
        />
        <ProjectSelect items={projectItems} selected={selectedProject} onSelect={selectProject} />
      </div>

      {queryError !== undefined && (
        <div className="absolute top-16 left-3 right-3 z-10 max-w-2xl">
          {isApiError(queryError)
            ? <CorrelationError error={queryError} />
            : <p className="text-[13px] text-destructive">{String(queryError)}</p>}
        </div>
      )}

      {queryError === undefined && incompletePipelines.length > 0 && (
        <div
          className="absolute top-16 left-3 z-10 max-w-xl rounded-md border border-amber-600/50 bg-amber-500/10 px-3 py-2"
          data-testid="graph-incomplete-lineage"
        >
          <p className="text-[13px] font-medium text-amber-700 dark:text-amber-400">
            Lineage incomplete for {incompletePipelines.length} flow{incompletePipelines.length === 1 ? "" : "s"}
          </p>
          <p className="text-[12px] text-muted-foreground">
            {incompletePipelines[0].incompleteReason}
            {incompletePipelines.length > 1 && " (and similar for the others - hover a dashed node for its reason)"}
            {" "}Run a connected sync once the control plane can reach the referenced servers.
          </p>
        </div>
      )}

      {!graphEnabled && !projectsQuery.isError && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-empty">
          <EmptyState
            title="Search for a node, or pick a project"
            description="Search above for any table, view, file, or flow to land on it and trace what feeds it and what depends on it. Or seed the map from a project (a repo's root folder) to see all of its flows, the objects they read and write, and everything downstream, even across repos."
          />
        </div>
      )}

      {resolvingFocus && !queryError && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-resolving-focus-box">
          <div className="flex flex-col items-center gap-3" data-testid="graph-resolving-focus">
            <Loader2 className="size-7 animate-spin text-muted-foreground" />
            <p className="text-[13px] text-muted-foreground">Locating the node in the lineage graph...</p>
          </div>
        </div>
      )}

      {loadingGraph && !resolvingFocus && !queryError && (
        <Skeleton className="absolute inset-0 h-full rounded-none" data-testid="graph-loading" />
      )}

      {graphEnabled && !loadingGraph && !queryError && graph !== null && !hasContent && (
        <div className="absolute inset-0 flex items-center justify-center p-6" data-testid="graph-no-lineage">
          {focusSeed !== "" || focusHasNoRepo ? (
            <EmptyState
              data-testid="graph-focus-no-repo"
              title="No lineage references this node yet"
              description="This node has no recorded lineage edges, so nothing feeds it and nothing depends on it. Open it in the object explorer to see its definition and columns."
              action={(
                <Button
                  variant="outline"
                  size="sm"
                  onClick={() => navigate("/lineage/objects")}
                  data-testid="graph-focus-open-explorer"
                >
                  <Rows3 />
                  Open explorer
                </Button>
              )}
            />
          ) : (
            <EmptyState
              title="No lineage for this project yet"
              description={graphView === "flows"
                ? "This project has no active flows, or its repo has not been synced. Sync the repo, then come back."
                : "No object edges were recorded for this project. Sync the repo (a connected sync adds the derived tier), then come back."}
            />
          )}
        </div>
      )}

      {graphEnabled && !loadingGraph && !queryError && graph !== null && hasContent && (
        <ReactFlowProvider>
          <GraphCanvas
            graph={graph}
            focus={focus}
            colorMode={mode}
            accents={accents}
            centerRequest={centerRequest}
            onFocus={focusNode}
            onOpen={openNode}
            onNodeContextMenu={(id, position) => setNodeMenu({ id, x: position.x, y: position.y })}
          >
            {panelOpen ? (
              <Panel position="top-right">{detailsPanel}</Panel>
            ) : (
              <Panel position="top-right">
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      variant="outline"
                      size="icon-sm"
                      onClick={() => setPanelOpen(true)}
                      data-testid="graph-panel-open"
                      aria-label="Show details panel"
                      className="bg-card shadow-sm"
                    >
                      <Info />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>Show details</TooltipContent>
                </Tooltip>
              </Panel>
            )}
          </GraphCanvas>
        </ReactFlowProvider>
      )}

      <DropdownMenu
        open={nodeMenu !== null}
        onOpenChange={(open) => {
          if (!open) {
            setNodeMenu(null);
          }
        }}
      >
        {/* An invisible anchor pinned to the right-click position, so the menu opens at the pointer. */}
        <DropdownMenuTrigger asChild>
          <span
            aria-hidden
            className="fixed size-px"
            style={{ top: nodeMenu?.y ?? 0, left: nodeMenu?.x ?? 0 }}
          />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="w-56">
          {(nodeMenu === null || !flowKindById.has(nodeMenu.id) || flowKindById.get(nodeMenu.id) === "sp") && (
            <DropdownMenuItem
              data-testid="node-menu-view-script"
              onSelect={() => {
                if (nodeMenu !== null) {
                  setScriptTarget(flowKindById.has(nodeMenu.id) ? { key: nodeMenu.id, view: "object" } : { key: nodeMenu.id });
                }
              }}
            >
              View script
            </DropdownMenuItem>
          )}
          {nodeMenu !== null && flowKindById.has(nodeMenu.id) && (
            <DropdownMenuItem
              data-testid="node-menu-view-yaml"
              onSelect={() => {
                setScriptTarget({ key: nodeMenu.id });
              }}
            >
              View YAML
            </DropdownMenuItem>
          )}
          <DropdownMenuItem
            onSelect={() => {
              if (nodeMenu !== null) {
                focusNode(nodeMenu.id);
              }
            }}
          >
            Trace upstream / downstream
          </DropdownMenuItem>
          {nodeMenu !== null && graph !== null && graph.frontier.has(nodeMenu.id) && (
            <DropdownMenuItem
              data-testid="node-menu-expand"
              onSelect={() => {
                if (nodeMenu !== null) {
                  expandNode(nodeMenu.id);
                }
              }}
            >
              Expand downstream
            </DropdownMenuItem>
          )}
          <DropdownMenuItem
            onSelect={() => {
              if (nodeMenu !== null) {
                openNode(nodeMenu.id);
              }
            }}
          >
            Open details
          </DropdownMenuItem>
          {menuCanRun && graph !== null && nodeMenu !== null && (
            <>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                data-testid="node-menu-run-flow"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "flow" });
                  }
                }}
              >
                Run flow
              </DropdownMenuItem>
              <DropdownMenuItem
                data-testid="node-menu-run-node"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "node" });
                  }
                }}
              >
                Run flow + descendants
              </DropdownMenuItem>
              <DropdownMenuItem
                data-testid="node-menu-run-batch"
                onSelect={() => {
                  const flowName = graph.names.get(nodeMenu.id);
                  if (flowName) {
                    setRunDialog({ flowName, scope: "batch" });
                  }
                }}
              >
                Run batch
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      <TriggerRunDialog
        open={runDialog !== null}
        onClose={() => setRunDialog(null)}
        repoId={repoId || undefined}
        flowName={runDialog?.flowName}
        scope={runDialog?.scope}
      />

      <Sheet
        open={scriptTarget !== null}
        onOpenChange={(open) => {
          if (!open) {
            setScriptTarget(null);
          }
        }}
      >
        {/* Focus stays outside Monaco so Escape reaches the sheet, not the editor. */}
        <SheetContent side="right" className="w-full gap-0 sm:max-w-[760px]" onOpenAutoFocus={(event) => event.preventDefault()}>
          <SheetHeader className="border-b border-border pr-10">
            <SheetTitle className="truncate font-mono text-sm">
              {scriptQuery.data?.name ?? scriptTarget?.key ?? ""}
            </SheetTitle>
            <SheetDescription className="sr-only">
              The captured script behind the selected lineage node.
            </SheetDescription>
            {scriptQuery.data && (
              <div className="flex flex-wrap items-center gap-1.5">
                <Badge>{scriptQuery.data.kind}</Badge>
                <Badge variant="outline">{scriptQuery.data.language.toUpperCase()}</Badge>
                {scriptQuery.data.source && <Badge variant="outline">{scriptQuery.data.source}</Badge>}
              </div>
            )}
          </SheetHeader>
          <div className="min-h-0 flex-1 overflow-y-auto p-4">
            {scriptQuery.isLoading && (
              <div className="flex justify-center py-8">
                <Loader2 className="size-5 animate-spin text-muted-foreground" />
              </div>
            )}
            {scriptQuery.isError && (isApiError(scriptQuery.error)
              ? <CorrelationError error={scriptQuery.error} />
              : <p className="text-[13px] text-destructive">{String(scriptQuery.error)}</p>)}
            {scriptQuery.data && (scriptQuery.data.script
              ? (
                <CodeView
                  value={scriptQuery.data.script}
                  language={scriptQuery.data.language === "yaml" ? "yaml" : "sql"}
                  height="calc(100vh - 190px)"
                  data-testid="node-script-body"
                />
              )
              : <EmptyState title="No script" description="This node has no captured script yet. For database objects, run a connected sync (--connect) so the source is read." />)}
          </div>
        </SheetContent>
      </Sheet>
    </div>
  );
}
