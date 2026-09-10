import type { ReactNode } from "react";

interface EmptyStateProps {
  /** An optional muted icon above the text (a lucide icon; it is sized here). */
  icon?: ReactNode;
  title: ReactNode;
  /** An optional secondary line explaining what fills this space. */
  description?: ReactNode;
  /** An optional action (a button) under the text. */
  action?: ReactNode;
  "data-testid"?: string;
}

/**
 * The one empty / no-data panel (DESIGN.md 8.3): a centered, muted message with an optional icon,
 * secondary description, and action. Used by the table shell's empty branch and by every page's own
 * no-data state so they all read the same.
 */
export function EmptyState({ icon, title, description, action, "data-testid": testId }: EmptyStateProps) {
  return (
    <div className="flex flex-col items-center gap-1 px-6 py-12 text-center" data-testid={testId}>
      {icon !== undefined && (
        <div className="mb-1 text-muted-foreground/50 [&_svg]:size-10 [&_svg]:stroke-[1.25]">{icon}</div>
      )}
      <div className="text-sm font-medium text-muted-foreground">{title}</div>
      {description !== undefined && (
        <div className="max-w-md text-[13px] text-muted-foreground/80">{description}</div>
      )}
      {action !== undefined && <div className="mt-2">{action}</div>}
    </div>
  );
}
