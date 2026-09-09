import type { ReactNode } from "react";

/**
 * The tint an active (non-default) filter control wears so an applied filter is never mistaken for an untouched one:
 * a brand-primary border and wash. `SelectTrigger`'s `active` prop applies the same look to dropdown filters; this
 * constant is for the text/search `Input` filters, merged in via `cn(base, value !== "" && activeFilterClass)`.
 */
export const activeFilterClass = "border-primary/70 bg-primary/10 dark:bg-primary/20";

/**
 * The wrapper every filter row renders through (DESIGN.md 7.1): one gap, one wrap behaviour, stacking
 * full-width on the narrowest screens and sitting in a row from the small breakpoint up. Controls inside
 * are 32px (`h-8`), so the filter bars line up the same on every list page.
 */
export function FilterBar({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col flex-wrap gap-2 sm:flex-row sm:items-center">
      {children}
    </div>
  );
}
