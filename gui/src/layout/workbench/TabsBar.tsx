import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { ChevronDown, ChevronLeft, ChevronRight, Link2, X } from "lucide-react";
import { toast } from "sonner";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { flowKindIdentity, flowKindToneClasses } from "../../modules/flowKinds";
import { routeTitle } from "../nav";
import { useWorkbenchTabs, type WorkbenchTab } from "./TabsContext";
import { focusTarget, pageOffset, revealOffset, stripEdges, wheelScroll, type StripEdges } from "./tabStrip";

function copyLink(tab: WorkbenchTab): void {
  navigator.clipboard
    .writeText(new URL(tab.url, window.location.origin).toString())
    .then(() => toast.success("Link copied"))
    .catch(() => toast.error("Could not copy the link"));
}

/**
 * A tab's glyph: the icon of the flow kind its page reported, in the kind's colour, or else its route's icon, quiet.
 * The kind's colour stays at full strength, so a strip of pipelines of several kinds reads apart at a glance.
 */
function TabIcon({ tab }: { tab: WorkbenchTab }) {
  if (tab.flowKind !== undefined) {
    const identity = flowKindIdentity(tab.flowKind);
    const Icon = identity.icon;
    return <Icon aria-hidden data-kind={tab.flowKind} className={cn("size-3.5 shrink-0", flowKindToneClasses(identity.tone).icon)} />;
  }

  const { icon: Icon } = routeTitle(tab.path);
  return <Icon aria-hidden className="size-3.5 shrink-0 opacity-70" />;
}

/** The strip's tabs, in their order along it. */
function tabElements(strip: HTMLElement | null): HTMLElement[] {
  return strip === null ? [] : Array.from(strip.querySelectorAll<HTMLElement>('[role="tab"]'));
}

function prefersReducedMotion(): boolean {
  return window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

/** A row of the Open pages menu, by which the menu moves its focus when a row is closed from it. */
const OPEN_PAGE_ROW = "[data-open-page]";

/**
 * The row the focus moves to when a row of the Open pages menu is closed: the one under it, or the one above it when it
 * was the last, so a person closing pages from the keyboard keeps going down the list.
 */
function neighbourRow(row: Element | null): HTMLElement | null {
  const list = row?.parentElement;
  if (row === null || list === null || list === undefined) {
    return null;
  }

  const rows = Array.from(list.querySelectorAll<HTMLElement>(OPEN_PAGE_ROW));
  const index = rows.indexOf(row as HTMLElement);
  return index < 0 ? null : rows[index + 1] ?? rows[index - 1] ?? null;
}

/** One end's scroll button: shown while the strip hides tabs, enabled while tabs lie hidden past its end. */
function ScrollButton({ direction, enabled, onStep }: {
  direction: "before" | "after";
  enabled: boolean;
  onStep: (direction: "before" | "after") => void;
}) {
  const Icon = direction === "before" ? ChevronLeft : ChevronRight;
  const label = direction === "before" ? "Show the open pages to the left" : "Show the open pages to the right";
  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <button
          type="button"
          aria-label={label}
          // The keyboard walks the tabs with the arrow keys and the focus brings each into view, so these stay off the
          // Tab order: they are the pointer's way to the tabs past an end.
          tabIndex={-1}
          disabled={!enabled}
          onClick={() => onStep(direction)}
          data-testid={`tabs-scroll-${direction}`}
          className={cn(
            "flex w-6 shrink-0 items-center justify-center border-border/60 text-muted-foreground hover:bg-tab-active/50 hover:text-foreground disabled:pointer-events-none disabled:opacity-30",
            direction === "before" ? "border-r" : "border-l",
          )}
        >
          <Icon className="size-3.5" />
        </button>
      </TooltipTrigger>
      <TooltipContent>{label}</TooltipContent>
    </Tooltip>
  );
}

/**
 * The VS Code-style tab strip over the router (DESIGN.md section 6): one tab per visited route, the active tab wearing
 * the editor surface and a top accent line. Tabs give up width down to a readable minimum before the strip scrolls; once
 * it does, a button at each end steps it a view's width along, the mouse wheel scrolls it, and the routed page's tab is
 * always brought into view, so no tab is ever out of reach. Middle-click, the X or Delete closes a tab, the arrow keys,
 * Home and End walk the strip, right-click opens the tab menu (close, close others, close to the left/right, close all),
 * and the strip ends in the Open pages menu, which lists every open tab to go to or close.
 */
