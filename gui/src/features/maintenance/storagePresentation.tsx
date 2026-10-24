import { format } from "date-fns";
import { BrushCleaning, Scale, ShieldCheck } from "lucide-react";
import type { StorageVerdict } from "../../api/types";
import { parseUtc } from "../../lib/time";
import { verdictLabels } from "./storageText";
import { cn } from "@/lib/utils";

const verdictGlyphs: Record<StorageVerdict, { icon: typeof ShieldCheck; tone: string }> = {
  safe: { icon: BrushCleaning, tone: "text-success" },
  optional: { icon: Scale, tone: "text-warning" },
  keep: { icon: ShieldCheck, tone: "text-primary" },
};

/** A verdict's glyph in its tone, alone: for a group header or a heading that already names the verdict. */
export function VerdictGlyph({ verdict, className }: { verdict: StorageVerdict; className?: string }) {
  const { icon: Icon, tone } = verdictGlyphs[verdict];
  return <Icon className={cn("size-3.5 shrink-0", tone, className)} aria-hidden />;
}

/** A verdict as a neutral chip led by its glyph in the verdict's tone: a category, not a status to alarm about. */
export function VerdictMark({ verdict, className }: { verdict: StorageVerdict; className?: string }) {
  return (
    <span
      className={cn(
        "inline-flex shrink-0 items-center gap-1 rounded-sm border border-border px-1.5 py-0.5 text-[11px] leading-4 text-foreground",
        className,
      )}
      data-testid={`verdict-${verdict}`}
    >
      <VerdictGlyph verdict={verdict} />
      {verdictLabels[verdict]}
    </span>
  );
}

/** A cut-off or a moment as a date with its year, since a cut-off a year back must not read as this year's. */
export function dateText(value: string): string {
  return format(parseUtc(value), "d MMM yyyy");
}

/** A recent moment as a short date and time, for a grid cell: "24 Oct 22:05". */
export function shortDateTimeText(value: string): string {
  return format(parseUtc(value), "d MMM HH:mm");
}

/** A moment as a date and time with its year. */
export function dateTimeText(value: string): string {
  return format(parseUtc(value), "d MMM yyyy, HH:mm");
}
