import { Link } from "react-router-dom";
import { CopyButton } from "./CopyButton";

interface IdChipProps {
  /** The quiet caption naming what the id is: "run", "repo", "pipeline", "group", "commit". */
  label: string;
  /** The full identifier; this is what gets copied, whatever the shortened display shows. */
  value: string;
  /** The short form actually rendered; defaults to the first eight characters, enough to recognise it. */
  display?: string;
  /** Where the id navigates when it points at another resource; omitted for the id of the current page. */
  to?: string;
  testId?: string;
  copyTestId?: string;
}

/**
 * A compact identifier: a caption, a shortened id, and an icon-copy that yields the full value, optionally
 * linking to the resource it names. The full GUID is deliberately not shown, since the raw string carries no
 * meaning on screen; recognising the short prefix, following the link, or copying the exact value are the
 * things an operator actually does with it.
 */
export function IdChip({ label, value, display, to, testId, copyTestId }: IdChipProps) {
  const short = display ?? value.slice(0, 8);

  return (
    <span className="inline-flex items-center gap-1.5 rounded-md border border-border/60 bg-muted/40 py-0.5 pr-0.5 pl-2 text-[11px]">
      <span className="font-medium uppercase tracking-wide text-muted-foreground">{label}</span>
      {/* The testid rides the interactive target: the anchor when this id links somewhere (so a click both
          navigates and is what tests drive), otherwise the value span. */}
      {to
        ? (
          <Link to={to} className="font-mono text-[11px] text-primary hover:underline" data-testid={testId}>
            {short}
          </Link>
        )
        : <span className="font-mono text-[11px] text-foreground" data-testid={testId}>{short}</span>}
      <CopyButton iconOnly label={`Copy ${label} id`} text={value} testId={copyTestId ?? `copy-${label}`} />
    </span>
  );
}
