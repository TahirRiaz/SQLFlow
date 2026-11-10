import { useState, type ReactNode } from "react";
import { ArrowLeftToLine, ArrowRightToLine, Eye, EyeOff, FoldHorizontal, Layers, MoveHorizontal, SlidersHorizontal, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Slider } from "@/components/ui/slider";
import { Toggle } from "@/components/ui/toggle";
import { ToggleGroup, ToggleGroupItem } from "@/components/ui/toggle-group";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { counted, kindLook } from "./graphKinds";

/** A pressed choice is told by its edge and fill, not by a shade the eye has to compare. */
const PRESSED = "data-[state=on]:border-primary data-[state=on]:bg-primary/10 data-[state=on]:text-foreground";
import type { GraphLevel, KindCount, TraceDirection } from "./graphModel";

/**
 * One control of the graph's bar under the word that names what it changes. The bar reads left to right as the
 * order a drawing is made in: what to look at, how much of it, then which part.
 */
export function Field({ label, children, testId }: { label: string; children: ReactNode; testId?: string }) {
  return (
    <div className="flex min-w-0 flex-col gap-1" data-testid={testId}>
      <span className="text-[10px] leading-none font-medium tracking-wider text-muted-foreground uppercase">{label}</span>
      <div className="flex h-8 items-center gap-1.5">{children}</div>
    </div>
  );
}

const LEVELS: readonly { value: GraphLevel; label: string; effect: string }[] = [
  { value: "flows", label: "Flows", effect: "Flows alone. An arrow says one flow reads what the other writes." },
  { value: "handovers", label: "Handovers", effect: "Flows, and the objects one flow writes and another reads." },
  { value: "all", label: "Everything", effect: "Flows with every object each one reads and writes." },
  { value: "objects", label: "Data only", effect: "Tables, files and datasets, joined by the flows that move data between them." },
];

/** How much is drawn. Each choice carries the number of nodes it draws for the whole scope, so its cost is seen before it is picked. */
export function LevelControl({ value, sizes, onChange }: {
  value: GraphLevel;
  sizes: Record<GraphLevel, number>;
  onChange: (level: GraphLevel) => void;
}) {
  return (
    <ToggleGroup
      type="single"
      variant="outline"
      size="sm"
      value={value}
      onValueChange={(next) => {
        if (next !== "") {
          onChange(next as GraphLevel);
        }
      }}
      aria-label="How much is drawn"
      data-testid="graph-level"
    >
      {LEVELS.map((level) => (
        <Tooltip key={level.value}>
          <TooltipTrigger asChild>
            <ToggleGroupItem
              value={level.value}
              data-testid={`graph-level-${level.value}`}
              className={cn("h-8 gap-1.5 px-2.5 text-xs", PRESSED)}
            >
              {level.label}
              <span className="font-normal text-muted-foreground tabular-nums">{sizes[level.value]}</span>
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>{level.effect} {counted(sizes[level.value], "node")} in this scope.</TooltipContent>
        </Tooltip>
      ))}
    </ToggleGroup>
  );
}

/** The two ways several nodes draw as one: a folder's flows, and look-alike nodes. */
export function CompactControl({ grouped, groupable, stacked, onGrouped, onStacked }: {
  grouped: boolean;
  groupable: boolean;
  stacked: boolean;
  onGrouped: (value: boolean) => void;
  onStacked: (value: boolean) => void;
}) {
  return (
    <>
      {groupable && (
        <Tooltip>
          <TooltipTrigger asChild>
            <Toggle
              variant="outline"
              size="sm"
              pressed={grouped}
              onPressedChange={onGrouped}
              data-testid="graph-group-toggle"
              className={cn("h-8 gap-1.5 px-2.5 text-xs", PRESSED)}
            >
              <FoldHorizontal aria-hidden />
              Folders
            </Toggle>
          </TooltipTrigger>
          <TooltipContent>
            {grouped
              ? "Each folder's flows draw as one node. Double-click a folder to open it."
              : "Draw each folder's flows as one node, to see how the folders depend on each other."}
          </TooltipContent>
        </Tooltip>
      )}
      <Tooltip>
        <TooltipTrigger asChild>
          <Toggle
            variant="outline"
            size="sm"
            pressed={stacked}
            onPressedChange={onStacked}
            data-testid="graph-stack-toggle"
            className={cn("h-8 gap-1.5 px-2.5 text-xs", PRESSED)}
          >
            <Layers aria-hidden />
            Stacks
          </Toggle>
        </TooltipTrigger>
        <TooltipContent>
          {stacked
            ? "Nodes of one kind with the same producers and consumers draw as one stack. Double-click a stack to open it."
            : "Draw nodes of one kind with the same producers and consumers as one stack."}
        </TooltipContent>
      </Tooltip>
    </>
  );
}

