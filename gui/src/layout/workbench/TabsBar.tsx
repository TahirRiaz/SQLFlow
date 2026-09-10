import { useEffect, useRef } from "react";
import { ChevronDown, Link2, X } from "lucide-react";
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
import { cn } from "@/lib/utils";
import { routeTitle } from "../nav";
import { useWorkbenchTabs, type WorkbenchTab } from "./TabsContext";

function copyLink(tab: WorkbenchTab): void {
  navigator.clipboard
    .writeText(new URL(tab.url, window.location.origin).toString())
    .then(() => toast.success("Link copied"))
    .catch(() => toast.error("Could not copy the link"));
}

/**
 * The VS Code-style tab strip over the router (DESIGN.md section 6): one tab per visited route, the
 * active tab wearing the editor surface and a top accent line. Middle-click or the X closes a tab,
 * right-click opens the tab menu (close, close others, close to the left/right, close all), and the strip
 * ends in an overflow menu that lists every open tab so a long strip stays navigable.
 */
export function TabsBar() {
  const { tabs, activePath, activate, close, closeOthers, closeToLeft, closeToRight, closeAll } = useWorkbenchTabs();
  const activeTabRef = useRef<HTMLDivElement | null>(null);

  // Tabs keep their natural width and the strip scrolls, so the routed page's tab is pulled into view
  // whenever navigation happens from somewhere other than the strip (side bar, palette, a link).
  useEffect(() => {
    activeTabRef.current?.scrollIntoView({ block: "nearest", inline: "nearest" });
  }, [activePath]);

  return (
    <div className="flex h-[35px] shrink-0 items-stretch border-b border-border bg-tab-bar">
      <div
        role="tablist"
        aria-label="Open pages"
        className="flex min-w-0 flex-1 items-stretch overflow-x-auto [scrollbar-width:none] [&::-webkit-scrollbar]:hidden"
      >
        {tabs.map((tab, index) => {
          const { icon: Icon } = routeTitle(tab.path);
          const isActive = tab.path === activePath;
          const isFirst = index === 0;
          const isLast = index === tabs.length - 1;
          return (
            <ContextMenu key={tab.path}>
              <ContextMenuTrigger asChild>
                <div
                  ref={isActive ? activeTabRef : undefined}
                  role="tab"
                  aria-selected={isActive}
                  tabIndex={0}
                  title={tab.title}
                  onClick={() => activate(tab)}
                  onKeyDown={(event) => {
                    if (event.key === "Enter" || event.key === " ") {
                      event.preventDefault();
                      activate(tab);
                    }
                  }}
                  onAuxClick={(event) => {
                    if (event.button === 1) {
                      close(tab.path);
                    }
                  }}
                  className={cn(
                    "group relative flex max-w-52 shrink-0 cursor-pointer select-none items-center gap-1.5 border-r border-border/60 px-3 text-xs",
                    isActive ? "bg-tab-active text-foreground" : "text-muted-foreground hover:bg-tab-active/50",
                  )}
                >
                  {isActive && <span className="absolute inset-x-0 top-0 h-px bg-primary" />}
                  <Icon className="size-3.5 shrink-0 opacity-70" />
                  <span className="truncate">{tab.title}</span>
                  <button
                    aria-label={`Close ${tab.title}`}
                    onClick={(event) => {
                      event.stopPropagation();
                      close(tab.path);
                    }}
                    className={cn(
                      "ml-0.5 shrink-0 rounded-sm p-0.5 hover:bg-muted",
                      isActive ? "opacity-70 hover:opacity-100" : "opacity-0 group-hover:opacity-70",
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
      <DropdownMenu>
        <DropdownMenuTrigger
          aria-label="Open pages menu"
          data-testid="tabs-overflow-menu"
          className="flex shrink-0 items-center border-l border-border/60 px-2 text-muted-foreground hover:bg-tab-active/50 hover:text-foreground"
        >
          <ChevronDown className="size-3.5" />
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="max-w-72">
          <DropdownMenuLabel className="text-xs text-muted-foreground">Open pages</DropdownMenuLabel>
          {tabs.map((tab) => {
            const { icon: Icon } = routeTitle(tab.path);
            return (
              <DropdownMenuItem
                key={tab.path}
                onSelect={() => activate(tab)}
                className={cn("text-xs", tab.path === activePath && "bg-accent/60 text-accent-foreground")}
              >
                <Icon className="size-3.5 shrink-0 opacity-70" />
                <span className="truncate">{tab.title}</span>
              </DropdownMenuItem>
            );
          })}
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
