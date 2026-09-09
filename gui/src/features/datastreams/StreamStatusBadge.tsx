import { CircleAlert, CircleCheck, CircleHelp, CircleSlash, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { InsightSeverity, StreamStatus } from "../../api/types";
import { statusLabels } from "./streamPresentation";

const icons: Record<StreamStatus, typeof CircleAlert> = {
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

/**
 * A stream's verdict: reserved status color plus icon plus label, never color alone (DESIGN.md 3.2). The color
 * comes from the SEVERITY and the word from the STATUS, which are deliberately different axes: a stopped
 * stream with only one detector behind it is still "Stopped", but it is not painted as a critical until a
 * second independent test agrees.
 */
export function StreamStatusBadge({ status, severity }: { status: StreamStatus; severity: InsightSeverity }) {
  const Icon = icons[status] ?? CircleHelp;
  const ink = status === "healthy" ? "text-success" : severityInk[severity] ?? "text-muted-foreground";

  return (
    <span className={cn("inline-flex shrink-0 items-center gap-1 text-xs font-medium", ink)}>
      <Icon className="size-3.5" aria-hidden />
      {statusLabels[status] ?? status}
    </span>
  );
}
