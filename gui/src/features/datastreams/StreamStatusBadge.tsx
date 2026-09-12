import { CircleAlert, CircleCheck, CircleHelp, CircleSlash, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { DataStream, InsightSeverity, StreamStatus } from "../../api/types";
import { statusLabels, statusRank } from "./streamPresentation";

/** The glyph of each verdict, shared by the badge and the group counts so a stopped stream looks the same
 * whether it is one row or one number. */
export const statusIcons: Record<StreamStatus, typeof CircleAlert> = {
  stalled: CircleSlash,
  degraded: TriangleAlert,
  watch: CircleAlert,
  healthy: CircleCheck,
  "insufficient-history": CircleHelp,
};

const severityInk: Record<InsightSeverity, string> = {
  critical: "text-destructive",
  warning: "text-warning",
  info: "text-info",
};

/** The ink of each verdict when it is counted rather than judged: a count of stopped streams is red however
 * sure the detector was about each one. Shared with the lineage graph, whose per-flow glyphs are counts of
 * one and must wear the same colour as the chip that totals them. */
export const statusInk: Record<StreamStatus, string> = {
  stalled: "text-destructive",
  degraded: "text-warning",
  watch: "text-info",
  healthy: "text-success",
  "insufficient-history": "text-muted-foreground",
};

/**
 * A stream's verdict: reserved status color plus icon plus label, never color alone (DESIGN.md 3.2). The color
 * comes from the SEVERITY and the word from the STATUS, which are deliberately different axes: a stopped
 * stream with only one detector behind it is still "Stopped", but it is not painted as a critical until a
 * second independent test agrees.
 */
export function StreamStatusBadge({ status, severity }: { status: StreamStatus; severity: InsightSeverity }) {
  const Icon = statusIcons[status] ?? CircleHelp;
  const ink = status === "healthy" ? "text-success" : severityInk[severity] ?? "text-muted-foreground";

  return (
    <span className={cn("inline-flex shrink-0 items-center gap-1 text-xs font-medium", ink)}>
      <Icon className="size-3.5" aria-hidden />
      {statusLabels[status] ?? status}
    </span>
  );
}

/**
 * How many of a group's streams are in each verdict, worst first, as glyph-plus-count chips: "⊘ 17 ⚠ 3 ✓ 1"
 * reads in the width the words "17 stopped, 3 missing data, 1 ok" would need three times over, and the
 * glyphs are the same ones the rows wear, so the eye maps one to the other. Only the verdicts present are
 * shown; each chip names itself on hover and to assistive tech (DESIGN.md 7.8, a bare count says what it
 * counts).
 */
export function StreamStatusCounts({ rows }: { rows: DataStream[] }) {
  const counts = new Map<StreamStatus, number>();
  for (const row of rows) {
    counts.set(row.status, (counts.get(row.status) ?? 0) + 1);
  }

  const ordered = [...counts.entries()].sort((a, b) => statusRank(a[0]) - statusRank(b[0]));

  return (
    <span className="inline-flex shrink-0 items-center gap-2.5">
      {ordered.map(([status, n]) => {
        const Icon = statusIcons[status] ?? CircleHelp;
        const words = `${n} ${statusLabels[status].toLowerCase()}`;
        return (
          <span
            key={status}
            className={cn("inline-flex items-center gap-1 font-mono text-xs tabular-nums", statusInk[status])}
            title={words}
            aria-label={words}
          >
            <Icon className="size-3.5" aria-hidden />
            {n}
          </span>
        );
      })}
    </span>
  );
}
