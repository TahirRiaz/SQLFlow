import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { flowKindIdentity, flowKindToneClasses } from "../modules/flowKinds";

/**
 * A flow's kind as a chip: its glyph in the kind's colour and its name, with the kind's value on hover (DESIGN.md 7.3.1).
 * Square-cornered on a tint of the kind's colour, so beside a status it never reads as one.
 */
export function FlowKindBadge({ kind, testId = "flow-kind" }: { kind: string; testId?: string }) {
  const identity = flowKindIdentity(kind);
  const tone = flowKindToneClasses(identity.tone);
  const Icon = identity.icon;
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span
          data-testid={testId}
          data-kind={kind}
          className={cn(
            "inline-flex h-5 w-fit shrink-0 items-center gap-1 whitespace-nowrap rounded-sm border px-1.5 text-[11px] font-medium text-foreground",
            tone.surface,
          )}
        >
          <Icon aria-hidden className={cn("size-3.5 shrink-0", tone.icon)} />
          {identity.label}
        </span>
      </TooltipTrigger>
      <TooltipContent>
        Flow kind <span className="font-mono">{kind}</span>
      </TooltipContent>
    </Tooltip>
  );
}

/**
 * A flow's kind as the tile that leads its page's header: the kind's glyph on a tint of its colour, larger than a chip's,
 * so the kind is the first thing the header says (DESIGN.md 7.3.1). Its name is written beside it by the header.
 */
export function FlowKindTile({ kind, testId = "flow-kind-tile" }: { kind: string; testId?: string }) {
  const identity = flowKindIdentity(kind);
  const tone = flowKindToneClasses(identity.tone);
  const Icon = identity.icon;
  return (
    <span
      data-testid={testId}
      data-kind={kind}
      className={cn("inline-flex size-10 shrink-0 items-center justify-center rounded-md border", tone.surface)}
    >
      <Icon aria-hidden className={cn("size-5", tone.icon)} />
    </span>
  );
}
