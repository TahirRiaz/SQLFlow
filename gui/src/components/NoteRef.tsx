import { StickyNote } from "lucide-react";
import { RichTooltip } from "./RichTooltip";

interface NoteRefProps {
  /** The remark, or null when nothing was recorded. Line breaks are preserved in the panel. */
  note: string | null | undefined;
  /** Caption for the hover panel. */
  title?: string;
  /** Shown when there is no note (matches the tables' "-" placeholder). */
  placeholder?: string;
  testId?: string;
}

/**
 * A free-text remark reduced to a glyph, with the whole note in a hover panel. A note runs to several
 * sentences and often several lines, so the first thirty characters a table cell can show are almost always
 * the same boilerplate opener ("Incomplete dataset. This report ...") repeated down the column: the clipped
 * text costs a column's width and tells the reader nothing. The glyph says the one thing scanning needs,
 * that there IS a remark on this row, and hovering says what it is.
 */
export function NoteRef({ note, title = "Notes", placeholder = "-", testId }: NoteRefProps) {
  if (note === null || note === undefined || note === "") {
    return <span className="text-muted-foreground">{placeholder}</span>;
  }

  return (
    <RichTooltip body={note} title={title}>
      <span
        className="inline-flex size-6 items-center justify-center align-bottom text-muted-foreground"
        data-testid={testId ?? "note"}
      >
        <StickyNote className="size-4" aria-label={title} />
      </span>
    </RichTooltip>
  );
}
