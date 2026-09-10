import type { ReactNode } from "react";
import { Card } from "@/components/ui/card";

interface DetailHeaderCardProps {
  title: ReactNode;
  /** Inline badges next to the title: status, kind, sync state. */
  badges?: ReactNode;
  /** Right-aligned actions: sync now, lineage graph, trigger run. */
  actions?: ReactNode;
  /** A quiet strip under the title row for identifiers (IdChip): the low-value GUIDs that belong on the page
   * for copy/link but should not compete with the metrics grid for attention. */
  meta?: ReactNode;
  /** The DetailPair cells; laid out on an even, responsive grid. */
  children: ReactNode;
  "data-testid"?: string;
}

/**
 * The bordered header card shared by the detail pages (repo, pipeline, run): a title row with inline
 * badges on the left and actions on the right, above a responsive grid of DetailPair cells. The grid
 * (even minmax columns) is what makes the label/value pairs line up in tidy columns.
 */
export function DetailHeaderCard({ title, badges, actions, meta, children, "data-testid": testId }: DetailHeaderCardProps) {
  return (
    <Card className="gap-0 rounded-lg p-4" data-testid={testId}>
      <div className="flex flex-wrap items-center gap-2">
        <h1 className="min-w-0 break-words text-lg font-semibold leading-7">{title}</h1>
        {badges}
        <div className="grow" />
        {actions !== undefined && <div className="flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
      {meta !== undefined && <div className="mt-3 flex flex-wrap items-center gap-1.5">{meta}</div>}
      <div className="mt-4 grid gap-4 [grid-template-columns:repeat(auto-fill,minmax(180px,1fr))]">
        {children}
      </div>
    </Card>
  );
}
