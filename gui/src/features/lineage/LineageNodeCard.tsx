import { createContext, memo, useContext } from "react";
import { Handle, Position, type Node, type NodeProps } from "@xyflow/react";
import { ChevronsDown, ChevronsRight, Layers, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { StreamStatus } from "../../api/types";
import { statusIcons, statusInk } from "../datastreams/StreamStatusBadge";
import { statusLabels } from "../datastreams/streamPresentation";
import { counted, kindLook } from "./graphKinds";
import { NODE_HEIGHT, NODE_WIDTH, type Orientation } from "./graphLayout";
import type { ViewNode } from "./graphModel";

/** Where a node stands against the selection: the selected node, on its trace, or beside it. */
export type CardRole = "selected" | "upstream" | "downstream" | "unrelated" | null;

export interface CardData extends Record<string, unknown> {
  node: ViewNode;
  /** Which way data runs: where the card's edges attach, and on which side it counts what was left out. */
  orientation: Orientation;
  role: CardRole;
  /** Dimmed while a control is being dragged towards hiding it, so its effect is seen before it is applied. */
  leaving: boolean;
  /** The worst delivery verdict among the flows the node stands for; null when they are healthy or unjudged. */
  status: StreamStatus | null;
}

export type CardNode = Node<CardData, "card">;

/** What a card's own controls do. Held in context so a node's data stays plain values. */
export interface CardActions {
  /** Bring back the producers (`up`) or consumers (`down`) a trace left out beside this node. */
  onReveal: (id: string, side: "up" | "down") => void;
  /** Ask the server to walk past the frontier objects reached through this node. */
  onExpandFrontier: (keys: string[]) => void;
}

export const CardActionsContext = createContext<CardActions>({ onReveal: () => {}, onExpandFrontier: () => {} });

function waveText(waves: readonly number[]): string {
  const real = waves.filter((wave) => wave >= 0);
  if (real.length === 0) {
    return waves.length > 0 ? "unwaved" : "";
  }
  const first = real[0];
  const last = real[real.length - 1];
  return first === last ? `wave ${first}` : `waves ${first}-${last}`;
}

/** The line under a node's name: what it is and where it sits. */
export function cardCaption(node: ViewNode): string {
  const parts: string[] = [];
  if (node.type === "group") {
    parts.push(counted(node.flowCount, "flow"));
    if (node.objectCount > 0) {
      parts.push(counted(node.objectCount, "object"));
    }
    parts.push(waveText(node.waves));
  } else if (node.type === "stack") {
    parts.push(node.name);
  } else if (node.type === "flow") {
    parts.push(kindLook("flow", node.kind).label, waveText(node.waves));
    if (node.incompleteReasons.length > 0) {
      parts.push("lineage not derived");
    }
  } else {
    parts.push(node.kind, node.place);
  }
  return parts.filter((part) => part !== "").join(" · ");
}

/** The characters of a flow's or an object's name a card has room for, in its monospaced face. */
const NAME_ROOM = 27;
/** How much of a long name's end is always shown. */
const NAME_TAIL = 12;

/**
 * A flow's or an object's name, cut in its middle when the card has no room for it. The flows of a folder share
 * how their names begin and differ in how they end (`...-01-pre`, `...-03-delivery`), and a kind's version is its
 * last segment, so cutting the end would leave a column of cards reading alike. The whole name stays in the page
 * (the start is clipped by its box, not by rewriting it), so it is still found by a search of the page.
 */
function CardName({ name }: { name: string }) {
  if (name.length <= NAME_ROOM) {
    return <>{name}</>;
  }
  const cut = name.length - NAME_TAIL;
  return (
    <span className="flex min-w-0">
      <span className="min-w-0 truncate">{name.slice(0, cut)}</span>
      <span className="shrink-0 whitespace-pre">{name.slice(cut)}</span>
    </span>
  );
}

/** A node's name as its card writes it: a stack says how many of what it holds, everything else its own name. */
export function cardTitle(node: ViewNode): string {
  if (node.type !== "stack") {
    return node.name;
  }
  const look = kindLook(node.memberType, node.kind);
  return node.memberType === "flow"
    ? `${node.members.length} ${look.label.toLowerCase()} flows`
    : `${node.members.length} × ${look.label}`;
}

const ROLE_RING: Readonly<Record<Exclude<CardRole, null>, string>> = {
  selected: "border-primary bg-primary text-primary-foreground shadow-[0_0_0_3px_color-mix(in_srgb,var(--primary)_35%,transparent)]",
  upstream: "border-chart-7 shadow-[0_0_0_1px_var(--chart-7)]",
  downstream: "border-chart-2 shadow-[0_0_0_1px_var(--chart-2)]",
  unrelated: "opacity-25",
};

/**
 * One node of the graph. A flow is a square-cornered card led by its kind's glyph; data is a pill with a dashed
 * edge, so the two read apart without colour; a folder and a stack are cards with a second edge behind them, since
 * each stands for several nodes. What a reduction left out beside a node is said on the node, as a count above or
 * below it, and clicking the count brings exactly that back.
 */
function LineageNodeCardInner({ data }: NodeProps<CardNode>) {
  const { node, orientation, role, leaving, status } = data;
  const down = orientation === "down";
  // What was left out is counted where it would have been drawn: above and below the card, or before and after it.
  const before = down ? "-top-1 right-3 -translate-y-full" : "top-1/2 -left-2 -translate-x-full -translate-y-1/2";
  const after = down ? "-bottom-1 right-3 translate-y-full" : "top-1/2 -right-2 translate-x-full -translate-y-1/2";
  const More = down ? ChevronsDown : ChevronsRight;
  const actions = useContext(CardActionsContext);
  const look = kindLook(node.type === "group" ? "group" : node.memberType, node.kind);
  const Icon = node.type === "stack" ? Layers : look.icon;
  const title = cardTitle(node);
  const caption = cardCaption(node);
  const selected = role === "selected";
  const incomplete = node.incompleteReasons.length > 0;
  const StatusIcon = status !== null ? statusIcons[status] : null;
  const several = node.type === "group" || node.type === "stack";

  return (
    <div
      data-testid="graph-node"
      data-node-type={node.type}
      data-node-id={node.id}
      title={incomplete ? node.incompleteReasons[0] : `${title}\n${caption}`}
      style={{ width: NODE_WIDTH, height: NODE_HEIGHT }}
      className={cn(
        "relative flex items-center gap-2 border bg-card px-2.5 text-left text-card-foreground transition-opacity duration-150",
        node.memberType === "object" && node.type !== "stack" ? "rounded-full border-dashed px-3.5" : "rounded-md",
        several && "shadow-[3px_3px_0_0_var(--border)]",
        incomplete && "border-2 border-dashed border-warning",
        node.anchor && role === null && "border-primary shadow-[0_0_0_2px_color-mix(in_srgb,var(--primary)_40%,transparent)]",
        role !== null && ROLE_RING[role],
        leaving && "opacity-20",
      )}
    >
      <Handle type="target" position={down ? Position.Top : Position.Left} isConnectable={false} className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" />
      <Icon aria-hidden className={cn("size-4 shrink-0", selected ? "text-primary-foreground" : look.iconClass)} />
      <div className="min-w-0 flex-1 overflow-hidden">
        {node.type === "group" ? (
          // A folder's path is told apart by its end, so a long one loses its start, not its end.
          <div className="truncate text-left text-[12px] leading-[18px] font-semibold [direction:rtl]">
            <bdi>{title}</bdi>
          </div>
        ) : (
          <div className={cn("truncate text-[13px] leading-[18px] font-semibold", node.type !== "stack" && "font-mono text-[12px]")}>
            {node.type === "stack" ? title : <CardName name={title} />}
          </div>
        )}
        <div className={cn("truncate text-[11px] leading-4", selected ? "text-primary-foreground/80" : "text-muted-foreground")}>
          {caption}
        </div>
      </div>
      {incomplete && <TriangleAlert aria-label="Lineage not derived" className="size-3.5 shrink-0 text-warning" />}
      {StatusIcon !== null && status !== null && (
        <span
          className={cn("inline-flex shrink-0", selected ? "text-primary-foreground" : statusInk[status])}
          title={`Data stream: ${statusLabels[status]}`}
          aria-label={`Data stream: ${statusLabels[status]}`}
        >
          <StatusIcon aria-hidden className="size-3.5" />
        </span>
      )}

      {node.hiddenUp > 0 && (
        <button
          type="button"
          data-testid="graph-node-more-up"
          className={cn("nodrag nopan absolute h-5 min-w-5 cursor-pointer rounded-full border border-border bg-background px-1.5 text-[10px] font-medium text-muted-foreground hover:border-primary hover:text-foreground", before)}
          title={`${counted(node.hiddenUp, "producer")} not drawn. Click to draw ${node.hiddenUp === 1 ? "it" : "them"}.`}
          aria-label={`Draw ${counted(node.hiddenUp, "hidden producer")}`}
          onClick={(event) => {
            event.stopPropagation();
            actions.onReveal(node.id, "up");
          }}
        >
          +{node.hiddenUp}
        </button>
      )}
      {node.hiddenDown > 0 && (
        <button
          type="button"
          data-testid="graph-node-more-down"
          className={cn("nodrag nopan absolute h-5 min-w-5 cursor-pointer rounded-full border border-border bg-background px-1.5 text-[10px] font-medium text-muted-foreground hover:border-primary hover:text-foreground", after)}
          title={`${counted(node.hiddenDown, "consumer")} not drawn. Click to draw ${node.hiddenDown === 1 ? "it" : "them"}.`}
          aria-label={`Draw ${counted(node.hiddenDown, "hidden consumer")}`}
          onClick={(event) => {
            event.stopPropagation();
            actions.onReveal(node.id, "down");
          }}
        >
          +{node.hiddenDown}
        </button>
      )}
      {node.frontierKeys.length > 0 && node.hiddenDown === 0 && (
        <button
          type="button"
          data-testid="graph-node-expand"
          className={cn("nodrag nopan absolute inline-flex h-5 cursor-pointer items-center gap-0.5 rounded-full border border-chart-7 bg-background px-1.5 text-[10px] font-medium text-foreground hover:bg-accent", after)}
          title="More is downstream of this node than the graph loaded. Click to load it."
          aria-label="Load what is further downstream"
          onClick={(event) => {
            event.stopPropagation();
            actions.onExpandFrontier(node.frontierKeys);
          }}
        >
          more
          <More aria-hidden className="size-3" />
        </button>
      )}
      <Handle type="source" position={down ? Position.Bottom : Position.Right} isConnectable={false} className="!size-1 !min-h-0 !min-w-0 !border-0 !opacity-0" />
    </div>
  );
}

export const LineageNodeCard = memo(LineageNodeCardInner);
