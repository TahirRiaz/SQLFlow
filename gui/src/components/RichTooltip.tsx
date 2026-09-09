import type { ReactNode } from "react";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";

interface RichTooltipProps {
  /** The full value to reveal. Line breaks are preserved, so a multi-line note reads as it was written. */
  body: string;
  /**
   * Optional caption naming what the value is ("Notes", "Description", "Location"). Worth setting on a table
   * cell, where the column header is often scrolled out of view by the time the pointer reaches the row.
   */
  title?: string;
  /**
   * Code-like values (URLs, paths, keys, connection references) render mono and may break mid-token, since
   * there are no word boundaries to break on and an unbreakable 200-character URL would otherwise force the
   * panel wider than its cap. Prose breaks on words.
   */
  mono?: boolean;
  side?: "top" | "right" | "bottom" | "left";
  align?: "start" | "center" | "end";
  /** Milliseconds of hover before the panel appears. */
  delayDuration?: number;
  /** The element the panel hangs off; rendered as-is through `asChild`. */
  children: ReactNode;
}

/**
 * A hover panel revealing a value that its trigger can only show clipped: the full text of a truncated cell,
 * a multi-line note, the URL behind a link icon. The one way this GUI reveals long or formatted content on
 * hover (DESIGN.md 7.8), so the reference components (`TruncatedText`, `PathRef`, `ConnectionRef`, `LinkRef`)
 * all present the same surface instead of each hand-rolling a tooltip.
 *
 * Display only, and deliberately so: a Radix tooltip closes as soon as the pointer leaves its trigger, so a
 * button inside the panel could never be clicked. Anything actionable (copy, open) belongs in the cell beside
 * the trigger, which is where the reference components put it.
 */
export function RichTooltip({
  body, title, mono = false, side = "top", align = "start", delayDuration = 400, children,
}: RichTooltipProps) {
  return (
    // The panel holds nothing interactive, so it must never behave as if it did. `disableHoverableContent`
    // stops the pointer from holding it open once it has left the trigger, and `pointer-events-none` stops a
    // panel that overlaps neighbouring rows from swallowing their hovers and clicks. Without the pair, a panel
    // opened over a dense grid stays up and blocks the cells underneath it.
    <Tooltip delayDuration={delayDuration} disableHoverableContent>
      <TooltipTrigger asChild>{children}</TooltipTrigger>
      <TooltipContent variant="panel" side={side} align={align} className="pointer-events-none">
        {title !== undefined && (
          <span className="mb-1 block text-[11px] font-medium uppercase tracking-wider text-muted-foreground">
            {title}
          </span>
        )}
        <span className={cn("block whitespace-pre-wrap", mono ? "break-all font-mono" : "break-words")}>
          {body}
        </span>
      </TooltipContent>
    </Tooltip>
  );
}
