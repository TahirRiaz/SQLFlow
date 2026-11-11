import type { ReactNode } from "react";
import { RotateCcw, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";

/** One thing that shapes the drawing. With `onRemove` it is a reduction the reader made, and removing it undoes just that. */
export interface StatusPart {
  key: string;
  label: string;
  /** What removing it does, for the remove button's tooltip. */
  removeHint?: string;
  onRemove?: () => void;
}

/**
 * What the canvas is showing, in words: how much of the scope is drawn, and every choice that made it so. Each
 * control in the bar changes this line, which is how a reader learns what a control does; each reduction can be
 * taken back from here on its own, and Reset takes them all back.
 */
export function GraphStatusLine({ drawn, total, parts, attention, canReset, onReset }: {
  drawn: number;
  total: number;
  parts: readonly StatusPart[];
  /** Things the reader should know about the drawing's truth: a truncated walk, lineage not derived, stalled streams. */
  attention?: ReactNode;
  /** Whether anything about the drawing differs from how its scope opens. */
  canReset: boolean;
  onReset: () => void;
}) {
  return (
    <div
      data-testid="graph-status-line"
      className="flex min-h-8 shrink-0 flex-wrap items-center gap-x-2 gap-y-1 border-b border-border bg-background px-3 py-1 text-[12px]"
    >
      <span className="whitespace-nowrap" data-testid="graph-status-count">
        <span className="font-medium tabular-nums">{drawn}</span>
        <span className="text-muted-foreground"> drawn of </span>
        <span className="font-medium tabular-nums">{total}</span>
        <span className="text-muted-foreground"> in scope</span>
      </span>
      {parts.map((part) => (
        <span
          key={part.key}
          data-testid={`graph-status-${part.key}`}
          className={part.onRemove
            ? "inline-flex h-5 max-w-[320px] items-center gap-0.5 rounded-sm border border-border bg-card pr-0.5 pl-1.5"
            : "inline-flex h-5 max-w-[320px] items-center text-muted-foreground before:mr-2 before:content-['·']"}
        >
          <span className="truncate" title={part.label}>{part.label}</span>
          {part.onRemove && (
            <Tooltip>
              <TooltipTrigger asChild>
                <button
                  type="button"
                  onClick={part.onRemove}
                  aria-label={part.removeHint ?? `Remove: ${part.label}`}
                  data-testid={`graph-status-remove-${part.key}`}
                  className="inline-flex size-4 shrink-0 cursor-pointer items-center justify-center rounded-sm text-muted-foreground hover:bg-accent hover:text-foreground"
                >
                  <X aria-hidden className="size-3" />
                </button>
              </TooltipTrigger>
              <TooltipContent>{part.removeHint ?? `Remove: ${part.label}`}</TooltipContent>
            </Tooltip>
          )}
        </span>
      ))}
      {canReset && (
        <Button variant="ghost" size="xs" onClick={onReset} className="h-5 gap-1 px-1.5 text-muted-foreground" data-testid="graph-reset">
          <RotateCcw className="size-3" />
          Reset
        </Button>
      )}
      {attention && <span className="ml-auto flex flex-wrap items-center gap-x-3 gap-y-1">{attention}</span>}
    </div>
  );
}
