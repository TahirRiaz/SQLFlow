import { useNavigate } from "react-router-dom";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";

interface KpiCardProps {
  label: string;
  value: string | number;
  caption?: string;
  linkTo?: string;
  color?: "primary" | "success" | "error" | "warning" | "info";
  testId?: string;
}

const valueColors: Record<NonNullable<KpiCardProps["color"]>, string> = {
  primary: "text-primary",
  success: "text-success",
  error: "text-destructive",
  warning: "text-warning",
  info: "text-info",
};

/**
 * A dashboard headline number (DESIGN.md 7.7): 11px uppercase muted label over a 24px semibold value,
 * optionally linking to the page behind it. Full height so every card in a dashboard grid row is the
 * same size; the grid controls the width.
 */
export function KpiCard({ label, value, caption, linkTo, color, testId }: KpiCardProps) {
  const navigate = useNavigate();

  const content = (
    <>
      <div className="text-[11px] font-medium uppercase tracking-wider text-muted-foreground">{label}</div>
      <div
        className={cn("mt-1 font-mono text-2xl font-semibold leading-8 tabular-nums", color && valueColors[color])}
        data-testid={testId ? `${testId}-value` : undefined}
      >
        {value}
      </div>
      {caption && <div className="mt-0.5 text-xs text-muted-foreground">{caption}</div>}
    </>
  );

  if (linkTo !== undefined) {
    return (
      <Card className="h-full gap-0 rounded-lg p-0" data-testid={testId}>
        <button
          onClick={() => navigate(linkTo)}
          className="h-full w-full rounded-lg p-4 text-left transition-colors hover:bg-accent/50 focus-visible:bg-accent/50"
        >
          {content}
        </button>
      </Card>
    );
  }

  return (
    <Card className="h-full gap-0 rounded-lg p-4" data-testid={testId}>
      {content}
    </Card>
  );
}
