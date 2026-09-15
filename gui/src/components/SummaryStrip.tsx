import type { ReactNode } from "react";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";

export interface SummaryCell {
  label: string;
  value: ReactNode;
  /** A quiet line under the value: when it was captured, what the number means right now. */
  caption?: ReactNode;
  /** Tints the value; reserved for a number that asks for attention (something waiting, something wrong). */
  tone?: "success" | "warning" | "destructive" | "info";
  /** Where the cell leads when clicked (the tab or list behind the number); a cell without one is display only. */
  onClick?: () => void;
  /** Base testid; the value gets `<testId>-value`, matching KpiCard. */
  testId?: string;
}

const toneClasses: Record<NonNullable<SummaryCell["tone"]>, string> = {
  success: "text-success",
  warning: "text-warning",
  destructive: "text-destructive",
  info: "text-info",
};

/**
 * A row of headline facts above a working surface: a caption over a compact value, hairlines between the cells.
 * Where `KpiCard` (DESIGN.md 7.7) gives a dashboard number a tile of its own, this strip keeps the facts a page is
 * read against (which version, how many, what is waiting) on one line that costs the height of a toolbar, so the
 * list under it starts above the fold. The cells wrap on a narrow viewport and the hairlines follow them.
 */
export function SummaryStrip({ cells, "data-testid": testId }: { cells: SummaryCell[]; "data-testid"?: string }) {
  return (
    <Card
      className="grid gap-px overflow-hidden rounded-lg border bg-border p-0 [grid-template-columns:repeat(auto-fit,minmax(200px,1fr))]"
      data-testid={testId}
    >
      {cells.map((cell) => {
        const body = (
          <>
            <div className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{cell.label}</div>
            <div
              className={cn(
                "mt-0.5 flex min-w-0 items-center gap-2 font-mono text-[15px] font-semibold leading-6 tabular-nums",
                cell.tone && toneClasses[cell.tone],
              )}
              data-testid={cell.testId ? `${cell.testId}-value` : undefined}
            >
              {cell.value}
            </div>
            {cell.caption !== undefined && (
              <div className="mt-0.5 truncate text-[11px] text-muted-foreground">{cell.caption}</div>
            )}
          </>
        );

        if (cell.onClick !== undefined) {
          return (
            <button
              key={cell.label}
              type="button"
              onClick={cell.onClick}
              className="min-w-0 bg-card px-4 py-2.5 text-left outline-none transition-colors hover:bg-accent/50 focus-visible:bg-accent/50"
              data-testid={cell.testId}
            >
              {body}
            </button>
          );
        }

        return (
          <div key={cell.label} className="min-w-0 bg-card px-4 py-2.5" data-testid={cell.testId}>
            {body}
          </div>
        );
      })}
    </Card>
  );
}