function waveName(wave: number): string {
  return wave < 0 ? "Unwaved" : `Wave ${wave}`;
}

/**
 * The wave range. Each stop is a wave, with a bar as tall as its share of the flows, so the reader sees where the
 * bulk sits before dragging; the two thumbs keep every wave between them, so a chain running through those waves
 * stays whole. While a thumb moves, `onPreview` tells the canvas which waves would stay (it fades the rest), and
 * the range is applied when the thumb is released.
 */
export function WaveRange({ stops, range, onPreview, onCommit }: {
  stops: readonly { wave: number; count: number }[];
  /** The first and last drawn stop, as indexes into `stops`. */
  range: readonly [number, number];
  onPreview: (range: readonly [number, number] | null) => void;
  onCommit: (range: readonly [number, number]) => void;
}) {
  const [draft, setDraft] = useState<readonly [number, number] | null>(null);
  const shown = draft ?? range;
  const most = Math.max(...stops.map((stop) => stop.count), 1);
  const flows = stops.slice(shown[0], shown[1] + 1).reduce((sum, stop) => sum + stop.count, 0);
  const whole = shown[0] === 0 && shown[1] === stops.length - 1;
  const first = stops[shown[0]];
  const last = stops[shown[1]];
  const text = whole
    ? `All ${stops.length}`
    : shown[0] === shown[1] ? waveName(first.wave) : `${waveName(first.wave)} to ${last.wave < 0 ? "unwaved" : last.wave}`;

  return (
    <div className="flex items-center gap-2" data-testid="graph-wave-range">
      <div className="flex w-[168px] flex-col gap-1">
        <div className="flex h-3 items-end gap-px px-[7px]" aria-hidden>
          {stops.map((stop, index) => (
            <div
              key={stop.wave}
              title={`${waveName(stop.wave)}: ${counted(stop.count, "flow")}`}
              className={cn("flex-1 rounded-t-[1px]", index >= shown[0] && index <= shown[1] ? "bg-primary/60" : "bg-muted-foreground/25")}
              style={{ height: `${Math.max(15, Math.round((stop.count / most) * 100))}%` }}
            />
          ))}
        </div>
        <Slider
          min={0}
          max={stops.length - 1}
          step={1}
          minStepsBetweenThumbs={0}
          value={[shown[0], shown[1]]}
          thumbLabels={["First wave drawn", "Last wave drawn"]}
          onValueChange={(next) => {
            const value: [number, number] = [Math.min(next[0], next[1]), Math.max(next[0], next[1])];
            setDraft(value);
            onPreview(value);
          }}
          onValueCommit={(next) => {
            setDraft(null);
            onPreview(null);
            onCommit([Math.min(next[0], next[1]), Math.max(next[0], next[1])]);
          }}
        />
      </div>
      <Tooltip>
        <TooltipTrigger asChild>
          <span className="w-[92px] text-[11px] leading-tight text-muted-foreground" data-testid="graph-wave-range-text">
            <span className="block truncate text-foreground">{text}</span>
            <span className="block truncate tabular-nums">{counted(flows, "flow")}</span>
          </span>
        </TooltipTrigger>
        <TooltipContent>
          Flows run in waves: a flow runs after everything it reads from. Drag the ends to draw only the flows of
          those waves; the bars show how many flows each wave holds.
        </TooltipContent>
      </Tooltip>
    </div>
  );
}

/**
 * The legend, which is also the filter: every kind of node the drawing holds, with its glyph and count, and an eye
 * to hide it. Hiding a kind takes its nodes out and joins what they joined, so a chain through them stays connected.
 */
