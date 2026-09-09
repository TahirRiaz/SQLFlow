import type { ReactNode } from "react";

interface PageProps {
  children: ReactNode;
  "data-testid"?: string;
}

/**
 * The frame every routed page renders through: one vertical rhythm (a single flex gap) so the gaps
 * between the header, an optional filter bar, and the content are identical across the whole application.
 * Pages pass their sections as children, the PageHeader first.
 */
export function Page({ children, "data-testid": testId }: PageProps) {
  return (
    <div className="flex flex-col gap-5" data-testid={testId}>
      {children}
    </div>
  );
}
