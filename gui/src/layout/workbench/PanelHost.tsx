import { X } from "lucide-react";
import { usePanel } from "./PanelContext";

/**
 * The bottom panel surface (DESIGN.md section 6): an uppercase title header with a close button, the
 * feature-provided content scrolling beneath. Rendered only while a feature has content open.
 */
export function PanelHost() {
  const { content, close } = usePanel();
  if (content === null) {
    return null;
  }

  return (
    <section className="flex h-full flex-col bg-panel" aria-label={content.title}>
      {/* The window titlebar: a distinct accent surface with a primary accent strip, so the panel reads as its own
          surface rather than blending into the page/editor behind it. */}
      <header className="flex h-8 shrink-0 items-center justify-between border-b border-border bg-accent px-3 shadow-sm">
        <span className="flex items-center gap-2 text-[11px] font-semibold uppercase tracking-wider text-accent-foreground">
          <span aria-hidden className="h-3 w-0.5 rounded-full bg-primary" />
          {content.title}
        </span>
        <button
          onClick={close}
          aria-label="Close panel"
          className="rounded-sm p-1 text-muted-foreground hover:bg-foreground/10 hover:text-foreground"
        >
          <X className="size-3.5" />
        </button>
      </header>
      <div className="min-h-0 flex-1 overflow-auto">{content.node}</div>
    </section>
  );
}