export function KindFilter({ kinds, hidden, onToggle, onShowAll }: {
  kinds: readonly KindCount[];
  hidden: ReadonlySet<string>;
  onToggle: (key: string) => void;
  onShowAll: () => void;
}) {
  const hiddenHere = kinds.filter((kind) => hidden.has(kind.key)).length;
  return (
    <Popover>
      <PopoverTrigger asChild>
        <Button variant="outline" size="sm" className="h-8 gap-1.5 px-2.5 text-xs font-medium" data-testid="graph-kind-filter">
          <SlidersHorizontal aria-hidden />
          {hiddenHere === 0 ? `${kinds.length} kinds` : `${hiddenHere} of ${kinds.length} hidden`}
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-72 p-0">
        <div className="flex items-center justify-between border-b border-border px-3 py-2">
          <span className="text-[13px] font-medium">Kinds in this drawing</span>
          {hiddenHere > 0 && (
            <Button variant="ghost" size="xs" onClick={onShowAll} data-testid="graph-kind-show-all">Show all</Button>
          )}
        </div>
        <ul className="max-h-80 overflow-y-auto p-1">
          {kinds.map((kind) => {
            const look = kindLook(kind.type, kind.kind);
            const isHidden = hidden.has(kind.key);
            return (
              <li key={kind.key}>
                <button
                  type="button"
                  role="switch"
                  aria-checked={!isHidden}
                  data-testid="graph-kind-option"
                  data-kind={kind.key}
                  onClick={() => onToggle(kind.key)}
                  className={cn(
                    "flex w-full cursor-pointer items-center gap-2 rounded-sm px-2 py-1.5 text-left text-[13px] hover:bg-accent",
                    isHidden && "text-muted-foreground",
                  )}
                >
                  <look.icon aria-hidden className={cn("size-4 shrink-0", isHidden ? "text-muted-foreground/60" : look.iconClass)} />
                  <span className={cn("min-w-0 flex-1 truncate", isHidden && "line-through")}>
                    {look.label}
                    <span className="ml-1.5 text-xs text-muted-foreground">{kind.type === "flow" ? "flow" : "data"}</span>
                  </span>
                  <span className="text-xs text-muted-foreground tabular-nums">{kind.count}</span>
                  {isHidden ? <EyeOff aria-hidden className="size-3.5 shrink-0" /> : <Eye aria-hidden className="size-3.5 shrink-0 text-muted-foreground" />}
                </button>
              </li>
            );
          })}
        </ul>
        <p className="border-t border-border px-3 py-2 text-[11px] leading-4 text-muted-foreground">
          A hidden kind is bridged: what it joined stays joined, so no chain breaks.
        </p>
      </PopoverContent>
    </Popover>
  );
}

const HOPS: readonly { value: string; label: string }[] = [
  { value: "1", label: "1" },
  { value: "2", label: "2" },
  { value: "3", label: "3" },
  { value: "all", label: "All" },
];

/** What a trace draws around its node: how far, and to which side. Shown only while a node is traced. */
export function TraceControl({ name, hops, direction, onHops, onDirection, onClear }: {
  name: string;
  hops: number | null;
  direction: TraceDirection;
  onHops: (hops: number | null) => void;
  onDirection: (direction: TraceDirection) => void;
  onClear: () => void;
}) {
  return (
    <>
      <span className="max-w-[180px] truncate font-mono text-[12px]" title={name} data-testid="graph-trace-name">{name}</span>
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={hops === null ? "all" : String(Math.min(hops, 3))}
        onValueChange={(next) => {
          if (next !== "") {
            onHops(next === "all" ? null : Number(next));
          }
        }}
        aria-label="Hops drawn around the traced node"
        data-testid="graph-trace-hops"
      >
        {HOPS.map((option) => (
          <Tooltip key={option.value}>
            <TooltipTrigger asChild>
              <ToggleGroupItem value={option.value} data-testid={`graph-trace-hops-${option.value}`} className={cn("h-8 px-2 text-xs tabular-nums", PRESSED)}>
                {option.label}
              </ToggleGroupItem>
            </TooltipTrigger>
            <TooltipContent>
              {option.value === "all" ? "Everything upstream and downstream of it." : `What is within ${counted(Number(option.value), "step")} of it.`}
            </TooltipContent>
          </Tooltip>
        ))}
      </ToggleGroup>
      <ToggleGroup
        type="single"
        variant="outline"
        size="sm"
        value={direction}
        onValueChange={(next) => {
          if (next !== "") {
            onDirection(next as TraceDirection);
          }
        }}
        aria-label="Side of the traced node that is drawn"
        data-testid="graph-trace-direction"
      >
        <Tooltip>
          <TooltipTrigger asChild>
            <ToggleGroupItem value="upstream" aria-label="Upstream only" data-testid="graph-trace-upstream" className={cn("h-8 px-2", PRESSED)}>
              <ArrowLeftToLine aria-hidden />
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>Upstream only: what it is made from.</TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger asChild>
            <ToggleGroupItem value="both" aria-label="Both sides" data-testid="graph-trace-both" className={cn("h-8 px-2", PRESSED)}>
              <MoveHorizontal aria-hidden />
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>Both sides.</TooltipContent>
        </Tooltip>
        <Tooltip>
          <TooltipTrigger asChild>
            <ToggleGroupItem value="downstream" aria-label="Downstream only" data-testid="graph-trace-downstream" className={cn("h-8 px-2", PRESSED)}>
              <ArrowRightToLine aria-hidden />
            </ToggleGroupItem>
          </TooltipTrigger>
          <TooltipContent>Downstream only: what a change to it reaches.</TooltipContent>
        </Tooltip>
      </ToggleGroup>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button variant="ghost" size="icon-sm" onClick={onClear} aria-label="Stop tracing" data-testid="graph-trace-clear">
            <X />
          </Button>
        </TooltipTrigger>
        <TooltipContent>Stop tracing and draw the whole scope again.</TooltipContent>
      </Tooltip>
    </>
  );
}
