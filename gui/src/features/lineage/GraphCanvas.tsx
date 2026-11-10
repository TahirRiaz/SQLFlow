import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import {
  Background,
  Controls,
  MarkerType,
  MiniMap,
  ReactFlow,
  useNodesState,
  useReactFlow,
  type Edge as FlowEdge,
} from "@xyflow/react";
import type { StreamStatus } from "../../api/types";
import { NODE_HEIGHT, NODE_WIDTH, type Position } from "./graphLayout";
import type { View, ViewEdge } from "./graphModel";
import { CardActionsContext, LineageNodeCard, type CardActions, type CardNode, type CardRole } from "./LineageNodeCard";

const nodeTypes = { card: LineageNodeCard };

/** A drawing this small is seen whole, so a map of it would only cover part of it. */
const MINIMAP_ABOVE = 60;

/** Up to this many edges, each one names the flows it stands for; past it the names would bury the drawing. */
const LABEL_EVERY_EDGE_UP_TO = 30;
/** The selected node's own edges say what they pass through, unless it has more of them than can be read. */
const LABEL_SELECTED_EDGES_UP_TO = 10;
/** The width the details panel covers on the right, kept clear when a drawing is fitted with a node selected. */
const DETAILS_WIDTH = 324;

function fitOptions(detailsOpen: boolean) {
  return {
    maxZoom: 1,
    padding: { top: "24px", bottom: "24px", left: "32px", right: `${detailsOpen ? DETAILS_WIDTH : 32}px` },
  } as const;
}

export interface CanvasSelection {
  id: string;
  upstream: ReadonlySet<string>;
  downstream: ReadonlySet<string>;
}

export interface CenterRequest {
  id: string;
  nonce: number;
}

interface CanvasProps {
  view: View;
  positions: ReadonlyMap<string, Position>;
  selection: CanvasSelection | null;
  /** The worst delivery verdict per drawn node, where there is one worth showing. */
  statuses: ReadonlyMap<string, StreamStatus>;
  /** While the wave range is being dragged: the waves that would stay. Nodes outside them fade. */
  previewWaves: ReadonlySet<number> | null;
  /** The names the hidden nodes an edge passes through go by. */
  nameOf: (baseId: string) => string;
  colorMode: "light" | "dark";
  centerRequest: CenterRequest | null;
  actions: CardActions;
  onSelect: (id: string | null) => void;
  onActivate: (id: string) => void;
  onNodeContextMenu: (id: string, position: { x: number; y: number }) => void;
  children?: ReactNode;
}

function edgeLabel(edge: ViewEdge, nameOf: (id: string) => string): string {
  if (edge.labels.length > 0) {
    return edge.labels.length <= 2 ? edge.labels.join(", ") : `${edge.labels.length} flows`;
  }
  if (edge.via.length === 1) {
    return `via ${nameOf(edge.via[0])}`;
  }
  return edge.via.length > 1 ? `via ${edge.via.length} nodes` : "";
}

/**
 * The canvas. Positions live in React Flow's state so a node can be dragged, and are seeded again whenever the
 * drawing changes; the selection's emphasis is applied on top without touching positions. A click selects (the
 * node's producers and consumers light up, the rest fades), a double click opens what the node stands for, and a
 * click on the empty canvas clears the selection. A new drawing is fitted to the canvas, and its nodes slide to
 * their places instead of jumping, so the reader keeps their bearings through a change of scope.
 */
