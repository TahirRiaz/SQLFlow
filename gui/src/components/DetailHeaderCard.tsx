import type { ReactNode } from "react";
import { Card } from "@/components/ui/card";
import { cn } from "@/lib/utils";
import { ExplainPageButton } from "../features/chat/ExplainPageButton";
import { DetailPairLayoutContext } from "./detailPairLayout";

interface DetailHeaderCardProps {
  title: ReactNode;
  /** Inline badges next to the title: status, kind, sync state. */
  badges?: ReactNode;
  /** A tile before the title, as tall as the title and the line under it together: what kind of thing the page shows. */
  leading?: ReactNode;
  /** A quiet line under the title that names what the title is: "Ingestion flow". */
  subtitle?: ReactNode;
  /** Right-aligned actions: sync now, lineage graph, trigger run. */
  actions?: ReactNode;
  /** Identifiers (IdChip): the low-value GUIDs that belong on the page for copy/link, on the quiet line under the
   * title after the subtitle, so they take no row of their own and do not compete with the facts for attention. */
  meta?: ReactNode;
  /** Classes that colour the card's top edge (`border-t-chart-1`), so the kind of thing shown reads from across the
   * workbench; the edge is drawn only when they are given. */
  edgeClassName?: string;
  /** The DetailPair cells: the facts, each a caption and its value on one line. */
  children: ReactNode;
  "data-testid"?: string;
}

/**
 * The bordered header card shared by the detail pages (repo, pipeline, run): what the page shows in two lines (the title
 * with its badges, Explain and the actions, then a quiet line of what it is and its identifiers), and under a hairline the
 * facts, each DetailPair's caption beside its value, flowing along a line or two. A dozen facts stacked caption over value
 * on a grid took three or four rows at a laptop's width and pushed the page's own content half way down the screen; side
 * by side they keep the card a header.
 */
export function DetailHeaderCard({
  title, badges, leading, subtitle, actions, meta, edgeClassName, children, "data-testid": testId,
}: DetailHeaderCardProps) {
  return (
    <Card className={cn("gap-0 rounded-lg px-4 py-3", edgeClassName !== undefined && cn("border-t-[3px]", edgeClassName))} data-testid={testId}>
      <div className="flex flex-wrap items-center gap-x-3 gap-y-2">
        {leading !== undefined && <div className="flex shrink-0">{leading}</div>}
        <div className="flex min-w-0 flex-[1_1_20rem] flex-col gap-0.5">
          <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1">
            <h1 className="min-w-0 break-words text-lg font-semibold leading-7">{title}</h1>
            <ExplainPageButton />
            {badges}
          </div>
          {(subtitle !== undefined || meta !== undefined) && (
            <div className="flex min-w-0 flex-wrap items-center gap-x-2 gap-y-1 text-[13px] leading-5 text-muted-foreground">
              {subtitle}
              {meta}
            </div>
          )}
        </div>
        {actions !== undefined && <div className="ml-auto flex flex-wrap items-center gap-2">{actions}</div>}
      </div>
      <DetailPairLayoutContext.Provider value="inline">
        <div className="mt-2.5 flex flex-wrap items-baseline gap-x-6 gap-y-0.5 border-t border-border/60 pt-2.5">
          {children}
        </div>
      </DetailPairLayoutContext.Provider>
    </Card>
  );
}
