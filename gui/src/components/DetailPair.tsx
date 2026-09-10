import type { ReactNode } from "react";

/**
 * One caption-over-value cell in a detail header: the single implementation shared by every detail page
 * (repo, pipeline, run) and the lineage drawer. Laid out by DetailHeaderCard's grid, so values line up
 * in even columns; long values (URLs, hashes) wrap inside their cell rather than overflowing.
 */
export function DetailPair({ label, children }: { label: ReactNode; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-[11px] font-medium uppercase tracking-wide text-muted-foreground">{label}</div>
      <div className="mt-0.5 break-words text-[13px]">{children}</div>
    </div>
  );
}
