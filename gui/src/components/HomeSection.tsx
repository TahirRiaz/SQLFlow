import type { ReactNode } from "react";

/**
 * A section of the home page (DESIGN.md 12) under its heading, with what it is as of, or the way into its area, on the
 * right. SQLFlow's own sections and the ones a module adds (`HomeContribution.sections`) are all set in it, so the page
 * reads as one.
 */
export function HomeSection({ title, aside, testId, children }: {
  title: ReactNode;
  aside?: ReactNode;
  testId: string;
  children: ReactNode;
}) {
  return (
    <section className="flex flex-col gap-3" data-testid={testId}>
      <div className="flex min-h-7 flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <h2 className="flex items-center gap-2 text-base font-medium">{title}</h2>
        {aside}
      </div>
      {children}
    </section>
  );
}
