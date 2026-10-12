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
    <section className="flex h-full flex-col bg-panel" aria-label={content.title} data-testid="bottom-panel">
      {/* The panel's titlebar: a card surface under a 2px edge in the interaction blue, as the active tab wears one, so it
          reads as a surface of its own and never as one more grid header of the page above it. */}
      <header
        className="relative flex h-9 shrink-0 items-center justify-between border-b border-border bg-card px-3 before:absolute before:inset-x-0 before:top-0 before:h-0.5 before:bg-primary"
        data-testid="bottom-panel-header"
      >
        <span className="truncate text-[11px] font-semibold uppercase tracking-wider text-foreground">{content.title}</span>
        <button
          onClick={close}
          aria-label="Close panel"
          className="rounded-sm p-1 text-muted-foreground hover:bg-accent hover:text-foreground"
        >
          <X className="size-4" />
        </button>
      </header>
      {/* Keyed by what it shows, so other content opens at its top rather than where the last one was scrolled to. */}
      <div key={content.id} className="min-h-0 flex-1 overflow-auto">{content.node}</div>
    </section>
  );
}
