import { CircleAlert, Info, TriangleAlert } from "lucide-react";
import { cn } from "@/lib/utils";
import type { InsightSeverity } from "../../api/types";

const severities: Record<InsightSeverity, { label: string; className: string; Icon: typeof Info }> = {
  critical: { label: "Critical", className: "text-destructive", Icon: CircleAlert },
  warning: { label: "Warning", className: "text-warning", Icon: TriangleAlert },
  info: { label: "Info", className: "text-info", Icon: Info },
};

/** An advisory's severity: reserved status color plus icon plus label, never color alone (DESIGN.md 3.2). */
export function SeverityBadge({ severity }: { severity: InsightSeverity }) {
  const { label, className, Icon } = severities[severity] ?? severities.info;
  return (
    <span className={cn("inline-flex shrink-0 items-center gap-1 text-xs font-medium", className)}>
      <Icon className="size-3.5" aria-hidden />
      {label}
    </span>
  );
}
