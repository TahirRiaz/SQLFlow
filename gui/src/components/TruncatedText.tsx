import { useEffect, useState } from "react";
import { cn } from "@/lib/utils";
import { CopyButton } from "./CopyButton";
import { RichTooltip } from "./RichTooltip";

interface TruncatedTextProps {
  /** The full value: clipped with an ellipsis and shown in full on hover. */
  text: string | null | undefined;
  /**
   * Hard cap on the rendered width in pixels; the cell never grows past this no matter how long the value is,
   * which is what keeps one long path or URL from stretching a whole table column. Short values render at their
   * natural width and are not truncated.
   */
  maxWidth?: number;
  /** Render in monospace, for paths, ids, keys, and other code-like values. */
  mono?: boolean;
  /** Shown when `text` is null/empty (matches the tables' "-" placeholder). */
  placeholder?: string;
  /** Append an icon copy button that yields the full value, for the long strings a reader needs verbatim
   * (paths, URLs) but only ever sees clipped on screen. */
  copy?: boolean;
  copyTestId?: string;
  /**
   * Caption for the hover panel, naming what the value is ("Notes", "Description"). Worth setting in a wide
   * table, where the column header is often out of view by the time the pointer reaches the row. Setting it
   * also forces the panel on, so a short value still identifies itself.
   */
  title?: string;
  /** Extra classes for the clipped span (weight, color); layout and truncation stay with this component. */
  className?: string;
}

/**
 * Tracks whether an element's content overflows the width it is allowed, re-measuring whenever the element or
 * the column it sits in is resized. A callback ref rather than a `useRef`, so the observer follows the node
 * across the remount that attaching a tooltip trigger causes.
 */
function useClipped(text: string): [(node: HTMLSpanElement | null) => void, boolean] {
  const [node, setNode] = useState<HTMLSpanElement | null>(null);
  const [clipped, setClipped] = useState(false);

  useEffect(() => {
    if (node === null) {
      return;
    }

    // A sub-pixel slack: a fractional layout width can leave scrollWidth a hair over clientWidth on text that
    // is not actually clipped, which would put a panel on every cell in the table.
    const measure = () => setClipped(node.scrollWidth > node.clientWidth + 1);
    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [node, text]);

  return [setNode, clipped];
}

/**
 * A single-line value that clips with an ellipsis at a fixed pixel cap and reveals the full text in a hover
 * panel. The full string stays in the DOM (only visually clipped), so it remains selectable and copyable. This
 * is the one canonical way to render potentially long free-text values (paths, URLs, object keys, SQL, errors)
 * in a table cell or detail row, so a long value can never blow out the surrounding layout.
 *
 * The panel appears only when it has something to add: a value actually clipped by the cap, a multi-line value
 * whose line breaks the single line hides, or a value given a `title` to identify it. A short value that is
 * already fully visible gets no hover at all, so dragging the pointer across a table does not trail a string of
 * panels repeating text that is right there on screen.
 */
export function TruncatedText({
  text, maxWidth = 360, mono = false, placeholder = "-", copy = false, copyTestId, title, className,
}: TruncatedTextProps) {
  const value = text ?? "";
  const [ref, clipped] = useClipped(value);

  const span = (
    <span
      ref={ref}
      className={cn(
        // align-bottom keeps the clipped inline-block on the baseline of adjacent text in the same cell
        "inline-block max-w-full overflow-hidden text-ellipsis whitespace-nowrap align-bottom",
        mono && "font-mono text-[12px]",
        className,
      )}
      style={{ maxWidth }}
    >
      {value}
    </span>
  );

  if (text === null || text === undefined || text === "") {
    return <>{placeholder}</>;
  }

  // Line breaks survive in the panel but collapse to spaces on the clipped line, so a multi-line value is
  // worth revealing even when it happens to fit.
  const reveal = clipped || value.includes("\n") || title !== undefined;
  const clippedValue = reveal
    ? <RichTooltip body={value} title={title} mono={mono}>{span}</RichTooltip>
    : span;

  if (!copy) {
    return clippedValue;
  }

  // The copy button never clips, so the clipped value flexes and the icon keeps its place at the end.
  return (
    <span className="inline-flex max-w-full items-center gap-1 align-bottom">
      {clippedValue}
      <CopyButton iconOnly label="Copy" text={value} testId={copyTestId ?? "copy-value"} />
    </span>
  );
}
