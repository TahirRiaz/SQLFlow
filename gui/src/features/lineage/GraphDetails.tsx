import { useState, type ReactNode } from "react";
import { Crosshair, FolderOpen, Layers, X } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { counted, kindLook } from "./graphKinds";
import type { BaseGraph, BaseNode, ViewNode } from "./graphModel";
import { cardCaption, cardTitle } from "./LineageNodeCard";

const LIST_PREVIEW = 8;

/** A titled list of base nodes, each a button that draws and selects it. Long lists open on request. */
function NodeList({ title, nodes, onPick, testId }: {
  title: string;
  nodes: readonly BaseNode[];
  onPick: (id: string) => void;
  testId: string;
}) {
  const [all, setAll] = useState(false);
  if (nodes.length === 0) {
    return null;
  }
  const shown = all ? nodes : nodes.slice(0, LIST_PREVIEW);
  return (
    <section className="mt-3" data-testid={testId}>
      <h4 className="mb-1 text-[11px] font-medium tracking-wider text-muted-foreground uppercase">
        {title} <span className="tabular-nums">{nodes.length}</span>
      </h4>
      <ul className="flex flex-col">
        {shown.map((node) => {
          const look = kindLook(node.type, node.kind);
          return (
            <li key={node.id}>
              <button
                type="button"
                onClick={() => onPick(node.id)}
                title={`${node.name}\n${look.label}${node.place === "" ? "" : ` · ${node.place}`}`}
                data-testid="graph-detail-node"
                className="flex w-full cursor-pointer items-center gap-1.5 rounded-sm px-1 py-0.5 text-left hover:bg-accent"
              >
                <look.icon aria-hidden className={`size-3.5 shrink-0 ${look.iconClass}`} />
                <span className="min-w-0 flex-1 truncate font-mono text-[12px]">{node.name}</span>
              </button>
            </li>
          );
        })}
      </ul>
      {nodes.length > LIST_PREVIEW && (
        <Button variant="ghost" size="xs" className="mt-0.5 px-1 text-muted-foreground" onClick={() => setAll(!all)}>
          {all ? "Show fewer" : `Show all ${nodes.length}`}
        </Button>
      )}
    </section>
  );
}

function byName(a: BaseNode, b: BaseNode): number {
  return a.name.localeCompare(b.name);
}

function neighbours(base: BaseGraph, id: string, side: "incoming" | "outgoing"): BaseNode[] {
  return [...(base[side].get(id) ?? [])]
    .map((neighbour) => base.nodes.get(neighbour))
    .filter((node): node is BaseNode => node !== undefined)
    .sort(byName);
}

/**
 * What the selected node is, and what can be done with it. The graph shows how nodes connect; the lists here hold
 * what a card has no room for (every object a flow reads and writes, every flow in a folder), and each entry draws
 * and selects its node, so a detail is one click from being on the canvas.
 */
