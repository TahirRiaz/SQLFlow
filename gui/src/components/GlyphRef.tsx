import type { LucideIcon } from "lucide-react";
import { cn } from "@/lib/utils";
import { RichTooltip } from "./RichTooltip";

interface GlyphRefProps {
  icon: LucideIcon;
  /** What the value is: the hover panel's caption, and the glyph's name for assistive tech when no label is shown. */
  title: string;
  /** The full value the panel reveals. Line breaks are kept, so a list reads one item per line. */
  body: string | null | undefined;
  /** A short word or count beside the glyph ("File", "3"); omitted for the glyph alone. */
  label?: string;
  /** Lets the label give way in a narrow table (the DataTable's `table` container), leaving the glyph. */
  collapse?: boolean;
  /** Code-like values (kinds, paths) render mono in the panel. */
  mono?: boolean;
  /** Shown when there is no value (matches the tables' "-" placeholder). */
  placeholder?: string;
  testId?: string;
}

/**
 * A secondary value reduced to a glyph, with at most a word or a count beside it, and the whole value in a hover
 * panel: where a document came from, the types a value points to, the path a file was read from. A table needs to know
 * such a value exists and roughly what sort it is; spelling it out would cost a column's width on every row, so the
 * glyph carries the sort and the panel carries the text.
 */
export function GlyphRef({
  icon: Icon, title, body, label, collapse = false, mono = false, placeholder = "-", testId,
}: GlyphRefProps) {
  if (body === null || body === undefined || body === "") {
    return <span className="text-muted-foreground">{placeholder}</span>;
  }

  return (
    <RichTooltip body={body} title={title} mono={mono}>
      <span className="inline-flex items-center gap-1 align-bottom text-muted-foreground" data-testid={testId}>
        {label === undefined
          ? <Icon className="size-3.5 shrink-0" aria-label={title} />
          : <Icon className="size-3.5 shrink-0" aria-hidden />}
        {label !== undefined && <span className={cn(collapse && "@max-3xl/table:sr-only")}>{label}</span>}
      </span>
    </RichTooltip>
  );
}