export function TabsBar() {
  const { tabs, activePath, activate, close, closeOthers, closeToLeft, closeToRight, closeAll } = useWorkbenchTabs();
  const stripRef = useRef<HTMLDivElement | null>(null);
  // The strip position a tab closed from the keyboard leaves the focus at, applied once the strip is drawn without it.
  const pendingFocus = useRef<number | null>(null);
  const [edges, setEdges] = useState<StripEdges>({ before: false, after: false });
  const activeTitle = tabs.find((tab) => tab.path === activePath)?.title ?? null;
  const overflowing = edges.before || edges.after;

  const measure = useCallback(() => {
    const strip = stripRef.current;
    if (strip === null) {
      return;
    }

    const next = stripEdges(strip);
    setEdges((current) => (current.before === next.before && current.after === next.after ? current : next));
  }, []);

  const revealActive = useCallback(() => {
    const strip = stripRef.current;
    const tab = strip?.querySelector<HTMLElement>('[role="tab"][aria-selected="true"]');
    if (strip === null || strip === undefined || tab === null || tab === undefined) {
      return;
    }

    const left = revealOffset(strip, { left: tab.offsetLeft, width: tab.offsetWidth });
    if (left !== strip.scrollLeft) {
      strip.scrollLeft = left;
    }
  }, []);

  // The routed page's tab is brought into view once it is drawn (a page opened from the side bar, the palette or a link
  // gets its tab only after the route changes) and again when its title, and so its width, changes.
  useLayoutEffect(() => {
    revealActive();
  }, [activePath, activeTitle, revealActive]);

  // Which ends hide tabs is read again whenever the strip changes size (the window, the side bar, its own scroll buttons
  // coming and going) or its tabs change (opened, closed, retitled); a change of size also keeps the active tab in view.
  useLayoutEffect(() => {
    const strip = stripRef.current;
    if (strip === null) {
      return undefined;
    }

    const resize = new ResizeObserver(() => {
      revealActive();
      measure();
    });
    const content = new MutationObserver(() => measure());
    resize.observe(strip);
    content.observe(strip, { childList: true, subtree: true, characterData: true });
    return () => {
      resize.disconnect();
      content.disconnect();
    };
  }, [measure, revealActive]);

  useLayoutEffect(() => {
    const position = pendingFocus.current;
    if (position === null) {
      return;
    }

    pendingFocus.current = null;
    const remaining = tabElements(stripRef.current);
    remaining[Math.min(position, remaining.length - 1)]?.focus();
  }, [tabs]);

  const step = useCallback((direction: "before" | "after") => {
    const strip = stripRef.current;
    if (strip === null) {
      return;
    }

    const spans = tabElements(strip).map((tab) => ({ left: tab.offsetLeft, width: tab.offsetWidth }));
    strip.scrollTo({ left: pageOffset(strip, spans, direction), behavior: prefersReducedMotion() ? "auto" : "smooth" });
  }, []);

  const closeFromMenu = (row: Element | null, path: string) => {
    const next = neighbourRow(row);
    close(path);
    next?.focus();
  };

  return (
    <div className="flex h-[35px] shrink-0 items-stretch border-b border-border bg-tab-bar">
      {overflowing && <ScrollButton direction="before" enabled={edges.before} onStep={step} />}
      <div
        ref={stripRef}
        role="tablist"
        aria-label="Open pages"
        onScroll={measure}
        onWheel={(event) => {
          const strip = stripRef.current;
          const delta = strip === null
            ? null
            : wheelScroll(event.deltaX, event.deltaY, event.deltaMode, event.shiftKey, strip.clientWidth);
          if (strip !== null && delta !== null) {
            strip.scrollLeft += delta;
          }
        }}
        className="relative flex min-w-0 flex-1 items-stretch overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
      >
        {tabs.map((tab, index) => {
          const isActive = tab.path === activePath;
          const isFirst = index === 0;
          const isLast = index === tabs.length - 1;
          return (
            <ContextMenu key={tab.path}>
              <ContextMenuTrigger asChild>
                <div
                  role="tab"
                  aria-selected={isActive}
                  // One stop in the Tab order, on the active tab; the arrow keys walk the rest.
                  tabIndex={isActive ? 0 : -1}
                  title={tab.title}
                  onClick={() => activate(tab)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter" || event.key === " ") {
                      event.preventDefault();
                      activate(tab);
                      return;
                    }

                    if (event.key === "Delete") {
                      event.preventDefault();
                      pendingFocus.current = index;
                      close(tab.path);
                      return;
                    }

                    const target = focusTarget(event.key, index, tabs.length);
                    if (target !== null) {
                      event.preventDefault();
                      tabElements(stripRef.current)[target]?.focus();
                    }
                  }}
                  onMouseDown={(event) => {
                    // A middle press over a scrolling strip starts the browser's autoscroll on Windows; it closes here.
                    if (event.button === 1) {
                      event.preventDefault();
                    }
                  }}
                  onAuxClick={(event) => {
                    if (event.button === 1) {
                      close(tab.path);
                    }
                  }}
                  data-testid="workbench-tab"
                  className={cn(
                    "group relative flex min-w-40 max-w-52 flex-[0_1_auto] cursor-pointer select-none items-center gap-1.5 border-r border-border/60 px-3 text-xs outline-none focus-visible:ring-1 focus-visible:ring-inset focus-visible:ring-ring",
                    isActive ? "bg-tab-active text-foreground" : "text-muted-foreground hover:bg-tab-active/50",
                  )}
                >
                  {isActive && <span className="absolute inset-x-0 top-0 h-px bg-primary" />}
                  <TabIcon tab={tab} />
                  <span className="min-w-0 flex-1 truncate">{tab.title}</span>
                  <button
                    type="button"
                    aria-label={`Close ${tab.title}`}
                    // Delete closes the focused tab, so the X is the pointer's and stays off the Tab order.
                    tabIndex={-1}
                    onClick={(event) => {
                      event.stopPropagation();
                      close(tab.path);
                    }}
                    className={cn(
                      "ml-0.5 shrink-0 rounded-sm p-0.5 hover:bg-muted",
                      isActive ? "opacity-70 hover:opacity-100" : "opacity-0 group-hover:opacity-70 group-focus-visible:opacity-70",
                    )}
                  >
                    <X className="size-3.5" />
                  </button>
                </div>
              </ContextMenuTrigger>
              <ContextMenuContent className="w-56">
                <ContextMenuItem onSelect={() => close(tab.path)}>
                  <X className="size-3.5" />
                  Close
                </ContextMenuItem>
                <ContextMenuItem disabled={tabs.length < 2} onSelect={() => closeOthers(tab.path)}>
                  Close Others
                </ContextMenuItem>
                <ContextMenuItem disabled={isFirst} onSelect={() => closeToLeft(tab.path)}>
                  Close to the Left
                </ContextMenuItem>
                <ContextMenuItem disabled={isLast} onSelect={() => closeToRight(tab.path)}>
                  Close to the Right
                </ContextMenuItem>
                <ContextMenuItem onSelect={closeAll}>Close All</ContextMenuItem>
                <ContextMenuSeparator />
                <ContextMenuItem onSelect={() => copyLink(tab)}>
                  <Link2 className="size-3.5" />
                  Copy Link
                </ContextMenuItem>
              </ContextMenuContent>
            </ContextMenu>
          );
        })}
      </div>
      {overflowing && <ScrollButton direction="after" enabled={edges.after} onStep={step} />}
      <DropdownMenu>
        <Tooltip>
          <TooltipTrigger asChild>
            <DropdownMenuTrigger
              aria-label="Open pages menu"
              data-testid="tabs-overflow-menu"
              className="flex shrink-0 items-center border-l border-border/60 px-2 text-muted-foreground hover:bg-tab-active/50 hover:text-foreground"
            >
              <ChevronDown className="size-3.5" />
            </DropdownMenuTrigger>
          </TooltipTrigger>
          <TooltipContent>Every open page, to go to or close</TooltipContent>
        </Tooltip>
        <DropdownMenuContent align="end" className="w-72">
          <DropdownMenuLabel className="text-xs text-muted-foreground">Open pages</DropdownMenuLabel>
          {tabs.map((tab) => (
            <DropdownMenuItem
              key={tab.path}
              data-open-page=""
              onSelect={() => activate(tab)}
              onKeyDown={(event) => {
                if (event.key === "Delete") {
                  event.preventDefault();
                  closeFromMenu(event.currentTarget, tab.path);
                }
              }}
              className={cn("pr-1 text-xs", tab.path === activePath && "bg-accent/60 text-accent-foreground")}
              data-testid="open-page"
            >
              <TabIcon tab={tab} />
              <span className="min-w-0 flex-1 truncate">{tab.title}</span>
              <button
                type="button"
                aria-label={`Close ${tab.title}`}
                // Delete closes the highlighted row from the keyboard; the X is the pointer's.
                tabIndex={-1}
                onClick={(event) => {
                  // Kept from the row, so closing a page leaves the menu open on the rest.
                  event.stopPropagation();
                  closeFromMenu(event.currentTarget.closest(OPEN_PAGE_ROW), tab.path);
                }}
                className="shrink-0 rounded-sm p-0.5 opacity-60 hover:bg-muted hover:opacity-100"
                data-testid="open-page-close"
              >
                <X className="size-3.5" />
              </button>
            </DropdownMenuItem>
          ))}
          <DropdownMenuSeparator />
          <DropdownMenuItem
            className="text-xs"
            disabled={tabs.length < 2}
            onSelect={() => closeOthers(activePath)}
          >
            Close Others
          </DropdownMenuItem>
          <DropdownMenuItem className="text-xs" onSelect={closeAll}>
            Close All
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>
  );
}