export function GraphDetails({
  node, base, upstream, downstream, traced, openLabel, canViewScript, canViewYaml, stream,
  onClose, onTrace, onOpen, onUnfold, onPick, onViewScript, onViewYaml,
}: {
  node: ViewNode;
  /** The scope's graph at the drawn level, for the node's direct producers and consumers whatever is hidden. */
  base: BaseGraph;
  upstream: number;
  downstream: number;
  /** Whether this node is the one the drawing is traced around. */
  traced: boolean;
  /** What opening the node elsewhere is called; null for a folder or a stack, which open in place. */
  openLabel: string | null;
  canViewScript: boolean;
  canViewYaml: boolean;
  stream: ReactNode;
  onClose: () => void;
  onTrace: () => void;
  onOpen: () => void;
  /** Open a folder, or unstack a stack. */
  onUnfold: () => void;
  onPick: (id: string) => void;
  onViewScript: () => void;
  onViewYaml: () => void;
}) {
  const look = kindLook(node.type === "group" ? "group" : node.memberType, node.kind);
  const Icon = node.type === "stack" ? Layers : look.icon;
  const plain = node.type === "flow" || node.type === "object";
  const members = node.members
    .map((id) => base.nodes.get(id))
    .filter((member): member is BaseNode => member !== undefined)
    .sort(byName);

  return (
    <div
      data-testid="graph-focus-panel"
      className="flex max-h-[calc(100%-16px)] w-[300px] flex-col overflow-hidden rounded-lg border border-border bg-card shadow-sm"
    >
      <div className="flex shrink-0 items-start gap-2 border-b border-border px-3 py-2">
        <Icon aria-hidden className={`mt-0.5 size-4 shrink-0 ${look.iconClass}`} />
        <div className="min-w-0 flex-1">
          <div className="font-mono text-[13px] leading-[18px] font-semibold break-all" data-testid="graph-focus-name">
            {node.type === "stack" ? cardTitle(node) : node.name}
          </div>
          <div className="text-[11px] leading-4 text-muted-foreground">
            {node.type === "stack" ? look.label : cardCaption(node)}
          </div>
        </div>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Clear selection" data-testid="graph-clear-focus">
              <X />
            </Button>
          </TooltipTrigger>
          <TooltipContent>Clear selection</TooltipContent>
        </Tooltip>
      </div>

      <div className="min-h-0 flex-1 overflow-y-auto p-3">
        <div className="flex items-center gap-1.5">
          <Badge variant="secondary" className="gap-1.5">
            <span aria-hidden className="size-2 rounded-full bg-chart-7" />
            {upstream} upstream
          </Badge>
          <Badge variant="secondary" className="gap-1.5">
            <span aria-hidden className="size-2 rounded-full bg-chart-2" />
            {downstream} downstream
          </Badge>
        </div>

        <div className="mt-2 flex flex-wrap items-center gap-1.5">
          {node.type !== "stack" && (
            <Tooltip>
              <TooltipTrigger asChild>
                <Button size="xs" onClick={onTrace} disabled={traced} data-testid="graph-trace-selected">
                  <Crosshair />
                  {traced ? "Traced" : "Trace"}
                </Button>
              </TooltipTrigger>
              <TooltipContent>
                {traced
                  ? "The drawing already holds only what is upstream and downstream of this node."
                  : "Draw only what is upstream and downstream of this node."}
              </TooltipContent>
            </Tooltip>
          )}
          {node.type === "group" && (
            <Button variant="outline" size="xs" onClick={onUnfold} data-testid="graph-open-folder">
              <FolderOpen />
              Open folder
            </Button>
          )}
          {node.type === "stack" && (
            <Button size="xs" onClick={onUnfold} data-testid="graph-unstack">
              <Layers />
              Unstack
            </Button>
          )}
          {plain && openLabel !== null && (
            <Button variant="outline" size="xs" onClick={onOpen} data-testid="graph-open-selected">{openLabel}</Button>
          )}
          {plain && canViewScript && (
            <Button variant="ghost" size="xs" onClick={onViewScript} data-testid="graph-view-script">View script</Button>
          )}
          {plain && canViewYaml && (
            <Button variant="ghost" size="xs" onClick={onViewYaml} data-testid="graph-view-yaml">View YAML</Button>
          )}
        </div>

        {node.incompleteReasons.length > 0 && (
          <p className="mt-3 rounded-md border border-warning/50 bg-warning/10 px-2 py-1.5 text-[11px] leading-4" data-testid="graph-detail-incomplete">
            {node.incompleteReasons[0]}
          </p>
        )}

        {stream}

        {node.type === "flow" && (
          <>
            <NodeList title="Reads" nodes={neighbours(base, node.id, "incoming")} onPick={onPick} testId="graph-detail-reads" />
            <NodeList title="Writes" nodes={neighbours(base, node.id, "outgoing")} onPick={onPick} testId="graph-detail-writes" />
          </>
        )}
        {node.type === "object" && (
          <>
            <NodeList title="Fed by" nodes={neighbours(base, node.id, "incoming")} onPick={onPick} testId="graph-detail-writers" />
            <NodeList title="Read by" nodes={neighbours(base, node.id, "outgoing")} onPick={onPick} testId="graph-detail-readers" />
          </>
        )}
        {!plain && (
          <NodeList
            title={node.type === "group" ? "In this folder" : `${counted(members.length, "node")} stacked`}
            nodes={members}
            onPick={onPick}
            testId="graph-detail-members"
          />
        )}
      </div>
    </div>
  );
}