export function GraphCanvas({
  view, positions, selection, statuses, previewWaves, nameOf, colorMode, centerRequest, actions,
  onSelect, onActivate, onNodeContextMenu, children,
}: CanvasProps) {
  const flow = useReactFlow();
  const seeded = useMemo<CardNode[]>(
    () => view.nodes.map((node) => ({
      id: node.id,
      type: "card" as const,
      position: positions.get(node.id) ?? { x: 0, y: 0 },
      data: { node, role: null, leaving: false, status: null },
      width: NODE_WIDTH,
      height: NODE_HEIGHT,
    })),
    [view, positions],
  );
  const [flowNodes, setFlowNodes, onNodesChange] = useNodesState<CardNode>(seeded);
  const [moving, setMoving] = useState(false);
  const firstLayout = useRef(true);
  // Read when a drawing is fitted, never a reason to fit: selecting a node must not move the canvas.
  const detailsOpen = useRef(selection !== null);
  useEffect(() => {
    detailsOpen.current = selection !== null;
  }, [selection]);

  useEffect(() => {
    setFlowNodes(seeded);
    // The first drawing appears in place; later ones slide, and the canvas is fitted once they have landed.
    if (firstLayout.current) {
      firstLayout.current = false;
      return;
    }
    setMoving(true);
    const settle = window.setTimeout(() => setMoving(false), 260);
    const fit = window.requestAnimationFrame(() => {
      void flow.fitView({ duration: 240, ...fitOptions(detailsOpen.current) });
    });
    return () => {
      window.clearTimeout(settle);
      window.cancelAnimationFrame(fit);
    };
  }, [seeded, setFlowNodes, flow]);

  // A request is answered once, by the first drawing that holds its node: a node picked from a list may only be
  // drawn by the render after the one that asked for it (its folder opens, its stack unstacks).
  const answered = useRef(0);
  useEffect(() => {
    if (centerRequest === null || answered.current === centerRequest.nonce) {
      return;
    }
    const target = positions.get(centerRequest.id);
    if (target === undefined) {
      return;
    }
    // After the fit a new drawing asks for, so the node asked for is what the canvas ends on.
    const handle = window.setTimeout(() => {
      answered.current = centerRequest.nonce;
      void flow.setCenter(target.x + NODE_WIDTH / 2, target.y + NODE_HEIGHT / 2, {
        zoom: Math.max(flow.getZoom(), 0.9),
        duration: 400,
      });
    }, 300);
    return () => window.clearTimeout(handle);
  }, [centerRequest, positions, flow]);

  const styledNodes = useMemo<CardNode[]>(() => flowNodes.map((flowNode) => {
    const node = flowNode.data.node;
    let role: CardRole = null;
    if (selection !== null) {
      role = node.id === selection.id
        ? "selected"
        : selection.upstream.has(node.id)
          ? "upstream"
          : selection.downstream.has(node.id) ? "downstream" : "unrelated";
    }
    const leaving = previewWaves !== null && node.memberType === "flow" && node.waves.length > 0
      && !node.waves.some((wave) => previewWaves.has(wave));
    return {
      ...flowNode,
      zIndex: role === "selected" ? 10 : undefined,
      data: { node, role, leaving, status: statuses.get(node.id) ?? null },
    };
  }), [flowNodes, selection, previewWaves, statuses]);

  const styledEdges = useMemo<FlowEdge[]>(() => {
    const labelAll = view.edges.length <= LABEL_EVERY_EDGE_UP_TO;
    const own = selection === null
      ? 0
      : view.edges.filter((edge) => edge.source === selection.id || edge.target === selection.id).length;
    const upstream = selection === null ? null : new Set([selection.id, ...selection.upstream]);
    const downstream = selection === null ? null : new Set([selection.id, ...selection.downstream]);
    return view.edges.map((edge) => {
      const onUpstream = upstream !== null && upstream.has(edge.source) && upstream.has(edge.target);
      const onDownstream = downstream !== null && downstream.has(edge.source) && downstream.has(edge.target);
      const related = onUpstream || onDownstream;
      const stroke = onUpstream ? "var(--chart-7)" : onDownstream ? "var(--chart-2)" : "var(--muted-foreground)";
      const opacity = selection === null ? 0.55 : related ? 1 : 0.08;
      // An edge is named where the name can be read: on a small drawing the flows it stands for, and on the
      // selected node's own edges what they pass through. Everything else is in the details panel.
      const touchesSelected = selection !== null && (edge.source === selection.id || edge.target === selection.id);
      const label = touchesSelected && own <= LABEL_SELECTED_EDGES_UP_TO
        ? edgeLabel(edge, nameOf)
        : labelAll && selection === null && edge.labels.length > 0 ? edgeLabel(edge, nameOf) : "";
      return {
        id: edge.id,
        source: edge.source,
        target: edge.target,
        label: label === "" ? undefined : label,
        zIndex: related ? 5 : 0,
        markerEnd: { type: MarkerType.ArrowClosed, color: stroke, width: 16, height: 16 },
        style: { stroke, strokeWidth: edge.weight > 1 ? 2 : 1.25, opacity },
        labelStyle: { fontSize: 11, opacity },
      };
    });
  }, [view.edges, selection, nameOf]);

  return (
    <CardActionsContext.Provider value={actions}>
      <ReactFlow
        className={moving ? "sf-lineage-moving" : undefined}
        nodes={styledNodes}
        edges={styledEdges}
        nodeTypes={nodeTypes}
        onNodesChange={onNodesChange}
        onNodeClick={(_, node) => onSelect(node.id)}
        onNodeDoubleClick={(_, node) => onActivate(node.id)}
        onNodeContextMenu={(event, node) => {
          event.preventDefault();
          onNodeContextMenu(node.id, { x: event.clientX, y: event.clientY });
        }}
        onPaneClick={() => onSelect(null)}
        colorMode={colorMode}
        fitView
        fitViewOptions={fitOptions(selection !== null)}
        minZoom={0.05}
        nodesConnectable={false}
        nodesDraggable
        zoomOnDoubleClick={false}
      >
        <Background />
        <Controls showInteractive={false} />
        {view.nodes.length > MINIMAP_ABOVE && <MiniMap pannable zoomable />}
        {children}
      </ReactFlow>
    </CardActionsContext.Provider>
  );
}
