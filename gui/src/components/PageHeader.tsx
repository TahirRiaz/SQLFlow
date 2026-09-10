import type { ReactNode } from "react";

interface PageHeaderProps {
  title: ReactNode;
  /** An optional line under the title (context, "as of" stamps, one-line descriptions). */
  subtitle?: ReactNode;
  /** Right-aligned actions: the page's primary button, a view toggle, and so on. */
  actions?: ReactNode;
}

/**
 * The one page title block (DESIGN.md 7.1): an 18px semibold title, an optional muted subtitle, and a
 * right-aligned actions slot. Every list page renders this as its first section so titles and action
 * alignment match everywhere. Vertical spacing comes from the enclosing Page, not from this component.
 */
export function PageHeader({ title, subtitle, actions }: PageHeaderProps) {
  return (
    <div className="flex flex-wrap items-start justify-between gap-3">
      <div className="min-w-0">
        <h1 className="text-lg font-semibold leading-7">{title}</h1>
        {subtitle !== undefined && (
          <div className="mt-0.5 text-[13px] text-muted-foreground">{subtitle}</div>
        )}
      </div>
      {actions !== undefined && (
        <div className="flex flex-wrap items-center gap-2">{actions}</div>
      )}
    </div>
  );
}
