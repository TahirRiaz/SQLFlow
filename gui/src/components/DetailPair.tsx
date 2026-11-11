import { useContext, type ReactNode } from "react";
import { DetailPairLayoutContext } from "./detailPairLayout";

/**
 * One caption-and-value cell: the single implementation shared by every detail page (repo, pipeline, run), the panels
 * and the lineage drawer. Stacked, the caption sits over the value and a grid lines the cells up in columns; inline (a
 * detail page's header card), the caption sits before the value on one line, so the facts flow along a line or two.
 * Long values (URLs, hashes) wrap inside their cell rather than overflowing.
 */
export function DetailPair({ label, children }: { label: ReactNode; children: ReactNode }) {
  const layout = useContext(DetailPairLayoutContext);
  if (layout === "inline") {
    return (
      <div className="flex min-w-0 max-w-full items-baseline gap-1.5">
        <span className="shrink-0 text-[10.5px] font-medium uppercase tracking-wide text-muted-foreground">{label}</span>
        <span className="min-w-0 break-words text-[13px] leading-6">{children}</span>
      </div>
    );
  }

  return (
    <div className="min-w-0">
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-0.5 break-words text-[13px]">{children}</div>
    </div>
  );
}
