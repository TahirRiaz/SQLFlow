import type { ReactNode } from "react";
import { Info } from "lucide-react";
import { HoverCard, HoverCardContent, HoverCardTrigger } from "@/components/ui/hover-card";
import { CopyButton } from "@/components/CopyButton";

/**
 * An explanation behind what it explains: what a state means, why a number is what it is, what a control does. It opens
 * on hover over its trigger and stays while the pointer is in it, so its Copy button can be reached; the page itself keeps
 * to one line per fact instead of a paragraph above every table.
 */
export function ExplainTip({ title, text, children, testId, side = "top" }: {
  title: string;
  text: string;
  children: ReactNode;
  testId: string;
  side?: "top" | "right" | "bottom" | "left";
}) {
  return (
    <HoverCard openDelay={350} closeDelay={150}>
      <HoverCardTrigger asChild>{children}</HoverCardTrigger>
      <HoverCardContent side={side} align="start" className="w-80 p-3" data-testid={`${testId}-tip`}>
        <div className="flex items-start justify-between gap-2">
          <span className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{title}</span>
          <CopyButton iconOnly label={`Copy: ${title.toLowerCase()}`} text={text} testId={`${testId}-copy`} />
        </div>
        <p className="mt-1 whitespace-pre-wrap break-words text-[12.5px] leading-snug">{text}</p>
      </HoverCardContent>
    </HoverCard>
  );
}

/** A section's quiet info mark with its explanation behind it: what a heading alone cannot say about the list under it. */
export function ExplainMark({ title, text, testId }: { title: string; text: string; testId: string }) {
  return (
    <ExplainTip title={title} text={text} testId={testId} side="bottom">
      <button
        type="button"
        className="inline-flex size-5 items-center justify-center rounded text-muted-foreground hover:text-foreground focus-visible:outline-1 focus-visible:outline-ring"
        aria-label={`About: ${title.toLowerCase()}`}
        data-testid={testId}
      >
        <Info className="size-3.5" aria-hidden />
      </button>
    </ExplainTip>
  );
}
